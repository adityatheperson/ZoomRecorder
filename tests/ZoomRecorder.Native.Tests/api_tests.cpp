#include "zoom_recorder.h"
#include <cstdlib>

int main() {
  zr_handle handle{};
  if (zr_create(&handle) != ZR_OK || !handle) return EXIT_FAILURE;
  if (zr_enter_meeting(handle) != ZR_INVALID_STATE) return EXIT_FAILURE;
  if (zr_prepare_meeting(handle, R"({"meetingId":"1234567890"})") != ZR_OK) return EXIT_FAILURE;
  if (zr_start_recording(handle, L"recording.mp4") != ZR_OK) return EXIT_FAILURE;
  if (zr_enter_meeting(handle) != ZR_OK) return EXIT_FAILURE;
  if (zr_finalize_recording(handle) != ZR_OK) return EXIT_FAILURE;
  if (zr_finalize_recording(handle) != ZR_OK) return EXIT_FAILURE;
  return zr_destroy(handle) == ZR_OK ? EXIT_SUCCESS : EXIT_FAILURE;
}
