#include "capture_size_state.h"

bool run_capture_size_state_tests() {
  CaptureSizeState state;
  if (state.observe(800, 600)) return false;
  if (state.observe(800, 600)) return false;
  if (!state.observe(1600, 900)) return false;
  if (state.observe(0, 900)) return false;
  if (state.observe(1600, 0)) return false;
  return !state.observe(1600, 900);
}
