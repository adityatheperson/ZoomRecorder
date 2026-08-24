#include "aspect_fit.h"

AspectFitRect calculate_aspect_fit(UINT source_width, UINT source_height, UINT target_width, UINT target_height) {
  if (!source_width || !source_height || !target_width || !target_height) return {};
  UINT width{}, height{};
  if (static_cast<unsigned long long>(source_width) * target_height >
      static_cast<unsigned long long>(target_width) * source_height) {
    width = target_width;
    height = static_cast<UINT>(static_cast<unsigned long long>(target_width) * source_height / source_width);
  } else {
    height = target_height;
    width = static_cast<UINT>(static_cast<unsigned long long>(target_height) * source_width / source_height);
  }
  width &= ~1u;
  height &= ~1u;
  const auto left = ((target_width - width) / 2) & ~1u;
  const auto top = ((target_height - height) / 2) & ~1u;
  return {left, top, width, height};
}
