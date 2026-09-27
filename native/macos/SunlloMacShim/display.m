// Display enumeration through CoreGraphics. No permission is needed to list displays; capture (which does
// need Screen Recording consent) is a separate concern handled in capture.m.
#import <CoreGraphics/CoreGraphics.h>
#import "shim.h"
#include <math.h>

int32_t fd_displays_get(FdDisplay *out, int32_t max)
{
    uint32_t count = 0;
    if (CGGetActiveDisplayList(0, NULL, &count) != kCGErrorSuccess || count == 0) {
        return 0;
    }

    CGDirectDisplayID ids[32];
    if (count > 32) {
        count = 32;
    }
    if (CGGetActiveDisplayList(count, ids, &count) != kCGErrorSuccess) {
        return 0;
    }

    if (out == NULL) {
        return (int32_t)count;
    }

    int32_t written = 0;
    for (uint32_t i = 0; i < count && written < max; i++) {
        CGDirectDisplayID did = ids[i];
        CGRect bounds = CGDisplayBounds(did);                 // in points
        size_t pw = CGDisplayPixelsWide(did);                 // backing pixel width
        size_t ph = CGDisplayPixelsHigh(did);
        double scale = bounds.size.width > 0 ? (double)pw / bounds.size.width : 1.0;

        out[written].id = did;
        out[written].x = (int32_t)bounds.origin.x;
        out[written].y = (int32_t)bounds.origin.y;
        out[written].width = (int32_t)pw;
        out[written].height = (int32_t)ph;
        out[written].scale = scale;
        out[written].isPrimary = CGDisplayIsMain(did) ? 1 : 0;
        written++;
    }

    return written;
}

// ---- Display modes ------------------------------------------------------------------------------------
//
// CoreGraphics lists a Retina display's modes twice: once at the panel's pixels and once "HiDPI", at half
// the points with the same pixels. Both are offered, told apart by scale, and the HiDPI one is what a
// person expects when they pick "1920x1080" on a 4K MacBook -- the same trick TeamViewer uses.

static double modeScale(CGDisplayModeRef mode) {
    size_t pw = CGDisplayModeGetPixelWidth(mode);
    size_t w = CGDisplayModeGetWidth(mode);
    return (w > 0 && pw > 0) ? (double)pw / (double)w : 1.0;
}

static CFArrayRef copyDesktopModes(CGDirectDisplayID did) {
    CFStringRef keys[] = { kCGDisplayShowDuplicateLowResolutionModes };
    CFBooleanRef values[] = { kCFBooleanTrue };
    CFDictionaryRef options = CFDictionaryCreate(kCFAllocatorDefault, (const void **)keys, (const void **)values, 1,
                                                 &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    CFArrayRef modes = CGDisplayCopyAllDisplayModes(did, options);
    CFRelease(options);
    return modes;
}

int32_t fd_display_modes_get(uint32_t displayId, FdDisplayMode *out, int32_t max)
{
    CFArrayRef modes = copyDesktopModes((CGDirectDisplayID)displayId);
    if (modes == NULL) {
        return 0;
    }

    CFIndex count = CFArrayGetCount(modes);
    FdDisplayMode found[256];
    int32_t n = 0;
    for (CFIndex i = 0; i < count && n < 256; i++) {
        CGDisplayModeRef mode = (CGDisplayModeRef)CFArrayGetValueAtIndex(modes, i);
        if (!CGDisplayModeIsUsableForDesktopGUI(mode)) {
            continue;
        }
        FdDisplayMode m;
        m.width = (int32_t)CGDisplayModeGetPixelWidth(mode);
        m.height = (int32_t)CGDisplayModeGetPixelHeight(mode);
        m.scale = modeScale(mode);
        if (m.width < 640 || m.height < 480) {
            continue;
        }
        int dup = 0;
        for (int32_t j = 0; j < n; j++) {
            if (found[j].width == m.width && found[j].height == m.height && fabs(found[j].scale - m.scale) < 0.01) {
                dup = 1;
                break;
            }
        }
        if (!dup) {
            found[n++] = m;
        }
    }
    CFRelease(modes);

    // Largest first; at equal pixels the HiDPI variant first, since that is the one people mean.
    for (int32_t i = 1; i < n; i++) {
        FdDisplayMode key = found[i];
        int32_t j = i - 1;
        while (j >= 0) {
            long a = (long)found[j].width * found[j].height;
            long b = (long)key.width * key.height;
            if (a > b || (a == b && found[j].scale >= key.scale)) {
                break;
            }
            found[j + 1] = found[j];
            j--;
        }
        found[j + 1] = key;
    }

    if (out == NULL) {
        return n;
    }
    int32_t written = n < max ? n : max;
    for (int32_t i = 0; i < written; i++) {
        out[i] = found[i];
    }
    return written;
}

int32_t fd_display_mode_set(uint32_t displayId, int32_t width, int32_t height, double scale)
{
    CGDirectDisplayID did = (CGDirectDisplayID)displayId;
    CFArrayRef modes = copyDesktopModes(did);
    if (modes == NULL) {
        return -1;
    }

    CGDisplayModeRef best = NULL;
    CFIndex count = CFArrayGetCount(modes);
    for (CFIndex i = 0; i < count; i++) {
        CGDisplayModeRef mode = (CGDisplayModeRef)CFArrayGetValueAtIndex(modes, i);
        if (!CGDisplayModeIsUsableForDesktopGUI(mode)) {
            continue;
        }
        if ((int32_t)CGDisplayModeGetPixelWidth(mode) != width || (int32_t)CGDisplayModeGetPixelHeight(mode) != height) {
            continue;
        }
        if (scale > 0 && fabs(modeScale(mode) - scale) >= 0.01) {
            continue;
        }
        // Same size: prefer the higher refresh rate.
        if (best == NULL || CGDisplayModeGetRefreshRate(mode) > CGDisplayModeGetRefreshRate(best)) {
            best = mode;
        }
    }

    int32_t result;
    if (best == NULL) {
        result = -1;
    } else {
        CGDisplayConfigRef config = NULL;
        CGError err = CGBeginDisplayConfiguration(&config);
        if (err == kCGErrorSuccess) {
            err = CGConfigureDisplayWithDisplayMode(config, did, best, NULL);
            if (err == kCGErrorSuccess) {
                // For this login session only: a host that dies mid-session is back to normal at the next
                // login, and nothing this program did is written into the person's settings.
                err = CGCompleteDisplayConfiguration(config, kCGConfigureForSession);
            } else {
                CGCancelDisplayConfiguration(config);
            }
        }
        result = (int32_t)err;
    }
    CFRelease(modes);
    return result;
}
