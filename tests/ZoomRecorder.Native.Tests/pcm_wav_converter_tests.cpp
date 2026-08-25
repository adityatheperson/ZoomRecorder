#include "audio_chunk_exporter.h"
#include "mp4_writer.h"
#include "pcm_wav_converter.h"
#include "zoom_recorder.h"

#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>

#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <string>
#include <thread>
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

bool create_junction(const fs::path& junction, const fs::path& target) {
  const auto command = L"cmd.exe /d /c mklink /J \"" + junction.wstring() + L"\" \"" +
    target.wstring() + L"\" >nul";
  return _wsystem(command.c_str()) == 0;
}

struct api_publication_gate {
  std::mutex mutex;
  std::condition_variable changed;
  zr_pcm_convert_handle handle{};
  bool entered{};
  bool released{};
  size_t cancellations{};
};

void __stdcall block_api_publication(void* handle, void* context) {
  auto& gate = *static_cast<api_publication_gate*>(context);
  std::unique_lock lock(gate.mutex);
  gate.handle = static_cast<zr_pcm_convert_handle>(handle);
  gate.entered = true;
  gate.changed.notify_all();
  gate.changed.wait(lock, [&gate] { return gate.released; });
}

void __stdcall observe_api_cancellation(void*, void* context) {
  auto& gate = *static_cast<api_publication_gate*>(context);
  std::scoped_lock lock(gate.mutex);
  ++gate.cancellations;
  gate.changed.notify_all();
}

bool wait_for_api_publication(api_publication_gate& gate) {
  std::unique_lock lock(gate.mutex);
  return gate.changed.wait_for(lock, std::chrono::seconds(5), [&gate] { return gate.entered; });
}

bool wait_for_api_cancellation(api_publication_gate& gate) {
  std::unique_lock lock(gate.mutex);
  return gate.changed.wait_for(lock, std::chrono::seconds(5), [&gate] { return gate.cancellations != 0; });
}

void release_api_publication(api_publication_gate& gate) {
  std::scoped_lock lock(gate.mutex);
  gate.released = true;
  gate.changed.notify_all();
}

class api_test_seam_guard {
 public:
  explicit api_test_seam_guard(api_publication_gate& gate) {
    const pcm_wav_api_test_seam seam{block_api_publication, observe_api_cancellation, &gate};
    zr_set_pcm_wav_api_test_seam(&seam);
  }
  ~api_test_seam_guard() { zr_set_pcm_wav_api_test_seam(nullptr); }
};

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

bool run_abi_cancel_before_publication_test(const fs::path& checkpoint, const fs::path& checkpoints) {
  api_publication_gate gate;
  api_test_seam_guard seam(gate);
  const auto wav_path = checkpoints / L"abi-cancel-before-publication.wav";
  zr_pcm_convert_handle handle{};
  zr_result conversion_result{ZR_INTERNAL_ERROR};
  std::thread conversion([&] {
    conversion_result = zr_convert_audio_to_pcm_wav(checkpoint.c_str(), wav_path.c_str(), &handle);
  });
  if (!wait_for_api_publication(gate)) {
    release_api_publication(gate);
    conversion.join();
    return expect(false, "ABI conversion reaches the deterministic publication gate");
  }
  const auto cancel_result = zr_cancel_pcm_conversion(gate.handle);
  release_api_publication(gate);
  conversion.join();
  return expect(cancel_result == ZR_OK, "ABI cancellation is accepted before publication") &&
    expect(conversion_result == ZR_CANCELLED, "ABI cancel-before-publication reports cancellation") &&
    expect(!fs::exists(wav_path) && !fs::exists(wav_path.wstring() + L".partial"),
      "ABI cancel-before-publication leaves no output") &&
    expect(zr_destroy_pcm_conversion(handle) == ZR_OK, "cancelled ABI conversion handle is destroyed");
}

bool run_abi_live_destroy_wait_test(const fs::path& checkpoint, const fs::path& checkpoints) {
  api_publication_gate gate;
  api_test_seam_guard seam(gate);
  const auto wav_path = checkpoints / L"abi-live-destroy.wav";
  zr_pcm_convert_handle handle{};
  zr_result conversion_result{ZR_INTERNAL_ERROR};
  zr_result destroy_result{ZR_INTERNAL_ERROR};
  std::atomic_bool destroy_finished{};
  std::thread conversion([&] {
    conversion_result = zr_convert_audio_to_pcm_wav(checkpoint.c_str(), wav_path.c_str(), &handle);
  });
  if (!wait_for_api_publication(gate)) {
    release_api_publication(gate);
    conversion.join();
    return expect(false, "live-destroy conversion reaches the publication gate");
  }
  std::thread destroy([&] {
    destroy_result = zr_destroy_pcm_conversion(gate.handle);
    destroy_finished.store(true, std::memory_order_release);
  });
  const auto destroy_cancelled = wait_for_api_cancellation(gate);
  const auto waited = !destroy_finished.load(std::memory_order_acquire);
  release_api_publication(gate);
  conversion.join();
  destroy.join();
  return expect(destroy_cancelled, "live ABI destroy requests cancellation") &&
    expect(waited, "live ABI destroy waits for the conversion worker") &&
    expect(conversion_result == ZR_CANCELLED && destroy_result == ZR_OK,
      "live ABI destroy completes after cancelled conversion") &&
    expect(!fs::exists(wav_path) && !fs::exists(wav_path.wstring() + L".partial"),
      "live ABI destroy leaves no output");
}

bool run_abi_cancel_destroy_race_test(const fs::path& checkpoint, const fs::path& checkpoints) {
  api_publication_gate gate;
  api_test_seam_guard seam(gate);
  const auto wav_path = checkpoints / L"abi-cancel-destroy-race.wav";
  zr_pcm_convert_handle handle{};
  zr_result conversion_result{ZR_INTERNAL_ERROR};
  zr_result cancel_result{ZR_INTERNAL_ERROR};
  zr_result destroy_result_one{ZR_INTERNAL_ERROR};
  zr_result destroy_result_two{ZR_INTERNAL_ERROR};
  std::atomic_bool start{};
  std::thread conversion([&] {
    conversion_result = zr_convert_audio_to_pcm_wav(checkpoint.c_str(), wav_path.c_str(), &handle);
  });
  if (!wait_for_api_publication(gate)) {
    release_api_publication(gate);
    conversion.join();
    return expect(false, "race conversion reaches the publication gate");
  }
  const auto race_call = [&start](auto&& call) {
    while (!start.load(std::memory_order_acquire)) std::this_thread::yield();
    call();
  };
  std::thread cancel([&] { race_call([&] { cancel_result = zr_cancel_pcm_conversion(gate.handle); }); });
  std::thread destroy_one([&] { race_call([&] { destroy_result_one = zr_destroy_pcm_conversion(gate.handle); }); });
  std::thread destroy_two([&] { race_call([&] { destroy_result_two = zr_destroy_pcm_conversion(gate.handle); }); });
  start.store(true, std::memory_order_release);
  const auto cancellation_observed = wait_for_api_cancellation(gate);
  release_api_publication(gate);
  conversion.join();
  cancel.join();
  destroy_one.join();
  destroy_two.join();
  const auto one_destroy_owned =
    (destroy_result_one == ZR_OK && destroy_result_two == ZR_INVALID_ARGUMENT) ||
    (destroy_result_two == ZR_OK && destroy_result_one == ZR_INVALID_ARGUMENT);
  return expect(cancellation_observed, "cancel/destroy race accepts cancellation before publication") &&
    expect(conversion_result == ZR_CANCELLED, "cancel/destroy race cannot publish success") &&
    expect(cancel_result == ZR_OK || cancel_result == ZR_INVALID_ARGUMENT,
      "racing cancel either acquires the live request or loses to destroy") &&
    expect(one_destroy_owned, "exactly one racing destroy owns and destroys the request") &&
    expect(zr_destroy_pcm_conversion(handle) == ZR_INVALID_ARGUMENT,
      "double destroy cannot reacquire request ownership") &&
    expect(!fs::exists(wav_path) && !fs::exists(wav_path.wstring() + L".partial"),
      "cancel/destroy race leaves no output");
}
}

bool run_pcm_wav_converter_tests() {
  temporary_directory temporary;
  const auto source_mp4 = temporary.path / L"source.mp4";
  const auto checkpoints = temporary.path / L"checkpoints";
  const auto silent_mp4 = checkpoints / L"silent.mp4";
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

  const auto commit_cancelled_wav = checkpoints / L"commit-cancelled.wav";
  pcm_wav_conversion_cancellation commit_cancelled;
  pcm_wav_converter_test_seam commit_cancellation_seam;
  commit_cancellation_seam.before_commit = [&] { commit_cancelled.cancel(); };
  pcm_wav_converter commit_cancelling_converter(commit_cancelled, &commit_cancellation_seam);
  if (!expect(commit_cancelling_converter.convert(checkpoint, commit_cancelled_wav) ==
        pcm_wav_conversion_result::cancelled, "cancellation accepted immediately before commit wins publication") ||
      !expect(!fs::exists(commit_cancelled_wav) && !fs::exists(commit_cancelled_wav.wstring() + L".partial"),
        "cancel-before-commit publishes neither final nor partial WAV")) return false;

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

  const auto linked_job = temporary.path / L"linked-job";
  if (!expect(create_junction(linked_job, checkpoints), "job-directory junction fixture is created") ||
      !expect(failure_converter.convert(linked_job / checkpoint.filename(), linked_job / L"linked-job.wav") ==
        pcm_wav_conversion_result::invalid_argument, "native converter rejects a reparse-point output directory")) return false;

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

  if (!run_abi_cancel_before_publication_test(checkpoint, checkpoints) ||
      !run_abi_live_destroy_wait_test(checkpoint, checkpoints) ||
      !run_abi_cancel_destroy_race_test(checkpoint, checkpoints)) return false;

  handle = nullptr;
  const auto missing_audio_result = zr_convert_audio_to_pcm_wav(silent_mp4.c_str(), missing_wav.c_str(), &handle);
  return expect(missing_audio_result == ZR_AUDIO_STREAM_MISSING && handle, "ABI maps a missing audio stream") &&
    expect(zr_destroy_pcm_conversion(handle) == ZR_OK, "failed ABI conversion handle is destroyed");
}
