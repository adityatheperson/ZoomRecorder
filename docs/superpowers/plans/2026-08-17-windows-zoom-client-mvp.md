# Windows Zoom Client MVP Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows desktop application that guest-joins a Zoom meeting, requires recording to start before entry, shows persistent recording health, and saves a local MP4 containing only the embedded meeting region plus meeting and microphone audio.

**Architecture:** A C# WinUI 3 application owns navigation and presentation while a platform-neutral C# core owns validation and lifecycle state. A narrow C ABI separates the managed app from a C++ native component that owns Zoom Meeting SDK hosting and Windows media capture/encoding. Development uses deterministic fake adapters until the separately supplied Zoom SDK is available, but release builds cannot substitute fakes.

**Tech Stack:** C# 12, .NET 8, WinUI 3 / Windows App SDK 1.7, xUnit, C++20, CMake 3.28+, Windows Graphics Capture, WASAPI, Media Foundation, Zoom Meeting SDK for Windows.

## Global Constraints

- Target Windows 11 x64 for the first version.
- Guest entry only; never request or store Zoom account credentials.
- Accept a Zoom link or meeting ID, a conditional passcode, and a display name.
- Block meeting entry unless meeting-region video, meeting audio, microphone audio, encoder, destination folder, and output file are all ready.
- Capture only the embedded Zoom meeting client area; exclude application chrome and the rest of the desktop.
- Save one local MP4 in `%USERPROFILE%\Videos\Meeting Recordings` with mixed meeting and microphone audio.
- Show a slim, persistent recording-status strip while the meeting is active.
- Finalize automatically when the meeting ends and show filename, duration, file size, Open recording, Open folder, and Done.
- Never present a healthy recording state after any required capture or storage component fails.
- Release configuration must fail clearly when the Zoom SDK package or SDK credentials are absent; it must never ship the fake adapter.

## File Structure

- `ZoomRecorder.sln` — solution entry point.
- `Directory.Build.props` — shared target framework, language, analyzers, and warnings policy.
- `src/ZoomRecorder.Core/` — platform-neutral meeting parsing, state machine, storage naming, and ports.
- `src/ZoomRecorder.App/` — WinUI 3 views, view models, navigation, and native adapter composition.
- `src/ZoomRecorder.Native/` — C++ Zoom host, capture sources, mixer, encoder, and C ABI.
- `tests/ZoomRecorder.Core.Tests/` — deterministic unit tests for core behavior.
- `tests/ZoomRecorder.App.Tests/` — view-model tests using fake ports.
- `tests/ZoomRecorder.Native.Tests/` — native lifecycle and media-pipeline tests using synthetic sources.
- `eng/Verify-Prerequisites.ps1` — reports required local toolchain, SDK, and credential inputs.

---

### Task 1: Toolchain Gate and Solution Skeleton

**Files:**
- Create: `eng/Verify-Prerequisites.ps1`
- Create: `Directory.Build.props`
- Create: `ZoomRecorder.sln`
- Create: `src/ZoomRecorder.Core/ZoomRecorder.Core.csproj`
- Create: `tests/ZoomRecorder.Core.Tests/ZoomRecorder.Core.Tests.csproj`
- Create: `tests/ZoomRecorder.Core.Tests/SmokeTests.cs`

**Interfaces:**
- Consumes: Visual Studio 2022 Build Tools with Windows 11 SDK, .NET 8 SDK, Windows App SDK 1.7 workload, CMake 3.28+, and `ZOOM_MEETING_SDK_DIR` supplied by the developer.
- Produces: A buildable solution and a prerequisite script that exits nonzero with a concrete list of missing release dependencies.

- [ ] **Step 1: Write the prerequisite contract test as script assertions**

```powershell
$requiredCommands = @('dotnet', 'cmake')
$missing = @($requiredCommands | Where-Object { -not (Get-Command $_ -ErrorAction SilentlyContinue) })
$sdkMissing = -not $env:ZOOM_MEETING_SDK_DIR -or -not (Test-Path -LiteralPath $env:ZOOM_MEETING_SDK_DIR)
if ($missing.Count -gt 0 -or $sdkMissing) {
    Write-Error ("Missing prerequisites: " + (($missing + @($(if ($sdkMissing) { 'ZOOM_MEETING_SDK_DIR' }))) -join ', '))
    exit 1
}
```

- [ ] **Step 2: Run the prerequisite check and record the expected current failure**

Run: `pwsh -File eng/Verify-Prerequisites.ps1`

Expected on the current machine: FAIL listing `dotnet`, `cmake` if absent, and `ZOOM_MEETING_SDK_DIR`. This failure is a dependency gate, not a product-test failure.

- [ ] **Step 3: Create the solution and test projects**

```powershell
dotnet new sln -n ZoomRecorder
dotnet new classlib -n ZoomRecorder.Core -o src/ZoomRecorder.Core -f net8.0
dotnet new xunit -n ZoomRecorder.Core.Tests -o tests/ZoomRecorder.Core.Tests -f net8.0
dotnet sln ZoomRecorder.sln add src/ZoomRecorder.Core/ZoomRecorder.Core.csproj tests/ZoomRecorder.Core.Tests/ZoomRecorder.Core.Tests.csproj
dotnet add tests/ZoomRecorder.Core.Tests/ZoomRecorder.Core.Tests.csproj reference src/ZoomRecorder.Core/ZoomRecorder.Core.csproj
```

- [ ] **Step 4: Add strict shared build settings**

```xml
<Project>
  <PropertyGroup>
    <LangVersion>12</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
  </PropertyGroup>
</Project>
```

- [ ] **Step 5: Verify and commit**

Run: `dotnet test ZoomRecorder.sln`

Expected: PASS after the .NET toolchain is installed.

```powershell
git add eng Directory.Build.props ZoomRecorder.sln src/ZoomRecorder.Core tests/ZoomRecorder.Core.Tests
git commit -m "build: scaffold Zoom recorder solution"
```

### Task 2: Meeting Input and Output Naming Core

**Files:**
- Create: `src/ZoomRecorder.Core/Meetings/MeetingJoinRequest.cs`
- Create: `src/ZoomRecorder.Core/Meetings/MeetingInputParser.cs`
- Create: `src/ZoomRecorder.Core/Storage/RecordingPathFactory.cs`
- Create: `tests/ZoomRecorder.Core.Tests/Meetings/MeetingInputParserTests.cs`
- Create: `tests/ZoomRecorder.Core.Tests/Storage/RecordingPathFactoryTests.cs`

**Interfaces:**
- Consumes: Raw meeting input, optional passcode, and display name.
- Produces: `MeetingJoinRequest Parse(string input, string? passcode, string displayName)` and `string Create(string directory, string? meetingLabel, DateTimeOffset startedAt, Func<string,bool> exists)`.

- [ ] **Step 1: Write failing parser tests**

```csharp
[Theory]
[InlineData("123 456 7890", "1234567890")]
[InlineData("https://zoom.us/j/1234567890?pwd=abc", "1234567890")]
public void Parse_extracts_normalized_meeting_id(string input, string expected)
{
    var result = MeetingInputParser.Parse(input, null, "Aditya");
    Assert.Equal(expected, result.MeetingId);
}

[Fact]
public void Parse_rejects_blank_display_name() =>
    Assert.Throws<MeetingInputException>(() => MeetingInputParser.Parse("1234567890", null, " "));
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/ZoomRecorder.Core.Tests --filter MeetingInputParserTests`

Expected: FAIL because the parser types do not exist.

- [ ] **Step 3: Implement the immutable request and parser**

```csharp
public sealed record MeetingJoinRequest(string MeetingId, string? Passcode, string DisplayName);

public static class MeetingInputParser
{
    public static MeetingJoinRequest Parse(string input, string? passcode, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) throw new MeetingInputException("Enter a display name.");
        var meetingId = ExtractMeetingId(input);
        if (meetingId.Length is < 9 or > 11) throw new MeetingInputException("Enter a valid Zoom link or meeting ID.");
        return new(meetingId, string.IsNullOrWhiteSpace(passcode) ? null : passcode, displayName.Trim());
    }
}
```

- [ ] **Step 4: Write failing filename collision and sanitization tests**

```csharp
[Fact]
public void Create_sanitizes_label_and_adds_collision_suffix()
{
    var path = RecordingPathFactory.Create("C:\\Videos", "Team: Sync", new(2026,8,17,9,30,0,TimeSpan.Zero), p => p.EndsWith(".mp4"));
    Assert.EndsWith("Team_ Sync - 2026-08-17 093000 (2).mp4", path);
}
```

- [ ] **Step 5: Implement naming, run tests, and commit**

Run: `dotnet test tests/ZoomRecorder.Core.Tests`

Expected: PASS.

```powershell
git add src/ZoomRecorder.Core/Meetings src/ZoomRecorder.Core/Storage tests/ZoomRecorder.Core.Tests
git commit -m "feat: parse meeting details and name recordings"
```

### Task 3: Recording-First Lifecycle State Machine

**Files:**
- Create: `src/ZoomRecorder.Core/Lifecycle/AppState.cs`
- Create: `src/ZoomRecorder.Core/Lifecycle/AppEvent.cs`
- Create: `src/ZoomRecorder.Core/Lifecycle/MeetingLifecycle.cs`
- Create: `tests/ZoomRecorder.Core.Tests/Lifecycle/MeetingLifecycleTests.cs`

**Interfaces:**
- Consumes: typed `AppEvent` values such as `JoinRequested`, `MeetingPrepared`, `RecordingStarted`, `MeetingEnded`, `RecordingFinalized`, and `RequiredComponentFailed`.
- Produces: `AppState Current`, `AppState Apply(AppEvent appEvent)`, and rejects illegal transitions with `InvalidStateTransitionException`.

- [ ] **Step 1: Write the failing happy-path test**

```csharp
[Fact]
public void Meeting_entry_requires_recording_started()
{
    var lifecycle = new MeetingLifecycle();
    lifecycle.Apply(new JoinRequested(Request));
    lifecycle.Apply(new MeetingPrepared());
    Assert.Throws<InvalidStateTransitionException>(() => lifecycle.Apply(new MeetingEntered()));
    lifecycle.Apply(new RecordingStarted());
    Assert.Equal(AppState.InMeetingRecording, lifecycle.Apply(new MeetingEntered()));
}
```

- [ ] **Step 2: Write the failing failure-path test**

```csharp
[Fact]
public void Required_component_failure_never_leaves_recording_healthy()
{
    var lifecycle = InMeetingLifecycle();
    var state = lifecycle.Apply(new RequiredComponentFailed("Microphone unavailable"));
    Assert.Equal(AppState.RecoverableError, state);
}
```

- [ ] **Step 3: Run the focused tests and verify failure**

Run: `dotnet test tests/ZoomRecorder.Core.Tests --filter MeetingLifecycleTests`

Expected: FAIL because lifecycle types do not exist.

- [ ] **Step 4: Implement an explicit transition table**

```csharp
private static readonly IReadOnlyDictionary<(AppState, Type), AppState> Transitions =
    new Dictionary<(AppState, Type), AppState>
    {
        [(AppState.ReadyToJoin, typeof(JoinRequested))] = AppState.PreparingMeeting,
        [(AppState.PreparingMeeting, typeof(MeetingPrepared))] = AppState.StartingRecording,
        [(AppState.StartingRecording, typeof(RecordingStarted))] = AppState.RecordingReady,
        [(AppState.RecordingReady, typeof(MeetingEntered))] = AppState.InMeetingRecording,
        [(AppState.InMeetingRecording, typeof(MeetingEnded))] = AppState.FinalizingRecording,
        [(AppState.FinalizingRecording, typeof(RecordingFinalized))] = AppState.RecordingComplete
    };
```

- [ ] **Step 5: Run all core tests and commit**

Run: `dotnet test tests/ZoomRecorder.Core.Tests`

Expected: PASS.

```powershell
git add src/ZoomRecorder.Core/Lifecycle tests/ZoomRecorder.Core.Tests/Lifecycle
git commit -m "feat: enforce recording-first meeting lifecycle"
```

### Task 4: Managed Ports and Orchestrator

**Files:**
- Create: `src/ZoomRecorder.Core/Ports/IMeetingClient.cs`
- Create: `src/ZoomRecorder.Core/Ports/IRecordingSession.cs`
- Create: `src/ZoomRecorder.Core/Ports/IRecordingStore.cs`
- Create: `src/ZoomRecorder.Core/Orchestration/MeetingOrchestrator.cs`
- Create: `tests/ZoomRecorder.Core.Tests/Orchestration/MeetingOrchestratorTests.cs`

**Interfaces:**
- Consumes: `IMeetingClient.PrepareAsync`, `IRecordingSession.StartAsync`, `IMeetingClient.EnterAsync`, and adapter events.
- Produces: `Task JoinAndRecordAsync(MeetingJoinRequest request, CancellationToken cancellationToken)`, `IObservable<MeetingStatus> Status`, and `RecordingResult(string Path, TimeSpan Duration, long ByteSize)` after finalization.

- [ ] **Step 1: Define ports and write a failing order test**

```csharp
[Fact]
public async Task Join_starts_recording_before_entering_meeting()
{
    var calls = new List<string>();
    var meeting = new FakeMeetingClient(calls);
    var recording = new FakeRecordingSession(calls);
    await new MeetingOrchestrator(meeting, recording, Store, Lifecycle).JoinAndRecordAsync(Request, default);
    Assert.Equal(new[] { "prepare", "record", "enter" }, calls);
}
```

- [ ] **Step 2: Write a failing blocked-entry test**

```csharp
[Fact]
public async Task Recording_failure_prevents_enter()
{
    var meeting = new FakeMeetingClient();
    var recording = new FailingRecordingSession("Low disk space");
    await Assert.ThrowsAsync<RecordingStartException>(() => Sut(meeting, recording).JoinAndRecordAsync(Request, default));
    Assert.Equal(0, meeting.EnterCount);
}
```

- [ ] **Step 3: Run focused tests and verify failure**

Run: `dotnet test tests/ZoomRecorder.Core.Tests --filter MeetingOrchestratorTests`

Expected: FAIL because ports and orchestrator do not exist.

- [ ] **Step 4: Implement orchestration with compensation**

```csharp
await meeting.PrepareAsync(request, cancellationToken);
try
{
    await recording.StartAsync(cancellationToken);
    lifecycle.Apply(new RecordingStarted());
    await meeting.EnterAsync(cancellationToken);
}
catch
{
    await recording.StopAndFinalizeIfStartedAsync(CancellationToken.None);
    await meeting.CancelPreparedMeetingAsync(CancellationToken.None);
    throw;
}
```

- [ ] **Step 5: Test, verify call order and compensation, then commit**

Run: `dotnet test tests/ZoomRecorder.Core.Tests`

Expected: PASS.

```powershell
git add src/ZoomRecorder.Core/Ports src/ZoomRecorder.Core/Orchestration tests/ZoomRecorder.Core.Tests/Orchestration
git commit -m "feat: orchestrate recording-gated meeting entry"
```

### Task 5: WinUI Application Shell and View Models

**Files:**
- Create: `src/ZoomRecorder.App/ZoomRecorder.App.csproj`
- Create: `src/ZoomRecorder.App/App.xaml`
- Create: `src/ZoomRecorder.App/MainWindow.xaml`
- Create: `src/ZoomRecorder.App/Views/JoinPage.xaml`
- Create: `src/ZoomRecorder.App/Views/MeetingPage.xaml`
- Create: `src/ZoomRecorder.App/Views/CompletionPage.xaml`
- Create: `src/ZoomRecorder.App/ViewModels/JoinViewModel.cs`
- Create: `src/ZoomRecorder.App/ViewModels/MeetingViewModel.cs`
- Create: `src/ZoomRecorder.App/ViewModels/CompletionViewModel.cs`
- Create: `tests/ZoomRecorder.App.Tests/JoinViewModelTests.cs`
- Create: `tests/ZoomRecorder.App.Tests/MeetingViewModelTests.cs`

**Interfaces:**
- Consumes: `MeetingOrchestrator`, `MeetingStatus`, and `RecordingResult` from the core project.
- Produces: bindable join fields, `JoinAndRecordCommand`, recording elapsed/health properties, and completion actions.

- [ ] **Step 1: Write failing join view-model tests**

```csharp
[Fact]
public async Task Join_command_surfaces_recording_gate_error_and_stays_on_join_page()
{
    var vm = new JoinViewModel(new FailingOrchestrator("Microphone unavailable"), Navigator);
    vm.MeetingInput = "1234567890"; vm.DisplayName = "Aditya";
    await vm.JoinAndRecordCommand.ExecuteAsync(null);
    Assert.Equal("Microphone unavailable", vm.ErrorMessage);
    Assert.Equal(0, Navigator.MeetingNavigationCount);
}
```

- [ ] **Step 2: Write failing status-strip tests**

```csharp
[Fact]
public void Required_failure_sets_error_visual_state()
{
    var vm = new MeetingViewModel(StatusSource);
    StatusSource.Publish(MeetingStatus.Failed("Meeting audio stopped"));
    Assert.True(vm.HasRecordingError);
    Assert.False(vm.IsRecordingHealthy);
}
```

- [ ] **Step 3: Run tests and verify failure**

Run: `dotnet test tests/ZoomRecorder.App.Tests`

Expected: FAIL because the application view models do not exist.

- [ ] **Step 4: Implement view models and restrained WinUI pages**

The meeting page must use a two-row grid: `*` for the native Zoom host surface and `40` device-independent pixels for the status strip. The status strip binds its indicator, elapsed time, meeting-audio health, microphone health, and save health to `MeetingViewModel`; it must not overlap the capture host.

The join page must state that joining starts a local recording and that the user is responsible for any notice or consent required by Zoom policy and applicable law. This notice is visible before **Join and record** and contains no pre-checked consent claim on behalf of other participants.

- [ ] **Step 5: Add an explicitly debug-only simulated adapter**

```csharp
#if DEBUG
services.AddSingleton<IMeetingClient, SimulatedMeetingClient>();
services.AddSingleton<IRecordingSession, SimulatedRecordingSession>();
#else
services.AddSingleton<IMeetingClient, NativeMeetingClient>();
services.AddSingleton<IRecordingSession, NativeRecordingSession>();
#endif
```

Add an MSBuild release check that errors if `UseSimulatedAdapters=true` under `Release`.

- [ ] **Step 6: Test the view models, build Debug x64, and commit**

Run: `dotnet test tests/ZoomRecorder.App.Tests`

Run: `dotnet build src/ZoomRecorder.App/ZoomRecorder.App.csproj -c Debug -p:Platform=x64`

Expected: PASS and a launchable shell showing Join, in-meeting status, and Completion states with simulated adapters.

```powershell
git add src/ZoomRecorder.App tests/ZoomRecorder.App.Tests ZoomRecorder.sln
git commit -m "feat: add WinUI meeting recorder shell"
```

### Task 6: Native ABI and Managed Interop

**Files:**
- Create: `src/ZoomRecorder.Native/include/zoom_recorder.h`
- Create: `src/ZoomRecorder.Native/src/api.cpp`
- Create: `src/ZoomRecorder.Native/CMakeLists.txt`
- Create: `src/ZoomRecorder.App/Interop/NativeMethods.cs`
- Create: `src/ZoomRecorder.App/Interop/NativeMeetingClient.cs`
- Create: `src/ZoomRecorder.App/Interop/NativeRecordingSession.cs`
- Create: `tests/ZoomRecorder.Native.Tests/api_tests.cpp`

**Interfaces:**
- Consumes: UTF-8 JSON command payloads and an event callback supplied by managed code.
- Produces: `zr_create`, `zr_destroy`, `zr_prepare_meeting`, `zr_start_recording`, `zr_enter_meeting`, `zr_finalize_recording`, and `zr_set_event_callback`; every function returns a stable `zr_result` code.

- [ ] **Step 1: Write failing native ownership tests**

```cpp
TEST(Api, DestroyIsSafeAfterPartialInitialization) {
  zr_handle handle{};
  ASSERT_EQ(ZR_OK, zr_create(&handle));
  EXPECT_EQ(ZR_OK, zr_destroy(handle));
}
```

- [ ] **Step 2: Define the ABI with explicit ownership**

```cpp
extern "C" {
typedef void* zr_handle;
typedef void(__stdcall *zr_event_callback)(const char* json, void* context);
ZR_API zr_result zr_create(zr_handle* out_handle);
ZR_API zr_result zr_destroy(zr_handle handle);
ZR_API zr_result zr_prepare_meeting(zr_handle handle, const char* request_json);
ZR_API zr_result zr_start_recording(zr_handle handle, const wchar_t* output_path);
ZR_API zr_result zr_enter_meeting(zr_handle handle);
ZR_API zr_result zr_finalize_recording(zr_handle handle);
}
```

- [ ] **Step 3: Implement managed `LibraryImport` declarations and callback lifetime**

```csharp
[LibraryImport("ZoomRecorder.Native", StringMarshalling = StringMarshalling.Utf8)]
internal static partial ZrResult zr_prepare_meeting(SafeZrHandle handle, string requestJson);
```

Keep the callback delegate rooted for the lifetime of `SafeZrHandle`; marshal every native event onto the WinUI dispatcher before updating UI state.

- [ ] **Step 4: Run native and managed interop tests**

Run: `cmake -S src/ZoomRecorder.Native -B artifacts/native -A x64`

Run: `cmake --build artifacts/native --config Debug`

Run: `ctest --test-dir artifacts/native -C Debug --output-on-failure`

Expected: PASS without loading the Zoom SDK or real media devices.

- [ ] **Step 5: Commit**

```powershell
git add src/ZoomRecorder.Native src/ZoomRecorder.App/Interop tests/ZoomRecorder.Native.Tests
git commit -m "feat: define native meeting and recording boundary"
```

### Task 7: Zoom Standard-UI Host Adapter

**Files:**
- Create: `src/ZoomRecorder.Native/src/zoom/zoom_meeting_client.h`
- Create: `src/ZoomRecorder.Native/src/zoom/zoom_meeting_client.cpp`
- Create: `src/ZoomRecorder.Native/src/zoom/zoom_event_mapper.cpp`
- Create: `tests/ZoomRecorder.Native.Tests/zoom_event_mapper_tests.cpp`
- Modify: `src/ZoomRecorder.Native/CMakeLists.txt`

**Interfaces:**
- Consumes: normalized meeting ID, optional passcode, display name, and the WinUI host child-window handle.
- Produces: prepared, entered, passcode-required, ended, and failed events through the native event callback.

- [ ] **Step 1: Add a configure-time Zoom SDK gate**

```cmake
if(NOT DEFINED ENV{ZOOM_MEETING_SDK_DIR})
  message(FATAL_ERROR "ZOOM_MEETING_SDK_DIR must point to the licensed Zoom Meeting SDK for Windows")
endif()
```

- [ ] **Step 2: Write event-mapping tests against local adapter enums**

```cpp
TEST(ZoomEventMapper, MeetingEndedMapsExactlyOnce) {
  ZoomEventMapper mapper;
  EXPECT_EQ(AppEvent::MeetingEnded, mapper.map(ZoomMeetingStatus::Ended));
  EXPECT_EQ(AppEvent::IgnoredDuplicate, mapper.map(ZoomMeetingStatus::Ended));
}
```

- [ ] **Step 3: Implement SDK initialization and standard-UI hosting**

Initialize the Zoom SDK once per process, select standard meeting UI, attach the SDK meeting window as a child of the WinUI host HWND, and resize it only to the host client rectangle. Do not expose SDK objects across the ABI.

- [ ] **Step 4: Implement guest join and lifecycle mapping**

Pass the display name, meeting number, and passcode to the SDK guest-join API. Map SDK errors into stable application errors without logging the passcode. Emit `MeetingEnded` exactly once for normal leave, host-ended meeting, disconnect, or SDK shutdown.

- [ ] **Step 5: Build with the supplied SDK and run mapper tests**

Run: `cmake -S src/ZoomRecorder.Native -B artifacts/native-release -A x64 -DZR_WITH_ZOOM=ON`

Run: `cmake --build artifacts/native-release --config Release`

Expected: PASS only when the licensed SDK is present and its redistributable files resolve.

- [ ] **Step 6: Commit**

```powershell
git add src/ZoomRecorder.Native/src/zoom src/ZoomRecorder.Native/CMakeLists.txt tests/ZoomRecorder.Native.Tests
git commit -m "feat: host Zoom standard meeting interface"
```

### Task 8: Meeting-Region Video and Dual-Source Audio Pipeline

**Files:**
- Create: `src/ZoomRecorder.Native/src/media/meeting_region_source.*`
- Create: `src/ZoomRecorder.Native/src/media/wasapi_loopback_source.*`
- Create: `src/ZoomRecorder.Native/src/media/microphone_source.*`
- Create: `src/ZoomRecorder.Native/src/media/audio_mixer.*`
- Create: `src/ZoomRecorder.Native/src/media/mp4_writer.*`
- Create: `src/ZoomRecorder.Native/src/media/recording_pipeline.*`
- Create: `tests/ZoomRecorder.Native.Tests/audio_mixer_tests.cpp`
- Create: `tests/ZoomRecorder.Native.Tests/recording_pipeline_tests.cpp`

**Interfaces:**
- Consumes: the Zoom host HWND, selected render/capture device IDs, output path, and stop token.
- Produces: timestamped BGRA/NV12 video frames, float PCM audio, health events, and a finalized H.264/AAC MP4.

- [ ] **Step 1: Write synthetic synchronization tests**

```cpp
TEST(AudioMixer, AlignsSourcesAndInsertsSilenceForShortGap) {
  AudioMixer mixer({48000, 2});
  mixer.push_meeting(make_tone(0ms, 100ms));
  mixer.push_microphone(make_tone(20ms, 80ms));
  auto mixed = mixer.take(0ms, 100ms);
  EXPECT_EQ(4800u, mixed.frames());
  EXPECT_TRUE(mixed.has_signal_between(20ms, 80ms));
}
```

- [ ] **Step 2: Implement capture-source readiness and health contracts**

Each source implements `start()`, `stop()`, `is_ready()`, and a monotonic timestamped callback. Readiness is not reported until the first usable sample arrives. Device invalidation emits a required-component failure immediately.

- [ ] **Step 3: Implement meeting-region capture**

Use Windows Graphics Capture targeting the Zoom host HWND. Crop to its client rectangle and update the capture size on host resize. Never target a monitor, the main application HWND, or the desktop.

- [ ] **Step 4: Implement WASAPI sources and mixer**

Use shared-mode loopback for meeting playback and shared-mode capture for the selected microphone. Convert both to 48 kHz stereo float PCM, align by monotonic timestamps, apply bounded gain and clipping protection, and emit silence only for sub-250 ms jitter gaps. Longer gaps are failures.

- [ ] **Step 5: Implement Media Foundation MP4 writing and idempotent finalization**

Encode H.264 video and AAC audio, preferring a hardware MFT and falling back to a system software MFT. Write to a `.partial` file, finalize the sink writer once, atomically rename to `.mp4`, and return duration and byte size.

- [ ] **Step 6: Run synthetic native tests and inspect a generated fixture**

Run: `ctest --test-dir artifacts/native -C Debug --output-on-failure`

Expected: PASS; `artifacts/test-output/synthetic.mp4` is playable, has one video and one audio track, and finalization called twice returns the original successful result.

- [ ] **Step 7: Commit**

```powershell
git add src/ZoomRecorder.Native/src/media tests/ZoomRecorder.Native.Tests
git commit -m "feat: record meeting region with mixed audio"
```

### Task 9: Storage Recovery, Completion Actions, and Privacy-Safe Logging

**Files:**
- Create: `src/ZoomRecorder.Core/Storage/RecordingRecovery.cs`
- Create: `src/ZoomRecorder.App/Services/LocalFileActions.cs`
- Create: `src/ZoomRecorder.App/Services/PrivacySafeLogger.cs`
- Create: `src/ZoomRecorder.App/Views/RecoveryPage.xaml`
- Create: `tests/ZoomRecorder.Core.Tests/Storage/RecordingRecoveryTests.cs`
- Create: `tests/ZoomRecorder.App.Tests/PrivacySafeLoggerTests.cs`

**Interfaces:**
- Consumes: `.partial` recordings, native recovery results, completion paths, and structured log events.
- Produces: recoverable-recording prompts, safe shell-open operations, and redacted logs.

- [ ] **Step 1: Write recovery classification tests**

```csharp
[Theory]
[InlineData("meeting.partial", true)]
[InlineData("meeting.mp4", false)]
public void Detect_only_returns_partial_recordings(string name, bool expected) =>
    Assert.Equal(expected, RecordingRecovery.IsCandidate(name));
```

- [ ] **Step 2: Write passcode-redaction tests**

```csharp
[Fact]
public void Meeting_request_never_logs_passcode()
{
    var text = Logger.Serialize(new MeetingJoinRequest("1234567890", "secret", "Aditya"));
    Assert.DoesNotContain("secret", text);
}
```

- [ ] **Step 3: Implement recovery enumeration and explicit user choice**

At startup, enumerate only `.partial` files in the app-owned recording folder. Show filename, last-write time, and recoverability result; allow Finalize or Dismiss, but never delete automatically.

- [ ] **Step 4: Implement completion actions**

Use Windows shell APIs to open the finalized MP4 or select it in File Explorer. Validate that the canonical path remains under the configured recording folder before invoking the shell.

- [ ] **Step 5: Run tests and commit**

Run: `dotnet test ZoomRecorder.sln`

Expected: PASS.

```powershell
git add src/ZoomRecorder.Core/Storage src/ZoomRecorder.App/Services src/ZoomRecorder.App/Views/RecoveryPage.xaml tests
git commit -m "feat: recover recordings and protect private meeting data"
```

### Task 10: End-to-End Verification and Release Gate

**Files:**
- Create: `tests/ZoomRecorder.E2E/RecordingFlowTests.cs`
- Create: `tests/ZoomRecorder.E2E/Fixtures/TestMeetingHost.cs`
- Create: `docs/verification/windows-zoom-client-mvp.md`
- Create: `eng/Verify-Release.ps1`
- Modify: `ZoomRecorder.sln`

**Interfaces:**
- Consumes: a signed-in Zoom SDK test environment, two test audio sources, a meeting test account operated according to Zoom's terms, and a writable local recording folder.
- Produces: a release verification report and nonzero exit when fake adapters, missing native binaries, missing SDK redistributables, failed tests, or privacy-boundary checks are detected.

- [ ] **Step 1: Add a simulated end-to-end state-flow test**

```csharp
[Fact]
public async Task Join_record_end_finalize_reaches_completion()
{
    await App.EnterMeetingDetails("1234567890", "Aditya");
    await App.JoinAndRecord();
    await App.AssertStatusStripHealthy();
    await App.EndSimulatedMeeting();
    await App.AssertCompletionShowsMp4();
}
```

- [ ] **Step 2: Add release artifact checks**

```powershell
if (Select-String -Path artifacts/release/**/*.deps.json -Pattern 'SimulatedMeetingClient') {
    throw 'Release artifact contains simulated meeting adapter.'
}
if (-not (Test-Path artifacts/release/ZoomRecorder.Native.dll)) {
    throw 'Native recording component is missing.'
}
```

- [ ] **Step 3: Run the complete automated suite**

Run: `dotnet test ZoomRecorder.sln -c Release`

Run: `ctest --test-dir artifacts/native-release -C Release --output-on-failure`

Run: `pwsh -File eng/Verify-Release.ps1`

Expected: all commands PASS.

- [ ] **Step 4: Perform the documented real-meeting matrix**

Verify: link join; ID/passcode join; invalid passcode; low disk; unavailable microphone; unavailable loopback; device loss mid-meeting; host-ended meeting; local leave; window resize; meeting UI capture boundary; app chrome exclusion; surrounding desktop exclusion; mixed meeting/microphone audio; crash recovery; filename collision; Open recording; Open folder.

- [ ] **Step 5: Record evidence and commit**

Record app version, Windows build, Zoom SDK version, GPU/encoder, device matrix, each scenario result, and the inspected MP4 stream metadata in `docs/verification/windows-zoom-client-mvp.md`.

```powershell
git add tests/ZoomRecorder.E2E eng/Verify-Release.ps1 docs/verification ZoomRecorder.sln
git commit -m "test: verify Zoom recorder end to end"
```

## Dependency Checkpoint

Tasks 2–4 are portable .NET work. Task 5 requires the .NET 8 and WinUI 3 toolchain. Tasks 6 and 8 require the native Windows build toolchain. Task 7 and the real-meeting portion of Task 10 require the licensed Zoom Meeting SDK package, SDK credentials/signature inputs, and compliance with Zoom's current redistribution requirements. Missing proprietary inputs must remain a visible blocker; they must not be replaced by unofficial packages or mocked release behavior.
