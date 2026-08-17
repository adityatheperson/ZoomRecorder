#pragma once

#include <functional>
#include <memory>
#include <span>

class WasapiSourceImpl;

class WasapiSource {
 public:
  using SamplesCallback = std::function<void(std::span<const float>, unsigned int, unsigned short)>;
  using HealthCallback = std::function<void(bool, const char*)>;
  WasapiSource(bool loopback, SamplesCallback samples, HealthCallback health);
  ~WasapiSource();
  bool start();
  void stop();
  bool is_ready() const;
 private:
  std::unique_ptr<WasapiSourceImpl> impl_;
};
