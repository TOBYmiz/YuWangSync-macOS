#import <Foundation/Foundation.h>
#import <Security/Security.h>
#include <stdlib.h>
#include <string.h>

static NSMutableDictionary *query(const char *key, BOOL legacy) {
    return [@{(__bridge id)kSecClass: (__bridge id)kSecClassGenericPassword,
              (__bridge id)kSecAttrService: legacy ? @"io.github.TOBYmiz.YuWangSync-macOS" : @"io.github.TOBYmiz.YuWangSync-macOS.credentials.v2",
              (__bridge id)kSecAttrAccount: [NSString stringWithUTF8String:key]} mutableCopy];
}
static OSStatus saveSecret(const char *key, NSData *data, BOOL legacy) {
    NSMutableDictionary *q = query(key, legacy);
    OSStatus status = SecItemUpdate((__bridge CFDictionaryRef)q, (__bridge CFDictionaryRef)@{(__bridge id)kSecValueData: data});
    if (status == errSecItemNotFound) {
        q[(__bridge id)kSecValueData] = data;
        status = SecItemAdd((__bridge CFDictionaryRef)q, NULL);
    }
    return status;
}
__attribute__((visibility("default"))) char *yw_secret_get(const char *key, int *status) {
    @autoreleasepool {
        NSMutableDictionary *q = query(key, NO);
        q[(__bridge id)kSecReturnData] = @YES;
        q[(__bridge id)kSecMatchLimit] = (__bridge id)kSecMatchLimitOne;
        CFTypeRef result = NULL;
        *status = (int)SecItemCopyMatching((__bridge CFDictionaryRef)q, &result);
        BOOL migrate = *status == errSecItemNotFound;
        if (migrate) {
            q[(__bridge id)kSecAttrService] = @"io.github.TOBYmiz.YuWangSync-macOS";
            *status = (int)SecItemCopyMatching((__bridge CFDictionaryRef)q, &result);
        }
        if (*status != errSecSuccess) return NULL;
        NSData *data = CFBridgingRelease(result);
        // Authorized legacy reads become items owned by the stable signed program.
        // Keep the old item intact; never rewrite or broaden its access controls.
        if (migrate) {
            NSMutableDictionary *destination = query(key, NO);
            destination[(__bridge id)kSecValueData] = data;
            *status = (int)SecItemAdd((__bridge CFDictionaryRef)destination, NULL);
            // A concurrent login may already have saved a newer value. Do not overwrite it.
            if (*status == errSecDuplicateItem) *status = errSecSuccess;
            if (*status != errSecSuccess) return NULL;
        }
        char *text = calloc(data.length + 1, 1);
        if (!text) { *status = -1; return NULL; }
        memcpy(text, data.bytes, data.length);
        return text;
    }
}
__attribute__((visibility("default"))) int yw_secret_set(const char *key, const char *value) {
    @autoreleasepool {
        NSData *data = [[NSString stringWithUTF8String:value] dataUsingEncoding:NSUTF8StringEncoding];
        return (int)saveSecret(key, data, NO);
    }
}
__attribute__((visibility("default"))) int yw_secret_delete(const char *key) {
    @autoreleasepool {
        OSStatus status = SecItemDelete((__bridge CFDictionaryRef)query(key, NO));
        if (status != errSecSuccess && status != errSecItemNotFound) return (int)status;
        OSStatus legacy = SecItemDelete((__bridge CFDictionaryRef)query(key, YES));
        return legacy == errSecItemNotFound ? (int)status : (int)legacy;
    }
}
// Synthetic keys only: lets CI prove old-service migration without touching account credentials.
__attribute__((visibility("default"))) int yw_secret_legacy_probe(const char *key, const char *value) {
    @autoreleasepool {
        if (strncmp(key, "signing-selftest:", 17) != 0) return (int)errSecParam;
        if (value) return (int)saveSecret(key, [[NSString stringWithUTF8String:value] dataUsingEncoding:NSUTF8StringEncoding], YES);
        OSStatus status = SecItemDelete((__bridge CFDictionaryRef)query(key, YES));
        return status == errSecItemNotFound ? (int)errSecSuccess : (int)status;
    }
}
__attribute__((visibility("default"))) void yw_free(void *pointer) { free(pointer); }
// Used by the cross-process CI probe: an unexpected authorization must fail, not hang on a dialog.
__attribute__((visibility("default"))) int yw_keychain_disable_ui(void) {
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
    return (int)SecKeychainSetUserInteractionAllowed(false);
#pragma clang diagnostic pop
}
__attribute__((visibility("default"))) int yw_snapshot_copy(const char *source, const char *destination) {
    @autoreleasepool {
        NSURL *from = [NSURL fileURLWithPath:[NSString stringWithUTF8String:source]];
        NSURL *to = [NSURL fileURLWithPath:[NSString stringWithUTF8String:destination]];
        NSFileCoordinator *coordinator = [[NSFileCoordinator alloc] initWithFilePresenter:nil];
        NSError *coordinationError = nil;
        __block NSError *copyError = nil;
        __block BOOL copied = NO;
        [coordinator coordinateReadingItemAtURL:from options:NSFileCoordinatorReadingWithoutChanges error:&coordinationError byAccessor:^(NSURL *url) {
            copied = [[NSFileManager defaultManager] copyItemAtURL:url toURL:to error:&copyError];
        }];
        if (coordinationError) return (int)coordinationError.code ?: -1;
        if (!copied) return (int)copyError.code ?: -2;
        return 0;
    }
}
