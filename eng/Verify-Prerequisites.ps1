[CmdletBinding()]
param(
    [switch]$PortableOnly
)

$missing = [System.Collections.Generic.List[string]]::new()

$requiredCommands = if ($PortableOnly) { @('dotnet') } else { @('dotnet', 'cmake') }

foreach ($command in $requiredCommands) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        $missing.Add($command)
    }
}

if (-not $PortableOnly) {
    $zoomSdkDirectory = [Environment]::GetEnvironmentVariable('ZOOM_MEETING_SDK_DIR')
    if ([string]::IsNullOrWhiteSpace($zoomSdkDirectory) -or
        -not (Test-Path -LiteralPath $zoomSdkDirectory -PathType Container)) {
        $missing.Add('ZOOM_MEETING_SDK_DIR')
    }
}

if ($missing.Count -gt 0) {
    Write-Error ("Missing prerequisites: {0}" -f ($missing -join ', '))
    exit 1
}

Write-Output 'All requested prerequisites are available.'
