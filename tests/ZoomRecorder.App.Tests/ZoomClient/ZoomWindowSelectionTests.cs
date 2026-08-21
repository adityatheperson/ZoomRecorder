using ZoomRecorder.App.ZoomClient;

namespace ZoomRecorder.App.Tests.ZoomClient;

public sealed class ZoomWindowSelectionTests
{
    [Fact]
    public void Selects_one_large_visible_zoom_meeting_window()
    {
        var home = Window((nint)1, "Zoom", "Zoom Workplace", 900, 700);
        var meeting = Window((nint)2, "Zoom", "Zoom Meeting", 1400, 900);

        var result = ZoomWindowSelection.Select([home, meeting]);

        Assert.Equal(ZoomWindowSelectionKind.Selected, result.Kind);
        Assert.Equal((nint)2, result.Handle);
    }

    [Fact]
    public void Equal_meeting_candidates_are_ambiguous()
    {
        var result = ZoomWindowSelection.Select([
            Window((nint)2, "Zoom", "Meeting", 1400, 900),
            Window((nint)3, "Zoom", "Meeting", 1400, 900)]);

        Assert.Equal(ZoomWindowSelectionKind.Ambiguous, result.Kind);
        Assert.Equal(nint.Zero, result.Handle);
    }

    [Theory]
    [InlineData("notepad", "Meeting", true, false, 1400, 900)]
    [InlineData("Zoom", "Meeting", false, false, 1400, 900)]
    [InlineData("Zoom", "Meeting", true, true, 1400, 900)]
    [InlineData("Zoom", "Meeting", true, false, 639, 900)]
    [InlineData("Zoom", "Meeting", true, false, 1400, 359)]
    [InlineData("Zoom", "Zoom Workplace", true, false, 1400, 900)]
    [InlineData("Zoom", "Settings", true, false, 1400, 900)]
    [InlineData("Zoom", "Sign In", true, false, 1400, 900)]
    [InlineData("Zoom", "Zoom Updater", true, false, 1400, 900)]
    public void Rejects_non_meeting_windows(
        string processName,
        string title,
        bool visible,
        bool minimized,
        int width,
        int height)
    {
        var result = ZoomWindowSelection.Select([
            Window((nint)1, processName, title, width, height, visible, minimized)]);

        Assert.Equal(ZoomWindowSelectionKind.None, result.Kind);
    }

    [Theory]
    [InlineData("ZPSettingsWndClass")]
    [InlineData("ZPLoginWndClass")]
    [InlineData("ZPUpdaterWndClass")]
    public void Rejects_known_non_meeting_window_classes(string className)
    {
        var result = ZoomWindowSelection.Select([
            Window((nint)1, "Zoom", "Meeting", 1400, 900, className: className)]);

        Assert.Equal(ZoomWindowSelectionKind.None, result.Kind);
    }

    [Fact]
    public void Rejects_unknown_zoom_window_without_affirmative_meeting_evidence()
    {
        var result = ZoomWindowSelection.Select([
            Window((nint)1, "Zoom", "Team Chat", 1400, 900, className: "ZPUnknownWndClass")]);

        Assert.Equal(ZoomWindowSelectionKind.None, result.Kind);
    }

    private static ZoomWindowDescription Window(
        nint handle,
        string processName,
        string title,
        int width,
        int height,
        bool visible = true,
        bool minimized = false,
        string className = "ZPContentViewWndClass") =>
        new(handle, 42, processName, className, title, visible, minimized, width, height);
}
