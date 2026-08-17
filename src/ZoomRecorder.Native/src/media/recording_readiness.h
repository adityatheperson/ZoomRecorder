#pragma once

#include <cstdint>

enum class RecordingComponent : std::uint8_t { Video, MeetingAudio, Microphone, Encoder, OutputFile };

class RecordingReadiness {
 public:
  void ready(RecordingComponent component);
  void failed(RecordingComponent component);
  bool can_enter_meeting() const;
  bool has_failed() const;
 private:
  std::uint8_t ready_mask_{};
  std::uint8_t failed_mask_{};
};
