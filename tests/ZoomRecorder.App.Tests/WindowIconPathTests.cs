using ZoomRecorder.App.Services;

namespace ZoomRecorder.App.Tests;

public sealed class WindowIconPathTests
{
    [Fact]
    public void ResolveUsesPackagedAssetsDirectory()
    {
        var result = WindowIconPath.Resolve(@"C:\ZoomRecorder\app");

        Assert.Equal(@"C:\ZoomRecorder\app\Assets\ZoomRecorder.ico", result);
    }
}
