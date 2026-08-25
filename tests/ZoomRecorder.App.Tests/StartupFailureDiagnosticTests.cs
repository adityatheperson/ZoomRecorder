using ZoomRecorder.App;

namespace ZoomRecorder.App.Tests;

public sealed class StartupFailureDiagnosticTests
{
    [Fact]
    public void Write_captures_exception_type_hresult_inner_exception_and_stack()
    {
        using var temp = new TestDirectory();
        Exception failure;
        try
        {
            ThrowStartupFailure();
            throw new InvalidOperationException("unreachable");
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        var path = StartupFailureDiagnostic.Write(failure, temp.Path);
        var diagnostic = File.ReadAllText(path);

        Assert.Contains("System.InvalidOperationException", diagnostic);
        Assert.Contains("outer startup failure", diagnostic);
        Assert.Contains("System.ArgumentException: inner XAML detail", diagnostic);
        Assert.Contains("HRESULT: 0x80131509", diagnostic);
        Assert.Contains(nameof(ThrowStartupFailure), diagnostic);
    }

    private static void ThrowStartupFailure() =>
        throw new InvalidOperationException(
            "outer startup failure",
            new ArgumentException("inner XAML detail"));

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "ZoomRecorder.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
