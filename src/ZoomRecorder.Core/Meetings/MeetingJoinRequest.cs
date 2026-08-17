namespace ZoomRecorder.Core.Meetings;

public sealed record MeetingJoinRequest(string MeetingId, string? Passcode, string DisplayName);

public sealed class MeetingInputException(string message) : ArgumentException(message);
