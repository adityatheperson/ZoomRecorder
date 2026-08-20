#include "audio_chunk_exporter.h"

#include <windows.h>
#include <bcrypt.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <wrl/client.h>
#include <wrl/implements.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cstring>
#include <fstream>
#include <iomanip>
#include <limits>
#include <mutex>
#include <sstream>
#include <span>
#include <utility>
#include <vector>

namespace {
using Microsoft::WRL::ComPtr;
namespace fs = std::filesystem;

constexpr std::uint32_t normalized_sample_rate = 16000;
constexpr std::uint32_t encoded_sample_rate = 48000;
constexpr std::uint32_t channel_count = 1;
constexpr std::uint32_t bits_per_sample = 16;
constexpr std::uint32_t bytes_per_frame = 2;
constexpr std::uint32_t aac_bitrate = 96000;
constexpr std::uint64_t overlap_frames = 5ull * normalized_sample_rate;
constexpr std::uint64_t millisecond_frames = normalized_sample_rate / 1000;
std::atomic_uint64_t invocation_sequence{};

class mf_session {
 public:
  mf_session() : result_(MFStartup(MF_VERSION)) {}
  ~mf_session() { if (SUCCEEDED(result_)) MFShutdown(); }
  bool started() const noexcept { return SUCCEEDED(result_); }

 private:
  HRESULT result_;
};

struct decoded_audio {
  std::uint64_t frame_count{};
  std::int64_t first_milliseconds{};
  std::uint32_t sample_rate{};
  std::uint32_t channels{};
};

bool set_pcm_type(IMFMediaType* type, std::uint32_t samples_per_second) {
  return type &&
    SUCCEEDED(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio)) &&
    SUCCEEDED(type->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM)) &&
    SUCCEEDED(type->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, channel_count)) &&
    SUCCEEDED(type->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, samples_per_second)) &&
    SUCCEEDED(type->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, bits_per_sample)) &&
    SUCCEEDED(type->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, bytes_per_frame)) &&
    SUCCEEDED(type->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, samples_per_second * bytes_per_frame)) &&
    SUCCEEDED(type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE));
}

bool set_aac_type(IMFMediaType* type, std::uint32_t bitrate) {
  return type &&
    SUCCEEDED(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio)) &&
    SUCCEEDED(type->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC)) &&
    SUCCEEDED(type->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, channel_count)) &&
    SUCCEEDED(type->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, encoded_sample_rate)) &&
    SUCCEEDED(type->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, bits_per_sample)) &&
    SUCCEEDED(type->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, bitrate / 8)) &&
    SUCCEEDED(type->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, 1)) &&
    SUCCEEDED(type->SetUINT32(MF_MT_AAC_PAYLOAD_TYPE, 0)) &&
    SUCCEEDED(type->SetUINT32(MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29));
}

class observed_byte_stream final : public Microsoft::WRL::RuntimeClass<
    Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>, IMFByteStream> {
 public:
  explicit observed_byte_stream(IMFByteStream* inner) : inner_(inner) {}

  IFACEMETHODIMP GetCapabilities(DWORD* capabilities) override { return inner_->GetCapabilities(capabilities); }
  IFACEMETHODIMP GetLength(QWORD* length) override { return inner_->GetLength(length); }
  IFACEMETHODIMP SetLength(QWORD length) override { return inner_->SetLength(length); }
  IFACEMETHODIMP GetCurrentPosition(QWORD* position) override { return inner_->GetCurrentPosition(position); }
  IFACEMETHODIMP SetCurrentPosition(QWORD position) override { return inner_->SetCurrentPosition(position); }
  IFACEMETHODIMP IsEndOfStream(BOOL* end_of_stream) override { return inner_->IsEndOfStream(end_of_stream); }
  IFACEMETHODIMP Read(BYTE* bytes, ULONG count, ULONG* read) override { return inner_->Read(bytes, count, read); }
  IFACEMETHODIMP BeginRead(BYTE* bytes, ULONG count, IMFAsyncCallback* callback, IUnknown* state) override {
    return inner_->BeginRead(bytes, count, callback, state);
  }
  IFACEMETHODIMP EndRead(IMFAsyncResult* result, ULONG* read) override { return inner_->EndRead(result, read); }
  IFACEMETHODIMP Write(const BYTE* bytes, ULONG count, ULONG* written) override {
    return inner_->Write(bytes, count, written);
  }
  IFACEMETHODIMP BeginWrite(const BYTE* bytes, ULONG count, IMFAsyncCallback* callback, IUnknown* state) override {
    return inner_->BeginWrite(bytes, count, callback, state);
  }
  IFACEMETHODIMP EndWrite(IMFAsyncResult* result, ULONG* written) override { return inner_->EndWrite(result, written); }
  IFACEMETHODIMP Seek(MFBYTESTREAM_SEEK_ORIGIN origin, LONGLONG offset, DWORD flags, QWORD* position) override {
    return inner_->Seek(origin, offset, flags, position);
  }
  IFACEMETHODIMP Flush() override { return inner_->Flush(); }
  IFACEMETHODIMP Close() override {
    const auto result = inner_->Close();
    std::scoped_lock lock(mutex_);
    if (!close_called_) {
      close_called_ = true;
      close_result_ = result;
    }
    return result;
  }

  HRESULT ensure_closed() {
    // The MPEG-4 sink closes its archive stream during Finalize. Preserve that
    // first Close HRESULT instead of issuing a second Close that returns E_INVALIDARG.
    {
      std::scoped_lock lock(mutex_);
      if (close_called_) return close_result_;
    }
    return Close();
  }

 private:
  ComPtr<IMFByteStream> inner_;
  std::mutex mutex_;
  bool close_called_{};
  HRESULT close_result_{E_PENDING};
};

audio_chunk_export_result decode_audio(
    const fs::path& input,
    const fs::path& spool_path,
    const audio_chunk_cancellation& cancellation,
    decoded_audio& output) {
  ComPtr<IMFAttributes> attributes;
  if (FAILED(MFCreateAttributes(&attributes, 1)) ||
      FAILED(attributes->SetUINT32(MF_READWRITE_DISABLE_CONVERTERS, FALSE))) return audio_chunk_export_result::media_failure;
  ComPtr<IMFSourceReader> reader;
  if (FAILED(MFCreateSourceReaderFromURL(input.c_str(), attributes.Get(), &reader))) return audio_chunk_export_result::media_failure;
  ComPtr<IMFMediaType> native_type;
  const auto native_result = reader->GetNativeMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0, &native_type);
  if (native_result == MF_E_INVALIDSTREAMNUMBER || native_result == MF_E_NO_MORE_TYPES)
    return audio_chunk_export_result::missing_audio;
  if (FAILED(native_result) || FAILED(reader->SetStreamSelection(MF_SOURCE_READER_ALL_STREAMS, FALSE)) ||
      FAILED(reader->SetStreamSelection(MF_SOURCE_READER_FIRST_AUDIO_STREAM, TRUE))) return audio_chunk_export_result::media_failure;
  ComPtr<IMFMediaType> pcm;
  if (FAILED(MFCreateMediaType(&pcm)) || !set_pcm_type(pcm.Get(), normalized_sample_rate) ||
      FAILED(reader->SetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, nullptr, pcm.Get())))
    return audio_chunk_export_result::media_failure;
  ComPtr<IMFMediaType> current_type;
  if (FAILED(reader->GetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, &current_type)) ||
      FAILED(current_type->GetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, &output.sample_rate)) ||
      FAILED(current_type->GetUINT32(MF_MT_AUDIO_NUM_CHANNELS, &output.channels)) ||
      output.sample_rate != normalized_sample_rate || output.channels != channel_count)
    return audio_chunk_export_result::media_failure;

  std::ofstream spool(spool_path, std::ios::binary | std::ios::trunc);
  if (!spool) return audio_chunk_export_result::io_failure;
  bool timestamp_set{};
  while (true) {
    if (cancellation.is_cancelled()) return audio_chunk_export_result::cancelled;
    DWORD actual_stream{}, flags{};
    LONGLONG timestamp{};
    ComPtr<IMFSample> sample;
    const auto read = reader->ReadSample(
      MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0, &actual_stream, &flags, &timestamp, &sample);
    if (FAILED(read) || (flags & MF_SOURCE_READERF_ERROR)) return audio_chunk_export_result::media_failure;
    if (sample) {
      if (!timestamp_set) {
        output.first_milliseconds = (std::max<LONGLONG>)(0, timestamp) / 10000;
        timestamp_set = true;
      }
      ComPtr<IMFMediaBuffer> buffer;
      if (FAILED(sample->ConvertToContiguousBuffer(&buffer))) return audio_chunk_export_result::media_failure;
      BYTE* bytes{};
      DWORD maximum{}, length{};
      if (FAILED(buffer->Lock(&bytes, &maximum, &length))) return audio_chunk_export_result::media_failure;
      const auto valid = length % sizeof(std::int16_t) == 0;
      const auto frames = static_cast<std::uint64_t>(length / sizeof(std::int16_t));
      const auto count_valid = frames <= (std::numeric_limits<std::uint64_t>::max)() - output.frame_count;
      if (valid && count_valid && length > 0)
        spool.write(reinterpret_cast<const char*>(bytes), static_cast<std::streamsize>(length));
      const auto unlocked = buffer->Unlock();
      if (!valid || !count_valid || FAILED(unlocked)) return audio_chunk_export_result::media_failure;
      if (!spool) return audio_chunk_export_result::io_failure;
      output.frame_count += frames;
    }
    if (flags & MF_SOURCE_READERF_ENDOFSTREAM) break;
  }
  spool.close();
  if (!spool) return audio_chunk_export_result::io_failure;
  return output.frame_count == 0 ? audio_chunk_export_result::missing_audio : audio_chunk_export_result::success;
}

struct sink_resources {
  ComPtr<observed_byte_stream> stream;
  ComPtr<IMFMediaSink> sink;
  ComPtr<IMFSinkWriter> writer;

  ~sink_resources() { (void)close(false, nullptr); }

  audio_chunk_export_result close(bool finalize, const audio_chunk_export_test_seam* test_seam) {
    const auto finalized = writer && finalize ? writer->Finalize() : (finalize ? E_UNEXPECTED : S_OK);
    writer.Reset();
    const auto shutdown = sink ? sink->Shutdown() : (finalize ? E_UNEXPECTED : S_OK);
    sink.Reset();
    const auto closed = stream ? stream->ensure_closed() : (finalize ? E_UNEXPECTED : S_OK);
    stream.Reset();
    auto close_succeeded = SUCCEEDED(closed);
    if (test_seam && test_seam->accept_byte_stream_close) {
      try { close_succeeded = close_succeeded && test_seam->accept_byte_stream_close(close_succeeded); }
      catch (...) { close_succeeded = false; }
    }
    if (FAILED(finalized) || FAILED(shutdown)) return audio_chunk_export_result::media_failure;
    return close_succeeded ? audio_chunk_export_result::success : audio_chunk_export_result::io_failure;
  }
};

class partial_file_guard {
 public:
  explicit partial_file_guard(fs::path path) : path_(std::move(path)) {}
  ~partial_file_guard() { if (active_) { std::error_code ignored; fs::remove(path_, ignored); } }
  void release() noexcept { active_ = false; }

 private:
  fs::path path_;
  bool active_{true};
};

void observe_buffered_bytes(const audio_chunk_export_test_seam* test_seam, std::uint64_t bytes) {
  if (test_seam && test_seam->peak_buffered_bytes && *test_seam->peak_buffered_bytes < bytes)
    *test_seam->peak_buffered_bytes = bytes;
}

audio_chunk_export_result encode_candidate(
    std::span<const std::int16_t> samples,
    std::uint64_t owned_candidate_bytes,
    const fs::path& partial_path,
    std::uint32_t bitrate,
    const audio_chunk_cancellation& cancellation,
    const audio_chunk_export_test_seam* test_seam) {
  if (samples.empty()) return audio_chunk_export_result::media_failure;
  observe_buffered_bytes(test_seam, owned_candidate_bytes);
  sink_resources resources;
  ComPtr<IMFByteStream> file_stream;
  auto stage_result = MFCreateFile(MF_ACCESSMODE_READWRITE, MF_OPENMODE_DELETE_IF_EXIST, MF_FILEFLAGS_NONE,
    partial_path.c_str(), &file_stream);
  if (FAILED(stage_result)) return audio_chunk_export_result::io_failure;
  resources.stream = Microsoft::WRL::Make<observed_byte_stream>(file_stream.Get());
  if (!resources.stream) return audio_chunk_export_result::media_failure;
  ComPtr<IMFMediaType> output_type;
  stage_result = MFCreateMediaType(&output_type);
  if (FAILED(stage_result) || !set_aac_type(output_type.Get(), bitrate)) {
    resources.close(false, nullptr); return audio_chunk_export_result::media_failure;
  }
  stage_result = MFCreateMPEG4MediaSink(resources.stream.Get(), nullptr, output_type.Get(), &resources.sink);
  if (FAILED(stage_result)) {
    resources.close(false, nullptr); return audio_chunk_export_result::media_failure;
  }
  ComPtr<IMFAttributes> writer_attributes;
  stage_result = MFCreateAttributes(&writer_attributes, 1);
  if (SUCCEEDED(stage_result))
    stage_result = writer_attributes->SetGUID(MF_TRANSCODE_CONTAINERTYPE, MFTranscodeContainerType_MPEG4);
  if (SUCCEEDED(stage_result))
    stage_result = MFCreateSinkWriterFromMediaSink(resources.sink.Get(), writer_attributes.Get(), &resources.writer);
  if (FAILED(stage_result)) {
    resources.close(false, nullptr); return audio_chunk_export_result::media_failure;
  }
  ComPtr<IMFMediaType> input_type;
  stage_result = MFCreateMediaType(&input_type);
  if (FAILED(stage_result) || !set_pcm_type(input_type.Get(), encoded_sample_rate)) {
    resources.close(false, nullptr); return audio_chunk_export_result::media_failure;
  }
  stage_result = resources.writer->SetInputMediaType(0, input_type.Get(), nullptr);
  if (FAILED(stage_result)) {
    resources.close(false, nullptr); return audio_chunk_export_result::media_failure;
  }
  stage_result = resources.writer->BeginWriting();
  if (FAILED(stage_result)) {
    resources.close(false, nullptr); return audio_chunk_export_result::media_failure;
  }

  constexpr std::size_t normalized_frames_per_sample = normalized_sample_rate;
  for (std::size_t source_offset = 0; source_offset < samples.size(); source_offset += normalized_frames_per_sample) {
    if (cancellation.is_cancelled()) {
      resources.close(false, nullptr); return audio_chunk_export_result::cancelled;
    }
    const auto source_frame_count = (std::min)(normalized_frames_per_sample, samples.size() - source_offset);
    const auto encoder_frame_count = source_frame_count * 3;
    const auto byte_count = static_cast<DWORD>(encoder_frame_count * sizeof(std::int16_t));
    observe_buffered_bytes(test_seam, owned_candidate_bytes + byte_count);
    ComPtr<IMFMediaBuffer> buffer;
    BYTE* target{};
    if (FAILED(MFCreateMemoryBuffer(byte_count, &buffer)) || FAILED(buffer->Lock(&target, nullptr, nullptr))) {
      resources.close(false, nullptr); return audio_chunk_export_result::media_failure;
    }
    auto* encoder_samples = reinterpret_cast<std::int16_t*>(target);
    for (std::size_t index = 0; index < source_frame_count; ++index) {
      const auto absolute_index = source_offset + index;
      const auto first = static_cast<std::int32_t>(samples[absolute_index]);
      const auto second = static_cast<std::int32_t>(samples[(std::min)(absolute_index + 1, samples.size() - 1)]);
      encoder_samples[index * 3] = static_cast<std::int16_t>(first);
      encoder_samples[index * 3 + 1] = static_cast<std::int16_t>((first * 2 + second) / 3);
      encoder_samples[index * 3 + 2] = static_cast<std::int16_t>((first + second * 2) / 3);
    }
    const auto unlocked = buffer->Unlock();
    if (FAILED(unlocked) || FAILED(buffer->SetCurrentLength(byte_count))) {
      resources.close(false, nullptr); return audio_chunk_export_result::media_failure;
    }
    ComPtr<IMFSample> sample;
    const auto encoder_offset = source_offset * 3;
    if (FAILED(MFCreateSample(&sample)) || FAILED(sample->AddBuffer(buffer.Get())) ||
        FAILED(sample->SetSampleTime(static_cast<LONGLONG>(encoder_offset) * 10000000LL / encoded_sample_rate)) ||
        FAILED(sample->SetSampleDuration(static_cast<LONGLONG>(encoder_frame_count) * 10000000LL / encoded_sample_rate)) ||
        FAILED(resources.writer->WriteSample(0, sample.Get()))) {
      resources.close(false, nullptr); return audio_chunk_export_result::media_failure;
    }
  }
  return resources.close(true, test_seam);
}

std::string sha256_file(const fs::path& path) {
  BCRYPT_ALG_HANDLE algorithm{};
  BCRYPT_HASH_HANDLE hash{};
  DWORD object_size{}, copied{};
  if (!BCRYPT_SUCCESS(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0))) return {};
  if (!BCRYPT_SUCCESS(BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH,
        reinterpret_cast<PUCHAR>(&object_size), sizeof(object_size), &copied, 0))) {
    BCryptCloseAlgorithmProvider(algorithm, 0); return {};
  }
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
  const auto finished = input.eof() && BCRYPT_SUCCESS(BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0));
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

std::wstring make_invocation_prefix() {
  const auto sequence = invocation_sequence.fetch_add(1, std::memory_order_relaxed);
  return L"audio-" + std::to_wstring(GetCurrentProcessId()) + L"-" +
    std::to_wstring(GetTickCount64()) + L"-" + std::to_wstring(sequence);
}

std::wstring chunk_suffix(std::uint32_t index) {
  std::wostringstream value;
  value << L'-' << std::setw(4) << std::setfill(L'0') << index;
  return value.str();
}

std::uint64_t initial_candidate_frames(
    std::uint64_t max_bytes,
    std::uint64_t remaining_frames,
    std::uint64_t maximum_frames) {
  const auto allowance = max_bytes > 4096 ? max_bytes - 4096 : max_bytes / 2;
  const auto estimated = allowance > (std::numeric_limits<std::uint64_t>::max)() / (8ull * normalized_sample_rate)
    ? remaining_frames : allowance * 8ull * normalized_sample_rate / aac_bitrate;
  const auto aligned = estimated / millisecond_frames * millisecond_frames;
  return (std::min)({remaining_frames, maximum_frames, (std::max<std::uint64_t>)(aligned, millisecond_frames)});
}

std::uint64_t maximum_candidate_frames(const audio_chunk_export_test_seam* test_seam) {
  auto memory_limit = audio_chunk_exporter::maximum_buffered_bytes;
  if (test_seam && test_seam->maximum_buffered_bytes != 0)
    memory_limit = (std::min)(memory_limit, test_seam->maximum_buffered_bytes);
  constexpr auto maximum_encoder_buffer_bytes =
    static_cast<std::uint64_t>(encoded_sample_rate) * sizeof(std::int16_t);
  if (memory_limit <= maximum_encoder_buffer_bytes) return 0;
  return ((memory_limit - maximum_encoder_buffer_bytes) / bytes_per_frame) /
    millisecond_frames * millisecond_frames;
}

audio_chunk_export_result read_candidate(
    const fs::path& spool_path,
    std::uint64_t start_frame,
    std::uint64_t frame_count,
    std::vector<std::int16_t>& samples) {
  if (frame_count == 0 || frame_count > (std::numeric_limits<std::size_t>::max)() ||
      start_frame > static_cast<std::uint64_t>((std::numeric_limits<std::streamoff>::max)()) / bytes_per_frame)
    return audio_chunk_export_result::media_failure;
  const auto byte_count = frame_count * bytes_per_frame;
  if (byte_count > static_cast<std::uint64_t>((std::numeric_limits<std::streamsize>::max)()))
    return audio_chunk_export_result::media_failure;
  samples.resize(static_cast<std::size_t>(frame_count));
  std::ifstream spool(spool_path, std::ios::binary);
  if (!spool) return audio_chunk_export_result::io_failure;
  spool.seekg(static_cast<std::streamoff>(start_frame * bytes_per_frame));
  if (!spool) return audio_chunk_export_result::io_failure;
  spool.read(reinterpret_cast<char*>(samples.data()), static_cast<std::streamsize>(byte_count));
  return static_cast<std::uint64_t>(spool.gcount()) == byte_count
    ? audio_chunk_export_result::success
    : audio_chunk_export_result::io_failure;
}
}

void audio_chunk_cancellation::cancel() noexcept { cancelled_.store(true, std::memory_order_release); }
bool audio_chunk_cancellation::is_cancelled() const noexcept { return cancelled_.load(std::memory_order_acquire); }

audio_chunk_exporter::audio_chunk_exporter(
    audio_chunk_cancellation& cancellation,
    const audio_chunk_export_test_seam* test_seam) noexcept
    : cancellation_(cancellation), test_seam_(test_seam) {}

audio_chunk_export_result audio_chunk_exporter::export_chunks(
    const fs::path& mp4_path,
    const fs::path& output_directory,
    std::uint64_t max_chunk_bytes,
    callback on_chunk) const {
  if (mp4_path.empty() || output_directory.empty() || max_chunk_bytes == 0 || !on_chunk)
    return audio_chunk_export_result::invalid_argument;
  fs::path canonical_input, canonical_output;
  try {
    canonical_input = fs::canonical(mp4_path);
    canonical_output = fs::canonical(output_directory);
    if (!fs::is_regular_file(canonical_input) || !fs::is_directory(canonical_output))
      return audio_chunk_export_result::invalid_argument;
  } catch (const fs::filesystem_error&) {
    return audio_chunk_export_result::invalid_argument;
  }
  if (cancellation_.is_cancelled()) return audio_chunk_export_result::cancelled;
  mf_session media_foundation;
  if (!media_foundation.started()) return audio_chunk_export_result::media_failure;
  const auto prefix = make_invocation_prefix();
  const auto spool_path = canonical_output / (prefix + L"-normalized.pcm.partial");
  if (fs::weakly_canonical(spool_path.parent_path()) != canonical_output)
    return audio_chunk_export_result::invalid_argument;
  std::error_code ignored;
  fs::remove(spool_path, ignored);
  partial_file_guard spool_guard(spool_path);
  decoded_audio audio;
  const auto decoded = decode_audio(canonical_input, spool_path, cancellation_, audio);
  if (decoded != audio_chunk_export_result::success) return decoded;

  const auto candidate_limit = maximum_candidate_frames(test_seam_);
  if (candidate_limit < millisecond_frames) return audio_chunk_export_result::invalid_argument;
  std::uint64_t start_frame{};
  std::uint32_t index{};
  while (start_frame < audio.frame_count) {
    if (cancellation_.is_cancelled()) return audio_chunk_export_result::cancelled;
    const auto remaining = audio.frame_count - start_frame;
    auto candidate_frames = initial_candidate_frames(max_chunk_bytes, remaining, candidate_limit);
    std::vector<std::int16_t> candidate_samples;
    const auto read = read_candidate(spool_path, start_frame, candidate_frames, candidate_samples);
    if (read != audio_chunk_export_result::success) return read;
    const auto owned_candidate_bytes = static_cast<std::uint64_t>(candidate_samples.size()) * bytes_per_frame;
    bool published{};
    while (!published) {
      if (cancellation_.is_cancelled()) return audio_chunk_export_result::cancelled;
      const auto reaches_end = candidate_frames >= remaining;
      if (!reaches_end && candidate_frames <= overlap_frames) return audio_chunk_export_result::io_failure;
      const auto suffix = chunk_suffix(index);
      const auto partial_path = canonical_output / (prefix + suffix + L".partial");
      const auto final_path = canonical_output / (prefix + suffix + L".m4a");
      if (fs::weakly_canonical(partial_path.parent_path()) != canonical_output ||
          fs::weakly_canonical(final_path.parent_path()) != canonical_output)
        return audio_chunk_export_result::invalid_argument;
      fs::remove(partial_path, ignored);
      partial_file_guard partial_guard(partial_path);
      const auto encoded = encode_candidate(
        std::span(candidate_samples).first(static_cast<size_t>(candidate_frames)), owned_candidate_bytes,
        partial_path, aac_bitrate, cancellation_, test_seam_);
      if (encoded != audio_chunk_export_result::success) {
        fs::remove(partial_path, ignored);
        return encoded;
      }
      if (cancellation_.is_cancelled()) {
        fs::remove(partial_path, ignored);
        return audio_chunk_export_result::cancelled;
      }
      std::uint64_t byte_size{};
      try { byte_size = fs::file_size(partial_path); }
      catch (const fs::filesystem_error&) { fs::remove(partial_path, ignored); return audio_chunk_export_result::io_failure; }
      if (byte_size == 0 || byte_size > max_chunk_bytes) {
        fs::remove(partial_path, ignored);
        if (candidate_frames <= millisecond_frames) return audio_chunk_export_result::io_failure;
        candidate_frames = (candidate_frames * 3 / 4) / millisecond_frames * millisecond_frames;
        candidate_frames = (std::max<std::uint64_t>)(candidate_frames, millisecond_frames);
        continue;
      }
      const auto hash = sha256_file(partial_path);
      if (hash.empty()) { fs::remove(partial_path, ignored); return audio_chunk_export_result::io_failure; }
      if (fs::exists(final_path) || !MoveFileExW(partial_path.c_str(), final_path.c_str(), MOVEFILE_WRITE_THROUGH)) {
        fs::remove(partial_path, ignored); return audio_chunk_export_result::io_failure;
      }
      partial_guard.release();
      const auto end_frame = start_frame + candidate_frames;
      audio_chunk_export_record record{
        index,
        final_path,
        audio.first_milliseconds + static_cast<std::int64_t>(start_frame * 1000 / normalized_sample_rate),
        audio.first_milliseconds + static_cast<std::int64_t>(end_frame * 1000 / normalized_sample_rate),
        hash,
        byte_size,
        audio.sample_rate,
        encoded_sample_rate,
        audio.channels
      };
      on_chunk(record);
      published = true;
      ++index;
      if (end_frame >= audio.frame_count) start_frame = audio.frame_count;
      else start_frame = end_frame - overlap_frames;
    }
  }
  return cancellation_.is_cancelled() ? audio_chunk_export_result::cancelled : audio_chunk_export_result::success;
}
