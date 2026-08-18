#pragma once

enum class ZoomMeetingStatus { Connecting, InMeeting, Disconnecting, Ended, Failed, Other };
enum class AppMeetingEvent { Connecting, Entered, Ended, Failed, Ignored, IgnoredDuplicate };

class ZoomEventMapper {
 public:
  AppMeetingEvent map(ZoomMeetingStatus status, int failure_code = 0);
 private:
  bool entered_{}, ended_{};
};
