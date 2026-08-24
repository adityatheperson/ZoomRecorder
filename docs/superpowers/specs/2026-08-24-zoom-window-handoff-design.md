# Zoom Pre-Join to Meeting Window Handoff Design

## Problem

Zoom Workplace displays a pre-join window for microphone and camera choices, then replaces it with a separate in-meeting window. ZoomRecorder currently attaches capture to the first stable Zoom meeting candidate. When that window closes, the native capture pipeline reports that the meeting ended, so the app finalizes a video containing only the pre-join screen.

## Desired Behavior

- Record the pre-join screen if capture has already begun.
- Continue recording meeting audio and microphone audio while Zoom replaces the window.
- Attach video capture to the replacement Zoom meeting window and continue writing the same MP4.
- Finalize only when no replacement meeting window appears during a bounded handoff grace period, or when the user stops recording.
- Preserve exactly-once finalization.

## Architecture

The native pipeline will distinguish a lost capture window from a confirmed meeting end. When its current capture item closes or the window watchdog confirms disappearance, it will emit a `capture_window_lost` event and stop only the video source. Audio capture and the MP4 writer remain active.

The managed join flow will own handoff policy. On `capture_window_lost`, it will ask the existing Zoom window detector for a stable candidate other than the lost handle. If a replacement appears during a 15-second grace period, it calls a new recording-session operation to attach that window. If no replacement appears, the flow finalizes the recording. Manual stop cancels any pending handoff and finalizes immediately.

The native capture operation will support replacing its video source. The MP4 writer keeps the dimensions established by its first video frame. Replacement frames will be normalized to those dimensions before encoding so a differently sized in-meeting window does not invalidate the active Media Foundation stream. Aspect ratio will be preserved with letterboxing rather than stretching or cropping meeting content.

## Component Changes

- `MeetingRegionSource`: reports loss without declaring the overall recording ended.
- `RecordingPipeline`: detaches a lost video source and accepts a replacement HWND while retaining audio and writer state.
- Native ABI and `NativeRecordingSession`: expose a replace/reattach-video operation.
- `IZoomWindowDetector`: allow detection that excludes the previous HWND.
- `ExternalZoomJoinFlow`: coordinate the grace-period search, replacement attachment, cancellation, and exactly-once finalization.
- Video normalization: convert replacement textures to the writer's fixed output size with black letterboxing when dimensions differ.

## State and Event Flow

1. Launch Zoom and detect the first stable meeting candidate.
2. Start audio and attach capture to that window.
3. When the window disappears, native code emits `capture_window_lost` once.
4. Managed code searches for a stable Zoom candidate excluding the lost HWND.
5. If found within 15 seconds, replace the video source and continue the same recording.
6. If none is found, stop and finalize once.
7. A later window loss repeats the same bounded search, allowing normal Zoom UI window replacement without prematurely ending the recording.

## Error Handling

- Failure to attach a discovered replacement is treated as a failed handoff and proceeds to finalization with a user-visible error.
- A timeout is considered a genuine meeting exit and finalizes normally, not as an error.
- Manual stop cancels detection and wins the finalization race through the existing finalization gate.
- Duplicate native loss notifications are ignored while a handoff is already running.
- Encoder failures still stop the recording through the existing health/error path.

## Testing

- Managed regression test: a native window-loss event triggers replacement detection and reattachment without finalization.
- Managed regression test: no replacement within the grace period finalizes once.
- Managed concurrency tests: duplicate loss events and manual stop during handoff finalize at most once.
- Detector tests: an excluded HWND is never returned and a later stable candidate is selected.
- Native tests: replacing a video source preserves recording state; duplicate loss notification is suppressed.
- Native video-size tests: same-size frames pass through and differently sized frames are aspect-fit into the fixed output dimensions.
- Run the complete managed and native suites, Release build, package verification, and a manual Zoom pre-join-to-meeting recording check.

## Scope

This change does not add Zoom SDK integration, inspect Zoom meeting content, or depend on localized window titles beyond the existing candidate selection. It changes only external Zoom Workplace window supervision and capture continuity.
