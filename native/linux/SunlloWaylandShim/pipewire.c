// One PipeWire video stream from a portal's remote. See shim.h for the contract.
//
// Threads: PipeWire's thread loop runs every callback below with the loop lock held; the dp_pw_* entry points take
// the same lock, so the two never see each other half-way. The only thing that crosses without the lock is the
// pixel memory of the locked picture, and that is exactly why it is mapped here rather than by pw_stream: a
// renegotiation (the monitor changed size) removes buffers from under the consumer, and a pw_stream mapping would
// be unmapped with them while C# may still be encoding from it. A mapping of our own keeps the memfd alive until the
// picture is given back.
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <time.h>
#include <unistd.h>

#include <pipewire/pipewire.h>
#include <spa/buffer/meta.h>
#include <spa/param/video/format-utils.h>
#include <spa/pod/builder.h>
#include <spa/utils/result.h>

#include "shim.h"

// Cursors larger than this are ignored rather than truncated; GNOME's largest at 2x is 96.
#define CURSOR_MAX 256
#define CURSOR_META_SIZE(w, h) \
    ((int)(sizeof(struct spa_meta_cursor) + sizeof(struct spa_meta_bitmap) + (size_t)(w) * (size_t)(h) * 4))

// Enough for the consumer never to starve the compositor: one buffer locked by the caller, one pending, one being
// drawn into.
#define MIN_BUFFERS 3

struct mapping {
    uint8_t *base;   // our own mmap of the buffer's memfd, or NULL (MemPtr: the data pointer is used as is)
    size_t length;
};

struct dp_pw {
    struct pw_thread_loop *loop;
    struct pw_context *context;
    struct pw_core *core;
    struct spa_hook core_listener;
    struct pw_stream *stream;
    struct spa_hook stream_listener;

    struct spa_video_info_raw format;
    int have_format;
    int32_t buffers;

    struct pw_buffer *pending;   // newest picture nobody has locked
    struct pw_buffer *locked;    // the picture the caller holds
    struct mapping *orphan;      // the locked picture's mapping, after the stream removed its buffer
    uint64_t sequence;

    int state;
    int connected;
    char error[256];

    dp_cursor cursor;
    uint8_t cursor_bitmap[CURSOR_MAX * CURSOR_MAX * 4];
};

static pthread_once_t init_once = PTHREAD_ONCE_INIT;

static void init_pipewire(void)
{
    pw_init(NULL, NULL);
}

static void copy_text(char *to, int32_t length, const char *text)
{
    if (to != NULL && length > 0) {
        snprintf(to, (size_t)length, "%s", text);
    }
}

static void fail(struct dp_pw *pw, const char *text)
{
    if (pw->state != DP_STATE_ERROR) {
        pw->state = DP_STATE_ERROR;
        copy_text(pw->error, sizeof(pw->error), text);
    }
    pw_thread_loop_signal(pw->loop, false);
}

static int32_t frame_format(uint32_t format)
{
    switch (format) {
    case SPA_VIDEO_FORMAT_BGRx:
    case SPA_VIDEO_FORMAT_BGRA:
        return DP_FORMAT_BGRX;
    case SPA_VIDEO_FORMAT_RGBx:
    case SPA_VIDEO_FORMAT_RGBA:
        return DP_FORMAT_RGBX;
    default:
        return DP_FORMAT_NONE;
    }
}

static int32_t cursor_format(uint32_t format)
{
    switch (format) {
    case SPA_VIDEO_FORMAT_BGRA:
    case SPA_VIDEO_FORMAT_BGRx:
        return DP_FORMAT_BGRA;
    case SPA_VIDEO_FORMAT_RGBA:
    case SPA_VIDEO_FORMAT_RGBx:
        return DP_FORMAT_RGBA;
    default:
        return DP_FORMAT_NONE;
    }
}

static void unmap(struct mapping *m)
{
    if (m != NULL) {
        if (m->base != NULL) {
            munmap(m->base, m->length);
        }
        free(m);
    }
}

// The caller's picture goes back to the stream, or, if the stream already dropped its buffer, its memory goes.
static void give_back(struct dp_pw *pw)
{
    if (pw->locked != NULL) {
        pw_stream_queue_buffer(pw->stream, pw->locked);
        pw->locked = NULL;
    }
    unmap(pw->orphan);
    pw->orphan = NULL;
}

// ---- stream events (loop thread, lock held) ----

static void on_state_changed(void *data, enum pw_stream_state old, enum pw_stream_state state, const char *error)
{
    struct dp_pw *pw = data;
    (void)old;
    switch (state) {
    case PW_STREAM_STATE_ERROR:
        fail(pw, error != NULL ? error : "the PipeWire stream failed");
        return;
    case PW_STREAM_STATE_UNCONNECTED:
        if (pw->connected && pw->state != DP_STATE_ERROR) {
            pw->state = DP_STATE_CLOSED;
        }
        break;
    case PW_STREAM_STATE_CONNECTING:
        pw->state = DP_STATE_CONNECTING;
        pw->connected = 1;
        break;
    case PW_STREAM_STATE_PAUSED:
        pw->state = DP_STATE_PAUSED;
        break;
    case PW_STREAM_STATE_STREAMING:
        pw->state = DP_STATE_STREAMING;
        break;
    }
    pw_thread_loop_signal(pw->loop, false);
}

static void on_param_changed(void *data, uint32_t id, const struct spa_pod *param)
{
    struct dp_pw *pw = data;
    if (param == NULL || id != SPA_PARAM_Format) {
        return;
    }

    uint32_t media_type, media_subtype;
    struct spa_video_info_raw raw;
    memset(&raw, 0, sizeof(raw));
    if (spa_format_parse(param, &media_type, &media_subtype) < 0 || media_type != SPA_MEDIA_TYPE_video ||
        media_subtype != SPA_MEDIA_SUBTYPE_raw || spa_format_video_raw_parse(param, &raw) < 0) {
        fail(pw, "the compositor offered a video format this build cannot read");
        return;
    }

    pw->format = raw;
    pw->have_format = frame_format(raw.format) != DP_FORMAT_NONE;

    uint8_t buffer[1024];
    struct spa_pod_builder b = SPA_POD_BUILDER_INIT(buffer, sizeof(buffer));
    const struct spa_pod *params[4];
    uint32_t n = 0;
    params[n++] = spa_pod_builder_add_object(&b,
        SPA_TYPE_OBJECT_ParamBuffers, SPA_PARAM_Buffers,
        SPA_PARAM_BUFFERS_buffers, SPA_POD_CHOICE_RANGE_Int(4, MIN_BUFFERS, 16),
        SPA_PARAM_BUFFERS_dataType, SPA_POD_CHOICE_FLAGS_Int((1 << SPA_DATA_MemFd) | (1 << SPA_DATA_MemPtr)));
    params[n++] = spa_pod_builder_add_object(&b,
        SPA_TYPE_OBJECT_ParamMeta, SPA_PARAM_Meta,
        SPA_PARAM_META_type, SPA_POD_Id(SPA_META_Header),
        SPA_PARAM_META_size, SPA_POD_Int(sizeof(struct spa_meta_header)));
    params[n++] = spa_pod_builder_add_object(&b,
        SPA_TYPE_OBJECT_ParamMeta, SPA_PARAM_Meta,
        SPA_PARAM_META_type, SPA_POD_Id(SPA_META_VideoCrop),
        SPA_PARAM_META_size, SPA_POD_Int(sizeof(struct spa_meta_region)));
    // The range has to reach whatever the compositor offers, or the two sides share no size and the cursor meta is
    // dropped without a word: mutter offers one fixed size well above 256x256, and with 256 as the ceiling here the
    // stream carried no pointer at all. Bitmaps larger than CURSOR_MAX are still ignored when they arrive.
    params[n++] = spa_pod_builder_add_object(&b,
        SPA_TYPE_OBJECT_ParamMeta, SPA_PARAM_Meta,
        SPA_PARAM_META_type, SPA_POD_Id(SPA_META_Cursor),
        SPA_PARAM_META_size, SPA_POD_CHOICE_RANGE_Int(CURSOR_META_SIZE(64, 64), CURSOR_META_SIZE(1, 1), CURSOR_META_SIZE(1024, 1024)));
    pw_stream_update_params(pw->stream, params, n);
}

static void on_add_buffer(void *data, struct pw_buffer *buffer)
{
    struct dp_pw *pw = data;
    struct mapping *m = calloc(1, sizeof(*m));
    struct spa_data *d = &buffer->buffer->datas[0];
    if (m != NULL && d->type == SPA_DATA_MemFd && d->fd >= 0) {
        size_t length = (size_t)d->mapoffset + d->maxsize;
        void *base = mmap(NULL, length, PROT_READ, MAP_SHARED, (int)d->fd, 0);
        if (base != MAP_FAILED) {
            m->base = base;
            m->length = length;
        }
    }
    buffer->user_data = m;
    pw->buffers++;
}

static void on_remove_buffer(void *data, struct pw_buffer *buffer)
{
    struct dp_pw *pw = data;
    struct mapping *m = buffer->user_data;
    buffer->user_data = NULL;
    pw->buffers--;
    if (buffer == pw->pending) {
        pw->pending = NULL;
    }
    if (buffer == pw->locked) {
        // The caller is still reading it: keep the memory until it is given back.
        pw->locked = NULL;
        unmap(pw->orphan);
        pw->orphan = m;
        return;
    }
    unmap(m);
}

static void read_cursor(struct dp_pw *pw, struct spa_buffer *buffer)
{
    struct spa_meta *meta = spa_buffer_find_meta(buffer, SPA_META_Cursor);
    if (meta == NULL || meta->data == NULL || meta->size < sizeof(struct spa_meta_cursor)) {
        return;
    }

    struct spa_meta_cursor *c = meta->data;
    dp_cursor *out = &pw->cursor;
    if (!spa_meta_cursor_is_valid(c)) {
        if (out->visible) {
            out->visible = 0;
            out->position_serial++;
        }
        return;
    }

    if (!out->visible || out->x != c->position.x || out->y != c->position.y) {
        out->visible = 1;
        out->x = c->position.x;
        out->y = c->position.y;
        out->position_serial++;
    }

    // A bitmap comes only when the shape changed; everything about it is checked against the meta's own size.
    if (c->bitmap_offset < sizeof(*c) || (size_t)c->bitmap_offset + sizeof(struct spa_meta_bitmap) > meta->size) {
        return;
    }
    struct spa_meta_bitmap *bitmap = SPA_PTROFF(c, c->bitmap_offset, struct spa_meta_bitmap);
    uint32_t w = bitmap->size.width, h = bitmap->size.height;
    int32_t format = cursor_format(bitmap->format);
    if (w == 0 || h == 0 || w > CURSOR_MAX || h > CURSOR_MAX || format == DP_FORMAT_NONE || bitmap->stride < (int32_t)(w * 4) ||
        bitmap->offset < sizeof(*bitmap) ||
        (size_t)c->bitmap_offset + bitmap->offset + (size_t)bitmap->stride * (h - 1) + (size_t)w * 4 > meta->size) {
        return;
    }

    const uint8_t *source = SPA_PTROFF(bitmap, bitmap->offset, const uint8_t);
    for (uint32_t y = 0; y < h; y++) {
        memcpy(pw->cursor_bitmap + (size_t)y * w * 4, source + (size_t)y * (size_t)bitmap->stride, (size_t)w * 4);
    }
    out->width = (int32_t)w;
    out->height = (int32_t)h;
    out->hot_x = c->hotspot.x;
    out->hot_y = c->hotspot.y;
    out->format = format;
    out->shape_serial++;
}

static int has_picture(struct spa_buffer *buffer)
{
    if (buffer->n_datas == 0 || buffer->datas[0].chunk == NULL) {
        return 0;
    }
    struct spa_meta_header *header = spa_buffer_find_meta_data(buffer, SPA_META_Header, sizeof(*header));
    if (header != NULL && (header->flags & SPA_META_HEADER_FLAG_CORRUPTED)) {
        return 0;
    }
    // A buffer with only the cursor moved carries an empty chunk.
    struct spa_chunk *chunk = buffer->datas[0].chunk;
    return chunk->size > 0 && !(chunk->flags & SPA_CHUNK_FLAG_CORRUPTED);
}

static void on_process(void *data)
{
    struct dp_pw *pw = data;
    struct pw_buffer *b;
    while ((b = pw_stream_dequeue_buffer(pw->stream)) != NULL) {
        read_cursor(pw, b->buffer);
        if (!has_picture(b->buffer)) {
            pw_stream_queue_buffer(pw->stream, b);
            continue;
        }
        if (pw->pending != NULL) {
            pw_stream_queue_buffer(pw->stream, pw->pending);
        }
        pw->pending = b;
        pw->sequence++;
    }
    pw_thread_loop_signal(pw->loop, false);
}

static const struct pw_stream_events stream_events = {
    PW_VERSION_STREAM_EVENTS,
    .state_changed = on_state_changed,
    .param_changed = on_param_changed,
    .add_buffer = on_add_buffer,
    .remove_buffer = on_remove_buffer,
    .process = on_process,
};

static void on_core_error(void *data, uint32_t id, int seq, int res, const char *message)
{
    struct dp_pw *pw = data;
    (void)seq;
    if (id == PW_ID_CORE) {
        char text[256];
        snprintf(text, sizeof(text), "PipeWire: %s (%s)", message != NULL ? message : "error", spa_strerror(res));
        fail(pw, text);
    }
}

static const struct pw_core_events core_events = {
    PW_VERSION_CORE_EVENTS,
    .error = on_core_error,
};

// ---- the C surface ----

DP_EXPORT int dp_pw_abi(int32_t *frame_size, int32_t *cursor_size)
{
    if (frame_size != NULL) {
        *frame_size = (int32_t)sizeof(dp_frame);
    }
    if (cursor_size != NULL) {
        *cursor_size = (int32_t)sizeof(dp_cursor);
    }
    return DP_PW_ABI;
}

DP_EXPORT int dp_pw_open(int fd, uint32_t node, dp_pw **out, char *error, int32_t error_len)
{
    *out = NULL;
    pthread_once(&init_once, init_pipewire);

    struct dp_pw *pw = calloc(1, sizeof(*pw));
    if (pw == NULL) {
        copy_text(error, error_len, "out of memory");
        return -ENOMEM;
    }

    int own = fcntl(fd, F_DUPFD_CLOEXEC, 3);
    if (own < 0) {
        int e = errno;
        copy_text(error, error_len, "the PipeWire remote's file descriptor cannot be duplicated");
        free(pw);
        return -e;
    }

    pw->loop = pw_thread_loop_new("deskpair-pipewire", NULL);
    pw->context = pw->loop != NULL ? pw_context_new(pw_thread_loop_get_loop(pw->loop), NULL, 0) : NULL;
    if (pw->context == NULL || pw_thread_loop_start(pw->loop) < 0) {
        copy_text(error, error_len, "PipeWire's loop cannot be started");
        close(own);
        dp_pw_close(pw);
        return -EIO;
    }

    pw_thread_loop_lock(pw->loop);
    // The core owns the descriptor from here on, and closes it when it disconnects.
    pw->core = pw_context_connect_fd(pw->context, own, NULL, 0);
    if (pw->core == NULL) {
        int e = errno;
        pw_thread_loop_unlock(pw->loop);
        copy_text(error, error_len, "the portal's PipeWire remote refused the connection");
        dp_pw_close(pw);
        return e != 0 ? -e : -EIO;
    }
    pw_core_add_listener(pw->core, &pw->core_listener, &core_events, pw);

    pw->stream = pw_stream_new(pw->core, "DeskPair screen",
        pw_properties_new(PW_KEY_MEDIA_TYPE, "Video", PW_KEY_MEDIA_CATEGORY, "Capture", PW_KEY_MEDIA_ROLE, "Screen", NULL));
    if (pw->stream == NULL) {
        pw_thread_loop_unlock(pw->loop);
        copy_text(error, error_len, "a PipeWire stream cannot be created");
        dp_pw_close(pw);
        return -EIO;
    }
    pw_stream_add_listener(pw->stream, &pw->stream_listener, &stream_events, pw);

    // Packed 32-bit RGB only, and no modifiers, so the compositor hands over shared memory rather than a dma-buf:
    // it does the GPU readback, which is the one thing every compositor already knows how to do.
    uint8_t buffer[1024];
    struct spa_pod_builder b = SPA_POD_BUILDER_INIT(buffer, sizeof(buffer));
    const struct spa_pod *params[1];
    params[0] = spa_pod_builder_add_object(&b,
        SPA_TYPE_OBJECT_Format, SPA_PARAM_EnumFormat,
        SPA_FORMAT_mediaType, SPA_POD_Id(SPA_MEDIA_TYPE_video),
        SPA_FORMAT_mediaSubtype, SPA_POD_Id(SPA_MEDIA_SUBTYPE_raw),
        SPA_FORMAT_VIDEO_format, SPA_POD_CHOICE_ENUM_Id(5,
            SPA_VIDEO_FORMAT_BGRx, SPA_VIDEO_FORMAT_BGRx, SPA_VIDEO_FORMAT_BGRA, SPA_VIDEO_FORMAT_RGBx, SPA_VIDEO_FORMAT_RGBA),
        SPA_FORMAT_VIDEO_size, SPA_POD_CHOICE_RANGE_Rectangle(
            &SPA_RECTANGLE(1920, 1080), &SPA_RECTANGLE(1, 1), &SPA_RECTANGLE(16384, 16384)),
        SPA_FORMAT_VIDEO_framerate, SPA_POD_CHOICE_RANGE_Fraction(
            &SPA_FRACTION(60, 1), &SPA_FRACTION(0, 1), &SPA_FRACTION(360, 1)));

    int res = pw_stream_connect(pw->stream, PW_DIRECTION_INPUT, node, PW_STREAM_FLAG_AUTOCONNECT, params, 1);
    pw_thread_loop_unlock(pw->loop);
    if (res < 0) {
        char text[256];
        snprintf(text, sizeof(text), "the stream cannot connect to node %u: %s", node, spa_strerror(res));
        copy_text(error, error_len, text);
        dp_pw_close(pw);
        return res;
    }

    *out = pw;
    return 0;
}

DP_EXPORT int dp_pw_wait(dp_pw *pw, int32_t timeout_ms)
{
    pw_thread_loop_lock(pw->loop);
    struct timespec deadline;
    pw_thread_loop_get_time(pw->loop, &deadline, (int64_t)(timeout_ms < 0 ? 0 : timeout_ms) * SPA_NSEC_PER_MSEC);
    int result;
    for (;;) {
        if (pw->state == DP_STATE_ERROR || pw->state == DP_STATE_CLOSED) {
            result = -1;
            break;
        }
        if (pw->pending != NULL) {
            result = 1;
            break;
        }
        if (pw_thread_loop_timed_wait_full(pw->loop, &deadline) != 0) {
            result = pw->pending != NULL ? 1 : 0;
            break;
        }
    }
    pw_thread_loop_unlock(pw->loop);
    return result;
}

DP_EXPORT int dp_pw_lock_frame(dp_pw *pw, dp_frame *out)
{
    memset(out, 0, sizeof(*out));
    pw_thread_loop_lock(pw->loop);
    int result = 0;
    struct pw_buffer *b = pw->pending;
    if (b != NULL && pw->have_format) {
        pw->pending = NULL;
        struct spa_buffer *buffer = b->buffer;
        struct spa_data *d = &buffer->datas[0];
        struct mapping *m = b->user_data;
        const uint8_t *base = m != NULL && m->base != NULL ? m->base + d->mapoffset : d->data;

        int32_t width = (int32_t)pw->format.size.width;
        int32_t height = (int32_t)pw->format.size.height;
        int32_t stride = d->chunk->stride > 0 ? d->chunk->stride : width * 4;
        size_t offset = d->chunk->offset;
        struct spa_meta_region *crop = spa_buffer_find_meta_data(buffer, SPA_META_VideoCrop, sizeof(*crop));
        if (crop != NULL && spa_meta_region_is_valid(crop) && crop->region.position.x >= 0 && crop->region.position.y >= 0 &&
            crop->region.position.x + (int64_t)crop->region.size.width <= width &&
            crop->region.position.y + (int64_t)crop->region.size.height <= height) {
            offset += (size_t)crop->region.position.y * (size_t)stride + (size_t)crop->region.position.x * 4;
            width = (int32_t)crop->region.size.width;
            height = (int32_t)crop->region.size.height;
        }

        if (base == NULL || width <= 0 || height <= 0 || stride < width * 4 ||
            offset + (size_t)stride * (size_t)(height - 1) + (size_t)width * 4 > d->maxsize) {
            // Nothing that can be read safely: hand it straight back rather than give out a pointer past its end.
            pw_stream_queue_buffer(pw->stream, b);
        } else {
            give_back(pw);
            pw->locked = b;
            struct spa_meta_header *header = spa_buffer_find_meta_data(buffer, SPA_META_Header, sizeof(*header));
            out->data = base + offset;
            out->width = width;
            out->height = height;
            out->stride = stride;
            out->format = frame_format(pw->format.format);
            out->sequence = pw->sequence;
            out->pts_ns = header != NULL ? header->pts : 0;
            out->buffers = pw->buffers;
            out->flags = m != NULL && m->base != NULL && pw->buffers >= MIN_BUFFERS ? DP_FRAME_BORROWABLE : 0;
            out->readable = (int64_t)(d->maxsize - offset);
            result = 1;
        }
    }
    pw_thread_loop_unlock(pw->loop);
    return result;
}

DP_EXPORT void dp_pw_release_frame(dp_pw *pw)
{
    pw_thread_loop_lock(pw->loop);
    give_back(pw);
    pw_thread_loop_unlock(pw->loop);
}

DP_EXPORT int dp_pw_cursor(dp_pw *pw, dp_cursor *out, uint8_t *bitmap, int32_t capacity)
{
    pw_thread_loop_lock(pw->loop);
    *out = pw->cursor;
    size_t size = (size_t)pw->cursor.width * (size_t)pw->cursor.height * 4;
    if (bitmap != NULL && size > 0 && capacity >= 0 && (size_t)capacity >= size) {
        memcpy(bitmap, pw->cursor_bitmap, size);
    }
    pw_thread_loop_unlock(pw->loop);
    return 0;
}

DP_EXPORT int dp_pw_state(dp_pw *pw, char *error, int32_t error_len)
{
    pw_thread_loop_lock(pw->loop);
    int state = pw->state;
    copy_text(error, error_len, pw->error);
    pw_thread_loop_unlock(pw->loop);
    return state;
}

DP_EXPORT void dp_pw_close(dp_pw *pw)
{
    if (pw == NULL) {
        return;
    }
    // The loop thread stops first, so nothing below races a callback.
    if (pw->loop != NULL) {
        pw_thread_loop_stop(pw->loop);
    }
    if (pw->stream != NULL) {
        pw->pending = NULL;
        pw->locked = NULL;
        pw_stream_destroy(pw->stream);   // every buffer's remove_buffer runs, unmapping it
    }
    unmap(pw->orphan);
    if (pw->core != NULL) {
        pw_core_disconnect(pw->core);
    }
    if (pw->context != NULL) {
        pw_context_destroy(pw->context);
    }
    if (pw->loop != NULL) {
        pw_thread_loop_destroy(pw->loop);
    }
    free(pw);
}
