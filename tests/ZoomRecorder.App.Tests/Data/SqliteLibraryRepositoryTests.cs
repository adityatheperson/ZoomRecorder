using System.Globalization;
using Microsoft.Data.Sqlite;
using ZoomRecorder.App.Data;
using ZoomRecorder.Core.Library;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.Tests.Data;

public sealed class SqliteLibraryRepositoryTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 18, 12, 34, 56, 789, TimeSpan.FromHours(-7));

    [Fact]
    public async Task Open_creates_schema_version_2_and_enables_foreign_keys()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);

        await using var command = database.Connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT version FROM schema_info),
                (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN (
                    'schema_info', 'classes', 'recordings', 'meeting_class_mappings',
                    'processing_jobs', 'audio_chunks', 'transcription_chunks', 'processing_transcripts', 'lecture_packages',
                    'assignments', 'class_study_guides', 'app_settings', 'recording_deletion_journal',
                    'recording_rename_journal')),
                (SELECT foreign_keys FROM pragma_foreign_keys);
            """;

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.Equal(14L, reader.GetInt64(1));
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
    public async Task Atomic_existing_assignment_rolls_back_when_cancelled_after_recording_update()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var repository = Repository(database);
        var originalClass = await repository.CreateClassAsync("Original", null, default);
        var targetClass = await repository.CreateClassAsync("Target", null, default);
        var recording = await repository.AddRecordingAsync(
            Recording(Guid.NewGuid(), originalClass.Id, temp.File("atomic-existing.mp4"), "meeting-42"),
            default);
        var originalMapping = new MeetingClassMapping("meeting-42", originalClass.Id);
        await repository.UpsertMappingAsync(originalMapping, default);
        using var cancellation = new CancellationTokenSource();
        database.Connection.CreateFunction("cancel_after_assignment", () =>
        {
            cancellation.Cancel();
            return 0;
        });
        await using (var trigger = database.Connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER cancel_after_recording_assignment
                AFTER UPDATE OF class_id ON recordings
                BEGIN
                    SELECT cancel_after_assignment();
                END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.AssignRecordingToClassAsync(
            recording.Id,
            targetClass.Id,
            "meeting-42",
            cancellation.Token));

        Assert.Equal(
            originalClass.Id,
            Assert.Single(await repository.ListRecordingsAsync(originalClass.Id, default)).ClassId);
        Assert.Empty(await repository.ListRecordingsAsync(targetClass.Id, default));
        Assert.Equal(originalMapping, await repository.FindMappingAsync("meeting-42", default));
    }

    [Fact]
    public async Task Atomic_existing_assignment_commits_assignment_and_mapping_together()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var repository = Repository(database);
        var originalClass = await repository.CreateClassAsync("Original", null, default);
        var targetClass = await repository.CreateClassAsync("Target", null, default);
        var recording = await repository.AddRecordingAsync(
            Recording(Guid.NewGuid(), originalClass.Id, temp.File("atomic-existing-success.mp4"), "meeting-42"),
            default);

        await repository.AssignRecordingToClassAsync(
            recording.Id, targetClass.Id, "meeting-42", CancellationToken.None);

        Assert.Equal(
            targetClass.Id,
            Assert.Single(await repository.ListRecordingsAsync(targetClass.Id, default)).ClassId);
        Assert.Equal(
            new MeetingClassMapping("meeting-42", targetClass.Id),
            await repository.FindMappingAsync("meeting-42", default));
    }

    [Fact]
    public async Task Atomic_create_and_assign_rolls_back_class_assignment_and_mapping_on_late_failure()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var repository = Repository(database);
        var originalClass = await repository.CreateClassAsync("Original", null, default);
        var recording = await repository.AddRecordingAsync(
            Recording(Guid.NewGuid(), originalClass.Id, temp.File("atomic-create.mp4"), "meeting-42"),
            default);
        var originalMapping = new MeetingClassMapping("meeting-42", originalClass.Id);
        await repository.UpsertMappingAsync(originalMapping, default);
        await using (var trigger = database.Connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER fail_new_class_mapping
                BEFORE INSERT ON meeting_class_mappings
                WHEN NEW.meeting_id = 'new-meeting'
                BEGIN
                    SELECT RAISE(ABORT, 'forced mapping failure');
                END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => repository.CreateClassAndAssignRecordingAsync(
            "Physics",
            "Fall 2026",
            recording.Id,
            "new-meeting",
            CancellationToken.None));

        Assert.Equal([originalClass], await repository.ListClassesAsync(includeArchived: false, default));
        Assert.Equal(
            originalClass.Id,
            Assert.Single(await repository.ListRecordingsAsync(originalClass.Id, default)).ClassId);
        Assert.Null(await repository.FindMappingAsync("new-meeting", default));
        Assert.Equal(originalMapping, await repository.FindMappingAsync("meeting-42", default));
    }

    [Fact]
    public async Task Atomic_create_without_mapping_rolls_back_when_cancelled_after_class_insert()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var repository = Repository(database);
        var originalClass = await repository.CreateClassAsync("Original", null, default);
        var recording = await repository.AddRecordingAsync(
            Recording(Guid.NewGuid(), originalClass.Id, temp.File("atomic-create-first-cancel.mp4"), "meeting-42"),
            default);
        var originalMapping = new MeetingClassMapping("meeting-42", originalClass.Id);
        await repository.UpsertMappingAsync(originalMapping, default);
        using var cancellation = new CancellationTokenSource();
        database.Connection.CreateFunction("cancel_after_class_insert", () =>
        {
            cancellation.Cancel();
            return 0;
        });
        await using (var trigger = database.Connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER cancel_after_new_class_insert
                AFTER INSERT ON classes
                BEGIN
                    SELECT cancel_after_class_insert();
                END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.CreateClassAndAssignRecordingAsync(
            "Physics",
            "Fall 2026",
            recording.Id,
            meetingIdToRemember: null,
            cancellation.Token));

        Assert.Equal([originalClass], await repository.ListClassesAsync(includeArchived: false, default));
        Assert.Equal(
            originalClass.Id,
            Assert.Single(await repository.ListRecordingsAsync(originalClass.Id, default)).ClassId);
        Assert.Equal(originalMapping, await repository.FindMappingAsync("meeting-42", default));
        Assert.Null(await repository.FindMappingAsync("unrequested-meeting", default));
    }

    [Fact]
    public async Task Atomic_create_and_assign_rolls_back_every_mutation_when_cancelled_after_mapping()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var repository = Repository(database);
        var originalClass = await repository.CreateClassAsync("Original", null, default);
        var recording = await repository.AddRecordingAsync(
            Recording(Guid.NewGuid(), originalClass.Id, temp.File("atomic-create-cancel.mp4"), "meeting-42"),
            default);
        using var cancellation = new CancellationTokenSource();
        database.Connection.CreateFunction("cancel_after_mapping", () =>
        {
            cancellation.Cancel();
            return 0;
        });
        await using (var trigger = database.Connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER cancel_after_new_class_mapping
                AFTER INSERT ON meeting_class_mappings
                WHEN NEW.meeting_id = 'cancel-create'
                BEGIN
                    SELECT cancel_after_mapping();
                END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.CreateClassAndAssignRecordingAsync(
            "Physics",
            "Fall 2026",
            recording.Id,
            "cancel-create",
            cancellation.Token));

        Assert.Equal([originalClass], await repository.ListClassesAsync(includeArchived: false, default));
        Assert.Equal(
            originalClass.Id,
            Assert.Single(await repository.ListRecordingsAsync(originalClass.Id, default)).ClassId);
        Assert.Null(await repository.FindMappingAsync("cancel-create", default));
    }

    [Fact]
    public async Task Atomic_create_and_assign_commits_class_assignment_and_mapping_together()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var repository = Repository(database);
        var recording = await repository.AddRecordingAsync(
            Recording(Guid.NewGuid(), null, temp.File("atomic-create-success.mp4"), "meeting-42"),
            default);

        var created = await repository.CreateClassAndAssignRecordingAsync(
            "Physics", "Fall 2026", recording.Id, "meeting-42", CancellationToken.None);

        Assert.Equal(created, Assert.Single(await repository.ListClassesAsync(false, default)));
        Assert.Equal(
            created.Id,
            Assert.Single(await repository.ListRecordingsAsync(created.Id, default)).ClassId);
        Assert.Equal(
            new MeetingClassMapping("meeting-42", created.Id),
            await repository.FindMappingAsync("meeting-42", default));
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

    [Fact]
    public async Task Study_material_updates_preserve_confirmed_ids_and_manage_stale_state()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var repository = Repository(database);
        var course = await repository.CreateClassAsync("Algorithms", null, default);
        var recording = await repository.AddRecordingAsync(Recording(Guid.NewGuid(), course.Id, temp.File("algorithms.mp4")), default);
        var jobId = Guid.NewGuid();
        var assignmentId = Guid.NewGuid();
        await using (var seed = database.Connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO processing_jobs(id, recording_id, class_id, mp4_path, job_directory, state, delete_video,
                    transcript_committed, lecture_package_committed, assignments_committed, guide_outcome, revision, created_at, updated_at)
                VALUES ($job, $recording, $class, $mp4, $dir, 'Completed', 0, 1, 1, 1, 'Succeeded', 1, $now, $now);
                INSERT INTO processing_transcripts(job_id, artifact_path, artifact_sha256) VALUES ($job, 'old.json', $oldHash);
                INSERT INTO lecture_packages(recording_id, schema_version, artifact_path, artifact_sha256, source_transcript_hash, is_stale, updated_at)
                VALUES ($recording, 1, 'package.json', $oldHash, $oldHash, 0, $now);
                INSERT INTO assignments(id, recording_id, description, due_date_text, confidence, is_user_confirmed, source_timestamp_ms, source_order)
                VALUES ($assignment, $recording, 'Keep me', 'Monday', .9, 1, 100, 0);
                """;
            seed.Parameters.AddWithValue("$job", jobId.ToString("D"));
            seed.Parameters.AddWithValue("$recording", recording.Id.ToString("D"));
            seed.Parameters.AddWithValue("$class", course.Id.ToString("D"));
            seed.Parameters.AddWithValue("$assignment", assignmentId.ToString("D"));
            seed.Parameters.AddWithValue("$mp4", recording.FilePath);
            seed.Parameters.AddWithValue("$dir", temp.File("job"));
            seed.Parameters.AddWithValue("$now", CreatedAt.ToString("O"));
            seed.Parameters.AddWithValue("$oldHash", new string('a', 64));
            await seed.ExecuteNonQueryAsync();
        }

        var edited = new ArtifactCheckpoint(temp.File("edited.json"), new string('b', 64));
        await repository.SaveEditedTranscriptAsync(recording.Id, edited, default);
        Assert.Equal(edited, await repository.GetTranscriptAsync(recording.Id, default));
        await using (var stale = database.Connection.CreateCommand())
        {
            stale.CommandText = "SELECT is_stale FROM lecture_packages WHERE recording_id = $id";
            stale.Parameters.AddWithValue("$id", recording.Id.ToString("D"));
            Assert.Equal(1L, await stale.ExecuteScalarAsync());
        }

        var assignments = await repository.ListAssignmentsAsync(recording.Id, default);
        Assert.Equal(assignmentId, Assert.Single(assignments).Id);
        await repository.SaveRefreshedPackageAsync(
            recording.Id,
            new ArtifactCheckpoint(temp.File("refreshed.json"), new string('c', 64)),
            edited.Sha256,
            assignments,
            default);

        await using var verify = database.Connection.CreateCommand();
        verify.CommandText = "SELECT is_stale FROM lecture_packages WHERE recording_id = $id";
        verify.Parameters.AddWithValue("$id", recording.Id.ToString("D"));
        Assert.Equal(0L, await verify.ExecuteScalarAsync());
        Assert.Equal(assignmentId, Assert.Single(await repository.ListAssignmentsAsync(recording.Id, default)).Id);
    }

    [Fact]
    public async Task Reassignment_marks_both_class_guides_pending_atomically()
    {
        using var temp = new TestDirectory();
        await using var database = await LibraryDatabase.OpenAsync(temp.DatabasePath, default);
        var repository = Repository(database);
        var oldClass = await repository.CreateClassAsync("Old", null, default);
        var newClass = await repository.CreateClassAsync("New", null, default);
        var recording = await repository.AddRecordingAsync(Recording(Guid.NewGuid(), oldClass.Id, temp.File("move.mp4")), default);

        var returnedOld = await repository.ReassignAndMarkGuidesPendingAsync(recording.Id, newClass.Id, default);

        Assert.Equal(oldClass.Id, returnedOld);
        Assert.Equal(newClass.Id, Assert.Single(await repository.ListRecordingsAsync(newClass.Id, default)).ClassId);
        await using var verify = database.Connection.CreateCommand();
        verify.CommandText = "SELECT class_id FROM class_study_guides WHERE is_update_pending = 1 ORDER BY class_id";
        await using var reader = await verify.ExecuteReaderAsync();
        var pending = new List<Guid>();
        while (await reader.ReadAsync()) pending.Add(Guid.Parse(reader.GetString(0)));
        Assert.Equal(new[] { oldClass.Id, newClass.Id }.OrderBy(id => id), pending);
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
