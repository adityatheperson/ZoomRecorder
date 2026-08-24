#include "capture_size_state.h"

bool run_capture_size_state_tests() {
  CaptureSizeState state;
  if (state.needs_recreate(800, 600)) return false;
  state.commit(800, 600);
  if (state.needs_recreate(800, 600)) return false;
  if (!state.needs_recreate(1600, 900)) return false;
  if (!state.needs_recreate(1600, 900)) return false;
  if (state.needs_recreate(0, 900)) return false;
  if (state.needs_recreate(1600, 0)) return false;
  state.commit(1600, 900);
  return !state.needs_recreate(1600, 900);
}
