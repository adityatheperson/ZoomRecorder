#include "capture_size_state.h"

bool CaptureSizeState::needs_recreate(UINT width, UINT height) const {
  if (!width || !height) return false;
  return width_ && height_ && (width != width_ || height != height_);
}

void CaptureSizeState::commit(UINT width, UINT height) {
  if (!width || !height) return;
  width_ = width;
  height_ = height;
}
