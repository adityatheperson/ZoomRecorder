namespace ZoomRecorder.Core.Meetings;

public sealed record MeetingJoinRequest(string MeetingId, string? Passcode);

public sealed class MeetingInputException(string message) : ArgumentException(message);
