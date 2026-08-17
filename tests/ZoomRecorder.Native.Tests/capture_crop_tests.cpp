#include "capture_crop.h"

bool run_capture_crop_tests() {
  CaptureCrop crop{};
  const RECT root{100, 100, 1500, 1000};
  const RECT target{120, 150, 1320, 830};
  if (!calculate_capture_crop(target, root, 1400, 900, crop)) return false;
  if (crop.left != 20 || crop.top != 50 || crop.width != 1200 || crop.height != 680) return false;

  const RECT partially_outside{50, 50, 300, 300};
  if (!calculate_capture_crop(partially_outside, root, 1400, 900, crop)) return false;
  return crop.left == 0 && crop.top == 0 && crop.width == 200 && crop.height == 200;
}
