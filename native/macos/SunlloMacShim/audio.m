// Controller-side audio playback through an AudioQueue. (System-audio capture now lives in capture.m, sharing
// the one ScreenCaptureKit stream with video.) Playback keeps a small pool of queue buffers and copies each
// enqueue into a free one, blocking only if every buffer is still in flight.
#import <Foundation/Foundation.h>
#import <AudioToolbox/AudioToolbox.h>
#import "shim.h"


#define FD_PLAYBACK_BUFFERS 4

typedef struct {
    AudioQueueRef queue;
    AudioQueueBufferRef buffers[FD_PLAYBACK_BUFFERS];
    dispatch_semaphore_t free;   // counts buffers available to fill
    NSObject *lock;
} FdPlaybackImpl;

static void playbackCallback(void *userData, AudioQueueRef queue, AudioQueueBufferRef buffer) {
    FdPlaybackImpl *impl = (FdPlaybackImpl *)userData;
    buffer->mAudioDataByteSize = 0;        // mark it free for the enqueue scan
    dispatch_semaphore_signal(impl->free); // this buffer is drained and free again
}

FdAudioPlayback fd_audio_playback_create(int32_t sampleRate, int32_t channels) {
    FdPlaybackImpl *impl = (FdPlaybackImpl *)calloc(1, sizeof(FdPlaybackImpl));
    impl->free = dispatch_semaphore_create(FD_PLAYBACK_BUFFERS);
    impl->lock = [[NSObject alloc] init];
    CFBridgingRetain(impl->lock);

    AudioStreamBasicDescription fmt = {0};
    fmt.mSampleRate = sampleRate;
    fmt.mFormatID = kAudioFormatLinearPCM;
    fmt.mFormatFlags = kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked;
    fmt.mChannelsPerFrame = channels;
    fmt.mBitsPerChannel = 32;
    fmt.mBytesPerFrame = channels * sizeof(float);
    fmt.mFramesPerPacket = 1;
    fmt.mBytesPerPacket = fmt.mBytesPerFrame;

    if (AudioQueueNewOutput(&fmt, playbackCallback, impl, NULL, NULL, 0, &impl->queue) != noErr) {
        free(impl);
        return NULL;
    }

    UInt32 bufBytes = (UInt32)(sampleRate * channels * sizeof(float) / 10); // ~100 ms each
    for (int i = 0; i < FD_PLAYBACK_BUFFERS; i++) {
        AudioQueueAllocateBuffer(impl->queue, bufBytes, &impl->buffers[i]);
    }
    AudioQueueStart(impl->queue, NULL);
    return (FdAudioPlayback)impl;
}

void fd_audio_playback_enqueue(FdAudioPlayback h, const float *interleaved, int32_t samples) {
    FdPlaybackImpl *impl = (FdPlaybackImpl *)h;
    if (impl == NULL || interleaved == NULL || samples <= 0) {
        return;
    }
    // Wait for a free buffer (bounded), then find and fill it.
    if (dispatch_semaphore_wait(impl->free, dispatch_time(DISPATCH_TIME_NOW, 200 * NSEC_PER_MSEC)) != 0) {
        return; // playback is backed up; drop this frame rather than stall the caller
    }
    @synchronized(impl->lock) {
        for (int i = 0; i < FD_PLAYBACK_BUFFERS; i++) {
            AudioQueueBufferRef b = impl->buffers[i];
            UInt32 cap = b->mAudioDataBytesCapacity / sizeof(float);
            if (b->mAudioDataByteSize != 0) {
                continue; // in flight; the callback resets size to 0 when done
            }
            int32_t n = samples < (int32_t)cap ? samples : (int32_t)cap;
            memcpy(b->mAudioData, interleaved, (size_t)n * sizeof(float));
            b->mAudioDataByteSize = (UInt32)(n * sizeof(float));
            AudioQueueEnqueueBuffer(impl->queue, b, 0, NULL);
            return;
        }
    }
}

void fd_audio_playback_destroy(FdAudioPlayback h) {
    FdPlaybackImpl *impl = (FdPlaybackImpl *)h;
    if (impl == NULL) {
        return;
    }
    if (impl->queue) {
        AudioQueueStop(impl->queue, true);
        AudioQueueDispose(impl->queue, true);
    }
    if (impl->lock) {
        CFBridgingRelease((__bridge CFTypeRef)impl->lock);
    }
    free(impl);
}
