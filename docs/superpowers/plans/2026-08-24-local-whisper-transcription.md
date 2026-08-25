# Local Whisper Transcription Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace paid cloud processing with a manual, English-only local Whisper flow that finishes at an editable, checkpointed transcript and never calls OpenAI.

**Architecture:** Keep the existing `ITranscriptionClient` and checkpointed coordinator boundary. Add a verified first-use `small.en` model manager, a native M4A-to-PCM WAV adapter, and an isolated whisper.cpp worker runner that tries a Vulkan build before a CPU build; then complete the job immediately after transcript commit.

**Tech Stack:** C#/.NET 8, WinUI 3, SQLite, C++20, Windows Media Foundation, whisper.cpp v1.9.1 (`f049fff`), CMake 3.28, Vulkan GPU worker, xUnit, CTest, PowerShell release verification.

**Spec:** `docs/superpowers/specs/2026-08-24-local-whisper-transcription-design.md`

## Global Constraints

- Windows x64 only; preserve `net8.0-windows10.0.19041.0` and Windows App SDK 1.7.
- English-only model: `ggml-small.en.bin` from Hugging Face repository `ggerganov/whisper.cpp`, pinned to repository revision `5359861c739e955e79d9a303bcbc70fb988958b1`.
- The model is downloaded on first use into `%LOCALAPPDATA%\ZoomRecorder\Models`; it is not committed or packaged.
- Model size and SHA-256 come from the pinned Hugging Face LFS metadata and are committed in a manifest before implementation tests are accepted.
- The release contains Vulkan and CPU workers built from whisper.cpp v1.9.1 commit `f049fff` plus required license notices.
- The end user installs no CUDA or Vulkan SDK; Vulkan initialization failure falls back once to CPU.
- Local transcription never reads the OpenAI credential, constructs an OpenAI client, sends audio/transcript data to a network service, or deletes the MP4.
- Preserve all unrelated untracked build directories and user changes.
- Use test-driven development for every behavior change and commit after each task.

## File Structure

### New application files

- `src/ZoomRecorder.App/LocalTranscription/WhisperModelManifest.cs` — immutable source URL, length, digest, and local filename.
- `src/ZoomRecorder.App/LocalTranscription/WhisperModelManager.cs` — verified, coalesced, cancellable first-use download.
- `src/ZoomRecorder.App/LocalTranscription/ILocalPcmAudioConverter.cs` — narrow managed conversion port.
- `src/ZoomRecorder.App/LocalTranscription/NativeLocalPcmAudioConverter.cs` — native conversion adapter and result validation.
- `src/ZoomRecorder.App/LocalTranscription/IWhisperWorkerRunner.cs` — process-runner port and result records.
- `src/ZoomRecorder.App/LocalTranscription/WhisperWorkerRunner.cs` — Vulkan-first/CPU-fallback worker process lifecycle.
- `src/ZoomRecorder.App/LocalTranscription/WhisperWorkerJson.cs` — strict worker-output DTOs and mapping.
- `src/ZoomRecorder.App/LocalTranscription/LocalWhisperTranscriptionClient.cs` — `ITranscriptionClient` orchestration.
- `src/ZoomRecorder.App/LocalTranscription/LocalTranscriptionPaths.cs` — canonical model and worker asset paths.
- `src/ZoomRecorder.App/Assets/Whisper/model-small.en.json` — pinned model manifest.
- `eng/Build-WhisperWorkers.ps1` — reproducible pinned whisper.cpp CPU/Vulkan worker build.
- `eng/whisper.cpp.version` — exact tag and commit consumed by the build script.
- `README.md` — personal-use launch, model-download, privacy, and offline-transcription instructions.

### New native files

- `src/ZoomRecorder.Native/src/media/pcm_wav_converter.h` — native conversion API implementation contract.
- `src/ZoomRecorder.Native/src/media/pcm_wav_converter.cpp` — Media Foundation M4A decode and atomic WAV publication.
- `tests/ZoomRecorder.Native.Tests/pcm_wav_converter_tests.cpp` — WAV publication, cancellation, and failure tests.

### New managed tests

- `tests/ZoomRecorder.App.Tests/LocalTranscription/WhisperModelManagerTests.cs`
- `tests/ZoomRecorder.App.Tests/LocalTranscription/NativeLocalPcmAudioConverterTests.cs`
- `tests/ZoomRecorder.App.Tests/LocalTranscription/WhisperWorkerRunnerTests.cs`
- `tests/ZoomRecorder.App.Tests/LocalTranscription/WhisperWorkerJsonTests.cs`
- `tests/ZoomRecorder.App.Tests/LocalTranscription/LocalWhisperTranscriptionClientTests.cs`
- `tests/ZoomRecorder.App.Tests/Composition/AppServicesTests.cs`

### Existing files changed

- `src/ZoomRecorder.Core/Processing/ProcessingPorts.cs`
- `src/ZoomRecorder.Core/Processing/ProcessingJob.cs`
- `src/ZoomRecorder.Core/Processing/IProcessingJobStore.cs`
- `src/ZoomRecorder.Core/Processing/ProcessingCoordinator.cs`
- `src/ZoomRecorder.App/Data/SqliteProcessingJobStore.cs`
- `src/ZoomRecorder.App/Interop/NativeMethods.cs`
- `src/ZoomRecorder.App/Composition/AppServices.cs`
- `src/ZoomRecorder.App/ViewModels/Library/ProcessingViewModel.cs`
- `src/ZoomRecorder.App/ViewModels/Library/LectureDetailViewModel.cs`
- `src/ZoomRecorder.App/Views/Library/ProcessingDialog.xaml`
- `src/ZoomRecorder.App/Views/Library/LectureDetailPage.xaml`
- `src/ZoomRecorder.App/MainWindow.xaml.cs`
- `src/ZoomRecorder.App/ZoomRecorder.App.csproj`
- `src/ZoomRecorder.Native/include/zoom_recorder.h`
- `src/ZoomRecorder.Native/src/api.cpp`
- `src/ZoomRecorder.Native/CMakeLists.txt`
- `tests/ZoomRecorder.Core.Tests/Processing/ProcessingDomainTests.cs`
- `tests/ZoomRecorder.Core.Tests/Processing/ProcessingCoordinatorTests.cs`
- `tests/ZoomRecorder.App.Tests/Data/SqliteProcessingJobStoreTests.cs`
- `tests/ZoomRecorder.App.Tests/ViewModels/Library/ProcessingViewModelTests.cs`
- `tests/ZoomRecorder.App.Tests/ViewModels/Library/LectureDetailViewModelTests.cs`
- `eng/Verify-Release.ps1`
- `Launch Zoom Recorder.cmd`

---

### Task 1: Add transcript-only completion and local activity semantics

**Files:**
- Modify: `src/ZoomRecorder.Core/Processing/ProcessingPorts.cs`
- Modify: `src/ZoomRecorder.Core/Processing/ProcessingJob.cs`
- Modify: `src/ZoomRecorder.Core/Processing/IProcessingJobStore.cs`
- Modify: `src/ZoomRecorder.Core/Processing/ProcessingCoordinator.cs`
- Modify: `src/ZoomRecorder.App/Data/SqliteProcessingJobStore.cs`
- Test: `tests/ZoomRecorder.Core.Tests/Processing/ProcessingDomainTests.cs`
- Test: `tests/ZoomRecorder.Core.Tests/Processing/ProcessingCoordinatorTests.cs`
- Test: `tests/ZoomRecorder.App.Tests/Data/SqliteProcessingJobStoreTests.cs`

**Interfaces:**
- Produces: `TranscriptionActivityKind`, `TranscriptionActivity`, job-scoped `ITranscriptionClient.TranscribeAsync(..., IProgress<TranscriptionActivity>?, ...)`, `ProcessingProgress.TranscriptionActivity`, `IProcessingJobStore.CompleteTranscriptOnlyAsync(...)`, and local-specific `CloudProcessingErrorCode` values used by later tasks.
- Consumes: existing `TranscriptChunk`, `ProcessingProgress`, and transcript checkpoint APIs.

- [ ] **Step 1: Write failing domain and coordinator tests**

Add tests proving that a job with a committed transcript can complete directly from `Transcribing`, that it cannot do so before transcript commit, and that the coordinator never calls study generation, guide generation, or video deletion:

```csharp
[Fact]
public void Transcript_only_completion_requires_committed_transcript()
{
    var job = ProcessingJob.Start(JobId, RecordingId, deleteVideo: true, StartedAt);
    job.TransitionTo(ProcessingState.PreparingAudio, StartedAt.AddSeconds(1));
    job.TransitionTo(ProcessingState.Transcribing, StartedAt.AddSeconds(2));
    Assert.Throws<InvalidProcessingTransitionException>(() =>
        job.CompleteTranscriptOnly(StartedAt.AddSeconds(3)));
    job.MarkTranscriptCommitted(StartedAt.AddSeconds(3));
    job.CompleteTranscriptOnly(StartedAt.AddSeconds(4));
    Assert.Equal(ProcessingState.Completed, job.State);
}
```

```csharp
[Fact]
public async Task Transcript_only_run_stops_after_transcript_and_preserves_video()
{
    var fixture = CoordinatorFixture.TranscriptOnly(deleteVideo: true);
    await fixture.Coordinator.StartAsync(fixture.Request, CancellationToken.None);
    Assert.Equal(ProcessingState.Completed, fixture.Store.Current.State);
    Assert.True(fixture.Store.Current.TranscriptCommitted);
    Assert.Equal(0, fixture.Study.GenerateLectureCalls);
    Assert.Equal(0, fixture.Study.GenerateGuideCalls);
    Assert.Equal(0, fixture.Recycler.Calls);
}
```

- [ ] **Step 2: Run the focused tests and verify red**

Run:

```powershell
dotnet test tests/ZoomRecorder.Core.Tests/ZoomRecorder.Core.Tests.csproj --filter "Transcript_only"
dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter "CompleteTranscriptOnly"
```

Expected: FAIL because transcript-only completion and store APIs do not exist.

- [ ] **Step 3: Add activity and error contracts**

Extend the core port without exposing Whisper-specific implementation types:

```csharp
public enum TranscriptionActivityKind
{
    AcquiringModel,
    Transcribing,
    UsingCpuFallback
}

public sealed record TranscriptionActivity(
    TranscriptionActivityKind Kind,
    long? CompletedBytes = null,
    long? TotalBytes = null);

public interface ITranscriptionClient
{
    Task<TranscriptChunk> TranscribeAsync(
        AudioChunk chunk,
        IProgress<TranscriptionActivity>? progress,
        CancellationToken cancellationToken);
}
```

Add optional `TranscriptionActivity? TranscriptionActivity`, `long? ActivityCompletedBytes`, and `long? ActivityTotalBytes` data to `ProcessingProgress`. Update every fake transcription client in core/app tests to accept the new progress parameter.

Add `ModelDownloadFailed`, `ModelVerificationFailed`, `LocalAudioConversionFailed`, `LocalTranscriptionRuntimeFailed`, and `LocalTranscriptionOutputInvalid` to the existing persisted error enum, with explicit actionable messages in `ProcessingOperationException`.

- [ ] **Step 4: Implement transcript-only domain/store completion**

Add:

```csharp
public void CompleteTranscriptOnly(DateTimeOffset now)
{
    EnsureTimestamp(now);
    if (State is not (ProcessingState.Transcribing or ProcessingState.GeneratingStudyPackage or ProcessingState.UpdatingClassGuide) ||
        !TranscriptCommitted)
        throw new InvalidProcessingTransitionException(State, ProcessingState.Completed);
    State = ProcessingState.Completed;
    UpdatedAt = CompletedAt = now;
}
```

Add the store signature:

```csharp
Task<ProcessingJobSnapshot> CompleteTranscriptOnlyAsync(
    Guid jobId,
    long expectedRevision,
    CancellationToken cancellationToken);
```

Implement it as one optimistic-concurrency SQLite update that requires `transcript_committed = 1`, accepts `Transcribing`, `GeneratingStudyPackage`, `UpdatingClassGuide`, or `NeedsAttention` whose `failed_stage` is one of those stages, sets `state = 'Completed'`, clears failure fields, increments `revision`, and does not modify lecture/assignment/guide columns.

- [ ] **Step 5: Change coordinator execution and activity forwarding**

For each job, pass a job-scoped `IProgress<TranscriptionActivity>` callback into `TranscribeAsync`; map each callback to `ProcessingProgress` for that job so concurrent jobs cannot mix activity. After `TranscribeAsync` commits the merged transcript, call `CompleteTranscriptOnlyAsync`, publish `Completed`, clean transient job files while preserving published checkpoints, and return. Remove the execution path into cloud study generation for new/resumed work but retain historical read APIs.

- [ ] **Step 6: Run focused and complete core tests**

Run:

```powershell
dotnet test tests/ZoomRecorder.Core.Tests/ZoomRecorder.Core.Tests.csproj
dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter "SqliteProcessingJobStoreTests"
```

Expected: all tests pass with no study-generation calls on transcript-only jobs.

- [ ] **Step 7: Commit**

```powershell
git add src/ZoomRecorder.Core/Processing src/ZoomRecorder.App/Data/SqliteProcessingJobStore.cs tests/ZoomRecorder.Core.Tests/Processing tests/ZoomRecorder.App.Tests/Data/SqliteProcessingJobStoreTests.cs
git commit -m "feat: complete processing after local transcript"
```

---

### Task 2: Build the verified first-use Whisper model manager

**Files:**
- Create: `src/ZoomRecorder.App/LocalTranscription/WhisperModelManifest.cs`
- Create: `src/ZoomRecorder.App/LocalTranscription/WhisperModelManager.cs`
- Create: `src/ZoomRecorder.App/LocalTranscription/LocalTranscriptionPaths.cs`
- Create: `src/ZoomRecorder.App/Assets/Whisper/model-small.en.json`
- Modify: `src/ZoomRecorder.App/ZoomRecorder.App.csproj`
- Test: `tests/ZoomRecorder.App.Tests/LocalTranscription/WhisperModelManagerTests.cs`

**Interfaces:**
- Produces: `IWhisperModelManager.EnsureModelAsync(IProgress<ModelDownloadProgress>?, CancellationToken) -> Task<string>` and canonical paths used by Task 5.
- Consumes: `HttpClient`, `WhisperModelManifest`, and `%LOCALAPPDATA%` path resolution.

Define the task boundary explicitly:

```csharp
internal sealed record ModelDownloadProgress(long CompletedBytes, long TotalBytes);

internal interface IWhisperModelManager
{
    Task<string> EnsureModelAsync(
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Add the pinned model manifest and parser test**

Add the exact LFS metadata for `ggml-small.en.bin` at revision `5359861c739e955e79d9a303bcbc70fb988958b1`:

```json
{
  "schemaVersion": 1,
  "fileName": "ggml-small.en.bin",
  "downloadUri": "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-small.en.bin",
  "byteLength": 487614201,
  "sha256": "c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d"
}
```

Add a manifest parser test that asserts the exact filename, URI, byte length, digest, positive length, and exactly 64 lowercase hexadecimal digest characters.

- [ ] **Step 2: Write failing model lifecycle tests**

Use a fake `HttpMessageHandler` and temporary directory to cover: verified first download, cached verification, `.partial` cleanup on cancellation, corrupt-file quarantine, hash mismatch rejection, byte progress, and two concurrent callers sharing one HTTP request.

```csharp
[Fact]
public async Task Concurrent_callers_share_one_verified_download()
{
    var fixture = ModelFixture.ValidPayload();
    var first = fixture.Manager.EnsureModelAsync(null, CancellationToken.None);
    var second = fixture.Manager.EnsureModelAsync(null, CancellationToken.None);
    Assert.Equal(await first, await second);
    Assert.Equal(1, fixture.Handler.RequestCount);
    Assert.True(File.Exists(fixture.FinalModelPath));
}
```

- [ ] **Step 3: Run the model tests and verify red**

Run:

```powershell
dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter "WhisperModelManagerTests"
```

Expected: FAIL because the model manager types do not exist.

- [ ] **Step 4: Implement strict manifest parsing and path containment**

`WhisperModelManifest.Load` must reject unknown schema versions, non-HTTPS URLs, URLs outside the pinned `huggingface.co/ggerganov/whisper.cpp` origin/path, invalid filenames, nonpositive sizes, and invalid digests. `LocalTranscriptionPaths` canonicalizes the model root and rejects any manifest filename escaping that root.

- [ ] **Step 5: Implement streaming, coalesced, atomic acquisition**

Use one guarded shared task; stream with `HttpCompletionOption.ResponseHeadersRead`; update SHA-256 incrementally; report `(bytesRead, manifest.ByteLength)`; flush; verify size/digest; and `File.Move(partial, final, overwrite: false)`. Quarantine mismatch files using `ggml-small.en.bin.corrupt-<GUID>` and never expose a `.partial` or corrupt path as a model.

- [ ] **Step 6: Run tests and commit**

Run:

```powershell
dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter "WhisperModelManagerTests"
```

Expected: all model manager tests pass.

```powershell
git add src/ZoomRecorder.App/LocalTranscription src/ZoomRecorder.App/Assets/Whisper src/ZoomRecorder.App/ZoomRecorder.App.csproj tests/ZoomRecorder.App.Tests/LocalTranscription/WhisperModelManagerTests.cs
git commit -m "feat: add verified whisper model download"
```

---

### Task 3: Add cancellable native M4A-to-PCM WAV conversion

**Files:**
- Create: `src/ZoomRecorder.Native/src/media/pcm_wav_converter.h`
- Create: `src/ZoomRecorder.Native/src/media/pcm_wav_converter.cpp`
- Create: `tests/ZoomRecorder.Native.Tests/pcm_wav_converter_tests.cpp`
- Modify: `src/ZoomRecorder.Native/include/zoom_recorder.h`
- Modify: `src/ZoomRecorder.Native/src/api.cpp`
- Modify: `src/ZoomRecorder.Native/CMakeLists.txt`
- Modify: `src/ZoomRecorder.App/Interop/NativeMethods.cs`
- Create: `src/ZoomRecorder.App/LocalTranscription/ILocalPcmAudioConverter.cs`
- Create: `src/ZoomRecorder.App/LocalTranscription/NativeLocalPcmAudioConverter.cs`
- Test: `tests/ZoomRecorder.App.Tests/LocalTranscription/NativeLocalPcmAudioConverterTests.cs`

**Interfaces:**
- Produces: native `zr_convert_audio_to_pcm_wav`, cancellation/destroy APIs, and managed `ILocalPcmAudioConverter.ConvertAsync(AudioChunk, string jobDirectory, CancellationToken) -> Task<string>`.
- Consumes: existing `zr_result`, Media Foundation, and validated M4A `AudioChunk` paths.

- [ ] **Step 1: Write failing native tests**

Add tests for a valid fixture producing a RIFF/WAVE PCM file with format `1`, one channel, 16,000 Hz, 16 bits, correct data length; cancellation publishing no final file; invalid source; and output path collision. Assert publication uses `.partial` followed by rename.

- [ ] **Step 2: Add the native API contract**

Add opaque `zr_pcm_convert_handle` and exports:

```cpp
ZR_API zr_result zr_convert_audio_to_pcm_wav(
    const wchar_t* m4a_path,
    const wchar_t* wav_path,
    zr_pcm_convert_handle* out_handle);
ZR_API zr_result zr_cancel_pcm_conversion(zr_pcm_convert_handle handle);
ZR_API zr_result zr_destroy_pcm_conversion(zr_pcm_convert_handle handle);
```

- [ ] **Step 3: Run native tests and verify red**

Run:

```powershell
cmake --build build-release --config Release
ctest --test-dir build-release -C Release --output-on-failure
```

Expected: compile/test failure until the converter is implemented.

- [ ] **Step 4: Implement Media Foundation decode and WAV publication**

Use `IMFSourceReader` to request PCM, mono, 16 kHz, 16-bit samples; stream samples to `<wav>.partial`; check the atomic cancellation flag between samples; patch the RIFF/data lengths only on successful end-of-stream; flush and atomically rename to the requested final path. Map missing stream to `ZR_AUDIO_STREAM_MISSING`, decode/type failures to `ZR_MEDIA_ERROR`, cancellation to `ZR_CANCELLED`, and publication failures to `ZR_IO_ERROR`.

- [ ] **Step 5: Add the managed adapter and tests**

Mirror the existing `NativeAudioChunkPreparer` handle/cancellation pattern. Validate that source and result are absolute, source is the expected M4A checkpoint, final WAV remains inside the job directory, and the result header is PCM mono/16 kHz/16-bit. Delete `.partial` and final transient WAV on failed conversion.

- [ ] **Step 6: Run native and managed tests**

Run:

```powershell
cmake --build build-release --config Release
ctest --test-dir build-release -C Release --output-on-failure
dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter "NativeLocalPcmAudioConverterTests"
```

Expected: all focused tests pass.

- [ ] **Step 7: Commit**

```powershell
git add src/ZoomRecorder.Native src/ZoomRecorder.App/Interop/NativeMethods.cs src/ZoomRecorder.App/LocalTranscription/ILocalPcmAudioConverter.cs src/ZoomRecorder.App/LocalTranscription/NativeLocalPcmAudioConverter.cs tests/ZoomRecorder.Native.Tests tests/ZoomRecorder.App.Tests/LocalTranscription/NativeLocalPcmAudioConverterTests.cs
git commit -m "feat: convert local transcript audio to pcm wav"
```

---

### Task 4: Implement the isolated Vulkan-first Whisper worker runner

**Files:**
- Create: `src/ZoomRecorder.App/LocalTranscription/IWhisperWorkerRunner.cs`
- Create: `src/ZoomRecorder.App/LocalTranscription/WhisperWorkerRunner.cs`
- Test: `tests/ZoomRecorder.App.Tests/LocalTranscription/WhisperWorkerRunnerTests.cs`

**Interfaces:**
- Produces: `IWhisperWorkerRunner.RunAsync(WhisperWorkerRequest, CancellationToken) -> Task<WhisperWorkerResult>`.
- Consumes: canonical GPU/CPU worker paths, model path, PCM WAV path, and job output base.

- [ ] **Step 1: Define request/result records and failing tests**

```csharp
internal sealed record WhisperWorkerRequest(
    string ModelPath,
    string WavPath,
    string OutputBasePath);

internal sealed record WhisperWorkerResult(
    string JsonPath,
    bool UsedCpuFallback);
```

Test GPU success, GPU nonzero exit then CPU success, both failures, cancellation killing the process tree, output missing, absolute path validation, output containment, and bounded stderr truncation.

- [ ] **Step 2: Run worker tests and verify red**

Run:

```powershell
dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter "WhisperWorkerRunnerTests"
```

Expected: FAIL because the runner does not exist.

- [ ] **Step 3: Implement safe process creation**

Use `ProcessStartInfo.UseShellExecute = false`, redirect stderr/stdout, set `CreateNoWindow = true`, and populate only `ArgumentList`:

```csharp
arguments.Add("--model"); arguments.Add(request.ModelPath);
arguments.Add("--file"); arguments.Add(request.WavPath);
arguments.Add("--language"); arguments.Add("en");
arguments.Add("--output-json-full");
arguments.Add("--output-file"); arguments.Add(request.OutputBasePath);
arguments.Add("--no-prints");
```

Do not accept arbitrary extra arguments. Capture at most 16 KiB of stderr. On cancellation call `Kill(entireProcessTree: true)`, await exit, and rethrow cancellation.

- [ ] **Step 4: Implement fallback classification**

Attempt the Vulkan worker first. Fall back exactly once when process start fails, exit code is nonzero, or no JSON artifact exists. If CPU also fails, throw a local-runtime exception containing only the bounded diagnostic category and exit codes. Do not retry when a JSON artifact exists but later schema validation fails.

- [ ] **Step 5: Run tests and commit**

```powershell
dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter "WhisperWorkerRunnerTests"
git add src/ZoomRecorder.App/LocalTranscription/IWhisperWorkerRunner.cs src/ZoomRecorder.App/LocalTranscription/WhisperWorkerRunner.cs tests/ZoomRecorder.App.Tests/LocalTranscription/WhisperWorkerRunnerTests.cs
git commit -m "feat: run isolated whisper workers with cpu fallback"
```

---

### Task 5: Parse worker JSON and implement `LocalWhisperTranscriptionClient`

**Files:**
- Create: `src/ZoomRecorder.App/LocalTranscription/WhisperWorkerJson.cs`
- Create: `src/ZoomRecorder.App/LocalTranscription/LocalWhisperTranscriptionClient.cs`
- Test: `tests/ZoomRecorder.App.Tests/LocalTranscription/WhisperWorkerJsonTests.cs`
- Test: `tests/ZoomRecorder.App.Tests/LocalTranscription/LocalWhisperTranscriptionClientTests.cs`

**Interfaces:**
- Produces: concrete `ITranscriptionClient` used by Task 6.
- Consumes: `IWhisperModelManager`, `ILocalPcmAudioConverter`, `IWhisperWorkerRunner`, `AudioChunk`, and core transcription activity contracts.

- [ ] **Step 1: Capture a schema fixture and write failing parser tests**

Check in a minimal, non-sensitive whisper.cpp v1.9.1 `--output-json-full` fixture under the test project. Test ordered valid segments, blank text rejection, negative/nonfinite/reversed times, overlapping out-of-order segments, missing required fields, unknown root schema members, and the 250 ms end tolerance with clamp.

```csharp
[Fact]
public void Final_segment_within_rounding_tolerance_is_clamped()
{
    var result = WhisperWorkerJson.Parse(JsonEndingAt(10.200), chunkDurationMs: 10_000);
    Assert.Equal(10_000, result.Segments[^1].EndMilliseconds);
}
```

- [ ] **Step 2: Run parser tests and verify red**

Run:

```powershell
dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter "WhisperWorkerJsonTests"
```

Expected: FAIL because the parser does not exist.

- [ ] **Step 3: Implement strict parsing and normalization**

Deserialize with `JsonUnmappedMemberHandling.Disallow`, map whisper timestamps to checked milliseconds, normalize whitespace without changing words, require monotonic ordering, allow/clamp only `<= 250 ms` excess at the final boundary, and delete invalid JSON output before throwing `LocalTranscriptionOutputInvalid`.

- [ ] **Step 4: Write failing client orchestration tests**

Prove reported activity order (`AcquiringModel`, `Transcribing`, optional `UsingCpuFallback`), converter/worker argument flow, chunk timestamp offset, cleanup of WAV and JSON on success/failure/cancel, preservation of M4A, and no duplicate worker call when a cancellation occurs during model download.

- [ ] **Step 5: Implement local client orchestration**

For each chunk: validate source; acquire/verify model; report acquisition bytes through the supplied `IProgress<TranscriptionActivity>`; convert to a unique WAV in the job directory; run worker; report fallback activity when returned; parse/map JSON; and clean WAV/JSON in `finally`. Wrap failures into the specific local processing error without swallowing caller cancellation.

- [ ] **Step 6: Run focused tests and commit**

```powershell
dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter "WhisperWorkerJsonTests|LocalWhisperTranscriptionClientTests"
git add src/ZoomRecorder.App/LocalTranscription/WhisperWorkerJson.cs src/ZoomRecorder.App/LocalTranscription/LocalWhisperTranscriptionClient.cs tests/ZoomRecorder.App.Tests/LocalTranscription
git commit -m "feat: transcribe audio chunks with local whisper"
```

---

### Task 6: Compose local-only processing and update the WinUI workflow

**Files:**
- Modify: `src/ZoomRecorder.App/Composition/AppServices.cs`
- Modify: `src/ZoomRecorder.App/MainWindow.xaml.cs`
- Modify: `src/ZoomRecorder.App/ViewModels/Library/ProcessingViewModel.cs`
- Modify: `src/ZoomRecorder.App/ViewModels/Library/LectureDetailViewModel.cs`
- Modify: `src/ZoomRecorder.App/Views/Library/ProcessingDialog.xaml`
- Modify: `src/ZoomRecorder.App/Views/Library/LectureDetailPage.xaml`
- Create: `tests/ZoomRecorder.App.Tests/Composition/AppServicesTests.cs`
- Modify: `tests/ZoomRecorder.App.Tests/ViewModels/Library/ProcessingViewModelTests.cs`
- Modify: `tests/ZoomRecorder.App.Tests/ViewModels/Library/LectureDetailViewModelTests.cs`

**Interfaces:**
- Consumes: Tasks 1–5 local transcription components and progress events.
- Produces: user-visible manual **Transcribe locally** workflow and composition proof that OpenAI is unused.

- [ ] **Step 1: Write failing composition and view-model tests**

Assert that transcript-only composition creates `LocalWhisperTranscriptionClient` and never reads `ICredentialVault`; the primary command text is **Transcribe locally**; model byte progress is determinate; normal inference is indeterminate; CPU fallback text is visible; completion says **Transcript ready**; delete-video and cloud-cost/upload UI are hidden; and failures remain retryable.

- [ ] **Step 2: Run the focused tests and verify red**

Run:

```powershell
dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter "AppServicesTests|ProcessingViewModelTests|LectureDetailViewModelTests"
```

Expected: FAIL on current cloud labels/composition.

- [ ] **Step 3: Replace cloud composition**

Construct one shared `HttpClient` for model download only, load the embedded manifest, and compose:

```csharp
var transcription = new LocalWhisperTranscriptionClient(
    new WhisperModelManager(http, manifest, paths.ModelsRoot),
    new NativeLocalPcmAudioConverter(),
    new WhisperWorkerRunner(paths.GpuWorkerPath, paths.CpuWorkerPath));
```

Pass this client into `ProcessingCoordinator`. Do not instantiate `OpenAiApiClient` or either OpenAI processing adapter. Historical study-material readers may remain, but refresh/generation commands must be unavailable in this version.

- [ ] **Step 4: Update processing and lecture UI**

Change dialog title/button/info text to local transcription. Bind `ProgressBar.IsIndeterminate` and value/maximum separately so model downloads show determinate byte progress. Remove/hide upload estimate, cost estimate, cloud notice, and MP4 deletion checkbox. Map activities to exact strings from the spec and map Completed to **Transcript ready**.

In lecture detail, rename the primary action to **Transcribe locally**. Keep Transcript editable. Disable summary refresh and render non-generated tabs as unavailable without opening a cloud dialog.

- [ ] **Step 5: Run application tests and commit**

```powershell
dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj
git add src/ZoomRecorder.App tests/ZoomRecorder.App.Tests
git commit -m "feat: expose manual local transcription workflow"
```

---

### Task 7: Build and package pinned whisper.cpp workers

**Files:**
- Create: `eng/Build-WhisperWorkers.ps1`
- Create: `eng/whisper.cpp.version`
- Modify: `src/ZoomRecorder.App/ZoomRecorder.App.csproj`
- Modify: `eng/Verify-Release.ps1`
- Add generated release assets only under ignored `outputs/ZoomRecorder-0.4.0/tools/whisper/`

**Interfaces:**
- Produces: `tools/whisper/vulkan/whisper-cli.exe`, Vulkan runtime dependencies, `tools/whisper/cpu/whisper-cli.exe`, and license notices consumed by `LocalTranscriptionPaths`.
- Consumes: pinned whisper.cpp tag `v1.9.1` and commit `f049fff`.

- [ ] **Step 1: Make release verification fail on the current package**

Extend `Verify-Release.ps1` to require the two worker paths, their adjacent required DLLs, `LICENSE-whisper.cpp`, `LICENSE-ggml`, and `Assets/Whisper/model-small.en.json`; reject any `ggml-*.bin` model in the release; reject Python runtimes; and retain existing Zoom SDK/API-key checks.

Run:

```powershell
pwsh -File eng/Verify-Release.ps1 -ReleaseDirectory D:\ZoomRecorder\outputs\ZoomRecorder-0.3.0
```

Expected: FAIL listing missing local transcription worker assets.

- [ ] **Step 2: Implement the reproducible worker build**

`eng/whisper.cpp.version` contains exactly:

```text
tag=v1.9.1
commit=f049fff
```

The script clones/fetches into an ignored dependency cache, verifies `git rev-parse HEAD` begins with `f049fff`, configures separate x64 Release builds with `WHISPER_BUILD_EXAMPLES=ON`, `WHISPER_BUILD_TESTS=OFF`, and `GGML_VULKAN=ON/OFF`, then copies only runtime artifacts and licenses into a caller-supplied staging directory. It fails if the worker reports a different version or required DLL discovery is incomplete.

- [ ] **Step 3: Build both workers and run smoke probes**

Run:

```powershell
pwsh -File eng/Build-WhisperWorkers.ps1 -OutputDirectory D:\ZoomRecorder\work\whisper-workers
D:\ZoomRecorder\work\whisper-workers\vulkan\whisper-cli.exe --help
D:\ZoomRecorder\work\whisper-workers\cpu\whisper-cli.exe --help
```

Expected: both exit successfully and advertise `--output-json-full`, `--output-file`, `--language`, and `--no-prints`.

- [ ] **Step 4: Copy workers during Release packaging**

Add explicit MSBuild content items or the existing release staging script so worker directories and manifest are copied without placing the model in `bin/`, source control, or the release.

- [ ] **Step 5: Run verifier and commit scripts/config only**

Build the app and stage `outputs/ZoomRecorder-0.4.0`, then run:

```powershell
pwsh -File eng/Verify-Release.ps1 -ReleaseDirectory D:\ZoomRecorder\outputs\ZoomRecorder-0.4.0
```

Expected: `Release verification passed.`

```powershell
git add eng/Build-WhisperWorkers.ps1 eng/whisper.cpp.version eng/Verify-Release.ps1 src/ZoomRecorder.App/ZoomRecorder.App.csproj
git commit -m "build: package pinned local whisper workers"
```

---

### Task 8: Verify recovery, privacy, full build, and real local transcription

**Files:**
- Modify: `tests/ZoomRecorder.Core.Tests/Processing/ProcessingCoordinatorTests.cs`
- Modify: `tests/ZoomRecorder.App.Tests/Composition/AppServicesTests.cs`
- Modify: `eng/Verify-Release.ps1` only if a verification gap is demonstrated by a failing test.
- Modify: `Launch Zoom Recorder.cmd`
- Create: `README.md`

**Interfaces:**
- Consumes: complete local transcript pipeline.
- Produces: verified `outputs/ZoomRecorder-0.4.0` package and root launcher target.

- [ ] **Step 1: Add final regression tests**

Cover an existing `NeedsAttention/Transcribing` cloud-era job resuming locally from its M4A checkpoint; a post-transcription cloud-era job with a verified transcript completing without cloud calls; cached-model offline processing; and MP4 preservation after success, failure, and cancellation.

- [ ] **Step 2: Run all automated verification**

Run sequentially to avoid the existing one-second parallel timeout flakes:

```powershell
dotnet test tests/ZoomRecorder.Core.Tests/ZoomRecorder.Core.Tests.csproj
dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj
cmake --build build-release --config Release
ctest --test-dir build-release -C Release --output-on-failure
dotnet build src/ZoomRecorder.App/ZoomRecorder.App.csproj -c Release -p:Platform=x64
pwsh -File eng/Verify-Release.ps1 -ReleaseDirectory D:\ZoomRecorder\outputs\ZoomRecorder-0.4.0
```

Expected: zero test failures, successful native/app Release builds, and release verification passed.

- [ ] **Step 3: Update the root launcher and docs**

Point `Launch Zoom Recorder.cmd` at `outputs\ZoomRecorder-0.4.0`. Document that transcription is manual, English-only, downloads approximately 500 MB once, remains offline afterward, uses GPU with CPU fallback, never requires an OpenAI key, and never deletes the MP4 in transcript-only mode.

- [ ] **Step 4: Perform the real recording verification**

Launch `0.4.0`; transcribe the existing short recording with no model present; observe verified download; confirm GPU or labeled CPU fallback; open/edit/save/reopen transcript text; inspect the committed transcript JSON for segment timestamps; disconnect networking and transcribe a second recording from the cached model; confirm both MP4s still exist; and confirm no new OpenAI usage or credential read occurs.

- [ ] **Step 5: Commit final verification/docs**

```powershell
git add tests eng/Verify-Release.ps1 'Launch Zoom Recorder.cmd' README.md
git commit -m "test: verify offline transcript-only release"
```

- [ ] **Step 6: Request final code review**

Use `superpowers:requesting-code-review` against the complete diff from `c0f9dbc` to `HEAD`, resolve any Critical or Important findings with `superpowers:receiving-code-review`, rerun the full verification commands, and only then present the packaged app for user testing.
