param([string]$ReleaseDirectory)
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ReleaseDirectory)) { throw 'Pass -ReleaseDirectory.' }
$required = @('ZoomRecorder.App.exe', 'ZoomRecorder.App.dll', 'ZoomRecorder.App.pri', 'ZoomRecorder.Native.dll')
$required += @(
    'Assets\Whisper\model-small.en.json',
    'tools\whisper\LICENSE-whisper.cpp',
    'tools\whisper\LICENSE-ggml',
    'tools\whisper\vulkan\whisper-cli.exe',
    'tools\whisper\vulkan\whisper.dll',
    'tools\whisper\vulkan\ggml.dll',
    'tools\whisper\vulkan\ggml-base.dll',
    'tools\whisper\vulkan\ggml-cpu.dll',
    'tools\whisper\vulkan\ggml-vulkan.dll',
    'tools\whisper\cpu\whisper-cli.exe',
    'tools\whisper\cpu\whisper.dll',
    'tools\whisper\cpu\ggml.dll',
    'tools\whisper\cpu\ggml-base.dll',
    'tools\whisper\cpu\ggml-cpu.dll'
)
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
$packagedFiles = @(Get-ChildItem -LiteralPath $ReleaseDirectory -Recurse -File)
$forbidden = @($meetingSdkPayload | Where-Object { $packagedNames -contains $_ })
if ($forbidden.Count) { throw ('Release contains Zoom Meeting SDK payload: ' + ($forbidden -join ', ')) }
if (@($packagedFiles | Where-Object Name -Like 'ggml-*.bin').Count) {
    throw 'Release contains a Whisper model; models must be downloaded and verified at runtime.'
}
$pythonPayload = @($packagedFiles | Where-Object {
    $_.Name -match '^python(?:w|\d+)?\.exe$|^python\d*\.dll$' -or
    $_.FullName -match '[\\/](?:site-packages|python\d+(?:\.\d+)?)[\\/]'
})
if ($pythonPayload.Count) { throw 'Release contains a Python runtime.' }
if (Test-Path -LiteralPath (Join-Path $ReleaseDirectory 'tools\whisper\cpu\ggml-vulkan.dll')) {
    throw 'CPU worker package unexpectedly contains the Vulkan backend.'
}
if (Test-Path -LiteralPath (Join-Path $ReleaseDirectory 'work')) { throw 'Unrelated work directory was packaged.' }

Write-Host 'Release verification passed.'
