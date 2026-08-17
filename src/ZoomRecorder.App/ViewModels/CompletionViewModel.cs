using ZoomRecorder.Core.Ports;

namespace ZoomRecorder.App.ViewModels;

public sealed class CompletionViewModel
{
    public CompletionViewModel(RecordingResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Path = result.Path;
        FileName = System.IO.Path.GetFileName(result.Path);
        DurationText = result.Duration.ToString(result.Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");
        FileSizeText = $"{result.ByteSize / 1_048_576d:0.0} MB";
    }

    public string Path { get; }
    public string FileName { get; }
    public string DurationText { get; }
    public string FileSizeText { get; }
}
