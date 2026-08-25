using ZoomRecorder.App.ViewModels.Library;
using ZoomRecorder.Core.Library;

namespace ZoomRecorder.App.Tests.ViewModels.Library;

public sealed class LibraryViewModelTests
{
    [Fact]
    public async Task Recordings_project_library_visible_processing_status()
    {
        var repository = new TestLibraryRepository();
        var ready = Recording("ready.mp4", BiologyId, Now);
        var resumable = Recording("resume.mp4", ChemistryId, Now.AddMinutes(-1));
        repository.Recordings.AddRange([ready, resumable]);
        var viewModel = new RecordingsViewModel(
            repository,
            (recordingId, _) => Task.FromResult(recordingId == ready.Id
                ? "Transcript ready"
                : "Resume transcription"));

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Contains(viewModel.Recordings, item =>
            item.Id == ready.Id && item.ProcessingStatus == "Transcript ready");
        Assert.Contains(viewModel.Recordings, item =>
            item.Id == resumable.Id && item.ProcessingStatus == "Resume transcription");
    }
    private static readonly Guid BiologyId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid ChemistryId = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Classes_shows_active_classes_ordered_by_name_with_lecture_counts()
    {
        var repository = new TestLibraryRepository();
        repository.Classes.AddRange([
            Class(ChemistryId, "Chemistry", archived: false),
            Class(Guid.NewGuid(), "Archived", archived: true),
            Class(BiologyId, "Biology", archived: false)
        ]);
        repository.Recordings.AddRange([
            Recording("biology-1.mp4", BiologyId, Now.AddHours(-2)),
            Recording("chemistry.mp4", ChemistryId, Now.AddHours(-1)),
            Recording("biology-2.mp4", BiologyId, Now)
        ]);
        var viewModel = new ClassesViewModel(repository);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal(["Biology", "Chemistry"], viewModel.Classes.Select(item => item.Name));
        Assert.Equal([2, 1], viewModel.Classes.Select(item => item.LectureCount));
        Assert.All(viewModel.Classes, item => Assert.Equal("Study package pending", item.StudyPackageStatus));
        Assert.False(repository.LastIncludeArchived);
    }

    [Fact]
    public async Task Home_shows_unassigned_count_and_recent_recordings_newest_first()
    {
        var repository = new TestLibraryRepository();
        repository.Recordings.AddRange([
            Recording("old.mp4", BiologyId, Now.AddDays(-2)),
            Recording("new.mp4", null, Now),
            Recording("middle.mp4", null, Now.AddDays(-1))
        ]);
        var viewModel = new LibraryShellViewModel(repository);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal(2, viewModel.UnassignedCount);
        Assert.Equal(["new.mp4", "middle.mp4", "old.mp4"], viewModel.RecentRecordings.Select(item => item.FileName));
    }

    [Fact]
    public async Task Recordings_marks_assignment_and_searches_file_names()
    {
        var repository = new TestLibraryRepository();
        repository.Recordings.AddRange([
            Recording("mitosis-review.mp4", BiologyId, Now),
            Recording("cell-lab.mp4", null, Now.AddMinutes(-1)),
            Recording("economics.mp4", ChemistryId, Now.AddMinutes(-2))
        ]);
        var viewModel = new RecordingsViewModel(repository);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal(["Assigned", "Unassigned", "Assigned"], viewModel.Recordings.Select(item => item.AssignmentStatus));

        await viewModel.SearchAsync("CELL", CancellationToken.None);

        var result = Assert.Single(viewModel.Recordings);
        Assert.Equal("cell-lab.mp4", result.FileName);
        Assert.True(result.IsUnassigned);
    }

    [Fact]
    public async Task Assignment_exception_shows_sanitized_error_and_can_retry()
    {
        var repository = new TestLibraryRepository();
        repository.Recordings.Add(Recording("lecture.mp4", null, Now));
        var viewModel = new RecordingsViewModel(repository);
        await viewModel.LoadAsync(CancellationToken.None);
        var item = Assert.Single(viewModel.Recordings);

        var assigned = await viewModel.AssignAsync(
            item,
            (_, _) => Task.FromException<bool>(new InvalidOperationException("database path secret")),
            CancellationToken.None);

        Assert.False(assigned);
        Assert.Equal("Assignment is unavailable right now. Try again.", viewModel.AssignmentErrorMessage);
        Assert.DoesNotContain("secret", viewModel.AssignmentErrorMessage);
        Assert.True(viewModel.CanRetryAssignment);
        Assert.Equal("lecture.mp4", Assert.Single(viewModel.Recordings).FileName);

        var retryCalls = 0;
        var retried = await viewModel.RetryAssignmentAsync(
            (_, _) =>
            {
                retryCalls++;
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.True(retried);
        Assert.Equal(1, retryCalls);
        Assert.Null(viewModel.AssignmentErrorMessage);
        Assert.False(viewModel.CanRetryAssignment);
    }

    [Fact]
    public async Task Assignment_dialog_cancellation_leaves_recordings_unchanged_without_error()
    {
        var repository = new TestLibraryRepository();
        repository.Recordings.Add(Recording("lecture.mp4", null, Now));
        var viewModel = new RecordingsViewModel(repository);
        await viewModel.LoadAsync(CancellationToken.None);
        var item = Assert.Single(viewModel.Recordings);

        var assigned = await viewModel.AssignAsync(
            item,
            (_, _) => Task.FromResult(false),
            CancellationToken.None);

        Assert.False(assigned);
        Assert.Null(viewModel.AssignmentErrorMessage);
        Assert.False(viewModel.CanRetryAssignment);
        Assert.Same(item, Assert.Single(viewModel.Recordings));
    }

    [Fact]
    public async Task Class_detail_loads_only_its_class_lectures()
    {
        var repository = SeedTwoClasses();
        var viewModel = new ClassDetailViewModel(repository, BiologyId);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal("Biology", viewModel.ClassName);
        Assert.Equal("Fall 2026", viewModel.Term);
        Assert.Equal(2, viewModel.LectureCount);
        Assert.All(viewModel.Lectures, item => Assert.Equal(BiologyId, item.ClassId));
    }

    [Fact]
    public async Task Class_search_calls_scoped_query_and_cannot_expose_another_class()
    {
        var repository = SeedTwoClasses();
        repository.SearchResults = [
            Recording("mitosis.mp4", BiologyId, Now),
            Recording("other-class.mp4", ChemistryId, Now.AddMinutes(-1))
        ];
        var viewModel = new ClassDetailViewModel(repository, BiologyId);

        await viewModel.SearchAsync("mitosis", CancellationToken.None);

        Assert.Equal(BiologyId, repository.LastSearchClassId);
        Assert.Equal("mitosis", repository.LastSearchQuery);
        Assert.All(viewModel.Lectures, item => Assert.Equal(BiologyId, item.ClassId));
        Assert.DoesNotContain(viewModel.Lectures, item => item.FileName == "other-class.mp4");
    }

    [Fact]
    public async Task Blank_class_search_reloads_only_the_selected_class()
    {
        var repository = SeedTwoClasses();
        var viewModel = new ClassDetailViewModel(repository, BiologyId);

        await viewModel.SearchAsync("   ", CancellationToken.None);

        Assert.Equal(BiologyId, repository.LastListClassId);
        Assert.Equal(0, repository.SearchCallCount);
        Assert.Equal(2, viewModel.LectureCount);
        Assert.All(viewModel.Lectures, item => Assert.Equal(BiologyId, item.ClassId));
    }

    [Fact]
    public async Task Repository_errors_become_plain_error_and_clear_busy_state()
    {
        var repository = new TestLibraryRepository { ClassesFailure = new InvalidOperationException("database path secret") };
        var viewModel = new ClassesViewModel(repository);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal("The class library is unavailable right now. Try again.", viewModel.ErrorMessage);
        Assert.DoesNotContain("secret", viewModel.ErrorMessage);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task Cancellation_propagates_clears_busy_and_preserves_successful_data()
    {
        var repository = new TestLibraryRepository();
        repository.Recordings.Add(Recording("kept.mp4", null, Now));
        var viewModel = new RecordingsViewModel(repository);
        await viewModel.LoadAsync(CancellationToken.None);
        repository.BlockRecordingsUntilCancellation = true;
        using var cancellation = new CancellationTokenSource();

        var search = viewModel.SearchAsync("anything", cancellation.Token);
        await repository.RecordingsRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
        Assert.False(viewModel.IsBusy);
        Assert.Equal("kept.mp4", Assert.Single(viewModel.Recordings).FileName);
    }

    [Fact]
    public void Shell_routes_all_destinations_and_hides_navigation_in_meeting_mode()
    {
        var viewModel = new LibraryShellViewModel(new TestLibraryRepository());

        foreach (var destination in new[]
                 {
                     LibraryDestination.Home,
                     LibraryDestination.Classes,
                     LibraryDestination.Recordings,
                     LibraryDestination.Settings,
                     LibraryDestination.RecordClass
                 })
        {
            viewModel.Navigate(destination);
            Assert.Equal(destination, viewModel.CurrentDestination);
            Assert.True(viewModel.IsNavigationVisible);
        }

        viewModel.Navigate(LibraryDestination.Meeting);

        Assert.Equal(LibraryDestination.Meeting, viewModel.CurrentDestination);
        Assert.False(viewModel.IsNavigationVisible);

        viewModel.Navigate(LibraryDestination.Home);
        Assert.True(viewModel.IsNavigationVisible);
    }

    private static TestLibraryRepository SeedTwoClasses()
    {
        var repository = new TestLibraryRepository();
        repository.Classes.AddRange([
            Class(BiologyId, "Biology", archived: false, term: "Fall 2026"),
            Class(ChemistryId, "Chemistry", archived: false)
        ]);
        repository.Recordings.AddRange([
            Recording("mitosis.mp4", BiologyId, Now),
            Recording("cell-lab.mp4", BiologyId, Now.AddMinutes(-1)),
            Recording("chemistry.mp4", ChemistryId, Now.AddMinutes(-2))
        ]);
        return repository;
    }

    private static ClassRecord Class(Guid id, string name, bool archived, string? term = null) =>
        new(id, name, term, Now.AddDays(-10), archived);

    private static RecordingRecord Recording(string fileName, Guid? classId, DateTimeOffset recordedAt) =>
        new(Guid.NewGuid(), classId, $"C:\\Videos\\{fileName}", fileName, null, recordedAt,
            TimeSpan.FromMinutes(42), 50_000_000, true);

    private sealed class TestLibraryRepository : ILibraryRepository
    {
        public List<ClassRecord> Classes { get; } = [];
        public List<RecordingRecord> Recordings { get; } = [];
        public IReadOnlyList<RecordingRecord>? SearchResults { get; set; }
        public Exception? ClassesFailure { get; set; }
        public bool BlockRecordingsUntilCancellation { get; set; }
        public TaskCompletionSource RecordingsRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool LastIncludeArchived { get; private set; } = true;
        public Guid? LastListClassId { get; private set; }
        public Guid? LastSearchClassId { get; private set; }
        public string? LastSearchQuery { get; private set; }
        public int SearchCallCount { get; private set; }

        public Task<ClassRecord> CreateClassAsync(string name, string? term, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RecordingRecord> AddRecordingAsync(RecordingRecord recording, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RecordingRecord?> FindRecordingByPathAsync(string canonicalPath, CancellationToken cancellationToken) =>
            Task.FromResult<RecordingRecord?>(null);

        public Task<IReadOnlyList<ClassRecord>> ListClassesAsync(bool includeArchived, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastIncludeArchived = includeArchived;
            if (ClassesFailure is not null)
            {
                return Task.FromException<IReadOnlyList<ClassRecord>>(ClassesFailure);
            }

            return Task.FromResult<IReadOnlyList<ClassRecord>>(
                Classes.Where(item => includeArchived || !item.IsArchived).ToArray());
        }

        public async Task<IReadOnlyList<RecordingRecord>> ListRecordingsAsync(Guid? classId, CancellationToken cancellationToken)
        {
            LastListClassId = classId;
            if (BlockRecordingsUntilCancellation)
            {
                RecordingsRequestStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Recordings.Where(item => classId is null || item.ClassId == classId).ToArray();
        }

        public Task<IReadOnlyList<RecordingRecord>> ListUnassignedRecordingsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<RecordingRecord>>(
                Recordings.Where(item => item.ClassId is null).ToArray());
        }

        public Task<IReadOnlyList<RecordingRecord>> SearchClassRecordingsAsync(
            Guid classId, string query, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastSearchClassId = classId;
            LastSearchQuery = query;
            SearchCallCount++;
            return Task.FromResult(SearchResults ?? (IReadOnlyList<RecordingRecord>)Recordings
                .Where(item => item.ClassId == classId && item.FileName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToArray());
        }

        public Task AssignRecordingAsync(Guid recordingId, Guid? classId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AssignRecordingToClassAsync(Guid recordingId, Guid classId, string? meetingIdToRemember, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClassRecord> CreateClassAndAssignRecordingAsync(string name, string? term, Guid recordingId, string? meetingIdToRemember, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MeetingClassMapping?> FindMappingAsync(string meetingId, CancellationToken cancellationToken) => Task.FromResult<MeetingClassMapping?>(null);
        public Task UpsertMappingAsync(MeetingClassMapping mapping, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ForgetMappingAsync(string meetingId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
