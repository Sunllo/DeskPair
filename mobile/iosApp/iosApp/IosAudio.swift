import AVFoundation
import DeskPairShared

/// Plays the host's sound.
///
/// The plan for this assumed iOS had no Opus decoder and that libopus would have to be vendored as an
/// XCFramework. It does have one: `kAudioFormatOpus` is in what AudioToolbox reports it can decode, so
/// AVAudioConverter will take raw Opus packets and hand back PCM, and nothing has to ship alongside the app.
/// `isAvailable` records whether that held on this device, because the honest fallback is silence with a
/// reason rather than a decoder that half works.
final class IosAudio: NSObject, NativeAudioSink {

    private let engine = AVAudioEngine()
    private let player = AVAudioPlayerNode()

    private var converter: AVAudioConverter?
    private var pcmFormat: AVAudioFormat?
    private var maximumPacketSize = 0

    /// Set once the decoder has been built, so the UI can say "no sound, and here is why".
    private(set) var isAvailable = false
    private(set) var unavailableReason: String? {
        didSet { onProblem?(unavailableReason) }
    }

    /// Told whenever the reason changes, so a screen can show it instead of just being quiet.
    var onProblem: ((String?) -> Void)?

    /// Frames handed to the player, so a test can assert on something other than someone listening.
    private(set) var framesPlayed = 0

    func onFormat(sampleRate: Int32, channels: Int32) {
        stop()

        guard let opus = opusFormat(sampleRate: Double(sampleRate), channels: UInt32(channels)),
              let pcm = AVAudioFormat(standardFormatWithSampleRate: Double(sampleRate),
                                      channels: AVAudioChannelCount(channels)),
              let converter = AVAudioConverter(from: opus, to: pcm) else {
            isAvailable = false
            unavailableReason = String(localized: "audio.noOpus")
            sessionLog.error("audio unavailable: no Opus decoder for \(sampleRate, privacy: .public) Hz")
            return
        }

        self.converter = converter
        self.pcmFormat = pcm
        // 120 ms at 48 kHz stereo is far more than a 10 ms Opus packet ever needs; the field only has to be
        // an upper bound the buffer can be sized from.
        self.maximumPacketSize = 1500

        do {
            try configureSession()
            engine.attach(player)
            engine.connect(player, to: engine.mainMixerNode, format: pcm)
            try engine.start()
            player.play()
            isAvailable = true
            unavailableReason = nil
            sessionLog.notice("audio ready: \(sampleRate, privacy: .public) Hz, \(channels, privacy: .public) ch")
        } catch {
            isAvailable = false
            unavailableReason = error.localizedDescription
            sessionLog.error("audio engine failed: \(error.localizedDescription, privacy: .public)")
        }
    }

    // NSData rather than a Kotlin ByteArray: an Opus packet arrives every ten milliseconds and reading one
    // through the bridge a byte at a time would cost more than decoding it.
    func onFrame(opus: Data, ptsMs: Int64) {
        guard isAvailable, let converter, let pcmFormat else { return }

        let bytes = opus
        guard !bytes.isEmpty else { return }

        let compressed = AVAudioCompressedBuffer(
            format: converter.inputFormat,
            packetCapacity: 1,
            maximumPacketSize: maximumPacketSize
        )
        compressed.byteLength = UInt32(bytes.count)
        compressed.packetCount = 1
        bytes.withUnsafeBytes { raw in
            compressed.data.copyMemory(from: raw.baseAddress!, byteCount: bytes.count)
        }
        compressed.packetDescriptions?.pointee = AudioStreamPacketDescription(
            mStartOffset: 0,
            mVariableFramesInPacket: 0,
            mDataByteSize: UInt32(bytes.count)
        )

        // One packet is 10 ms; the buffer is sized for more so a converter that wants to emit the pre-skip
        // along with the first frame has somewhere to put it.
        guard let out = AVAudioPCMBuffer(pcmFormat: pcmFormat, frameCapacity: 4096) else { return }

        var supplied = false
        var error: NSError?
        let status = converter.convert(to: out, error: &error) { _, outStatus in
            if supplied {
                outStatus.pointee = .noDataNow
                return nil
            }
            supplied = true
            outStatus.pointee = .haveData
            return compressed
        }

        guard status != .error, out.frameLength > 0 else {
            if let error {
                sessionLog.error("audio decode failed: \(error.localizedDescription, privacy: .public)")
            }
            return
        }

        framesPlayed += 1
        // The first one proves the decoder works; after that every five seconds is enough to tell a stream
        // that stopped from one the host is simply not filling, without writing a line per packet.
        if framesPlayed == 1 || framesPlayed % 500 == 0 {
            sessionLog.debug("audio decoded \(self.framesPlayed) packet(s), \(out.frameLength) samples in the last")
        }
        player.scheduleBuffer(out, completionHandler: nil)
    }

    func stop() {
        if engine.isRunning {
            player.stop()
            engine.stop()
            engine.detach(player)
        }
        converter = nil
        isAvailable = false
    }

    /// Playback only, and mixing with whatever else is going on: a remote desktop is not a phone call, and
    /// taking exclusive control of the audio session would silence the user's music to play nothing.
    private func configureSession() throws {
        let session = AVAudioSession.sharedInstance()
        try session.setCategory(.playback, mode: .default, options: [.mixWithOthers])
        try session.setActive(true)
    }

    private func opusFormat(sampleRate: Double, channels: UInt32) -> AVAudioFormat? {
        var description = AudioStreamBasicDescription(
            mSampleRate: sampleRate,
            mFormatID: kAudioFormatOpus,
            mFormatFlags: 0,
            mBytesPerPacket: 0,
            // 10 ms, which is what the host encodes. A decoder reads the real length from the packet, but
            // the format still has to name one.
            mFramesPerPacket: UInt32(sampleRate / 100),
            mBytesPerFrame: 0,
            mChannelsPerFrame: channels,
            mBitsPerChannel: 0,
            mReserved: 0
        )
        return AVAudioFormat(streamDescription: &description)
    }
}
