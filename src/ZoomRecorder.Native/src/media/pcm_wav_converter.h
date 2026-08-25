#pragma once

#include <atomic>
#include <filesystem>
#include <functional>
#include <mutex>

enum class pcm_wav_conversion_result {
  success,
  invalid_argument,
  missing_audio,
  cancelled,
  media_failure,
  io_failure
};

enum class pcm_wav_publication_result {
  published,
  cancelled,
  failed
};

class pcm_wav_conversion_cancellation {
 public:
  void cancel() noexcept;
  bool is_cancelled() const noexcept;
  pcm_wav_publication_result publish(const std::function<bool()>& commit);

 private:
  std::atomic_bool cancelled_{};
  std::mutex publication_mutex_;
};

struct pcm_wav_converter_test_seam {
  std::function<void()> after_sample_written;
  std::function<void(const std::filesystem::path&, const std::filesystem::path&)> before_publish;
  std::function<void()> before_commit;
};

using pcm_wav_api_test_callback = void(__stdcall*)(void* handle, void* context);

struct pcm_wav_api_test_seam {
  pcm_wav_api_test_callback before_commit{};
  pcm_wav_api_test_callback after_cancel{};
  void* context{};
};

#ifdef ZOOMRECORDER_NATIVE_EXPORTS
#define ZR_PCM_WAV_TEST_API __declspec(dllexport)
#else
#define ZR_PCM_WAV_TEST_API __declspec(dllimport)
#endif
extern "C" ZR_PCM_WAV_TEST_API void zr_set_pcm_wav_api_test_seam(const pcm_wav_api_test_seam* seam);
#undef ZR_PCM_WAV_TEST_API

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
