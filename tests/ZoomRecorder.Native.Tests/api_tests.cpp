#include "zoom_recorder.h"
#include <cstdlib>

bool run_zoom_event_mapper_tests();
bool run_media_tests();
bool run_mp4_writer_tests();
bool run_capture_crop_tests();

int main() {
  if (!run_zoom_event_mapper_tests()) return EXIT_FAILURE;
  if (!run_media_tests()) return EXIT_FAILURE;
  if (!run_mp4_writer_tests()) return EXIT_FAILURE;
  if (!run_capture_crop_tests()) return EXIT_FAILURE;
  zr_handle handle{};
  if (zr_create(&handle) != ZR_OK || !handle) return EXIT_FAILURE;
  if (zr_enter_meeting(handle) != ZR_INVALID_STATE) return EXIT_FAILURE;
  if (zr_prepare_meeting(handle, R"({"meetingId":"1234567890"})") != ZR_OK) return EXIT_FAILURE;
  if (zr_prepare_meeting(handle, R"({"meetingId":"1234567890"})") != ZR_OK) return EXIT_FAILURE;
  if (zr_enter_meeting(handle) != ZR_INVALID_STATE) return EXIT_FAILURE;
  return zr_destroy(handle) == ZR_OK ? EXIT_SUCCESS : EXIT_FAILURE;
}
