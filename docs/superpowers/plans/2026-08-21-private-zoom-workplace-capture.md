# Private Zoom Workplace Capture Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace embedded Meeting SDK joining with automatic capture of meetings opened in the installed Zoom Workplace desktop app.

**Architecture:** Managed code constructs and shell-opens the Zoom link, discovers a stable Zoom meeting window through a testable Win32 enumeration boundary, and passes that HWND to a capture-only native recorder. Existing MP4 capture, completion, class assignment, processing, and study-library flows remain intact; the Meeting SDK, JWT credentials, and runtime payload are removed.

**Tech Stack:** C# 12/.NET 8, WinUI 3, Win32 P/Invoke, C++20, Windows Graphics Capture, WASAPI, Media Foundation, xUnit, CMake/CTest, PowerShell release verification.

**Spec:** `docs/superpowers/specs/2026-08-21-private-zoom-workplace-capture-design.md`

## Global Constraints

- Zoom Workplace is an external prerequisite and is never bundled.
- Meeting-window selection must use process identity, visibility, size, exclusions, and stability; title text alone is insufficient.
- Window discovery is cancelable and times out after exactly fifteen minutes.
- Capture never begins until one unambiguous stable meeting window is selected.
- Finalization remains exactly-once; failed or empty captures are not registered in the library.
- Existing Classes, transcription, summaries, study guides, and optional video deletion behavior must remain unchanged.
- Production and release builds must not read Zoom SDK credentials or contain Meeting SDK runtime files.

---

### Task 1: Meeting launch request and URL construction

**Files:**
- Modify: `src/ZoomRecorder.Core/Meetings/MeetingJoinRequest.cs`
- Modify: `src/ZoomRecorder.Core/Meetings/MeetingInputParser.cs`
- Create: `src/ZoomRecorder.Core/Meetings/ZoomMeetingLaunchUri.cs`
- Modify: `tests/ZoomRecorder.Core.Tests/Meetings/MeetingInputParserTests.cs`
- Create: `tests/ZoomRecorder.Core.Tests/Meetings/ZoomMeetingLaunchUriTests.cs`

**Interfaces:**
- Produces: `MeetingJoinRequest(string MeetingId, string? Passcode)`.
- Produces: `MeetingInputParser.Parse(string input, string? passcode)`.
- Produces: `ZoomMeetingLaunchUri.Create(MeetingJoinRequest request) : Uri`.

- [ ] **Step 1: Write failing parser tests that remove the SDK display-name requirement**

```csharp
[Fact]
public void Meeting_id_does_not_require_a_display_name()
{
    var request = MeetingInputParser.Parse("123 456 7890", " pass ");
    Assert.Equal("1234567890", request.MeetingId);
    Assert.Equal("pass", request.Passcode);
}
```

- [ ] **Step 2: Write failing launch-URI tests**

```csharp
[Theory]
[InlineData("1234567890", null, "https://zoom.us/j/1234567890")]
[InlineData("1234567890", "a b&c", "https://zoom.us/j/1234567890?pwd=a%20b%26c")]
public void Creates_https_join_uri(string id, string? passcode, string expected)
{
    Assert.Equal(expected, ZoomMeetingLaunchUri.Create(new(id, passcode)).AbsoluteUri);
}
```

- [ ] **Step 3: Run focused tests and verify RED**

Run: `dotnet test tests/ZoomRecorder.Core.Tests/ZoomRecorder.Core.Tests.csproj --filter "FullyQualifiedName~MeetingInputParserTests|FullyQualifiedName~ZoomMeetingLaunchUriTests"`

Expected: compile failures for the two-argument request/parser and missing `ZoomMeetingLaunchUri`.

- [ ] **Step 4: Implement the minimal immutable request and URI builder**

```csharp
public sealed record MeetingJoinRequest(string MeetingId, string? Passcode);

public static class ZoomMeetingLaunchUri
{
    public static Uri Create(MeetingJoinRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var suffix = string.IsNullOrWhiteSpace(request.Passcode)
            ? string.Empty
            : $"?pwd={Uri.EscapeDataString(request.Passcode)}";
        return new Uri($"https://zoom.us/j/{request.MeetingId}{suffix}", UriKind.Absolute);
    }
}
```

Update `MeetingInputParser.Parse` to accept only `input` and `passcode`, preserve embedded `pwd`, and return the two-field request.

- [ ] **Step 5: Run focused and full Core tests**

Run focused command from Step 3, then `dotnet test tests/ZoomRecorder.Core.Tests/ZoomRecorder.Core.Tests.csproj`.

Expected: all tests pass; update existing call sites/tests that construct the old three-field record without changing behavior beyond removal of display name.

- [ ] **Step 6: Commit**

```powershell
git add src/ZoomRecorder.Core/Meetings tests/ZoomRecorder.Core.Tests/Meetings
git commit -m "refactor: model external Zoom meeting launch"
```

---

### Task 2: Testable Zoom meeting-window discovery

**Files:**
- Create: `src/ZoomRecorder.App/ZoomClient/ZoomWindowDescription.cs`
- Create: `src/ZoomRecorder.App/ZoomClient/IZoomWindowEnumerator.cs`
- Create: `src/ZoomRecorder.App/ZoomClient/ZoomWindowSelection.cs`
- Create: `src/ZoomRecorder.App/ZoomClient/ZoomWindowDetector.cs`
- Create: `src/ZoomRecorder.App/ZoomClient/Win32ZoomWindowEnumerator.cs`
- Create: `tests/ZoomRecorder.App.Tests/ZoomClient/ZoomWindowSelectionTests.cs`
- Create: `tests/ZoomRecorder.App.Tests/ZoomClient/ZoomWindowDetectorTests.cs`

**Interfaces:**
- Produces: `ZoomWindowDescription(nint Handle, int ProcessId, string ProcessName, string ClassName, string Title, bool IsVisible, bool IsMinimized, int Width, int Height)`.
- Produces: `IZoomWindowEnumerator.Enumerate() : IReadOnlyList<ZoomWindowDescription>`.
- Produces: `ZoomWindowSelection.Select(IReadOnlyList<ZoomWindowDescription>) : ZoomWindowSelectionResult` with `None`, `Selected`, or `Ambiguous`.
- Produces: `ZoomWindowDetector.WaitForMeetingWindowAsync(TimeSpan timeout, CancellationToken cancellationToken) : Task<nint>`.

- [ ] **Step 1: Write failing selection-policy tests**

```csharp
[Fact]
public void Selects_one_large_visible_zoom_meeting_window()
{
    var home = Window((nint)1, "Zoom", "Zoom Workplace", 900, 700);
    var meeting = Window((nint)2, "Zoom", "Zoom Meeting", 1400, 900);
    var result = ZoomWindowSelection.Select([home, meeting]);
    Assert.Equal(ZoomWindowSelectionKind.Selected, result.Kind);
    Assert.Equal((nint)2, result.Handle);
}

[Fact]
public void Equal_meeting_candidates_are_ambiguous()
{
    var result = ZoomWindowSelection.Select([
        Window((nint)2, "Zoom", "Meeting", 1400, 900),
        Window((nint)3, "Zoom", "Meeting", 1400, 900)]);
    Assert.Equal(ZoomWindowSelectionKind.Ambiguous, result.Kind);
}
```

Also cover non-Zoom processes, invisible/minimized windows, widths below 640, heights below 360, and known home/settings/updater/authentication classes or titles.

- [ ] **Step 2: Run selection tests and verify RED**

Run: `dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj -p:Platform=x64 --filter FullyQualifiedName~ZoomWindowSelectionTests`

Expected: compile failure because the discovery types do not exist.

- [ ] **Step 3: Implement immutable descriptions and deterministic selection**

Implement a pure scoring policy. A candidate must be visible, not minimized, at least 640x360, and owned by `Zoom.exe` or `zTscoder.exe` only where the process/window role confirms the meeting UI. Explicitly exclude Zoom home/settings/updater/sign-in windows. Return `Ambiguous` when top candidates have equal evidence rather than guessing.

- [ ] **Step 4: Write failing stability, timeout, and cancellation tests**

Use a scripted fake enumerator and injected `TimeProvider`. Assert that the same handle must be selected on three consecutive 250 ms observations, ambiguity throws `ZoomWindowAmbiguousException`, timeout throws `ZoomWindowTimeoutException`, and caller cancellation propagates `OperationCanceledException`.

- [ ] **Step 5: Run detector tests and verify RED**

Run: `dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj -p:Platform=x64 --filter FullyQualifiedName~ZoomWindowDetectorTests`

Expected: missing detector and typed exceptions.

- [ ] **Step 6: Implement detector and Win32 enumerator**

Use `EnumWindows`, `GetWindowThreadProcessId`, `IsWindowVisible`, `IsIconic`, `GetClientRect`, `GetClassNameW`, and `GetWindowTextW`. Open the process only long enough to obtain its executable name and always release handles. `WaitForMeetingWindowAsync` polls every 250 ms via `Task.Delay(TimeSpan.FromMilliseconds(250), timeProvider, cancellationToken)`, requires three stable observations, and honors the supplied timeout and cancellation token.

- [ ] **Step 7: Run focused App tests and commit**

Run both focused filters, then:

```powershell
git add src/ZoomRecorder.App/ZoomClient tests/ZoomRecorder.App.Tests/ZoomClient
git commit -m "feat: detect stable Zoom Workplace meeting windows"
```

---

### Task 3: Capture-only native ABI

**Files:**
- Modify: `src/ZoomRecorder.Native/include/zoom_recorder.h`
- Modify: `src/ZoomRecorder.Native/src/api.cpp`
- Modify: `src/ZoomRecorder.Native/CMakeLists.txt`
- Delete: `src/ZoomRecorder.Native/src/zoom/zoom_meeting_client.cpp`
- Delete: `src/ZoomRecorder.Native/src/zoom/zoom_meeting_client.h`
- Delete: `src/ZoomRecorder.Native/src/zoom/zoom_event_mapper.cpp`
- Delete: `src/ZoomRecorder.Native/src/zoom/zoom_event_mapper.h`
- Modify: `tests/ZoomRecorder.Native.Tests/api_tests.cpp`
- Delete: `tests/ZoomRecorder.Native.Tests/zoom_event_mapper_tests.cpp`

**Interfaces:**
- Removes: `zr_set_meeting_host`, `zr_prepare_meeting`, and `zr_enter_meeting`.
- Changes: `zr_start_recording(zr_handle handle, const wchar_t* output_path, intptr_t meeting_window)`.
- Preserves: event callback, exactly-once `zr_finalize_recording`, audio chunk preparation ABI.

- [ ] **Step 1: Rewrite native API tests first**

```cpp
zr_handle handle{};
if (zr_create(&handle) != ZR_OK || !handle) return EXIT_FAILURE;
if (zr_start_recording(handle, L"test.mp4", 0) != ZR_INVALID_ARGUMENT) return EXIT_FAILURE;
if (zr_finalize_recording(handle) != ZR_OK) return EXIT_FAILURE;
return zr_destroy(handle) == ZR_OK ? EXIT_SUCCESS : EXIT_FAILURE;
```

Add a test-only fake-window seam around `IsWindow` so a valid fake HWND can assert that the pipeline receives the exact handle without invoking live capture.

- [ ] **Step 2: Configure/build and verify RED**

Run:

```powershell
cmake -S src/ZoomRecorder.Native -B work/native-external-tests -G "Visual Studio 17 2022" -A x64
cmake --build work/native-external-tests --config Release
```

Expected: compile failure because `zr_start_recording` still has two parameters and obsolete symbols remain.

- [ ] **Step 3: Implement minimal capture-only session state**

Remove `prepared`, `entered`, host-window, Zoom client, and all `ZR_WITH_ZOOM` branches. Validate `output_path`, `meeting_window`, and `IsWindow`; call `pipeline->start(output_path)` followed by `pipeline->attach_video(HWND)`. If attach fails, finalize/stop the pipeline and return `ZR_INTERNAL_ERROR`. Emit `recording_started` only after both operations succeed.

- [ ] **Step 4: Remove Meeting SDK build linkage**

Delete the `ZR_WITH_ZOOM` option and all SDK include/library logic from CMake. Remove Zoom-specific source and test targets while preserving Windows capture and media libraries.

- [ ] **Step 5: Build and run native tests**

Run:

```powershell
cmake --build work/native-external-tests --config Release
ctest --test-dir work/native-external-tests -C Release --output-on-failure
```

Expected: 100% tests passed and no dependency on `sdk.dll`.

- [ ] **Step 6: Commit**

```powershell
git add src/ZoomRecorder.Native tests/ZoomRecorder.Native.Tests
git commit -m "refactor: make native recorder capture-only"
```

---

### Task 4: Managed launcher and external-client recording flow

**Files:**
- Create: `src/ZoomRecorder.App/ZoomClient/IMeetingLauncher.cs`
- Create: `src/ZoomRecorder.App/ZoomClient/WindowsMeetingLauncher.cs`
- Create: `src/ZoomRecorder.App/Services/ExternalZoomJoinFlow.cs`
- Modify: `src/ZoomRecorder.App/Interop/NativeMethods.cs`
- Modify: `src/ZoomRecorder.App/Interop/NativeSession.cs`
- Modify: `src/ZoomRecorder.App/Interop/NativeRecordingSession.cs`
- Delete: `src/ZoomRecorder.App/Interop/NativeMeetingClient.cs`
- Delete: `src/ZoomRecorder.App/Interop/MeetingEntryAwaiter.cs`
- Delete: `src/ZoomRecorder.App/Interop/MeetingSdkJwtFactory.cs`
- Delete: `src/ZoomRecorder.App/Services/NativeJoinFlow.cs`
- Create: `tests/ZoomRecorder.App.Tests/ZoomClient/WindowsMeetingLauncherTests.cs`
- Create: `tests/ZoomRecorder.App.Tests/ExternalZoomJoinFlowTests.cs`
- Delete: `tests/ZoomRecorder.App.Tests/MeetingEntryAwaiterTests.cs`
- Delete: `tests/ZoomRecorder.App.Tests/MeetingSdkJwtFactoryTests.cs`
- Delete: `tests/ZoomRecorder.App.Tests/NativeJoinFlowTests.cs`

**Interfaces:**
- Produces: `IMeetingLauncher.OpenAsync(Uri meetingUri, CancellationToken cancellationToken)`.
- Consumes: `ZoomWindowDetector.WaitForMeetingWindowAsync(TimeSpan.FromMinutes(15), token)`.
- Produces: `ExternalZoomJoinFlow : IJoinFlow` plus `StopAndSaveAsync`, `RecordingCompleted`, `FinalizationFailed`, and `CurrentMeetingId` matching current MainWindow consumers.
- Changes: `NativeSession.StartRecording(string path, nint meetingWindow)`.

- [ ] **Step 1: Write failing launcher tests**

Inject `IWindowsShell.Open(Uri)` and assert the exact HTTPS URI is passed. Map missing-handler errors to `ZoomWorkplaceUnavailableException` and other shell failures to `MeetingLaunchException`; never catch caller cancellation.

- [ ] **Step 2: Write failing orchestration tests**

Use fakes to prove the exact order `Prepare output → Open URI → Detect HWND → Start(path, HWND)`, no native start before detection, timeout/cancellation cleanup, correct meeting ID exposure, and exactly-once completion after both native `capture_ended` and manual stop race.

```csharp
Assert.Equal(
    ["prepare", "launch:https://zoom.us/j/1234567890", "detect", "start:42"],
    events);
```

- [ ] **Step 3: Run focused tests and verify RED**

Run: `dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~WindowsMeetingLauncherTests|FullyQualifiedName~ExternalZoomJoinFlowTests"`

Expected: missing launcher and external flow types.

- [ ] **Step 4: Update managed native ABI and implement launcher**

Change the P/Invoke signature to include `nint meetingWindow`; delete managed prepare/enter/host calls and all credential/JWT serialization. Implement shell launch with `ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true }` behind the injected shell boundary.

- [ ] **Step 5: Implement `ExternalZoomJoinFlow`**

Keep `FinalizationGate` and `RecordingCompleted` semantics. Prepare a target before launch, delete it only when it remains zero bytes after a failed start, preserve non-empty partial files, and ensure all error messages identify launch, discovery, or capture stage. Subscribe to native `capture_ended`; ignore obsolete SDK event names.

- [ ] **Step 6: Run focused and full App tests**

Run the focused filter, then the established x64 App test build/VSTest sequence. Expected: all tests pass with no references to `ZOOM_CLIENT_ID`, `ZOOM_CLIENT_SECRET`, JWT, SDK auth, or SDK error codes.

- [ ] **Step 7: Commit**

```powershell
git add src/ZoomRecorder.App tests/ZoomRecorder.App.Tests
git commit -m "feat: launch and record Zoom Workplace meetings"
```

---

### Task 5: Join and waiting user interface

**Files:**
- Modify: `src/ZoomRecorder.App/ViewModels/JoinViewModel.cs`
- Modify: `src/ZoomRecorder.App/Views/JoinPage.xaml`
- Modify: `src/ZoomRecorder.App/Views/JoinPage.xaml.cs`
- Modify: `src/ZoomRecorder.App/MainWindow.xaml.cs`
- Modify: `src/ZoomRecorder.App/App.xaml.cs`
- Modify: `tests/ZoomRecorder.App.Tests/JoinViewModelTests.cs`
- Modify: `tests/ZoomRecorder.App.Tests/StudyWorkflowTests.cs`

**Interfaces:**
- `JoinViewModel` no longer exposes `DisplayName`.
- Produces: `JoinStatusText`, `CanCancel`, and `CancelJoin()` backed by a per-attempt `CancellationTokenSource`.
- MainWindow composes `ExternalZoomJoinFlow`, `WindowsMeetingLauncher`, `Win32ZoomWindowEnumerator`, and `ZoomWindowDetector`.

- [ ] **Step 1: Write failing view-model tests**

Assert that starting changes status to `Waiting for Zoom meeting…`, prevents a second attempt, `CancelJoin` cancels the active flow and stays on Join, timeout/ambiguous/missing-Zoom errors are user-readable, and successful detection navigates once.

- [ ] **Step 2: Run focused tests and verify RED**

Run: `dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj -p:Platform=x64 --filter FullyQualifiedName~JoinViewModelTests`

Expected: missing waiting/cancel members and old display-name assumptions.

- [ ] **Step 3: Implement the minimal UI state**

Remove the display-name text box. Change explanatory text to: `Zoom Workplace opens the meeting. Recording starts automatically when the meeting window appears.` Add a progress ring, `JoinStatusText`, and a Cancel button visible only during discovery. Keep the recording-consent notice.

- [ ] **Step 4: Replace composition and remove credential startup requirements**

Construct the external flow in `MainWindow`; remove Meeting SDK host attachment and credential validation. Keep completion registration/assignment handlers unchanged. Do not delete any stored Windows Credential Manager entry automatically.

- [ ] **Step 5: Run focused tests, full Core/App tests, and x64 app build**

Run:

```powershell
dotnet test tests/ZoomRecorder.Core.Tests/ZoomRecorder.Core.Tests.csproj
& 'C:\BuildTools\MSBuild\Current\Bin\MSBuild.exe' src/ZoomRecorder.App/ZoomRecorder.App.csproj /restore /m /p:Configuration=Debug /p:Platform=x64 /p:RuntimeIdentifier=win-x64
```

Then run the established full x64 App VSTest sequence. Expected: zero build errors and every test passes.

- [ ] **Step 6: Commit**

```powershell
git add src/ZoomRecorder.App tests/ZoomRecorder.App.Tests
git commit -m "feat: add Zoom Workplace waiting flow"
```

---

### Task 6: SDK-free release and launcher migration

**Files:**
- Modify: `eng/Verify-Prerequisites.ps1`
- Modify: `eng/Verify-Release.ps1`
- Modify: `.github/workflows/windows-ci.yml`
- Modify: `docs/verification/windows-zoom-client-mvp.md`
- Modify: `docs/verification/class-library-cloud-study-tools.md`
- Modify: `Launch Zoom Recorder.cmd`
- Create during release build, do not commit: `outputs/ZoomRecorder-0.3.0/`

**Interfaces:**
- `Verify-Prerequisites.ps1` requires Zoom Workplace for manual/live mode but never requires `ZOOM_MEETING_SDK_DIR`.
- `Verify-Release.ps1` requires `ZoomRecorder.App.exe`, `ZoomRecorder.App.dll`, and `ZoomRecorder.Native.dll`; it fails if `sdk.dll`, `sdkExt.dll`, or known Meeting SDK runtime payload files are present.
- Root launcher targets `outputs\ZoomRecorder-0.3.0`.

- [ ] **Step 1: Write RED release assertions**

Update the verifier first and run it against `outputs/ZoomRecorder-0.2.0`.

Run: `pwsh -File eng/Verify-Release.ps1 -ReleaseDirectory D:\ZoomRecorder\outputs\ZoomRecorder-0.2.0`

Expected: FAIL because `sdk.dll` and Meeting SDK payloads are present.

- [ ] **Step 2: Update CI native configuration and prerequisites**

Remove `ZR_WITH_ZOOM`, SDK environment setup, and SDK artifact copying. Keep native Debug/Release builds, CTest, Core/App tests, and WinUI Release build.

- [ ] **Step 3: Build the current app and native recorder**

Run a Release x64 WinUI build and a clean Release native CMake build. Create an empty `outputs/ZoomRecorder-0.3.0`, recursively copy every file from `src/ZoomRecorder.App/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/`, then overwrite its `ZoomRecorder.Native.dll` with the clean native Release output. Do not copy any file from `0.2.0` because that directory contains Meeting SDK runtime files.

- [ ] **Step 4: Turn release verification GREEN**

Run: `pwsh -File eng/Verify-Release.ps1 -ReleaseDirectory D:\ZoomRecorder\outputs\ZoomRecorder-0.3.0`

Expected: `Release verification passed.` Confirm `Get-ChildItem -Recurse outputs/ZoomRecorder-0.3.0 | Select-String 'sdk.dll'` finds nothing.

- [ ] **Step 5: Update launcher and verification documentation**

Point `APP_DIR` to `outputs\ZoomRecorder-0.3.0`. Document Zoom Workplace as the prerequisite, external meeting support, automatic window discovery, consent responsibility, and removal of Meeting SDK credentials.

- [ ] **Step 6: Run complete automated verification**

Run fresh Core tests, full x64 App tests, Debug and Release x64 app builds, clean native Release build/CTest, `git diff --check`, and the release verifier. Record exact pass counts and commands in the verification docs.

- [ ] **Step 7: Commit tracked release changes**

```powershell
git add eng .github/workflows/windows-ci.yml docs/verification "Launch Zoom Recorder.cmd"
git commit -m "build: package SDK-free Zoom Workplace release"
```

---

### Task 7: Real Zoom Workplace acceptance test

**Files:**
- Modify: `docs/verification/windows-zoom-client-mvp.md`
- Modify: `docs/verification/class-library-cloud-study-tools.md`

**Interfaces:**
- Consumes the packaged `outputs/ZoomRecorder-0.3.0` release.
- Produces an evidence table with date, scenario, result, MP4 path, duration, audio/video observations, assignment result, and any follow-up defect.

- [ ] **Step 1: Launch only version 0.3.0**

Close older Zoom Recorder processes, run `D:\ZoomRecorder\Launch Zoom Recorder.cmd`, and confirm the process path is `D:\ZoomRecorder\outputs\ZoomRecorder-0.3.0\ZoomRecorder.App.exe`.

- [ ] **Step 2: Exercise external Zoom meeting entry**

Use a meeting hosted by an unrelated Zoom account. Confirm Zoom Workplace—not the Meeting SDK—opens it, the waiting state remains until a stable meeting window exists, and no SDK error 63 appears.

- [ ] **Step 3: Verify capture and finalization**

Produce at least a two-minute recording containing moving video, remote speech, and microphone speech. Leave through Zoom Workplace and confirm one playable MP4 is finalized and exactly one class-assignment prompt appears.

- [ ] **Step 4: Verify class processing flow**

Assign the MP4 to a new test class, run cloud transcription/summary, verify transcript and study guide access, and exercise the configured keep/delete-video choice without exposing credentials in documentation.

- [ ] **Step 5: Verify recovery cases**

Cancel once while waiting, verify Zoom home plus meeting selects the meeting, and manually stop once. Confirm no empty library entries and no duplicate finalization.

- [ ] **Step 6: Record results and fix failures through separate TDD cycles**

For any failed scenario, stop acceptance, use `superpowers:systematic-debugging`, add a reproducing test, implement one root-cause fix, rerun full relevant verification, rebuild `0.3.0`, and repeat the failed acceptance scenario.

- [ ] **Step 7: Commit verification evidence**

```powershell
git add docs/verification
git commit -m "test: verify private Zoom Workplace recording flow"
```
