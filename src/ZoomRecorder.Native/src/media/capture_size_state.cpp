#include "capture_size_state.h"

bool CaptureSizeState::observe(UINT width, UINT height) {
  if (!width || !height) return false;
  if (!width_ || !height_) {
    width_ = width;
    height_ = height;
    return false;
  }
  if (width == width_ && height == height_) return false;
  width_ = width;
  height_ = height;
  return true;
}
