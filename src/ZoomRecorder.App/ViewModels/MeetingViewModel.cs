using System.ComponentModel;
using System.Runtime.CompilerServices;
using ZoomRecorder.Core.Lifecycle;
using ZoomRecorder.Core.Orchestration;

namespace ZoomRecorder.App.ViewModels;

public sealed class MeetingViewModel : INotifyPropertyChanged, IObserver<MeetingStatus>, IDisposable
{
    private readonly IDisposable _subscription;
    private bool _hasRecordingError;
    private bool _isRecordingHealthy;
    private string _statusText = "Preparing recording…";

    public MeetingViewModel(IObservable<MeetingStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        _subscription = statuses.Subscribe(this);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool HasRecordingError
    {
        get => _hasRecordingError;
        private set => Set(ref _hasRecordingError, value);
    }

    public bool IsRecordingHealthy
    {
        get => _isRecordingHealthy;
        private set => Set(ref _isRecordingHealthy, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public void OnNext(MeetingStatus value)
    {
        HasRecordingError = value.State == AppState.RecoverableError;
        IsRecordingHealthy = value.State == AppState.InMeetingRecording && !HasRecordingError;
        StatusText = value.ErrorMessage ?? (IsRecordingHealthy ? "Recording" : "Preparing recording…");
    }

    public void OnError(Exception error)
    {
        HasRecordingError = true;
        IsRecordingHealthy = false;
        StatusText = error.Message;
    }

    public void OnCompleted()
    {
    }

    public void Dispose() => _subscription.Dispose();

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
