// Asking for administrator rights with this application's name on the dialog.
//
// The obvious way -- osascript with "do shell script ... with administrator privileges" -- works, and puts
// the wrong name on the prompt. macOS attributes an authorisation dialog to the process that raised it, and
// that process is /usr/bin/osascript, so what a person sees is a password box from a scripting tool they
// have never heard of, on behalf of a program that does remote access. That is exactly the shape of
// something a careful person should refuse, and asking them to ignore the instinct is the wrong trade.
//
// Raising it here instead makes the dialog name DeskPair, show its icon, and carry a sentence saying what
// the rights are for.
//
// AuthorizationExecuteWithPrivileges has been deprecated since 10.7. The blessed replacement is a helper
// installed with SMJobBless, which needs the app and the helper signed with a Developer ID naming each
// other's designated requirements -- a certificate this product does not have yet. So this is what there
// is, it is still present and working on macOS 26, and the caller falls back to osascript if it ever stops
// being either.

#import <Foundation/Foundation.h>
#import <Security/Authorization.h>
#import <Security/AuthorizationTags.h>
#import "shim.h"

#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"

// Runs `tool` (an absolute path) as root, after asking with a dialog this application is named in.
//
// argv is NULL-terminated and does NOT include the tool itself, matching the API being wrapped. `prompt`
// is the sentence shown above the password field; NULL for the system's own wording.
//
// Whatever the tool writes to its standard output is copied into `out`, because that is the only channel
// back: the child is started by the security server, so its exit status is not ours to collect. The caller
// decides what success looks like by what it reads there.
//
// Returns 0 when the tool was started, errAuthorizationCanceled (-60006) when the person said no, and the
// OSStatus otherwise.
int32_t fd_authorize_run(const char *tool, const char *const *argv, const char *prompt, char *out, int32_t outMax)
{
    @autoreleasepool {
        if (tool == NULL) {
            return errAuthorizationInternal;
        }

        AuthorizationRef auth = NULL;
        OSStatus status = AuthorizationCreate(NULL, kAuthorizationEmptyEnvironment, kAuthorizationFlagDefaults, &auth);
        if (status != errAuthorizationSuccess) {
            return (int32_t)status;
        }

        AuthorizationItem right = { kAuthorizationRightExecute, 0, NULL, 0 };
        AuthorizationRights rights = { 1, &right };

        // The sentence on the dialog. Without it macOS writes its own, which says only that the
        // application wants to make changes -- true, and no help to somebody deciding whether to allow it.
        AuthorizationItem promptItem = { kAuthorizationEnvironmentPrompt, 0, NULL, 0 };
        AuthorizationEnvironment environment = { 0, NULL };
        if (prompt != NULL) {
            promptItem.valueLength = strlen(prompt);
            promptItem.value = (void *)prompt;
            environment.count = 1;
            environment.items = &promptItem;
        }

        AuthorizationFlags flags = kAuthorizationFlagDefaults
            | kAuthorizationFlagInteractionAllowed
            | kAuthorizationFlagPreAuthorize
            | kAuthorizationFlagExtendRights;

        status = AuthorizationCopyRights(auth, &rights, &environment, flags, NULL);
        if (status != errAuthorizationSuccess) {
            AuthorizationFree(auth, kAuthorizationFlagDefaults);
            return (int32_t)status;
        }

        FILE *pipe = NULL;
        status = AuthorizationExecuteWithPrivileges(auth, tool, kAuthorizationFlagDefaults, (char *const *)argv, &pipe);
        if (status != errAuthorizationSuccess) {
            AuthorizationFree(auth, kAuthorizationFlagDefaults);
            return (int32_t)status;
        }

        // Read to the end, which is also how this waits: the pipe closes when the tool exits. Reading it
        // is not optional -- a tool whose output nobody drains stops when the pipe fills, and a stopped
        // tool never finishes.
        int32_t written = 0;
        if (pipe != NULL) {
            int c;
            while ((c = fgetc(pipe)) != EOF) {
                if (out != NULL && written < outMax - 1) {
                    out[written++] = (char)c;
                }
            }

            fclose(pipe);
        }

        if (out != NULL && outMax > 0) {
            out[written] = '\0';
        }

        AuthorizationFree(auth, kAuthorizationFlagDefaults);
        return 0;
    }
}

#pragma clang diagnostic pop
