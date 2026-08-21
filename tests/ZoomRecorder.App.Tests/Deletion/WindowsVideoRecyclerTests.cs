using ZoomRecorder.App.Deletion;

namespace ZoomRecorder.App.Tests.Deletion;

public sealed class WindowsVideoRecyclerTests
{
    [Fact]
    public async Task Recycler_canonicalizes_the_exact_recording_path_and_uses_recycle_bin()
    {
        var shell = new FakeShell();
        var recycler = new WindowsVideoRecycler(shell, _ => true);
        var path = Path.Combine(Path.GetTempPath(), "folder", "..", "lecture.mp4");

        var result = await recycler.RecycleAsync(path, default);

        Assert.True(result.Recycled);
        Assert.Equal(Path.GetFullPath(path), result.RecycledPath);
        Assert.Equal(Path.GetFullPath(path), shell.Path);
        Assert.True(shell.SendToRecycleBin);
    }

    [Fact]
    public async Task Unsupported_shell_returns_unavailable_without_permanent_delete()
    {
        var shell = new FakeShell { Failure = new PlatformNotSupportedException() };
        var recycler = new WindowsVideoRecycler(shell, _ => true);

        var result = await recycler.RecycleAsync("C:\\recordings\\lecture.mp4", default);

        Assert.False(result.Recycled);
        Assert.Null(result.RecycledPath);
        Assert.Equal(1, shell.Calls);
    }

    [Fact]
    public async Task Missing_recording_is_not_reported_as_recycled()
    {
        var shell = new FakeShell();
        var recycler = new WindowsVideoRecycler(shell, _ => false);

        var result = await recycler.RecycleAsync("C:\\recordings\\missing.mp4", default);

        Assert.False(result.Recycled);
        Assert.Equal(0, shell.Calls);
    }

    private sealed class FakeShell : IRecycleShell
    {
        public Exception? Failure { get; init; }
        public int Calls { get; private set; }
        public string? Path { get; private set; }
        public bool SendToRecycleBin { get; private set; }

        public void Delete(string path, bool sendToRecycleBin)
        {
            Calls++;
            Path = path;
            SendToRecycleBin = sendToRecycleBin;
            if (Failure is not null) throw Failure;
        }
    }
}
