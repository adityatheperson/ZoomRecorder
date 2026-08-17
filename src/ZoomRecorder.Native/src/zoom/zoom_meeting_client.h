#pragma once

#include <functional>
#include <memory>
#include <string>
#include <windows.h>

class ZoomMeetingClientImpl;

class ZoomMeetingClient {
 public:
  using EventSink = std::function<void(const char*)>;
  explicit ZoomMeetingClient(EventSink sink);
  ~ZoomMeetingClient();
  int prepare(const std::string& request_json);
  int enter();
  void set_host(HWND host);
 private:
  std::unique_ptr<ZoomMeetingClientImpl> impl_;
};
