#pragma once

#include <atomic>
#include <cstdint>
#include <filesystem>
#include <functional>
#include <string>

enum class audio_chunk_export_result {
  success,
  invalid_argument,
  missing_audio,
  cancelled,
  media_failure,
  io_failure
};

struct audio_chunk_export_record {
  std::uint32_t index{};
  std::filesystem::path path;
  std::int64_t start_milliseconds{};
  std::int64_t end_milliseconds{};
  std::string sha256;
  std::uint64_t byte_size{};
  std::uint32_t normalized_sample_rate{};
  std::uint32_t encoded_sample_rate{};
  std::uint32_t channel_count{};
};

class audio_chunk_cancellation {
 public:
  void cancel() noexcept;
  bool is_cancelled() const noexcept;

 private:
  std::atomic_bool cancelled_{};
};

class audio_chunk_exporter {
 public:
  using callback = std::function<void(const audio_chunk_export_record&)>;
  static constexpr std::uint64_t default_max_chunk_bytes = 24ull * 1024ull * 1024ull;

  explicit audio_chunk_exporter(audio_chunk_cancellation& cancellation) noexcept;
  audio_chunk_export_result export_chunks(
      const std::filesystem::path& mp4_path,
      const std::filesystem::path& output_directory,
      std::uint64_t max_chunk_bytes,
      callback on_chunk) const;

 private:
  audio_chunk_cancellation& cancellation_;
};
