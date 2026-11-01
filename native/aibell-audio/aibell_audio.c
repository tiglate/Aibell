/*
 * Aibell audio shim: a tiny C surface over miniaudio so managed code needs no knowledge of miniaudio's struct layouts.
 * - Output: a playback device that pulls interleaved stereo float frames from a callback.
 * - Decoder: miniaudio's built-in WAV, FLAC, MP3 and Vorbis decoders reading through caller-supplied read/seek callbacks,
 *   always producing stereo float at the requested sample rate.
 * miniaudio and stb_vorbis are public domain / MIT-0 (see vendor/LICENSE).
 */
#include "vendor/stb_vorbis.c"
#undef L
#undef R
#undef C
#define MA_NO_ENCODING
#define MA_NO_GENERATION
#define MA_NO_RESOURCE_MANAGER
#define MA_NO_NODE_GRAPH
#define MA_NO_ENGINE
#define MINIAUDIO_IMPLEMENTATION
#include "vendor/miniaudio.h"

#include <stdint.h>
#include <stdlib.h>

#if defined(_WIN32)
#define AIBELL_API __declspec(dllexport)
#else
#define AIBELL_API __attribute__((visibility("default")))
#endif

#define AIBELL_CHANNELS 2

typedef void (*aibell_pull_fn)(void *user, float *out, uint32_t frames);
typedef int64_t (*aibell_read_fn)(void *user, void *buffer, uint64_t bytes);
typedef int (*aibell_seek_fn)(void *user, int64_t offset, int origin);

AIBELL_API int32_t aibell_abi_version(void) { return 1; }

/* ---- Output ---------------------------------------------------------------------------------------------------- */

typedef struct
{
    ma_context context;
    ma_device device;
    aibell_pull_fn pull;
    void *user;
} aibell_output;

static void aibell_output_data(ma_device *device, void *output, const void *input, ma_uint32 frames)
{
    aibell_output *self = (aibell_output *)device->pUserData;
    (void)input;
    self->pull(self->user, (float *)output, frames);
}

/* sampleRate 0 selects the device's own rate; useNull picks miniaudio's silent backend (tests, machines without sound). */
AIBELL_API void *aibell_output_open(uint32_t sampleRate, int32_t useNull, aibell_pull_fn pull, void *user, int32_t *error)
{
    aibell_output *self = (aibell_output *)calloc(1, sizeof(*self));
    ma_backend nullBackend = ma_backend_null;
    ma_result result;
    ma_device_config config;
    if (self == NULL) { *error = MA_OUT_OF_MEMORY; return NULL; }
    self->pull = pull;
    self->user = user;
    result = ma_context_init(useNull ? &nullBackend : NULL, useNull ? 1 : 0, NULL, &self->context);
    if (result != MA_SUCCESS) { *error = result; free(self); return NULL; }
    config = ma_device_config_init(ma_device_type_playback);
    config.playback.format = ma_format_f32;
    config.playback.channels = AIBELL_CHANNELS;
    config.sampleRate = sampleRate;
    config.dataCallback = aibell_output_data;
    config.pUserData = self;
    config.performanceProfile = ma_performance_profile_conservative;
    result = ma_device_init(&self->context, &config, &self->device);
    if (result != MA_SUCCESS) { *error = result; ma_context_uninit(&self->context); free(self); return NULL; }
    *error = MA_SUCCESS;
    return self;
}

AIBELL_API uint32_t aibell_output_sample_rate(void *handle) { return ((aibell_output *)handle)->device.sampleRate; }
AIBELL_API int32_t aibell_output_start(void *handle) { return ma_device_start(&((aibell_output *)handle)->device); }
AIBELL_API int32_t aibell_output_stop(void *handle) { return ma_device_stop(&((aibell_output *)handle)->device); }

AIBELL_API void aibell_output_close(void *handle)
{
    aibell_output *self = (aibell_output *)handle;
    if (self == NULL) return;
    ma_device_uninit(&self->device);
    ma_context_uninit(&self->context);
    free(self);
}

/* ---- Decoder --------------------------------------------------------------------------------------------------- */

typedef struct
{
    ma_decoder decoder;
    aibell_read_fn read;
    aibell_seek_fn seek;
    void *user;
} aibell_decoder;

static ma_result aibell_decoder_on_read(ma_decoder *decoder, void *buffer, size_t bytes, size_t *bytesRead)
{
    aibell_decoder *self = (aibell_decoder *)decoder->pUserData;
    int64_t count = self->read(self->user, buffer, (uint64_t)bytes);
    if (count < 0) { *bytesRead = 0; return MA_ERROR; }
    *bytesRead = (size_t)count;
    return count == 0 && bytes > 0 ? MA_AT_END : MA_SUCCESS;
}

static ma_result aibell_decoder_on_seek(ma_decoder *decoder, ma_int64 offset, ma_seek_origin origin)
{
    aibell_decoder *self = (aibell_decoder *)decoder->pUserData;
    int mapped = origin == ma_seek_origin_start ? 0 : origin == ma_seek_origin_current ? 1 : 2;
    return self->seek(self->user, offset, mapped) == 0 ? MA_SUCCESS : MA_ERROR;
}

AIBELL_API void *aibell_decoder_open(aibell_read_fn read, aibell_seek_fn seek, void *user, uint32_t sampleRate, int32_t *error)
{
    aibell_decoder *self = (aibell_decoder *)calloc(1, sizeof(*self));
    ma_decoder_config config;
    ma_result result;
    if (self == NULL) { *error = MA_OUT_OF_MEMORY; return NULL; }
    self->read = read;
    self->seek = seek;
    self->user = user;
    config = ma_decoder_config_init(ma_format_f32, AIBELL_CHANNELS, sampleRate);
    result = ma_decoder_init(aibell_decoder_on_read, aibell_decoder_on_seek, self, &config, &self->decoder);
    if (result != MA_SUCCESS) { *error = result; free(self); return NULL; }
    *error = MA_SUCCESS;
    return self;
}

/* Frames read (0 at the end of the stream), or a negative miniaudio result on failure. */
AIBELL_API int64_t aibell_decoder_read(void *handle, float *out, uint32_t frames)
{
    ma_uint64 read = 0;
    ma_result result = ma_decoder_read_pcm_frames(&((aibell_decoder *)handle)->decoder, out, frames, &read);
    if (result != MA_SUCCESS && result != MA_AT_END) return -(int64_t)(result < 0 ? -result : result);
    return (int64_t)read;
}

AIBELL_API int32_t aibell_decoder_seek(void *handle, uint64_t frame)
{
    return ma_decoder_seek_to_pcm_frame(&((aibell_decoder *)handle)->decoder, frame);
}

/* Total frames, or -1 when the stream does not say (internet radio, some Vorbis files). */
AIBELL_API int64_t aibell_decoder_length(void *handle)
{
    ma_uint64 length = 0;
    if (ma_decoder_get_length_in_pcm_frames(&((aibell_decoder *)handle)->decoder, &length) != MA_SUCCESS || length == 0) return -1;
    return (int64_t)length;
}

AIBELL_API void aibell_decoder_close(void *handle)
{
    aibell_decoder *self = (aibell_decoder *)handle;
    if (self == NULL) return;
    ma_decoder_uninit(&self->decoder);
    free(self);
}
