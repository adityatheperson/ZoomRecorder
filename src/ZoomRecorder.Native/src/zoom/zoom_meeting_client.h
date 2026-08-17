#pragma once

#include <functional>
#include <memory>
#include <string>

class ZoomMeetingClientImpl;

class ZoomMeetingClient {
 public:
  using EventSink = std::function<void(const char*)>;
  explicit ZoomMeetingClient(EventSink sink);
  ~ZoomMeetingClient();
  int prepare(const std::string& request_json);
  int enter();
 private:
  std::unique_ptr<ZoomMeetingClientImpl> impl_;
};
