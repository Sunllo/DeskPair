// Machine identity (IOPlatformUUID) and small secrets (Keychain generic passwords). The identity key and
// password hashes the app keeps are what these store; the C# ISecretStore/IMachineIdProvider bind them.
#import <Foundation/Foundation.h>
#import <IOKit/IOKitLib.h>
#import <Security/Security.h>
#import "shim.h"
#include <stdlib.h>
#include <string.h>

static NSString *const kService = @"Sunllo DeskPair";

int32_t fd_machine_uuid(char *out, int32_t max)
{
    @autoreleasepool {
        io_service_t expert = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("IOPlatformExpertDevice"));
        if (expert == 0) {
            return 0;
        }
        CFTypeRef uuid = IORegistryEntryCreateCFProperty(expert, CFSTR(kIOPlatformUUIDKey), kCFAllocatorDefault, 0);
        IOObjectRelease(expert);
        if (uuid == NULL) {
            return 0;
        }
        NSString *s = (__bridge_transfer NSString *)uuid;
        const char *utf8 = [s UTF8String];
        if (utf8 == NULL || out == NULL) {
            return 0;
        }
        int32_t len = (int32_t)strlen(utf8);
        if (len >= max) {
            len = max - 1;
        }
        memcpy(out, utf8, len);
        out[len] = '\0';
        return len;
    }
}

static NSDictionary *queryFor(NSString *service, const char *account)
{
    return @{
        (id)kSecClass: (id)kSecClassGenericPassword,
        (id)kSecAttrService: service,
        (id)kSecAttrAccount: [NSString stringWithUTF8String:account],
    };
}

static NSDictionary *baseQuery(const char *account)
{
    return queryFor(kService, account);
}

int32_t fd_keychain_set(const char *account, const uint8_t *data, int32_t len)
{
    @autoreleasepool {
        if (account == NULL || data == NULL || len < 0) {
            return 0;
        }
        NSData *value = [NSData dataWithBytes:data length:(NSUInteger)len];
        NSMutableDictionary *q = [baseQuery(account) mutableCopy];

        // Update if present, otherwise add.
        NSDictionary *attrs = @{ (id)kSecValueData: value };
        OSStatus s = SecItemUpdate((__bridge CFDictionaryRef)q, (__bridge CFDictionaryRef)attrs);
        if (s == errSecItemNotFound) {
            NSMutableDictionary *add = [q mutableCopy];
            add[(id)kSecValueData] = value;
            add[(id)kSecAttrAccessible] = (id)kSecAttrAccessibleAfterFirstUnlock;
            s = SecItemAdd((__bridge CFDictionaryRef)add, NULL);
        }
        return s == errSecSuccess ? 1 : 0;
    }
}

// Returns the OSStatus rather than a yes/no.
//
// It used to answer 1 or 0, which folded "there is no such item" together with "there is one and this
// process may not have it" -- a denied keychain prompt, or an item whose ACL no longer lists this binary
// after a re-sign. The caller reads a 0 as "nothing stored here" and writes a replacement, and on a
// keychain that is exactly how a machine loses the identity it was reachable by. That happened on the
// Windows side of this, for the same reason, and cost a host its identity key, its password salt and its
// peer id. errSecSuccess is 0 and errSecItemNotFound is -25300, so the status tells them apart by itself.
int32_t fd_keychain_get(const char *account, uint8_t **out, int32_t *len)
{
    @autoreleasepool {
        if (account == NULL || out == NULL || len == NULL) {
            return errSecParam;
        }
        NSMutableDictionary *q = [baseQuery(account) mutableCopy];
        q[(id)kSecReturnData] = @YES;
        q[(id)kSecMatchLimit] = (id)kSecMatchLimitOne;

        CFTypeRef result = NULL;
        OSStatus s = SecItemCopyMatching((__bridge CFDictionaryRef)q, &result);
        if (s != errSecSuccess) {
            return (int32_t)s;
        }

        if (result == NULL) {
            return errSecItemNotFound;
        }
        NSData *data = (__bridge_transfer NSData *)result;
        int32_t n = (int32_t)data.length;
        uint8_t *copy = (uint8_t *)malloc(n > 0 ? n : 1);
        memcpy(copy, data.bytes, n);
        *out = copy;
        *len = n;
        return errSecSuccess;
    }
}

// Reads from a service this build does not own, for one purpose: carrying a machine's identity across a
// rename. The product was called FastDesk and kept its keychain items under "Sunllo FastDesk"; without a
// way to look there, upgrading silently produces a machine with a new key, a new id and a new password,
// and every device that had trusted the old one quietly stops recognising it. Same status convention as
// fd_keychain_get -- errSecItemNotFound is nothing there, anything else is something that will not come
// out, and those must not be confused.
int32_t fd_keychain_get_from(const char *service, const char *account, uint8_t **out, int32_t *len)
{
    @autoreleasepool {
        if (service == NULL || account == NULL || out == NULL || len == NULL) {
            return errSecParam;
        }
        NSMutableDictionary *q = [queryFor([NSString stringWithUTF8String:service], account) mutableCopy];
        q[(id)kSecReturnData] = @YES;
        q[(id)kSecMatchLimit] = (id)kSecMatchLimitOne;

        CFTypeRef result = NULL;
        OSStatus s = SecItemCopyMatching((__bridge CFDictionaryRef)q, &result);
        if (s != errSecSuccess) {
            return (int32_t)s;
        }
        if (result == NULL) {
            return errSecItemNotFound;
        }

        NSData *data = (__bridge_transfer NSData *)result;
        int32_t n = (int32_t)data.length;
        uint8_t *copy = (uint8_t *)malloc(n > 0 ? n : 1);
        memcpy(copy, data.bytes, n);
        *out = copy;
        *len = n;
        return errSecSuccess;
    }
}

int32_t fd_keychain_delete(const char *account)
{
    @autoreleasepool {
        if (account == NULL) {
            return 0;
        }
        OSStatus s = SecItemDelete((__bridge CFDictionaryRef)baseQuery(account));
        return (s == errSecSuccess || s == errSecItemNotFound) ? 1 : 0;
    }
}
