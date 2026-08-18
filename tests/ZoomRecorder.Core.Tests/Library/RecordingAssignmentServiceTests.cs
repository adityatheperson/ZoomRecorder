using ZoomRecorder.Core.Library;
using ZoomRecorder.Core.Ports;

namespace ZoomRecorder.Core.Tests.Library;

public sealed class RecordingAssignmentServiceTests
{
    private static readonly DateTimeOffset RecordedAt = new(2026, 8, 18, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public async Task Explicit_class_wins_over_mapping_and_updates_mapping_when_remembered()
    {
        var mappedClassId = Guid.NewGuid();
        var explicitClassId = Guid.NewGuid();
        var repository = new TestLibraryRepository
        {
            Mapping = new MeetingClassMapping("meeting-42", mappedClassId)
        };
        var service = CreateService(repository);

        var recording = await service.RegisterFinalizedAsync(
            Result(), " meeting-42 ", explicitClassId, rememberExplicitChoice: true, CancellationToken.None);

        Assert.Equal(explicitClassId, recording.ClassId);
        Assert.Equal(new MeetingClassMapping("meeting-42", explicitClassId), repository.Mapping);
    }

    [Fact]
    public async Task Remembered_mapping_assigns_when_no_explicit_class_is_supplied()
    {
        var classId = Guid.NewGuid();
        var repository = new TestLibraryRepository
        {
            Mapping = new MeetingClassMapping("meeting-42", classId)
        };
        var service = CreateService(repository);

        var recording = await service.RegisterFinalizedAsync(
            Result(), "meeting-42", explicitClassId: null, rememberExplicitChoice: false, CancellationToken.None);

        Assert.Equal(classId, recording.ClassId);
    }

    [Fact]
    public async Task Missing_mapping_leaves_recording_unassigned()
    {
        var repository = new TestLibraryRepository();
        var service = CreateService(repository);

        var recording = await service.RegisterFinalizedAsync(
            Result(), "meeting-42", explicitClassId: null, rememberExplicitChoice: false, CancellationToken.None);

        Assert.Null(recording.ClassId);
    }

    [Fact]
    public async Task Blank_meeting_id_is_absent_and_is_never_queried_or_saved()
    {
        var repository = new TestLibraryRepository();
        var service = CreateService(repository);

        var recording = await service.RegisterFinalizedAsync(
            Result(), "  ", explicitClassId: null, rememberExplicitChoice: true, CancellationToken.None);

        Assert.Null(recording.MeetingId);
        Assert.Equal(0, repository.FindMappingCallCount);
        Assert.Equal(0, repository.UpsertMappingCallCount);
    }

    [Fact]
    public async Task Explicit_choice_is_not_remembered_when_disabled()
    {
        var mappedClassId = Guid.NewGuid();
        var explicitClassId = Guid.NewGuid();
        var repository = new TestLibraryRepository
        {
            Mapping = new MeetingClassMapping("meeting-42", mappedClassId)
        };
        var service = CreateService(repository);

        var recording = await service.RegisterFinalizedAsync(
            Result(), "meeting-42", explicitClassId, rememberExplicitChoice: false, CancellationToken.None);

        Assert.Equal(explicitClassId, recording.ClassId);
        Assert.Equal(new MeetingClassMapping("meeting-42", mappedClassId), repository.Mapping);
        Assert.Equal(0, repository.UpsertMappingCallCount);
    }

    [Fact]
    public async Task Cancellation_is_propagated()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var repository = new TestLibraryRepository();
        var service = CreateService(repository);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.RegisterFinalizedAsync(
            Result(), "meeting-42", explicitClassId: null, rememberExplicitChoice: false, cancellation.Token));
    }

    [Fact]
    public async Task Recording_metadata_is_converted_from_result_and_clock()
    {
        var repository = new TestLibraryRepository();
        var service = CreateService(repository);
        var result = new RecordingResult(Path.Combine("videos", "lesson.mp4"), TimeSpan.FromMinutes(42), 52_428_800);

        var recording = await service.RegisterFinalizedAsync(
            result, " meeting-42 ", explicitClassId: null, rememberExplicitChoice: false, CancellationToken.None);

        Assert.NotEqual(Guid.Empty, recording.Id);
        Assert.Null(recording.ClassId);
        Assert.Equal(Path.GetFullPath(result.Path), recording.FilePath);
        Assert.Equal(Path.GetFileName(result.Path), recording.FileName);
        Assert.Equal("meeting-42", recording.MeetingId);
        Assert.Equal(RecordedAt, recording.RecordedAt);
        Assert.Equal(result.Duration, recording.Duration);
        Assert.Equal(result.ByteSize, recording.ByteSize);
        Assert.True(recording.VideoAvailable);
        Assert.Same(recording, repository.AddedRecording);
    }

    private static RecordingAssignmentService CreateService(TestLibraryRepository repository) =>
        new(repository, () => RecordedAt);

    private static RecordingResult Result() =>
        new(Path.Combine("videos", "lesson.mp4"), TimeSpan.FromMinutes(42), 52_428_800);

    private sealed class TestLibraryRepository : ILibraryRepository
    {
        public MeetingClassMapping? Mapping { get; set; }
        public RecordingRecord? AddedRecording { get; private set; }
        public int FindMappingCallCount { get; private set; }
        public int UpsertMappingCallCount { get; private set; }

        public Task<ClassRecord> CreateClassAsync(string name, string? term, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RecordingRecord> AddRecordingAsync(RecordingRecord recording, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddedRecording = recording;
            return Task.FromResult(recording);
        }

        public Task<RecordingRecord?> FindRecordingByPathAsync(string canonicalPath, CancellationToken cancellationToken) =>
            Task.FromResult<RecordingRecord?>(null);

        public Task<IReadOnlyList<ClassRecord>> ListClassesAsync(bool includeArchived, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ClassRecord>>([]);

        public Task<IReadOnlyList<RecordingRecord>> ListRecordingsAsync(Guid? classId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecordingRecord>>([]);

        public Task<IReadOnlyList<RecordingRecord>> ListUnassignedRecordingsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecordingRecord>>([]);

        public Task<IReadOnlyList<RecordingRecord>> SearchClassRecordingsAsync(Guid classId, string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecordingRecord>>([]);

        public Task AssignRecordingAsync(Guid recordingId, Guid? classId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<MeetingClassMapping?> FindMappingAsync(string meetingId, CancellationToken cancellationToken)
        {
            FindMappingCallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Mapping);
        }

        public Task UpsertMappingAsync(MeetingClassMapping mapping, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpsertMappingCallCount++;
            Mapping = mapping;
            return Task.CompletedTask;
        }

        public Task ForgetMappingAsync(string meetingId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
