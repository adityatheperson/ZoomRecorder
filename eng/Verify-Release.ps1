param([string]$ReleaseDirectory)
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ReleaseDirectory)) { throw 'Pass -ReleaseDirectory.' }
$required = @('ZoomRecorder.App.exe', 'ZoomRecorder.App.dll', 'ZoomRecorder.Native.dll')
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $ReleaseDirectory $_)) })
if ($missing.Count) { throw ('Release files missing: ' + ($missing -join ', ')) }

$deps = Get-Content -Raw -LiteralPath (Join-Path $ReleaseDirectory 'ZoomRecorder.App.deps.json')
if ($deps -match 'SimulatedMeetingClient|SimulatedRecordingSession') { throw 'Release contains simulated adapters.' }
if ($deps -notmatch 'Microsoft.Data.Sqlite') { throw 'SQLite library dependency is missing.' }
if ($deps -match 'secret-key|sk-[A-Za-z0-9]') { throw 'Release appears to contain an API key.' }
$meetingSdkPayload = @(
    'sdk.dll',
    'sdkExt.dll',
    'zSDK.dll',
    'zoom_meeting_bridge.dll',
    'ZZHostIPCSDK.dll',
    'CptControl.exe',
    'CptInstall.exe',
    'zTscoder.exe'
)
$packagedNames = @(Get-ChildItem -LiteralPath $ReleaseDirectory -Recurse -File | ForEach-Object Name)
$forbidden = @($meetingSdkPayload | Where-Object { $packagedNames -contains $_ })
if ($forbidden.Count) { throw ('Release contains Zoom Meeting SDK payload: ' + ($forbidden -join ', ')) }
if (Test-Path -LiteralPath (Join-Path $ReleaseDirectory 'work')) { throw 'Unrelated work directory was packaged.' }

Write-Host 'Release verification passed.'
