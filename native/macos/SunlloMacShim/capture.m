// Screen and system-audio capture through ScreenCaptureKit: one stream per display, reference-counted.
//
// Each display the host streams has its own SCStream, queue, newest frame and refcount, keyed by its
// CGDirectDisplayID. There used to be one stream per process, built on content.displays[0] whatever display
// was asked for -- so on a Mac the second display showed the first -- and two capturers sharing it would
// have taken each other's frames and unlocked each other's pixel buffers.
//
// Audio rides on the main display's stream. macOS delivers cleanest when one SCStream carries both outputs;
// a separate audio-only stream on the same display went silent, and audio on every display's stream would
// deliver the same system sound once per display. So the audio capturer holds a reference to the main
// display's stream (creating it if no video capturer has), and only that stream captures audio.
//
// The delegate keeps the newest video frame (retaining the pixel buffer, releasing the old) and appends audio
// into a ring; the C# side waits on a condition for a changed frame and drains the ring. One video capturer per
// display is assumed -- the host runs one stream per display -- since the frame hand-out state is the stream's.
// Capture needs Screen Recording (TCC) consent; without it startCapture reports an error and create returns NULL.
#import <Foundation/Foundation.h>
#import <ScreenCaptureKit/ScreenCaptureKit.h>
#import <CoreVideo/CoreVideo.h>
#import <CoreMedia/CoreMedia.h>
#import "shim.h"

#define FD_AUDIO_RING (48000 * 2 * 2) // ~2 s of 48 kHz stereo float

API_AVAILABLE(macos(13.0))
@interface FdCaptureImpl : NSObject <SCStreamOutput, SCStreamDelegate>
@property(nonatomic, strong) SCStream *stream;
@property(nonatomic, assign) uint32_t displayId;
@property(nonatomic, assign) int32_t refcount;
@property(nonatomic, assign) int32_t width;
@property(nonatomic, assign) int32_t height;
@property(nonatomic, assign) int32_t showCursor;
@property(nonatomic, assign) BOOL audio;                 // this stream carries the system audio
// video
@property(nonatomic, strong) NSCondition *cond;
@property(nonatomic, assign) CVPixelBufferRef latest;   // retained; the newest frame not yet taken
@property(nonatomic, assign) CVPixelBufferRef locked;   // base-address-locked during a copy
@property(nonatomic, assign) uint64_t seq;              // bumped on each delivered frame
@property(nonatomic, assign) uint64_t taken;           // seq of the last frame handed out
// audio ring
@property(nonatomic, assign) float *ring;
@property(nonatomic, assign) int32_t head;
@property(nonatomic, assign) int32_t tail;
@end

@implementation FdCaptureImpl

- (instancetype)init {
    if ((self = [super init])) {
        _cond = [[NSCondition alloc] init];
        _latest = NULL;
        _locked = NULL;
        _seq = 0;
        _taken = 0;
        _ring = (float *)calloc(FD_AUDIO_RING, sizeof(float));
        _head = 0;
        _tail = 0;
    }
    return self;
}

- (void)dealloc {
    free(_ring);
}

- (void)stream:(SCStream *)stream didOutputSampleBuffer:(CMSampleBufferRef)sampleBuffer ofType:(SCStreamOutputType)type {
    if (type == SCStreamOutputTypeScreen) {
        CVImageBufferRef img = CMSampleBufferGetImageBuffer(sampleBuffer);
        if (img == NULL) {
            return;
        }
        CVPixelBufferRef pb = (CVPixelBufferRef)img;
        CVPixelBufferRetain(pb);
        [_cond lock];
        if (_latest) {
            CVPixelBufferRelease(_latest);
        }
        _latest = pb;
        _seq++;
        [_cond signal];
        [_cond unlock];
    } else if (type == SCStreamOutputTypeAudio) {
        // ScreenCaptureKit delivers stereo audio non-interleaved: two mono buffers, one per channel. The
        // AudioBufferList must therefore have room for more than one buffer or the fetch fails, which is why a
        // bare `AudioBufferList` (one inline buffer) returned nothing. Over-allocate for up to 8 channels.
        char ablStorage[sizeof(AudioBufferList) + 7 * sizeof(AudioBuffer)];
        AudioBufferList *abl = (AudioBufferList *)ablStorage;
        CMBlockBufferRef block = NULL;
        if (CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(sampleBuffer, NULL, abl, sizeof(ablStorage), NULL, NULL, 0, &block) != noErr) {
            return;
        }

        [_cond lock];
        if (abl->mNumberBuffers >= 2) {
            // Planar: interleave the first two channels into L,R,L,R for the pipeline.
            const float *l = (const float *)abl->mBuffers[0].mData;
            const float *r = (const float *)abl->mBuffers[1].mData;
            int32_t frames = (int32_t)(abl->mBuffers[0].mDataByteSize / sizeof(float));
            for (int32_t i = 0; i < frames; i++) {
                _ring[_head] = l[i]; _head = (_head + 1) % FD_AUDIO_RING; if (_head == _tail) _tail = (_tail + 1) % FD_AUDIO_RING;
                _ring[_head] = r[i]; _head = (_head + 1) % FD_AUDIO_RING; if (_head == _tail) _tail = (_tail + 1) % FD_AUDIO_RING;
            }
        } else if (abl->mNumberBuffers == 1) {
            // Already interleaved (or mono): copy straight through.
            const float *src = (const float *)abl->mBuffers[0].mData;
            int32_t n = (int32_t)(abl->mBuffers[0].mDataByteSize / sizeof(float));
            for (int32_t i = 0; i < n; i++) {
                _ring[_head] = src[i]; _head = (_head + 1) % FD_AUDIO_RING; if (_head == _tail) _tail = (_tail + 1) % FD_AUDIO_RING;
            }
        }
        [_cond signal];
        [_cond unlock];
        if (block) {
            CFRelease(block);
        }
    }
}

- (void)stream:(SCStream *)stream didStopWithError:(NSError *)error {
    // The stream ended (display removed, permission revoked); acquires and reads time out from here on.
}

@end

// The streams, one per display, keyed by CGDirectDisplayID.
static NSMutableDictionary<NSNumber *, FdCaptureImpl *> *gStreams API_AVAILABLE(macos(13.0)) = nil;
static NSObject *gLock = nil;

// Made once, on first use: two capturers starting at the same moment used to be able to make two.
static NSObject *streamsLock(void) {
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        gLock = [[NSObject alloc] init];
    });
    return gLock;
}

API_AVAILABLE(macos(13.0))
static SCStreamConfiguration *configFor(SCDisplay *display, int32_t width, int32_t height, int32_t showCursor, BOOL audio) {
    SCStreamConfiguration *config = [[SCStreamConfiguration alloc] init];
    config.width = width > 0 ? width : display.width;
    config.height = height > 0 ? height : display.height;
    config.pixelFormat = kCVPixelFormatType_32BGRA;
    config.showsCursor = showCursor ? YES : NO;
    config.minimumFrameInterval = CMTimeMake(1, 60);
    config.queueDepth = 4;
    config.capturesAudio = audio;
    if (audio) {
        config.sampleRate = 48000;
        config.channelCount = 2;
        config.excludesCurrentProcessAudio = YES;
    }
    return config;
}

API_AVAILABLE(macos(13.0))
static SCDisplay *findDisplay(uint32_t displayId) {
    __block SCShareableContent *content = nil;
    dispatch_semaphore_t sem = dispatch_semaphore_create(0);
    [SCShareableContent getShareableContentWithCompletionHandler:^(SCShareableContent *c, NSError *e) {
        content = c;
        dispatch_semaphore_signal(sem);
    }];
    dispatch_semaphore_wait(sem, dispatch_time(DISPATCH_TIME_NOW, 5 * NSEC_PER_SEC));
    for (SCDisplay *d in content.displays) {
        if (d.displayID == displayId) {
            return d;
        }
    }
    return nil;
}

API_AVAILABLE(macos(13.0))
static FdCaptureImpl *createStream(uint32_t displayId, int32_t width, int32_t height, int32_t showCursor) {
    SCDisplay *target = findDisplay(displayId);
    if (target == nil) {
        return nil; // not a display any more (or no consent to list them)
    }

    BOOL audio = displayId == CGMainDisplayID();
    SCContentFilter *filter = [[SCContentFilter alloc] initWithDisplay:target excludingWindows:@[]];
    SCStreamConfiguration *config = configFor(target, width, height, showCursor, audio);

    FdCaptureImpl *impl = [[FdCaptureImpl alloc] init];
    impl.displayId = displayId;
    impl.width = (int32_t)config.width;
    impl.height = (int32_t)config.height;
    impl.showCursor = showCursor;
    impl.audio = audio;
    impl.stream = [[SCStream alloc] initWithFilter:filter configuration:config delegate:impl];

    NSString *name = [NSString stringWithFormat:@"com.sunllo.deskpair.capture.%u", displayId];
    dispatch_queue_t vq = dispatch_queue_create(name.UTF8String, DISPATCH_QUEUE_SERIAL);
    NSError *err = nil;
    if (![impl.stream addStreamOutput:impl type:SCStreamOutputTypeScreen sampleHandlerQueue:vq error:&err]) {
        return nil;
    }
    if (audio) {
        // Best-effort: a machine with no audio device still captures video.
        dispatch_queue_t aq = dispatch_queue_create("com.sunllo.deskpair.audio", DISPATCH_QUEUE_SERIAL);
        [impl.stream addStreamOutput:impl type:SCStreamOutputTypeAudio sampleHandlerQueue:aq error:&err];
    }

    __block BOOL started = NO;
    dispatch_semaphore_t startSem = dispatch_semaphore_create(0);
    [impl.stream startCaptureWithCompletionHandler:^(NSError *e) {
        started = (e == nil);
        dispatch_semaphore_signal(startSem);
    }];
    dispatch_semaphore_wait(startSem, dispatch_time(DISPATCH_TIME_NOW, 5 * NSEC_PER_SEC));
    return started ? impl : nil;
}

// Returns a retained handle to a display's stream, creating it on first use; 0 means the main display.
// Returns NULL if it cannot start.
API_AVAILABLE(macos(13.0))
static FdCapture acquireStream(uint32_t displayId, int32_t width, int32_t height, int32_t showCursor) {
    if (displayId == 0) {
        displayId = CGMainDisplayID();
    }
    @synchronized(streamsLock()) {
        if (gStreams == nil) {
            gStreams = [NSMutableDictionary dictionary];
        }
        FdCaptureImpl *impl = gStreams[@(displayId)];
        if (impl == nil) {
            impl = createStream(displayId, width, height, showCursor);
            if (impl == nil) {
                return NULL;
            }
            gStreams[@(displayId)] = impl;
        } else if (width > 0 && height > 0 && (width != impl.width || height != impl.height || showCursor != impl.showCursor)) {
            // The stream was sized at creation and may be shared with the audio capturer, so a display whose
            // mode changed would go on being scaled to the old size -- a squashed picture. Reconfigure the
            // stream that exists instead of tearing it down under whoever else holds it.
            SCDisplay *target = findDisplay(displayId);
            if (target != nil) {
                SCStreamConfiguration *config = configFor(target, width, height, showCursor, impl.audio);
                dispatch_semaphore_t sem = dispatch_semaphore_create(0);
                [impl.stream updateConfiguration:config completionHandler:^(NSError *e) {
                    dispatch_semaphore_signal(sem);
                }];
                dispatch_semaphore_wait(sem, dispatch_time(DISPATCH_TIME_NOW, 3 * NSEC_PER_SEC));
                impl.width = width;
                impl.height = height;
                impl.showCursor = showCursor;
            }
        }
        impl.refcount++;
        return (FdCapture)CFBridgingRetain(impl);
    }
}

static void releaseStream(FdCapture h) API_AVAILABLE(macos(13.0)) {
    if (h == NULL) {
        return;
    }
    @synchronized(streamsLock()) {
        FdCaptureImpl *impl = (FdCaptureImpl *)CFBridgingRelease(h); // balance the acquire retain
        impl.refcount--;
        if (impl.refcount <= 0) {
            [impl.stream stopCaptureWithCompletionHandler:^(NSError *e) {}];
            [impl.cond lock];
            if (impl.locked) {
                CVPixelBufferUnlockBaseAddress(impl.locked, kCVPixelBufferLock_ReadOnly);
                CVPixelBufferRelease(impl.locked);
                impl.locked = NULL;
            }
            if (impl.latest) {
                CVPixelBufferRelease(impl.latest);
                impl.latest = NULL;
            }
            [impl.cond unlock];
            [gStreams removeObjectForKey:@(impl.displayId)];
        }
    }
}

FdCapture fd_capture_create(uint32_t displayId, int32_t width, int32_t height, int32_t showCursor) {
    if (@available(macOS 13.0, *)) {
        return acquireStream(displayId, width, height, showCursor);
    }
    return NULL;
}

int32_t fd_capture_acquire(FdCapture h, int32_t timeoutMs, FdFrame *out) {
    if (@available(macOS 13.0, *)) {
        FdCaptureImpl *impl = (__bridge FdCaptureImpl *)h;
        if (impl == nil || out == NULL) {
            return -1;
        }

        [impl.cond lock];
        if (impl.seq == impl.taken) {
            [impl.cond waitUntilDate:[NSDate dateWithTimeIntervalSinceNow:timeoutMs / 1000.0]];
        }
        if (impl.seq == impl.taken || impl.latest == NULL) {
            [impl.cond unlock];
            return 0; // timed out; the screen did not change
        }

        CVPixelBufferRef pb = impl.latest;
        CVPixelBufferRetain(pb);
        impl.taken = impl.seq;
        [impl.cond unlock];

        CVPixelBufferLockBaseAddress(pb, kCVPixelBufferLock_ReadOnly);
        impl.locked = pb;
        out->width = (int32_t)CVPixelBufferGetWidth(pb);
        out->height = (int32_t)CVPixelBufferGetHeight(pb);
        out->stride = (int32_t)CVPixelBufferGetBytesPerRow(pb);
        out->data = (const uint8_t *)CVPixelBufferGetBaseAddress(pb);
        return 1;
    }
    return -1;
}

void fd_capture_release(FdCapture h) {
    if (@available(macOS 13.0, *)) {
        FdCaptureImpl *impl = (__bridge FdCaptureImpl *)h;
        if (impl == nil || impl.locked == NULL) {
            return;
        }
        CVPixelBufferUnlockBaseAddress(impl.locked, kCVPixelBufferLock_ReadOnly);
        CVPixelBufferRelease(impl.locked);
        impl.locked = NULL;
    }
}

void fd_capture_destroy(FdCapture h) {
    if (@available(macOS 13.0, *)) {
        releaseStream(h);
    }
}

// ---- Audio capture: attaches to the main display's stream ----

FdAudioCapture fd_audio_capture_create(void) {
    if (@available(macOS 13.0, *)) {
        // Display 0 is the main display; 0 size lets its stream keep (or pick) the display's own dimensions.
        return (FdAudioCapture)acquireStream(0, 0, 0, 1);
    }
    return NULL;
}

int32_t fd_audio_capture_read(FdAudioCapture h, float *out, int32_t maxSamples, int32_t timeoutMs) {
    if (@available(macOS 13.0, *)) {
        FdCaptureImpl *impl = (__bridge FdCaptureImpl *)h;
        if (impl == nil || out == NULL) {
            return 0;
        }
        [impl.cond lock];
        if (impl.head == impl.tail) {
            [impl.cond waitUntilDate:[NSDate dateWithTimeIntervalSinceNow:timeoutMs / 1000.0]];
        }
        int32_t n = 0;
        while (n < maxSamples && impl.tail != impl.head) {
            out[n++] = impl.ring[impl.tail];
            impl.tail = (impl.tail + 1) % FD_AUDIO_RING;
        }
        [impl.cond unlock];
        return n;
    }
    return 0;
}

void fd_audio_capture_destroy(FdAudioCapture h) {
    if (@available(macOS 13.0, *)) {
        releaseStream((FdCapture)h);
    }
}
