#include "meeting_window_watchdog.h"

bool MeetingWindowWatchdog::observe(bool exists, bool visible) {
  if (ended_) return false;
  if (!exists) {
    ended_ = true;
    return true;
  }
  if (visible) {
    hidden_count_ = 0;
    return false;
  }
  if (++hidden_count_ < 4) return false;
  ended_ = true;
  return true;
}
