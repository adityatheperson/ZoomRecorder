#pragma once
#include <stdint.h>

#ifdef ZOOMRECORDER_NATIVE_EXPORTS
#define ZR_API __declspec(dllexport)
#else
#define ZR_API __declspec(dllimport)
#endif

extern "C" {
typedef void* zr_handle;
typedef void* zr_audio_prepare_handle;
typedef void(__stdcall* zr_event_callback)(const char* json, void* context);

enum zr_result {
  ZR_OK = 0,
  ZR_INVALID_ARGUMENT = 1,
  ZR_INVALID_STATE = 2,
  ZR_INTERNAL_ERROR = 3,
  ZR_CANCELLED = 4,
  ZR_AUDIO_STREAM_MISSING = 5,
  ZR_MEDIA_ERROR = 6,
  ZR_IO_ERROR = 7
};

struct zr_audio_chunk {
  uint32_t index;
  const wchar_t* path;
  int64_t start_milliseconds;
  int64_t end_milliseconds;
  const char* sha256;
  uint64_t byte_size;
  uint32_t normalized_sample_rate;
  uint32_t encoded_sample_rate;
  uint32_t channel_count;
};

typedef void(__stdcall* zr_chunk_callback)(const zr_audio_chunk* chunk, void* context);

ZR_API zr_result zr_create(zr_handle* out_handle);
ZR_API zr_result zr_destroy(zr_handle handle);
ZR_API zr_result zr_set_event_callback(zr_handle handle, zr_event_callback callback, void* context);
ZR_API zr_result zr_start_recording(zr_handle handle, const wchar_t* output_path, intptr_t meeting_window);
ZR_API zr_result zr_finalize_recording(zr_handle handle);
ZR_API zr_result zr_prepare_audio_chunks(
    const wchar_t* mp4_path,
    const wchar_t* output_directory,
    uint64_t max_chunk_bytes,
    zr_chunk_callback callback,
    void* context,
    zr_audio_prepare_handle* out_handle);
ZR_API zr_result zr_cancel_audio_preparation(zr_audio_prepare_handle handle);
ZR_API zr_result zr_destroy_audio_preparation(zr_audio_prepare_handle handle);
}
