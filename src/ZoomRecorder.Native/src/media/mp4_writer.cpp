#include "mp4_writer.h"

#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <wrl/client.h>
#include <windows.h>
#include <algorithm>

using Microsoft::WRL::ComPtr;

class Mp4WriterImpl {
 public:
  ~Mp4WriterImpl() { finalize(); }

  bool open(const std::wstring& final_path, unsigned width, unsigned height, unsigned frame_rate) {
    if (writer_ || final_path.empty() || width == 0 || height == 0 || frame_rate == 0) return false;
    final_path_ = final_path; partial_path_ = final_path + L".partial.mp4"; frame_duration_ = 10'000'000LL / frame_rate;
    if (FAILED(MFStartup(MF_VERSION))) return false;
    mf_started_ = true;
    ComPtr<IMFAttributes> attributes; MFCreateAttributes(&attributes, 2);
    attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE);
    attributes->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE);
    if (FAILED(MFCreateSinkWriterFromURL(partial_path_.c_str(), nullptr, attributes.Get(), &writer_))) return false;

    ComPtr<IMFMediaType> output_video; MFCreateMediaType(&output_video);
    output_video->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video); output_video->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
    output_video->SetUINT32(MF_MT_AVG_BITRATE, 8'000'000); output_video->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    MFSetAttributeSize(output_video.Get(), MF_MT_FRAME_SIZE, width, height); MFSetAttributeRatio(output_video.Get(), MF_MT_FRAME_RATE, frame_rate, 1);
    MFSetAttributeRatio(output_video.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    if (FAILED(writer_->AddStream(output_video.Get(), &video_stream_))) return false;
    ComPtr<IMFMediaType> input_video; MFCreateMediaType(&input_video);
    input_video->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video); input_video->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_ARGB32);
    input_video->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    MFSetAttributeSize(input_video.Get(), MF_MT_FRAME_SIZE, width, height); MFSetAttributeRatio(input_video.Get(), MF_MT_FRAME_RATE, frame_rate, 1);
    MFSetAttributeRatio(input_video.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    if (FAILED(writer_->SetInputMediaType(video_stream_, input_video.Get(), nullptr))) return false;

    ComPtr<IMFMediaType> output_audio; MFCreateMediaType(&output_audio);
    output_audio->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio); output_audio->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC);
    output_audio->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2); output_audio->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, 48000);
    output_audio->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16); output_audio->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 24000);
    if (FAILED(writer_->AddStream(output_audio.Get(), &audio_stream_))) return false;
    ComPtr<IMFMediaType> input_audio; MFCreateMediaType(&input_audio);
    input_audio->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio); input_audio->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_Float);
    input_audio->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2); input_audio->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, 48000);
    input_audio->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 32); input_audio->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, 8);
    input_audio->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 48000 * 8);
    if (FAILED(writer_->SetInputMediaType(audio_stream_, input_audio.Get(), nullptr))) return false;
    return SUCCEEDED(writer_->BeginWriting());
  }

  bool write_video(ID3D11Texture2D* texture, std::int64_t timestamp) {
    if (!writer_ || !texture) return false;
    ComPtr<IMFMediaBuffer> buffer; if (FAILED(MFCreateDXGISurfaceBuffer(__uuidof(ID3D11Texture2D), texture, 0, FALSE, &buffer))) return false;
    ComPtr<IMFSample> sample; MFCreateSample(&sample); sample->AddBuffer(buffer.Get());
    const auto time = normalize(timestamp, first_video_); sample->SetSampleTime(time); sample->SetSampleDuration(frame_duration_);
    return SUCCEEDED(writer_->WriteSample(video_stream_, sample.Get()));
  }

  bool write_audio(std::span<const float> audio, std::int64_t timestamp) {
    if (!writer_ || audio.empty() || audio.size() % 2 != 0) return false;
    const auto bytes = static_cast<DWORD>(audio.size_bytes()); ComPtr<IMFMediaBuffer> buffer;
    if (FAILED(MFCreateMemoryBuffer(bytes, &buffer))) return false;
    BYTE* target{}; if (FAILED(buffer->Lock(&target, nullptr, nullptr))) return false;
    memcpy(target, audio.data(), bytes); buffer->Unlock(); buffer->SetCurrentLength(bytes);
    ComPtr<IMFSample> sample; MFCreateSample(&sample); sample->AddBuffer(buffer.Get());
    sample->SetSampleTime(normalize(timestamp, first_audio_)); sample->SetSampleDuration(static_cast<LONGLONG>(audio.size() / 2) * 10'000'000LL / 48000);
    return SUCCEEDED(writer_->WriteSample(audio_stream_, sample.Get()));
  }

  bool finalize() {
    if (finalized_) return final_result_;
    finalized_ = true;
    if (!writer_) { shutdown(); return false; }
    final_result_ = SUCCEEDED(writer_->Finalize()); writer_.Reset(); shutdown();
    if (final_result_) final_result_ = MoveFileExW(partial_path_.c_str(), final_path_.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) != FALSE;
    return final_result_;
  }
  bool is_open() const { return writer_ != nullptr && !finalized_; }
  const std::wstring& final_path() const { return final_path_; }

 private:
  std::int64_t normalize(std::int64_t value, std::int64_t& first) { if (first < 0) first = value; return std::max<std::int64_t>(0, value - first); }
  void shutdown() { if (mf_started_) { MFShutdown(); mf_started_ = false; } }
  ComPtr<IMFSinkWriter> writer_; DWORD video_stream_{}, audio_stream_{}; std::int64_t frame_duration_{};
  std::int64_t first_video_{-1}, first_audio_{-1}; bool mf_started_{}, finalized_{}, final_result_{};
  std::wstring final_path_, partial_path_;
};

Mp4Writer::Mp4Writer() : impl_(std::make_unique<Mp4WriterImpl>()) {}
Mp4Writer::~Mp4Writer() = default;
bool Mp4Writer::open(const std::wstring& path, unsigned w, unsigned h, unsigned fps) { return impl_->open(path, w, h, fps); }
bool Mp4Writer::write_video(ID3D11Texture2D* value, std::int64_t time) { return impl_->write_video(value, time); }
bool Mp4Writer::write_audio(std::span<const float> value, std::int64_t time) { return impl_->write_audio(value, time); }
bool Mp4Writer::finalize() { return impl_->finalize(); }
bool Mp4Writer::is_open() const { return impl_->is_open(); }
const std::wstring& Mp4Writer::final_path() const { return impl_->final_path(); }
