using ZoomRecorder.Core.Meetings;

namespace ZoomRecorder.Core.Tests.Meetings;

public sealed class ZoomMeetingLaunchUriTests
{
    [Theory]
    [InlineData("1234567890", null, "https://zoom.us/j/1234567890")]
    [InlineData("1234567890", "a b&c", "https://zoom.us/j/1234567890?pwd=a%20b%26c")]
    public void Creates_https_join_uri(string id, string? passcode, string expected)
    {
        Assert.Equal(expected, ZoomMeetingLaunchUri.Create(new(id, passcode)).AbsoluteUri);
    }
}
