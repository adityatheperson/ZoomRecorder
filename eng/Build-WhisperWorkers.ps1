param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$DependencyCache,
    [string]$VulkanSdk,
    [string]$BuildCache
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($DependencyCache)) {
    $DependencyCache = Join-Path $repositoryRoot 'artifacts\whisper.cpp'
}
$sourceDirectory = [IO.Path]::GetFullPath($DependencyCache)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if ([string]::IsNullOrWhiteSpace($BuildCache)) {
    $BuildCache = Join-Path (Split-Path -Parent $outputRoot) 'whisper-build'
}
$buildRoot = [IO.Path]::GetFullPath($BuildCache)
$versionLines = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'whisper.cpp.version'))
if ($versionLines.Count -ne 2 -or $versionLines[0] -ne 'tag=v1.9.1' -or $versionLines[1] -ne 'commit=f049fff') {
    throw 'eng/whisper.cpp.version must contain the approved tag and commit exactly.'
}
$tag = $versionLines[0].Substring(4)
$commit = $versionLines[1].Substring(7)

if (-not [string]::IsNullOrWhiteSpace($VulkanSdk)) {
    $resolvedVulkanSdk = [IO.Path]::GetFullPath($VulkanSdk)
    if (-not (Test-Path -LiteralPath (Join-Path $resolvedVulkanSdk 'Bin\glslc.exe')) -or
        -not (Test-Path -LiteralPath (Join-Path $resolvedVulkanSdk 'Include\vulkan\vulkan.h'))) {
        throw 'The supplied Vulkan SDK does not contain glslc and Vulkan headers.'
    }
    $env:VULKAN_SDK = $resolvedVulkanSdk
}

$cmakeCommand = Get-Command cmake.exe -ErrorAction SilentlyContinue
$cmake = if ($null -ne $cmakeCommand) { $cmakeCommand.Source } else { $null }
if ([string]::IsNullOrWhiteSpace($cmake)) {
    $cmake = 'C:\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
}
if (-not (Test-Path -LiteralPath $cmake)) { throw 'CMake was not found.' }
if (-not (Get-Command git.exe -ErrorAction SilentlyContinue)) { throw 'Git was not found.' }

if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory '.git'))) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $sourceDirectory) | Out-Null
    & git clone --filter=blob:none --no-checkout https://github.com/ggerganov/whisper.cpp.git $sourceDirectory
    if ($LASTEXITCODE) { throw 'Unable to clone whisper.cpp.' }
}
& git -C $sourceDirectory fetch --force --depth=1 origin "refs/tags/$tag`:refs/tags/$tag"
if ($LASTEXITCODE) { throw "Unable to fetch whisper.cpp $tag." }
& git -C $sourceDirectory checkout --force --detach $tag
if ($LASTEXITCODE) { throw "Unable to check out whisper.cpp $tag." }
$head = (& git -C $sourceDirectory rev-parse HEAD).Trim()
if (-not $head.StartsWith($commit, [StringComparison]::OrdinalIgnoreCase)) {
    throw "whisper.cpp $tag resolved to $head instead of $commit."
}

function Build-Worker([string]$Name, [bool]$UseVulkan) {
    $buildDirectory = Join-Path $buildRoot $Name
    $vulkan = if ($UseVulkan) { 'ON' } else { 'OFF' }
    $vulkanArgument = "-DGGML_VULKAN=$vulkan"
    & $cmake -S $sourceDirectory -B $buildDirectory -G 'Visual Studio 17 2022' -A x64 `
        -DWHISPER_BUILD_EXAMPLES=ON -DWHISPER_BUILD_TESTS=OFF -DWHISPER_BUILD_SERVER=OFF `
        $vulkanArgument
    if ($LASTEXITCODE) { throw "Unable to configure the $Name whisper.cpp worker." }
    & $cmake --build $buildDirectory --config Release --target whisper-cli --parallel
    if ($LASTEXITCODE) { throw "Unable to build the $Name whisper.cpp worker." }

    $binaryDirectory = Join-Path $buildDirectory 'bin\Release'
    $required = @('whisper-cli.exe', 'whisper.dll', 'ggml.dll', 'ggml-base.dll', 'ggml-cpu.dll')
    if ($UseVulkan) { $required += 'ggml-vulkan.dll' }
    $missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $binaryDirectory $_)) })
    if ($missing.Count) { throw "$Name worker runtime artifacts are incomplete: $($missing -join ', ')" }

    $destination = Join-Path $outputRoot $Name
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    foreach ($file in $required) {
        Copy-Item -LiteralPath (Join-Path $binaryDirectory $file) -Destination (Join-Path $destination $file) -Force
    }

    $worker = Join-Path $destination 'whisper-cli.exe'
    $version = (& $worker --version 2>&1 | Out-String)
    $reportedVersion = $tag.TrimStart('v')
    if ($LASTEXITCODE -or ($version -notmatch [regex]::Escape($reportedVersion))) {
        throw "$Name worker did not report the pinned $tag version. Output: $version"
    }
    $help = (& $worker --help 2>&1 | Out-String)
    if ($LASTEXITCODE) { throw "$Name worker --help failed." }
    foreach ($option in @('--output-json-full', '--output-file', '--language', '--no-prints')) {
        if ($help -notmatch [regex]::Escape($option)) { throw "$Name worker does not advertise $option." }
    }
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
Build-Worker -Name 'vulkan' -UseVulkan $true
Build-Worker -Name 'cpu' -UseVulkan $false
Copy-Item -LiteralPath (Join-Path $sourceDirectory 'LICENSE') -Destination (Join-Path $outputRoot 'LICENSE-whisper.cpp') -Force
# v1.9.1 ships one repository-level MIT notice covering the ggml authors and bundled code.
Copy-Item -LiteralPath (Join-Path $sourceDirectory 'LICENSE') -Destination (Join-Path $outputRoot 'LICENSE-ggml') -Force
$allowedRelativePaths = @(
    'LICENSE-whisper.cpp', 'LICENSE-ggml',
    'vulkan\whisper-cli.exe', 'vulkan\whisper.dll', 'vulkan\ggml.dll',
    'vulkan\ggml-base.dll', 'vulkan\ggml-cpu.dll', 'vulkan\ggml-vulkan.dll',
    'cpu\whisper-cli.exe', 'cpu\whisper.dll', 'cpu\ggml.dll',
    'cpu\ggml-base.dll', 'cpu\ggml-cpu.dll'
)
$unexpected = @(Get-ChildItem -LiteralPath $outputRoot -Recurse -File | Where-Object {
    $relative = [IO.Path]::GetRelativePath($outputRoot, $_.FullName)
    $allowedRelativePaths -notcontains $relative
})
if ($unexpected.Count) { throw 'The worker staging directory contains unexpected runtime artifacts.' }
Write-Host "Pinned whisper.cpp workers staged at $outputRoot"
