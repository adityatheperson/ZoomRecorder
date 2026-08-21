using System.Text.Json;
using ZoomRecorder.Core.Meetings;

namespace ZoomRecorder.App.Services;

public static class PrivacySafeLogger
{
    public static string Serialize(MeetingJoinRequest request) => JsonSerializer.Serialize(new
    {
        Event = "meeting_join_requested",
        MeetingIdSuffix = request.MeetingId.Length > 4 ? request.MeetingId[^4..] : "redacted",
        HasPasscode = !string.IsNullOrEmpty(request.Passcode)
    });
}
