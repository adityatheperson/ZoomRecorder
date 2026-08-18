#define ZOOMRECORDER_NATIVE_EXPORTS
#include "zoom_recorder.h"

#include <memory>
#include <mutex>
#include <condition_variable>
#include <string>
#include <thread>
#include "media/audio_chunk_exporter.h"
#include "media/recording_pipeline.h"
#ifdef ZR_WITH_ZOOM
#include "zoom/zoom_meeting_client.h"
#endif

namespace {
struct session {
  std::mutex mutex;
  zr_event_callback callback{};
  void* context{};
  bool prepared{};
  bool recording_started{};
  bool entered{};
  HWND meeting_host{};
  std::unique_ptr<RecordingPipeline> pipeline;
#ifdef ZR_WITH_ZOOM
  std::unique_ptr<ZoomMeetingClient> zoom;
#endif
};

struct audio_preparation {
  std::mutex mutex;
  std::condition_variable finished;
  audio_chunk_cancellation cancellation;
  bool active{true};
  std::thread::id worker;
};

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
}

zr_result zr_create(zr_handle* out_handle) {
  if (!out_handle) return ZR_INVALID_ARGUMENT;
  try {
    auto value = std::make_unique<session>();
    auto* raw = value.get();
    value->pipeline = std::make_unique<RecordingPipeline>([raw](bool ok, const char* message) {
      const std::string event = std::string{"{\"type\":\""} + (ok ? "component_ready" : "failed") + "\",\"message\":\"" + message + "\"}";
      emit(*raw, event.c_str());
    }, [raw] { emit(*raw, R"({"type":"capture_ended"})"); });
#ifdef ZR_WITH_ZOOM
    value->zoom = std::make_unique<ZoomMeetingClient>(
      [raw](const char* json) { emit(*raw, json); },
      [raw](HWND window) { if (raw->pipeline) raw->pipeline->attach_video(window); });
#endif
    *out_handle = value.release(); return ZR_OK;
  }
  catch (...) { *out_handle = nullptr; return ZR_INTERNAL_ERROR; }
}

zr_result zr_set_meeting_host(zr_handle handle, intptr_t window_handle) {
  if (!handle || !window_handle || !IsWindow(reinterpret_cast<HWND>(window_handle))) return ZR_INVALID_ARGUMENT;
  auto& value = *static_cast<session*>(handle);
  std::scoped_lock lock(value.mutex);
  value.meeting_host = reinterpret_cast<HWND>(window_handle);
#ifdef ZR_WITH_ZOOM
  value.zoom->set_host(value.meeting_host);
#endif
  return ZR_OK;
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

zr_result zr_prepare_meeting(zr_handle handle, const char* request_json) {
  if (!handle || !request_json || !*request_json) return ZR_INVALID_ARGUMENT;
  auto& value = *static_cast<session*>(handle);
  std::scoped_lock lock(value.mutex);
  if (value.prepared && !value.entered) return ZR_OK;
  if (value.prepared) return ZR_INVALID_STATE;
#ifdef ZR_WITH_ZOOM
  const auto result = value.zoom->prepare(request_json);
  if (result != 0) return static_cast<zr_result>(result);
#endif
  value.prepared = true; emit(value, R"({"type":"meeting_prepared"})");
  return ZR_OK;
}

zr_result zr_start_recording(zr_handle handle, const wchar_t* output_path) {
  if (!handle || !output_path || !*output_path) return ZR_INVALID_ARGUMENT;
  auto& value = *static_cast<session*>(handle);
  std::scoped_lock lock(value.mutex);
  if (!value.prepared || value.recording_started) return ZR_INVALID_STATE;
  if (!value.pipeline->start(output_path)) return ZR_INTERNAL_ERROR;
  value.recording_started = true; emit(value, R"({"type":"recording_started"})");
  return ZR_OK;
}

zr_result zr_enter_meeting(zr_handle handle) {
  if (!handle) return ZR_INVALID_ARGUMENT;
  auto& value = *static_cast<session*>(handle);
  std::scoped_lock lock(value.mutex);
  if (!value.recording_started || value.entered) return ZR_INVALID_STATE;
#ifdef ZR_WITH_ZOOM
  const auto result = value.zoom->enter();
  if (result != 0) return static_cast<zr_result>(result);
#endif
  value.entered = true; emit(value, R"({"type":"meeting_entered"})");
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
  try {
    auto preparation = std::make_unique<audio_preparation>();
    preparation->worker = std::this_thread::get_id();
    auto* raw = preparation.release();
    *out_handle = raw;
    audio_chunk_exporter exporter(raw->cancellation);
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
      std::scoped_lock lock(raw->mutex);
      raw->active = false;
    }
    raw->finished.notify_all();
    return map_audio_result(result);
  } catch (...) {
    auto* raw = static_cast<audio_preparation*>(*out_handle);
    if (raw) {
      {
        std::scoped_lock lock(raw->mutex);
        raw->active = false;
      }
      raw->finished.notify_all();
    }
    return ZR_INTERNAL_ERROR;
  }
}

zr_result zr_cancel_audio_preparation(zr_audio_prepare_handle handle) {
  if (!handle) return ZR_INVALID_ARGUMENT;
  static_cast<audio_preparation*>(handle)->cancellation.cancel();
  return ZR_OK;
}

zr_result zr_destroy_audio_preparation(zr_audio_prepare_handle handle) {
  if (!handle) return ZR_INVALID_ARGUMENT;
  auto* preparation = static_cast<audio_preparation*>(handle);
  preparation->cancellation.cancel();
  std::unique_lock lock(preparation->mutex);
  if (preparation->active && preparation->worker == std::this_thread::get_id()) return ZR_INVALID_STATE;
  preparation->finished.wait(lock, [preparation] { return !preparation->active; });
  lock.unlock();
  delete preparation;
  return ZR_OK;
}
