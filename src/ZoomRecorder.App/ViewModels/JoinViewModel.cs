using System.ComponentModel;
using System.Runtime.CompilerServices;
using ZoomRecorder.Core.Meetings;

namespace ZoomRecorder.App.ViewModels;

public interface IJoinFlow
{
    Task JoinAndRecordAsync(MeetingJoinRequest request, CancellationToken cancellationToken);
}

public interface IAppNavigator
{
    void ShowMeeting();
}

public sealed class JoinViewModel(IJoinFlow joinFlow, IAppNavigator navigator) : INotifyPropertyChanged
{
    private string _meetingInput = string.Empty;
    private string? _passcode;
    private string? _errorMessage;
    private string? _joinStatusText;
    private bool _isJoining;
    private CancellationTokenSource? _joinCancellation;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string MeetingInput
    {
        get => _meetingInput;
        set => Set(ref _meetingInput, value);
    }

    public string? Passcode
    {
        get => _passcode;
        set => Set(ref _passcode, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => Set(ref _errorMessage, value);
    }

    public bool IsJoining
    {
        get => _isJoining;
        private set
        {
            if (Set(ref _isJoining, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCancel)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanJoin)));
            }
        }
    }

    public bool CanCancel => IsJoining;
    public bool CanJoin => !IsJoining;

    public string? JoinStatusText
    {
        get => _joinStatusText;
        private set => Set(ref _joinStatusText, value);
    }

    public async Task JoinAndRecordAsync(CancellationToken cancellationToken = default)
    {
        if (IsJoining)
        {
            return;
        }

        IsJoining = true;
        ErrorMessage = null;
        JoinStatusText = "Waiting for Zoom meeting…";
        using var attemptCancellation = new CancellationTokenSource();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            attemptCancellation.Token);
        _joinCancellation = attemptCancellation;

        try
        {
            var request = MeetingInputParser.Parse(MeetingInput, Passcode);
            await joinFlow.JoinAndRecordAsync(request, linkedCancellation.Token).ConfigureAwait(true);
            navigator.ShowMeeting();
        }
        catch (OperationCanceledException) when (attemptCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is MeetingInputException or InvalidOperationException)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            _joinCancellation = null;
            JoinStatusText = null;
            IsJoining = false;
        }
    }

    public void CancelJoin() => _joinCancellation?.Cancel();

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
