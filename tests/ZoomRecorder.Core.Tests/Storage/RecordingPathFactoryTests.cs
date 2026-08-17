using ZoomRecorder.Core.Storage;

namespace ZoomRecorder.Core.Tests.Storage;

public sealed class RecordingPathFactoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 17, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Create_sanitizes_meeting_label()
    {
        var path = RecordingPathFactory.Create("C:\\Videos", "Team: Sync", Start, _ => false);

        Assert.Equal("C:\\Videos\\Team_ Sync - 2026-08-17 093000.mp4", path);
    }

    [Fact]
    public void Create_uses_fallback_label_and_collision_suffix()
    {
        var path = RecordingPathFactory.Create(
            "C:\\Videos",
            null,
            Start,
            candidate => candidate.EndsWith("Zoom Meeting - 2026-08-17 093000.mp4", StringComparison.Ordinal));

        Assert.Equal("C:\\Videos\\Zoom Meeting - 2026-08-17 093000 (2).mp4", path);
    }
}
