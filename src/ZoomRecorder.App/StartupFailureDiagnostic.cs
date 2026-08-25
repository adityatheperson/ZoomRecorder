using System.Globalization;
using System.Text;

namespace ZoomRecorder.App;

internal static class StartupFailureDiagnostic
{
    internal static string Write(Exception exception, string directory)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var diagnosticDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(diagnosticDirectory);
        var path = Path.Combine(diagnosticDirectory, "startup-error.log");
        var content = new StringBuilder()
            .AppendLine($"UTC: {DateTimeOffset.UtcNow:O}")
            .AppendLine($"HRESULT: 0x{exception.HResult.ToString("X8", CultureInfo.InvariantCulture)}")
            .Append(exception)
            .ToString();
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }
}
