import AVFoundation
import CoreImage
import OSLog
import SwiftUI
import DeskPairShared
import UIKit
import VideoToolbox

/// Its own category, so "why is the picture black" is one predicate rather than a scroll through a session.
let videoLog = Logger(subsystem: "com.sunllo.deskpair", category: "video")

/// Shows H.264 the host sends, decoded by VideoToolbox and composited by the window server; and VP9, from a
/// host that cannot encode H.264, decoded here first (see `enqueueVp9`).
///
/// `AVSampleBufferDisplayLayer` rather than a `VTDecompressionSession` writing into a Metal texture: it is
/// the shorter path by a long way, it handles its own timing, and the frame never becomes a pixel buffer
/// this process has to own. What it costs is a format conversion, because the layer wants AVCC — length
/// prefixed NAL units with the parameter sets held separately — while the wire carries Annex-B, where the
/// units are separated by start codes and the parameter sets are simply among them.
final class VideoDisplayView: UIView {

    override class var layerClass: AnyClass { AVSampleBufferDisplayLayer.self }

    private var displayLayer: AVSampleBufferDisplayLayer { layer as! AVSampleBufferDisplayLayer }

    /// Built from the SPS and PPS, and rebuilt when either changes — which happens when the host's
    /// resolution changes mid-session, something the desktop client learned to notice the hard way.
    private var formatDescription: CMVideoFormatDescription?
    private var storedSps: [UInt8]?
    private var storedPps: [UInt8]?

    private(set) var framesEnqueued = 0
    private(set) var lastError: String? {
        didSet { onProblem?(lastError) }
    }

    /// Told when the picture stops being possible, so a screen can say so.
    ///
    /// `lastError` was written in four places and read in none, so a stream this device could not decode
    /// showed as a black rectangle under a healthy signal chip with no explanation anywhere.
    var onProblem: ((String?) -> Void)?

    override init(frame: CGRect) {
        super.init(frame: frame)
        // resize rather than resizeAspect: the canvas decides the frame this view occupies, so the
        // letterbox geometry lives in one place instead of being split between a model and a video
        // gravity that would quietly fight it.
        displayLayer.videoGravity = .resize
        backgroundColor = .black
    }

    required init?(coder: NSCoder) {
        super.init(coder: coder)
        displayLayer.videoGravity = .resize
        backgroundColor = .black
    }

    /// VP9 is decoded here rather than by the layer: see `enqueueVp9`.
    private var vp9Configuration: Vp9Configuration?
    private var vp9Format: CMVideoFormatDescription?
    private var vp9Session: VTDecompressionSession?

    deinit {
        if let session = vp9Session {
            VTDecompressionSessionInvalidate(session)
        }
    }

    /// Takes a frame in whichever format the host chose, which can change between keyframes when the audience
    /// does. Returns false when the frame could not be used, which makes the session ask for a keyframe.
    @discardableResult
    func enqueue(frame: Data, isKeyFrame: Bool, format: VideoFormat, vp9: Vp9Configuration?) -> Bool {
        if format == VideoFormat.h264 {
            dropVp9()
            return enqueue(annexB: frame, isKeyFrame: isKeyFrame)
        }

        if format == VideoFormat.vp9 {
            return enqueueVp9(frame, isKeyFrame: isKeyFrame, configuration: vp9)
        }

        // Never listed in `formats`, so a host newer than this build.
        lastError = String(localized: "video.unsupportedFormat")
        return false
    }

    /// How many frames have been looked at, so the first few can say what they contained.
    ///
    /// Two of the refusals below used to be silent: a stream this device could not parse produced a black
    /// rectangle, a keyframe request every second, and not one word anywhere -- on the screen, in the log,
    /// or in the host's. Describing the first few frames costs nothing and is the difference between
    /// "the picture does not come" and knowing why.
    private var framesSeen = 0

    /// Returns false when the frame could not be used, which makes the session ask for a keyframe.
    @discardableResult
    func enqueue(annexB: Data, isKeyFrame: Bool) -> Bool {
        let units = Self.splitAnnexB(annexB)
        framesSeen += 1
        let describe = framesSeen <= 5

        guard !units.isEmpty else {
            if describe {
                let head = annexB.prefix(8).map { String(format: "%02x", $0) }.joined(separator: " ")
                videoLog.error(
                    "frame \(self.framesSeen) (\(annexB.count) bytes, key=\(isKeyFrame)) has no start codes; begins \(head, privacy: .public)"
                )
            }

            lastError = String(localized: "video.unsupportedFormat")
            return false
        }

        var payload = Data()

        for unit in units {
            guard let first = unit.first else { continue }
            // The low five bits are the NAL type: 7 is a sequence parameter set, 8 a picture parameter set.
            switch first & 0x1F {
            case 7: storedSps = [UInt8](unit)
            case 8: storedPps = [UInt8](unit)
            default:
                // AVCC: each unit prefixed by its length as a four-byte big-endian integer.
                var length = UInt32(unit.count).bigEndian
                payload.append(Data(bytes: &length, count: 4))
                payload.append(unit)
            }
        }

        if describe {
            let types = units.compactMap { $0.first.map { Int($0 & 0x1F) } }
            videoLog.notice(
                "frame \(self.framesSeen): \(annexB.count) bytes, key=\(isKeyFrame), nal types \(types, privacy: .public), sps=\(self.storedSps != nil), pps=\(self.storedPps != nil)"
            )
        }

        if formatDescription == nil || isKeyFrame {
            rebuildFormatDescriptionIfPossible()
        }

        guard let format = formatDescription else {
            // Started mid-stream, before any parameter set arrived. Asking for a keyframe is exactly right,
            // and staying quiet about it is right too -- for a while. A keyframe that has been asked for
            // and has arrived and still leaves us here is a different thing, and says so.
            if isKeyFrame {
                lastError = String(localized: "video.noParameterSets")
            }

            return false
        }

        guard !payload.isEmpty else {
            // A frame of nothing but parameter sets is normal and is not a failure.
            return true
        }

        let ok = enqueue(payload: payload, format: format, isKeyFrame: isKeyFrame)
        if describe {
        }

        return ok
    }

    private func rebuildFormatDescriptionIfPossible() {
        guard let sps = storedSps, let pps = storedPps else { return }

        var created: CMVideoFormatDescription?
        let status = sps.withUnsafeBufferPointer { spsBuffer in
            pps.withUnsafeBufferPointer { ppsBuffer in
                let pointers: [UnsafePointer<UInt8>] = [spsBuffer.baseAddress!, ppsBuffer.baseAddress!]
                let sizes: [Int] = [sps.count, pps.count]
                return pointers.withUnsafeBufferPointer { p in
                    sizes.withUnsafeBufferPointer { s in
                        CMVideoFormatDescriptionCreateFromH264ParameterSets(
                            allocator: kCFAllocatorDefault,
                            parameterSetCount: 2,
                            parameterSetPointers: p.baseAddress!,
                            parameterSetSizes: s.baseAddress!,
                            nalUnitHeaderLength: 4,
                            formatDescriptionOut: &created
                        )
                    }
                }
            }
        }

        if status == noErr {
            formatDescription = created
            // Recovered: whatever went wrong before, this stream is decodable now.
            if lastError != nil { lastError = nil }
        } else {
            lastError = String(format: String(localized: "video.parameterSetsFailed"), Int64(status))
        }
    }

    private func enqueue(payload: Data, format: CMVideoFormatDescription, isKeyFrame: Bool) -> Bool {
        guard let sample = makeSample(payload: payload, format: format) else { return false }

        Self.displayImmediately(sample)
        if displayLayer.status == .failed {
            // The layer gives up permanently after an error; flushing is the only way back.
            displayLayer.flush()
        }

        displayLayer.enqueue(sample)
        framesEnqueued += 1
        return true
    }

    /// One frame as a sample buffer the layer or a decompression session will take, or nil with `lastError` set.
    private func makeSample(payload: Data, format: CMVideoFormatDescription) -> CMSampleBuffer? {
        // The buffer allocates and owns its memory, and the frame is copied into it while the source
        // pointer is still alive.
        //
        // The previous version passed payload.withUnsafeMutableBytes's base address with kCFAllocatorNull
        // and copied afterwards, outside the closure -- by which time that pointer had expired. Data backed
        // by an NSData from Kotlin makes that worse rather than academic: withUnsafeMutableBytes can take a
        // copy-on-write copy, so the block was often left pointing at a temporary buffer that had already
        // been freed. What the decoder then read was whatever had landed there, which is a black picture
        // for as long as it lasts and a crash when it stops being mapped at all.
        var blockBuffer: CMBlockBuffer?
        let blockStatus = CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault,
            memoryBlock: nil,
            blockLength: payload.count,
            blockAllocator: kCFAllocatorDefault,
            customBlockSource: nil,
            offsetToData: 0,
            dataLength: payload.count,
            flags: kCMBlockBufferAssureMemoryNowFlag,
            blockBufferOut: &blockBuffer
        )

        guard blockStatus == kCMBlockBufferNoErr, let ownedBlock = blockBuffer else {
            lastError = String(format: String(localized: "video.blockBufferFailed"), Int64(blockStatus))
            return nil
        }

        let copyStatus = payload.withUnsafeBytes { source -> OSStatus in
            guard let base = source.baseAddress else { return OSStatus(-1) }
            return CMBlockBufferReplaceDataBytes(
                with: base,
                blockBuffer: ownedBlock,
                offsetIntoDestination: 0,
                dataLength: payload.count
            )
        }

        guard copyStatus == kCMBlockBufferNoErr else {
            lastError = String(format: String(localized: "video.copyFailed"), Int64(copyStatus))
            return nil
        }

        var sampleBuffer: CMSampleBuffer?
        var sampleSize = payload.count
        // No presentation time: the host paces the stream and the layer is told to show each frame as it
        // arrives. Inventing timestamps here would add latency for no benefit.
        var timing = CMSampleTimingInfo(
            duration: .invalid,
            presentationTimeStamp: .invalid,
            decodeTimeStamp: .invalid
        )

        let sampleStatus = CMSampleBufferCreateReady(
            allocator: kCFAllocatorDefault,
            dataBuffer: ownedBlock,
            formatDescription: format,
            sampleCount: 1,
            sampleTimingEntryCount: 1,
            sampleTimingArray: &timing,
            sampleSizeEntryCount: 1,
            sampleSizeArray: &sampleSize,
            sampleBufferOut: &sampleBuffer
        )

        guard sampleStatus == noErr, let sample = sampleBuffer else {
            lastError = String(format: String(localized: "video.sampleBufferFailed"), Int64(sampleStatus))
            return nil
        }

        return sample
    }

    // ---- VP9 ----

    /// Whether this device decodes VP9, asked once.
    ///
    /// VideoToolbox's VP9 decoder is a supplemental one: iOS 26.2 and later have it where the hardware does, and it
    /// is registered for this process only when asked for. Whether it is really there is settled by opening a
    /// session for the one kind of stream a host sends -- profile 0, 8-bit 4:2:0 -- rather than by trusting the
    /// registration, and the answer decides whether the session offers VP9 to the host at all.
    static let decodesVp9: Bool = {
        guard #available(iOS 26.2, *) else { return false }

        VTRegisterSupplementalVideoDecoderIfAvailable(kCMVideoCodecType_VP9)
        let desk = Data([1, 0, 0, 0, 0, 40, 0x80, 2, 2, 5, 0, 0])
        guard let format = vp9FormatDescription(record: desk, width: 1280, height: 720, fullRange: false, matrix: 5),
              let session = makeVp9Session(format: format, fullRange: false) else {
            videoLog.notice("VP9: no decoder here (hardware says \(VTIsHardwareDecodeSupported(kCMVideoCodecType_VP9)))")
            return false
        }

        VTDecompressionSessionInvalidate(session)
        videoLog.notice("VP9: decoder available (hardware \(VTIsHardwareDecodeSupported(kCMVideoCodecType_VP9)))")
        return true
    }()

    /// Decodes one VP9 frame and shows the picture.
    ///
    /// Not handed to the layer compressed, as H.264 is: the supplemental decoder is registered for this process,
    /// and a decompression session here is certain to find it where the layer's own decoding might not. The
    /// picture that comes out is enqueued like any other sample, so the layer still does the compositing.
    ///
    /// Synchronous on purpose, so the answer is known before returning: the decoder is hardware, a frame takes a
    /// few milliseconds, and a refusal has to reach the session as a keyframe request rather than get lost.
    private func enqueueVp9(_ frame: Data, isKeyFrame: Bool, configuration: Vp9Configuration?) -> Bool {
        if let configuration, configuration != vp9Configuration || vp9Session == nil {
            rebuildVp9(configuration)
        }

        guard let session = vp9Session, let format = vp9Format else {
            // Joined mid-stream: nothing says what the stream is until a keyframe does. Asking for one is exactly
            // right; a keyframe that could not be read says so.
            if isKeyFrame {
                lastError = String(localized: "video.unsupportedFormat")
            }
            return false
        }

        guard let sample = makeSample(payload: frame, format: format) else { return false }

        let output = DecodedPicture()
        let status = VTDecompressionSessionDecodeFrame(session, sampleBuffer: sample, flags: [], infoFlagsOut: nil) { status, _, image, _, _ in
            output.status = status
            output.image = image
        }

        let failure = status != noErr ? status : output.status
        guard failure == noErr else {
            lastError = String(format: String(localized: "video.sampleBufferFailed"), Int64(failure))
            // A session that failed once is not trusted with the next frame; the keyframe asked for rebuilds it.
            dropVp9()
            return false
        }

        // A frame decoded but not meant to be shown gives no picture, and that is not a failure.
        guard let image = output.image else { return true }
        noteDecoded(image)
        return show(image)
    }

    /// With `SUNLLO_TEST_SNAPSHOT` set, the decoded picture is written to the app's Documents every 30 frames as
    /// `snapshot.png`, beside `snapshot.txt` saying how many frames were decoded and at what size, for
    /// `devicectl device copy from` to bring back. A real phone is the only place VP9 decodes, and nothing that
    /// runs on the Mac can take a screenshot of one.
    private static let snapshotting = ProcessInfo.processInfo.environment["SUNLLO_TEST_SNAPSHOT"] != nil
    private var framesDecoded = 0

    private func noteDecoded(_ image: CVImageBuffer) {
        framesDecoded += 1
        guard Self.snapshotting, framesDecoded == 1 || framesDecoded % 30 == 0,
              let documents = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first else {
            return
        }

        let picture = CIImage(cvImageBuffer: image)
        if let cgImage = CIContext().createCGImage(picture, from: picture.extent),
           let png = UIImage(cgImage: cgImage).pngData() {
            try? png.write(to: documents.appendingPathComponent("snapshot.png"))
        }

        let summary = "vp9 frames=\(framesDecoded) size=\(CVPixelBufferGetWidth(image))x\(CVPixelBufferGetHeight(image))\n"
        try? Data(summary.utf8).write(to: documents.appendingPathComponent("snapshot.txt"))
    }

    private func rebuildVp9(_ configuration: Vp9Configuration) {
        dropVp9()
        guard let format = Self.vp9FormatDescription(
                  record: configuration.recordData,
                  width: configuration.width,
                  height: configuration.height,
                  fullRange: configuration.fullRange,
                  matrix: configuration.matrixCoefficients),
              let session = Self.makeVp9Session(format: format, fullRange: configuration.fullRange) else {
            videoLog.error("VP9: no session for profile \(configuration.profile) \(configuration.bitDepth)-bit \(configuration.width)x\(configuration.height)")
            lastError = String(localized: "video.unsupportedFormat")
            return
        }

        vp9Configuration = configuration
        vp9Format = format
        vp9Session = session
        // Whatever the layer holds is from another stream.
        displayLayer.flush()
        if lastError != nil { lastError = nil }
        videoLog.notice("VP9: decoding profile \(configuration.profile) \(configuration.bitDepth)-bit \(configuration.width)x\(configuration.height), matrix \(configuration.matrixCoefficients)")
    }

    private func dropVp9() {
        if let session = vp9Session {
            VTDecompressionSessionInvalidate(session)
        }
        vp9Session = nil
        vp9Format = nil
        vp9Configuration = nil
    }

    private func show(_ image: CVImageBuffer) -> Bool {
        var format: CMVideoFormatDescription?
        guard CMVideoFormatDescriptionCreateForImageBuffer(allocator: kCFAllocatorDefault, imageBuffer: image, formatDescriptionOut: &format) == noErr,
              let format else {
            return false
        }

        var timing = CMSampleTimingInfo(duration: .invalid, presentationTimeStamp: .invalid, decodeTimeStamp: .invalid)
        var sample: CMSampleBuffer?
        guard CMSampleBufferCreateReadyWithImageBuffer(
                  allocator: kCFAllocatorDefault,
                  imageBuffer: image,
                  formatDescription: format,
                  sampleTiming: &timing,
                  sampleBufferOut: &sample) == noErr,
              let sample else {
            return false
        }

        Self.displayImmediately(sample)
        if displayLayer.status == .failed {
            displayLayer.flush()
        }

        displayLayer.enqueue(sample)
        framesEnqueued += 1
        return true
    }

    /// The format description VideoToolbox decodes VP9 against: the vpcC record, as a file's sample description
    /// would carry it, plus the range and matrix spelled out for whatever converts the picture for display.
    private static func vp9FormatDescription(record: Data, width: Int32, height: Int32, fullRange: Bool, matrix: Int32) -> CMVideoFormatDescription? {
        var extensions: [CFString: Any] = [
            kCMFormatDescriptionExtension_SampleDescriptionExtensionAtoms: ["vpcC": record] as CFDictionary,
            kCMFormatDescriptionExtension_FullRangeVideo: fullRange,
        ]
        if let ycbcr = ycbcrMatrix(matrix) {
            extensions[kCMFormatDescriptionExtension_YCbCrMatrix] = ycbcr
        }

        var format: CMVideoFormatDescription?
        let status = CMVideoFormatDescriptionCreate(
            allocator: kCFAllocatorDefault,
            codecType: kCMVideoCodecType_VP9,
            width: width,
            height: height,
            extensions: extensions as CFDictionary,
            formatDescriptionOut: &format
        )
        return status == noErr ? format : nil
    }

    private static func makeVp9Session(format: CMVideoFormatDescription, fullRange: Bool) -> VTDecompressionSession? {
        let attributes: [CFString: Any] = [
            kCVPixelBufferPixelFormatTypeKey: fullRange
                ? kCVPixelFormatType_420YpCbCr8BiPlanarFullRange
                : kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange,
            // Shared with the window server, so the layer shows the picture without copying it.
            kCVPixelBufferIOSurfacePropertiesKey: [:] as CFDictionary,
        ]

        var session: VTDecompressionSession?
        let status = VTDecompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            formatDescription: format,
            decoderSpecification: nil,
            imageBufferAttributes: attributes as CFDictionary,
            outputCallback: nil,
            decompressionSessionOut: &session
        )
        if status != noErr {
            videoLog.error("VP9: VTDecompressionSessionCreate returned \(status)")
        }
        return status == noErr ? session : nil
    }

    /// ISO/IEC 23091-4 matrix codes, as `Vp9Configuration` records them, in VideoToolbox's vocabulary.
    private static func ycbcrMatrix(_ code: Int32) -> CFString? {
        switch code {
        case 1: return kCMFormatDescriptionYCbCrMatrix_ITU_R_709_2
        case 5, 6: return kCMFormatDescriptionYCbCrMatrix_ITU_R_601_4
        case 7: return kCMFormatDescriptionYCbCrMatrix_SMPTE_240M_1995
        case 9: return kCMFormatDescriptionYCbCrMatrix_ITU_R_2020
        default: return nil
        }
    }

    /// Shows the sample the moment it is enqueued: the host paces the stream, and there are no timestamps to wait for.
    private static func displayImmediately(_ sample: CMSampleBuffer) {
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: true),
           CFArrayGetCount(attachments) > 0 {
            let dictionary = unsafeBitCast(CFArrayGetValueAtIndex(attachments, 0), to: CFMutableDictionary.self)
            CFDictionarySetValue(
                dictionary,
                Unmanaged.passUnretained(kCMSampleAttachmentKey_DisplayImmediately).toOpaque(),
                Unmanaged.passUnretained(kCFBooleanTrue).toOpaque()
            )
        }
    }

    /// Splits an Annex-B stream into its NAL units. Start codes are three or four bytes, and both occur.
    private static func splitAnnexB(_ data: Data) -> [Data] {
        var units: [Data] = []
        var index = data.startIndex
        var unitStart: Int?

        while index < data.endIndex {
            let remaining = data.endIndex - index
            var startCodeLength = 0

            if remaining >= 4, data[index] == 0, data[index + 1] == 0, data[index + 2] == 0, data[index + 3] == 1 {
                startCodeLength = 4
            } else if remaining >= 3, data[index] == 0, data[index + 1] == 0, data[index + 2] == 1 {
                startCodeLength = 3
            }

            if startCodeLength > 0 {
                if let start = unitStart, index > start {
                    units.append(data.subdata(in: start..<index))
                }
                index += startCodeLength
                unitStart = index
            } else {
                index += 1
            }
        }

        if let start = unitStart, start < data.endIndex {
            units.append(data.subdata(in: start..<data.endIndex))
        }

        return units
    }
}

/// What a synchronous decode hands back from inside VideoToolbox's output handler.
private final class DecodedPicture {
    var status: OSStatus = noErr
    var image: CVImageBuffer?
}

extension Vp9Configuration {
    /// The vpcC record as bytes. Twelve of them, so reading the Kotlin array one element at a time costs nothing.
    var recordData: Data {
        let bytes = record()
        return Data((0..<bytes.size).map { UInt8(bitPattern: bytes.get(index: $0)) })
    }
}

/// Bridges the UIKit view into SwiftUI and hands it to the model, which feeds it frames.
struct RemoteVideo: UIViewRepresentable {
    let model: SessionModel

    func makeUIView(context: Context) -> VideoDisplayView {
        let view = VideoDisplayView()
        model.attach(view)
        return view
    }

    func updateUIView(_ uiView: VideoDisplayView, context: Context) {}

    static func dismantleUIView(_ uiView: VideoDisplayView, coordinator: ()) {}
}

/// The picture, placed and sized by the canvas.
///
/// A plain frame rather than a transform: the layer scales its own contents to whatever rectangle it is
/// given, so panning is a position and zooming is a size, and the arithmetic that maps a touch back to a
/// host pixel is the same arithmetic that put the picture there.
struct CanvasVideo: View {
    let model: SessionModel
    let canvas: CanvasState

    var body: some View {
        RemoteVideo(model: model)
            .frame(
                width: max(1, CGFloat(canvas.hostWidth) * CGFloat(canvas.scale)),
                height: max(1, CGFloat(canvas.hostHeight) * CGFloat(canvas.scale))
            )
            .position(
                x: CGFloat(canvas.x) + CGFloat(canvas.hostWidth) * CGFloat(canvas.scale) / 2,
                y: CGFloat(canvas.y) + CGFloat(canvas.hostHeight) * CGFloat(canvas.scale) / 2
            )
    }
}
