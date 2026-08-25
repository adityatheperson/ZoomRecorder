# Local Whisper Transcription Design

**Date:** 2026-08-24
**Status:** Approved design
**Scope:** Manual, English-only, transcript-only processing for the local Windows Zoom Recorder

## Goal

Allow a user to select a finalized recording and create an editable, timestamped transcript entirely on the local computer. The first transcription may download one verified Whisper model. After that download, transcription must work offline and must not call OpenAI or any other cloud-processing service.

The first version ends successfully at **Transcript ready**. It does not generate summaries, notes, key terms, assignments, review questions, or class-guide updates.

## Decisions

- Transcription is started manually from an individual recording.
- The default model is the English-only whisper.cpp `small.en` GGML model.
- The model is downloaded once on first use rather than bundled in every release.
- A separate `whisper.cpp` worker process performs inference so a native runtime failure cannot terminate the WinUI application.
- The worker attempts Vulkan GPU acceleration first and falls back once to a CPU-only worker when GPU initialization fails before usable output is produced. The end user does not install a CUDA or Vulkan SDK; the GPU worker relies only on the Vulkan loader supplied by the installed display driver.
- Existing M4A audio checkpoints remain reusable.
- The existing checkpointed transcript storage and transcript-merging logic remain authoritative.
- Transcript-only completion never deletes the MP4.

## Non-goals

- Cloud transcription or cloud study-material generation
- Automatic transcription when a meeting ends
- Languages other than English
- Speaker identification or diarization
- Live transcription during a meeting
- Local summarization
- User-selectable Whisper models in the first version
- Deleting or changing an already stored OpenAI API key

## User Experience

The lecture-detail primary action becomes **Transcribe locally**.

On first use, the existing processing dialog enters a model-acquisition stage and displays **Downloading English transcription model (~500 MB)** with byte progress and a Cancel action. Cancellation leaves no usable partial model and does not alter the recording.

After the verified model is available, the dialog displays **Preparing audio** and then **Transcribing locally**. Transcription progress may be indeterminate in the first version; completed audio and transcript chunks are checkpointed so Retry does not repeat successful chunks.

On success:

- the processing dialog reports **Transcript ready**;
- the recording/library status reports **Transcript ready**;
- the existing editable Transcript tab loads the committed text, while the committed artifact retains segment timestamps for seeking and future UI features;
- summary-related controls remain unavailable and clearly indicate that study materials have not been generated.

The workflow contains no cloud-consent prompt and never asks for an API key.

## Architecture

The existing processing boundary remains in place:

```text
Recording MP4
  -> existing NativeAudioChunkPreparer (M4A checkpoints)
  -> local M4A-to-PCM converter (temporary 16 kHz mono WAV)
  -> LocalWhisperTranscriptionClient
  -> isolated whisper.cpp worker
  -> validated timestamped TranscriptChunk
  -> existing TranscriptMerger
  -> existing verified transcript artifact and SQLite checkpoint
  -> transcript-only Completed state
```

`ITranscriptionClient` remains the coordinator-facing abstraction. App composition supplies `LocalWhisperTranscriptionClient` instead of `OpenAiTranscriptionClient`. Transcript-only composition does not construct `OpenAiApiClient`, `OpenAiTranscriptionClient`, or `OpenAiStudyGenerationClient`.

The coordinator changes its success path for this release: once all transcript chunks and the merged transcript are committed, the job transitions directly to `Completed`. It does not enter `GeneratingStudyPackage` or `UpdatingClassGuide`, and it does not run video-deletion eligibility.

Existing unfinished jobs are recoverable. A `NeedsAttention` or `Transcribing` job resumes through the local client and reuses valid audio/transcript checkpoints. If an older unfinished job is already in a post-transcription cloud stage and has a verified committed transcript, recovery completes it as transcript-only; without a verified transcript, recovery returns it to the earliest safe transcript stage. Completed historical study packages remain readable and are not altered.

## Components

### WhisperModelManager

Owns the model lifecycle under:

```text
%LOCALAPPDATA%\ZoomRecorder\Models\ggml-small.en.bin
```

The source URL, expected byte length, and SHA-256 digest are pinned in a versioned model manifest in source control. Download writes only to a unique `.partial` path. The manager streams the download, reports byte progress, computes SHA-256 while writing, verifies both size and digest, flushes the file, and atomically moves it into the final path.

At every use, an existing model is checked against the manifest before the worker launches. A mismatched file is quarantined with a `.corrupt` suffix and replaced by a new verified download. Only one in-process model acquisition may run at a time; simultaneous requests share the same operation.

No model request contains recording data. The download is the only network activity in transcript-only processing.

### LocalPcmAudioConverter

Converts one existing M4A checkpoint at a time to signed 16-bit, mono, 16 kHz PCM WAV using the existing Windows/native media layer. It writes inside the job directory through a `.partial` file and publishes atomically only after the WAV header and payload are finalized.

The WAV is transient. It is removed after its transcript chunk is durably checkpointed, on cancellation, and during recovery cleanup. The original M4A checkpoint remains intact so conversion can be retried safely.

### WhisperWorkerRunner

Launches a worker with explicit absolute paths for the executable, verified model, input WAV, and unique output base. Shell execution is disabled; no user-controlled text is concatenated into a command line.

The release contains two independently built workers from one pinned whisper.cpp revision:

- a Vulkan GPU-enabled Windows x64 worker built with `GGML_VULKAN=ON`;
- a CPU-only Windows x64 worker.

The GPU worker is attempted first. A failure before a valid output artifact is produced triggers exactly one CPU retry and updates the dialog to **Using CPU fallback**. Invalid transcript output never triggers a second GPU/CPU cycle. Cancellation terminates the worker process tree and awaits exit before cleanup.

The runner captures bounded stderr for diagnostics but never persists transcript text to logs. Worker output is written to a job-scoped JSON file rather than parsed from console text.

### LocalWhisperTranscriptionClient

Implements `ITranscriptionClient` and orchestrates model validation, temporary WAV conversion, worker execution, JSON parsing, and cleanup.

It validates every returned segment before constructing `TranscriptChunk`:

- finite, non-negative timestamps;
- end greater than or equal to start;
- timestamps contained within the source chunk, allowing at most 250 milliseconds of encoder-rounding tolerance at the final boundary and clamping that tolerated excess to the chunk end;
- nonblank text after normalization;
- monotonically ordered segments.

Chunk-relative timestamps are offset using the existing `AudioChunk` metadata. Overlap deduplication remains the responsibility of the existing `TranscriptMerger`.

### Processing Coordinator

The coordinator retains checkpoint/retry semantics but adds an explicit transcript-only completion path. Transcript-only completion sets no lecture-package, assignment, or guide checkpoint. `DeletionEligibility` remains false because the required study outputs do not exist.

New actionable error codes distinguish model acquisition, audio conversion, runtime startup, worker failure, and invalid worker output. Cloud credential and cloud service errors cannot originate from this flow.

### WinUI Presentation

The processing view model exposes the model download, preparation, local transcription, CPU fallback, cancellation, failure, and transcript-ready states. The lecture detail page exposes the transcript editor as it does today. Cloud-only controls are disabled for transcript-only recordings rather than initiating a failing cloud operation.

Settings may retain the previously stored API key for future work, but local transcription neither reads nor tests it.

## Error Handling

| Failure | Behavior |
|---|---|
| Download unavailable | Preserve no final model; show a network/download error with Retry |
| Download cancelled | Delete `.partial`; leave the job resumable |
| Size or checksum mismatch | Quarantine the file; show model verification failure; never launch worker |
| M4A conversion failure | Remove temporary WAV; preserve M4A and MP4; show audio preparation failure |
| GPU initialization/startup failure | Retry once with CPU worker and surface CPU fallback state |
| CPU worker failure | Preserve completed checkpoints; show local transcription failure with Retry |
| Invalid/missing JSON | Reject output, remove it, and show invalid transcription output |
| User cancellation | Terminate worker tree, clean transient files, preserve durable checkpoints |
| App interruption | Recovery removes transient files and resumes from the latest durable checkpoint |

Failures never delete the MP4, committed transcript chunks, the verified model, or completed historical study artifacts.

## Security and Privacy

- Recording audio and transcript data never leave the computer.
- The model download is fetched only from the pinned HTTPS origin.
- Model integrity is enforced by pinned byte length and SHA-256 before native code reads the file.
- Worker executable paths are fixed release assets and checked by release verification.
- Worker arguments are passed through `ProcessStartInfo.ArgumentList`; shell invocation is prohibited.
- Diagnostics omit API keys, recording content, transcript text, and full user-controlled command lines.

## Packaging

The release verifier requires the WinUI application, native capture DLL, GPU worker and dependencies, CPU worker and dependencies, and a versioned model manifest. The model itself is not included in the release directory.

Both workers are built from a pinned whisper.cpp revision in a reproducible release step. The CPU worker is built with `GGML_VULKAN=OFF`; the GPU worker is built with `GGML_VULKAN=ON`. The packaged GPU dependencies exclude build-time SDK files and require only a current display driver at runtime. Licenses and required notices ship with the release. The root launcher continues to target the current verified output directory.

The app must remain usable for recording and library browsing when no model exists and when the computer is offline.

## Testing

Automated tests cover:

- first-use model download, byte progress, atomic publication, cancellation, and retry;
- rejection of wrong size/hash and quarantine behavior;
- coalescing simultaneous model requests;
- M4A-to-WAV conversion contract and transient-file cleanup;
- worker argument safety, bounded diagnostics, cancellation, and process-tree termination;
- GPU success, GPU startup failure followed by CPU success, and GPU plus CPU failure;
- valid JSON mapping and rejection of invalid timestamps, ordering, text, and schema;
- transcript checkpoint resume without repeating successful chunks;
- migration/recovery of existing failed cloud-transcription jobs;
- direct transcript-only completion after transcript commit;
- absence of OpenAI client calls and credential reads;
- preservation of MP4 files for success, failure, and cancellation;
- UI labels, disabled cloud actions, actionable errors, and transcript-ready state;
- release verification for worker binaries, dependencies, manifest, licenses, and absence of a bundled model.

Manual verification uses a short real Zoom recording:

1. Remove or temporarily relocate any existing model.
2. Select **Transcribe locally** and confirm download progress and successful verification.
3. Confirm GPU transcription or clearly reported CPU fallback.
4. Open the transcript, verify readable English text, edit it, save it, and reopen it; separately verify segment timestamps in the committed transcript artifact.
5. Disconnect networking and transcribe a second recording using the cached model.
6. Confirm the MP4 remains present and no OpenAI API usage is recorded.

## Success Criteria

The feature is complete when an English Zoom recording can be manually transcribed on the local Windows computer, the first-use model is downloaded and verified safely, subsequent transcriptions work offline, native inference failures do not crash the WinUI app, retries preserve completed work, editable transcript text is available in the existing library UI with segment timestamps retained in its committed artifact, the MP4 remains untouched, and no OpenAI credential or API request participates in the workflow.
