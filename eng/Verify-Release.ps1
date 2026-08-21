param([string]$ReleaseDirectory)
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ReleaseDirectory)) { throw 'Pass -ReleaseDirectory.' }
$required = @('ZoomRecorder.App.exe', 'ZoomRecorder.App.dll', 'ZoomRecorder.Native.dll', 'sdk.dll')
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $ReleaseDirectory $_)) })
if ($missing.Count) { throw ('Release files missing: ' + ($missing -join ', ')) }

$deps = Get-Content -Raw -LiteralPath (Join-Path $ReleaseDirectory 'ZoomRecorder.App.deps.json')
if ($deps -match 'SimulatedMeetingClient|SimulatedRecordingSession') { throw 'Release contains simulated adapters.' }
if ($deps -notmatch 'Microsoft.Data.Sqlite') { throw 'SQLite library dependency is missing.' }
if ($deps -match 'secret-key|sk-[A-Za-z0-9]') { throw 'Release appears to contain an API key.' }
if (-not (Test-Path -LiteralPath (Join-Path $ReleaseDirectory 'ZoomRecorder.Native.dll'))) { throw 'Native audio preparation is missing.' }
if (Test-Path -LiteralPath (Join-Path $ReleaseDirectory 'work')) { throw 'Unrelated work directory was packaged.' }

$secret = [Environment]::GetEnvironmentVariable('ZOOM_CLIENT_SECRET', 'User')
if ($deps -match [regex]::Escape($secret) -and -not [string]::IsNullOrWhiteSpace($secret)) { throw 'Release contains the Zoom Client Secret.' }
Write-Host 'Release verification passed.'
