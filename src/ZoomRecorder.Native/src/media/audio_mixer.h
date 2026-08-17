#pragma once

#include <cstdint>
#include <span>
#include <vector>

struct AudioFormat { std::uint32_t sample_rate{48000}; std::uint16_t channels{2}; };

class AudioMixer {
 public:
  explicit AudioMixer(AudioFormat format);
  std::vector<float> mix(std::span<const float> meeting, std::span<const float> microphone) const;
 private:
  AudioFormat format_;
};
