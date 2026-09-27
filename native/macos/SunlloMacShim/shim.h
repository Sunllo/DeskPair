// C ABI surface of the Sunllo DeskPair macOS shim. The policy stays in C#; this exposes the AppKit and
// CoreGraphics / ScreenCaptureKit / VideoToolbox primitives that are awkward or unsafe to call through raw
// objc_msgSend from .NET (blocks, delegates, CMSampleBuffer, arm64 struct returns). Everything is plain C
// so C#'s LibraryImport can bind it directly.
#ifndef SUNLLO_MAC_SHIM_H
#define SUNLLO_MAC_SHIM_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// ---- Memory --------------------------------------------------------------------------------------------

// Frees a buffer the shim returned (malloc'd strings and blobs).
void fd_free(void *p);

// ---- Clipboard (NSPasteboard) --------------------------------------------------------------------------

// The general pasteboard's change count; a change means someone wrote to it.
int64_t fd_clipboard_change_count(void);
// The current text, malloc'd UTF-8 (free with fd_free), or NULL when there is no text.
char *fd_clipboard_read_text(void);
void fd_clipboard_write_text(const char *utf8);

// 1 when the pasteboard holds an image, without copying any of it. Cheap enough to ask on every change,
// and — unlike reading the bytes — it does not raise Sonoma's "wants to paste from" alert.
int32_t fd_clipboard_has_image(void);
// The current image as PNG, malloc'd (free with fd_free). 1 on success, 0 when there is no image or it
// could not be re-encoded. TIFF is converted here: most Mac apps put only TIFF on the pasteboard, and
// NSBitmapImageRep is Cocoa work that has no business on the C# side.
int32_t fd_clipboard_read_png(uint8_t **out, int32_t *len);
// Replaces the pasteboard with this PNG, its TIFF rendering for apps that read only TIFF, and optionally
// text. All in one declareTypes: — a second declaration would clear the first.
int32_t fd_clipboard_write_image(const uint8_t *png, int32_t len, const char *text_or_null);

// ---- Clipboard file promises ---------------------------------------------------------------------------
//
// macOS delivers promised files through a lazy pasteboard provider, not NSFilePromiseProvider: on the general
// pasteboard that class never publishes com.apple.pasteboard.promised-file-url and its delegate is never
// called. One NSPasteboardItem per file, each with a data provider for public.file-url, is what works, and it
// is the only shape that carries more than one file.
//
// No function pointers cross the ABI, matching the rest of the shim: C# pulls requests with a timeout.

// The absolute paths of the files currently on the pasteboard, as NUL-separated UTF-8 in one malloc'd block
// (free with fd_free). Returns the number of paths; *out is NULL when there are none.
int32_t fd_clipboard_read_file_paths(char **out);

// Publishes `count` promised files and, optionally, text — in ONE writeObjects:, because a later
// setString:forType: writes into the first item and corrupts the promise. `names` is NUL-separated UTF-8,
// one relative name per file, in the order C# will be asked for them. Returns 0 on success.
int32_t fd_clipboard_write_file_promises(const char *names, int32_t count, const char *text_or_null);

// Waits up to timeout_ms for something to paste. Returns the index of the file being asked for, or -1 if
// nothing was asked in that time. *request_id identifies the request for fd_promise_complete.
int32_t fd_promise_next(int32_t timeout_ms, int64_t *request_id);

// Answers a request with the local file to hand over, or NULL to fail it. The paste is blocked until this
// is called, so it must be called for every request fd_promise_next returns.
void fd_promise_complete(int64_t request_id, const char *staged_path);

// Fails every outstanding request and stops offering. Called when the promise leaves the clipboard.
void fd_promise_shutdown(void);

// ---- Machine identity and secrets ----------------------------------------------------------------------

// The hardware UUID (IOPlatformUUID) as a string; returns the length written, 0 on failure.
int32_t fd_machine_uuid(char *out, int32_t max);

// Keychain generic-password items under a fixed service. Return 1 on success, 0 otherwise.
int32_t fd_keychain_set(const char *account, const uint8_t *data, int32_t len);
// Returns an OSStatus: errSecSuccess (0) with *out malloc'd and freed with fd_free, errSecItemNotFound
// (-25300) when there is none, and anything else when there is one this process may not read.
int32_t fd_keychain_get(const char *account, uint8_t **out, int32_t *len);
int32_t fd_keychain_delete(const char *account);
// ---- Administrator rights ------------------------------------------------------------------------------

// Runs `tool` as root after asking, with a dialog this application is named in rather than osascript.
// argv is NULL-terminated and excludes the tool. `prompt` is the sentence above the password field.
// The tool's standard output is copied into `out`. Returns 0 when it ran, -60006 when the person said no.
int32_t fd_authorize_run(const char *tool, const char *const *argv, const char *prompt, char *out, int32_t outMax);

// Reads an item stored under a different service name. Only for carrying an identity across the rename
// from FastDesk; see security.m.
int32_t fd_keychain_get_from(const char *service, const char *account, uint8_t **out, int32_t *len);

// ---- Permissions (TCC) and identity ---------------------------------------------------------------------

int32_t fd_tcc_screen_granted(void);          // 1 if Screen Recording is allowed
int32_t fd_tcc_screen_request(void);          // prompts; returns the result
int32_t fd_tcc_accessibility_granted(void);   // 1 if Accessibility is allowed
int32_t fd_tcc_accessibility_request(void);   // prompts (shows the system dialog)
int32_t fd_is_root(void);                     // effective uid == 0
// The console (GUI) user's name and uid; returns 1 on success.
int32_t fd_console_user(char *nameOut, int32_t max, uint32_t *uidOut);

// ---- Displays ------------------------------------------------------------------------------------------

typedef struct {
    uint32_t id;        // CGDirectDisplayID
    int32_t  x;
    int32_t  y;
    int32_t  width;     // pixel width (backing), not points
    int32_t  height;
    double   scale;     // backing scale factor (2.0 on Retina)
    int32_t  isPrimary;
} FdDisplay;

// Fills up to `max` displays, returns the count written (or the total if out is NULL).
int32_t fd_displays_get(FdDisplay *out, int32_t max);

// A mode a display can be set to: pixels as captured, and the backing scale (2.0 for a HiDPI mode).
typedef struct {
    int32_t width;
    int32_t height;
    double  scale;
} FdDisplayMode;

// Fills up to `max` modes usable for the desktop, largest first, deduplicated on (width, height, scale);
// returns the count written (or the total if out is NULL). 0 when the display is unknown.
int32_t fd_display_modes_get(uint32_t displayId, FdDisplayMode *out, int32_t max);

// Switches the display to the mode with that pixel size and scale for this login session (it reverts
// at logout). Returns 0 on success, otherwise the CGError, or -1 when no such mode exists.
int32_t fd_display_mode_set(uint32_t displayId, int32_t width, int32_t height, double scale);

// ---- Input (CGEvent) -----------------------------------------------------------------------------------

typedef void *FdInput;

FdInput fd_input_create(void);
void    fd_input_destroy(FdInput h);

// Absolute move in global display pixels.
// held: -1 none, 0 left, 1 right, 2 other. A held button makes this a drag, not a move.
void fd_input_mouse_move(FdInput h, double x, double y, uint64_t flags, int32_t held);
// button: 0 left, 1 right, 2 other; down: 1 press, 0 release.
void fd_input_mouse_button(FdInput h, int32_t button, int32_t down, double x, double y, uint64_t flags);
void fd_input_scroll(FdInput h, int32_t dx, int32_t dy);
// keycode is a macOS virtual key code (kVK_*). down: 1 press, 0 release. flags is the current modifier mask.
void fd_input_key(FdInput h, uint16_t keycode, int32_t down, uint64_t flags);
// Types arbitrary text by attaching a UTF-16 string to a synthetic key event (no layout mapping needed).
void fd_input_text(FdInput h, const uint16_t *utf16, int32_t len);

// ---- Cursor --------------------------------------------------------------------------------------------

// Global cursor position in display pixels. Returns 1 on success.
int32_t fd_cursor_position(double *x, double *y);

// The system pointer's shape, so the viewer draws it instead of the host baking it into the video.
// fd_cursor_seed returns 0 when the change-detection SPI is unavailable; hash the image instead.
uint64_t fd_cursor_seed(void);
int32_t fd_cursor_image(int32_t *width, int32_t *height, int32_t *hotX, int32_t *hotY, uint8_t *bgra, int32_t bgraLen);

// ---- Screen capture (ScreenCaptureKit) -----------------------------------------------------------------

typedef struct {
    int32_t width;
    int32_t height;
    int32_t stride;         // bytes per row
    const uint8_t *data;    // BGRA, valid until fd_capture_release or the next acquire
} FdFrame;

typedef void *FdCapture;

// Creates and starts a capture stream for a display at the given backing-pixel size. showCursor composites
// the real pointer into the frame. Returns NULL if ScreenCaptureKit could not start (no consent, bad id).
FdCapture fd_capture_create(uint32_t displayId, int32_t width, int32_t height, int32_t showCursor);

// Waits up to timeoutMs for a new frame. Returns 1 and fills `out` on a frame, 0 on timeout, -1 on error.
// The pixel data is locked until fd_capture_release is called, so the caller copies then releases.
int32_t fd_capture_acquire(FdCapture h, int32_t timeoutMs, FdFrame *out);

// Releases the lock taken by a successful fd_capture_acquire.
void fd_capture_release(FdCapture h);

void fd_capture_destroy(FdCapture h);

// ---- Video encode (VideoToolbox) -----------------------------------------------------------------------

typedef struct {
    const uint8_t *data;    // Annex B, valid until the next encode or fd_encoder_release
    int32_t length;
    int32_t isKeyframe;
} FdPacket;

typedef void *FdEncoder;

// codec: 0 = H.264, 1 = HEVC. Returns NULL when the hardware encoder could not be created.
FdEncoder fd_encoder_create(int32_t width, int32_t height, int32_t fps, int32_t bitrateKbps, int32_t codec);
// Encodes one BGRA frame. Returns 1 and fills `out` when a packet was produced, 0 when not, -1 on error.
int32_t fd_encoder_encode(FdEncoder h, const uint8_t *bgra, int32_t stride, int64_t ptsTicks, int32_t forceKeyframe, FdPacket *out);
void fd_encoder_release(FdEncoder h);            // frees the buffer returned by the last encode
void fd_encoder_set_bitrate(FdEncoder h, int32_t bitrateKbps);
int32_t fd_encoder_is_hardware(FdEncoder h);
void fd_encoder_destroy(FdEncoder h);

// ---- Video decode (VideoToolbox) -----------------------------------------------------------------------

typedef struct {
    int32_t width;
    int32_t height;
    int32_t stride;
    const uint8_t *data;    // BGRA, valid until fd_decoder_release or the next decode
} FdDecodedFrame;

typedef void *FdDecoder;

// codec: 0 = H.264, 1 = HEVC.
FdDecoder fd_decoder_create(int32_t codec);
// Decodes one Annex B access unit. Returns 1 and fills `out` on a frame, 0 when none, -1 on error.
int32_t fd_decoder_decode(FdDecoder h, const uint8_t *annexb, int32_t len, FdDecodedFrame *out);
void fd_decoder_release(FdDecoder h);
void fd_decoder_destroy(FdDecoder h);

// ---- Audio capture (ScreenCaptureKit system audio) -----------------------------------------------------

typedef void *FdAudioCapture;

// Starts capturing system audio ("what you hear") as 48 kHz stereo float. Needs Screen Recording consent.
FdAudioCapture fd_audio_capture_create(void);
// Reads up to `maxSamples` interleaved floats into `out`, waiting up to timeoutMs. Returns the count read, 0 on timeout.
int32_t fd_audio_capture_read(FdAudioCapture h, float *out, int32_t maxSamples, int32_t timeoutMs);
void fd_audio_capture_destroy(FdAudioCapture h);

// ---- Audio playback (AudioQueue) -----------------------------------------------------------------------

typedef void *FdAudioPlayback;

FdAudioPlayback fd_audio_playback_create(int32_t sampleRate, int32_t channels);
void fd_audio_playback_enqueue(FdAudioPlayback h, const float *interleaved, int32_t samples);
void fd_audio_playback_destroy(FdAudioPlayback h);

// ---- Pseudo-terminal -----------------------------------------------------------------------------------

// Opens a pty and starts `path` (argv, envp NULL-terminated; cwd may be NULL) as a session leader whose
// controlling terminal and standard streams are the pty. On success returns 0 and fills the master
// descriptor and the pid; otherwise returns an errno value and starts nothing.
int fd_pty_spawn(const char *path, char *const argv[], char *const envp[], const char *cwd,
                 int columns, int rows, int *master_out, int *pid_out);
// Sets the window size, which also sends SIGWINCH to the foreground job. 0 or an errno value.
int fd_pty_resize(int master, int columns, int rows);

#ifdef __cplusplus
}
#endif

#endif
