#include "meeting_window_watchdog.h"

bool run_meeting_window_watchdog_tests() {
  MeetingWindowWatchdog hidden;
  const auto hidden_grace = !hidden.observe(true, false) &&
                            !hidden.observe(true, false) &&
                            !hidden.observe(true, false) &&
                            hidden.observe(true, false) &&
                            !hidden.observe(true, false);

  MeetingWindowWatchdog reset;
  const auto visible_resets = !reset.observe(true, false) &&
                              !reset.observe(true, false) &&
                              !reset.observe(true, true) &&
                              !reset.observe(true, false) &&
                              !reset.observe(true, false) &&
                              !reset.observe(true, false) &&
                              reset.observe(true, false);

  MeetingWindowWatchdog destroyed;
  const auto destruction_ends_once = destroyed.observe(false, false) &&
                                     !destroyed.observe(false, false);

  return hidden_grace && visible_resets && destruction_ends_once;
}
