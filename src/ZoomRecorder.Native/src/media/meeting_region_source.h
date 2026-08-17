#pragma once

#include <windows.h>
#include <functional>
#include <memory>

class MeetingRegionSourceImpl;

class MeetingRegionSource {
 public:
  using HealthCallback = std::function<void(bool, const char*)>;
  MeetingRegionSource(HWND target, HealthCallback health);
  ~MeetingRegionSource();
  bool start();
  void stop();
  bool is_ready() const;
 private:
  std::unique_ptr<MeetingRegionSourceImpl> impl_;
};
