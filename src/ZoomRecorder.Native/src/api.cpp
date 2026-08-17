#define ZOOMRECORDER_NATIVE_EXPORTS
#include "zoom_recorder.h"

#include <memory>
#include <mutex>
#include <string>
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

void emit(session& value, const char* json) {
  if (value.callback) value.callback(json, value.context);
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
    });
#ifdef ZR_WITH_ZOOM
    value->zoom = std::make_unique<ZoomMeetingClient>([raw](const char* json) { emit(*raw, json); });
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
  if (!value.meeting_host || !value.pipeline->start(value.meeting_host, output_path)) return ZR_INTERNAL_ERROR;
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
