#pragma once

#include <atomic>
#include <filesystem>
#include <functional>

enum class pcm_wav_conversion_result {
  success,
  invalid_argument,
  missing_audio,
  cancelled,
  media_failure,
  io_failure
};

class pcm_wav_conversion_cancellation {
 public:
  void cancel() noexcept;
  bool is_cancelled() const noexcept;

 private:
  std::atomic_bool cancelled_{};
};

struct pcm_wav_converter_test_seam {
  std::function<void()> after_sample_written;
  std::function<void(const std::filesystem::path&, const std::filesystem::path&)> before_publish;
};

class pcm_wav_converter {
 public:
  explicit pcm_wav_converter(
      pcm_wav_conversion_cancellation& cancellation,
      const pcm_wav_converter_test_seam* test_seam = nullptr) noexcept;
  pcm_wav_conversion_result convert(
      const std::filesystem::path& m4a_path,
      const std::filesystem::path& wav_path) const;

 private:
  pcm_wav_conversion_cancellation& cancellation_;
  const pcm_wav_converter_test_seam* test_seam_;
};
