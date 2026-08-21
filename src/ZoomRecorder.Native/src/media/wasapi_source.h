#pragma once

#include <functional>
#include <memory>
#include <span>
#include <cstdint>

class WasapiSourceImpl;

enum class WasapiStartupStage {
  ThreadStarted,
  ClientStarted,
  FirstPacket,
};

constexpr bool wasapi_stage_is_ready(WasapiStartupStage stage) {
  return stage != WasapiStartupStage::ThreadStarted;
}

class WasapiSource {
 public:
  using SamplesCallback = std::function<void(std::span<const float>, unsigned int, unsigned short, std::int64_t)>;
  using HealthCallback = std::function<void(bool, const char*)>;
  WasapiSource(bool loopback, SamplesCallback samples, HealthCallback health);
  ~WasapiSource();
  bool start();
  void stop();
  bool is_ready() const;
 private:
  std::unique_ptr<WasapiSourceImpl> impl_;
};
