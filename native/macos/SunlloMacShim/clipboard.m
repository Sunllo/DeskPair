// The general pasteboard: text and images. Change detection is the pasteboard's own changeCount, which the
// C# side polls; there is no push notification for pasteboard changes on macOS.
#import <AppKit/AppKit.h>
#import "shim.h"
#include <stdlib.h>
#include <string.h>

void fd_free(void *p)
{
    free(p);
}

int64_t fd_clipboard_change_count(void)
{
    @autoreleasepool {
        return (int64_t)[[NSPasteboard generalPasteboard] changeCount];
    }
}

char *fd_clipboard_read_text(void)
{
    @autoreleasepool {
        NSString *s = [[NSPasteboard generalPasteboard] stringForType:NSPasteboardTypeString];
        if (s == nil) {
            return NULL;
        }
        const char *utf8 = [s UTF8String];
        if (utf8 == NULL) {
            return NULL;
        }
        size_t len = strlen(utf8) + 1;
        char *copy = (char *)malloc(len);
        memcpy(copy, utf8, len);
        return copy;
    }
}

void fd_clipboard_write_text(const char *utf8)
{
    @autoreleasepool {
        if (utf8 == NULL) {
            return;
        }
        NSString *s = [NSString stringWithUTF8String:utf8];
        if (s == nil) {
            return;
        }
        NSPasteboard *pb = [NSPasteboard generalPasteboard];
        [pb clearContents];
        [pb setString:s forType:NSPasteboardTypeString];
    }
}

// ---- Images ---------------------------------------------------------------------------------------------

// The best image representation on the pasteboard, as PNG.
//
// PNG is asked for first so the common case costs nothing: if an app already put PNG there, those bytes go
// out untouched. TIFF is the fallback because a great many Mac apps — Preview, screenshots, most of AppKit
// — publish only TIFF.
static NSData *fd_png_from_pasteboard(NSPasteboard *pb)
{
    NSPasteboardType type = [pb availableTypeFromArray:@[NSPasteboardTypePNG, NSPasteboardTypeTIFF]];
    if (type == nil) {
        return nil;
    }

    NSData *data = [pb dataForType:type];
    if (data == nil) {
        return nil;
    }

    if ([type isEqualToString:NSPasteboardTypePNG]) {
        return data;
    }

    // imageRepsWithData:, not imageRepWithData:. A Retina screenshot's TIFF carries both a 1x and a 2x
    // representation, and the singular form hands back whichever comes first — which may be the small one.
    NSArray<NSImageRep *> *reps = [NSBitmapImageRep imageRepsWithData:data];
    NSBitmapImageRep *best = nil;
    NSInteger bestPixels = 0;
    for (NSImageRep *rep in reps) {
        if (![rep isKindOfClass:[NSBitmapImageRep class]]) {
            continue;
        }
        NSInteger pixels = rep.pixelsWide * rep.pixelsHigh;
        if (pixels > bestPixels) {
            bestPixels = pixels;
            best = (NSBitmapImageRep *)rep;
        }
    }

    if (best == nil) {
        return nil;
    }

    NSData *png = [best representationUsingType:NSBitmapImageFileTypePNG properties:@{}];
    if (png != nil) {
        return png;
    }

    // Some layouts — planar, 16-bit float, CMYK — cannot be re-encoded directly. Draw into a plain 8-bit
    // sRGB buffer and try again, rather than returning nothing for a picture that is plainly there.
    NSBitmapImageRep *flat = [[NSBitmapImageRep alloc]
        initWithBitmapDataPlanes:NULL
                      pixelsWide:best.pixelsWide
                      pixelsHigh:best.pixelsHigh
                   bitsPerSample:8
                 samplesPerPixel:4
                        hasAlpha:YES
                        isPlanar:NO
                  colorSpaceName:NSDeviceRGBColorSpace
                     bytesPerRow:0
                    bitsPerPixel:0];
    if (flat == nil) {
        return nil;
    }

    NSGraphicsContext *context = [NSGraphicsContext graphicsContextWithBitmapImageRep:flat];
    if (context == nil) {
        return nil;
    }

    [NSGraphicsContext saveGraphicsState];
    [NSGraphicsContext setCurrentContext:context];
    [best drawInRect:NSMakeRect(0, 0, best.pixelsWide, best.pixelsHigh)];
    [NSGraphicsContext restoreGraphicsState];

    return [flat representationUsingType:NSBitmapImageFileTypePNG properties:@{}];
}

int32_t fd_clipboard_has_image(void)
{
    @autoreleasepool {
        NSPasteboard *pb = [NSPasteboard generalPasteboard];
        return [pb availableTypeFromArray:@[NSPasteboardTypePNG, NSPasteboardTypeTIFF]] != nil ? 1 : 0;
    }
}

int32_t fd_clipboard_read_png(uint8_t **out, int32_t *len)
{
    @autoreleasepool {
        if (out == NULL || len == NULL) {
            return 0;
        }

        *out = NULL;
        *len = 0;

        NSData *png = fd_png_from_pasteboard([NSPasteboard generalPasteboard]);
        if (png == nil || png.length == 0) {
            return 0;
        }

        uint8_t *copy = (uint8_t *)malloc(png.length);
        if (copy == NULL) {
            return 0;
        }

        memcpy(copy, png.bytes, png.length);
        *out = copy;
        *len = (int32_t)png.length;
        return 1;
    }
}

int32_t fd_clipboard_write_image(const uint8_t *png, int32_t len, const char *text_or_null)
{
    @autoreleasepool {
        if (png == NULL || len <= 0) {
            return 0;
        }

        NSData *pngData = [NSData dataWithBytes:png length:(NSUInteger)len];

        // TIFF as well as PNG, because the app the user pastes into may read only TIFF. If the rendering
        // fails there is still a perfectly good PNG to offer, so this is not a reason to fail the write.
        NSData *tiff = nil;
        NSBitmapImageRep *rep = [NSBitmapImageRep imageRepWithData:pngData];
        if (rep != nil) {
            tiff = [rep TIFFRepresentation];
        }

        NSString *text = nil;
        if (text_or_null != NULL) {
            text = [NSString stringWithUTF8String:text_or_null];
        }

        NSMutableArray<NSPasteboardType> *types = [NSMutableArray arrayWithObject:NSPasteboardTypePNG];
        if (tiff != nil) {
            [types addObject:NSPasteboardTypeTIFF];
        }
        if (text != nil) {
            [types addObject:NSPasteboardTypeString];
        }

        // One declareTypes: for everything. It clears the pasteboard itself, and declaring a second time
        // would throw away what the first one put there.
        NSPasteboard *pb = [NSPasteboard generalPasteboard];
        [pb declareTypes:types owner:nil];

        BOOL ok = [pb setData:pngData forType:NSPasteboardTypePNG];
        if (tiff != nil) {
            [pb setData:tiff forType:NSPasteboardTypeTIFF];
        }
        if (text != nil) {
            [pb setString:text forType:NSPasteboardTypeString];
        }

        return ok ? 1 : 0;
    }
}
