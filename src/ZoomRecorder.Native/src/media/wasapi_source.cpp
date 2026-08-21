#include "wasapi_source.h"

#include <windows.h>
#include <audioclient.h>
#include <mmdeviceapi.h>
#include <wrl/client.h>
#include <atomic>
#include <thread>
#include <vector>
#include <chrono>

using Microsoft::WRL::ComPtr;

class WasapiSourceImpl {
 public:
  WasapiSourceImpl(bool loopback, WasapiSource::SamplesCallback samples, WasapiSource::HealthCallback health)
      : loopback_(loopback), samples_(std::move(samples)), health_(std::move(health)) {}
  ~WasapiSourceImpl() { stop(); }

  bool start() {
    if (worker_.joinable()) return false;
    stop_event_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!stop_event_) return false;
    worker_ = std::jthread([this] { run(); });
    return true;
  }
  void stop() {
    if (stop_event_) SetEvent(stop_event_);
    if (worker_.joinable()) worker_.join();
    if (stop_event_) CloseHandle(stop_event_);
    stop_event_ = nullptr; ready_ = false;
  }
  bool is_ready() const { return ready_; }

 private:
  void run() {
    if (FAILED(CoInitializeEx(nullptr, COINIT_MULTITHREADED))) { fail("Audio COM initialization failed"); return; }
    HANDLE sample_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    ComPtr<IMMDeviceEnumerator> enumerator; ComPtr<IMMDevice> device; ComPtr<IAudioClient> client; ComPtr<IAudioCaptureClient> capture;
    WAVEFORMATEX* format{};
    auto cleanup = [&] { if (client) client->Stop(); if (format) CoTaskMemFree(format); if (sample_event) CloseHandle(sample_event); CoUninitialize(); };
    if (FAILED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator))) ||
        FAILED(enumerator->GetDefaultAudioEndpoint(loopback_ ? eRender : eCapture, loopback_ ? eConsole : eCommunications, &device)) ||
        FAILED(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, &client)) || FAILED(client->GetMixFormat(&format))) {
      fail(loopback_ ? "Meeting audio device unavailable" : "Microphone unavailable"); cleanup(); return;
    }
    const DWORD flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK | (loopback_ ? AUDCLNT_STREAMFLAGS_LOOPBACK : 0);
    if (FAILED(client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 0, 0, format, nullptr)) ||
        FAILED(client->SetEventHandle(sample_event)) || FAILED(client->GetService(IID_PPV_ARGS(&capture))) || FAILED(client->Start())) {
      fail(loopback_ ? "Meeting audio capture failed" : "Microphone capture failed"); cleanup(); return;
    }
    if (wasapi_stage_is_ready(WasapiStartupStage::ClientStarted) && !ready_.exchange(true))
      health_(true, loopback_ ? "Meeting audio ready" : "Microphone ready");
    HANDLE waits[]{stop_event_, sample_event};
    while (WaitForMultipleObjects(2, waits, FALSE, INFINITE) == WAIT_OBJECT_0 + 1) {
      UINT32 packets{}; if (FAILED(capture->GetNextPacketSize(&packets))) break;
      while (packets) {
        BYTE* bytes{}; UINT32 frames{}; DWORD packet_flags{};
        if (FAILED(capture->GetBuffer(&bytes, &frames, &packet_flags, nullptr, nullptr))) break;
        std::vector<float> converted(static_cast<size_t>(frames) * format->nChannels);
        if (packet_flags & AUDCLNT_BUFFERFLAGS_SILENT) {
          std::fill(converted.begin(), converted.end(), 0.0f);
        } else if (format->wFormatTag == WAVE_FORMAT_IEEE_FLOAT || (format->wFormatTag == WAVE_FORMAT_EXTENSIBLE && format->wBitsPerSample == 32)) {
          auto* input = reinterpret_cast<const float*>(bytes); std::copy(input, input + converted.size(), converted.begin());
        } else if (format->wBitsPerSample == 16) {
          auto* input = reinterpret_cast<const short*>(bytes); for (size_t i = 0; i < converted.size(); ++i) converted[i] = input[i] / 32768.0f;
        } else { capture->ReleaseBuffer(frames); fail("Unsupported audio device format"); cleanup(); return; }
        const auto now = std::chrono::steady_clock::now().time_since_epoch();
        samples_(converted, format->nSamplesPerSec, format->nChannels, std::chrono::duration_cast<std::chrono::nanoseconds>(now).count() / 100);
        capture->ReleaseBuffer(frames); if (FAILED(capture->GetNextPacketSize(&packets))) packets = 0;
      }
    }
    cleanup();
  }
  void fail(const char* message) { health_(false, message); }
  bool loopback_{}; WasapiSource::SamplesCallback samples_; WasapiSource::HealthCallback health_;
  std::atomic_bool ready_{}; HANDLE stop_event_{}; std::jthread worker_;
};

WasapiSource::WasapiSource(bool loopback, SamplesCallback samples, HealthCallback health)
    : impl_(std::make_unique<WasapiSourceImpl>(loopback, std::move(samples), std::move(health))) {}
WasapiSource::~WasapiSource() = default;
bool WasapiSource::start() { return impl_->start(); }
void WasapiSource::stop() { impl_->stop(); }
bool WasapiSource::is_ready() const { return impl_->is_ready(); }
