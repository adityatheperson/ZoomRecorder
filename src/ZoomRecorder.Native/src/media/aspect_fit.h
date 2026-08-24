#pragma once

#include <windows.h>

struct AspectFitRect {
  UINT left{};
  UINT top{};
  UINT width{};
  UINT height{};
};

AspectFitRect calculate_aspect_fit(UINT source_width, UINT source_height, UINT target_width, UINT target_height);
