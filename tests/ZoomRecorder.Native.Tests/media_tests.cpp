#include "audio_mixer.h"
#include "recording_readiness.h"
#include <cmath>
#include <vector>

bool run_media_tests() {
  AudioMixer mixer({48000, 2});
  const std::vector<float> meeting{0.5f, 1.0f, 0.5f};
  const std::vector<float> microphone{0.5f, 1.0f};
  const auto mixed = mixer.mix(meeting, microphone);
  if (mixed.size() != 3 || std::abs(mixed[0] - 0.75f) > 0.001f || mixed[1] != 1.0f) return false;

  RecordingReadiness readiness;
  readiness.ready(RecordingComponent::Video);
  readiness.ready(RecordingComponent::MeetingAudio);
  readiness.ready(RecordingComponent::Microphone);
  readiness.ready(RecordingComponent::Encoder);
  if (readiness.can_enter_meeting()) return false;
  readiness.ready(RecordingComponent::OutputFile);
  if (!readiness.can_enter_meeting()) return false;
  readiness.failed(RecordingComponent::Microphone);
  return readiness.has_failed() && !readiness.can_enter_meeting();
}
