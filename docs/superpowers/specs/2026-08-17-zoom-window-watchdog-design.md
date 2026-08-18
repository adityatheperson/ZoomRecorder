# Zoom Meeting Window Watchdog Design

## Goal

Reliably stop, finalize, and save the local MP4 when the user leaves the separate Zoom Meeting SDK window, even when Zoom does not emit its normal disconnect or ended callbacks and Windows Graphics Capture does not emit `Closed`.

## Architecture

The native layer will retain the exact top-level Zoom meeting window handle after capture attaches. A dedicated watchdog owned by the recording pipeline will inspect that handle every 500 milliseconds while recording is active.

The watchdog will signal meeting completion when either condition occurs:

- `IsWindow` becomes false, meaning the Zoom meeting window was destroyed.
- The window remains non-visible for two consecutive seconds, covering Zoom flows that hide rather than destroy the meeting window while avoiding false stops from brief UI transitions.

The existing Zoom SDK status and Graphics Capture `Closed` callbacks remain as faster signals. All three paths emit the same end event and pass through the existing one-shot finalization gate, so duplicate notifications cannot finalize twice.

## Manual Fallback

The recorder companion window will show a clearly labeled **Stop & Save** button beside the recording timer. Pressing it will use the same one-shot finalization path as automatic detection. It will not attempt to close or leave the Zoom meeting; it only stops and saves the local recording.

## Data Flow

1. Zoom reports `INMEETING` and exposes its meeting window handle.
2. Recording attaches to that window and starts the watchdog.
3. Leaving Zoom produces any one of: SDK disconnect, capture closed, invalid window, or sustained hidden window.
4. The first end signal starts finalization; later signals are ignored.
5. The MP4 writer stops audio and video, finalizes the partial file, moves it to its final `.mp4` path, and the app shows the completion screen.
6. **Stop & Save** enters the same flow at step 4.

## Error Handling

- A transient hidden state shorter than two seconds does not stop recording.
- A watchdog end signal is treated as normal completion, not a recording failure.
- If MP4 finalization fails, the companion remains open and shows a recording-specific error instead of silently claiming success.
- Closing the companion app still performs its current native cleanup; this design does not change app-exit behavior.

## Testing

- Unit-test watchdog state transitions: visible, briefly hidden, hidden past grace period, and destroyed.
- Unit-test one-shot behavior when SDK, capture, and watchdog signals overlap.
- Unit-test that **Stop & Save** invokes the same finalization path.
- Run the full app and native suites, build the Zoom-enabled release, sign the updated binaries, verify deployed hashes, and relaunch for a real meeting test.

## Scope

This change only replaces unreliable meeting-end detection and adds the manual recording stop. It does not alter Zoom joining, authentication, capture selection, audio mixing, encoding settings, or output location.
