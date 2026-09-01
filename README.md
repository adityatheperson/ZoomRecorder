# Zoom Recorder

Zoom Recorder records the installed Zoom Workplace meeting window and keeps recordings in a local class library.

## Launch

Run `Launch Zoom Recorder.cmd`. The launcher opens `outputs\ZoomRecorder-0.5.0\ZoomRecorder.App.exe`.

## Requirements

- Windows 10 version 2004 (build 19041) or later, x64
- Zoom Workplace installed for the current Windows user
- Internet access for the first local-transcription model download

The `0.5.0` package includes the required .NET and Windows App SDK runtime files. It does not require a separate .NET installation, Zoom Meeting SDK, OpenAI key, or Python runtime.

## Local transcription

- Open a class recording and select **Transcribe locally**. Transcription starts only when you request it.
- The first transcription downloads and verifies the English Whisper model (approximately 500 MB). Later transcriptions use the cached model and work offline.
- The app tries the Vulkan GPU worker first and clearly reports **Using CPU fallback** if GPU initialization is unavailable.
- Transcription is English-only in this version. It creates an editable, timestamped transcript; summaries and other generated study materials are unavailable.
- Local transcription does not require or read an OpenAI API key, does not upload the recording, and does not call OpenAI.
- Transcript-only processing never deletes the MP4. The source recording remains available after success, failure, or cancellation.

Worker binaries are pinned to whisper.cpp `v1.9.1` at commit `f049fff` and are packaged under `tools\whisper`. The model itself is intentionally not included in the release.

## Local data

Recordings, transcripts, processing checkpoints, and the downloaded Whisper model are stored under the current user's local application-data folders. Updating the portable application directory does not delete that data.
