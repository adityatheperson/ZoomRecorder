# Class Library and Cloud Study Tools Verification

## Build under verification

- Application version: 0.2.0 development branch
- Windows CI: GitHub `windows-latest`, x64
- Zoom Meeting SDK: supplied locally at release packaging time; `sdk.dll` is required
- Transcription model: `gpt-transcribe`
- Study generation model: `gpt-5.6-luna`
- Credential storage: Windows Credential Manager (`ZoomRecorder/OpenAI`)
- API retention: depends on the configured OpenAI account; no key or lecture content is recorded here
- Audio normalization: mono 16 kHz PCM boundary, encoded to 48 kHz mono AAC/M4A for Windows Media Foundation compatibility
- Chunk limit: 24 MiB with five-second overlap

## Automated evidence

| Check | Result |
|---|---|
| Core processing, merge, persistence, and deletion eligibility tests | Automated in Windows CI |
| WinUI view-model, SQLite, cloud adapter, credential, recycler, and workflow tests | Automated in Windows CI |
| Debug x64 WinUI/XAML build | Automated in Windows CI |
| Native exporter CTest | Required by release verification pipeline |
| Release contains SQLite, native audio preparation, Zoom SDK, and no obvious API key | `eng/Verify-Release.ps1` |
| Unrelated repository `work/` directory excluded | `eng/Verify-Release.ps1` |

Generated artifacts are stored under the configured class artifact root using recording/class GUID directories. Job-only chunks remain under the registered job directory and recovery cleans only registered job artifacts.

## Manual verification matrix

The following checks require an interactive Zoom meeting, a disposable OpenAI test account, or a real recording. They must not be marked passed from fake-adapter evidence.

| Scenario | Status | Evidence to record |
|---|---|---|
| Record with no API key | Pending manual run | Recording path and local-library row only |
| Create, rename, and archive class | Pending manual run | Class IDs; no lecture content |
| Assigned/unassigned recordings and create class during assignment | Pending manual run | Recording/class IDs |
| Remember, override, and forget meeting mapping | Pending manual run | Redacted meeting ID suffix |
| Successful real transcription and study generation | Pending manual run | Recording length, request count, artifact paths |
| Invalid key, no billing, offline start, and HTTP 429 retry | Pending disposable-account/network run | Sanitized error code and retry count |
| Close/reopen at every processing stage | Pending long-form run | Stage and recovered checkpoint count |
| Edit transcript and refresh without retranscription | Pending real-artifact run | Transcription request count remains unchanged |
| Confirmed assignment preservation and guide update failure | Pending real-artifact run | Stable assignment ID and pending guide flag |
| Class reassignment | Pending manual run | Old/new class IDs and guide pending flags |
| Cancellation | Pending long-form run | Cancelled state and retained original hash |
| Recycle success and recycle-unavailable fallback | Pending manual shell-policy run | Outcome and `video_available` state |
| Video-unavailable UI | Pending manual run | Disabled seek control |
| Original recording hash after every failed job | Pending long-form run | SHA-256 before/after (hash only) |

Never include API keys, credentials, raw transcript text, lecture notes, or identifiable meeting content in this document.
