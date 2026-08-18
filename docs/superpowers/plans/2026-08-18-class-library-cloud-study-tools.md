# Class Library and Cloud Study Tools Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a local class-and-recording library plus an on-demand, resumable OpenAI workflow that creates editable lecture study packages and optionally recycles the source MP4 after verified success.

**Architecture:** Keep Zoom capture isolated and publish each finalized `RecordingResult` into a new core library. SQLite and versioned local artifacts are authoritative; a checkpointed coordinator invokes replaceable audio, transcription, study-generation, credential, and recycle-bin adapters. WinUI composes those services and exposes Classes, Recordings, lecture details, processing progress, and settings without making cloud availability a prerequisite for recording.

**Tech Stack:** C# 12, .NET 8, WinUI 3 / Windows App SDK 1.7, Microsoft.Data.Sqlite 8.x, System.Text.Json, xUnit, C++20, Windows Media Foundation, OpenAI file-transcription and Responses APIs.

**Spec:** `docs/superpowers/specs/2026-08-18-class-library-cloud-study-tools-design.md`

## Global Constraints

- One local Windows user; no app account, sharing, collaboration, or cloud library sync.
- Recording and MP4 finalization must work with no OpenAI key and during total cloud unavailability.
- AI processing starts only after the user selects **Transcribe & Summarize** and sees the cloud-use notice.
- Store the OpenAI API key only in Windows Credential Manager; never log it, meeting passcodes, transcripts, or raw model responses.
- Keep MP4s and all durable study artifacts local; cloud processing receives only prepared lecture audio or transcript text required for the requested job.
- Use `gpt-transcribe` as the initial configurable transcription model and split inputs below the endpoint's current 25 MB file limit.
- Preserve successful chunk checkpoints so retry does not repeat completed paid transcription requests.
- Never damage or delete a finalized MP4 on processing failure or cancellation.
- Keep **Delete video after successful processing** off by default and prefer the Windows Recycle Bin.
- Do not recycle an MP4 until transcript, study package, assignments, and class-guide outcome are durably verified.
- User edits beat regenerated suggestions; reprocessing must not silently overwrite confirmed edits.
- Target Windows 11 x64 and retain the existing .NET 8 / WinUI 3 / native Zoom architecture.

## Planned File Structure

- `src/ZoomRecorder.Core/Library/` — domain records, assignment rules, meeting mappings, queries, and repository contracts.
- `src/ZoomRecorder.Core/Processing/` — processing state machine, schemas, transcript merging, coordinator, and external ports.
- `src/ZoomRecorder.App/Data/` — SQLite schema, migrations, repository implementation, and artifact store.
- `src/ZoomRecorder.App/Cloud/` — OpenAI HTTP adapters and sanitized error mapping.
- `src/ZoomRecorder.App/Media/` — managed wrapper for native Media Foundation audio preparation.
- `src/ZoomRecorder.App/Security/` — Windows Credential Manager adapter.
- `src/ZoomRecorder.App/Deletion/` — Recycle Bin adapter.
- `src/ZoomRecorder.App/ViewModels/Library/` — shell, class, recording, lecture, assignment, processing, and settings presentation state.
- `src/ZoomRecorder.App/Views/Library/` — corresponding WinUI pages and dialogs.
- `src/ZoomRecorder.Native/src/media/audio_chunk_exporter.*` — MP4 audio demux/transcode and bounded chunk export.
- `tests/ZoomRecorder.Core.Tests/Library/` and `Processing/` — platform-neutral domain and orchestration tests.
- `tests/ZoomRecorder.App.Tests/Data/`, `Cloud/`, and `ViewModels/Library/` — adapter, persistence, redaction, and presentation tests.
- `tests/ZoomRecorder.Native.Tests/audio_chunk_exporter_tests.cpp` — deterministic native chunk tests.

---

### Task 1: Library Domain and Assignment Rules

**Files:**
- Create: `src/ZoomRecorder.Core/Library/ClassRecord.cs`
- Create: `src/ZoomRecorder.Core/Library/RecordingRecord.cs`
- Create: `src/ZoomRecorder.Core/Library/MeetingClassMapping.cs`
- Create: `src/ZoomRecorder.Core/Library/ILibraryRepository.cs`
- Create: `src/ZoomRecorder.Core/Library/RecordingAssignmentService.cs`
- Test: `tests/ZoomRecorder.Core.Tests/Library/RecordingAssignmentServiceTests.cs`

**Interfaces:**
- Consumes: finalized recording metadata, normalized Zoom meeting ID, optional explicit class ID, and remembered mappings.
- Produces: `ClassRecord`, `RecordingRecord`, `MeetingClassMapping`, `ILibraryRepository`, and `RecordingAssignmentService.RegisterFinalizedAsync(...)`.

- [ ] **Step 1: Write failing assignment tests**

```csharp
[Fact]
public async Task Explicit_class_wins_over_remembered_mapping()
{
    var repository = new FakeLibraryRepository(mappingClassId: ClassA);
    var service = new RecordingAssignmentService(repository, () => Now);

    var recording = await service.RegisterFinalizedAsync(
        Result, "1234567890", explicitClassId: ClassB, rememberExplicitChoice: true, default);

    Assert.Equal(ClassB, recording.ClassId);
    Assert.Equal(ClassB, repository.Mapping!.ClassId);
}

[Fact]
public async Task Missing_mapping_leaves_recording_unassigned()
{
    var service = new RecordingAssignmentService(new FakeLibraryRepository(), () => Now);
    var recording = await service.RegisterFinalizedAsync(Result, "1234567890", null, false, default);
    Assert.Null(recording.ClassId);
}
```

- [ ] **Step 2: Run the focused test and verify failure**

Run: `dotnet test tests/ZoomRecorder.Core.Tests/ZoomRecorder.Core.Tests.csproj --filter RecordingAssignmentServiceTests`

Expected: FAIL because the library types do not exist.

- [ ] **Step 3: Add immutable domain records and the repository contract**

```csharp
public sealed record ClassRecord(Guid Id, string Name, string? Term, DateTimeOffset CreatedAt, bool IsArchived);

public sealed record RecordingRecord(
    Guid Id, Guid? ClassId, string FilePath, string FileName, string? MeetingId,
    DateTimeOffset RecordedAt, TimeSpan Duration, long ByteSize, bool VideoAvailable);

public interface ILibraryRepository
{
    Task<ClassRecord> CreateClassAsync(string name, string? term, CancellationToken cancellationToken);
    Task<RecordingRecord> AddRecordingAsync(RecordingRecord recording, CancellationToken cancellationToken);
    Task<RecordingRecord?> FindRecordingByPathAsync(string canonicalPath, CancellationToken cancellationToken);
    Task<IReadOnlyList<ClassRecord>> ListClassesAsync(bool includeArchived, CancellationToken cancellationToken);
    Task<IReadOnlyList<RecordingRecord>> ListRecordingsAsync(Guid? classId, CancellationToken cancellationToken);
    Task<IReadOnlyList<RecordingRecord>> ListUnassignedRecordingsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<RecordingRecord>> SearchClassRecordingsAsync(Guid classId, string query, CancellationToken cancellationToken);
    Task AssignRecordingAsync(Guid recordingId, Guid? classId, CancellationToken cancellationToken);
    Task<MeetingClassMapping?> FindMappingAsync(string meetingId, CancellationToken cancellationToken);
    Task UpsertMappingAsync(MeetingClassMapping mapping, CancellationToken cancellationToken);
    Task ForgetMappingAsync(string meetingId, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Implement explicit, mapped, and unassigned precedence**

```csharp
var mapped = explicitClassId is null && meetingId is not null
    ? await repository.FindMappingAsync(meetingId, cancellationToken)
    : null;
var classId = explicitClassId ?? mapped?.ClassId;
var recording = RecordingRecord.From(result, meetingId, classId, clock());
await repository.AddRecordingAsync(recording, cancellationToken);
if (rememberExplicitChoice && explicitClassId is not null && meetingId is not null)
    await repository.UpsertMappingAsync(new(meetingId, explicitClassId.Value), cancellationToken);
return recording;
```

- [ ] **Step 5: Run tests and commit**

Run: `dotnet test tests/ZoomRecorder.Core.Tests/ZoomRecorder.Core.Tests.csproj`

Expected: PASS.

```powershell
git add src/ZoomRecorder.Core/Library tests/ZoomRecorder.Core.Tests/Library
git commit -m "feat: model classes and recording assignment"
```

### Task 2: SQLite Library and Versioned Artifact Store

**Files:**
- Modify: `src/ZoomRecorder.App/ZoomRecorder.App.csproj`
- Create: `src/ZoomRecorder.App/Data/LibraryPaths.cs`
- Create: `src/ZoomRecorder.App/Data/LibraryDatabase.cs`
- Create: `src/ZoomRecorder.App/Data/SqliteLibraryRepository.cs`
- Create: `src/ZoomRecorder.App/Data/ArtifactStore.cs`
- Test: `tests/ZoomRecorder.App.Tests/Data/SqliteLibraryRepositoryTests.cs`
- Test: `tests/ZoomRecorder.App.Tests/Data/ArtifactStoreTests.cs`

**Interfaces:**
- Consumes: Task 1 `ILibraryRepository` and stable record IDs.
- Produces: schema version 1, `SqliteLibraryRepository`, and `ArtifactStore.WriteAtomicallyAsync(Guid,string,ReadOnlyMemory<byte>,CancellationToken)`.

- [ ] **Step 1: Add SQLite and failing persistence tests**

Add `<PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.8" />` to `ZoomRecorder.App.csproj`.

```csharp
[Fact]
public async Task Reopening_database_preserves_class_and_unassigned_recording()
{
    await using (var db = await TestDatabase.OpenAsync(Path))
    {
        var repository = new SqliteLibraryRepository(db);
        await repository.CreateClassAsync("Biology 101", "Fall 2026", default);
        await repository.AddRecordingAsync(UnassignedRecording, default);
    }
    await using var reopened = await TestDatabase.OpenAsync(Path);
    Assert.Single(await new SqliteLibraryRepository(reopened).ListClassesAsync(default));
    Assert.Single(await new SqliteLibraryRepository(reopened).ListUnassignedRecordingsAsync(default));
}
```

- [ ] **Step 2: Run tests and verify the missing implementation**

Run: `dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter "SqliteLibraryRepositoryTests|ArtifactStoreTests"`

Expected: FAIL because persistence types do not exist.

- [ ] **Step 3: Create schema version 1 in one transaction**

```sql
CREATE TABLE schema_info(version INTEGER NOT NULL);
CREATE TABLE classes(id TEXT PRIMARY KEY, name TEXT NOT NULL, term TEXT, created_at TEXT NOT NULL, is_archived INTEGER NOT NULL);
CREATE TABLE recordings(id TEXT PRIMARY KEY, class_id TEXT NULL REFERENCES classes(id), file_path TEXT NOT NULL, file_name TEXT NOT NULL, meeting_id TEXT, recorded_at TEXT NOT NULL, duration_ms INTEGER NOT NULL, byte_size INTEGER NOT NULL, video_available INTEGER NOT NULL);
CREATE TABLE meeting_class_mappings(meeting_id TEXT PRIMARY KEY, class_id TEXT NOT NULL REFERENCES classes(id));
CREATE TABLE processing_jobs(id TEXT PRIMARY KEY, recording_id TEXT NOT NULL REFERENCES recordings(id), state TEXT NOT NULL, delete_video INTEGER NOT NULL, completed_chunks INTEGER NOT NULL, error_code TEXT, updated_at TEXT NOT NULL);
CREATE TABLE transcription_chunks(job_id TEXT NOT NULL REFERENCES processing_jobs(id), chunk_index INTEGER NOT NULL, start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL, sha256 TEXT NOT NULL, artifact_path TEXT, PRIMARY KEY(job_id, chunk_index));
CREATE TABLE lecture_packages(recording_id TEXT PRIMARY KEY REFERENCES recordings(id), schema_version INTEGER NOT NULL, artifact_path TEXT NOT NULL, source_transcript_hash TEXT NOT NULL, is_stale INTEGER NOT NULL, updated_at TEXT NOT NULL);
CREATE TABLE assignments(id TEXT PRIMARY KEY, recording_id TEXT NOT NULL REFERENCES recordings(id), description TEXT NOT NULL, due_date_text TEXT, due_at TEXT, confidence REAL NOT NULL, is_user_confirmed INTEGER NOT NULL, source_timestamp_ms INTEGER);
CREATE TABLE class_study_guides(class_id TEXT PRIMARY KEY REFERENCES classes(id), schema_version INTEGER NOT NULL, artifact_path TEXT NOT NULL, is_update_pending INTEGER NOT NULL, updated_at TEXT NOT NULL);
CREATE TABLE app_settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
```

- [ ] **Step 4: Implement parameterized repository queries and atomic artifacts**

`LibraryPaths` returns `%LOCALAPPDATA%\ZoomRecorder\library.db`, `%USERPROFILE%\Documents\Zoom Recorder\Classes`, and `%LOCALAPPDATA%\ZoomRecorder\jobs`. `ArtifactStore` writes `file.tmp`, flushes it, validates nonzero length, then uses `File.Move(temp, destination, true)`.

- [ ] **Step 5: Verify reopen, foreign keys, atomic replacement, and commit**

Run: `dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter "SqliteLibraryRepositoryTests|ArtifactStoreTests"`

Expected: PASS, including `PRAGMA foreign_keys = ON` and a test proving a failed temp write leaves the old artifact intact.

```powershell
git add src/ZoomRecorder.App/ZoomRecorder.App.csproj src/ZoomRecorder.App/Data tests/ZoomRecorder.App.Tests/Data
git commit -m "feat: persist class library and artifacts"
```

### Task 3: Publish Finalized Recordings into the Library

**Files:**
- Modify: `src/ZoomRecorder.App/MainWindow.xaml.cs`
- Modify: `src/ZoomRecorder.App/ViewModels/CompletionViewModel.cs`
- Modify: `src/ZoomRecorder.App/Views/CompletionPage.xaml`
- Modify: `src/ZoomRecorder.App/Views/CompletionPage.xaml.cs`
- Create: `src/ZoomRecorder.App/ViewModels/Library/AssignRecordingViewModel.cs`
- Create: `src/ZoomRecorder.App/Views/Library/AssignRecordingDialog.xaml`
- Test: `tests/ZoomRecorder.App.Tests/CompletionViewModelTests.cs`
- Test: `tests/ZoomRecorder.App.Tests/ViewModels/Library/AssignRecordingViewModelTests.cs`

**Interfaces:**
- Consumes: `RecordingAssignmentService` and the existing `_joinFlow.RecordingCompleted` event.
- Produces: exactly one library record per finalized path plus `AssignAsync(Guid? classId,bool remember,CancellationToken)`.

- [ ] **Step 1: Write failing finalization and assignment tests**

```csharp
[Fact]
public async Task Finalization_registers_recording_once_before_completion_is_shown()
{
    await sut.HandleRecordingCompletedAsync(Result, "1234567890");
    await sut.HandleRecordingCompletedAsync(Result, "1234567890");
    Assert.Single(repository.Recordings);
}

[Fact]
public async Task Assignment_dialog_can_create_and_assign_a_class()
{
    var vm = CreateViewModel();
    await vm.CreateAndAssignAsync("Biology 101", "Fall 2026", remember: true, default);
    Assert.Equal("Biology 101", repository.AssignedClass!.Name);
}
```

- [ ] **Step 2: Run focused tests and verify failure**

Run: `dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter "CompletionViewModelTests|AssignRecordingViewModelTests"`

Expected: FAIL on missing library integration.

- [ ] **Step 3: Register the finalized recording before navigation**

```csharp
private async Task ShowCompletionAsync(RecordingResult result)
{
    var recording = await assignmentService.RegisterFinalizedAsync(
        result, _joinFlow.CurrentMeetingId, null, false, CancellationToken.None);
    RootFrame.Content = new CompletionPage(
        new CompletionViewModel(result, recording.Id), ShowLibrary, ShowAssignmentDialog);
}
```

Make registration idempotent with a unique canonical `file_path` index and repository lookup.

- [ ] **Step 4: Add **Assign to class** to completion and details flow**

Bind the dialog to current classes and two commands: select existing, or create-and-select. Show **Remember this Zoom meeting for this class** only when a meeting ID is available.

- [ ] **Step 5: Run all managed tests and commit**

Run: `dotnet test ZoomRecorder.sln -c Debug`

Expected: PASS; existing completion actions remain unchanged.

```powershell
git add src/ZoomRecorder.App tests/ZoomRecorder.App.Tests
git commit -m "feat: add finalized recordings to class library"
```

### Task 4: Library Navigation, Classes, Recordings, and Search

**Files:**
- Modify: `src/ZoomRecorder.App/MainWindow.xaml`
- Modify: `src/ZoomRecorder.App/MainWindow.xaml.cs`
- Create: `src/ZoomRecorder.App/ViewModels/Library/LibraryShellViewModel.cs`
- Create: `src/ZoomRecorder.App/ViewModels/Library/ClassesViewModel.cs`
- Create: `src/ZoomRecorder.App/ViewModels/Library/ClassDetailViewModel.cs`
- Create: `src/ZoomRecorder.App/ViewModels/Library/RecordingsViewModel.cs`
- Create: `src/ZoomRecorder.App/Views/Library/ClassesPage.xaml`
- Create: `src/ZoomRecorder.App/Views/Library/ClassDetailPage.xaml`
- Create: `src/ZoomRecorder.App/Views/Library/RecordingsPage.xaml`
- Test: `tests/ZoomRecorder.App.Tests/ViewModels/Library/LibraryViewModelTests.cs`

**Interfaces:**
- Consumes: repository list/search methods and existing join navigation.
- Produces: Home, Classes, Recordings, Settings navigation; class cards; assigned/unassigned recording lists; class-scoped search.

- [ ] **Step 1: Write failing query and navigation tests**

```csharp
[Fact]
public async Task Classes_page_shows_active_classes_and_unassigned_count()
{
    var vm = new ClassesViewModel(repository);
    await vm.LoadAsync(default);
    Assert.Equal("Biology 101", Assert.Single(vm.Classes).Name);
    Assert.Equal(2, vm.UnassignedCount);
}

[Fact]
public async Task Class_search_never_returns_another_class_recording()
{
    var vm = new ClassDetailViewModel(repository, BiologyId);
    await vm.SearchAsync("mitosis", default);
    Assert.All(vm.Lectures, lecture => Assert.Equal(BiologyId, lecture.ClassId));
}
```

- [ ] **Step 2: Run focused tests and verify failure**

Run: `dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter LibraryViewModelTests`

Expected: FAIL because library view models do not exist.

- [ ] **Step 3: Replace the root frame-only shell with `NavigationView`**

```xml
<NavigationView x:Name="Nav" PaneDisplayMode="Left" SelectionChanged="Navigate">
  <NavigationView.MenuItems>
    <NavigationViewItem Content="Home" Tag="home" Icon="Home" />
    <NavigationViewItem Content="Classes" Tag="classes" Icon="Library" />
    <NavigationViewItem Content="Recordings" Tag="recordings" Icon="Video" />
  </NavigationView.MenuItems>
  <Frame x:Name="RootFrame" />
</NavigationView>
```

Meeting mode temporarily hides the pane but retains the existing Zoom lifecycle and status UI.

- [ ] **Step 4: Implement the approved classes and class-detail layouts**

Use `ItemsRepeater`/`ListView` with virtualization. Class detail exposes **Lectures**, **Study guide**, **Assignments**, and **Class settings** tabs. Empty states link directly to **Record a class** or **Assign recording**.

- [ ] **Step 5: Build, run view-model tests, and commit**

Run: `dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter LibraryViewModelTests`

Run: `dotnet build src/ZoomRecorder.App/ZoomRecorder.App.csproj -c Debug -p:Platform=x64`

Expected: PASS and XAML compilation succeeds.

```powershell
git add src/ZoomRecorder.App/MainWindow.xaml* src/ZoomRecorder.App/ViewModels/Library src/ZoomRecorder.App/Views/Library tests/ZoomRecorder.App.Tests/ViewModels/Library
git commit -m "feat: add classes and recordings library UI"
```

### Task 5: Processing Domain, Schemas, and Transcript Merge

**Files:**
- Create: `src/ZoomRecorder.Core/Processing/ProcessingState.cs`
- Create: `src/ZoomRecorder.Core/Processing/ProcessingJob.cs`
- Create: `src/ZoomRecorder.Core/Processing/TranscriptModels.cs`
- Create: `src/ZoomRecorder.Core/Processing/StudyPackage.cs`
- Create: `src/ZoomRecorder.Core/Processing/ProcessingPorts.cs`
- Create: `src/ZoomRecorder.Core/Processing/TranscriptMerger.cs`
- Test: `tests/ZoomRecorder.Core.Tests/Processing/ProcessingDomainTests.cs`
- Test: `tests/ZoomRecorder.Core.Tests/Processing/TranscriptMergerTests.cs`

**Interfaces:**
- Consumes: bounded audio chunks and versioned cloud responses.
- Produces: explicit processing transitions, `TranscriptMerger.Merge(IReadOnlyList<TranscriptChunk>)`, `StudyPackage` schema version 1, and adapter contracts.

- [ ] **Step 1: Write failing transition and overlap tests**

```csharp
[Fact]
public void Cannot_complete_before_guide_outcome_is_recorded()
{
    var job = ProcessingJob.Start(RecordingId, deleteVideo: true, Now);
    Assert.Throws<InvalidProcessingTransitionException>(() => job.MoveTo(ProcessingState.Completed, Now));
}

[Fact]
public void Merge_removes_repeated_overlap_and_keeps_absolute_timestamps()
{
    var result = TranscriptMerger.Merge([Chunk(0, "cells divide", 0), Chunk(1, "divide by mitosis", 9_000)]);
    Assert.Equal("cells divide by mitosis", result.Text);
    Assert.Equal(9_000, result.Segments.Last().StartMilliseconds);
}
```

- [ ] **Step 2: Run focused tests and verify failure**

Run: `dotnet test tests/ZoomRecorder.Core.Tests/ZoomRecorder.Core.Tests.csproj --filter "ProcessingDomainTests|TranscriptMergerTests"`

Expected: FAIL because processing types do not exist.

- [ ] **Step 3: Implement state transitions and external ports**

```csharp
public interface IAudioChunkPreparer
{
    Task<IReadOnlyList<AudioChunk>> PrepareAsync(string mp4Path, string jobDirectory, long maxBytes, CancellationToken cancellationToken);
}
public interface ITranscriptionClient
{
    Task<TranscriptChunk> TranscribeAsync(AudioChunk chunk, CancellationToken cancellationToken);
}
public interface IStudyGenerationClient
{
    Task<StudyPackage> GenerateLectureAsync(Transcript transcript, CancellationToken cancellationToken);
    Task<ClassStudyGuide> GenerateGuideAsync(IReadOnlyList<StudyPackage> lectures, CancellationToken cancellationToken);
}
public interface ICredentialVault { Task<string?> GetApiKeyAsync(CancellationToken cancellationToken); Task SaveApiKeyAsync(string apiKey, CancellationToken cancellationToken); }
public interface IVideoRecycler { Task<RecycleResult> RecycleAsync(string path, CancellationToken cancellationToken); }
```

- [ ] **Step 4: Implement deterministic transcript merge and JSON schema validation**

Deduplicate only normalized word suffix/prefix matches within the configured overlap window; never collapse repeated words elsewhere. Define required `StudyPackage` members with `[JsonRequired]`, reject unknown schema versions, confidence outside `0..1`, negative timestamps, and assignments with blank descriptions.

- [ ] **Step 5: Test and commit**

Run: `dotnet test tests/ZoomRecorder.Core.Tests/ZoomRecorder.Core.Tests.csproj`

Expected: PASS.

```powershell
git add src/ZoomRecorder.Core/Processing tests/ZoomRecorder.Core.Tests/Processing
git commit -m "feat: define resumable study processing domain"
```

### Task 6: Native Audio Chunk Export

**Files:**
- Modify: `src/ZoomRecorder.Native/include/zoom_recorder.h`
- Modify: `src/ZoomRecorder.Native/src/api.cpp`
- Create: `src/ZoomRecorder.Native/src/media/audio_chunk_exporter.h`
- Create: `src/ZoomRecorder.Native/src/media/audio_chunk_exporter.cpp`
- Create: `src/ZoomRecorder.App/Media/NativeAudioChunkPreparer.cs`
- Test: `tests/ZoomRecorder.Native.Tests/audio_chunk_exporter_tests.cpp`
- Test: `tests/ZoomRecorder.App.Tests/Media/NativeAudioChunkPreparerTests.cs`

**Interfaces:**
- Consumes: finalized MP4, private job directory, 24 MB target maximum, and cancellation flag.
- Produces: mono 16 kHz AAC/M4A chunks with 5-second overlap plus `AudioChunk(Index,Path,Start,End,Sha256,ByteSize)`.

- [ ] **Step 1: Write failing native boundary tests**

```cpp
TEST(AudioChunkExporter, BoundsEveryChunkAndAddsOverlap) {
  constexpr auto max_bytes = 24ull * 1024ull * 1024ull;
  auto chunks = export_fixture("fixtures/sixty_minute_audio.mp4", max_bytes, 5s);
  ASSERT_GT(chunks.size(), 1u);
  EXPECT_TRUE(std::ranges::all_of(chunks, [=](auto& c) { return c.byte_size <= max_bytes; }));
  EXPECT_EQ(5s, chunks[0].end - chunks[1].start);
}
```

- [ ] **Step 2: Run native test and verify failure**

Run: `cmake --build artifacts/native --config Debug`

Run: `ctest --test-dir artifacts/native -C Debug --output-on-failure`

Expected: FAIL because `audio_chunk_exporter` is missing.

- [ ] **Step 3: Implement Media Foundation export with temp-file publication**

Use `IMFSourceReader` to decode the MP4 audio stream, resample to 16 kHz mono PCM, and `IMFSinkWriter` to encode AAC in M4A. Close each chunk before measuring it; if it exceeds 24 MB, retry that time range at a lower AAC bitrate. Write `.partial`, finalize, hash, then rename to `.m4a`.

- [ ] **Step 4: Add a narrow native ABI and managed wrapper**

```cpp
ZR_API zr_result zr_prepare_audio_chunks(
    const wchar_t* mp4_path, const wchar_t* output_directory,
    uint64_t max_chunk_bytes, zr_chunk_callback callback, void* context);
ZR_API zr_result zr_cancel_audio_preparation();
```

`NativeAudioChunkPreparer` converts callback metadata into `AudioChunk` and deletes incomplete `.partial` files on cancellation.

- [ ] **Step 5: Run native and managed adapter tests, then commit**

Run: `ctest --test-dir artifacts/native -C Debug --output-on-failure`

Run: `dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter NativeAudioChunkPreparerTests`

Expected: PASS; no chunk exceeds 24 MB and the original MP4 hash is unchanged.

```powershell
git add src/ZoomRecorder.Native src/ZoomRecorder.App/Media tests/ZoomRecorder.Native.Tests tests/ZoomRecorder.App.Tests/Media
git commit -m "feat: prepare bounded lecture audio chunks"
```

### Task 7: Secure Credentials and OpenAI Adapters

**Files:**
- Create: `src/ZoomRecorder.App/Security/WindowsCredentialVault.cs`
- Create: `src/ZoomRecorder.App/Cloud/OpenAiApiClient.cs`
- Create: `src/ZoomRecorder.App/Cloud/OpenAiTranscriptionClient.cs`
- Create: `src/ZoomRecorder.App/Cloud/OpenAiStudyGenerationClient.cs`
- Create: `src/ZoomRecorder.App/Cloud/OpenAiErrorMapper.cs`
- Test: `tests/ZoomRecorder.App.Tests/Security/WindowsCredentialVaultTests.cs`
- Test: `tests/ZoomRecorder.App.Tests/Cloud/OpenAiClientTests.cs`

**Interfaces:**
- Consumes: Task 5 ports, API key from Credential Manager, audio chunks, transcript, and versioned JSON schema.
- Produces: sanitized `CloudProcessingException.Code`, timestamped transcript chunks, validated study packages, and validated class guides.

- [ ] **Step 1: Write failing HTTP contract and redaction tests**

```csharp
[Fact]
public async Task Transcription_uses_configured_model_and_multipart_audio()
{
    var handler = new RecordingHandler(Json("{\"text\":\"hello\"}"));
    await CreateTranscriber(handler, "secret-key").TranscribeAsync(Chunk, default);
    Assert.Equal("Bearer secret-key", handler.Request!.Headers.Authorization!.ToString());
    Assert.Contains("gpt-transcribe", await handler.BodyAsync());
}

[Fact]
public void Error_mapping_never_contains_key_or_transcript()
{
    var error = OpenAiErrorMapper.Map(429, "secret-key", "lecture transcript");
    Assert.DoesNotContain("secret-key", error.Message);
    Assert.DoesNotContain("lecture transcript", error.Message);
    Assert.Equal(CloudErrorCode.RateLimited, error.Code);
}
```

- [ ] **Step 2: Run focused tests and verify failure**

Run: `dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter "OpenAiClientTests|WindowsCredentialVaultTests"`

Expected: FAIL because adapters do not exist.

- [ ] **Step 3: Implement generic-credential storage**

Use `CredWriteW`, `CredReadW`, `CredFree`, and target name `ZoomRecorder/OpenAI`. Copy credential bytes into a short-lived managed string, zero unmanaged buffers that the app owns, and expose only Save, Read, and Delete operations.

- [ ] **Step 4: Implement transcription and strict Responses requests**

POST multipart requests to `https://api.openai.com/v1/audio/transcriptions` with `model=gpt-transcribe`. POST study requests to `https://api.openai.com/v1/responses` with initial configurable model `gpt-5.6-luna`, `text.format.type=json_schema`, `strict=true`, and schema name `zoom_recorder_study_package_v1`. Define both model identifiers in `OpenAiOptions`, not UI code, so a later supported-model change does not alter stored artifacts.

- [ ] **Step 5: Map retry behavior without logging bodies**

Map 401 to `InvalidCredential`, 402/403 to `AccountRestricted`, 408 and network exceptions to `NetworkUnavailable`, 429 to `RateLimited`, 5xx to `ServiceUnavailable`, and schema failures to `InvalidResponse`. Honor `Retry-After` with capped exponential backoff and cancellation.

- [ ] **Step 6: Run adapter tests and commit**

Run: `dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter "OpenAiClientTests|WindowsCredentialVaultTests"`

Expected: PASS using fake `HttpMessageHandler`; tests make no real API calls.

```powershell
git add src/ZoomRecorder.App/Security src/ZoomRecorder.App/Cloud tests/ZoomRecorder.App.Tests/Security tests/ZoomRecorder.App.Tests/Cloud
git commit -m "feat: add secure OpenAI processing adapters"
```

### Task 8: Checkpointed Processing Coordinator

**Files:**
- Create: `src/ZoomRecorder.Core/Processing/ProcessingCoordinator.cs`
- Create: `src/ZoomRecorder.Core/Processing/IProcessingJobStore.cs`
- Create: `src/ZoomRecorder.App/Data/SqliteProcessingJobStore.cs`
- Test: `tests/ZoomRecorder.Core.Tests/Processing/ProcessingCoordinatorTests.cs`
- Test: `tests/ZoomRecorder.App.Tests/Data/SqliteProcessingJobStoreTests.cs`

**Interfaces:**
- Consumes: Tasks 2, 5, 6, and 7 ports.
- Produces: `StartAsync`, `ResumeAsync`, `CancelAsync`, durable progress events, reusable chunk checkpoints, and independently retryable guide updates.

- [ ] **Step 1: Write failing resume and safety tests**

```csharp
[Fact]
public async Task Resume_reuses_completed_chunks()
{
    store.Seed(JobAtChunkTwoOfThree);
    await coordinator.ResumeAsync(JobId, default);
    Assert.Equal([2], transcriber.RequestedChunkIndexes);
}

[Fact]
public async Task Failed_generation_preserves_mp4_and_previous_package()
{
    generator.FailLecture = true;
    await Assert.ThrowsAsync<CloudProcessingException>(() => coordinator.StartAsync(Request, default));
    Assert.False(recycler.WasCalled);
    Assert.Equal(PreviousPackage, artifacts.ReadPackage(RecordingId));
}
```

- [ ] **Step 2: Run focused tests and verify failure**

Run: `dotnet test tests/ZoomRecorder.Core.Tests/ZoomRecorder.Core.Tests.csproj --filter ProcessingCoordinatorTests`

Expected: FAIL because the coordinator does not exist.

- [ ] **Step 3: Implement one checkpoint per committed stage**

```csharp
await jobs.MoveAsync(job.Id, ProcessingState.PreparingAudio, null, ct);
var chunks = await audio.PrepareAsync(recording.FilePath, job.Directory, MaxChunkBytes, ct);
foreach (var chunk in chunks.Where(c => !job.CompletedChunkIndexes.Contains(c.Index)))
{
    var transcriptChunk = await transcription.TranscribeAsync(chunk, ct);
    await jobs.CommitChunkAsync(job.Id, transcriptChunk, ct);
}
var transcript = TranscriptMerger.Merge(await jobs.ReadChunksAsync(job.Id, ct));
await jobs.MoveAsync(job.Id, ProcessingState.GeneratingStudyPackage, null, ct);
```

Commit the lecture package before updating the guide. If guide generation fails, retain the old guide, mark `is_update_pending=1`, and leave the lecture usable.

- [ ] **Step 4: Add clean pause, startup recovery, and sanitized failures**

Cancellation moves to `Cancelled` only after current durable writes finish. On startup, jobs in active states become resumable; `.partial` artifacts are removed only inside their recorded job directory. Persist error codes, never service bodies.

- [ ] **Step 5: Run coordinator and SQLite recovery tests, then commit**

Run: `dotnet test tests/ZoomRecorder.Core.Tests/ZoomRecorder.Core.Tests.csproj --filter ProcessingCoordinatorTests`

Run: `dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter SqliteProcessingJobStoreTests`

Expected: PASS for failure injected after every stage and no repeated completed chunk calls.

```powershell
git add src/ZoomRecorder.Core/Processing src/ZoomRecorder.App/Data tests/ZoomRecorder.Core.Tests/Processing tests/ZoomRecorder.App.Tests/Data
git commit -m "feat: checkpoint and resume lecture processing"
```

### Task 9: Edit Preservation, Assignments, and Class Guide Rebuild

**Files:**
- Create: `src/ZoomRecorder.Core/Processing/StudyMaterialMergeService.cs`
- Create: `src/ZoomRecorder.Core/Processing/ClassGuideService.cs`
- Modify: `src/ZoomRecorder.App/Data/SqliteLibraryRepository.cs`
- Test: `tests/ZoomRecorder.Core.Tests/Processing/StudyMaterialMergeServiceTests.cs`
- Test: `tests/ZoomRecorder.Core.Tests/Processing/ClassGuideServiceTests.cs`

**Interfaces:**
- Consumes: generated packages, confirmed user edits, and all completed lecture packages for a class.
- Produces: merged assignments, stale-state tracking, and atomic rebuild requests for both classes after reassignment.

- [ ] **Step 1: Write failing edit and reassignment tests**

```csharp
[Fact]
public void Regeneration_does_not_overwrite_confirmed_assignment()
{
    var result = StudyMaterialMergeService.Merge(GeneratedAssignment("Friday"), ConfirmedAssignment("Monday"));
    Assert.Equal("Monday", Assert.Single(result).DueDateText);
}

[Fact]
public async Task Reassignment_rebuilds_old_and_new_guides_without_transcription()
{
    await service.ReassignAsync(RecordingId, NewClassId, default);
    Assert.Equal([OldClassId, NewClassId], guideBuilder.Requests.Order());
    Assert.Equal(0, transcriber.CallCount);
}
```

- [ ] **Step 2: Run focused tests and verify failure**

Run: `dotnet test tests/ZoomRecorder.Core.Tests/ZoomRecorder.Core.Tests.csproj --filter "StudyMaterialMergeServiceTests|ClassGuideServiceTests"`

Expected: FAIL because merge services do not exist.

- [ ] **Step 3: Implement stable edit identities and stale flags**

Assignments use stable IDs once confirmed. Regeneration may add suggestions and update unconfirmed suggestions, but it preserves confirmed description and date fields. Transcript edits update its content hash and set the package `IsStale`; **Refresh study materials** uses the edited transcript and skips `ITranscriptionClient`.

- [ ] **Step 4: Rebuild guides only from completed package artifacts**

Load packages ordered by recording date, generate a replacement guide, validate it, write atomically, then clear `is_update_pending`. Reassignment schedules rebuilds for old and new classes after the relationship commit.

- [ ] **Step 5: Test and commit**

Run: `dotnet test tests/ZoomRecorder.Core.Tests/ZoomRecorder.Core.Tests.csproj`

Expected: PASS.

```powershell
git add src/ZoomRecorder.Core/Processing src/ZoomRecorder.App/Data tests/ZoomRecorder.Core.Tests/Processing
git commit -m "feat: preserve edits and rebuild class guides"
```

### Task 10: Processing, Lecture, Assignment, and Settings UI

**Files:**
- Create: `src/ZoomRecorder.App/ViewModels/Library/LectureDetailViewModel.cs`
- Create: `src/ZoomRecorder.App/ViewModels/Library/ProcessingViewModel.cs`
- Create: `src/ZoomRecorder.App/ViewModels/Library/AssignmentsViewModel.cs`
- Create: `src/ZoomRecorder.App/ViewModels/Library/StudyGuideViewModel.cs`
- Create: `src/ZoomRecorder.App/ViewModels/Library/SettingsViewModel.cs`
- Create: `src/ZoomRecorder.App/Views/Library/LectureDetailPage.xaml`
- Create: `src/ZoomRecorder.App/Views/Library/ProcessingDialog.xaml`
- Create: `src/ZoomRecorder.App/Views/Library/SettingsPage.xaml`
- Modify: `src/ZoomRecorder.App/Views/Library/ClassDetailPage.xaml`
- Test: `tests/ZoomRecorder.App.Tests/ViewModels/Library/ProcessingViewModelTests.cs`
- Test: `tests/ZoomRecorder.App.Tests/ViewModels/Library/LectureDetailViewModelTests.cs`

**Interfaces:**
- Consumes: processing coordinator progress, study artifacts, assignments, credential vault, and app settings.
- Produces: cloud confirmation, progress/retry/cancel UI, editable materials, refresh action, key management, and deletion preference.

- [ ] **Step 1: Write failing UI-state tests**

```csharp
[Fact]
public async Task Start_requires_cloud_notice_and_never_defaults_delete_video_on()
{
    var vm = CreateProcessingViewModel(settingsDefaultDelete: false);
    Assert.False(vm.DeleteVideoAfterSuccess);
    await vm.StartAsync(default);
    Assert.True(vm.CloudNoticeWasPresented);
}

[Fact]
public void Invalid_key_error_offers_settings_action()
{
    var vm = CreateLectureViewModel();
    vm.Apply(ProcessingProgress.Failed(CloudErrorCode.InvalidCredential));
    Assert.Equal("Check API key", vm.RecoveryActionText);
}
```

- [ ] **Step 2: Run focused tests and verify failure**

Run: `dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter "ProcessingViewModelTests|LectureDetailViewModelTests"`

Expected: FAIL because processing presentation types do not exist.

- [ ] **Step 3: Implement the confirmation and progress UI**

The dialog states that audio leaves the PC, displays estimated upload bytes and cost only when calculable from current configured pricing, shows the class, and leaves deletion unchecked unless the user has explicitly changed the saved default. Progress labels map exactly to Preparing audio, Transcribing, Creating study materials, Updating class guide, Completed, Needs attention, and Cancelled.

- [ ] **Step 4: Implement editable lecture tabs and class aggregates**

Lecture detail includes Summary, Notes, Transcript, Key terms, Assignments, and Review questions. Timestamp buttons seek the local video when available and disable with an explanation after video deletion. Transcript save marks derived output stale; refresh asks before cloud use.

- [ ] **Step 5: Add settings and meeting-mapping controls**

Settings can Save/Test/Delete API key, set the future deletion default, and show privacy text. Class settings list remembered meeting IDs with **Forget mapping**; forgetting affects future recordings only.

- [ ] **Step 6: Build, test, and commit**

Run: `dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter "ProcessingViewModelTests|LectureDetailViewModelTests"`

Run: `dotnet build src/ZoomRecorder.App/ZoomRecorder.App.csproj -c Debug -p:Platform=x64`

Expected: PASS with successful XAML compilation.

```powershell
git add src/ZoomRecorder.App/ViewModels/Library src/ZoomRecorder.App/Views/Library tests/ZoomRecorder.App.Tests/ViewModels/Library
git commit -m "feat: add lecture study and processing UI"
```

### Task 11: Verified MP4 Recycling and Permanent-Delete Fallback

**Files:**
- Create: `src/ZoomRecorder.App/Deletion/WindowsVideoRecycler.cs`
- Create: `src/ZoomRecorder.Core/Processing/DeletionEligibility.cs`
- Modify: `src/ZoomRecorder.Core/Processing/ProcessingCoordinator.cs`
- Modify: `src/ZoomRecorder.App/ViewModels/Library/ProcessingViewModel.cs`
- Test: `tests/ZoomRecorder.Core.Tests/Processing/DeletionEligibilityTests.cs`
- Test: `tests/ZoomRecorder.App.Tests/Deletion/WindowsVideoRecyclerTests.cs`

**Interfaces:**
- Consumes: verified artifact hashes, committed database state, guide success/explicit acceptance, and per-job deletion selection.
- Produces: `DeletionEligibility.Evaluate(...)`, recoverable recycle result, explicit permanent-delete prompt, and `VideoAvailable=false` only after deletion succeeds.

- [ ] **Step 1: Write failing eligibility tests**

```csharp
[Theory]
[InlineData(false, true, true, false)]
[InlineData(true, false, true, false)]
[InlineData(true, true, false, false)]
[InlineData(true, true, true, true)]
public void Delete_requires_package_assignments_and_guide_outcome(
    bool package, bool assignments, bool guideOutcome, bool expected)
{
    Assert.Equal(expected, DeletionEligibility.Evaluate(package, assignments, guideOutcome).CanDelete);
}
```

- [ ] **Step 2: Run focused tests and verify failure**

Run: `dotnet test tests/ZoomRecorder.Core.Tests/ZoomRecorder.Core.Tests.csproj --filter DeletionEligibilityTests`

Expected: FAIL because eligibility logic does not exist.

- [ ] **Step 3: Implement Recycle Bin behavior**

Use `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin)` on a canonical path matching the recording record. Return `RecycleUnavailable` instead of permanently deleting on any unsupported-shell or policy error.

- [ ] **Step 4: Gate coordinator deletion and add fallback confirmation**

Re-read artifact hashes and committed status immediately before recycle. If recycling is unavailable, return a pending decision to the UI. Only a separate user confirmation may call permanent `File.Delete`; cancellation retains the video. After successful deletion, set `VideoAvailable=false` and keep all study records.

- [ ] **Step 5: Run tests and commit**

Run: `dotnet test tests/ZoomRecorder.Core.Tests/ZoomRecorder.Core.Tests.csproj --filter DeletionEligibilityTests`

Run: `dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter WindowsVideoRecyclerTests`

Expected: PASS using a fake shell adapter; automated tests do not delete user files.

```powershell
git add src/ZoomRecorder.Core/Processing src/ZoomRecorder.App/Deletion src/ZoomRecorder.App/ViewModels/Library tests
git commit -m "feat: recycle processed videos after verified success"
```

### Task 12: Composition, Release Packaging, and End-to-End Verification

**Files:**
- Modify: `src/ZoomRecorder.App/App.xaml.cs`
- Modify: `src/ZoomRecorder.App/MainWindow.xaml.cs`
- Modify: `eng/Verify-Release.ps1`
- Create: `docs/verification/class-library-cloud-study-tools.md`
- Test: `tests/ZoomRecorder.App.Tests/StudyWorkflowTests.cs`

**Interfaces:**
- Consumes: all previous tasks and the existing release package pipeline.
- Produces: production composition, startup migration/recovery, release dependency checks, and full-flow verification evidence.

- [ ] **Step 1: Write a fake-adapter end-to-end workflow test**

```csharp
[Fact]
public async Task Finalize_assign_process_edit_refresh_reopen_and_recycle()
{
    await app.FinalizeRecording(Result, "1234567890");
    await app.AssignToNewClass("Biology 101", remember: true);
    await app.Process(deleteVideo: true);
    await app.EditTranscript("Corrected mitosis explanation");
    await app.RefreshStudyMaterials();
    await app.Restart();
    Assert.True(app.CurrentLecture.HasStudyPackage);
    Assert.False(app.CurrentLecture.VideoAvailable);
}
```

- [ ] **Step 2: Compose production services and startup order**

`App.OnLaunched` creates `LibraryPaths`, runs SQLite migration, cleans only registered stale job temp directories, creates repository/adapters/coordinator, loads resumable jobs, and then creates `MainWindow`. The Zoom `NativeSession` remains independently disposable. Use one shared `HttpClient` with a bounded timeout and no request-body logging.

- [ ] **Step 3: Extend release verification**

```powershell
$deps = Get-Content -Raw -LiteralPath (Join-Path $ReleaseDirectory 'ZoomRecorder.App.deps.json')
if ($deps -notmatch 'Microsoft.Data.Sqlite') { throw 'SQLite library dependency is missing.' }
if ($deps -match 'secret-key|sk-[A-Za-z0-9]') { throw 'Release appears to contain an API key.' }
if (-not (Test-Path -LiteralPath (Join-Path $ReleaseDirectory 'ZoomRecorder.Native.dll'))) { throw 'Native audio preparation is missing.' }
```

- [ ] **Step 4: Run the complete automated suite**

Run: `dotnet test ZoomRecorder.sln -c Release`

Run: `cmake --build artifacts/native-release --config Release`

Run: `ctest --test-dir artifacts/native-release -C Release --output-on-failure`

Run: `dotnet build src/ZoomRecorder.App/ZoomRecorder.App.csproj -c Release -p:Platform=x64`

Run: `pwsh -File eng/Verify-Release.ps1 -ReleaseDirectory D:\ZoomRecorder\outputs\ZoomRecorder-0.2.0`

Expected: all commands PASS; the unrelated existing `work/` directory is not packaged.

- [ ] **Step 5: Perform the manual verification matrix**

Verify: record with no API key; create/rename/archive class; assigned and unassigned recordings; create class during assignment; remember/override/forget meeting mapping; successful real transcription; invalid key; no billing; offline start; 429 retry; close/reopen at every processing stage; edit transcript; refresh without retranscription; assignment confirmation preservation; guide-update failure; class reassignment; cancellation; recycle success; recycle-unavailable fallback; video-unavailable UI; original recording hash unchanged after every failed job.

- [ ] **Step 6: Record evidence and commit**

Document app version, Windows build, Zoom SDK version, OpenAI model configuration, test account retention setting, recording lengths, chunk sizes, request counts across retry, generated artifact paths, deletion outcomes, and every matrix result in `docs/verification/class-library-cloud-study-tools.md` without recording secrets or lecture content.

```powershell
git add src/ZoomRecorder.App/App.xaml.cs src/ZoomRecorder.App/MainWindow.xaml.cs eng/Verify-Release.ps1 tests/ZoomRecorder.App.Tests/StudyWorkflowTests.cs docs/verification/class-library-cloud-study-tools.md
git commit -m "test: verify class study workflow end to end"
```

## Execution Checkpoints

- After Task 4: the app is a useful local class/recording organizer with no cloud dependency.
- After Task 8: fake-adapter processing is durable and restartable; real OpenAI calls are isolated behind tested adapters.
- After Task 10: the complete study workflow is usable while MP4 retention remains conservative.
- After Task 12: deletion safety, packaging, and real-service/manual verification are complete.
