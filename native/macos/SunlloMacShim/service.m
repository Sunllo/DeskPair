// Permission (TCC) preflight and a little identity, for the onboarding and hosting layers. Screen Recording
// and Accessibility are the two consents a remote-desktop host needs on macOS; they cannot be granted
// programmatically, only checked and prompted for, which is exactly what the permission onboarding surfaces.
#import <Foundation/Foundation.h>
#import <CoreGraphics/CoreGraphics.h>
#import <ApplicationServices/ApplicationServices.h>
#import <SystemConfiguration/SystemConfiguration.h>
#import "shim.h"
#include <unistd.h>
#include <string.h>

int32_t fd_tcc_screen_granted(void)
{
    return CGPreflightScreenCaptureAccess() ? 1 : 0;
}

int32_t fd_tcc_screen_request(void)
{
    return CGRequestScreenCaptureAccess() ? 1 : 0;
}

int32_t fd_tcc_accessibility_granted(void)
{
    return AXIsProcessTrusted() ? 1 : 0;
}

int32_t fd_tcc_accessibility_request(void)
{
    NSDictionary *options = @{ (__bridge id)kAXTrustedCheckOptionPrompt: @YES };
    return AXIsProcessTrustedWithOptions((__bridge CFDictionaryRef)options) ? 1 : 0;
}

int32_t fd_is_root(void)
{
    return geteuid() == 0 ? 1 : 0;
}

int32_t fd_console_user(char *nameOut, int32_t max, uint32_t *uidOut)
{
    @autoreleasepool {
        uid_t uid = 0;
        gid_t gid = 0;
        CFStringRef name = SCDynamicStoreCopyConsoleUser(NULL, &uid, &gid);
        if (name == NULL) {
            return 0;
        }
        NSString *s = (__bridge_transfer NSString *)name;
        const char *utf8 = [s UTF8String];
        if (utf8 && nameOut && max > 0) {
            int32_t len = (int32_t)strlen(utf8);
            if (len >= max) {
                len = max - 1;
            }
            memcpy(nameOut, utf8, len);
            nameOut[len] = '\0';
        }
        if (uidOut) {
            *uidOut = uid;
        }
        return 1;
    }
}
