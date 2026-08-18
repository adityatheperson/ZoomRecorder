using System.ComponentModel;
using ZoomRecorder.Core.Ports;

namespace ZoomRecorder.App.ViewModels;

public sealed class CompletionViewModel : INotifyPropertyChanged
{
    public CompletionViewModel(RecordingResult result)
        : this(result, recordingId: null)
    {
    }

    public CompletionViewModel(
        RecordingResult result,
        Guid? recordingId,
        string? assignmentStatus = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        Path = result.Path;
        FileName = System.IO.Path.GetFileName(result.Path);
        DurationText = result.Duration.ToString(result.Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");
        FileSizeText = $"{result.ByteSize / 1_048_576d:0.0} MB";
        RecordingId = recordingId;
        _assignmentStatus = assignmentStatus;
    }

    private string? _assignmentStatus;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }
    public string FileName { get; }
    public string DurationText { get; }
    public string FileSizeText { get; }
    public Guid? RecordingId { get; }
    public bool CanAssign => RecordingId.HasValue;
    public string? AssignmentStatus
    {
        get => _assignmentStatus;
        private set
        {
            if (_assignmentStatus == value)
            {
                return;
            }

            _assignmentStatus = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AssignmentStatus)));
        }
    }

    public void MarkAssigned(string className)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        AssignmentStatus = $"Assigned to {className}.";
    }

    public void MarkAssignmentUnavailable() =>
        AssignmentStatus = "The class library is unavailable right now. Try again later.";
}
