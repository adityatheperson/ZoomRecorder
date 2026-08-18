#pragma once

class MeetingWindowWatchdog {
 public:
  bool observe(bool exists, bool visible);

 private:
  unsigned hidden_count_{};
  bool ended_{};
};
