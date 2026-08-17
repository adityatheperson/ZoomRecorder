#pragma once
#include <windows.h>

struct CaptureCrop {
  UINT left{};
  UINT top{};
  UINT width{};
  UINT height{};
};

bool calculate_capture_crop(const RECT& target, const RECT& captured_window, UINT texture_width,
                            UINT texture_height, CaptureCrop& crop);
