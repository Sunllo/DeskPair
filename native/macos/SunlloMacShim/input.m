// Input injection through CoreGraphics events. Posting events requires the process to hold Accessibility
// (TCC) consent; without it the calls are silently dropped by the window server, which is the same "granted
// or nothing happens" model the C# side surfaces through IPlatformInfo.
#import <CoreGraphics/CoreGraphics.h>
#import <math.h>
#import "shim.h"

typedef struct {
    CGEventSourceRef source;
    double lastX;
    double lastY;
} FdInputImpl;

FdInput fd_input_create(void)
{
    FdInputImpl *impl = (FdInputImpl *)calloc(1, sizeof(FdInputImpl));
    impl->source = CGEventSourceCreate(kCGEventSourceStateHIDSystemState);
    return impl;
}

void fd_input_destroy(FdInput h)
{
    FdInputImpl *impl = (FdInputImpl *)h;
    if (impl == NULL) {
        return;
    }
    if (impl->source) {
        CFRelease(impl->source);
    }
    free(impl);
}

static void post(FdInputImpl *impl, CGEventRef ev, uint64_t flags)
{
    if (ev == NULL) {
        return;
    }
    CGEventSetFlags(ev, (CGEventFlags)flags);
    CGEventPost(kCGHIDEventTap, ev);
    CFRelease(ev);
}

// Movement while a button is held is a *drag*, and macOS says so with a different event type: an app that
// tracks a drag listens for mouseDragged:, never mouseMoved:. Posting mouseMoved with the button down leaves
// the press registered but the motion unreported, so a window follows the pointer in lurches instead of
// smoothly. Windows and X11 have no such distinction, which is why only a Mac host showed it.
//
// held: -1 none, 0 left, 1 right, 2 other.
void fd_input_mouse_move(FdInput h, double x, double y, uint64_t flags, int32_t held)
{
    FdInputImpl *impl = (FdInputImpl *)h;
    CGEventType type;
    CGMouseButton button;
    switch (held) {
        case 0:  type = kCGEventLeftMouseDragged;  button = kCGMouseButtonLeft;   break;
        case 1:  type = kCGEventRightMouseDragged; button = kCGMouseButtonRight;  break;
        case 2:  type = kCGEventOtherMouseDragged; button = kCGMouseButtonCenter; break;
        default: type = kCGEventMouseMoved;        button = kCGMouseButtonLeft;   break;
    }

    CGEventRef ev = CGEventCreateMouseEvent(impl->source, type, CGPointMake(x, y), button);
    if (ev != NULL) {
        // Absolute positions alone leave the deltas at zero, and anything that steers by relative motion
        // (sliders, canvases, 3D views) then sees a drag that never moves.
        CGEventSetIntegerValueField(ev, kCGMouseEventDeltaX, (int64_t)llround(x - impl->lastX));
        CGEventSetIntegerValueField(ev, kCGMouseEventDeltaY, (int64_t)llround(y - impl->lastY));
    }

    impl->lastX = x;
    impl->lastY = y;
    post(impl, ev, flags);
}

void fd_input_mouse_button(FdInput h, int32_t button, int32_t down, double x, double y, uint64_t flags)
{
    FdInputImpl *impl = (FdInputImpl *)h;
    CGEventType type;
    CGMouseButton b;
    switch (button) {
        case 1: type = down ? kCGEventRightMouseDown : kCGEventRightMouseUp; b = kCGMouseButtonRight; break;
        case 2: type = down ? kCGEventOtherMouseDown : kCGEventOtherMouseUp; b = kCGMouseButtonCenter; break;
        default: type = down ? kCGEventLeftMouseDown : kCGEventLeftMouseUp; b = kCGMouseButtonLeft; break;
    }
    impl->lastX = x;
    impl->lastY = y;
    CGEventRef ev = CGEventCreateMouseEvent(impl->source, type, CGPointMake(x, y), b);
    post(impl, ev, flags);
}

void fd_input_scroll(FdInput h, int32_t dx, int32_t dy)
{
    FdInputImpl *impl = (FdInputImpl *)h;
    // Pixel units, vertical then horizontal in the same event; sign follows the platform convention.
    CGEventRef ev = CGEventCreateScrollWheelEvent(impl->source, kCGScrollEventUnitPixel, 2, dy, dx);
    post(impl, ev, 0);
}

void fd_input_key(FdInput h, uint16_t keycode, int32_t down, uint64_t flags)
{
    FdInputImpl *impl = (FdInputImpl *)h;
    CGEventRef ev = CGEventCreateKeyboardEvent(impl->source, (CGKeyCode)keycode, down ? true : false);
    post(impl, ev, flags);
}

void fd_input_text(FdInput h, const uint16_t *utf16, int32_t len)
{
    FdInputImpl *impl = (FdInputImpl *)h;
    if (utf16 == NULL || len <= 0) {
        return;
    }
    // A down+up pair carrying the string; the keycode is irrelevant when a unicode string is attached.
    CGEventRef down = CGEventCreateKeyboardEvent(impl->source, 0, true);
    CGEventKeyboardSetUnicodeString(down, (UniCharCount)len, (const UniChar *)utf16);
    CGEventPost(kCGHIDEventTap, down);
    CFRelease(down);

    CGEventRef up = CGEventCreateKeyboardEvent(impl->source, 0, false);
    CGEventKeyboardSetUnicodeString(up, (UniCharCount)len, (const UniChar *)utf16);
    CGEventPost(kCGHIDEventTap, up);
    CFRelease(up);
}

int32_t fd_cursor_position(double *x, double *y)
{
    CGEventRef ev = CGEventCreate(NULL);
    if (ev == NULL) {
        return 0;
    }
    CGPoint p = CGEventGetLocation(ev);
    CFRelease(ev);
    if (x) { *x = p.x; }
    if (y) { *y = p.y; }
    return 1;
}
