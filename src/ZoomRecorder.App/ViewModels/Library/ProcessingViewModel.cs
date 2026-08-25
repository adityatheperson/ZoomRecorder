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
    private readonly Func<CancellationToken, Task>? resume;
    private readonly object operationGate = new();
    private CancellationTokenSource? operationCancellation;
    private TaskCompletionSource? operationCompletion;
    private bool deleteVideoAfterSuccess;
    private bool isProcessing;
    private bool hasError;
    private bool canResume;
    private string statusText = "Ready to process";
    private bool isProgressIndeterminate;
    private double progressValue;
    private double progressMaximum = 1;

    public ProcessingViewModel(
        string className,
        long? estimatedUploadBytes,
        bool savedDeleteDefault,
        ICloudNoticePresenter notice,
        Func<bool, CancellationToken, Task> start,
        Func<CancellationToken, Task> cancel,
        decimal? estimatedCost = null,
        Func<CancellationToken, Task>? permanentDelete = null,
        Func<CancellationToken, Task>? resume = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        ClassName = className;
        EstimatedUploadText = null;
        EstimatedCostText = null;
        deleteVideoAfterSuccess = false;
        this.notice = notice ?? throw new ArgumentNullException(nameof(notice));
        this.start = start ?? throw new ArgumentNullException(nameof(start));
        this.cancel = cancel ?? throw new ArgumentNullException(nameof(cancel));
        this.permanentDelete = permanentDelete;
        this.resume = resume;
    }

    public string ClassName { get; }
    public string PrimaryActionText => "Transcribe locally";
    public bool ShowsCloudControls => false;
    public bool SupportsVideoDeletion => false;
    public string? EstimatedUploadText { get; }
    public string? EstimatedCostText { get; }
    public bool DeleteVideoAfterSuccess
    {
        get => deleteVideoAfterSuccess;
        set { if (deleteVideoAfterSuccess != value) { deleteVideoAfterSuccess = value; RaisePropertyChanged(); } }
    }
    public bool IsProcessing => isProcessing;
    public bool IsProgressIndeterminate => isProgressIndeterminate;
    public double ProgressValue => progressValue;
    public double ProgressMaximum => progressMaximum;
    public bool HasError => hasError;
    public string StatusText => statusText;
    public bool PermanentDeleteDecisionPending { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource scopedCancellation;
        TaskCompletionSource scopedCompletion;
        lock (operationGate)
        {
            if (operationCompletion is not null)
            {
                return;
            }

            scopedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            scopedCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            operationCancellation = scopedCancellation;
            operationCompletion = scopedCompletion;
        }

        try
        {
            await StartCoreAsync(scopedCancellation.Token);
        }
        finally
        {
            lock (operationGate)
            {
                if (ReferenceEquals(operationCompletion, scopedCompletion))
                {
                    operationCancellation = null;
                    operationCompletion = null;
                }
            }

            scopedCancellation.Dispose();
            scopedCompletion.TrySetResult();
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        hasError = false;
        statusText = "Starting processing";
        isProcessing = true;
        isProgressIndeterminate = true;
        RaisePropertyChanged(nameof(HasError));
        RaisePropertyChanged(nameof(StatusText));
        RaisePropertyChanged(nameof(IsProcessing));
        RaisePropertyChanged(nameof(IsProgressIndeterminate));
        try
        {
            if (canResume && resume is not null)
                await resume(cancellationToken);
            else
                await start(false, cancellationToken);
        }
        catch (ProcessingOperationException error)
        {
            canResume = resume is not null;
            ApplyFailure(error.Message);
        }
        catch (OperationCanceledException)
        {
            ApplyFailure("Processing was cancelled.");
        }
        catch (Exception)
        {
            ApplyFailure("Processing stopped unexpectedly. Try again.");
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? scopedCancellation;
        Task? completion;
        lock (operationGate)
        {
            scopedCancellation = operationCancellation;
            completion = operationCompletion?.Task;
        }

        if (scopedCancellation is null || completion is null)
        {
            return;
        }

        scopedCancellation.Cancel();
        await cancel(cancellationToken);
        await completion.WaitAsync(cancellationToken);
    }

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
        if (hasError && progress.State == ProcessingState.NeedsAttention)
        {
            isProcessing = false;
            RaisePropertyChanged(nameof(IsProcessing));
            return;
        }
        statusText = progress.TranscriptionActivity?.Kind switch
        {
            TranscriptionActivityKind.AcquiringModel => "Downloading English transcription model (~500 MB)",
            TranscriptionActivityKind.Transcribing => "Transcribing locally",
            TranscriptionActivityKind.UsingCpuFallback => "Using CPU fallback",
            _ => progress.State switch
            {
                ProcessingState.PreparingAudio => "Preparing audio",
                ProcessingState.Transcribing => "Transcribing locally",
                ProcessingState.GeneratingStudyPackage => "Transcribing locally",
                ProcessingState.UpdatingClassGuide => "Transcribing locally",
                ProcessingState.Completed => "Transcript ready",
                ProcessingState.NeedsAttention => "Needs attention",
                ProcessingState.Cancelled => "Cancelled",
                _ => "Ready to process"
            }
        };
        var completedBytes = progress.ActivityCompletedBytes ?? progress.TranscriptionActivity?.CompletedBytes;
        var totalBytes = progress.ActivityTotalBytes ?? progress.TranscriptionActivity?.TotalBytes;
        isProcessing = progress.State is ProcessingState.PreparingAudio or ProcessingState.Transcribing or
            ProcessingState.GeneratingStudyPackage or ProcessingState.UpdatingClassGuide;
        var hasByteProgress = progress.TranscriptionActivity?.Kind == TranscriptionActivityKind.AcquiringModel &&
            completedBytes is >= 0 && totalBytes is > 0;
        isProgressIndeterminate = isProcessing && !hasByteProgress;
        if (hasByteProgress)
        {
            progressValue = Math.Min(completedBytes!.Value, totalBytes!.Value);
            progressMaximum = totalBytes.Value;
        }
        RaisePropertyChanged(nameof(StatusText));
        RaisePropertyChanged(nameof(IsProcessing));
        RaisePropertyChanged(nameof(IsProgressIndeterminate));
        RaisePropertyChanged(nameof(ProgressValue));
        RaisePropertyChanged(nameof(ProgressMaximum));
    }

    private void ApplyFailure(string message)
    {
        statusText = message;
        isProcessing = false;
        isProgressIndeterminate = false;
        hasError = true;
        RaisePropertyChanged(nameof(StatusText));
        RaisePropertyChanged(nameof(IsProcessing));
        RaisePropertyChanged(nameof(IsProgressIndeterminate));
        RaisePropertyChanged(nameof(HasError));
    }

}
