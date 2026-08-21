namespace ZoomRecorder.Core.Meetings;

public static class ZoomMeetingLaunchUri
{
    public static Uri Create(MeetingJoinRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var suffix = string.IsNullOrWhiteSpace(request.Passcode)
            ? string.Empty
            : $"?pwd={Uri.EscapeDataString(request.Passcode)}";

        return new Uri($"https://zoom.us/j/{request.MeetingId}{suffix}", UriKind.Absolute);
    }
}
