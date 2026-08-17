#include "recording_pipeline.h"
#include "audio_mixer.h"
#include "meeting_region_source.h"
#include "mp4_writer.h"
#include "recording_readiness.h"
#include "wasapi_source.h"

#include <condition_variable>
#include <mutex>
#include <vector>
#include <chrono>
#include <algorithm>

namespace {
std::vector<float> normalize_audio(std::span<const float> input, unsigned rate, unsigned short channels) {
  if (input.empty() || rate == 0 || channels == 0) return {};
  const auto input_frames = input.size() / channels;
  const auto output_frames = static_cast<size_t>((static_cast<unsigned long long>(input_frames) * 48000) / rate);
  std::vector<float> output(output_frames * 2);
  for (size_t frame = 0; frame < output_frames; ++frame) {
    const auto source_position = static_cast<double>(frame) * rate / 48000.0;
    const auto first = (std::min)(static_cast<size_t>(source_position), input_frames - 1);
    const auto second = (std::min)(first + 1, input_frames - 1);
    const auto fraction = static_cast<float>(source_position - first);
    for (size_t channel = 0; channel < 2; ++channel) {
      const auto source_channel = channels == 1 ? 0 : (std::min)(channel, static_cast<size_t>(channels - 1));
      const auto a = input[first * channels + source_channel], b = input[second * channels + source_channel];
      output[frame * 2 + channel] = a + ((b - a) * fraction);
    }
  }
  return output;
}
}

class RecordingPipelineImpl {
 public:
  explicit RecordingPipelineImpl(RecordingPipeline::HealthCallback health) : health_(std::move(health)), mixer_({48000, 2}) {}

  bool start(HWND host, const std::wstring& output) {
    RECT bounds{}; if (!IsWindow(host) || !GetClientRect(host, &bounds)) return fail("Meeting capture area unavailable");
    const auto width = static_cast<unsigned>(bounds.right - bounds.left), height = static_cast<unsigned>(bounds.bottom - bounds.top);
    if (!writer_.open(output, width, height)) return fail("MP4 encoder or output file unavailable");
    mark(RecordingComponent::Encoder); mark(RecordingComponent::OutputFile);
    video_ = std::make_unique<MeetingRegionSource>(host,
      [this](ID3D11Texture2D* texture, std::int64_t time) { std::scoped_lock lock(writer_mutex_); if (!writer_.write_video(texture, time)) fail("Video encoder stopped"); },
      [this](bool ok, const char* message) { component_health(RecordingComponent::Video, ok, message); });
    meeting_audio_ = std::make_unique<WasapiSource>(true,
      [this](auto samples, unsigned rate, unsigned short channels, std::int64_t time) { audio(false, samples, rate, channels, time); },
      [this](bool ok, const char* message) { component_health(RecordingComponent::MeetingAudio, ok, message); });
    microphone_ = std::make_unique<WasapiSource>(false,
      [this](auto samples, unsigned rate, unsigned short channels, std::int64_t time) { audio(true, samples, rate, channels, time); },
      [this](bool ok, const char* message) { component_health(RecordingComponent::Microphone, ok, message); });
    // Each source reports its own diagnostic. Do not overwrite it with a generic
    // error, or the managed client cannot tell which Windows API failed.
    if (!video_->start()) return false;
    if (!meeting_audio_->start()) return fail("Meeting audio capture thread could not start");
    if (!microphone_->start()) return fail("Microphone capture thread could not start");
    std::unique_lock lock(state_mutex_);
    state_changed_.wait_for(lock, std::chrono::seconds(8), [this] { return readiness_.can_enter_meeting() || readiness_.has_failed(); });
    if (!readiness_.can_enter_meeting()) { lock.unlock(); stop_and_finalize(); return fail("Recording sources did not become ready"); }
    return true;
  }

  bool stop_and_finalize() {
    if (microphone_) microphone_->stop(); if (meeting_audio_) meeting_audio_->stop(); if (video_) video_->stop();
    std::scoped_lock lock(writer_mutex_); return writer_.finalize();
  }
  bool is_ready() const { std::scoped_lock lock(state_mutex_); return readiness_.can_enter_meeting(); }

 private:
  void audio(bool microphone, std::span<const float> samples, unsigned rate, unsigned short channels, std::int64_t time) {
    std::scoped_lock lock(audio_mutex_);
    auto& target = microphone ? microphone_buffer_ : meeting_buffer_; target = normalize_audio(samples, rate, channels);
    if (target.empty()) { component_health(microphone ? RecordingComponent::Microphone : RecordingComponent::MeetingAudio, false, "Audio conversion failed"); return; }
    if (meeting_buffer_.empty() || microphone_buffer_.empty()) return;
    auto mixed = mixer_.mix(meeting_buffer_, microphone_buffer_); meeting_buffer_.clear(); microphone_buffer_.clear();
    std::scoped_lock writer_lock(writer_mutex_); if (!writer_.write_audio(mixed, time)) fail("Audio encoder stopped");
  }
  void component_health(RecordingComponent component, bool ok, const char* message) {
    { std::scoped_lock lock(state_mutex_); ok ? readiness_.ready(component) : readiness_.failed(component); }
    health_(ok, message); state_changed_.notify_all();
  }
  void mark(RecordingComponent component) { component_health(component, true, "Recording component ready"); }
  bool fail(const char* message) { health_(false, message); return false; }
  RecordingPipeline::HealthCallback health_; mutable std::mutex state_mutex_, audio_mutex_, writer_mutex_; std::condition_variable state_changed_;
  RecordingReadiness readiness_; AudioMixer mixer_; Mp4Writer writer_;
  std::unique_ptr<MeetingRegionSource> video_; std::unique_ptr<WasapiSource> meeting_audio_, microphone_;
  std::vector<float> meeting_buffer_, microphone_buffer_;
};

RecordingPipeline::RecordingPipeline(HealthCallback health) : impl_(std::make_unique<RecordingPipelineImpl>(std::move(health))) {}
RecordingPipeline::~RecordingPipeline() = default;
bool RecordingPipeline::start(HWND host, const std::wstring& path) { return impl_->start(host, path); }
bool RecordingPipeline::stop_and_finalize() { return impl_->stop_and_finalize(); }
bool RecordingPipeline::is_ready() const { return impl_->is_ready(); }
