# Class Library and Cloud Study Tools Design

## Purpose

Extend Zoom Recorder from a recording utility into a personal class library. Recordings can be organized by class and, on demand, converted into editable transcripts, structured study materials, detected assignments, and a cumulative class study guide using OpenAI cloud APIs.

This design extends the existing Windows Zoom Client MVP. It does not change the requirement that recording and MP4 finalization work independently of cloud processing.

## Product Decisions

- The feature is for one local Windows user; there is no application account or multi-user collaboration.
- Recording remains local and completes before AI processing begins.
- AI processing is user-initiated through **Transcribe & Summarize**, not automatic during a meeting.
- Processing uses OpenAI cloud APIs with the user's own API key.
- Classes, recordings, transcripts, notes, assignments, and study guides remain locally owned and accessible through one application.
- A recording may be unassigned, assigned to one class, or reassigned later.
- The application remembers a Zoom meeting-ID-to-class association after the first assignment and uses it for future recordings. The user can forget or override the association.

## Scope

### Included

- A persistent **Classes** section in the main navigation.
- A unified **Recordings** section containing assigned and unassigned recordings.
- Creating, renaming, viewing, and archiving classes.
- Assigning a recording from its completion screen or detail screen.
- Creating a class within the assignment flow.
- Automatic future assignment based on a remembered Zoom meeting ID.
- Per-lecture transcription and study-package generation.
- Editable transcripts, notes, and detected assignments.
- A cumulative study guide for each class.
- Resumable, checkpointed cloud-processing jobs.
- Secure storage of the user's OpenAI API key in Windows Credential Manager.
- Optional deletion of an MP4 after successful processing.

### Excluded from the first version

- Multi-device synchronization or cloud backup of the local library.
- User accounts, sharing, collaboration, or instructor/student roles.
- Live transcription or live summarization during a meeting.
- Automatic processing without a user action.
- Learning-management-system integration.
- Calendar integration and automatic assignment reminders.
- Semantic chat over an entire class.

## User Experience

### Main library

The application shell contains **Home**, **Classes**, **Recordings**, and **Settings** navigation plus a prominent **Record a class** action.

The Classes page shows class cards with the class name, term, lecture count, most recent lecture, and study-package status. It also shows recent recordings and an **Unassigned recordings** area so an uncategorized recording is always discoverable.

### Class detail

A class page contains:

- Class name, term, lecture count, and a direct **Open study guide** action.
- **Lectures**, **Study guide**, **Assignments**, and **Class settings** tabs.
- Search limited to the current class.
- A lecture list with date, duration, processing state, and access to the recording or generated materials.
- A consolidated list of detected assignments.
- Settings for naming, archiving, meeting mappings, and processing defaults.

### Recording assignment

After finalization, the completion screen offers **Assign to class** without blocking access to the MP4. The same action is available later from recording details.

The assignment dialog supports choosing an existing class or creating a new one. Reassignment is allowed. When the recording has a normalized Zoom meeting ID, the app offers to remember the association. Future recordings with that ID are assigned automatically, while manual selection always wins. A class setting can remove the remembered mapping.

### Lecture study package

Selecting **Transcribe & Summarize** produces:

- A searchable transcript with timestamps.
- A short summary.
- Structured lecture notes.
- Key terms and definitions.
- Detected assignments and due dates, with uncertain dates clearly marked.
- Likely exam or review questions.
- An update to the class's cumulative study guide.

The transcript, notes, and assignments are editable. Editing the transcript marks derived materials as potentially stale and offers an optional **Refresh study materials** action. Refreshing from an existing transcript does not repeat transcription.

### Processing confirmation

Before starting, the application shows:

- That lecture audio will be sent to OpenAI.
- Estimated upload size and an approximate cost when a reliable estimate is available.
- The selected class.
- A **Delete video after successful processing** option.

The deletion option is off by default. Settings may establish a future default, and each job can override it.

## Processing States and Recovery

Recording and AI processing use separate lifecycles. A successfully finalized recording remains valid regardless of AI availability.

User-visible processing states are:

1. `ReadyToProcess`
2. `PreparingAudio`
3. `Transcribing`
4. `GeneratingStudyPackage`
5. `UpdatingClassGuide`
6. `Completed`
7. `NeedsAttention`
8. `Cancelled`

Each stage writes a durable checkpoint. Closing the application requests a clean pause; reopening it resumes safe work or offers **Retry**. Completed transcription chunks are reused so transient failures do not repeat successful paid requests. Cancellation retains the MP4 and all verified intermediate results needed for a later retry.

Errors distinguish at least invalid or missing credentials, account/billing restrictions, lost connectivity, API rate limits, unsupported/corrupt media, insufficient local storage, and unexpected service responses. Error text provides a plain-language next action and never exposes the API key or raw service payloads containing lecture content.

## Cloud Processing Flow

1. Validate that the MP4 exists and the local library is writable.
2. Extract compressed audio to a job-specific temporary directory.
3. Split long audio into overlapping chunks below the transcription endpoint's current file-size limit.
4. Transcribe chunks through OpenAI's file-transcription API. `gpt-transcribe` is the initial default because current official documentation recommends it for completed recordings. The model name is configuration rather than a UI choice so it can be updated without changing stored data.
5. Merge timestamped chunk results and remove overlap duplication.
6. Generate a versioned, structured study-package document through the OpenAI Responses API.
7. Validate the response against the application's schema before committing it.
8. Save the transcript and lecture materials locally in a single transaction-like commit.
9. Regenerate the class study guide from the class's completed lecture packages, then atomically replace the previous guide.
10. Remove job-specific temporary audio and chunks.
11. If requested, move the MP4 to the Windows Recycle Bin only after every required artifact and database update has been verified.

The app deletes its own temporary files immediately after successful processing or cancellation cleanup. It does not claim that it can force immediate deletion from OpenAI systems; remote data handling is governed by the user's OpenAI account and API data-retention settings.

## Study-Package Schema

The cloud response is accepted only when it conforms to a versioned structure containing:

- Lecture title and date.
- Short summary.
- Ordered note sections with headings and timestamp references.
- Key terms with definitions and supporting timestamps.
- Assignments with description, due-date text, normalized due date when confidently available, confidence, and supporting timestamp.
- Review questions with suggested answers and supporting lecture sections.
- Study-guide contributions grouped by topic.

Generated claims should reference transcript timestamps whenever practical. Low-confidence assignments or due dates are presented for confirmation rather than silently treated as fact.

## Cumulative Class Study Guide

Each lecture package remains the source of record for lecture-specific output. The class study guide is a replaceable derived artifact, not the only copy of accumulated knowledge.

When a lecture package changes, the application rebuilds the guide using completed lecture packages for that class. The guide organizes major concepts, recurring themes, key terms, review questions, and lecture references while avoiding duplicate material. Rebuilding must not require the original MP4 or another transcription request.

If updating the guide fails, the lecture package is still marked complete and the previous valid guide remains available. The guide displays that an update is pending and can be retried independently.

## Local Storage Model

SQLite stores authoritative metadata and relationships:

- `Classes`
- `Recordings`
- `MeetingClassMappings`
- `ProcessingJobs`
- `TranscriptionChunks`
- `LecturePackages`
- `Assignments`
- `ClassStudyGuides`
- `AppSettings`

Large human-readable artifacts are stored as ordinary local files referenced by the database. The initial formats are UTF-8 JSON for structured/versioned data and Markdown or plain text for user-readable exports. The database stores stable IDs, paths, hashes, timestamps, schema versions, edit state, and processing status rather than embedding MP4 or large transcript data.

Local artifact directories use stable IDs so renaming or reassigning a class does not require destructive file moves. Database and artifact writes use temporary files plus atomic replacement where supported.

## Component Architecture

### WinUI application layer

Owns navigation, class and recording views, editors, processing confirmation, progress, error recovery, and settings.

### Recording subsystem

Remains responsible for Zoom lifecycle, media capture, MP4 finalization, and recording health. It publishes finalized recording metadata to the library through a narrow interface and has no dependency on the OpenAI client.

### Library subsystem

Owns SQLite access, artifact paths, class/recording relationships, meeting mappings, searches, and atomic persistence. UI and processing code use repository interfaces rather than direct SQL.

### Processing coordinator

Owns the durable job state machine, audio preparation, chunk scheduling, checkpoints, cancellation, retry policy, schema validation, artifact commits, cleanup, and optional MP4 deletion.

### External adapters

- An audio-extraction adapter isolates the chosen media tool.
- An OpenAI transcription adapter isolates the audio endpoint.
- An OpenAI study-generation adapter isolates the Responses API and prompt/schema versions.
- A credential-vault adapter isolates Windows Credential Manager.
- A recycle-bin adapter isolates recoverable deletion behavior.

These boundaries allow local workflows to be tested without joining Zoom meetings or making paid cloud requests.

## Security, Privacy, and Deletion

- The OpenAI API key is stored only through Windows Credential Manager.
- The key, meeting passcodes, transcript bodies, and raw model responses are excluded from logs.
- Network requests are made only after the user starts processing and sees the cloud-use notice.
- API errors are sanitized before display or logging.
- Temporary audio uses a private per-job directory and is cleaned after success, cancellation, and recoverable startup cleanup.
- MP4 deletion is never attempted until transcript, lecture package, assignment updates, and class-guide outcome have been durably recorded.
- A failed class-guide update does not delete the MP4 until the guide has either updated successfully or the user explicitly accepts completion without that update.
- Normal MP4 deletion uses the Recycle Bin when supported. If recoverable deletion is unavailable, the app requests a separate explicit confirmation before permanent deletion.
- After deletion, the recording remains in the library as a study-material entry and clearly shows that its video is no longer locally available.

## Failure and Consistency Rules

- AI failures never alter the finalized MP4.
- A recording cannot display `Completed` unless required artifacts pass schema and file-integrity checks.
- Partial study output is not substituted for a previously valid package.
- Assignment edits made by the user are preserved during regeneration; generated suggestions are merged or presented for review rather than overwriting confirmed edits.
- Reassigning a recording moves its logical class relationship and triggers study-guide rebuilds for both affected classes without requiring retranscription.
- Forgetting a meeting mapping affects only future recordings.
- Deleting or archiving a class requires an explicit decision about its recordings; no media is silently removed.

## Verification Strategy

### Unit tests

- Class, recording, and mapping rules.
- Processing-state transitions and restart behavior.
- Chunk-boundary and transcript-overlap merging.
- Study-package schema validation and version migration.
- Assignment confidence and edit-preservation rules.
- MP4-deletion eligibility.
- Credential and error redaction.

### Integration tests

- Persist and reopen a library containing assigned and unassigned recordings.
- Complete processing through fake transcription and generation adapters.
- Fail and resume at every checkpoint without repeating completed chunks.
- Rebuild a class guide after adding, editing, or reassigning a lecture.
- Verify that cloud failure cannot damage a recording.
- Verify that MP4 deletion occurs only after all required durable commits.
- Verify Recycle Bin failure falls back to explicit permanent-deletion confirmation.

### End-to-end tests

- Finalize a recording, assign it, process it, edit the transcript, refresh study materials, and find it through the class UI.
- Allow a recording to remain unassigned and assign it later.
- Remember a Zoom meeting mapping, auto-assign a later recording, override it, and forget the mapping.
- Close and reopen during processing and recover correctly.
- Process with MP4 deletion enabled and verify that all study materials remain accessible while video playback is reported unavailable.

## Success Criteria

The feature succeeds when the user can organize every finalized recording into a class, intentionally process a lecture through the cloud, receive an editable and searchable study package, see the cumulative class guide update, recover safely from interruption or service failure, and optionally remove the original MP4 only after all required outputs are verified. Existing Zoom recording must continue to work with no OpenAI key and during complete cloud-service unavailability.
