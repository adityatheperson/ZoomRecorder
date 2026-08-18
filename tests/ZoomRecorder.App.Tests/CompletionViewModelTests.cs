using ZoomRecorder.App.ViewModels;
using ZoomRecorder.App.Services;
using ZoomRecorder.Core.Library;
using ZoomRecorder.Core.Ports;

namespace ZoomRecorder.App.Tests;

public sealed class CompletionViewModelTests
{
    [Fact]
    public void Completion_exposes_finalized_recording_metadata()
    {
        var result = new RecordingResult("C:\\Videos\\meeting.mp4", TimeSpan.FromMinutes(42), 52_428_800);

        var viewModel = new CompletionViewModel(result);

        Assert.Equal("meeting.mp4", viewModel.FileName);
        Assert.Equal("42:00", viewModel.DurationText);
        Assert.Equal("50.0 MB", viewModel.FileSizeText);
        Assert.Equal(result.Path, viewModel.Path);
    }

    [Fact]
    public void Registered_completion_preserves_metadata_and_enables_assignment()
    {
        var result = new RecordingResult("C:\\Videos\\meeting.mp4", TimeSpan.FromMinutes(42), 52_428_800);
        var recordingId = Guid.NewGuid();

        var viewModel = new CompletionViewModel(result, recordingId);

        Assert.Equal("meeting.mp4", viewModel.FileName);
        Assert.Equal("42:00", viewModel.DurationText);
        Assert.Equal("50.0 MB", viewModel.FileSizeText);
        Assert.Equal(result.Path, viewModel.Path);
        Assert.Equal(recordingId, viewModel.RecordingId);
        Assert.True(viewModel.CanAssign);
    }

    [Fact]
    public void Unavailable_library_keeps_completion_metadata_and_disables_assignment()
    {
        var result = new RecordingResult("C:\\Videos\\meeting.mp4", TimeSpan.FromMinutes(42), 52_428_800);

        var viewModel = new CompletionViewModel(
            result,
            recordingId: null,
            "Your recording was saved, but the class library is unavailable right now.");

        Assert.Equal(result.Path, viewModel.Path);
        Assert.Null(viewModel.RecordingId);
        Assert.False(viewModel.CanAssign);
        Assert.Equal(
            "Your recording was saved, but the class library is unavailable right now.",
            viewModel.AssignmentStatus);
    }

    [Fact]
    public void Successful_assignment_updates_completion_status()
    {
        var viewModel = new CompletionViewModel(
            new RecordingResult("C:\\Videos\\meeting.mp4", TimeSpan.FromMinutes(42), 52_428_800),
            Guid.NewGuid());

        viewModel.MarkAssigned("Biology 101");

        Assert.Equal("Assigned to Biology 101.", viewModel.AssignmentStatus);
    }

    [Fact]
    public void Assignment_failure_updates_plain_language_status()
    {
        var viewModel = new CompletionViewModel(
            new RecordingResult("C:\\Videos\\meeting.mp4", TimeSpan.FromMinutes(42), 52_428_800),
            Guid.NewGuid());

        viewModel.MarkAssignmentUnavailable();

        Assert.Equal("The class library is unavailable right now. Try again later.", viewModel.AssignmentStatus);
    }

    [Fact]
    public async Task Duplicate_completion_registration_returns_the_existing_record()
    {
        var repository = new RegistrationRepository();
        var service = new RecordingLibraryService(
            repository,
            () => new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        var result = new RecordingResult("C:\\Videos\\meeting.mp4", TimeSpan.FromMinutes(42), 52_428_800);

        var first = await service.RegisterFinalizedAsync(result, " 1234567890 ", CancellationToken.None);
        var duplicate = await service.RegisterFinalizedAsync(result, "1234567890", CancellationToken.None);

        Assert.Same(first, duplicate);
        Assert.Single(repository.Recordings);
    }

    private sealed class RegistrationRepository : ILibraryRepository
    {
        public List<RecordingRecord> Recordings { get; } = [];

        public Task<ClassRecord> CreateClassAsync(string name, string? term, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RecordingRecord> AddRecordingAsync(RecordingRecord recording, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Recordings.Add(recording);
            return Task.FromResult(recording);
        }

        public Task<RecordingRecord?> FindRecordingByPathAsync(string canonicalPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = Recordings.SingleOrDefault(recording =>
                string.Equals(recording.FilePath, Path.GetFullPath(canonicalPath), StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(found);
        }

        public Task<IReadOnlyList<ClassRecord>> ListClassesAsync(bool includeArchived, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ClassRecord>>([]);

        public Task<IReadOnlyList<RecordingRecord>> ListRecordingsAsync(Guid? classId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecordingRecord>>(Recordings);

        public Task<IReadOnlyList<RecordingRecord>> ListUnassignedRecordingsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecordingRecord>>(Recordings);

        public Task<IReadOnlyList<RecordingRecord>> SearchClassRecordingsAsync(Guid classId, string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecordingRecord>>([]);

        public Task AssignRecordingAsync(Guid recordingId, Guid? classId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MeetingClassMapping?> FindMappingAsync(string meetingId, CancellationToken cancellationToken) =>
            Task.FromResult<MeetingClassMapping?>(null);

        public Task UpsertMappingAsync(MeetingClassMapping mapping, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ForgetMappingAsync(string meetingId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
