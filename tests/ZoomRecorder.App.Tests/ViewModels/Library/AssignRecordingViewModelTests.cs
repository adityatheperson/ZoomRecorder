using ZoomRecorder.App.ViewModels.Library;
using ZoomRecorder.Core.Library;

namespace ZoomRecorder.App.Tests.ViewModels.Library;

public sealed class AssignRecordingViewModelTests
{
    private static readonly Guid RecordingId = Guid.Parse("5f0ce3bd-19b6-4bf4-926f-acde02ab12bf");
    private static readonly Guid ExistingClassId = Guid.Parse("f49c9a86-109b-4ba8-a18a-4b365d644afb");

    [Fact]
    public async Task Load_classes_returns_only_active_classes()
    {
        var repository = new TestLibraryRepository();
        repository.Classes.Add(repository.Class(ExistingClassId, "Biology 101", archived: false));
        var viewModel = CreateViewModel(repository);

        var classes = await viewModel.LoadClassesAsync(CancellationToken.None);

        Assert.Single(classes);
        Assert.Equal("Biology 101", classes[0].Name);
        Assert.False(repository.LastIncludeArchived);
    }

    [Fact]
    public async Task Existing_class_assignment_updates_the_recording()
    {
        var repository = new TestLibraryRepository();
        var viewModel = CreateViewModel(repository);

        await viewModel.AssignExistingAsync(ExistingClassId, rememberMeeting: false, CancellationToken.None);

        Assert.Equal(ExistingClassId, repository.AssignedClassId);
    }

    [Fact]
    public async Task Create_and_assign_trims_name_and_optional_term()
    {
        var repository = new TestLibraryRepository();
        var viewModel = CreateViewModel(repository);

        var created = await viewModel.CreateAndAssignAsync(
            "  Biology 101  ", "  Fall 2026  ", rememberMeeting: false, CancellationToken.None);

        Assert.Equal("Biology 101", created.Name);
        Assert.Equal("Fall 2026", created.Term);
        Assert.Equal(created.Id, repository.AssignedClassId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_and_assign_rejects_blank_class_name(string name)
    {
        var repository = new TestLibraryRepository();
        var viewModel = CreateViewModel(repository);

        await Assert.ThrowsAsync<ArgumentException>(() => viewModel.CreateAndAssignAsync(
            name, "Fall 2026", rememberMeeting: false, CancellationToken.None));

        Assert.Empty(repository.Classes);
        Assert.Null(repository.AssignedClassId);
    }

    [Fact]
    public async Task Remembering_a_meeting_upserts_its_class_mapping()
    {
        var repository = new TestLibraryRepository();
        var viewModel = CreateViewModel(repository, meetingId: " 1234567890 ");

        await viewModel.AssignExistingAsync(ExistingClassId, rememberMeeting: true, CancellationToken.None);

        Assert.True(viewModel.CanRememberMeeting);
        Assert.Equal(new MeetingClassMapping("1234567890", ExistingClassId), repository.Mapping);
    }

    [Fact]
    public async Task Declining_remember_does_not_modify_an_existing_mapping()
    {
        var repository = new TestLibraryRepository
        {
            Mapping = new MeetingClassMapping("1234567890", Guid.NewGuid())
        };
        var original = repository.Mapping;
        var viewModel = CreateViewModel(repository, meetingId: "1234567890");

        await viewModel.AssignExistingAsync(ExistingClassId, rememberMeeting: false, CancellationToken.None);

        Assert.Equal(original, repository.Mapping);
        Assert.Equal(0, repository.UpsertMappingCallCount);
    }

    [Fact]
    public async Task Missing_meeting_id_hides_remember_and_cannot_write_a_mapping()
    {
        var repository = new TestLibraryRepository();
        var viewModel = CreateViewModel(repository, meetingId: "   ");

        await viewModel.AssignExistingAsync(ExistingClassId, rememberMeeting: true, CancellationToken.None);

        Assert.False(viewModel.CanRememberMeeting);
        Assert.Null(repository.Mapping);
        Assert.Equal(0, repository.UpsertMappingCallCount);
    }

    [Fact]
    public async Task Explicit_reassignment_replaces_the_previous_class()
    {
        var repository = new TestLibraryRepository { AssignedClassId = Guid.NewGuid() };
        var viewModel = CreateViewModel(repository);

        await viewModel.AssignExistingAsync(ExistingClassId, rememberMeeting: false, CancellationToken.None);

        Assert.Equal(ExistingClassId, repository.AssignedClassId);
    }

    [Fact]
    public async Task Precancelled_assignment_propagates_without_changes()
    {
        var repository = new TestLibraryRepository
        {
            AssignedClassId = Guid.NewGuid(),
            Mapping = new MeetingClassMapping("1234567890", Guid.NewGuid())
        };
        var originalClassId = repository.AssignedClassId;
        var originalMapping = repository.Mapping;
        var viewModel = CreateViewModel(repository, meetingId: "1234567890");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => viewModel.AssignExistingAsync(
            ExistingClassId, rememberMeeting: true, cancellation.Token));

        Assert.Equal(originalClassId, repository.AssignedClassId);
        Assert.Equal(originalMapping, repository.Mapping);
    }

    private static AssignRecordingViewModel CreateViewModel(
        TestLibraryRepository repository,
        string? meetingId = "1234567890") =>
        new(repository, new RecordingRecord(
            RecordingId,
            null,
            "C:\\Videos\\meeting.mp4",
            "meeting.mp4",
            meetingId,
            new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero),
            TimeSpan.FromMinutes(42),
            52_428_800,
            true));

    private sealed class TestLibraryRepository : ILibraryRepository
    {
        public List<ClassRecord> Classes { get; } = [];
        public Guid? AssignedClassId { get; set; }
        public MeetingClassMapping? Mapping { get; set; }
        public bool LastIncludeArchived { get; private set; } = true;
        public int UpsertMappingCallCount { get; private set; }

        public ClassRecord Class(Guid id, string name, bool archived) =>
            new(id, name, null, new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero), archived);

        public Task<ClassRecord> CreateClassAsync(string name, string? term, CancellationToken cancellationToken)
            => throw new NotSupportedException("Assignments must use the atomic repository operation.");

        public Task<RecordingRecord> AddRecordingAsync(RecordingRecord recording, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RecordingRecord?> FindRecordingByPathAsync(string canonicalPath, CancellationToken cancellationToken) =>
            Task.FromResult<RecordingRecord?>(null);

        public Task<IReadOnlyList<ClassRecord>> ListClassesAsync(bool includeArchived, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastIncludeArchived = includeArchived;
            return Task.FromResult<IReadOnlyList<ClassRecord>>(
                Classes.Where(item => includeArchived || !item.IsArchived).ToArray());
        }

        public Task<IReadOnlyList<RecordingRecord>> ListRecordingsAsync(Guid? classId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecordingRecord>>([]);

        public Task<IReadOnlyList<RecordingRecord>> ListUnassignedRecordingsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecordingRecord>>([]);

        public Task<IReadOnlyList<RecordingRecord>> SearchClassRecordingsAsync(Guid classId, string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecordingRecord>>([]);

        public Task AssignRecordingAsync(Guid recordingId, Guid? classId, CancellationToken cancellationToken)
            => throw new NotSupportedException("Assignments must use the atomic repository operation.");

        public Task AssignRecordingToClassAsync(
            Guid recordingId,
            Guid classId,
            string? meetingIdToRemember,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(RecordingId, recordingId);
            AssignedClassId = classId;
            if (meetingIdToRemember is not null)
            {
                UpsertMappingCallCount++;
                Mapping = new MeetingClassMapping(meetingIdToRemember, classId);
            }

            return Task.CompletedTask;
        }

        public Task<ClassRecord> CreateClassAndAssignRecordingAsync(
            string name,
            string? term,
            Guid recordingId,
            string? meetingIdToRemember,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(RecordingId, recordingId);
            var item = new ClassRecord(
                Guid.NewGuid(), name, term,
                new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero), false);
            Classes.Add(item);
            AssignedClassId = item.Id;
            if (meetingIdToRemember is not null)
            {
                UpsertMappingCallCount++;
                Mapping = new MeetingClassMapping(meetingIdToRemember, item.Id);
            }

            return Task.FromResult(item);
        }

        public Task<MeetingClassMapping?> FindMappingAsync(string meetingId, CancellationToken cancellationToken) =>
            Task.FromResult(Mapping);

        public Task UpsertMappingAsync(MeetingClassMapping mapping, CancellationToken cancellationToken)
            => throw new NotSupportedException("Assignments must use the atomic repository operation.");

        public Task ForgetMappingAsync(string meetingId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
