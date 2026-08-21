#include "audio_mixer.h"
#include "audio_sample_timeline.h"
#include "recording_readiness.h"
#include "wasapi_source.h"
#include <cmath>
#include <vector>

bool run_media_tests() {
  if (wasapi_stage_is_ready(WasapiStartupStage::ThreadStarted)) return false;
  if (!wasapi_stage_is_ready(WasapiStartupStage::ClientStarted)) return false;
  if (!wasapi_stage_is_ready(WasapiStartupStage::FirstPacket)) return false;

  AudioSampleTimeline timeline(48000, 2);
  const auto first = timeline.next(960);
  const auto second = timeline.next(480);
  if (first.timestamp != 0 || first.duration != 100000 ||
      second.timestamp != 100000 || second.duration != 50000) return false;

  AudioMixer mixer({48000, 2});
  const std::vector<float> meeting{0.5f, 1.0f, 0.5f};
  const std::vector<float> microphone{0.5f, 1.0f};
  const auto mixed = mixer.mix(meeting, microphone);
  if (mixed.size() != 3 || std::abs(mixed[0] - 0.75f) > 0.001f || mixed[1] != 1.0f) return false;

  RecordingReadiness readiness;
  readiness.ready(RecordingComponent::MeetingAudio);
  readiness.ready(RecordingComponent::Microphone);
  readiness.ready(RecordingComponent::Encoder);
  readiness.ready(RecordingComponent::OutputFile);
  if (!readiness.can_enter_before_video()) return false;
  if (readiness.can_enter_meeting()) return false;
  readiness.ready(RecordingComponent::Video);
  if (!readiness.can_enter_meeting()) return false;
  readiness.failed(RecordingComponent::Microphone);
  return readiness.has_failed() && !readiness.can_enter_meeting();
}
