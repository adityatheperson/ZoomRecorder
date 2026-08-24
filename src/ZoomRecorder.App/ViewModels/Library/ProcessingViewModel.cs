using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.ViewModels.Library;

public interface ICloudNoticePresenter
{
    Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken);
}

public sealed class ProcessingViewModel : LibraryViewModelBase
{
    private readonly ICloudNoticePresenter notice;
    private readonly Func<bool, CancellationToken, Task> start;
    private readonly Func<CancellationToken, Task> cancel;
    private readonly Func<CancellationToken, Task>? permanentDelete;
    private bool deleteVideoAfterSuccess;
    private bool isProcessing;
    private bool hasError;
    private string statusText = "Ready to process";

    public ProcessingViewModel(
        string className,
        long? estimatedUploadBytes,
        bool savedDeleteDefault,
        ICloudNoticePresenter notice,
        Func<bool, CancellationToken, Task> start,
        Func<CancellationToken, Task> cancel,
        decimal? estimatedCost = null,
        Func<CancellationToken, Task>? permanentDelete = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        ClassName = className;
        EstimatedUploadText = estimatedUploadBytes is { } bytes ? FormatBytes(bytes) : null;
        EstimatedCostText = estimatedCost is { } cost ? cost.ToString("C4") : null;
        deleteVideoAfterSuccess = savedDeleteDefault;
        this.notice = notice ?? throw new ArgumentNullException(nameof(notice));
        this.start = start ?? throw new ArgumentNullException(nameof(start));
        this.cancel = cancel ?? throw new ArgumentNullException(nameof(cancel));
        this.permanentDelete = permanentDelete;
    }

    public string ClassName { get; }
    public string? EstimatedUploadText { get; }
    public string? EstimatedCostText { get; }
    public bool DeleteVideoAfterSuccess
    {
        get => deleteVideoAfterSuccess;
        set { if (deleteVideoAfterSuccess != value) { deleteVideoAfterSuccess = value; RaisePropertyChanged(); } }
    }
    public bool IsProcessing => isProcessing;
    public bool HasError => hasError;
    public string StatusText => statusText;
    public bool PermanentDeleteDecisionPending { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        hasError = false;
        RaisePropertyChanged(nameof(HasError));
        isProcessing = true;
        RaisePropertyChanged(nameof(IsProcessing));
        try
        {
            await start(DeleteVideoAfterSuccess, cancellationToken);
        }
        catch (ProcessingOperationException error)
        {
            statusText = error.Message;
            isProcessing = false;
            hasError = true;
            RaisePropertyChanged(nameof(StatusText));
            RaisePropertyChanged(nameof(IsProcessing));
            RaisePropertyChanged(nameof(HasError));
        }
    }

    public Task CancelAsync(CancellationToken cancellationToken) => cancel(cancellationToken);

    public void ApplyRecycleUnavailable()
    {
        PermanentDeleteDecisionPending = true;
        RaisePropertyChanged(nameof(PermanentDeleteDecisionPending));
    }

    public async Task ConfirmPermanentDeleteAsync(CancellationToken cancellationToken)
    {
        if (!PermanentDeleteDecisionPending || permanentDelete is null) return;
        if (!await notice.ConfirmAsync(
            "The Recycle Bin is unavailable. Permanently delete the MP4? This cannot be undone.",
            cancellationToken)) return;
        await permanentDelete(cancellationToken);
        PermanentDeleteDecisionPending = false;
        RaisePropertyChanged(nameof(PermanentDeleteDecisionPending));
    }

    public void Apply(ProcessingProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        statusText = progress.State switch
        {
            ProcessingState.PreparingAudio => "Preparing audio",
            ProcessingState.Transcribing => "Transcribing",
            ProcessingState.GeneratingStudyPackage => "Creating study materials",
            ProcessingState.UpdatingClassGuide => "Updating class guide",
            ProcessingState.Completed => "Completed",
            ProcessingState.NeedsAttention => "Needs attention",
            ProcessingState.Cancelled => "Cancelled",
            _ => "Ready to process"
        };
        isProcessing = progress.State is ProcessingState.PreparingAudio or ProcessingState.Transcribing or
            ProcessingState.GeneratingStudyPackage or ProcessingState.UpdatingClassGuide;
        RaisePropertyChanged(nameof(StatusText));
        RaisePropertyChanged(nameof(IsProcessing));
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.0} KB";
        return $"{bytes / (1024d * 1024d):0.0} MB";
    }
}
