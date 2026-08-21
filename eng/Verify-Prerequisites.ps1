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
    $zoomCandidates = @(
        (Join-Path $env:APPDATA 'Zoom\bin\Zoom.exe'),
        (Join-Path $env:ProgramFiles 'Zoom\bin\Zoom.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Zoom\bin\Zoom.exe')
    )
    if (-not ($zoomCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })) {
        $missing.Add('Zoom Workplace')
    }
}

if ($missing.Count -gt 0) {
    Write-Error ("Missing prerequisites: {0}" -f ($missing -join ', '))
    exit 1
}

Write-Output 'All requested prerequisites are available.'
