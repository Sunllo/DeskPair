// Clipboard file promises: offering files this machine does not have yet.
//
// macOS does not deliver these through NSFilePromiseProvider. On the general pasteboard that class publishes
// its metadata but never com.apple.pasteboard.promised-file-url, so the delegate is never called and a reader
// times out — measured, with both processes bundled and signed. It is drag-and-drop machinery.
//
// What works is a lazy pasteboard provider: one NSPasteboardItem per file, each with a data provider for
// public.file-url. When something pastes, AppKit calls provideDataForType: in this process, once per item;
// we hand the request to C#, wait for it to stage the file, and answer with its URL. The pasteboard then
// also advertises NSFilenamesPboardType and the Apple URL type, which is what Finder reads.
//
// Two consequences worth stating plainly. The callback must return the data, so this is lazy per paste and
// not streaming — the pasting application waits for the whole file, as it does on X11. And the callback
// arrives on the main thread, which is the UI thread, so the wait runs a nested run loop rather than
// blocking: the application keeps drawing while the file is fetched. That is the counterpart of the pumping
// wait the Windows data object needs for the same reason.
//
// No function pointers cross the ABI. C# pulls requests with a timeout, like every other async capability in
// this shim.
#import <AppKit/AppKit.h>
#import "shim.h"
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

@interface FdPromiseOwner : NSObject <NSPasteboardItemDataProvider>
@property (nonatomic, strong) NSArray<NSString *> *names;
@end

static NSObject *g_lock = nil;
static NSMutableArray<NSNumber *> *g_queued = nil;                      // request ids, in arrival order
static NSMutableDictionary<NSNumber *, NSNumber *> *g_requestIndex = nil;
static NSMutableDictionary<NSNumber *, NSString *> *g_answers = nil;
static NSMutableSet<NSNumber *> *g_answered = nil;
static int64_t g_nextRequestId = 1;
static BOOL g_shutdown = NO;

// The pasteboard does not retain a data provider, so this file does.
static FdPromiseOwner *g_owner = nil;

static void fd_promise_init_once(void)
{
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        g_lock = [NSObject new];
        g_queued = [NSMutableArray array];
        g_requestIndex = [NSMutableDictionary dictionary];
        g_answers = [NSMutableDictionary dictionary];
        g_answered = [NSMutableSet set];
    });
}

@implementation FdPromiseOwner

- (void)pasteboard:(NSPasteboard *)pasteboard
              item:(NSPasteboardItem *)item
provideDataForType:(NSPasteboardType)type
{
    NSInteger index = [pasteboard.pasteboardItems indexOfObject:item];
    if (index == NSNotFound || (NSUInteger)index >= self.names.count) {
        return;
    }

    int64_t requestId;
    @synchronized (g_lock) {
        if (g_shutdown) {
            return;
        }
        requestId = g_nextRequestId++;
        g_requestIndex[@(requestId)] = @((int)index);
        [g_queued addObject:@(requestId)];
    }

    // Long enough for a large file on a slow link, short enough that a dead session does not wedge the
    // pasting application for ever. Answering with nothing fails that one file, not the whole paste.
    NSDate *deadline = [NSDate dateWithTimeIntervalSinceNow:600];
    NSString *staged = nil;
    BOOL finished = NO;

    while (!finished) {
        @synchronized (g_lock) {
            if ([g_answered containsObject:@(requestId)]) {
                staged = g_answers[@(requestId)];
                [g_answers removeObjectForKey:@(requestId)];
                [g_answered removeObject:@(requestId)];
                finished = YES;
            } else if (g_shutdown) {
                finished = YES;
            }
        }

        if (finished) {
            break;
        }

        if ([deadline timeIntervalSinceNow] <= 0) {
            break;
        }

        [[NSRunLoop currentRunLoop] runMode:NSDefaultRunLoopMode
                                 beforeDate:[NSDate dateWithTimeIntervalSinceNow:0.05]];
    }

    if (staged != nil) {
        [item setString:[NSURL fileURLWithPath:staged].absoluteString forType:type];
    }
}

@end

int32_t fd_clipboard_read_file_paths(char **out)
{
    @autoreleasepool {
        if (out == NULL) {
            return 0;
        }
        *out = NULL;

        NSArray<NSURL *> *urls = [[NSPasteboard generalPasteboard]
            readObjectsForClasses:@[[NSURL class]]
                          options:@{NSPasteboardURLReadingFileURLsOnlyKey: @YES}];
        if (urls.count == 0) {
            return 0;
        }

        NSMutableData *blob = [NSMutableData data];
        int32_t count = 0;
        for (NSURL *url in urls) {
            const char *utf8 = url.path.UTF8String;
            if (utf8 == NULL) {
                continue;
            }
            [blob appendBytes:utf8 length:strlen(utf8) + 1];
            count++;
        }

        if (count == 0) {
            return 0;
        }

        char terminator = 0;
        [blob appendBytes:&terminator length:1];
        char *copy = (char *)malloc(blob.length);
        memcpy(copy, blob.bytes, blob.length);
        *out = copy;
        return count;
    }
}

/// Splits the NUL-separated block C# passes in; nil if it does not hold exactly `count` names.
static NSArray<NSString *> *fd_split_names(const char *names, int32_t count)
{
    NSMutableArray<NSString *> *result = [NSMutableArray arrayWithCapacity:(NSUInteger)count];
    const char *p = names;
    for (int32_t i = 0; i < count && p != NULL && *p != 0; i++) {
        NSString *s = [NSString stringWithUTF8String:p];
        if (s == nil) {
            return nil;
        }
        [result addObject:s];
        p += strlen(p) + 1;
    }

    return result.count == (NSUInteger)count ? result : nil;
}

int32_t fd_clipboard_write_file_promises(const char *names, int32_t count, const char *text_or_null)
{
    @autoreleasepool {
        fd_promise_init_once();
        if (names == NULL || count <= 0) {
            return -1;
        }

        NSArray<NSString *> *list = fd_split_names(names, count);
        if (list == nil) {
            return -1;
        }

        @synchronized (g_lock) {
            g_shutdown = NO;
            [g_queued removeAllObjects];
            [g_requestIndex removeAllObjects];
            [g_answers removeAllObjects];
            [g_answered removeAllObjects];
        }

        FdPromiseOwner *owner = [FdPromiseOwner new];
        owner.names = list;
        g_owner = owner;

        NSMutableArray *items = [NSMutableArray arrayWithCapacity:(NSUInteger)count + 1];
        for (int32_t i = 0; i < count; i++) {
            NSPasteboardItem *item = [NSPasteboardItem new];
            [item setDataProvider:owner forTypes:@[NSPasteboardTypeFileURL]];
            [items addObject:item];
        }

        // The text goes out in the same writeObjects:. Setting it afterwards would land in the first item
        // and corrupt that file's promise.
        if (text_or_null != NULL) {
            NSString *text = [NSString stringWithUTF8String:text_or_null];
            if (text != nil) {
                NSPasteboardItem *textItem = [NSPasteboardItem new];
                [textItem setString:text forType:NSPasteboardTypeString];
                [items addObject:textItem];
            }
        }

        NSPasteboard *pb = [NSPasteboard generalPasteboard];
        [pb clearContents];
        return [pb writeObjects:items] ? 0 : -1;
    }
}

int32_t fd_promise_next(int32_t timeout_ms, int64_t *request_id)
{
    @autoreleasepool {
        fd_promise_init_once();
        if (request_id == NULL) {
            return -1;
        }

        NSDate *deadline = [NSDate dateWithTimeIntervalSinceNow:timeout_ms / 1000.0];
        while (YES) {
            @synchronized (g_lock) {
                if (g_queued.count > 0) {
                    NSNumber *identifier = g_queued.firstObject;
                    [g_queued removeObjectAtIndex:0];
                    NSNumber *index = g_requestIndex[identifier];
                    [g_requestIndex removeObjectForKey:identifier];
                    *request_id = identifier.longLongValue;
                    return index.intValue;
                }

            }

            // Nothing to report, including while shut down: returning early there would have the caller
            // ask again immediately and spin a core for as long as nothing is on the clipboard.
            if ([deadline timeIntervalSinceNow] <= 0) {
                return -1;
            }

            usleep(10 * 1000);
        }
    }
}

void fd_promise_complete(int64_t request_id, const char *staged_path)
{
    @autoreleasepool {
        fd_promise_init_once();
        NSString *path = staged_path == NULL ? nil : [NSString stringWithUTF8String:staged_path];
        @synchronized (g_lock) {
            if (path != nil) {
                g_answers[@(request_id)] = path;
            }

            [g_answered addObject:@(request_id)];
        }
    }
}

void fd_promise_shutdown(void)
{
    @autoreleasepool {
        fd_promise_init_once();
        @synchronized (g_lock) {
            g_shutdown = YES;
            [g_queued removeAllObjects];
            [g_requestIndex removeAllObjects];
            [g_answers removeAllObjects];
            [g_answered removeAllObjects];
        }

        g_owner = nil;
    }
}
