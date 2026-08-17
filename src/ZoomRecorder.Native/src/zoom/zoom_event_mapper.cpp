#include "zoom_event_mapper.h"

AppMeetingEvent ZoomEventMapper::map(ZoomMeetingStatus status) {
  switch (status) {
    case ZoomMeetingStatus::Connecting: return AppMeetingEvent::Connecting;
    case ZoomMeetingStatus::InMeeting: return AppMeetingEvent::Entered;
    case ZoomMeetingStatus::Failed: return AppMeetingEvent::Failed;
    case ZoomMeetingStatus::Ended:
      if (ended_) return AppMeetingEvent::IgnoredDuplicate;
      ended_ = true; return AppMeetingEvent::Ended;
    default: return AppMeetingEvent::Ignored;
  }
}
