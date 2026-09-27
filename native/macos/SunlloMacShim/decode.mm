// Hardware video decode through VideoToolbox, for a Mac controlling a remote host. Input is the Annex B the
// encoders elsewhere emit; the decoder splits it into NAL units, (re)builds the format description from the
// parameter sets, repackages the slice NALs as the AVCC (length-prefixed) a CMSampleBuffer wants, decodes to
// a BGRA CVPixelBuffer and hands the locked pixels up. It supports H.264 and HEVC.
#import <Foundation/Foundation.h>
#import <VideoToolbox/VideoToolbox.h>
#import <CoreVideo/CoreVideo.h>
#import "shim.h"
#include <vector>

typedef struct {
    int32_t codec;                     // 0 h264, 1 hevc
    VTDecompressionSessionRef session;
    CMVideoFormatDescriptionRef format;
    NSData *sps, *pps, *vps;           // last parameter sets, to detect changes
    CVPixelBufferRef decoded;          // newest decoded frame (retained)
    CVPixelBufferRef locked;           // base-address-locked during a copy
} FdDecoderImpl;

// Splits an Annex B buffer into NAL payloads (without start codes).
static std::vector<std::pair<const uint8_t *, size_t>> splitNals(const uint8_t *data, size_t len) {
    std::vector<std::pair<const uint8_t *, size_t>> nals;
    size_t i = 0;
    // Find the first start code.
    auto isStart = [&](size_t p, int *scLen) -> bool {
        if (p + 3 <= len && data[p] == 0 && data[p+1] == 0 && data[p+2] == 1) { *scLen = 3; return true; }
        if (p + 4 <= len && data[p] == 0 && data[p+1] == 0 && data[p+2] == 0 && data[p+3] == 1) { *scLen = 4; return true; }
        return false;
    };
    int sc = 0;
    while (i < len && !isStart(i, &sc)) { i++; }
    while (i < len) {
        i += sc;
        size_t start = i;
        while (i < len && !isStart(i, &sc)) { i++; }
        if (i > start) {
            nals.push_back({data + start, i - start});
        }
    }
    return nals;
}

static void decodeOutput(void *decompressionOutputRefCon, void *sourceFrameRefCon, OSStatus status,
                         VTDecodeInfoFlags infoFlags, CVImageBufferRef imageBuffer,
                         CMTime pts, CMTime dur) {
    FdDecoderImpl *impl = (FdDecoderImpl *)decompressionOutputRefCon;
    if (status != noErr || imageBuffer == NULL) {
        return;
    }
    CVPixelBufferRef pb = (CVPixelBufferRef)imageBuffer;
    CVPixelBufferRetain(pb);
    if (impl->decoded) {
        CVPixelBufferRelease(impl->decoded);
    }
    impl->decoded = pb;
}

static bool rebuildFormat(FdDecoderImpl *impl, std::vector<std::pair<const uint8_t*,size_t>> &params) {
    std::vector<const uint8_t *> ptrs;
    std::vector<size_t> sizes;
    for (auto &p : params) { ptrs.push_back(p.first); sizes.push_back(p.second); }
    CMFormatDescriptionRef fmt = NULL;
    OSStatus s;
    if (impl->codec == 1) {
        s = CMVideoFormatDescriptionCreateFromHEVCParameterSets(kCFAllocatorDefault, ptrs.size(), ptrs.data(), sizes.data(), 4, NULL, &fmt);
    } else {
        s = CMVideoFormatDescriptionCreateFromH264ParameterSets(kCFAllocatorDefault, ptrs.size(), ptrs.data(), sizes.data(), 4, &fmt);
    }
    if (s != noErr || fmt == NULL) {
        return false;
    }
    if (impl->format) { CFRelease(impl->format); }
    impl->format = fmt;
    if (impl->session) { VTDecompressionSessionInvalidate(impl->session); CFRelease(impl->session); impl->session = NULL; }

    NSDictionary *attrs = @{ (id)kCVPixelBufferPixelFormatTypeKey: @(kCVPixelFormatType_32BGRA),
                             (id)kCVPixelBufferIOSurfacePropertiesKey: @{} };
    VTDecompressionOutputCallbackRecord cb = { decodeOutput, impl };
    s = VTDecompressionSessionCreate(kCFAllocatorDefault, impl->format, NULL,
                                     (__bridge CFDictionaryRef)attrs, &cb, &impl->session);
    return s == noErr;
}

FdDecoder fd_decoder_create(int32_t codec) {
    FdDecoderImpl *impl = (FdDecoderImpl *)calloc(1, sizeof(FdDecoderImpl));
    impl->codec = codec;
    return (FdDecoder)impl;
}

int32_t fd_decoder_decode(FdDecoder h, const uint8_t *annexb, int32_t len, FdDecodedFrame *out) {
    FdDecoderImpl *impl = (FdDecoderImpl *)h;
    if (impl == NULL || annexb == NULL || len <= 0 || out == NULL) {
        return -1;
    }

    auto nals = splitNals(annexb, (size_t)len);
    std::vector<std::pair<const uint8_t*,size_t>> params;
    std::vector<std::pair<const uint8_t*,size_t>> vcl;
    bool haveNewParams = false;

    for (auto &n : nals) {
        int type = impl->codec == 1 ? ((n.first[0] >> 1) & 0x3F) : (n.first[0] & 0x1F);
        bool isParam = impl->codec == 1 ? (type == 32 || type == 33 || type == 34) : (type == 7 || type == 8);
        if (isParam) {
            params.push_back(n);
            haveNewParams = true;
        } else {
            vcl.push_back(n);
        }
    }

    if (haveNewParams && !params.empty()) {
        // Parameter sets must be ordered VPS,SPS,PPS for HEVC / SPS,PPS for H.264; they arrive in that order.
        if (!rebuildFormat(impl, params)) {
            return -1;
        }
    }
    if (impl->session == NULL || vcl.empty()) {
        return 0; // waiting for parameter sets, or a params-only unit
    }

    // Repackage the slice NALs as AVCC: 4-byte big-endian length + payload.
    size_t total = 0;
    for (auto &n : vcl) { total += 4 + n.second; }
    uint8_t *avcc = (uint8_t *)malloc(total);
    size_t off = 0;
    for (auto &n : vcl) {
        avcc[off++] = (uint8_t)((n.second >> 24) & 0xFF);
        avcc[off++] = (uint8_t)((n.second >> 16) & 0xFF);
        avcc[off++] = (uint8_t)((n.second >> 8) & 0xFF);
        avcc[off++] = (uint8_t)(n.second & 0xFF);
        memcpy(avcc + off, n.first, n.second);
        off += n.second;
    }

    CMBlockBufferRef bb = NULL;
    if (CMBlockBufferCreateWithMemoryBlock(kCFAllocatorDefault, avcc, total, kCFAllocatorMalloc, NULL, 0, total, 0, &bb) != kCMBlockBufferNoErr) {
        free(avcc);
        return -1;
    }
    CMSampleBufferRef sample = NULL;
    const size_t sizes[1] = { total };
    OSStatus s = CMSampleBufferCreateReady(kCFAllocatorDefault, bb, impl->format, 1, 0, NULL, 1, sizes, &sample);
    CFRelease(bb);
    if (s != noErr || sample == NULL) {
        return -1;
    }

    VTDecodeInfoFlags flags;
    s = VTDecompressionSessionDecodeFrame(impl->session, sample, kVTDecodeFrame_EnableTemporalProcessing, NULL, &flags);
    CFRelease(sample);
    if (s != noErr) {
        return -1;
    }
    VTDecompressionSessionWaitForAsynchronousFrames(impl->session);

    if (impl->decoded == NULL) {
        return 0;
    }
    CVPixelBufferRef pb = impl->decoded;
    CVPixelBufferLockBaseAddress(pb, kCVPixelBufferLock_ReadOnly);
    impl->locked = pb;
    out->width = (int32_t)CVPixelBufferGetWidth(pb);
    out->height = (int32_t)CVPixelBufferGetHeight(pb);
    out->stride = (int32_t)CVPixelBufferGetBytesPerRow(pb);
    out->data = (const uint8_t *)CVPixelBufferGetBaseAddress(pb);
    return 1;
}

void fd_decoder_release(FdDecoder h) {
    FdDecoderImpl *impl = (FdDecoderImpl *)h;
    if (impl && impl->locked) {
        CVPixelBufferUnlockBaseAddress(impl->locked, kCVPixelBufferLock_ReadOnly);
        impl->locked = NULL;
    }
}

void fd_decoder_destroy(FdDecoder h) {
    FdDecoderImpl *impl = (FdDecoderImpl *)h;
    if (impl == NULL) {
        return;
    }
    if (impl->locked) { CVPixelBufferUnlockBaseAddress(impl->locked, kCVPixelBufferLock_ReadOnly); }
    if (impl->decoded) { CVPixelBufferRelease(impl->decoded); }
    if (impl->session) { VTDecompressionSessionInvalidate(impl->session); CFRelease(impl->session); }
    if (impl->format) { CFRelease(impl->format); }
    free(impl);
}
