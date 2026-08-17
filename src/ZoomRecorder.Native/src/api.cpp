#define ZOOMRECORDER_NATIVE_EXPORTS
#include "zoom_recorder.h"

#include <memory>
#include <mutex>
#include <string>

namespace {
struct session {
  std::mutex mutex;
  zr_event_callback callback{};
  void* context{};
  bool prepared{};
  bool recording{};
  bool entered{};
};

void emit(session& value, const char* json) {
  if (value.callback) value.callback(json, value.context);
}
}

zr_result zr_create(zr_handle* out_handle) {
  if (!out_handle) return ZR_INVALID_ARGUMENT;
  try { *out_handle = new session{}; return ZR_OK; }
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

zr_result zr_prepare_meeting(zr_handle handle, const char* request_json) {
  if (!handle || !request_json || !*request_json) return ZR_INVALID_ARGUMENT;
  auto& value = *static_cast<session*>(handle);
  std::scoped_lock lock(value.mutex);
  if (value.prepared) return ZR_INVALID_STATE;
  value.prepared = true; emit(value, R"({"type":"meeting_prepared"})");
  return ZR_OK;
}

zr_result zr_start_recording(zr_handle handle, const wchar_t* output_path) {
  if (!handle || !output_path || !*output_path) return ZR_INVALID_ARGUMENT;
  auto& value = *static_cast<session*>(handle);
  std::scoped_lock lock(value.mutex);
  if (!value.prepared || value.recording) return ZR_INVALID_STATE;
  value.recording = true; emit(value, R"({"type":"recording_started"})");
  return ZR_OK;
}

zr_result zr_enter_meeting(zr_handle handle) {
  if (!handle) return ZR_INVALID_ARGUMENT;
  auto& value = *static_cast<session*>(handle);
  std::scoped_lock lock(value.mutex);
  if (!value.recording || value.entered) return ZR_INVALID_STATE;
  value.entered = true; emit(value, R"({"type":"meeting_entered"})");
  return ZR_OK;
}

zr_result zr_finalize_recording(zr_handle handle) {
  if (!handle) return ZR_INVALID_ARGUMENT;
  auto& value = *static_cast<session*>(handle);
  std::scoped_lock lock(value.mutex);
  if (!value.recording) return ZR_OK;
  value.recording = false; emit(value, R"({"type":"recording_finalized"})");
  return ZR_OK;
}
