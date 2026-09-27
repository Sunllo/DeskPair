// A pretend monitor for tools/wayland-ci: a PipeWire video source in BGRx, one colour with a white bar that moves a
// little every frame, in memfd buffers of its own -- which is what a compositor's screen-cast stream hands out, and
// what the shim maps itself to pass pictures on uncopied. Buffers that PipeWire allocates arrive in the other process
// as plain memory (MemPtr), as GStreamer's pipewiresink's do, and would only ever exercise the copying path; so the
// buffers are allocated here, the way mutter allocates them (meta-screen-cast-stream-src.c).
//
// After PipeWire's own src/examples/video-src.c. Built and run by inside.sh:
//   cc -O2 -o test-screen test-screen.c $(pkg-config --cflags --libs libpipewire-0.3)
//   test-screen RRGGBB WIDTH HEIGHT
#define _GNU_SOURCE
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <unistd.h>
#include <spa/param/video/format-utils.h>
#include <spa/pod/builder.h>
#include <pipewire/pipewire.h>

struct data {
    struct pw_main_loop *loop;
    struct spa_source *timer;
    struct pw_stream *stream;
    struct spa_video_info_raw format;
    int32_t stride;
    uint32_t colour;
    uint32_t frame;
};

static void on_timeout(void *userdata, uint64_t expirations)
{
    (void)expirations;
    struct data *d = userdata;
    pw_stream_trigger_process(d->stream);
}

static void on_process(void *userdata)
{
    struct data *d = userdata;
    struct pw_buffer *b = pw_stream_dequeue_buffer(d->stream);
    if (b == NULL) {
        return;
    }

    struct spa_buffer *buf = b->buffer;
    uint8_t *p = buf->datas[0].data;
    if (p != NULL) {
        uint32_t width = d->format.size.width;
        uint32_t height = d->format.size.height;
        uint32_t bar = (d->frame * 8) % width;
        for (uint32_t y = 0; y < height; y++) {
            uint32_t *row = (uint32_t *)(p + (size_t)y * d->stride);
            for (uint32_t x = 0; x < width; x++) {
                // BGRx in memory is 0xXXRRGGBB as a little-endian word.
                row[x] = (x >= bar && x < bar + 16) ? 0xffffffffu : (0xff000000u | d->colour);
            }
        }
        buf->datas[0].chunk->offset = 0;
        buf->datas[0].chunk->size = (uint32_t)d->stride * height;
        buf->datas[0].chunk->stride = d->stride;
        d->frame++;
    }

    pw_stream_queue_buffer(d->stream, b);
}

static void on_state_changed(void *userdata, enum pw_stream_state old, enum pw_stream_state state, const char *error)
{
    (void)old;
    struct data *d = userdata;
    fprintf(stderr, "test-screen: %s%s%s\n", pw_stream_state_as_string(state), error ? ": " : "", error ? error : "");
    struct timespec interval = { 0, 1000000000 / 30 };
    struct timespec start = { 0, 1 };
    struct timespec off = { 0, 0 };
    if (state == PW_STREAM_STATE_STREAMING) {
        pw_loop_update_timer(pw_main_loop_get_loop(d->loop), d->timer, &start, &interval, false);
    } else {
        pw_loop_update_timer(pw_main_loop_get_loop(d->loop), d->timer, &off, &off, false);
    }
}

static void on_param_changed(void *userdata, uint32_t id, const struct spa_pod *param)
{
    struct data *d = userdata;
    if (param == NULL || id != SPA_PARAM_Format) {
        return;
    }
    if (spa_format_video_raw_parse(param, &d->format) < 0) {
        return;
    }

    d->stride = SPA_ROUND_UP_N((int32_t)d->format.size.width * 4, 4);
    uint8_t buffer[1024];
    struct spa_pod_builder b = SPA_POD_BUILDER_INIT(buffer, sizeof(buffer));
    const struct spa_pod *params[1];
    params[0] = spa_pod_builder_add_object(&b,
        SPA_TYPE_OBJECT_ParamBuffers, SPA_PARAM_Buffers,
        SPA_PARAM_BUFFERS_buffers, SPA_POD_CHOICE_RANGE_Int(8, 2, 16),
        SPA_PARAM_BUFFERS_blocks, SPA_POD_Int(1),
        SPA_PARAM_BUFFERS_size, SPA_POD_Int(d->stride * (int32_t)d->format.size.height),
        SPA_PARAM_BUFFERS_stride, SPA_POD_Int(d->stride),
        SPA_PARAM_BUFFERS_dataType, SPA_POD_CHOICE_FLAGS_Int(1 << SPA_DATA_MemFd));
    pw_stream_update_params(d->stream, params, 1);
}

// The memory for each buffer, as a memfd the other side can map: PW_STREAM_FLAG_ALLOC_BUFFERS leaves it to us, and
// datas[0].type says which kinds the negotiation allowed.
static void on_add_buffer(void *userdata, struct pw_buffer *buffer)
{
    struct data *d = userdata;
    struct spa_data *sd = &buffer->buffer->datas[0];
    if ((sd->type & (1u << SPA_DATA_MemFd)) == 0) {
        fprintf(stderr, "test-screen: memfd buffers were not allowed\n");
        return;
    }

    uint32_t size = (uint32_t)d->stride * d->format.size.height;
    int fd = memfd_create("deskpair-test-screen", MFD_CLOEXEC);
    if (fd < 0 || ftruncate(fd, size) < 0) {
        fprintf(stderr, "test-screen: cannot create a memfd\n");
        return;
    }

    sd->type = SPA_DATA_MemFd;
    sd->flags = SPA_DATA_FLAG_READWRITE;
    sd->fd = fd;
    sd->mapoffset = 0;
    sd->maxsize = size;
    sd->data = mmap(NULL, size, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    if (sd->data == MAP_FAILED) {
        sd->data = NULL;
    }
}

static void on_remove_buffer(void *userdata, struct pw_buffer *buffer)
{
    (void)userdata;
    struct spa_data *sd = &buffer->buffer->datas[0];
    if (sd->type == SPA_DATA_MemFd) {
        if (sd->data != NULL) {
            munmap(sd->data, sd->maxsize);
        }
        close((int)sd->fd);
    }
}

static const struct pw_stream_events stream_events = {
    PW_VERSION_STREAM_EVENTS,
    .state_changed = on_state_changed,
    .param_changed = on_param_changed,
    .add_buffer = on_add_buffer,
    .remove_buffer = on_remove_buffer,
    .process = on_process,
};

int main(int argc, char *argv[])
{
    if (argc != 4) {
        fprintf(stderr, "usage: test-screen RRGGBB WIDTH HEIGHT\n");
        return 2;
    }

    struct data d = { 0 };
    d.colour = (uint32_t)strtoul(argv[1], NULL, 16);
    uint32_t width = (uint32_t)atoi(argv[2]);
    uint32_t height = (uint32_t)atoi(argv[3]);

    pw_init(&argc, &argv);
    d.loop = pw_main_loop_new(NULL);
    d.timer = pw_loop_add_timer(pw_main_loop_get_loop(d.loop), on_timeout, &d);
    d.stream = pw_stream_new_simple(
        pw_main_loop_get_loop(d.loop), "deskpair-test-screen",
        pw_properties_new(PW_KEY_MEDIA_CLASS, "Video/Source", PW_KEY_NODE_NAME, "deskpair-test-screen", NULL),
        &stream_events, &d);

    uint8_t buffer[1024];
    struct spa_pod_builder b = SPA_POD_BUILDER_INIT(buffer, sizeof(buffer));
    const struct spa_pod *params[1];
    params[0] = spa_pod_builder_add_object(&b,
        SPA_TYPE_OBJECT_Format, SPA_PARAM_EnumFormat,
        SPA_FORMAT_mediaType, SPA_POD_Id(SPA_MEDIA_TYPE_video),
        SPA_FORMAT_mediaSubtype, SPA_POD_Id(SPA_MEDIA_SUBTYPE_raw),
        SPA_FORMAT_VIDEO_format, SPA_POD_Id(SPA_VIDEO_FORMAT_BGRx),
        SPA_FORMAT_VIDEO_size, SPA_POD_Rectangle(&SPA_RECTANGLE(width, height)),
        SPA_FORMAT_VIDEO_framerate, SPA_POD_Fraction(&SPA_FRACTION(30, 1)));

    pw_stream_connect(d.stream, PW_DIRECTION_OUTPUT, PW_ID_ANY,
        PW_STREAM_FLAG_DRIVER | PW_STREAM_FLAG_ALLOC_BUFFERS, params, 1);
    pw_main_loop_run(d.loop);

    pw_stream_destroy(d.stream);
    pw_main_loop_destroy(d.loop);
    pw_deinit();
    return 0;
}
