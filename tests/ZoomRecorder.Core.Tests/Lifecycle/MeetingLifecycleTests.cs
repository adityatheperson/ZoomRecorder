using ZoomRecorder.Core.Lifecycle;
using ZoomRecorder.Core.Meetings;

namespace ZoomRecorder.Core.Tests.Lifecycle;

public sealed class MeetingLifecycleTests
{
    private static readonly MeetingJoinRequest Request = new("1234567890", null);

    [Fact]
    public void Happy_path_reaches_recording_complete()
    {
        var lifecycle = new MeetingLifecycle();

        Assert.Equal(AppState.PreparingMeeting, lifecycle.Apply(new JoinRequested(Request)));
        Assert.Equal(AppState.StartingRecording, lifecycle.Apply(new MeetingPrepared()));
        Assert.Equal(AppState.RecordingReady, lifecycle.Apply(new RecordingStarted()));
        Assert.Equal(AppState.InMeetingRecording, lifecycle.Apply(new MeetingEntered()));
        Assert.Equal(AppState.FinalizingRecording, lifecycle.Apply(new MeetingEnded()));
        Assert.Equal(AppState.RecordingComplete, lifecycle.Apply(new RecordingFinalized("meeting.mp4", TimeSpan.FromMinutes(2), 1_024)));
    }

    [Fact]
    public void Meeting_entry_before_recording_is_rejected()
    {
        var lifecycle = new MeetingLifecycle();
        lifecycle.Apply(new JoinRequested(Request));
        lifecycle.Apply(new MeetingPrepared());

        var exception = Assert.Throws<InvalidStateTransitionException>(() => lifecycle.Apply(new MeetingEntered()));

        Assert.Equal(AppState.StartingRecording, lifecycle.Current);
        Assert.Contains(nameof(MeetingEntered), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Required_component_failure_moves_active_meeting_to_error()
    {
        var lifecycle = CreateInMeetingLifecycle();

        var state = lifecycle.Apply(new RequiredComponentFailed("Microphone unavailable"));

        Assert.Equal(AppState.RecoverableError, state);
        Assert.Equal("Microphone unavailable", lifecycle.LastError);
    }

    [Fact]
    public void Duplicate_meeting_end_is_ignored_while_finalizing()
    {
        var lifecycle = CreateInMeetingLifecycle();
        lifecycle.Apply(new MeetingEnded());

        Assert.Equal(AppState.FinalizingRecording, lifecycle.Apply(new MeetingEnded()));
    }

    private static MeetingLifecycle CreateInMeetingLifecycle()
    {
        var lifecycle = new MeetingLifecycle();
        lifecycle.Apply(new JoinRequested(Request));
        lifecycle.Apply(new MeetingPrepared());
        lifecycle.Apply(new RecordingStarted());
        lifecycle.Apply(new MeetingEntered());
        return lifecycle;
    }
}
