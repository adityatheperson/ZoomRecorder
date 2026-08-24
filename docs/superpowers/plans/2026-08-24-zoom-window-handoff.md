# Zoom Window Handoff Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Continue one full-window 1920x1080 MP4 recording when Zoom replaces or resizes its window, and finalize only after no replacement appears for 15 seconds.

**Architecture:** Native capture reports a lost video window without stopping audio or finalizing the writer. The managed join flow supervises a bounded replacement search and calls a new native reattach operation. Native capture follows full-window size changes, while GPU normalization keeps every frame on a fixed 1920x1080 canvas with aspect-fit letterboxing.

**Tech Stack:** C#/.NET 8, xUnit, C++20, Win32, Windows Graphics Capture, D3D11 Video Processor, Media Foundation, CMake/CTest.

**Spec:** `docs/superpowers/specs/2026-08-24-zoom-window-handoff-design.md`

## Global Constraints

- Keep external Zoom Workplace integration; do not restore the Zoom Meeting SDK.
- Preserve meeting audio and microphone capture through a video-window handoff.
- Use a 15-second replacement grace period.
- Preserve exactly-once finalization.
- Encode every recording at 1920x1080, 30 FPS.
- Capture the entire Zoom top-level window and recreate WGC buffers when its content size changes.
- Preserve aspect ratio with black letterboxing when source dimensions differ.
- Do not modify or delete the untracked `outputs/` and `work/` directories.

---

### Task 1: Detect a Replacement Window While Excluding the Lost Handle

**Files:**
- Modify: `src/ZoomRecorder.App/ZoomClient/ZoomWindowDetector.cs`
- Test: `tests/ZoomRecorder.App.Tests/ZoomClient/ZoomWindowDetectorTests.cs`

**Interfaces:**
- Produces: `Task<nint> IZoomWindowDetector.WaitForMeetingWindowAsync(TimeSpan timeout, CancellationToken cancellationToken, nint excludedHandle = default)`
- Consumes: existing `ZoomWindowSelection.Select(IReadOnlyList<ZoomWindowDescription>)`

- [ ] **Step 1: Write a failing detector test**

Add a test whose scripted observations contain handle `7` followed by handle `8`, call `WaitForMeetingWindowAsync(..., excludedHandle: (nint)7)`, and assert that three stable observations select `8`, never `7`.

```csharp
[Fact]
public async Task Excluded_window_is_ignored_until_a_replacement_is_stable()
{
    var oldWindow = Window((nint)7);
    var replacement = Window((nint)8);
    var detector = new ZoomWindowDetector(
        new ScriptedEnumerator([oldWindow], [oldWindow, replacement], [replacement], [replacement]),
        TimeProvider.System,
        TimeSpan.FromMilliseconds(1));

    var handle = await detector.WaitForMeetingWindowAsync(
        TimeSpan.FromSeconds(1), CancellationToken.None, (nint)7);

    Assert.Equal((nint)8, handle);
}
```

- [ ] **Step 2: Run the focused test and verify RED**

Run: `dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter Excluded_window_is_ignored_until_a_replacement_is_stable`

Expected: compile failure because the interface and implementation do not accept `excludedHandle`.

- [ ] **Step 3: Add the exclusion parameter and filter candidates**

Change the interface and implementation signature to:

```csharp
Task<nint> WaitForMeetingWindowAsync(
    TimeSpan timeout,
    CancellationToken cancellationToken,
    nint excludedHandle = default);
```

Before selection, filter the enumerated list when `excludedHandle != nint.Zero`:

```csharp
var windows = enumerator.Enumerate();
if (excludedHandle != nint.Zero)
    windows = windows.Where(window => window.Handle != excludedHandle).ToArray();
var selection = ZoomWindowSelection.Select(windows);
```

- [ ] **Step 4: Run detector tests and verify GREEN**

Run: `dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter FullyQualifiedName~ZoomWindowDetectorTests`

Expected: all detector tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/ZoomRecorder.App/ZoomClient/ZoomWindowDetector.cs tests/ZoomRecorder.App.Tests/ZoomClient/ZoomWindowDetectorTests.cs
git commit -m "feat: detect replacement Zoom windows"
```

### Task 2: Reattach Native Video Without Finalizing the Recording

**Files:**
- Modify: `src/ZoomRecorder.Native/include/zoom_recorder.h`
- Modify: `src/ZoomRecorder.Native/src/api.cpp`
- Modify: `src/ZoomRecorder.Native/src/media/recording_pipeline.h`
- Modify: `src/ZoomRecorder.Native/src/media/recording_pipeline.cpp`
- Modify: `src/ZoomRecorder.App/Interop/NativeMethods.cs`
- Modify: `src/ZoomRecorder.App/Interop/NativeSession.cs`
- Modify: `src/ZoomRecorder.App/Interop/NativeRecordingSession.cs`
- Test: `tests/ZoomRecorder.Native.Tests/api_tests.cpp`
- Test: `tests/ZoomRecorder.App.Tests/ExternalZoomJoinFlowTests.cs`

**Interfaces:**
- Produces: `zr_result zr_attach_recording_window(zr_handle handle, intptr_t meeting_window)`
- Produces: `bool RecordingPipeline::replace_video(HWND meeting_window)`
- Produces: `Task IWindowRecordingSession.ReplaceWindowAsync(nint meetingWindow, CancellationToken cancellationToken)`
- Produces native event: `{"type":"capture_window_lost"}`

- [ ] **Step 1: Write failing ABI and managed interface tests**

In `api_tests.cpp`, assert null handle and null HWND return `ZR_INVALID_ARGUMENT`, and a created but not-started session returns `ZR_INVALID_STATE`:

```cpp
if (zr_attach_recording_window(nullptr, 1) != ZR_INVALID_ARGUMENT) return EXIT_FAILURE;
if (zr_attach_recording_window(handle, 0) != ZR_INVALID_ARGUMENT) return EXIT_FAILURE;
if (zr_attach_recording_window(handle, reinterpret_cast<intptr_t>(GetDesktopWindow())) != ZR_INVALID_STATE) return EXIT_FAILURE;
```

Update the managed fake recording session with a `ReplacedHandles` collection and add a compile-time behavioral test that calls `ReplaceWindowAsync((nint)8, ...)` and asserts `8` was recorded.

- [ ] **Step 2: Run focused suites and verify RED**

Run: `cmake --build build --config Debug --target ZoomRecorder.Native.Tests`

Run: `dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter FullyQualifiedName~ExternalZoomJoinFlowTests`

Expected: compilation fails because the new native and managed operations do not exist.

- [ ] **Step 3: Implement the native and managed reattach surface**

Declare/export `zr_attach_recording_window`. It must lock the session, require `recording_started`, validate the HWND with `IsWindow`, and call `pipeline->replace_video(window)`.

Refactor `RecordingPipeline::attach_video` into a shared internal attach operation. `replace_video` must stop and reset only `video_` and its watchdog, then attach the replacement; it must not stop WASAPI sources or finalize `Mp4Writer`.

Change the capture-item closed callback and watchdog callback to converge on one atomic loss notifier that stops watchdog self-joining and emits `capture_window_lost` exactly once for that attachment. Do not emit `capture_ended` from native code.

Add the P/Invoke and wrappers:

```csharp
[LibraryImport(Library)]
internal static partial ZrResult zr_attach_recording_window(nint handle, nint meetingWindow);

public void ReplaceRecordingWindow(nint meetingWindow) =>
    ThrowIfFailed(NativeMethods.zr_attach_recording_window(
        handle.DangerousGetHandle(), meetingWindow), "attach replacement Zoom window");
```

`NativeRecordingSession.ReplaceWindowAsync` checks cancellation and calls the wrapper.

- [ ] **Step 4: Run native and managed focused tests and verify GREEN**

Run: `cmake --build build --config Debug --target ZoomRecorder.Native.Tests`

Run: `ctest --test-dir build -C Debug --output-on-failure`

Run: `dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter FullyQualifiedName~ExternalZoomJoinFlowTests`

Expected: builds and tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/ZoomRecorder.Native src/ZoomRecorder.App/Interop tests/ZoomRecorder.Native.Tests/api_tests.cpp tests/ZoomRecorder.App.Tests/ExternalZoomJoinFlowTests.cs
git commit -m "feat: reattach native Zoom video capture"
```

### Task 3: Supervise Window Handoffs and Genuine Meeting Exit

**Files:**
- Modify: `src/ZoomRecorder.App/Services/ExternalZoomJoinFlow.cs`
- Test: `tests/ZoomRecorder.App.Tests/ExternalZoomJoinFlowTests.cs`

**Interfaces:**
- Consumes: detector overload with `excludedHandle`
- Consumes: `IWindowRecordingSession.ReplaceWindowAsync`
- Produces: one active handoff search at a time; timeout finalizes normally

- [ ] **Step 1: Write failing flow tests**

Add tests for these observable sequences:

```text
capture_window_lost -> detect excluding 42 -> replacement 84 -> replace:84 -> StopCount remains 0
capture_window_lost -> replacement timeout -> StopCount becomes 1
two capture_window_lost events -> only one replacement detection
capture_window_lost then manual stop -> cancellation plus exactly one StopAndFinalize call
```

Use `TaskCompletionSource` in fakes so each test controls replacement success, timeout, and cancellation without real 15-second waits. Assert event order and finalization counts, not private state.

- [ ] **Step 2: Run flow tests and verify RED**

Run: `dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter FullyQualifiedName~ExternalZoomJoinFlowTests`

Expected: failures because `capture_window_lost` still finalizes or is ignored and no replacement is attached.

- [ ] **Step 3: Implement managed handoff supervision**

Track `currentWindow`, a lock-protected `handoffTask`, and a per-recording `CancellationTokenSource`. Parse `capture_window_lost`; if no handoff is active, start a background handoff. Call:

```csharp
var replacement = await detector.WaitForMeetingWindowAsync(
    TimeSpan.FromSeconds(15), handoffCancellation.Token, currentWindow);
await recording.ReplaceWindowAsync(replacement, handoffCancellation.Token);
currentWindow = replacement;
```

Catch `ZoomWindowTimeoutException` and call `FinalizeAsync`. For an attachment exception, raise `FinalizationFailed`, then finalize the valid partial recording. `StopAndSaveAsync` cancels the handoff token before entering the existing finalization gate. Reset all handoff state at the beginning of `JoinAndRecordAsync`.

- [ ] **Step 4: Run flow and full managed tests and verify GREEN**

Run: `dotnet test tests/ZoomRecorder.App.Tests/ZoomRecorder.App.Tests.csproj --filter FullyQualifiedName~ExternalZoomJoinFlowTests`

Run: `dotnet test ZoomRecorder.sln`

Expected: all managed tests pass without warnings or errors.

- [ ] **Step 5: Commit**

```powershell
git add src/ZoomRecorder.App/Services/ExternalZoomJoinFlow.cs tests/ZoomRecorder.App.Tests/ExternalZoomJoinFlowTests.cs
git commit -m "fix: follow Zoom meeting window replacements"
```

### Task 4: Normalize Replacement Frames to Fixed MP4 Dimensions

**Files:**
- Create: `src/ZoomRecorder.Native/src/media/aspect_fit.h`
- Create: `src/ZoomRecorder.Native/src/media/aspect_fit.cpp`
- Create: `src/ZoomRecorder.Native/src/media/video_frame_normalizer.h`
- Create: `src/ZoomRecorder.Native/src/media/video_frame_normalizer.cpp`
- Create: `tests/ZoomRecorder.Native.Tests/aspect_fit_tests.cpp`
- Modify: `src/ZoomRecorder.Native/src/media/mp4_writer.h`
- Modify: `src/ZoomRecorder.Native/src/media/mp4_writer.cpp`
- Modify: `src/ZoomRecorder.Native/src/media/recording_pipeline.cpp`
- Modify: `src/ZoomRecorder.Native/CMakeLists.txt`
- Modify: `tests/ZoomRecorder.Native.Tests/api_tests.cpp`

**Interfaces:**
- Produces: `AspectFitRect calculate_aspect_fit(UINT source_width, UINT source_height, UINT target_width, UINT target_height)`
- Produces: `ID3D11Texture2D* VideoFrameNormalizer::normalize(ID3D11Texture2D* source, UINT width, UINT height)`
- Produces: `Mp4Writer::video_width()` and `video_height()` accessors

- [ ] **Step 1: Write failing aspect-fit tests**

Cover exact fit, wide-to-square letterboxing, tall-to-wide letterboxing, and even encoder coordinates. Example:

```cpp
const auto fit = calculate_aspect_fit(1920, 1080, 1280, 720);
if (fit.left != 0 || fit.top != 0 || fit.width != 1280 || fit.height != 720) return false;

const auto pillar = calculate_aspect_fit(800, 600, 1280, 720);
if (pillar.left != 160 || pillar.top != 0 || pillar.width != 960 || pillar.height != 720) return false;
```

Wire `run_aspect_fit_tests()` into `api_tests.cpp` and add the files to CMake.

- [ ] **Step 2: Build tests and verify RED**

Run: `cmake --build build --config Debug --target ZoomRecorder.Native.Tests`

Expected: compile/link failure because `calculate_aspect_fit` is missing.

- [ ] **Step 3: Implement aspect-fit math and D3D11 GPU normalization**

Implement integer cross-multiplication to select pillarbox vs letterbox, center the destination rectangle, and round every coordinate and dimension down to an even value.

`VideoFrameNormalizer` must cache an output BGRA texture at the writer's fixed size, clear it to opaque black, and use `ID3D11VideoProcessor` to scale the source into `calculate_aspect_fit`'s destination rectangle. Return the original texture when its dimensions already equal the writer dimensions. Return `nullptr` on D3D failures so the existing encoder health path reports failure.

Store the width and height passed to `Mp4Writer::open`. In the recording frame callback, establish them on the first frame, then call the normalizer before every `write_video`.

- [ ] **Step 4: Run native tests and verify GREEN**

Run: `cmake --build build --config Debug --target ZoomRecorder.Native.Tests`

Run: `ctest --test-dir build -C Debug --output-on-failure`

Expected: all native tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/ZoomRecorder.Native tests/ZoomRecorder.Native.Tests
git commit -m "feat: normalize replacement capture frames"
```

### Task 5: Full Verification, Packaging, and Manual Handoff Check

**Files:**
- Modify only if required by verification rules: `eng/Verify-Release.ps1`
- Verify: `outputs/ZoomRecorder-0.3.0/`

**Interfaces:**
- Consumes all prior tasks.
- Produces a locally runnable SDK-free package with the handoff fix.

- [ ] **Step 1: Run all automated verification**

Run:

```powershell
dotnet test ZoomRecorder.sln
cmake --build build --config Release
ctest --test-dir build -C Release --output-on-failure
dotnet build ZoomRecorder.sln -c Release --no-restore
```

Expected: all commands exit 0; all managed and native tests pass.

- [ ] **Step 2: Package and verify the local release**

Build the unpackaged WinUI output and copy it plus the native Release DLL into the existing local release directory:

```powershell
dotnet build src/ZoomRecorder.App/ZoomRecorder.App.csproj -c Release -p:Platform=x64
Copy-Item 'src/ZoomRecorder.App/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/*' 'outputs/ZoomRecorder-0.3.0' -Recurse -Force
Copy-Item 'build/Release/ZoomRecorder.Native.dll' 'outputs/ZoomRecorder-0.3.0/ZoomRecorder.Native.dll' -Force
pwsh -File eng/Verify-Release.ps1 -ReleaseDirectory 'D:\ZoomRecorder\outputs\ZoomRecorder-0.3.0'
```

Expected: verifier reports success, the package contains `ZoomRecorder.App.exe` and `ZoomRecorder.Native.dll`, and it contains no Zoom Meeting SDK DLLs.

- [ ] **Step 3: Launch and manually verify the reported scenario**

Launch the packaged app using the root launcher. Join a Zoom meeting, remain on the microphone/camera pre-join screen briefly, enter the meeting, remain for at least 20 seconds, then leave.

Expected: recording timer continues across the transition, recording finalizes after leaving, and the saved MP4 contains both the pre-join segment and the in-meeting segment with continuous audio.

- [ ] **Step 4: Commit packaging changes only if packaging files changed**

```powershell
git add scripts/package-release.ps1
git commit -m "build: package Zoom window handoff fix"
```

- [ ] **Step 5: Record final evidence**

Report exact managed/native test counts, Release build outcome, package verifier outcome, output path, and manual-check result. Do not claim the manual scenario passed unless the resulting MP4 was opened and inspected.

### Task 6: Correct Full-Window 1080p Capture and Resize Handling

**Files:**
- Create: `src/ZoomRecorder.Native/src/media/capture_size_state.h`
- Create: `src/ZoomRecorder.Native/src/media/capture_size_state.cpp`
- Create: `tests/ZoomRecorder.Native.Tests/capture_size_state_tests.cpp`
- Modify: `src/ZoomRecorder.Native/src/media/meeting_region_source.cpp`
- Modify: `src/ZoomRecorder.Native/src/media/recording_pipeline.cpp`
- Modify: `src/ZoomRecorder.Native/src/media/mp4_writer.cpp`
- Modify: `src/ZoomRecorder.Native/CMakeLists.txt`

**Interfaces:**
- Produces: `bool CaptureSizeState::observe(UINT width, UINT height)` returning `true` exactly when nonzero content dimensions change after initialization.
- Consumes: `Direct3D11CaptureFrame::ContentSize()` and `Direct3D11CaptureFramePool::Recreate(...)`.
- Produces: fixed encoder contract `Mp4Writer::open(output_, 1920, 1080, 30)`.

- [ ] **Step 1: Write failing resize-state and fixed-output tests**

Add `capture_size_state_tests.cpp` covering initial observation, unchanged dimensions, a larger resize, and zero-sized input:

```cpp
CaptureSizeState state;
if (state.observe(800, 600)) return false;
if (state.observe(800, 600)) return false;
if (!state.observe(1600, 900)) return false;
if (state.observe(0, 0)) return false;
```

Extend the GPU normalization test to normalize both an 800x600 texture and a 1600x900 texture into 1920x1080 and assert each output texture is exactly 1920x1080.

- [ ] **Step 2: Build native tests and verify RED**

Run:

```powershell
cmd /d /s /c "call C:\BuildTools\Common7\Tools\VsDevCmd.bat -arch=x64 -host_arch=x64 && C:\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe --build build-ninja --target ZoomRecorder.Native.Tests"
```

Expected: compile failure because `CaptureSizeState` does not exist, followed after test wiring by a behavioral failure until resize handling is implemented.

- [ ] **Step 3: Implement full-window capture and frame-pool recreation**

Implement `CaptureSizeState` as a focused nonzero dimension tracker. In the frame callback, read `frame.ContentSize()` before accessing the surface. If `observe` reports a change, release that frame and call:

```cpp
pool_.Recreate(
    device_,
    directx::DirectXPixelFormat::B8G8R8A8UIntNormalized,
    3,
    frame.ContentSize());
return;
```

For normal frames, copy the valid rectangle `{0, 0, ContentSize.Width, ContentSize.Height}` from the captured root-window texture. Remove target-child/capture-root rectangle intersection from the video path so the entire `GraphicsCaptureItem` is passed downstream. Keep width and height even before creating the copied texture.

- [ ] **Step 4: Open the writer at a fixed 1080p contract**

In the first-frame writer branch, replace source-derived dimensions with:

```cpp
constexpr UINT output_width = 1920;
constexpr UINT output_height = 1080;
if (!writer_.open(output_, output_width, output_height, 30)) {
  fail("MP4 encoder could not start for 1080p Zoom capture");
  return;
}
```

Continue passing every source texture through `VideoFrameNormalizer`, which aspect-fits into the writer's 1920x1080 dimensions.

- [ ] **Step 5: Run Debug and Release verification**

Run native Debug and Release CTest, Core tests, all staged App tests, WinUI Release build, and `eng/Verify-Release.ps1`. Expected: zero failures, a valid SDK-free package, and no build warnings introduced by these changes.

- [ ] **Step 6: Commit**

```powershell
git add src/ZoomRecorder.Native tests/ZoomRecorder.Native.Tests
git commit -m "fix: capture full Zoom window at 1080p"
```

- [ ] **Step 7: Package and manually verify**

Replace `outputs/ZoomRecorder-0.3.0` from the verified Release build, launch through `Launch Zoom Recorder.cmd`, join from the pre-join screen, resize/maximize the real meeting window, leave, and open the finalized MP4.

Expected: the MP4 reports 1920x1080, shows the entire Zoom window before and after resizing, keeps slides/text readable, and finalizes after the existing grace period.
