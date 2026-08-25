#include "audio_chunk_exporter.h"
#include "mp4_writer.h"
#include "pcm_wav_converter.h"
#include "zoom_recorder.h"

#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>

#include <array>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

namespace {
using Microsoft::WRL::ComPtr;
namespace fs = std::filesystem;

struct temporary_directory {
  temporary_directory() {
    path = fs::temp_directory_path() /
      (L"zoom-recorder-pcm-wav-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()));
    fs::create_directories(path);
  }
  ~temporary_directory() { std::error_code ignored; fs::remove_all(path, ignored); }
  fs::path path;
};

bool expect(bool condition, const char* message) {
  if (!condition) std::fprintf(stderr, "pcm wav converter test failed: %s\n", message);
  return condition;
}

bool create_fixture(const fs::path& path, int duration_seconds, bool write_audio) {
  Mp4Writer writer;
  if (!writer.open(path.wstring(), 320, 180, 10)) return false;
  auto* device = writer.device();
  D3D11_TEXTURE2D_DESC description{};
  description.Width = 320; description.Height = 180; description.MipLevels = 1; description.ArraySize = 1;
  description.Format = DXGI_FORMAT_B8G8R8A8_UNORM; description.SampleDesc.Count = 1;
  description.Usage = D3D11_USAGE_DEFAULT; description.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
  ComPtr<ID3D11Texture2D> texture;
  if (!device || FAILED(device->CreateTexture2D(&description, nullptr, &texture)) || !writer.write_video(texture.Get(), 0)) return false;
  if (write_audio) {
    constexpr size_t frames_per_sample = 960;
    std::vector<float> sample(frames_per_sample * 2);
    for (int packet = 0; packet < duration_seconds * 50; ++packet) {
      for (size_t frame = 0; frame < frames_per_sample; ++frame) {
        const auto absolute = static_cast<double>(packet * frames_per_sample + frame);
        const auto value = static_cast<float>(std::sin(absolute * 440.0 * 2.0 * 3.141592653589793 / 48000.0) * 0.1);
        sample[frame * 2] = value; sample[frame * 2 + 1] = value;
      }
      if (!writer.write_audio(sample, packet * 200000LL)) return false;
    }
  }
  return writer.finalize();
}

fs::path create_m4a_checkpoint(const fs::path& source, const fs::path& output) {
  audio_chunk_cancellation cancellation;
  audio_chunk_exporter exporter(cancellation);
  std::vector<audio_chunk_export_record> chunks;
  if (exporter.export_chunks(source, output, audio_chunk_exporter::default_max_chunk_bytes,
      [&](const auto& chunk) { chunks.push_back(chunk); }) != audio_chunk_export_result::success || chunks.size() != 1)
    return {};
  return chunks.front().path;
}

std::uint16_t uint16_at(const std::array<unsigned char, 44>& header, size_t offset) {
  return static_cast<std::uint16_t>(header[offset]) |
    static_cast<std::uint16_t>(header[offset + 1]) << 8;
}

std::uint32_t uint32_at(const std::array<unsigned char, 44>& header, size_t offset) {
  return static_cast<std::uint32_t>(header[offset]) |
    static_cast<std::uint32_t>(header[offset + 1]) << 8 |
    static_cast<std::uint32_t>(header[offset + 2]) << 16 |
    static_cast<std::uint32_t>(header[offset + 3]) << 24;
}

bool validate_pcm_wav(const fs::path& path) {
  std::ifstream input(path, std::ios::binary);
  std::array<unsigned char, 44> header{};
  input.read(reinterpret_cast<char*>(header.data()), header.size());
  if (!input || std::string(reinterpret_cast<const char*>(header.data()), 4) != "RIFF" ||
      std::string(reinterpret_cast<const char*>(header.data() + 8), 4) != "WAVE" ||
      std::string(reinterpret_cast<const char*>(header.data() + 12), 4) != "fmt " ||
      std::string(reinterpret_cast<const char*>(header.data() + 36), 4) != "data") return false;
  const auto file_size = fs::file_size(path);
  const auto data_length = uint32_at(header, 40);
  return uint32_at(header, 4) == file_size - 8 && uint32_at(header, 16) == 16 &&
    uint16_at(header, 20) == 1 && uint16_at(header, 22) == 1 &&
    uint32_at(header, 24) == 16000 && uint32_at(header, 28) == 32000 &&
    uint16_at(header, 32) == 2 && uint16_at(header, 34) == 16 &&
    data_length > 0 && data_length == file_size - 44 && data_length % 2 == 0;
}
}

bool run_pcm_wav_converter_tests() {
  temporary_directory temporary;
  const auto source_mp4 = temporary.path / L"source.mp4";
  const auto silent_mp4 = temporary.path / L"silent.mp4";
  const auto checkpoints = temporary.path / L"checkpoints";
  fs::create_directories(checkpoints);
  if (!expect(create_fixture(source_mp4, 3, true), "synthetic audio fixture is created") ||
      !expect(create_fixture(silent_mp4, 1, false), "synthetic missing-audio fixture is created")) return false;
  const auto checkpoint = create_m4a_checkpoint(source_mp4, checkpoints);
  if (!expect(!checkpoint.empty() && fs::exists(checkpoint), "validated M4A checkpoint is created")) return false;

  const auto wav_path = checkpoints / L"converted.wav";
  bool observed_partial_publication{};
  pcm_wav_converter_test_seam publication_seam;
  publication_seam.before_publish = [&](const fs::path& partial, const fs::path& final) {
    observed_partial_publication = partial == fs::path(wav_path.wstring() + L".partial") &&
      final == wav_path && fs::exists(partial) && !fs::exists(final);
  };
  pcm_wav_conversion_cancellation success_cancellation;
  pcm_wav_converter converter(success_cancellation, &publication_seam);
  if (!expect(converter.convert(checkpoint, wav_path) == pcm_wav_conversion_result::success,
        "valid M4A converts successfully") ||
      !expect(observed_partial_publication, "complete WAV is staged at the exact .partial path before publication") ||
      !expect(fs::exists(wav_path) && !fs::exists(wav_path.wstring() + L".partial"),
        "rename publishes only the final WAV") ||
      !expect(validate_pcm_wav(wav_path), "published WAV has a correct mono 16 kHz 16-bit PCM header and data length")) return false;

  const auto cancelled_wav = checkpoints / L"cancelled.wav";
  pcm_wav_conversion_cancellation cancelled;
  pcm_wav_converter_test_seam cancellation_seam;
  cancellation_seam.after_sample_written = [&] { cancelled.cancel(); };
  pcm_wav_converter cancelling_converter(cancelled, &cancellation_seam);
  if (!expect(cancelling_converter.convert(checkpoint, cancelled_wav) == pcm_wav_conversion_result::cancelled,
        "cancellation between decoded samples is reported") ||
      !expect(!fs::exists(cancelled_wav) && !fs::exists(cancelled_wav.wstring() + L".partial"),
        "cancellation publishes neither final nor partial WAV")) return false;

  pcm_wav_conversion_cancellation failure_cancellation;
  pcm_wav_converter failure_converter(failure_cancellation);
  const auto missing_wav = checkpoints / L"missing.wav";
  const auto corrupt_m4a = checkpoints / L"corrupt.m4a";
  { std::ofstream corrupt(corrupt_m4a, std::ios::binary); corrupt << "not an m4a"; }
  if (!expect(failure_converter.convert(temporary.path / L"missing.m4a", missing_wav) ==
        pcm_wav_conversion_result::invalid_argument, "missing source is rejected") ||
      !expect(failure_converter.convert(silent_mp4, missing_wav) == pcm_wav_conversion_result::missing_audio,
        "missing audio stream is stable") ||
      !expect(failure_converter.convert(corrupt_m4a, missing_wav) == pcm_wav_conversion_result::media_failure,
        "corrupt media is a decode failure")) return false;

  const auto collision_wav = checkpoints / L"collision.wav";
  { std::ofstream existing(collision_wav, std::ios::binary); existing << "preserve"; }
  if (!expect(failure_converter.convert(checkpoint, collision_wav) == pcm_wav_conversion_result::io_failure,
        "existing output collision is rejected") ||
      !expect(fs::file_size(collision_wav) == 8 && !fs::exists(collision_wav.wstring() + L".partial"),
        "collision preserves the existing final and leaves no partial")) return false;

  const auto partial_collision_wav = checkpoints / L"partial-collision.wav";
  const auto partial_collision = fs::path(partial_collision_wav.wstring() + L".partial");
  { std::ofstream existing(partial_collision, std::ios::binary); existing << "preserve"; }
  if (!expect(failure_converter.convert(checkpoint, partial_collision_wav) == pcm_wav_conversion_result::io_failure,
        "existing partial collision is rejected") ||
      !expect(!fs::exists(partial_collision_wav) && fs::file_size(partial_collision) == 8,
        "partial collision preserves the other invocation's staging file")) return false;

  zr_pcm_convert_handle handle{};
  const auto abi_wav = checkpoints / L"abi.wav";
  if (!expect(zr_convert_audio_to_pcm_wav(checkpoint.c_str(), abi_wav.c_str(), &handle) == ZR_OK && handle,
        "ABI converts and publishes a request handle") ||
      !expect(validate_pcm_wav(abi_wav), "ABI publishes a valid WAV") ||
      !expect(zr_cancel_pcm_conversion(handle) == ZR_OK, "completed ABI handle remains cancellable until destroy") ||
      !expect(zr_destroy_pcm_conversion(handle) == ZR_OK, "ABI conversion handle is destroyed") ||
      !expect(zr_cancel_pcm_conversion(handle) == ZR_INVALID_ARGUMENT,
        "operations after destroy cannot access the conversion") ||
      !expect(zr_convert_audio_to_pcm_wav(nullptr, abi_wav.c_str(), &handle) == ZR_INVALID_ARGUMENT &&
        zr_convert_audio_to_pcm_wav(checkpoint.c_str(), nullptr, &handle) == ZR_INVALID_ARGUMENT &&
        zr_convert_audio_to_pcm_wav(checkpoint.c_str(), abi_wav.c_str(), nullptr) == ZR_INVALID_ARGUMENT &&
        zr_cancel_pcm_conversion(nullptr) == ZR_INVALID_ARGUMENT && zr_destroy_pcm_conversion(nullptr) == ZR_INVALID_ARGUMENT,
        "ABI validates paths, handle storage, and handles")) return false;

  handle = nullptr;
  const auto missing_audio_result = zr_convert_audio_to_pcm_wav(silent_mp4.c_str(), missing_wav.c_str(), &handle);
  return expect(missing_audio_result == ZR_AUDIO_STREAM_MISSING && handle, "ABI maps a missing audio stream") &&
    expect(zr_destroy_pcm_conversion(handle) == ZR_OK, "failed ABI conversion handle is destroyed");
}
