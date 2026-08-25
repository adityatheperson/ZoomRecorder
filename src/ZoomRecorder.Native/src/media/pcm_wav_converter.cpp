#include "pcm_wav_converter.h"

#include <windows.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <wrl/client.h>

#include <algorithm>
#include <array>
#include <cstdint>
#include <limits>
#include <system_error>
#include <utility>

namespace {
using Microsoft::WRL::ComPtr;
namespace fs = std::filesystem;

constexpr std::uint32_t sample_rate = 16000;
constexpr std::uint16_t channel_count = 1;
constexpr std::uint16_t bits_per_sample = 16;
constexpr std::uint16_t block_alignment = channel_count * bits_per_sample / 8;
constexpr std::uint32_t bytes_per_second = sample_rate * block_alignment;
constexpr std::uint32_t wav_header_size = 44;

class mf_session {
 public:
  mf_session() : result_(MFStartup(MF_VERSION)) {}
  ~mf_session() { if (SUCCEEDED(result_)) MFShutdown(); }
  bool started() const noexcept { return SUCCEEDED(result_); }

 private:
  HRESULT result_;
};

class partial_file_guard {
 public:
  explicit partial_file_guard(fs::path path) : path_(std::move(path)) {}
  ~partial_file_guard() { if (active_) { std::error_code ignored; fs::remove(path_, ignored); } }
  void arm() noexcept { active_ = true; }
  void release() noexcept { active_ = false; }

 private:
  fs::path path_;
  bool active_{};
};

class wav_output_file {
 public:
  explicit wav_output_file(const fs::path& path)
      : handle_(CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr)) {}
  ~wav_output_file() { close(); }
  bool is_open() const noexcept { return handle_ != INVALID_HANDLE_VALUE; }
  bool write(const void* bytes, DWORD length) {
    DWORD written{};
    return is_open() && WriteFile(handle_, bytes, length, &written, nullptr) != FALSE && written == length;
  }
  bool seek_to_start() {
    LARGE_INTEGER offset{};
    return is_open() && SetFilePointerEx(handle_, offset, nullptr, FILE_BEGIN) != FALSE;
  }
  bool flush() { return is_open() && FlushFileBuffers(handle_) != FALSE; }
  bool close() {
    if (!is_open()) return true;
    const auto handle = handle_;
    handle_ = INVALID_HANDLE_VALUE;
    return CloseHandle(handle) != FALSE;
  }

 private:
  HANDLE handle_{INVALID_HANDLE_VALUE};
};

void set_uint16(std::array<unsigned char, wav_header_size>& header, size_t offset, std::uint16_t value) {
  header[offset] = static_cast<unsigned char>(value);
  header[offset + 1] = static_cast<unsigned char>(value >> 8);
}

void set_uint32(std::array<unsigned char, wav_header_size>& header, size_t offset, std::uint32_t value) {
  header[offset] = static_cast<unsigned char>(value);
  header[offset + 1] = static_cast<unsigned char>(value >> 8);
  header[offset + 2] = static_cast<unsigned char>(value >> 16);
  header[offset + 3] = static_cast<unsigned char>(value >> 24);
}

std::array<unsigned char, wav_header_size> make_header(std::uint32_t data_length) {
  std::array<unsigned char, wav_header_size> header{};
  std::copy_n(reinterpret_cast<const unsigned char*>("RIFF"), 4, header.begin());
  set_uint32(header, 4, data_length + wav_header_size - 8);
  std::copy_n(reinterpret_cast<const unsigned char*>("WAVEfmt "), 8, header.begin() + 8);
  set_uint32(header, 16, 16);
  set_uint16(header, 20, 1);
  set_uint16(header, 22, channel_count);
  set_uint32(header, 24, sample_rate);
  set_uint32(header, 28, bytes_per_second);
  set_uint16(header, 32, block_alignment);
  set_uint16(header, 34, bits_per_sample);
  std::copy_n(reinterpret_cast<const unsigned char*>("data"), 4, header.begin() + 36);
  set_uint32(header, 40, data_length);
  return header;
}

bool set_pcm_type(IMFMediaType* type) {
  return type &&
    SUCCEEDED(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio)) &&
    SUCCEEDED(type->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM)) &&
    SUCCEEDED(type->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, channel_count)) &&
    SUCCEEDED(type->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, sample_rate)) &&
    SUCCEEDED(type->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, bits_per_sample)) &&
    SUCCEEDED(type->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, block_alignment)) &&
    SUCCEEDED(type->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, bytes_per_second)) &&
    SUCCEEDED(type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE));
}

bool is_expected_pcm_type(IMFMediaType* type) {
  GUID major{}, subtype{};
  UINT32 channels{}, samples_per_second{}, sample_bits{}, alignment{};
  return type &&
    SUCCEEDED(type->GetGUID(MF_MT_MAJOR_TYPE, &major)) && major == MFMediaType_Audio &&
    SUCCEEDED(type->GetGUID(MF_MT_SUBTYPE, &subtype)) && subtype == MFAudioFormat_PCM &&
    SUCCEEDED(type->GetUINT32(MF_MT_AUDIO_NUM_CHANNELS, &channels)) && channels == channel_count &&
    SUCCEEDED(type->GetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, &samples_per_second)) && samples_per_second == sample_rate &&
    SUCCEEDED(type->GetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, &sample_bits)) && sample_bits == bits_per_sample &&
    SUCCEEDED(type->GetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, &alignment)) && alignment == block_alignment;
}

}

void pcm_wav_conversion_cancellation::cancel() noexcept {
  cancelled_.store(true, std::memory_order_release);
}

bool pcm_wav_conversion_cancellation::is_cancelled() const noexcept {
  return cancelled_.load(std::memory_order_acquire);
}

pcm_wav_converter::pcm_wav_converter(
    pcm_wav_conversion_cancellation& cancellation,
    const pcm_wav_converter_test_seam* test_seam) noexcept
    : cancellation_(cancellation), test_seam_(test_seam) {}

pcm_wav_conversion_result pcm_wav_converter::convert(
    const std::filesystem::path& m4a_path,
    const std::filesystem::path& wav_path) const {
  if (m4a_path.empty() || wav_path.empty() || !m4a_path.is_absolute() || !wav_path.is_absolute())
    return pcm_wav_conversion_result::invalid_argument;

  std::filesystem::path canonical_input, canonical_output;
  try {
    canonical_input = std::filesystem::canonical(m4a_path);
    const auto canonical_parent = std::filesystem::canonical(wav_path.parent_path());
    canonical_output = canonical_parent / wav_path.filename();
    if (!std::filesystem::is_regular_file(canonical_input) || !std::filesystem::is_directory(canonical_parent) ||
        canonical_input == canonical_output) return pcm_wav_conversion_result::invalid_argument;
  } catch (const std::filesystem::filesystem_error&) {
    return pcm_wav_conversion_result::invalid_argument;
  }

  std::error_code file_error;
  if (std::filesystem::exists(canonical_output, file_error) || file_error)
    return pcm_wav_conversion_result::io_failure;
  const std::filesystem::path partial_path(canonical_output.wstring() + L".partial");
  if (std::filesystem::exists(partial_path, file_error) || file_error)
    return pcm_wav_conversion_result::io_failure;
  if (cancellation_.is_cancelled()) return pcm_wav_conversion_result::cancelled;

  mf_session media_foundation;
  if (!media_foundation.started()) return pcm_wav_conversion_result::media_failure;
  ComPtr<IMFAttributes> attributes;
  if (FAILED(MFCreateAttributes(&attributes, 1)) ||
      FAILED(attributes->SetUINT32(MF_READWRITE_DISABLE_CONVERTERS, FALSE)))
    return pcm_wav_conversion_result::media_failure;
  ComPtr<IMFSourceReader> reader;
  if (FAILED(MFCreateSourceReaderFromURL(canonical_input.c_str(), attributes.Get(), &reader)))
    return pcm_wav_conversion_result::media_failure;
  ComPtr<IMFMediaType> native_type;
  const auto native_result = reader->GetNativeMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0, &native_type);
  if (native_result == MF_E_INVALIDSTREAMNUMBER || native_result == MF_E_NO_MORE_TYPES)
    return pcm_wav_conversion_result::missing_audio;
  if (FAILED(native_result) || FAILED(reader->SetStreamSelection(MF_SOURCE_READER_ALL_STREAMS, FALSE)) ||
      FAILED(reader->SetStreamSelection(MF_SOURCE_READER_FIRST_AUDIO_STREAM, TRUE)))
    return pcm_wav_conversion_result::media_failure;
  ComPtr<IMFMediaType> requested_type;
  if (FAILED(MFCreateMediaType(&requested_type)) || !set_pcm_type(requested_type.Get()) ||
      FAILED(reader->SetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, nullptr, requested_type.Get())))
    return pcm_wav_conversion_result::media_failure;
  ComPtr<IMFMediaType> current_type;
  if (FAILED(reader->GetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, &current_type)) ||
      !is_expected_pcm_type(current_type.Get())) return pcm_wav_conversion_result::media_failure;

  partial_file_guard partial_guard(partial_path);
  wav_output_file output(partial_path);
  if (!output.is_open()) return pcm_wav_conversion_result::io_failure;
  partial_guard.arm();
  const auto placeholder = make_header(0);
  if (!output.write(placeholder.data(), static_cast<DWORD>(placeholder.size())))
    return pcm_wav_conversion_result::io_failure;

  std::uint64_t data_length{};
  while (true) {
    if (cancellation_.is_cancelled()) return pcm_wav_conversion_result::cancelled;
    DWORD actual_stream{}, flags{};
    LONGLONG timestamp{};
    ComPtr<IMFSample> sample;
    const auto read = reader->ReadSample(
      MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0, &actual_stream, &flags, &timestamp, &sample);
    if (FAILED(read) || (flags & MF_SOURCE_READERF_ERROR)) return pcm_wav_conversion_result::media_failure;
    if (flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) {
      current_type.Reset();
      if (FAILED(reader->GetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, &current_type)) ||
          !is_expected_pcm_type(current_type.Get())) return pcm_wav_conversion_result::media_failure;
    }
    if (sample) {
      ComPtr<IMFMediaBuffer> buffer;
      if (FAILED(sample->ConvertToContiguousBuffer(&buffer))) return pcm_wav_conversion_result::media_failure;
      BYTE* bytes{};
      DWORD maximum{}, length{};
      if (FAILED(buffer->Lock(&bytes, &maximum, &length))) return pcm_wav_conversion_result::media_failure;
      constexpr auto maximum_data_length =
        static_cast<std::uint64_t>((std::numeric_limits<std::uint32_t>::max)()) - (wav_header_size - 8);
      const auto valid = length % block_alignment == 0 && data_length <= maximum_data_length &&
        length <= maximum_data_length - data_length;
      const auto written = length == 0 || (valid && output.write(bytes, length));
      const auto unlocked = buffer->Unlock();
      if (!valid || FAILED(unlocked)) return pcm_wav_conversion_result::media_failure;
      if (!written) return pcm_wav_conversion_result::io_failure;
      data_length += length;
      if (test_seam_ && test_seam_->after_sample_written) test_seam_->after_sample_written();
      if (cancellation_.is_cancelled()) return pcm_wav_conversion_result::cancelled;
    }
    if (flags & MF_SOURCE_READERF_ENDOFSTREAM) break;
  }
  if (data_length == 0) return pcm_wav_conversion_result::missing_audio;
  if (cancellation_.is_cancelled()) return pcm_wav_conversion_result::cancelled;

  const auto header = make_header(static_cast<std::uint32_t>(data_length));
  if (!output.seek_to_start() || !output.write(header.data(), static_cast<DWORD>(header.size())) ||
      !output.flush() || !output.close()) return pcm_wav_conversion_result::io_failure;
  if (cancellation_.is_cancelled()) return pcm_wav_conversion_result::cancelled;
  if (test_seam_ && test_seam_->before_publish) test_seam_->before_publish(partial_path, canonical_output);
  if (cancellation_.is_cancelled()) return pcm_wav_conversion_result::cancelled;
  if (std::filesystem::exists(canonical_output, file_error) || file_error ||
      !MoveFileExW(partial_path.c_str(), canonical_output.c_str(), MOVEFILE_WRITE_THROUGH))
    return pcm_wav_conversion_result::io_failure;
  partial_guard.release();
  return pcm_wav_conversion_result::success;
}
