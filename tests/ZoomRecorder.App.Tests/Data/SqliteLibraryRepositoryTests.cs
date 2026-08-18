using System.Globalization;
using Microsoft.Data.Sqlite;
using ZoomRecorder.App.Data;
using ZoomRecorder.Core.Library;

namespace ZoomRecorder.App.Tests.Data;

public sealed class SqliteLibraryRepositoryTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 18, 12, 34, 56, 789, TimeSpan.FromHours(-7));

    [Fact]
    public async Task Open_creates_schema_version_1_and_enables_foreign_keys()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);

        await using var command = database.Connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT version FROM schema_info),
                (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN (
                    'schema_info', 'classes', 'recordings', 'meeting_class_mappings',
                    'processing_jobs', 'transcription_chunks', 'lecture_packages',
                    'assignments', 'class_study_guides', 'app_settings')),
                (SELECT foreign_keys FROM pragma_foreign_keys);
            """;

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(10L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }

    [Fact]
    public async Task Reopen_preserves_a_class_and_an_unassigned_recording()
    {
        using var temp = new TestDirectory();
        var recording = Recording(Guid.Parse("10000000-0000-0000-0000-000000000001"), null, temp.File("lesson.mp4"));

        await using (var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default))
        {
            var repository = Repository(database);
            await repository.CreateClassAsync("Biology 101", "Fall 2026", default);
            await repository.AddRecordingAsync(recording, default);
        }

        await using var reopened = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var reopenedRepository = Repository(reopened);
        Assert.Single(await reopenedRepository.ListClassesAsync(false, default));
        Assert.Equal(recording, Assert.Single(await reopenedRepository.ListUnassignedRecordingsAsync(default)));
    }

    [Fact]
    public async Task Repository_methods_round_trip_exact_domain_values()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var repository = Repository(database);

        var created = await repository.CreateClassAsync("Physics", "Fall 2026", default);
        var recording = Recording(
            Guid.Parse("20000000-0000-0000-0000-000000000002"),
            created.Id,
            temp.File("Lecture 01.mp4"),
            "meeting-42",
            new DateTimeOffset(2026, 8, 18, 19, 20, 21, 456, TimeSpan.FromHours(5.5)),
            TimeSpan.FromMilliseconds(2_523_456),
            987_654_321,
            false);

        var added = await repository.AddRecordingAsync(recording, default);
        var found = await repository.FindRecordingByPathAsync(Path.Combine(temp.Path, ".", "Lecture 01.mp4"), default);
        var listedClasses = await repository.ListClassesAsync(false, default);
        var listedRecordings = await repository.ListRecordingsAsync(created.Id, default);

        Assert.Equal(new ClassRecord(created.Id, "Physics", "Fall 2026", CreatedAt, false), created);
        Assert.Equal(created, Assert.Single(listedClasses));
        Assert.Equal(recording with { FilePath = Path.GetFullPath(recording.FilePath) }, added);
        Assert.Equal(added, found);
        Assert.Equal(added, Assert.Single(listedRecordings));
    }

    [Fact]
    public async Task Explicit_assignment_and_null_unassignment_work()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var repository = Repository(database);
        var classRecord = await repository.CreateClassAsync("Chemistry", null, default);
        var recording = await repository.AddRecordingAsync(
            Recording(Guid.Parse("30000000-0000-0000-0000-000000000003"), null, temp.File("chemistry.mp4")),
            default);

        await repository.AssignRecordingAsync(recording.Id, classRecord.Id, default);
        Assert.Equal(classRecord.Id, Assert.Single(await repository.ListRecordingsAsync(classRecord.Id, default)).ClassId);
        Assert.Empty(await repository.ListUnassignedRecordingsAsync(default));

        await repository.AssignRecordingAsync(recording.Id, null, default);
        Assert.Equal(recording.Id, Assert.Single(await repository.ListUnassignedRecordingsAsync(default)).Id);
        Assert.Empty(await repository.ListRecordingsAsync(classRecord.Id, default));
    }

    [Fact]
    public async Task Mapping_upsert_find_and_forget_work_and_foreign_keys_reject_missing_classes()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var repository = Repository(database);
        var firstClass = await repository.CreateClassAsync("First", null, default);
        var secondClass = await repository.CreateClassAsync("Second", null, default);

        await repository.UpsertMappingAsync(new MeetingClassMapping("meeting-42", firstClass.Id), default);
        Assert.Equal(new MeetingClassMapping("meeting-42", firstClass.Id), await repository.FindMappingAsync("meeting-42", default));

        await repository.UpsertMappingAsync(new MeetingClassMapping("meeting-42", secondClass.Id), default);
        Assert.Equal(new MeetingClassMapping("meeting-42", secondClass.Id), await repository.FindMappingAsync("meeting-42", default));

        await Assert.ThrowsAsync<SqliteException>(() => repository.UpsertMappingAsync(
            new MeetingClassMapping("missing-class", Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")), default));

        await repository.ForgetMappingAsync("meeting-42", default);
        Assert.Null(await repository.FindMappingAsync("meeting-42", default));
    }

    [Fact]
    public async Task Canonical_duplicate_recording_paths_are_rejected_by_the_unique_index()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var repository = Repository(database);
        var path = temp.File("duplicate.mp4");

        await repository.AddRecordingAsync(Recording(Guid.NewGuid(), null, path), default);

        await Assert.ThrowsAsync<SqliteException>(() => repository.AddRecordingAsync(
            Recording(Guid.NewGuid(), null, Path.Combine(temp.Path, ".", "duplicate.mp4")), default));
    }

    [Fact]
    public async Task Find_by_path_uses_Windows_case_insensitive_identity_and_preserves_stored_casing()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var repository = Repository(database);
        var originalPath = temp.File("LectureOne.mp4");
        var added = await repository.AddRecordingAsync(Recording(Guid.NewGuid(), null, originalPath), default);

        var found = await repository.FindRecordingByPathAsync(temp.File("lectureone.MP4"), default);

        Assert.Equal(added, found);
        Assert.Equal(Path.GetFullPath(originalPath), found!.FilePath);
    }

    [Fact]
    public async Task Recording_unique_index_rejects_Windows_casing_variants()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var repository = Repository(database);
        await repository.AddRecordingAsync(
            Recording(Guid.NewGuid(), null, temp.File("LectureTwo.mp4")), default);

        await Assert.ThrowsAsync<SqliteException>(() => repository.AddRecordingAsync(
            Recording(Guid.NewGuid(), null, temp.File("lecturetwo.MP4")), default));
    }

    [Fact]
    public async Task Class_scoped_search_cannot_return_another_class_recording()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var repository = Repository(database);
        var biology = await repository.CreateClassAsync("Biology", null, default);
        var history = await repository.CreateClassAsync("History", null, default);
        var biologyRecording = Recording(Guid.NewGuid(), biology.Id, temp.File("cell division.mp4"));
        var historyRecording = Recording(Guid.NewGuid(), history.Id, temp.File("Cell division in history.mp4"));
        await repository.AddRecordingAsync(biologyRecording, default);
        await repository.AddRecordingAsync(historyRecording, default);

        var results = await repository.SearchClassRecordingsAsync(biology.Id, "CELL", default);

        Assert.Equal(biologyRecording.Id, Assert.Single(results).Id);
    }

    [Fact]
    public async Task Class_scoped_search_is_Unicode_case_insensitive()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var repository = Repository(database);
        var economics = await repository.CreateClassAsync("Economics", null, default);
        var recording = Recording(Guid.NewGuid(), economics.Id, temp.File("Économie 101.mp4"));
        await repository.AddRecordingAsync(recording, default);

        var results = await repository.SearchClassRecordingsAsync(economics.Id, "éCONOMIE", default);

        Assert.Equal(recording.Id, Assert.Single(results).Id);
    }

    [Fact]
    public async Task Lists_use_deterministic_ordering_and_exclude_archived_classes_by_default()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var repository = Repository(database);
        var zoo = await repository.CreateClassAsync("Zoo", null, default);
        var alphaTwo = await repository.CreateClassAsync("Alpha", "2", default);
        var alphaOne = await repository.CreateClassAsync("Alpha", "1", default);
        await SetArchivedAsync(database, zoo.Id);

        var timestamp = new DateTimeOffset(2026, 8, 18, 10, 0, 0, TimeSpan.Zero);
        var older = Recording(Guid.Parse("00000000-0000-0000-0000-000000000001"), null, temp.File("older.mp4"), recordedAt: timestamp.AddMinutes(-1));
        var sameTimeHigherId = Recording(Guid.Parse("00000000-0000-0000-0000-000000000003"), alphaOne.Id, temp.File("higher.mp4"), recordedAt: timestamp);
        var sameTimeLowerId = Recording(Guid.Parse("00000000-0000-0000-0000-000000000002"), alphaTwo.Id, temp.File("lower.mp4"), recordedAt: timestamp);
        await repository.AddRecordingAsync(older, default);
        await repository.AddRecordingAsync(sameTimeHigherId, default);
        await repository.AddRecordingAsync(sameTimeLowerId, default);

        var expectedAlphaOrder = new[] { alphaOne, alphaTwo }.OrderBy(item => item.Id).ToArray();
        Assert.Equal(expectedAlphaOrder, await repository.ListClassesAsync(false, default));
        Assert.Equal(
            new[] { expectedAlphaOrder[0], expectedAlphaOrder[1], zoo with { IsArchived = true } },
            await repository.ListClassesAsync(true, default));
        Assert.Equal(
            new[] { sameTimeLowerId.Id, sameTimeHigherId.Id, older.Id },
            (await repository.ListRecordingsAsync(null, default)).Select(item => item.Id));
    }

    [Fact]
    public async Task Blank_class_name_is_rejected()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);

        await Assert.ThrowsAsync<ArgumentException>(() => Repository(database).CreateClassAsync("  ", null, default));
    }

    private static SqliteLibraryRepository Repository(LibraryDatabase database) => new(database, () => CreatedAt);

    private static RecordingRecord Recording(
        Guid id,
        Guid? classId,
        string path,
        string? meetingId = null,
        DateTimeOffset? recordedAt = null,
        TimeSpan? duration = null,
        long byteSize = 123_456,
        bool videoAvailable = true) =>
        new(
            id,
            classId,
            path,
            Path.GetFileName(path),
            meetingId,
            recordedAt ?? new DateTimeOffset(2026, 8, 18, 9, 8, 7, 654, TimeSpan.FromHours(-4)),
            duration ?? TimeSpan.FromMilliseconds(65_432),
            byteSize,
            videoAvailable);

    private static async Task SetArchivedAsync(LibraryDatabase database, Guid classId)
    {
        await using var command = database.Connection.CreateCommand();
        command.CommandText = "UPDATE classes SET is_archived = 1 WHERE id = $id;";
        command.Parameters.AddWithValue("$id", classId.ToString("D", CultureInfo.InvariantCulture));
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ZoomRecorder.Tests", Guid.NewGuid().ToString("D"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string DatabasePath => System.IO.Path.Combine(Path, "nested", "library.db");
        public string File(string fileName) => System.IO.Path.Combine(Path, fileName);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
