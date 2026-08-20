#include "audio_chunk_exporter.h"
#include "mp4_writer.h"
#include "zoom_recorder.h"

#include <windows.h>
#include <bcrypt.h>
#include <d3d11.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <wrl/client.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <span>
#include <string>
#include <thread>
#include <vector>

namespace {
using Microsoft::WRL::ComPtr;
namespace fs = std::filesystem;

struct temporary_directory {
  temporary_directory() {
    path = fs::temp_directory_path() /
      (L"zoom-recorder-audio-chunks-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()));
    fs::create_directories(path);
  }
  ~temporary_directory() { std::error_code ignored; fs::remove_all(path, ignored); }
  fs::path path;
};

bool expect(bool condition, const char* message) {
  if (!condition) std::fprintf(stderr, "audio chunk exporter test failed: %s\n", message);
  return condition;
}

std::string sha256_file(const fs::path& path) {
  BCRYPT_ALG_HANDLE algorithm{};
  BCRYPT_HASH_HANDLE hash{};
  DWORD object_size{}, copied{};
  if (!BCRYPT_SUCCESS(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0)) ||
      !BCRYPT_SUCCESS(BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH,
        reinterpret_cast<PUCHAR>(&object_size), sizeof(object_size), &copied, 0))) return {};
  std::vector<unsigned char> object(object_size);
  std::array<unsigned char, 32> digest{};
  if (!BCRYPT_SUCCESS(BCryptCreateHash(algorithm, &hash, object.data(), static_cast<ULONG>(object.size()), nullptr, 0, 0))) {
    BCryptCloseAlgorithmProvider(algorithm, 0); return {};
  }
  std::ifstream input(path, std::ios::binary);
  std::array<unsigned char, 8192> buffer{};
  while (input) {
    input.read(reinterpret_cast<char*>(buffer.data()), buffer.size());
    if (input.gcount() > 0 && !BCRYPT_SUCCESS(BCryptHashData(hash, buffer.data(), static_cast<ULONG>(input.gcount()), 0))) {
      BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm, 0); return {};
    }
  }
  const auto finished = BCRYPT_SUCCESS(BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0));
  BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm, 0);
  if (!finished) return {};
  constexpr char digits[] = "0123456789abcdef";
  std::string result(digest.size() * 2, '0');
  for (size_t index = 0; index < digest.size(); ++index) {
    result[index * 2] = digits[digest[index] >> 4];
    result[index * 2 + 1] = digits[digest[index] & 0xf];
  }
  return result;
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

bool is_independently_readable_mono_48khz(const fs::path& path) {
  if (FAILED(MFStartup(MF_VERSION))) return false;
  ComPtr<IMFSourceReader> reader;
  auto result = MFCreateSourceReaderFromURL(path.c_str(), nullptr, &reader);
  UINT32 channels{}, samples_per_second{};
  if (SUCCEEDED(result)) {
    ComPtr<IMFMediaType> native_type;
    result = reader->GetNativeMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0, &native_type);
    if (SUCCEEDED(result)) result = native_type->GetUINT32(MF_MT_AUDIO_NUM_CHANNELS, &channels);
    if (SUCCEEDED(result)) result = native_type->GetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, &samples_per_second);
    DWORD stream{}, flags{}; LONGLONG timestamp{}; ComPtr<IMFSample> sample;
    if (SUCCEEDED(result)) result = reader->ReadSample(MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0, &stream, &flags, &timestamp, &sample);
    if (SUCCEEDED(result) && !sample) result = E_FAIL;
  }
  reader.Reset(); MFShutdown();
  return SUCCEEDED(result) && channels == 1 && samples_per_second == 48000;
}

bool no_partial_files(const fs::path& directory) {
  return std::ranges::none_of(fs::directory_iterator(directory), [](const auto& entry) {
    return entry.path().extension() == L".partial";
  });
}

bool no_published_chunks(const fs::path& directory) {
  return std::ranges::none_of(fs::directory_iterator(directory), [](const auto& entry) {
    return entry.path().extension() == L".m4a";
  });
}

bool validate_chunks(const std::vector<audio_chunk_export_record>& chunks, const fs::path& output, std::uint64_t max_bytes) {
  if (!expect(!chunks.empty(), "at least one chunk is published")) return false;
  for (size_t index = 0; index < chunks.size(); ++index) {
    const auto& chunk = chunks[index];
    if (!expect(chunk.index == index, "chunk indexes are contiguous")) return false;
    if (!expect(chunk.start_milliseconds >= 0 && chunk.end_milliseconds > chunk.start_milliseconds, "chunk times are valid")) return false;
    if (!expect(chunk.byte_size > 0 && chunk.byte_size <= max_bytes, "chunk respects max bytes")) return false;
    if (!expect(chunk.normalized_sample_rate == 16000 && chunk.encoded_sample_rate == 48000 && chunk.channel_count == 1,
      "native metadata proves 16 kHz mono normalization and the 48 kHz mono AAC boundary")) return false;
    if (!expect(chunk.sha256.size() == 64 && std::ranges::all_of(chunk.sha256, [](char value) {
      return value >= '0' && value <= '9' || value >= 'a' && value <= 'f';
    }), "chunk hash is lowercase SHA-256")) return false;
    if (!expect(fs::exists(chunk.path) && fs::file_size(chunk.path) == chunk.byte_size, "metadata size matches file")) return false;
    if (!expect(sha256_file(chunk.path) == chunk.sha256, "metadata hash matches file")) return false;
    if (!expect(fs::weakly_canonical(chunk.path.parent_path()) == fs::canonical(output), "chunk stays in job directory")) return false;
    if (!expect(chunk.path.extension() == L".m4a", "published chunk uses m4a extension")) return false;
    if (!expect(is_independently_readable_mono_48khz(chunk.path), "chunk is independently readable mono 48 kHz AAC")) return false;
    if (index > 0 && !expect(chunks[index - 1].end_milliseconds - chunk.start_milliseconds == 5000,
      "adjacent chunks overlap by five seconds")) return false;
  }
  return true;
}

struct abi_callback_state {
  zr_audio_prepare_handle* handle{};
  std::vector<audio_chunk_export_record> chunks;
  bool cancel_after_first{};
  std::atomic_bool* block_entered{};
  std::atomic_bool* block_release{};
};

void __stdcall collect_abi_chunk(const zr_audio_chunk* chunk, void* context) {
  auto& state = *static_cast<abi_callback_state*>(context);
  if (!chunk) return;
  state.chunks.push_back({chunk->index, chunk->path ? chunk->path : L"", chunk->start_milliseconds,
    chunk->end_milliseconds, chunk->sha256 ? chunk->sha256 : "", chunk->byte_size,
    chunk->normalized_sample_rate, chunk->encoded_sample_rate, chunk->channel_count});
  if (state.cancel_after_first && state.handle && *state.handle) zr_cancel_audio_preparation(*state.handle);
  if (state.block_entered && state.block_release) {
    state.block_entered->store(true, std::memory_order_release);
    while (!state.block_release->load(std::memory_order_acquire)) Sleep(1);
  }
}

bool wait_until_true(const std::atomic_bool& value, DWORD timeout_milliseconds) {
  const auto deadline = GetTickCount64() + timeout_milliseconds;
  while (!value.load(std::memory_order_acquire) && GetTickCount64() < deadline) Sleep(1);
  return value.load(std::memory_order_acquire);
}
}

bool run_audio_chunk_exporter_tests() {
  temporary_directory temporary;
  const auto fixture = temporary.path / L"lecture.mp4";
  const auto long_fixture = temporary.path / L"long-lecture.mp4";
  const auto silent_fixture = temporary.path / L"silent.mp4";
  const auto corrupt_fixture = temporary.path / L"corrupt.mp4";
  const auto output = temporary.path / L"job";
  const auto cancel_output = temporary.path / L"cancel-job";
  fs::create_directories(output); fs::create_directories(cancel_output);
  if (!expect(create_fixture(fixture, 11, true), "synthetic audio fixture is created") ||
      !expect(create_fixture(long_fixture, 45, true), "synthetic long audio fixture is created") ||
      !expect(create_fixture(silent_fixture, 1, false), "synthetic missing-audio fixture is created")) return false;
  { std::ofstream corrupt(corrupt_fixture, std::ios::binary); corrupt << "not an mp4"; }

  const auto original_hash = sha256_file(fixture);
  audio_chunk_cancellation one_cancel;
  audio_chunk_exporter one_exporter(one_cancel);
  std::vector<audio_chunk_export_record> one_chunk;
  auto result = one_exporter.export_chunks(fixture, output, audio_chunk_exporter::default_max_chunk_bytes,
    [&](const auto& chunk) { one_chunk.push_back(chunk); });
  if (!expect(result == audio_chunk_export_result::success, "default export succeeds") ||
      !expect(one_chunk.size() == 1, "short recording creates one chunk") ||
      !validate_chunks(one_chunk, output, audio_chunk_exporter::default_max_chunk_bytes)) return false;

  constexpr std::uint64_t forced_max = 96 * 1024;
  audio_chunk_cancellation multi_cancel;
  audio_chunk_exporter multi_exporter(multi_cancel);
  std::vector<audio_chunk_export_record> multiple;
  result = multi_exporter.export_chunks(fixture, output, forced_max, [&](const auto& chunk) { multiple.push_back(chunk); });
  if (!expect(result == audio_chunk_export_result::success, "forced multi-chunk export succeeds") ||
      !expect(multiple.size() > 1, "small bound forces multiple chunks") ||
      !validate_chunks(multiple, output, forced_max) || !expect(no_partial_files(output), "successful export leaves no partials")) return false;

  const auto close_failure_output = temporary.path / L"close-failure";
  fs::create_directories(close_failure_output);
  audio_chunk_export_test_seam close_failure_seam;
  close_failure_seam.accept_byte_stream_close = [](bool) { return false; };
  audio_chunk_cancellation close_failure_cancel;
  audio_chunk_exporter close_failure_exporter(close_failure_cancel, &close_failure_seam);
  std::vector<audio_chunk_export_record> close_failure_chunks;
  result = close_failure_exporter.export_chunks(fixture, close_failure_output,
    audio_chunk_exporter::default_max_chunk_bytes,
    [&](const auto& chunk) { close_failure_chunks.push_back(chunk); });
  if (!expect(result == audio_chunk_export_result::io_failure, "byte-stream close failure is reported") ||
      !expect(close_failure_chunks.empty() && no_published_chunks(close_failure_output),
        "byte-stream close failure never publishes a chunk") ||
      !expect(no_partial_files(close_failure_output), "byte-stream close failure removes its partial")) return false;

  const auto bounded_output = temporary.path / L"bounded-memory";
  fs::create_directories(bounded_output);
  constexpr std::uint64_t test_memory_bound = 512 * 1024;
  std::uint64_t peak_buffered_bytes{};
  audio_chunk_export_test_seam bounded_memory_seam;
  bounded_memory_seam.maximum_buffered_bytes = test_memory_bound;
  bounded_memory_seam.peak_buffered_bytes = &peak_buffered_bytes;
  audio_chunk_cancellation bounded_memory_cancel;
  audio_chunk_exporter bounded_memory_exporter(bounded_memory_cancel, &bounded_memory_seam);
  std::vector<audio_chunk_export_record> bounded_chunks;
  result = bounded_memory_exporter.export_chunks(long_fixture, bounded_output,
    audio_chunk_exporter::default_max_chunk_bytes,
    [&](const auto& chunk) { bounded_chunks.push_back(chunk); });
  if (!expect(result == audio_chunk_export_result::success, "long generated input exports with bounded buffering") ||
      !expect(bounded_chunks.size() > 1, "the injected memory bound forces streaming across candidates") ||
      !expect(peak_buffered_bytes > 0 && peak_buffered_bytes <= test_memory_bound,
        "peak owned PCM buffering stays within the explicit bound") ||
      !validate_chunks(bounded_chunks, bounded_output, audio_chunk_exporter::default_max_chunk_bytes) ||
      !expect(no_partial_files(bounded_output), "bounded streaming removes its PCM and M4A partials")) return false;

  audio_chunk_cancellation cancelled;
  audio_chunk_exporter cancelling_exporter(cancelled);
  std::vector<audio_chunk_export_record> published_before_cancel;
  result = cancelling_exporter.export_chunks(fixture, cancel_output, forced_max, [&](const auto& chunk) {
    published_before_cancel.push_back(chunk); cancelled.cancel();
  });
  if (!expect(result == audio_chunk_export_result::cancelled, "cancellation is reported") ||
      !expect(published_before_cancel.size() == 1 && fs::exists(published_before_cancel.front().path),
        "already-published chunk survives cancellation") ||
      !expect(no_partial_files(cancel_output), "cancellation removes invocation partials")) return false;

  audio_chunk_cancellation invalid_cancel;
  audio_chunk_exporter invalid_exporter(invalid_cancel);
  const auto discard = [](const auto&) {};
  if (!expect(invalid_exporter.export_chunks(fixture, output, 0, discard) == audio_chunk_export_result::invalid_argument,
        "zero max bytes is rejected") ||
      !expect(invalid_exporter.export_chunks(fixture, temporary.path / L"missing", forced_max, discard) == audio_chunk_export_result::invalid_argument,
        "nonexistent output directory is rejected") ||
      !expect(invalid_exporter.export_chunks(temporary.path / L"missing.mp4", output, forced_max, discard) == audio_chunk_export_result::invalid_argument,
        "missing input is rejected") ||
      !expect(invalid_exporter.export_chunks(silent_fixture, output, forced_max, discard) == audio_chunk_export_result::missing_audio,
        "missing audio is stable") ||
      !expect(invalid_exporter.export_chunks(corrupt_fixture, output, forced_max, discard) == audio_chunk_export_result::media_failure,
        "corrupt input is a media failure")) return false;

  const auto first_published = one_chunk.front().path;
  std::vector<audio_chunk_export_record> repeated;
  result = invalid_exporter.export_chunks(fixture, output, audio_chunk_exporter::default_max_chunk_bytes,
    [&](const auto& chunk) { repeated.push_back(chunk); });
  if (!expect(result == audio_chunk_export_result::success && repeated.size() == 1, "repeated export finalizes") ||
      !expect(repeated.front().path != first_published && fs::exists(first_published), "repeated export preserves prior published chunks") ||
      !expect(no_partial_files(output), "repeated finalization leaves no partials") ||
      !expect(sha256_file(fixture) == original_hash, "source MP4 remains unchanged")) return false;

  zr_audio_prepare_handle handle{};
  abi_callback_state abi{&handle};
  const auto abi_result = zr_prepare_audio_chunks(fixture.c_str(), output.c_str(),
    audio_chunk_exporter::default_max_chunk_bytes, collect_abi_chunk, &abi, &handle);
  const auto callback_count_at_return = abi.chunks.size();
  Sleep(20);
  if (!expect(abi_result == ZR_OK && handle && callback_count_at_return == 1 && abi.chunks.size() == callback_count_at_return,
        "synchronous ABI owns callback lifetime through return") ||
      !expect(zr_destroy_audio_preparation(handle) == ZR_OK, "ABI preparation handle is destroyed") ||
      !expect(zr_prepare_audio_chunks(fixture.c_str(), output.c_str(), 0, collect_abi_chunk, &abi, &handle) == ZR_INVALID_ARGUMENT,
        "ABI validates max bytes") ||
      !expect(zr_cancel_audio_preparation(nullptr) == ZR_INVALID_ARGUMENT &&
        zr_destroy_audio_preparation(nullptr) == ZR_INVALID_ARGUMENT, "ABI validates handles")) return false;

  handle = nullptr;
  if (!expect(zr_prepare_audio_chunks(silent_fixture.c_str(), output.c_str(), forced_max,
        collect_abi_chunk, &abi, &handle) == ZR_AUDIO_STREAM_MISSING && handle,
        "ABI maps a missing audio stream") ||
      !expect(zr_destroy_audio_preparation(handle) == ZR_OK, "missing-audio ABI handle is destroyed")) return false;
  handle = nullptr;
  if (!expect(zr_prepare_audio_chunks(corrupt_fixture.c_str(), output.c_str(), forced_max,
        collect_abi_chunk, &abi, &handle) == ZR_MEDIA_ERROR && handle,
        "ABI maps corrupt media") ||
      !expect(zr_destroy_audio_preparation(handle) == ZR_OK, "corrupt-media ABI handle is destroyed")) return false;

  handle = nullptr;
  abi_callback_state abi_cancel{&handle, {}, true};
  const auto abi_cancelled = zr_prepare_audio_chunks(fixture.c_str(), cancel_output.c_str(), forced_max,
    collect_abi_chunk, &abi_cancel, &handle);
  if (!expect(abi_cancelled == ZR_CANCELLED && handle && abi_cancel.chunks.size() == 1,
      "ABI request-scoped handle cancels only its synchronous invocation") &&
    expect(zr_destroy_audio_preparation(handle) == ZR_OK, "cancelled ABI preparation handle is destroyed") &&
    expect(no_partial_files(cancel_output), "ABI cancellation leaves no partials") &&
    expect(sha256_file(fixture) == original_hash, "ABI also preserves source MP4")) return false;

  const auto lifetime_output = temporary.path / L"handle-lifetime";
  fs::create_directories(lifetime_output);
  zr_audio_prepare_handle lifetime_handle{};
  std::atomic_bool callback_entered{}, callback_release{}, begin_operations{};
  abi_callback_state lifetime_state{&lifetime_handle, {}, false, &callback_entered, &callback_release};
  zr_result lifetime_prepare_result{ZR_INTERNAL_ERROR};
  std::thread prepare_thread([&] {
    lifetime_prepare_result = zr_prepare_audio_chunks(fixture.c_str(), lifetime_output.c_str(), forced_max,
      collect_abi_chunk, &lifetime_state, &lifetime_handle);
  });
  if (!wait_until_true(callback_entered, 10000)) {
    callback_release.store(true, std::memory_order_release);
    prepare_thread.join();
    return expect(false, "ABI lifetime regression reaches a live callback");
  }
  zr_result cancel_race_result{ZR_INTERNAL_ERROR};
  zr_result first_destroy_result{ZR_INTERNAL_ERROR};
  zr_result second_destroy_result{ZR_INTERNAL_ERROR};
  auto wait_for_start = [&] { while (!begin_operations.load(std::memory_order_acquire)) std::this_thread::yield(); };
  std::thread cancel_race([&] { wait_for_start(); cancel_race_result = zr_cancel_audio_preparation(lifetime_handle); });
  std::thread first_destroy([&] { wait_for_start(); first_destroy_result = zr_destroy_audio_preparation(lifetime_handle); });
  std::thread second_destroy([&] { wait_for_start(); second_destroy_result = zr_destroy_audio_preparation(lifetime_handle); });
  begin_operations.store(true, std::memory_order_release);
  Sleep(20);
  callback_release.store(true, std::memory_order_release);
  cancel_race.join(); first_destroy.join(); second_destroy.join(); prepare_thread.join();
  const auto successful_destroys = (first_destroy_result == ZR_OK ? 1 : 0) + (second_destroy_result == ZR_OK ? 1 : 0);
  const auto rejected_destroys = (first_destroy_result == ZR_INVALID_ARGUMENT ? 1 : 0) +
    (second_destroy_result == ZR_INVALID_ARGUMENT ? 1 : 0);
  if (!expect(lifetime_prepare_result == ZR_CANCELLED, "destroy cancels a live ABI preparation") ||
      !expect(successful_destroys == 1 && rejected_destroys == 1,
        "exactly one concurrent destroy acquires the handle") ||
      !expect(cancel_race_result == ZR_OK || cancel_race_result == ZR_INVALID_ARGUMENT,
        "cancel deterministically succeeds before removal or is rejected after removal") ||
      !expect(zr_cancel_audio_preparation(lifetime_handle) == ZR_INVALID_ARGUMENT,
        "operations after destroy cannot access released preparation memory")) return false;

  const auto parallel_cancel_output = temporary.path / L"parallel-cancel";
  const auto parallel_success_output = temporary.path / L"parallel-success";
  fs::create_directories(parallel_cancel_output); fs::create_directories(parallel_success_output);
  zr_audio_prepare_handle parallel_cancel_handle{}, parallel_success_handle{};
  abi_callback_state parallel_cancel{&parallel_cancel_handle, {}, true};
  abi_callback_state parallel_success{&parallel_success_handle};
  zr_result parallel_cancel_result{ZR_INTERNAL_ERROR}, parallel_success_result{ZR_INTERNAL_ERROR};
  std::thread cancel_thread([&] {
    parallel_cancel_result = zr_prepare_audio_chunks(fixture.c_str(), parallel_cancel_output.c_str(), forced_max,
      collect_abi_chunk, &parallel_cancel, &parallel_cancel_handle);
  });
  std::thread success_thread([&] {
    parallel_success_result = zr_prepare_audio_chunks(fixture.c_str(), parallel_success_output.c_str(),
      audio_chunk_exporter::default_max_chunk_bytes, collect_abi_chunk, &parallel_success, &parallel_success_handle);
  });
  cancel_thread.join(); success_thread.join();
  return expect(parallel_cancel_result == ZR_CANCELLED && parallel_cancel.chunks.size() == 1,
      "one concurrent request can be cancelled") &&
    expect(parallel_success_result == ZR_OK && parallel_success.chunks.size() == 1,
      "cancelling one request does not cancel another") &&
    expect(zr_destroy_audio_preparation(parallel_cancel_handle) == ZR_OK &&
      zr_destroy_audio_preparation(parallel_success_handle) == ZR_OK, "concurrent ABI handles are destroyed") &&
    expect(no_partial_files(parallel_cancel_output) && no_partial_files(parallel_success_output),
      "concurrent preparations leave no partials") &&
    expect(sha256_file(fixture) == original_hash, "concurrent preparation preserves source MP4");
}
