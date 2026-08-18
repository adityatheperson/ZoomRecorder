#pragma once

#include <cstddef>
#include <cstdint>

struct AudioSampleTiming {
  std::int64_t timestamp;
  std::int64_t duration;
};

class AudioSampleTimeline {
 public:
  AudioSampleTimeline(unsigned sample_rate, unsigned channels);
  AudioSampleTiming next(std::size_t interleaved_sample_count);

 private:
  unsigned sample_rate_;
  unsigned channels_;
  std::int64_t next_timestamp_{};
};
