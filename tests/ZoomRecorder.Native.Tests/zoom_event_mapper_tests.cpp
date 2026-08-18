#include "zoom_event_mapper.h"

bool run_zoom_event_mapper_tests() {
  ZoomEventMapper mapper;
  ZoomEventMapper removed_after_join;
  ZoomEventMapper removed_before_join;
  ZoomEventMapper participant_leaves;
  return mapper.map(ZoomMeetingStatus::Connecting) == AppMeetingEvent::Connecting &&
         mapper.map(ZoomMeetingStatus::InMeeting) == AppMeetingEvent::Entered &&
         mapper.map(ZoomMeetingStatus::Ended) == AppMeetingEvent::Ended &&
         mapper.map(ZoomMeetingStatus::Ended) == AppMeetingEvent::IgnoredDuplicate &&
         removed_after_join.map(ZoomMeetingStatus::InMeeting) == AppMeetingEvent::Entered &&
         removed_after_join.map(ZoomMeetingStatus::Failed, 61) == AppMeetingEvent::Ended &&
         removed_before_join.map(ZoomMeetingStatus::Failed, 61) == AppMeetingEvent::Failed &&
         participant_leaves.map(ZoomMeetingStatus::InMeeting) == AppMeetingEvent::Entered &&
         participant_leaves.map(ZoomMeetingStatus::Disconnecting) == AppMeetingEvent::Ended &&
         participant_leaves.map(ZoomMeetingStatus::Ended) == AppMeetingEvent::IgnoredDuplicate;
}
