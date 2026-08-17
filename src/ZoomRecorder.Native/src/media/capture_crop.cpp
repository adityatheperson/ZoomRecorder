#include "capture_crop.h"
#include <algorithm>

bool calculate_capture_crop(const RECT& target, const RECT& captured_window, UINT texture_width,
                            UINT texture_height, CaptureCrop& crop) {
  const auto left = (std::max)(target.left, captured_window.left) - captured_window.left;
  const auto top = (std::max)(target.top, captured_window.top) - captured_window.top;
  const auto right = (std::min)(target.right, captured_window.left + static_cast<LONG>(texture_width)) - captured_window.left;
  const auto bottom = (std::min)(target.bottom, captured_window.top + static_cast<LONG>(texture_height)) - captured_window.top;
  if (left < 0 || top < 0 || right <= left || bottom <= top) return false;
  crop = {static_cast<UINT>(left), static_cast<UINT>(top), static_cast<UINT>(right - left), static_cast<UINT>(bottom - top)};
  return true;
}
