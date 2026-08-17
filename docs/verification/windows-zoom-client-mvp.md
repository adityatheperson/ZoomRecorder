# Windows Zoom Client MVP Verification

## Automated evidence

- Windows target: Windows 11 x64
- Zoom Meeting SDK: 7.1.5.43953
- Managed core tests: 19 passed
- Managed app tests: 6 passed
- Native contract/media tests: passed
- WinUI Release x64 build: passed
- Zoom-enabled native Release x64 build: passed
- Packaged application startup: running and responsive
- Release contains the native bridge and Zoom SDK runtime
- Release dependency manifest contains no simulated meeting/recording adapter
- Zoom Client Secret is not embedded in release artifacts

## Manual real-meeting checks still required

- Join a meeting hosted by the developer Zoom account using link and ID/passcode
- Confirm embedded standard Zoom UI sizing and resize behavior
- Confirm meeting area is recorded while app chrome and surrounding desktop are excluded
- Confirm meeting audio and microphone are both audible in the resulting MP4
- Confirm host-ended and local-leave paths finalize the recording
- Confirm Open recording and Open folder actions
- Confirm external-account behavior after Zoom app review and OBF authorization
