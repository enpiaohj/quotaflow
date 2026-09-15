# release.ps1 -- Build a versioned, single-file, self-contained release snapshot.
#
# Produces:
#   publish/win-x64/QuotaFlow-vX.Y.Z-win-x64.exe        (versioned single-file exe)
#   release/vX.Y.Z/QuotaFlow-vX.Y.Z-win-x64.exe         (same exe, staged into the release dir)
#   release/vX.Y.Z/QuotaFlow-vX.Y.Z-win-x64.exe.sha256  (sha256 of the exe)
#   release/vX.Y.Z/QuotaFlow-vX.Y.Z-win-x64.zip         (zip containing only the exe)
#   release/vX.Y.Z/QuotaFlow-vX.Y.Z-win-x64.zip.sha256  (sha256 of the zip)
#
# Usage:
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/release.ps1
#
# Note: keep this file ASCII-only. Windows PowerShell 5.1 reads .ps1 as ANSI
# unless the file has a UTF-8 BOM; non-ASCII literals would be mangled.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'QuotaFlow.Windows.App\QuotaFlow.Windows.App.csproj'
$rid = 'win-x64'
$publishDir = Join-Path $root "publish\$rid"

# --- read <Version> from the app csproj ---
$xml = New-Object System.Xml.XmlDocument
$xml.Load($project)
# SDK-style csproj has no xmlns -> elements live in the empty namespace, so plain //Version matches.
$version = $xml.SelectSingleNode('//Version').InnerText
if (-not $version) { throw 'Version element not found in app csproj' }
Write-Output "Version: $version"

$exeName = "QuotaFlow-v$version-$rid.exe"
$zipName = "QuotaFlow-v$version-$rid.zip"
$releaseDir = Join-Path $root "release\v$version"
$releaseExePath = Join-Path $releaseDir $exeName
$zipPath = Join-Path $releaseDir $zipName

function Write-Sha256Sidecar {
    param([string]$FilePath)
    $stream = [System.IO.File]::OpenRead($FilePath)
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $bytes = $sha.ComputeHash($stream)
        } finally { $sha.Dispose() }
    } finally { $stream.Dispose() }
    $hash = ([System.BitConverter]::ToString($bytes)).Replace('-', '').ToLower()
    Set-Content -Path "$FilePath.sha256" -Value "$hash *$(Split-Path $FilePath -Leaf)" -Encoding ASCII
    return $hash
}

# --- publish (single-file, self-contained, compressed, natives inlined) ---
& dotnet publish $project -c Release -r $rid --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# --- safety: the publish output must contain no configs/caches/credentials/logs ---
$forbidden = @('settings.json', 'cache.json', '.credentials.json', '*.log', '*.pdb')
foreach ($pat in $forbidden) {
    $hit = Get-ChildItem -Path $publishDir -Recurse -Filter $pat -ErrorAction SilentlyContinue
    if ($hit) { throw "Forbidden artifact in publish output: $($hit.FullName)" }
}

# --- stage the versioned exe ---
$plainExe = Join-Path $publishDir 'QuotaFlow.exe'
if (-not (Test-Path $plainExe)) { throw "Expected publish output $plainExe not found" }
$versionedExe = Join-Path $publishDir $exeName
Copy-Item $plainExe $versionedExe -Force

# --- stage both the bare exe and a zip into the release dir (sha256 file uses .NET directly;
#     Get-FileHash is unavailable in some PS environments) ---
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

Copy-Item $versionedExe $releaseExePath -Force
$exeHash = Write-Sha256Sidecar -FilePath $releaseExePath
Write-Output "WROTE: $releaseExePath"
Write-Output "SHA256: $exeHash"

Compress-Archive -Path $versionedExe -DestinationPath $zipPath -Force
$zipHash = Write-Sha256Sidecar -FilePath $zipPath
Write-Output "WROTE: $zipPath"
Write-Output "SHA256: $zipHash"
