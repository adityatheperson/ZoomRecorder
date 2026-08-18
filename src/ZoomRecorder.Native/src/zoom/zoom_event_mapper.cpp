#include "zoom_event_mapper.h"

AppMeetingEvent ZoomEventMapper::map(ZoomMeetingStatus status, int failure_code) {
  switch (status) {
    case ZoomMeetingStatus::Connecting: return AppMeetingEvent::Connecting;
    case ZoomMeetingStatus::InMeeting: entered_ = true; return AppMeetingEvent::Entered;
    case ZoomMeetingStatus::Disconnecting:
      if (!entered_) return AppMeetingEvent::Ignored;
      [[fallthrough]];
    case ZoomMeetingStatus::Failed:
      if (status == ZoomMeetingStatus::Failed && !(entered_ && failure_code == 61)) return AppMeetingEvent::Failed;
      [[fallthrough]];
    case ZoomMeetingStatus::Ended:
      if (ended_) return AppMeetingEvent::IgnoredDuplicate;
      ended_ = true; return AppMeetingEvent::Ended;
    default: return AppMeetingEvent::Ignored;
  }
}
