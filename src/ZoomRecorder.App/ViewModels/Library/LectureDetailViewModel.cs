using System.Collections.ObjectModel;
using ZoomRecorder.Core.Library;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.ViewModels.Library;

public sealed class LectureDetailViewModel : LibraryViewModelBase
{
    private readonly Func<string, CancellationToken, Task> saveTranscript;
    private readonly Func<CancellationToken, Task> refresh;
    private readonly ICloudNoticePresenter notice;
    private string transcriptText = string.Empty;

    public LectureDetailViewModel(
        RecordingRecord recording,
        Func<string, CancellationToken, Task> saveTranscript,
        Func<CancellationToken, Task> refresh,
        ICloudNoticePresenter notice)
    {
        Recording = recording ?? throw new ArgumentNullException(nameof(recording));
        this.saveTranscript = saveTranscript ?? throw new ArgumentNullException(nameof(saveTranscript));
        this.refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        this.notice = notice ?? throw new ArgumentNullException(nameof(notice));
    }

    public RecordingRecord Recording { get; }
    public string Title => Recording.FileName;
    public string Summary { get; set; } = string.Empty;
    public ObservableCollection<NoteSection> Notes { get; } = [];
    public ObservableCollection<KeyTerm> KeyTerms { get; } = [];
    public ObservableCollection<StoredStudyAssignment> Assignments { get; } = [];
    public ObservableCollection<ReviewQuestion> ReviewQuestions { get; } = [];
    public string TranscriptText
    {
        get => transcriptText;
        set { if (transcriptText != value) { transcriptText = value; RaisePropertyChanged(); } }
    }
    public bool StudyMaterialsAreStale { get; private set; }
    public bool CanRefreshStudyMaterials => StudyMaterialsAreStale;
    public bool CanSeekVideo => Recording.VideoAvailable;
    public string? SeekUnavailableText => CanSeekVideo ? null : "The local video was deleted after processing.";
    public string? RecoveryActionText { get; private set; }
    public bool CanOpenSettings => RecoveryActionText == "Check API key";

    public async Task SaveTranscriptAsync(CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(TranscriptText);
        await saveTranscript(TranscriptText, cancellationToken);
        StudyMaterialsAreStale = true;
        RaisePropertyChanged(nameof(StudyMaterialsAreStale));
        RaisePropertyChanged(nameof(CanRefreshStudyMaterials));
    }

    public async Task RefreshStudyMaterialsAsync(CancellationToken cancellationToken)
    {
        if (!StudyMaterialsAreStale) return;
        if (!await notice.ConfirmAsync(
            "Refreshing study materials sends the edited transcript to the cloud. Audio is not transcribed again.",
            cancellationToken)) return;
        await refresh(cancellationToken);
        StudyMaterialsAreStale = false;
        RaisePropertyChanged(nameof(StudyMaterialsAreStale));
        RaisePropertyChanged(nameof(CanRefreshStudyMaterials));
    }

    public void ApplyFailure(CloudProcessingErrorCode errorCode)
    {
        RecoveryActionText = errorCode == CloudProcessingErrorCode.CredentialUnavailable
            ? "Check API key"
            : "Try again";
        RaisePropertyChanged(nameof(RecoveryActionText));
        RaisePropertyChanged(nameof(CanOpenSettings));
    }
}
