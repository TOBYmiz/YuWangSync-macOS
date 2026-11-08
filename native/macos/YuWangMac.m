#import <Foundation/Foundation.h>
#import <Security/Security.h>
#include <stdlib.h>
#include <string.h>

static NSMutableDictionary *query(const char *key) {
    return [@{(__bridge id)kSecClass: (__bridge id)kSecClassGenericPassword,
              (__bridge id)kSecAttrService: @"io.github.TOBYmiz.YuWangSync-macOS",
              (__bridge id)kSecAttrAccount: [NSString stringWithUTF8String:key]} mutableCopy];
}
__attribute__((visibility("default"))) char *yw_secret_get(const char *key, int *status) {
    @autoreleasepool {
        NSMutableDictionary *q = query(key);
        q[(__bridge id)kSecReturnData] = @YES;
        q[(__bridge id)kSecMatchLimit] = (__bridge id)kSecMatchLimitOne;
        CFTypeRef result = NULL;
        *status = (int)SecItemCopyMatching((__bridge CFDictionaryRef)q, &result);
        if (*status != errSecSuccess) return NULL;
        NSData *data = CFBridgingRelease(result);
        char *text = calloc(data.length + 1, 1);
        if (!text) { *status = -1; return NULL; }
        memcpy(text, data.bytes, data.length);
        return text;
    }
}
__attribute__((visibility("default"))) int yw_secret_set(const char *key, const char *value) {
    @autoreleasepool {
        NSMutableDictionary *q = query(key);
        NSData *data = [[NSString stringWithUTF8String:value] dataUsingEncoding:NSUTF8StringEncoding];
        NSDictionary *attributes = @{(__bridge id)kSecValueData: data};
        OSStatus status = SecItemUpdate((__bridge CFDictionaryRef)q, (__bridge CFDictionaryRef)attributes);
        if (status == errSecItemNotFound) {
            q[(__bridge id)kSecValueData] = data;
            status = SecItemAdd((__bridge CFDictionaryRef)q, NULL);
        }
        return (int)status;
    }
}
__attribute__((visibility("default"))) int yw_secret_delete(const char *key) {
    @autoreleasepool { return (int)SecItemDelete((__bridge CFDictionaryRef)query(key)); }
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
