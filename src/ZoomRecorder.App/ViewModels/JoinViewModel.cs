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
    private string _displayName = string.Empty;
    private string? _errorMessage;
    private bool _isJoining;

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

    public string DisplayName
    {
        get => _displayName;
        set => Set(ref _displayName, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => Set(ref _errorMessage, value);
    }

    public bool IsJoining
    {
        get => _isJoining;
        private set => Set(ref _isJoining, value);
    }

    public async Task JoinAndRecordAsync(CancellationToken cancellationToken = default)
    {
        if (IsJoining)
        {
            return;
        }

        IsJoining = true;
        ErrorMessage = null;

        try
        {
            var request = MeetingInputParser.Parse(MeetingInput, Passcode);
            await joinFlow.JoinAndRecordAsync(request, cancellationToken).ConfigureAwait(true);
            navigator.ShowMeeting();
        }
        catch (Exception exception) when (exception is MeetingInputException or InvalidOperationException)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsJoining = false;
        }
    }

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
