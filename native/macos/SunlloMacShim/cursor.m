// The system-wide pointer image, so the viewer can draw the host's cursor itself instead of the host
// baking it into the video. A baked-in cursor can only move when a new frame arrives, so it visibly trails
// the viewer's own pointer and the two overlap; sent as a shape, the viewer's pointer simply becomes the
// remote one and moves at the mouse's rate rather than the frame rate.
#import <AppKit/AppKit.h>
#import <dlfcn.h>
#import "shim.h"

// Changes whenever the system cursor does. Private SPI, so it is resolved by name and its absence is not
// fatal: the caller falls back to hashing the image, which costs more but keeps working if Apple drops it.
uint64_t fd_cursor_seed(void)
{
    static int32_t (*seed)(void);
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        seed = (int32_t (*)(void))dlsym(RTLD_DEFAULT, "CGSCurrentCursorSeed");
    });

    if (seed == NULL) {
        return 0;
    }

    // Reserve 0 for "unavailable"; a real seed of 0 is indistinguishable from it and only costs one
    // redundant fetch.
    uint64_t value = (uint64_t)(uint32_t)seed();
    return value == 0 ? 1 : value;
}

// Fills width/height/hotX/hotY, and the pixels when bgra is non-NULL and large enough. Returns 1 on success.
// Call once with bgra == NULL to learn the size, then again with a buffer.
//
// Sizes are in points, matching fd_cursor_position and the capture geometry. On a Retina display the image
// has a larger pixel representation; drawing it into a point-sized buffer downsamples it, which is the right
// trade while the capture itself is in points.
int32_t fd_cursor_image(int32_t *width, int32_t *height, int32_t *hotX, int32_t *hotY, uint8_t *bgra, int32_t bgraLen)
{
    if (width == NULL || height == NULL || hotX == NULL || hotY == NULL) {
        return 0;
    }

    @autoreleasepool {
        NSCursor *cursor = [NSCursor currentSystemCursor];
        if (cursor == nil) {
            return 0;
        }

        NSImage *image = [cursor image];
        if (image == nil) {
            return 0;
        }

        NSSize size = [image size];
        int32_t w = (int32_t)llround(size.width);
        int32_t h = (int32_t)llround(size.height);
        if (w <= 0 || h <= 0) {
            return 0;
        }

        NSPoint hot = [cursor hotSpot];
        *width = w;
        *height = h;
        *hotX = (int32_t)llround(hot.x);
        *hotY = (int32_t)llround(hot.y);
        if (bgra == NULL) {
            return 1;
        }

        if (bgraLen < w * h * 4) {
            return 0;
        }

        memset(bgra, 0, (size_t)(w * h * 4));
        CGColorSpaceRef space = CGColorSpaceCreateDeviceRGB();
        // Premultiplied-first plus little-endian 32-bit is BGRA in memory, which is what the pipeline wants.
        CGContextRef ctx = CGBitmapContextCreate(bgra, (size_t)w, (size_t)h, 8, (size_t)(w * 4), space,
                                                 kCGImageAlphaPremultipliedFirst | kCGBitmapByteOrder32Little);
        CGColorSpaceRelease(space);
        if (ctx == NULL) {
            return 0;
        }

        NSRect rect = NSMakeRect(0, 0, w, h);
        CGImageRef cg = [image CGImageForProposedRect:&rect context:nil hints:nil];
        if (cg != NULL) {
            CGContextDrawImage(ctx, CGRectMake(0, 0, w, h), cg);
        }

        CGContextRelease(ctx);
        return cg != NULL ? 1 : 0;
    }
}
