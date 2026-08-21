# Private Zoom Workplace Capture Design

## Purpose

Replace the embedded Zoom Meeting SDK join flow with a private, local workflow that opens meetings in the installed Zoom Workplace desktop application, detects its meeting window, and records that window with meeting audio and microphone input. This removes Marketplace publication requirements while preserving the class library, cloud transcription, summaries, study guides, and optional source-video deletion.

## User Experience

The join page keeps a Zoom link or meeting ID field and an optional passcode field. It removes the display-name field because Zoom Workplace owns the user's identity and meeting-entry experience.

When the user selects **Join and record**, Zoom Recorder:

1. Validates and normalizes the meeting input.
2. Opens the meeting through Windows so the installed Zoom Workplace application handles authentication, passcodes, waiting rooms, consent notices, and meeting entry.
3. Shows a cancelable **Waiting for Zoom meeting…** state.
4. Detects a stable, visible Zoom meeting window.
5. Starts meeting-audio, microphone, and window capture.
6. Shows the existing recording status and manual **Stop and save** action.
7. Finalizes when the detected meeting window closes or is persistently unavailable.
8. Registers the MP4 in the library and shows the existing class-assignment dialog.

The app does not silently capture unrelated windows. If Zoom Workplace is not installed, it displays a clear installation message and a link to Zoom's official download page.

## Architecture

### Meeting launch boundary

A managed `IMeetingLauncher` accepts the normalized meeting request and opens an HTTPS Zoom link through the Windows shell. Meeting IDs and passcodes are converted into a Zoom join URL before launch. The launcher reports a typed failure when Windows has no registered handler or launching fails.

Zoom Recorder does not automate Zoom credentials, buttons, or waiting-room interactions. Those remain inside Zoom Workplace.

### Window discovery boundary

A managed `IZoomWindowLocator` enumerates visible top-level windows and returns immutable window descriptions. A detector selects a meeting candidate using all of the following evidence:

- The owning executable is Zoom Workplace.
- The window is visible and not minimized.
- Its client area is large enough to contain meeting video.
- It is not a known launcher, settings, updater, authentication, or tray window.
- The same candidate remains valid across consecutive observations.

Window titles may contribute evidence but are not the sole criterion because titles vary by locale and Zoom version. Discovery is cancelable and waits up to fifteen minutes so waiting-room and delayed-class scenarios are supported. If multiple candidates remain equally plausible, recording does not start and the UI explains how to close extra Zoom windows and retry.

The locator and scoring policy are separate. The Win32 enumerator can therefore change without changing orchestration, and the scoring policy can be unit-tested with synthetic window descriptions.

### Recording boundary

The native recorder no longer initializes or authenticates the Zoom Meeting SDK. Its public operation becomes: start audio capture for a prepared output path, attach capture to a supplied top-level window handle, report health, and finalize once.

The existing Windows Graphics Capture, WASAPI loopback, microphone capture, audio mixing, MP4 writer, and meeting-window watchdog remain. The native watchdog treats transient visibility changes as recoverable and finalizes only after the target window is destroyed or persistently unavailable. Manual stop remains idempotent and uses the same finalization gate.

### Application orchestration

A new external-client join flow coordinates:

1. Prepare the output path.
2. Launch Zoom Workplace.
3. Await a meeting window.
4. Start and attach the recorder.
5. Publish recording state to the existing meeting page.
6. Finalize and raise the existing `RecordingCompleted` event.

No empty or failed recording is registered in the library. The existing completion registration and assignment flow remains the only path that adds a recording.

## SDK Removal and Packaging

Production code removes Zoom JWT generation, SDK authentication, SDK meeting services, SDK error mapping, and SDK-specific UI behavior. The native build no longer links `sdk.lib`, and the release no longer includes `sdk.dll` or the Meeting SDK runtime tree.

Zoom Workplace is an external prerequisite and is not bundled. The release verifier requires the app and native recorder, rejects embedded Meeting SDK runtime files, and confirms that the class/study dependencies remain present.

Existing Zoom API credentials stored by older builds are no longer read. Removing obsolete credentials from Windows Credential Manager may be offered as a later cleanup action, but the migration does not delete credentials automatically.

## Error Handling

User-visible failures identify the failed stage and preserve recovery:

- **Zoom Workplace is not installed:** provide the official installation link.
- **Meeting link could not open:** keep the entered link and allow retry.
- **No meeting window appeared within fifteen minutes:** return to the join page without a library entry.
- **Multiple meeting windows are ambiguous:** ask the user to close extra Zoom windows and retry.
- **Audio or video capture cannot initialize:** stop all started components, delete a zero-byte output, preserve any non-empty partial output, and show the native diagnostic.
- **Meeting window disappears briefly:** continue waiting through the watchdog grace period.
- **Finalization fails:** preserve the partial file and show its location; never register it as successfully finalized.

Cancellation is safe during launch, discovery, recording, and finalization. Repeated completion signals remain guarded so only one finalized recording and assignment prompt can occur.

## Testing

### Automated tests

- Meeting input normalization and HTTPS launch URL construction, including passcodes.
- Launcher success, missing-handler, and shell-failure behavior through an injected shell boundary.
- Window scoring across meeting, launcher, settings, hidden, minimized, undersized, stale, and ambiguous candidates.
- Stable-candidate requirements, cancellation, and timeout using a fake window enumerator and fake clock.
- Orchestration ordering: no capture before detection, correct supplied window handle, cleanup on every failure, and exactly-once finalization.
- Existing recording completion, library registration, class assignment, processing, and deletion regressions.
- Native capture and watchdog tests without Meeting SDK linkage.
- Release verification proving the Meeting SDK is absent and required app/native artifacts are present.

### Manual verification

On a machine with Zoom Workplace installed:

1. Join by full Zoom link and by meeting ID/passcode.
2. Exercise signed-in, signed-out, waiting-room, and passcode prompts.
3. Confirm the selected window is the actual meeting window when Zoom's home window is also open.
4. Confirm video, meeting audio, and microphone are present in the MP4.
5. Leave normally, close the meeting window, and use manual stop; verify each finalizes once.
6. Assign the recording to a class and complete transcription/summary processing.
7. Verify missing Zoom Workplace and discovery-timeout recovery.

## Success Criteria

- Meetings hosted by external Zoom accounts open through Zoom Workplace without Meeting SDK error 63.
- Recording starts automatically only after the correct meeting window is detected.
- Closing or leaving the meeting finalizes one playable MP4 and opens class assignment.
- No Meeting SDK credentials or runtime files are required.
- Existing class library and cloud study workflows continue to pass their regression suites.
