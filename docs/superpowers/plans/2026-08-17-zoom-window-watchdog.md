# Zoom Window Watchdog Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reliably finalize and save the MP4 when the separate Zoom meeting window disappears or stays hidden, with a manual Stop & Save fallback.

**Architecture:** A pure native watchdog state machine evaluates the retained Zoom HWND every 500 ms and emits the existing capture-end signal after destruction or four consecutive hidden observations. The app routes SDK, capture, watchdog, and manual-stop requests through its existing atomic one-shot finalizer, yielding off native callbacks before stopping native capture.

**Tech Stack:** C++20, Win32 `IsWindow`/`IsWindowVisible`, `std::jthread`, C#/.NET 8, WinUI 3, xUnit.

## Global Constraints

- Keep Zoom joining, authentication, capture selection, audio mixing, encoding settings, and output location unchanged.
- Check the Zoom HWND every 500 milliseconds.
- Treat four consecutive hidden checks (two seconds) or an invalid HWND as meeting completion.
- All completion signals must finalize at most once.
- Stop & Save stops local recording but does not leave or close Zoom.

---

### Task 1: Native Zoom Window Watchdog

**Files:**
- Create: `src/ZoomRecorder.Native/src/media/meeting_window_watchdog.h`
- Create: `src/ZoomRecorder.Native/src/media/meeting_window_watchdog.cpp`
- Modify: `src/ZoomRecorder.Native/src/media/recording_pipeline.cpp`
- Modify: `src/ZoomRecorder.Native/CMakeLists.txt`
- Create: `tests/ZoomRecorder.Native.Tests/meeting_window_watchdog_tests.cpp`
- Modify: `tests/ZoomRecorder.Native.Tests/api_tests.cpp`

**Interfaces:**
- Produces: `bool MeetingWindowWatchdog::observe(bool exists, bool visible)`; returns true exactly once when the window is destroyed or hidden for four consecutive observations.
- Consumes: the HWND already passed to `RecordingPipeline::attach_video` and the existing pipeline `EndedCallback`.

- [ ] **Step 1: Write the failing watchdog state tests**

Test visible reset, three hidden checks without ending, fourth hidden check ending, invalid window ending immediately, and duplicate observations returning false after ending. Register `run_meeting_window_watchdog_tests()` in the native test runner.

- [ ] **Step 2: Run the native Debug build and verify RED**

Run the existing normalized-environment CMake Debug build. Expected: compilation fails because `MeetingWindowWatchdog` does not exist.

- [ ] **Step 3: Implement the state machine**

Create a focused class with `hidden_count_` and `ended_`. `observe(false, _)` ends immediately; visible resets the counter; hidden increments it and ends at four; every observation after ending returns false.

- [ ] **Step 4: Add the pipeline polling worker**

After video capture attaches, retain the top-level HWND and start a `std::jthread` that calls `observe(IsWindow(hwnd), IsWindowVisible(hwnd))` every 500 ms. When it returns true, invoke `ended_()` and exit. Request stop and join the worker during pipeline finalization and destruction.

- [ ] **Step 5: Run the native suite and verify GREEN**

Build Debug and run `ZoomRecorder.Native.Tests.exe`. Expected: exit code 0.

- [ ] **Step 6: Commit the native unit**

Commit message: `fix: watch Zoom window lifecycle during recording`.

### Task 2: One-Shot Manual Stop and Safe Finalization

**Files:**
- Modify: `src/ZoomRecorder.App/Services/NativeJoinFlow.cs`
- Create: `src/ZoomRecorder.App/Services/FinalizationGate.cs`
- Modify: `src/ZoomRecorder.App/Views/MeetingPage.xaml`
- Modify: `src/ZoomRecorder.App/Views/MeetingPage.xaml.cs`
- Modify: `src/ZoomRecorder.App/MainWindow.xaml.cs`
- Modify: `tests/ZoomRecorder.App.Tests/NativeJoinFlowTests.cs`

**Interfaces:**
- Produces: `bool FinalizationGate.TryBegin()` backed by `Interlocked.Exchange`.
- Produces: `Task NativeJoinFlow.StopAndSaveAsync()` using the same gate as native end events.
- Produces: `MeetingPage(Func<Task> stopAndSave)`; the button disables immediately and awaits the supplied action.
- Consumes: `NativeRecordingSession.StopAndFinalizeIfStartedAsync` and `RecordingCompleted`.

- [ ] **Step 1: Write failing one-shot and manual-stop tests**

Test `FinalizationGate.TryBegin()` returns true once and false thereafter. Keep parser tests proving meeting/capture end events request finalization while malformed and ordinary events do not.

- [ ] **Step 2: Run app tests and verify RED**

Build `ZoomRecorder.App.Tests` and run its DLL with `dotnet vstest`. Expected: failure because `StopAndSaveAsync`/the unified gate is missing.

- [ ] **Step 3: Implement unified asynchronous finalization**

Route native events and `StopAndSaveAsync` through one method guarded by `FinalizationGate.TryBegin()`. Begin with `await Task.Yield()` so native callback threads return before native stop/finalize runs. Raise `RecordingCompleted` only after a non-null result.

- [ ] **Step 4: Add Stop & Save UI**

Add a WinUI button in the recording status bar beside the timer. Pass `_joinFlow.StopAndSaveAsync` from `MainWindow.ShowMeeting`; disable the button on click, change its text to `Saving…`, and await completion. Do not call any Zoom leave API.

- [ ] **Step 5: Run app tests and verify GREEN**

Build and execute the app test DLL. Expected: all tests pass.

- [ ] **Step 6: Commit the app unit**

Commit message: `feat: add one-shot Stop and Save fallback`.

### Task 3: Release Verification and Deployment

**Files:**
- Build output only: `artifacts/native-release/Release/ZoomRecorder.Native.dll`
- Deploy output only: `outputs/ZoomRecorder-0.1.0`

**Interfaces:**
- Consumes: completed Tasks 1 and 2.
- Produces: signed, deployed, running Zoom Recorder build.

- [ ] **Step 1: Run complete verification**

Run Core tests, App tests, native tests, and the Zoom-enabled Release build. Require zero test failures and zero build errors.

- [ ] **Step 2: Stop the running app and deploy**

Wait until `ZoomRecorder.App` fully exits, copy the complete WinUI output plus the new native DLL, and verify source/deployed SHA-256 hashes before signing.

- [ ] **Step 3: Sign only application binaries**

Sign `ZoomRecorder.App.exe`, `ZoomRecorder.App.dll`, `ZoomRecorder.Core.dll`, and `ZoomRecorder.Native.dll` with the approved local development certificate. Do not modify Zoom SDK binaries.

- [ ] **Step 4: Relaunch and verify process health**

Launch from `outputs/ZoomRecorder-0.1.0`, wait three seconds, and require `ZoomRecorder.App` to report `Responding=True`.

- [ ] **Step 5: Report the remaining manual test**

Ask the user to join, leave through Zoom, and confirm the app reaches the completion screen and the final `.mp4` exists. Do not claim the real Zoom flow is fixed until that test succeeds.
