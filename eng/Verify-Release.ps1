param([string]$ReleaseDirectory)
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ReleaseDirectory)) { throw 'Pass -ReleaseDirectory.' }
$required = @('ZoomRecorder.App.exe', 'ZoomRecorder.App.dll', 'ZoomRecorder.Native.dll', 'sdk.dll')
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $ReleaseDirectory $_)) })
if ($missing.Count) { throw ('Release files missing: ' + ($missing -join ', ')) }

$deps = Get-Content -Raw -LiteralPath (Join-Path $ReleaseDirectory 'ZoomRecorder.App.deps.json')
if ($deps -match 'SimulatedMeetingClient|SimulatedRecordingSession') { throw 'Release contains simulated adapters.' }

$secret = [Environment]::GetEnvironmentVariable('ZOOM_CLIENT_SECRET', 'User')
if ($deps -match [regex]::Escape($secret) -and -not [string]::IsNullOrWhiteSpace($secret)) { throw 'Release contains the Zoom Client Secret.' }
Write-Host 'Release verification passed.'
