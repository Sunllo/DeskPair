// Sunllo DeskPair Wayland shim: one PipeWire video stream, read from C# through a narrow, pull-shaped C surface.
//
// PipeWire's format negotiation is built from SPA pods, and every pod builder and parser is a static inline
// function in a header -- there is no symbol to P/Invoke. So the stream lives here, on PipeWire's own thread, and
// C# only ever asks: is there a new picture (dp_pw_wait), give it to me (dp_pw_lock_frame), I am done with it
// (dp_pw_release_frame). C# never runs on PipeWire's thread and PipeWire never calls into C#.
//
// Every struct here is mirrored in C# (DeskPair.Platform.Linux/Native/WaylandShim.cs); PortalShimLayoutTests
// recomputes the offsets from this file, and dp_pw_abi() lets the loader refuse a library built from another one.
#pragma once

#include <stdint.h>

#define DP_EXPORT __attribute__((visibility("default")))

// Bump when any declaration below changes shape or meaning.
#define DP_PW_ABI 1

// Pixel byte order of a frame (alpha, if any, is not meaningful) or of the cursor bitmap (alpha is).
#define DP_FORMAT_NONE 0
#define DP_FORMAT_BGRX 1
#define DP_FORMAT_RGBX 2
#define DP_FORMAT_BGRA 3
#define DP_FORMAT_RGBA 4

// dp_pw_state: how the stream stands. The negative ones are final.
#define DP_STATE_CONNECTING 0
#define DP_STATE_PAUSED 1
#define DP_STATE_STREAMING 2
#define DP_STATE_ERROR -1
#define DP_STATE_CLOSED -2

// dp_frame.flags: the pixels stay mapped until the next dp_pw_lock_frame or dp_pw_release_frame, however long that
// is, so the caller may hand them on without copying. Without it, copy them out and release at once.
#define DP_FRAME_BORROWABLE 1

typedef struct dp_frame {
    const uint8_t *data;   // first visible pixel
    int32_t width;         // pixels
    int32_t height;
    int32_t stride;        // bytes from one row to the next; rarely width * 4
    int32_t format;        // DP_FORMAT_BGRX or DP_FORMAT_RGBX
    uint64_t sequence;     // pictures received so far, this one included
    int64_t pts_ns;        // the producer's timestamp, 0 when it gave none
    int32_t buffers;       // how many buffers the stream negotiated
    int32_t flags;         // DP_FRAME_*
    int64_t readable;      // bytes that may be read from data onwards (at least stride * (height - 1) + width * 4)
} dp_frame;

typedef struct dp_cursor {
    int32_t visible;       // 0 when the pointer is off this stream or hidden
    int32_t x;             // hotspot position in stream pixels
    int32_t y;
    int32_t hot_x;         // hotspot within the bitmap
    int32_t hot_y;
    int32_t width;         // bitmap size; 0 until the first shape arrives
    int32_t height;
    int32_t format;        // DP_FORMAT_BGRA or DP_FORMAT_RGBA
    uint64_t position_serial;  // bumps whenever visible, x or y changes
    uint64_t shape_serial;     // bumps whenever the bitmap changes
} dp_cursor;

typedef struct dp_pw dp_pw;

// DP_PW_ABI, with the sizes of the two structs as this library was compiled.
DP_EXPORT int dp_pw_abi(int32_t *frame_size, int32_t *cursor_size);

// Connects to the PipeWire remote on fd (as the portal's OpenPipeWireRemote gave it; duplicated here, the caller
// keeps its own) and starts a stream from node. 0 and *out on success; otherwise a negative errno and a sentence
// in error.
DP_EXPORT int dp_pw_open(int fd, uint32_t node, dp_pw **out, char *error, int32_t error_len);

// Waits up to timeout_ms for a picture not yet locked: 1 there is one, 0 there is not, -1 the stream is over
// (dp_pw_state says why).
DP_EXPORT int dp_pw_wait(dp_pw *pw, int32_t timeout_ms);

// Takes the newest picture, giving back the one locked before it. 1 and *out filled; 0 nothing new.
DP_EXPORT int dp_pw_lock_frame(dp_pw *pw, dp_frame *out);

// Gives back the locked picture, if any. Its pixels must not be read after this.
DP_EXPORT void dp_pw_release_frame(dp_pw *pw);

// The pointer as the stream last described it. The bitmap (width * height * 4 bytes, tight) is copied to bitmap
// when capacity allows. Returns 0.
DP_EXPORT int dp_pw_cursor(dp_pw *pw, dp_cursor *out, uint8_t *bitmap, int32_t capacity);

// DP_STATE_*, with the reason for an error in error.
DP_EXPORT int dp_pw_state(dp_pw *pw, char *error, int32_t error_len);

// Stops the stream and frees everything, the locked picture included.
DP_EXPORT void dp_pw_close(dp_pw *pw);
