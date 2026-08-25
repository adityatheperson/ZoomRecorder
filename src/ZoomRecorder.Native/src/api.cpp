#define ZOOMRECORDER_NATIVE_EXPORTS
#include "zoom_recorder.h"

#include <memory>
#include <mutex>
#include <condition_variable>
#include <cstdint>
#include <string>
#include <thread>
#include <unordered_map>
#include "media/audio_chunk_exporter.h"
#include "media/pcm_wav_converter.h"
#include "media/recording_pipeline.h"

namespace {
struct session {
  std::mutex mutex;
  zr_event_callback callback{};
  void* context{};
  bool recording_started{};
  std::unique_ptr<RecordingPipeline> pipeline;
};

struct audio_preparation {
  std::mutex mutex;
  std::condition_variable finished;
  audio_chunk_cancellation cancellation;
  bool active{true};
  std::thread::id worker;
};

struct pcm_conversion {
  std::mutex mutex;
  std::condition_variable finished;
  pcm_wav_conversion_cancellation cancellation;
  bool active{true};
  std::thread::id worker;
};

std::mutex request_handle_mutex;
std::uintptr_t next_request_handle{1};

void* next_handle() {
  std::scoped_lock lock(request_handle_mutex);
  void* handle{};
  do {
    handle = reinterpret_cast<void*>(next_request_handle++);
  } while (!handle);
  return handle;
}

std::mutex audio_preparation_registry_mutex;
std::unordered_map<zr_audio_prepare_handle, std::shared_ptr<audio_preparation>> audio_preparation_registry;

zr_audio_prepare_handle register_audio_preparation(const std::shared_ptr<audio_preparation>& preparation) {
  std::scoped_lock lock(audio_preparation_registry_mutex);
  const auto handle = static_cast<zr_audio_prepare_handle>(next_handle());
  audio_preparation_registry.emplace(handle, preparation);
  return handle;
}

std::shared_ptr<audio_preparation> acquire_audio_preparation(zr_audio_prepare_handle handle) {
  std::scoped_lock lock(audio_preparation_registry_mutex);
  const auto found = audio_preparation_registry.find(handle);
  return found == audio_preparation_registry.end() ? nullptr : found->second;
}

std::mutex pcm_conversion_registry_mutex;
std::unordered_map<zr_pcm_convert_handle, std::shared_ptr<pcm_conversion>> pcm_conversion_registry;

zr_pcm_convert_handle register_pcm_conversion(const std::shared_ptr<pcm_conversion>& conversion) {
  std::scoped_lock lock(pcm_conversion_registry_mutex);
  const auto handle = static_cast<zr_pcm_convert_handle>(next_handle());
  pcm_conversion_registry.emplace(handle, conversion);
  return handle;
}

std::shared_ptr<pcm_conversion> acquire_pcm_conversion(zr_pcm_convert_handle handle) {
  std::scoped_lock lock(pcm_conversion_registry_mutex);
  const auto found = pcm_conversion_registry.find(handle);
  return found == pcm_conversion_registry.end() ? nullptr : found->second;
}

void emit(session& value, const char* json) {
  if (value.callback) value.callback(json, value.context);
}

zr_result map_audio_result(audio_chunk_export_result result) {
  switch (result) {
    case audio_chunk_export_result::success: return ZR_OK;
    case audio_chunk_export_result::invalid_argument: return ZR_INVALID_ARGUMENT;
    case audio_chunk_export_result::missing_audio: return ZR_AUDIO_STREAM_MISSING;
    case audio_chunk_export_result::cancelled: return ZR_CANCELLED;
    case audio_chunk_export_result::media_failure: return ZR_MEDIA_ERROR;
    case audio_chunk_export_result::io_failure: return ZR_IO_ERROR;
  }
  return ZR_INTERNAL_ERROR;
}

zr_result map_pcm_result(pcm_wav_conversion_result result) {
  switch (result) {
    case pcm_wav_conversion_result::success: return ZR_OK;
    case pcm_wav_conversion_result::invalid_argument: return ZR_INVALID_ARGUMENT;
    case pcm_wav_conversion_result::missing_audio: return ZR_AUDIO_STREAM_MISSING;
    case pcm_wav_conversion_result::cancelled: return ZR_CANCELLED;
    case pcm_wav_conversion_result::media_failure: return ZR_MEDIA_ERROR;
    case pcm_wav_conversion_result::io_failure: return ZR_IO_ERROR;
  }
  return ZR_INTERNAL_ERROR;
}
}

zr_result zr_create(zr_handle* out_handle) {
  if (!out_handle) return ZR_INVALID_ARGUMENT;
  try {
    auto value = std::make_unique<session>();
    auto* raw = value.get();
    value->pipeline = std::make_unique<RecordingPipeline>([raw](bool ok, const char* message) {
      const std::string event = std::string{"{\"type\":\""} + (ok ? "component_ready" : "failed") + "\",\"message\":\"" + message + "\"}";
      emit(*raw, event.c_str());
    }, [raw] { emit(*raw, R"({"type":"capture_window_lost"})"); });
    *out_handle = value.release(); return ZR_OK;
  }
  catch (...) { *out_handle = nullptr; return ZR_INTERNAL_ERROR; }
}

zr_result zr_destroy(zr_handle handle) {
  delete static_cast<session*>(handle);
  return ZR_OK;
}

zr_result zr_set_event_callback(zr_handle handle, zr_event_callback callback, void* context) {
  if (!handle) return ZR_INVALID_ARGUMENT;
  auto& value = *static_cast<session*>(handle);
  std::scoped_lock lock(value.mutex);
  value.callback = callback; value.context = context;
  return ZR_OK;
}

zr_result zr_start_recording(zr_handle handle, const wchar_t* output_path, intptr_t meeting_window) {
  if (!handle || !output_path || !*output_path || !meeting_window) return ZR_INVALID_ARGUMENT;
  const auto window = reinterpret_cast<HWND>(meeting_window);
  if (!IsWindow(window)) return ZR_INVALID_ARGUMENT;
  auto& value = *static_cast<session*>(handle);
  std::scoped_lock lock(value.mutex);
  if (value.recording_started) return ZR_INVALID_STATE;
  if (!value.pipeline->start(output_path)) return ZR_INTERNAL_ERROR;
  if (!value.pipeline->attach_video(window)) {
    value.pipeline->stop_and_finalize();
    return ZR_INTERNAL_ERROR;
  }
  value.recording_started = true; emit(value, R"({"type":"recording_started"})");
  return ZR_OK;
}

zr_result zr_finalize_recording(zr_handle handle) {
  if (!handle) return ZR_INVALID_ARGUMENT;
  auto& value = *static_cast<session*>(handle);
  std::scoped_lock lock(value.mutex);
  if (!value.recording_started) return ZR_OK;
  const auto finalized = value.pipeline->stop_and_finalize();
  value.recording_started = false; emit(value, R"({"type":"recording_finalized"})");
  return finalized ? ZR_OK : ZR_INTERNAL_ERROR;
}

zr_result zr_attach_recording_window(zr_handle handle, intptr_t meeting_window) {
  if (!handle || !meeting_window) return ZR_INVALID_ARGUMENT;
  const auto window = reinterpret_cast<HWND>(meeting_window);
  if (!IsWindow(window)) return ZR_INVALID_ARGUMENT;
  auto& value = *static_cast<session*>(handle);
  std::scoped_lock lock(value.mutex);
  if (!value.recording_started) return ZR_INVALID_STATE;
  return value.pipeline->replace_video(window) ? ZR_OK : ZR_INTERNAL_ERROR;
}

zr_result zr_prepare_audio_chunks(
    const wchar_t* mp4_path,
    const wchar_t* output_directory,
    uint64_t max_chunk_bytes,
    zr_chunk_callback callback,
    void* context,
    zr_audio_prepare_handle* out_handle) {
  if (!out_handle) return ZR_INVALID_ARGUMENT;
  *out_handle = nullptr;
  if (!mp4_path || !*mp4_path || !output_directory || !*output_directory || max_chunk_bytes == 0 || !callback)
    return ZR_INVALID_ARGUMENT;
  std::shared_ptr<audio_preparation> preparation;
  try {
    preparation = std::make_shared<audio_preparation>();
    preparation->worker = std::this_thread::get_id();
    *out_handle = register_audio_preparation(preparation);
    audio_chunk_exporter exporter(preparation->cancellation);
    const auto result = exporter.export_chunks(mp4_path, output_directory, max_chunk_bytes,
      [callback, context](const audio_chunk_export_record& chunk) {
        const zr_audio_chunk value{
          chunk.index,
          chunk.path.c_str(),
          chunk.start_milliseconds,
          chunk.end_milliseconds,
          chunk.sha256.c_str(),
          chunk.byte_size,
          chunk.normalized_sample_rate,
          chunk.encoded_sample_rate,
          chunk.channel_count
        };
        callback(&value, context);
      });
    {
      std::scoped_lock lock(preparation->mutex);
      preparation->active = false;
    }
    preparation->finished.notify_all();
    return map_audio_result(result);
  } catch (...) {
    if (preparation) {
      {
        std::scoped_lock lock(preparation->mutex);
        preparation->active = false;
      }
      preparation->finished.notify_all();
    }
    return ZR_INTERNAL_ERROR;
  }
}

zr_result zr_cancel_audio_preparation(zr_audio_prepare_handle handle) {
  if (!handle) return ZR_INVALID_ARGUMENT;
  const auto preparation = acquire_audio_preparation(handle);
  if (!preparation) return ZR_INVALID_ARGUMENT;
  preparation->cancellation.cancel();
  return ZR_OK;
}

zr_result zr_destroy_audio_preparation(zr_audio_prepare_handle handle) {
  if (!handle) return ZR_INVALID_ARGUMENT;
  std::shared_ptr<audio_preparation> preparation;
  {
    std::scoped_lock registry_lock(audio_preparation_registry_mutex);
    const auto found = audio_preparation_registry.find(handle);
    if (found == audio_preparation_registry.end()) return ZR_INVALID_ARGUMENT;
    preparation = found->second;
    std::scoped_lock preparation_lock(preparation->mutex);
    if (preparation->active && preparation->worker == std::this_thread::get_id()) return ZR_INVALID_STATE;
    audio_preparation_registry.erase(found);
  }
  preparation->cancellation.cancel();
  std::unique_lock lock(preparation->mutex);
  preparation->finished.wait(lock, [preparation] { return !preparation->active; });
  return ZR_OK;
}

zr_result zr_convert_audio_to_pcm_wav(
    const wchar_t* m4a_path,
    const wchar_t* wav_path,
    zr_pcm_convert_handle* out_handle) {
  if (!out_handle) return ZR_INVALID_ARGUMENT;
  *out_handle = nullptr;
  if (!m4a_path || !*m4a_path || !wav_path || !*wav_path) return ZR_INVALID_ARGUMENT;
  std::shared_ptr<pcm_conversion> conversion;
  try {
    conversion = std::make_shared<pcm_conversion>();
    conversion->worker = std::this_thread::get_id();
    const auto handle = register_pcm_conversion(conversion);
    InterlockedExchangePointer(reinterpret_cast<PVOID volatile*>(out_handle), handle);
    pcm_wav_converter converter(conversion->cancellation);
    const auto result = converter.convert(m4a_path, wav_path);
    {
      std::scoped_lock lock(conversion->mutex);
      conversion->active = false;
    }
    conversion->finished.notify_all();
    return map_pcm_result(result);
  } catch (...) {
    if (conversion) {
      {
        std::scoped_lock lock(conversion->mutex);
        conversion->active = false;
      }
      conversion->finished.notify_all();
    }
    return ZR_INTERNAL_ERROR;
  }
}

zr_result zr_cancel_pcm_conversion(zr_pcm_convert_handle handle) {
  if (!handle) return ZR_INVALID_ARGUMENT;
  const auto conversion = acquire_pcm_conversion(handle);
  if (!conversion) return ZR_INVALID_ARGUMENT;
  conversion->cancellation.cancel();
  return ZR_OK;
}

zr_result zr_destroy_pcm_conversion(zr_pcm_convert_handle handle) {
  if (!handle) return ZR_INVALID_ARGUMENT;
  std::shared_ptr<pcm_conversion> conversion;
  {
    std::scoped_lock registry_lock(pcm_conversion_registry_mutex);
    const auto found = pcm_conversion_registry.find(handle);
    if (found == pcm_conversion_registry.end()) return ZR_INVALID_ARGUMENT;
    conversion = found->second;
    std::scoped_lock conversion_lock(conversion->mutex);
    if (conversion->active && conversion->worker == std::this_thread::get_id()) return ZR_INVALID_STATE;
    pcm_conversion_registry.erase(found);
  }
  conversion->cancellation.cancel();
  std::unique_lock lock(conversion->mutex);
  conversion->finished.wait(lock, [conversion] { return !conversion->active; });
  return ZR_OK;
}
