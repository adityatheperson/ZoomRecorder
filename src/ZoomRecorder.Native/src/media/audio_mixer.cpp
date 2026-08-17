#include "audio_mixer.h"
#include <algorithm>
#include <cmath>
#include <stdexcept>

AudioMixer::AudioMixer(AudioFormat format) : format_(format) {
  if (format_.sample_rate == 0 || format_.channels == 0) throw std::invalid_argument("Invalid audio format");
}

std::vector<float> AudioMixer::mix(std::span<const float> meeting, std::span<const float> microphone) const {
  const auto count = std::max(meeting.size(), microphone.size());
  std::vector<float> result(count);
  for (std::size_t index = 0; index < count; ++index) {
    const auto a = index < meeting.size() ? meeting[index] : 0.0f;
    const auto b = index < microphone.size() ? microphone[index] : 0.0f;
    result[index] = std::clamp((a * 0.75f) + (b * 0.75f), -1.0f, 1.0f);
  }
  return result;
}
