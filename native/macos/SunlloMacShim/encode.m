// Hardware video encode through VideoToolbox. The M-series has a dedicated H.264/HEVC encoder; a
// VTCompressionSession drives it. Input is BGRA wrapped in a CVPixelBuffer (VideoToolbox does the colour
// conversion). Output is converted from VideoToolbox's AVCC layout (4-byte length-prefixed NAL units, with
// the parameter sets carried out of band in the format description) into Annex B (start-code delimited, with
// SPS/PPS prepended on each keyframe) so the Windows Media Foundation decoder on the controller can read the
// same bytes the software encoders produce.
#import <Foundation/Foundation.h>
#import <VideoToolbox/VideoToolbox.h>
#import <CoreVideo/CoreVideo.h>
#import "shim.h"

static const uint8_t kStartCode[4] = {0, 0, 0, 1};

typedef struct {
    VTCompressionSessionRef session;
    int32_t width;
    int32_t height;
    int32_t codec;              // 0 h264, 1 hevc
    int32_t isHardware;
    dispatch_semaphore_t done;  // signalled by the output callback
    NSMutableData *out;         // Annex B for the frame in flight
    int32_t keyframe;
    int32_t ok;
} FdEncoderImpl;

// Appends every NAL in an AVCC block buffer as Annex B (start code + payload).
static void appendAvccAsAnnexB(NSMutableData *dst, CMBlockBufferRef bb) {
    size_t total = CMBlockBufferGetDataLength(bb);
    size_t offset = 0;
    while (offset + 4 <= total) {
        uint8_t lenBytes[4];
        CMBlockBufferCopyDataBytes(bb, offset, 4, lenBytes);
        uint32_t nalLen = ((uint32_t)lenBytes[0] << 24) | ((uint32_t)lenBytes[1] << 16) | ((uint32_t)lenBytes[2] << 8) | (uint32_t)lenBytes[3];
        offset += 4;
        if (nalLen == 0 || offset + nalLen > total) {
            break;
        }
        [dst appendBytes:kStartCode length:4];
        char *ptr = NULL;
        size_t lengthAtOffset = 0, totalLength = 0;
        if (CMBlockBufferGetDataPointer(bb, offset, &lengthAtOffset, &totalLength, &ptr) == kCMBlockBufferNoErr && lengthAtOffset >= nalLen) {
            [dst appendBytes:ptr length:nalLen];
        } else {
            void *tmp = malloc(nalLen);
            CMBlockBufferCopyDataBytes(bb, offset, nalLen, tmp);
            [dst appendBytes:tmp length:nalLen];
            free(tmp);
        }
        offset += nalLen;
    }
}

// Prepends the parameter sets (SPS/PPS, or VPS/SPS/PPS for HEVC) from the format description, as Annex B.
static void appendParameterSets(NSMutableData *dst, CMFormatDescriptionRef fmt, int32_t codec) {
    size_t count = 0;
    int nalHeaderLen = 0;
    if (codec == 1) {
        CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(fmt, 0, NULL, NULL, &count, &nalHeaderLen);
    } else {
        CMVideoFormatDescriptionGetH264ParameterSetAtIndex(fmt, 0, NULL, NULL, &count, &nalHeaderLen);
    }
    for (size_t i = 0; i < count; i++) {
        const uint8_t *ps = NULL;
        size_t psSize = 0;
        OSStatus s;
        if (codec == 1) {
            s = CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(fmt, i, &ps, &psSize, NULL, NULL);
        } else {
            s = CMVideoFormatDescriptionGetH264ParameterSetAtIndex(fmt, i, &ps, &psSize, NULL, NULL);
        }
        if (s == noErr && ps && psSize > 0) {
            [dst appendBytes:kStartCode length:4];
            [dst appendBytes:ps length:psSize];
        }
    }
}

static void encodeOutput(void *outputCallbackRefCon, void *sourceFrameRefCon, OSStatus status,
                         VTEncodeInfoFlags infoFlags, CMSampleBufferRef sampleBuffer) {
    FdEncoderImpl *impl = (FdEncoderImpl *)outputCallbackRefCon;
    impl->ok = 0;
    impl->keyframe = 0;
    if (status != noErr || sampleBuffer == NULL || !CMSampleBufferDataIsReady(sampleBuffer)) {
        dispatch_semaphore_signal(impl->done);
        return;
    }

    // A keyframe has no "not sync" attachment.
    BOOL keyframe = YES;
    CFArrayRef attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, false);
    if (attachments && CFArrayGetCount(attachments) > 0) {
        CFDictionaryRef dict = CFArrayGetValueAtIndex(attachments, 0);
        CFBooleanRef notSync = CFDictionaryGetValue(dict, kCMSampleAttachmentKey_NotSync);
        if (notSync && CFBooleanGetValue(notSync)) {
            keyframe = NO;
        }
    }

    impl->out.length = 0;
    if (keyframe) {
        CMFormatDescriptionRef fmt = CMSampleBufferGetFormatDescription(sampleBuffer);
        if (fmt) {
            appendParameterSets(impl->out, fmt, impl->codec);
        }
    }
    CMBlockBufferRef bb = CMSampleBufferGetDataBuffer(sampleBuffer);
    if (bb) {
        appendAvccAsAnnexB(impl->out, bb);
    }

    impl->keyframe = keyframe ? 1 : 0;
    impl->ok = 1;
    dispatch_semaphore_signal(impl->done);
}

FdEncoder fd_encoder_create(int32_t width, int32_t height, int32_t fps, int32_t bitrateKbps, int32_t codec) {
    FdEncoderImpl *impl = (FdEncoderImpl *)calloc(1, sizeof(FdEncoderImpl));
    impl->width = width;
    impl->height = height;
    impl->codec = codec;
    impl->done = dispatch_semaphore_create(0);
    impl->out = [[NSMutableData alloc] initWithCapacity:width * height];
    CFBridgingRetain(impl->out); // keep it alive alongside the C struct

    CMVideoCodecType type = codec == 1 ? kCMVideoCodecType_HEVC : kCMVideoCodecType_H264;
    NSDictionary *encoderSpec = @{ (id)kVTVideoEncoderSpecification_EnableHardwareAcceleratedVideoEncoder: @YES };
    OSStatus s = VTCompressionSessionCreate(kCFAllocatorDefault, width, height, type,
                                            (__bridge CFDictionaryRef)encoderSpec, NULL, NULL,
                                            encodeOutput, impl, &impl->session);
    if (s != noErr || impl->session == NULL) {
        free(impl);
        return NULL;
    }

    CFBooleanRef usingHw = NULL;
    if (VTSessionCopyProperty(impl->session, kVTCompressionPropertyKey_UsingHardwareAcceleratedVideoEncoder, kCFAllocatorDefault, &usingHw) == noErr && usingHw) {
        impl->isHardware = CFBooleanGetValue(usingHw) ? 1 : 0;
        CFRelease(usingHw);
    }

    VTSessionSetProperty(impl->session, kVTCompressionPropertyKey_RealTime, kCFBooleanTrue);
    VTSessionSetProperty(impl->session, kVTCompressionPropertyKey_AllowFrameReordering, kCFBooleanFalse);
    if (codec != 1) {
        VTSessionSetProperty(impl->session, kVTCompressionPropertyKey_ProfileLevel, kVTProfileLevel_H264_High_AutoLevel);
    }
    int32_t frameInterval = fps > 0 ? fps * 2 : 120; // keyframe at least every ~2 s; QoS also forces them
    CFNumberRef maxKf = CFNumberCreate(NULL, kCFNumberSInt32Type, &frameInterval);
    VTSessionSetProperty(impl->session, kVTCompressionPropertyKey_MaxKeyFrameInterval, maxKf);
    CFRelease(maxKf);
    int32_t expected = fps > 0 ? fps : 60;
    CFNumberRef fr = CFNumberCreate(NULL, kCFNumberSInt32Type, &expected);
    VTSessionSetProperty(impl->session, kVTCompressionPropertyKey_ExpectedFrameRate, fr);
    CFRelease(fr);

    fd_encoder_set_bitrate((FdEncoder)impl, bitrateKbps);
    VTCompressionSessionPrepareToEncodeFrames(impl->session);
    return (FdEncoder)impl;
}

void fd_encoder_set_bitrate(FdEncoder h, int32_t bitrateKbps) {
    FdEncoderImpl *impl = (FdEncoderImpl *)h;
    if (impl == NULL || impl->session == NULL) {
        return;
    }
    int32_t bps = bitrateKbps * 1000;
    CFNumberRef avg = CFNumberCreate(NULL, kCFNumberSInt32Type, &bps);
    VTSessionSetProperty(impl->session, kVTCompressionPropertyKey_AverageBitRate, avg);
    CFRelease(avg);

    // A data-rate cap keeps a single frame from blowing the wire budget, but the window has to be short to
    // do it: measured over a whole second the encoder can spend most of the budget on one frame and still be
    // "within limits", which is exactly what made dragging a window stutter — a 486 KB frame needs ~90 ms of
    // a 43 Mb/s link, several frame times. Windows sizes its rate buffer at 0.2 s for the same reason, so
    // match it here and let the encoder spread the bits instead of spiking.
    const double windowSeconds = 0.2;
    int64_t cap = (int64_t)((double)bitrateKbps * 1000.0 / 8.0 * windowSeconds * 1.25);
    CFNumberRef bytes = CFNumberCreate(NULL, kCFNumberSInt64Type, &cap);
    CFNumberRef dur = CFNumberCreate(NULL, kCFNumberDoubleType, &windowSeconds);
    CFTypeRef vals[] = { bytes, dur };
    CFArrayRef limits = CFArrayCreate(NULL, vals, 2, &kCFTypeArrayCallBacks);
    VTSessionSetProperty(impl->session, kVTCompressionPropertyKey_DataRateLimits, limits);
    CFRelease(limits);
    CFRelease(bytes);
    CFRelease(dur);
}

int32_t fd_encoder_is_hardware(FdEncoder h) {
    FdEncoderImpl *impl = (FdEncoderImpl *)h;
    return impl ? impl->isHardware : 0;
}

static CVPixelBufferRef wrapBgra(const uint8_t *bgra, int32_t width, int32_t height, int32_t stride) {
    CVPixelBufferRef pb = NULL;
    NSDictionary *attrs = @{ (id)kCVPixelBufferIOSurfacePropertiesKey: @{} };
    if (CVPixelBufferCreate(kCFAllocatorDefault, width, height, kCVPixelFormatType_32BGRA,
                            (__bridge CFDictionaryRef)attrs, &pb) != kCVReturnSuccess) {
        return NULL;
    }
    CVPixelBufferLockBaseAddress(pb, 0);
    uint8_t *dst = CVPixelBufferGetBaseAddress(pb);
    size_t dstStride = CVPixelBufferGetBytesPerRow(pb);
    for (int32_t y = 0; y < height; y++) {
        memcpy(dst + (size_t)y * dstStride, bgra + (size_t)y * stride, (size_t)width * 4);
    }
    CVPixelBufferUnlockBaseAddress(pb, 0);
    return pb;
}

int32_t fd_encoder_encode(FdEncoder h, const uint8_t *bgra, int32_t stride, int64_t ptsTicks, int32_t forceKeyframe, FdPacket *out) {
    FdEncoderImpl *impl = (FdEncoderImpl *)h;
    if (impl == NULL || impl->session == NULL || bgra == NULL || out == NULL) {
        return -1;
    }

    CVPixelBufferRef pb = wrapBgra(bgra, impl->width, impl->height, stride);
    if (pb == NULL) {
        return -1;
    }

    // 100 ns ticks -> seconds for a 600-tick timescale presentation time.
    CMTime pts = CMTimeMake(ptsTicks / 100, 10000000);
    NSDictionary *props = forceKeyframe ? @{ (id)kVTEncodeFrameOptionKey_ForceKeyFrame: @YES } : nil;

    VTEncodeInfoFlags flags;
    OSStatus s = VTCompressionSessionEncodeFrame(impl->session, pb, pts, kCMTimeInvalid,
                                                 (__bridge CFDictionaryRef)props, NULL, &flags);
    CVPixelBufferRelease(pb);
    if (s != noErr) {
        return -1;
    }

    // Low latency: wait for this frame's callback rather than pipelining.
    dispatch_semaphore_wait(impl->done, dispatch_time(DISPATCH_TIME_NOW, 200 * NSEC_PER_MSEC));
    if (!impl->ok || impl->out.length == 0) {
        return 0;
    }

    out->data = (const uint8_t *)impl->out.bytes;
    out->length = (int32_t)impl->out.length;
    out->isKeyframe = impl->keyframe;
    return 1;
}

void fd_encoder_release(FdEncoder h) {
    // The buffer lives in impl->out and is reused next encode; nothing to free between frames.
    (void)h;
}

void fd_encoder_destroy(FdEncoder h) {
    FdEncoderImpl *impl = (FdEncoderImpl *)h;
    if (impl == NULL) {
        return;
    }
    if (impl->session) {
        VTCompressionSessionCompleteFrames(impl->session, kCMTimeInvalid);
        VTCompressionSessionInvalidate(impl->session);
        CFRelease(impl->session);
    }
    if (impl->out) {
        CFBridgingRelease((__bridge CFTypeRef)impl->out);
    }
    free(impl);
}
