#include "audio_sample_timeline.h"

AudioSampleTimeline::AudioSampleTimeline(unsigned sample_rate, unsigned channels)
    : sample_rate_(sample_rate), channels_(channels) {}

AudioSampleTiming AudioSampleTimeline::next(std::size_t interleaved_sample_count) {
  const auto frames = channels_ == 0 ? 0 : interleaved_sample_count / channels_;
  const auto duration = sample_rate_ == 0
      ? 0
      : static_cast<std::int64_t>(frames) * 10'000'000LL / sample_rate_;
  const AudioSampleTiming timing{next_timestamp_, duration};
  next_timestamp_ += duration;
  return timing;
}
