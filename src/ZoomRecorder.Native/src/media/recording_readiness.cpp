#include "recording_readiness.h"

namespace {
constexpr std::uint8_t bit(RecordingComponent component) { return 1u << static_cast<std::uint8_t>(component); }
constexpr std::uint8_t all_ready = (1u << 5u) - 1u;
}

void RecordingReadiness::ready(RecordingComponent component) {
  const auto value = bit(component);
  if ((failed_mask_ & value) == 0) ready_mask_ |= value;
}

void RecordingReadiness::failed(RecordingComponent component) {
  const auto value = bit(component);
  failed_mask_ |= value;
  ready_mask_ &= static_cast<std::uint8_t>(~value);
}

bool RecordingReadiness::can_enter_meeting() const { return failed_mask_ == 0 && ready_mask_ == all_ready; }
bool RecordingReadiness::has_failed() const { return failed_mask_ != 0; }
