[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ProjectFile,

    [Parameter(Mandatory)]
    [string]$ExecutableName,

    [Parameter(Mandatory)]
    [string]$PackageBaseName,

    [Parameter(Mandatory)]
    [string]$DisplayName
)

$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$projectPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $ProjectFile))
$publishDirectory = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts\publish\win-x64"))
$artifactsDirectory = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts"))
$releaseDirectoryRoot = [System.IO.Path]::GetFullPath((Join-Path $artifactsDirectory "release"))

function Test-ChildPath {
    param(
        [string]$Candidate,
        [string]$Parent
    )

    $parentWithSeparator = $Parent.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    return $Candidate.StartsWith($parentWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)
}

if (-not (Test-ChildPath -Candidate $projectPath -Parent $repositoryRoot)) {
    throw "Project path escaped the repository root."
}

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Project file was not found: $projectPath"
}

[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$version = [string]($project.Project.PropertyGroup.Version | Select-Object -First 1)
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "The project does not contain a valid three-part Version."
}

& dotnet publish $projectPath -p:PublishProfile=win-x64
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$applicationDll = [System.IO.Path]::GetFileNameWithoutExtension($ExecutableName) + ".dll"
$applicationPri = [System.IO.Path]::GetFileNameWithoutExtension($ExecutableName) + ".pri"
$requiredFiles = @(
    $ExecutableName,
    $applicationDll,
    $applicationPri,
    "System.Private.CoreLib.dll",
    "coreclr.dll",
    "hostfxr.dll",
    "hostpolicy.dll",
    "Microsoft.UI.Xaml.dll",
    "Microsoft.WinUI.dll",
    "Microsoft.WindowsAppRuntime.dll",
    "Microsoft.WindowsAppRuntime.Bootstrap.dll",
    "Microsoft.WindowsAppRuntime.Bootstrap.Net.dll",
    "WinRT.Runtime.dll"
)

foreach ($requiredFile in $requiredFiles) {
    $requiredPath = Join-Path $publishDirectory $requiredFile
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf) -or
        (Get-Item -LiteralPath $requiredPath).Length -eq 0) {
        throw "Published runtime is incomplete. Missing or empty file: $requiredFile"
    }
}

$publishedVersion = (Get-Item -LiteralPath (Join-Path $publishDirectory $applicationDll)).VersionInfo.FileVersion
if ($publishedVersion -ne "$version.0") {
    throw "Published binary version $publishedVersion does not match project version $version.0."
}

$releaseName = "$PackageBaseName-v$version-win-x64"
$releaseDirectory = [System.IO.Path]::GetFullPath((Join-Path $releaseDirectoryRoot $releaseName))
$applicationDirectory = Join-Path $releaseDirectory "app"
$zipPath = Join-Path $artifactsDirectory "$releaseName.zip"
$zipChecksumPath = "$zipPath.sha256"

if (-not (Test-ChildPath -Candidate $releaseDirectory -Parent $releaseDirectoryRoot)) {
    throw "Release path escaped the artifacts release directory."
}

New-Item -ItemType Directory -Path $releaseDirectoryRoot -Force | Out-Null
if (Test-Path -LiteralPath $releaseDirectory) {
    Remove-Item -LiteralPath $releaseDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $applicationDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $publishDirectory "*") -Destination $applicationDirectory -Recurse -Force

$launcherName = "START $DisplayName.cmd"
$launcherPath = Join-Path $releaseDirectory $launcherName
$launcherChecks = ($requiredFiles | ForEach-Object {
    'if not exist "%APPDIR%\' + $_ + '" goto incomplete'
}) -join [Environment]::NewLine
$launcher = @"
@echo off
setlocal
set "APPDIR=%~dp0app"
$launcherChecks
pushd "%APPDIR%"
start "" "$ExecutableName"
popd
exit /b 0

:incomplete
echo.
echo This $DisplayName package is incomplete.
echo Extract the complete release ZIP to a new folder. Do not copy only the EXE.
echo If the problem continues, run VERIFY PACKAGE.cmd and send its output to support.
echo.
pause
exit /b 2
"@
Set-Content -LiteralPath $launcherPath -Value $launcher -Encoding ascii

$readme = @"
$DisplayName $version - portable Windows x64 release

1. Extract the complete ZIP to a new local folder.
2. Run "$launcherName" from this folder.
3. Keep the entire app folder beside the launcher. Never copy the EXE by itself.
4. If startup reports a missing file, run "VERIFY PACKAGE.cmd".

This build contains its own .NET and Windows App SDK runtimes. Installing a separate
.NET runtime or Windows App Runtime is not required.
"@
Set-Content -LiteralPath (Join-Path $releaseDirectory "README FIRST.txt") -Value $readme -Encoding utf8

$verifyScript = @'
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$manifest = Join-Path $root "SHA256SUMS.txt"
$failures = [System.Collections.Generic.List[string]]::new()
foreach ($line in Get-Content -LiteralPath $manifest) {
    if ($line -notmatch '^([0-9a-f]{64})  (.+)$') {
        $failures.Add("Invalid manifest line: $line")
        continue
    }
    $expected = $Matches[1]
    $relativePath = $Matches[2]
    $path = Join-Path $root $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $failures.Add("Missing: $relativePath")
        continue
    }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        $failures.Add("Changed or damaged: $relativePath")
    }
}
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    Write-Host "Package verification failed. Extract a fresh copy of the complete ZIP." -ForegroundColor Red
    exit 2
}
Write-Host "Package verification passed. All published files are present and unchanged." -ForegroundColor Green
'@
Set-Content -LiteralPath (Join-Path $releaseDirectory "Verify-Package.ps1") -Value $verifyScript -Encoding utf8
$verifyLauncher = @'
@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Verify-Package.ps1"
echo.
pause
'@
Set-Content -LiteralPath (Join-Path $releaseDirectory "VERIFY PACKAGE.cmd") -Value $verifyLauncher -Encoding ascii

$manifestPath = Join-Path $releaseDirectory "SHA256SUMS.txt"
$filesToHash = Get-ChildItem -LiteralPath $releaseDirectory -Recurse -File |
    Where-Object { $_.FullName -ne $manifestPath } |
    Sort-Object FullName
$manifestLines = foreach ($file in $filesToHash) {
    $relativePath = $file.FullName.Substring($releaseDirectory.Length).TrimStart('\', '/').Replace('\', '/')
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $relativePath"
}
Set-Content -LiteralPath $manifestPath -Value $manifestLines -Encoding ascii

& powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $releaseDirectory "Verify-Package.ps1")
if ($LASTEXITCODE -ne 0) {
    throw "Staged release verification failed."
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $releaseDirectory "*") -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$zipHash  $releaseName.zip" | Set-Content -LiteralPath $zipChecksumPath -Encoding ascii

[pscustomobject]@{
    Version = $version
    FileVersion = $publishedVersion
    PublishedDirectory = $publishDirectory
    ReleaseDirectory = $releaseDirectory
    Archive = $zipPath
    ArchiveBytes = (Get-Item -LiteralPath $zipPath).Length
    Sha256 = $zipHash
}
