#include "zoom_event_mapper.h"

bool run_zoom_event_mapper_tests() {
  ZoomEventMapper mapper;
  return mapper.map(ZoomMeetingStatus::Connecting) == AppMeetingEvent::Connecting &&
         mapper.map(ZoomMeetingStatus::InMeeting) == AppMeetingEvent::Entered &&
         mapper.map(ZoomMeetingStatus::Ended) == AppMeetingEvent::Ended &&
         mapper.map(ZoomMeetingStatus::Ended) == AppMeetingEvent::IgnoredDuplicate;
}
