# Windows Zoom Client MVP Verification

## Automated evidence

- Windows target: Windows 11 x64
- Meeting client: installed Zoom Workplace (external prerequisite)
- Managed core tests: 100 passed locally
- Managed app tests: compiled locally; execution is enforced by Windows CI
- Native contract/media tests: 1/1 CTest passed
- WinUI Release x64 build: passed
- Capture-only native Release x64 build: passed
- SDK-free `ZoomRecorder-0.3.0` release verification: passed
- Release contains the native capture bridge and rejects Meeting SDK runtime files
- Release dependency manifest contains no simulated meeting/recording adapter
- No Zoom SDK client ID, client secret, JWT, or SDK environment variable is required

## Manual real-meeting checks still required

- Join a meeting hosted by an unrelated account using link and ID/passcode
- Confirm the installed Zoom Workplace app opens and no SDK error 63 appears
- Confirm automatic discovery selects the Zoom meeting window rather than Zoom home/settings
- Confirm the Zoom meeting window is recorded while Zoom Recorder and surrounding desktop are excluded
- Confirm meeting audio and microphone are both audible in the resulting MP4
- Confirm host-ended and local-leave paths finalize the recording
- Confirm Open recording and Open folder actions
- Confirm cancel-while-waiting and manual-stop paths create no empty or duplicate library entries

The person recording remains responsible for providing any notice or consent required by Zoom policy and applicable law.
