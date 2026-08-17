# Windows Zoom Client MVP Design

## Purpose

Build a greenfield Windows application that lets a guest join a Zoom meeting and automatically records only the meeting area, meeting audio, and the user's microphone. The recording is saved locally without requiring a Zoom login.

## Scope

The MVP supports:

- Guest entry using either a Zoom link or a meeting ID and passcode.
- A single join screen with a display-name field.
- Zoom's standard meeting interface embedded in the application.
- Recording that begins as part of joining and must succeed before meeting entry.
- Capture limited to the embedded Zoom meeting area.
- A mixed audio track containing meeting audio and microphone input.
- Automatic recording finalization when the meeting ends.
- Local MP4 output in the user's `Videos\Meeting Recordings` folder.
- A persistent, slim recording-status strip during the meeting.
- A completion screen with recording details and file-opening actions.

The MVP excludes Zoom account login, cloud recording or upload, arbitrary desktop capture, analytics, transcription, editing, and a fully custom meeting interface.

## User Experience

### Join screen

The launch screen contains:

- One input that accepts a Zoom meeting link or meeting ID.
- A passcode input that is shown or requested only when required.
- A display-name input.
- A primary **Join and record** action.

The application remembers no Zoom credentials because the MVP uses guest entry only.

### Pre-join recording gate

Selecting **Join and record** validates that:

- The destination folder can be created and written to.
- Sufficient disk space is available for recording to begin.
- Meeting-area video capture can initialize.
- Meeting-audio capture can initialize.
- Microphone capture can initialize.
- The encoder and output file can initialize.

The meeting is not entered unless all required recording components start successfully. Failure produces a plain-language error with **Retry** and **Cancel** actions. The MVP does not provide a join-without-recording bypass.

### In-meeting shell

Zoom's standard meeting interface occupies nearly the entire application window. A slim strip below it remains visible throughout the meeting and shows:

- A red recording indicator.
- Elapsed recording time.
- Meeting-audio and microphone health.
- Local-save health.

The strip provides status rather than routine recording controls. Recording starts and stops with the meeting lifecycle, preventing accidental partial recordings.

### Completion screen

When the meeting ends, the application stops capture, flushes pending media, finalizes the MP4, and then displays:

- Filename.
- Recording duration.
- File size.
- **Open recording**, **Open folder**, and **Done** actions.

The app does not report success until the output file has finalized successfully.

## Architecture

### WinUI 3 application layer (C#)

The managed application owns:

- Window and navigation lifecycle.
- Join-form validation and guest-entry workflow.
- Zoom meeting host surface.
- Recording-status presentation.
- Error and recovery screens.
- Completion metadata and local file actions.

### Native integration layer (C++)

The native layer owns:

- Zoom Meeting SDK integration and meeting lifecycle events.
- Binding Zoom's standard meeting interface to the host window.
- Capture of the meeting host region only.
- Meeting-audio loopback capture.
- Microphone capture.
- Audio mixing and synchronization.
- Hardware-assisted video encoding when available.
- MP4 writing and safe finalization.

The C# layer communicates with the native layer through a narrow asynchronous interface based on commands and state events. UI code does not manipulate media pipelines directly, and native code does not control application navigation.

## State Model and Data Flow

The primary application states are:

1. `ReadyToJoin`
2. `PreparingMeeting`
3. `StartingRecording`
4. `InMeetingRecording`
5. `FinalizingRecording`
6. `RecordingComplete`
7. `RecoverableError`

The join request is normalized into meeting credentials, then passed to the Zoom integration. Recording resources are prepared before final meeting entry. Once the Zoom meeting host is active and capture sources are valid, media flows through video capture and audio capture/mixing into the encoder and MP4 writer. Meeting termination triggers finalization exactly once.

The recording filename uses a filesystem-safe meeting identifier when available plus the local start timestamp. If no useful meeting identifier is available, it uses `Zoom Meeting` plus the timestamp. Name collisions receive an incrementing suffix.

## Recording Boundaries

Video capture is restricted to the client area assigned to the embedded Zoom meeting host. The application's join screen, status strip, completion screen, other applications, notifications outside that region, and the rest of the desktop are excluded.

Audio contains two sources:

- The meeting playback stream audible through the selected Windows output device.
- The selected microphone input.

The sources are synchronized and mixed into the MP4 audio track. The status strip independently reports whether each source remains healthy.

## Failure Handling

Before meeting entry, any required recording failure blocks entry and offers Retry or Cancel.

During a meeting, a media, encoder, or storage failure immediately changes the status strip to a prominent error. The application stops claiming that recording is active, attempts to finalize all recoverable captured media, and explains the resulting file state. It never silently continues the meeting while presenting a healthy recording state.

Unexpected application shutdown should leave a recoverable temporary recording where the media stack permits it. On the next launch, the application detects recoverable recordings and offers to finalize them. This recovery is limited to data already safely written; the MVP does not promise recovery from every process or hardware failure.

## Security and Privacy

- No Zoom account credentials are requested or stored.
- No recording is uploaded by the application.
- No desktop region outside the embedded meeting host is captured.
- Recording files remain in the local user profile unless the user moves them.
- Logs exclude meeting passcodes and raw media.
- The application clearly indicates recording for the entire time capture is active.

The product must make any legally required recording notice or consent responsibility clear to the user. It must not attempt to bypass Zoom or jurisdictional consent requirements.

## Verification Strategy

Unit tests cover meeting-input parsing, state transitions, filename generation, storage checks, and error mapping.

Native component tests cover source initialization, timestamp synchronization, audio mixing, finalization idempotency, and recovery behavior using controllable test sources.

Integration tests exercise successful guest join, passcode-required join, blocked entry for each unavailable recording dependency, normal meeting termination, abrupt meeting termination, and mid-meeting storage or device failure.

End-to-end verification confirms that the produced MP4 plays correctly, contains meeting-area video only, includes both audio sources, excludes the app chrome and surrounding desktop, and reports accurate completion metadata.

## MVP Success Criteria

The MVP succeeds when a Windows user can enter valid guest meeting details, join through the embedded standard Zoom interface, see unambiguous recording health, end the meeting, and open a playable local MP4 containing only the meeting area plus meeting and microphone audio. Meeting entry must be blocked whenever the application cannot establish that recording is active.
