#pragma once

#include <windows.h>
#include <d3d11.h>
#include <cstdint>
#include <functional>
#include <memory>

class MeetingRegionSourceImpl;

class MeetingRegionSource {
 public:
  using HealthCallback = std::function<void(bool, const char*)>;
  using FrameCallback = std::function<void(ID3D11Texture2D*, std::int64_t)>;
  using EndedCallback = std::function<void()>;
  MeetingRegionSource(HWND target, ID3D11Device* device, FrameCallback frame, HealthCallback health, EndedCallback ended);
  ~MeetingRegionSource();
  bool start();
  void stop();
  bool is_ready() const;
 private:
  std::unique_ptr<MeetingRegionSourceImpl> impl_;
};
