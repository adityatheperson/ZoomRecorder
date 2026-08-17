#pragma once

#ifdef ZOOMRECORDER_NATIVE_EXPORTS
#define ZR_API __declspec(dllexport)
#else
#define ZR_API __declspec(dllimport)
#endif

extern "C" {
typedef void* zr_handle;
typedef void(__stdcall* zr_event_callback)(const char* json, void* context);

enum zr_result { ZR_OK = 0, ZR_INVALID_ARGUMENT = 1, ZR_INVALID_STATE = 2, ZR_INTERNAL_ERROR = 3 };

ZR_API zr_result zr_create(zr_handle* out_handle);
ZR_API zr_result zr_destroy(zr_handle handle);
ZR_API zr_result zr_set_event_callback(zr_handle handle, zr_event_callback callback, void* context);
ZR_API zr_result zr_prepare_meeting(zr_handle handle, const char* request_json);
ZR_API zr_result zr_start_recording(zr_handle handle, const wchar_t* output_path);
ZR_API zr_result zr_enter_meeting(zr_handle handle);
ZR_API zr_result zr_finalize_recording(zr_handle handle);
}
