#include "zoom_recorder.h"
#include <cstdlib>
#include <windows.h>

bool run_media_tests();
bool run_mp4_writer_tests();
bool run_capture_crop_tests();
bool run_meeting_window_watchdog_tests();
bool run_audio_chunk_exporter_tests();
bool run_aspect_fit_tests();

int main() {
  if (!run_media_tests()) return EXIT_FAILURE;
  if (!run_mp4_writer_tests()) return EXIT_FAILURE;
  if (!run_capture_crop_tests()) return EXIT_FAILURE;
  if (!run_meeting_window_watchdog_tests()) return EXIT_FAILURE;
  if (!run_audio_chunk_exporter_tests()) return EXIT_FAILURE;
  if (!run_aspect_fit_tests()) return EXIT_FAILURE;
  zr_handle handle{};
  if (zr_create(&handle) != ZR_OK || !handle) return EXIT_FAILURE;
  if (zr_attach_recording_window(nullptr, 1) != ZR_INVALID_ARGUMENT) return EXIT_FAILURE;
  if (zr_attach_recording_window(handle, 0) != ZR_INVALID_ARGUMENT) return EXIT_FAILURE;
  if (zr_attach_recording_window(handle, reinterpret_cast<intptr_t>(GetDesktopWindow())) != ZR_INVALID_STATE) return EXIT_FAILURE;
  if (zr_start_recording(handle, L"test.mp4", 0) != ZR_INVALID_ARGUMENT) return EXIT_FAILURE;
  if (zr_finalize_recording(handle) != ZR_OK) return EXIT_FAILURE;
  return zr_destroy(handle) == ZR_OK ? EXIT_SUCCESS : EXIT_FAILURE;
}
