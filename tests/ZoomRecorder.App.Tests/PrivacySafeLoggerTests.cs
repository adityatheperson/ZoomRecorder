using ZoomRecorder.App.Services;
using ZoomRecorder.Core.Meetings;

namespace ZoomRecorder.App.Tests;

public sealed class PrivacySafeLoggerTests
{
    [Fact]
    public void Meeting_request_never_logs_private_values()
    {
        var text = PrivacySafeLogger.Serialize(new MeetingJoinRequest("1234567890", "very-secret"));
        Assert.DoesNotContain("very-secret", text);
        Assert.DoesNotContain("1234567890", text);
        Assert.Contains("7890", text);
    }
}
