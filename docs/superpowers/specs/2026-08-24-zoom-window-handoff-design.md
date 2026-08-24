# Zoom Pre-Join to Meeting Window Handoff Design

## Problem

Zoom Workplace displays a pre-join window for microphone and camera choices, then replaces it with a separate in-meeting window. ZoomRecorder currently attaches capture to the first stable Zoom meeting candidate. When that window closes, the native capture pipeline reports that the meeting ended, so the app finalizes a video containing only the pre-join screen.

## Desired Behavior

- Record the pre-join screen if capture has already begun.
- Continue recording meeting audio and microphone audio while Zoom replaces the window.
- Attach video capture to the replacement Zoom meeting window and continue writing the same MP4.
- Capture the entire Zoom top-level window at a fixed 1920x1080, 30 FPS output without clipping after a resize.
- Finalize only when no replacement meeting window appears during a bounded handoff grace period, or when the user stops recording.
- Preserve exactly-once finalization.

## Architecture

The native pipeline will distinguish a lost capture window from a confirmed meeting end. When its current capture item closes or the window watchdog confirms disappearance, it will emit a `capture_window_lost` event and stop only the video source. Audio capture and the MP4 writer remain active.

The managed join flow will own handoff policy. On `capture_window_lost`, it will ask the existing Zoom window detector for a stable candidate other than the lost handle. If a replacement appears during a 15-second grace period, it calls a new recording-session operation to attach that window. If no replacement appears, the flow finalizes the recording. Manual stop cancels any pending handoff and finalizes immediately.

The native capture operation will support replacing its video source. The MP4 writer always opens at 1920x1080 and 30 FPS rather than inheriting the smaller pre-join window's dimensions. Every frame is normalized into that fixed canvas so the in-meeting window retains readable class and slide detail. Aspect ratio is preserved with black letterboxing rather than stretching or cropping meeting content.

`MeetingRegionSource` captures the entire Zoom top-level window. It uses each frame's `ContentSize` as the valid source extent and recreates `Direct3D11CaptureFramePool` whenever Zoom changes size. This prevents Windows Graphics Capture from clipping a window that grows beyond the buffers allocated for the pre-resize dimensions.

## Component Changes

- `MeetingRegionSource`: reports loss without declaring the overall recording ended.
- `RecordingPipeline`: detaches a lost video source and accepts a replacement HWND while retaining audio and writer state.
- Native ABI and `NativeRecordingSession`: expose a replace/reattach-video operation.
- `IZoomWindowDetector`: allow detection that excludes the previous HWND.
- `ExternalZoomJoinFlow`: coordinate the grace-period search, replacement attachment, cancellation, and exactly-once finalization.
- Video normalization: convert all Zoom frames to 1920x1080 with black letterboxing when aspect ratios differ.
- Capture resize handling: recreate the WGC frame pool on `ContentSize` changes and copy only the valid full-window extent.

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
- Native video-size tests: full-window frames are aspect-fit into 1920x1080 and differently sized GPU textures preserve the full source extent.
- Native resize-state tests: a changed capture content size requests one frame-pool recreation and does not reuse stale dimensions.
- Run the complete managed and native suites, Release build, package verification, and a manual Zoom pre-join-to-meeting recording check.

## Scope

This change does not add Zoom SDK integration, inspect Zoom meeting content, or depend on localized window titles beyond the existing candidate selection. It changes only external Zoom Workplace window supervision and capture continuity. The fixed 1080p output intentionally trades additional GPU/storage use for readable class recordings and stable single-file encoding.
