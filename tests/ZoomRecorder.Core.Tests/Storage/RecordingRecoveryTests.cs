using ZoomRecorder.Core.Storage;

namespace ZoomRecorder.Core.Tests.Storage;

public sealed class RecordingRecoveryTests
{
    [Theory]
    [InlineData("meeting.partial", true)]
    [InlineData("meeting.partial.mp4", true)]
    [InlineData("meeting.PARTIAL", true)]
    [InlineData("meeting.mp4", false)]
    public void Detects_only_partial_recordings(string path, bool expected) => Assert.Equal(expected, RecordingRecovery.IsCandidate(path));
}
