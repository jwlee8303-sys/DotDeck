<#
.SYNOPSIS
  Builds the Windows release: self-contained publish -> Velopack package -> folder-choice setup EXE.

.DESCRIPTION
  Output (<OutputDir>\releases):
    DotDeck-Setup.exe             first install (folder choice, then the embedded Velopack setup)
    DotDeck-<v>-full.nupkg        update package (+ -delta.nupkg when a previous release was found)
    releases.win.json, assets.win.json, RELEASES   update feed index
    DotDeck-win-Portable.zip      portable build
  upload-assets.txt lists the files to attach to the GitHub Release (Velopack's bare Setup.exe is left out:
  the first-install link is DotDeck-Setup.exe).

.EXAMPLE
  ./build/package.ps1                                  # version from Directory.Build.props, delta from GitHub
  ./build/package.ps1 -Version 1.5.0-test1 -PackId DotDeckTest -PreviousFeed none -OutputDir publish/test1
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$PackId = 'DotDeck',
    [string]$OutputDir,
    # 'github' (published releases), 'none', or a local release folder used for delta packages.
    [string]$PreviousFeed = 'github',
    [string]$RepoUrl = 'https://github.com/jwlee8303-sys/DotDeck'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File failed ($LASTEXITCODE): $($Arguments -join ' ')" }
}

if (-not $Version) {
    $Version = ([xml](Get-Content -Raw Directory.Build.props)).Project.PropertyGroup.Version | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Invalid version: $Version" }
if (-not $OutputDir) { $OutputDir = "publish/release/$Version" }
$out = [IO.Path]::GetFullPath((Join-Path $root $OutputDir))
# The folder is deleted below: only ever below publish\ (ignored by git).
$publishRoot = [IO.Path]::GetFullPath((Join-Path $root 'publish')) + [IO.Path]::DirectorySeparatorChar
if (-not $out.StartsWith($publishRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "OutputDir must be inside $publishRoot" }
$appDir = Join-Path $out 'app'
$releases = Join-Path $out 'releases'
# Always from scratch: vpk refuses a version already in the folder, and a stale feed index must never be reused.
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force -Path $releases | Out-Null

Write-Host "== DotDeck $Version ($PackId) -> $out"
Invoke-Checked dotnet @('tool', 'restore')

# 1. App: the same single-file, self-contained build as before.
Invoke-Checked dotnet @('publish', 'src/DotDeck.App', '-c', 'Release', '-r', 'win-x64', '-p:PublishSingleFile=true',
    "-p:Version=$Version", '-o', $appDir)

# 2. Release notes: the CHANGELOG section of this version (stable versions only; test builds get a short line).
$notes = Join-Path $out 'release-notes.md'
$base = $Version.Split('-')[0]
$section = [regex]::Match((Get-Content -Raw -Encoding utf8 CHANGELOG.md), "(?ms)^## \[$([regex]::Escape($base))\][^\n]*\n(.*?)(?=^## \[|\z)")
$text = if ($section.Success -and $Version -eq $base) { $section.Groups[1].Value.Trim() } else { "DotDeck $Version" }
[IO.File]::WriteAllText($notes, $text + "`n", [Text.UTF8Encoding]::new($false))
$install = @"
## 설치
**$PackId-Setup.exe**를 내려받아 실행하고 설치 폴더(기본 ``%LocalAppData%\$PackId``)를 고른 뒤 [설치]를 누르세요. 관리자 권한과 .NET 런타임 설치는 필요하지 않습니다. 설치한 앱은 시작할 때 새 버전을 확인하며, 도움말 > 업데이트 확인으로 직접 확인할 수도 있습니다.

설치 없이 쓰려면 **$PackId-win-Portable.zip**을 압축 해제해 실행하세요. 파일 검증값은 ``SHA256SUMS.txt``에 있습니다.

"@
[IO.File]::WriteAllText((Join-Path $out 'github-release-notes.md'), $install + $text + "`n", [Text.UTF8Encoding]::new($false))

# 3. Previous release for a delta package (absent on the first release: then only the full package).
switch ($PreviousFeed) {
    'none' { }
    'github' {
        & dotnet vpk download github --repoUrl $RepoUrl --channel win -o $releases
        if ($LASTEXITCODE -ne 0) { Write-Warning 'No previous Velopack release on GitHub: full package only.' }
    }
    default {
        Get-ChildItem $PreviousFeed -File | Where-Object { $_.Name -like '*.nupkg' -or $_.Name -like 'releases.*.json' } |
            Copy-Item -Destination $releases -Force
    }
}

# 4. Velopack package, feed index, Setup.exe and portable ZIP.
Invoke-Checked dotnet @('vpk', 'pack', '--packId', $PackId, '--packVersion', $Version, '--packDir', $appDir,
    '--mainExe', 'DotDeck.exe', '--packTitle', 'DotDeck', '--packAuthors', 'DotDeck', '--runtime', 'win-x64',
    '--channel', 'win', '--releaseNotes', $notes, '--outputDir', $releases)

# 5. Folder-choice setup that embeds Velopack's Setup.exe.
$setup = Join-Path $releases "$PackId-win-Setup.exe"
$installerOut = Join-Path $out 'installer'
Invoke-Checked dotnet @('build', 'src/DotDeck.Installer', '-c', 'Release', '--no-incremental', "-p:SetupExe=$setup", "-p:PackId=$PackId",
    "-p:Version=$Version", '-o', $installerOut)
$firstInstall = Join-Path $releases "$PackId-Setup.exe"
Copy-Item (Join-Path $installerOut 'DotDeck-Setup.exe') $firstInstall -Force

# 6. Assets for this version only (older nupkgs downloaded for the delta stay out).
$assets = @(
    $firstInstall,
    (Join-Path $releases "$PackId-$Version-full.nupkg"),
    (Join-Path $releases "$PackId-$Version-delta.nupkg"),
    (Join-Path $releases 'releases.win.json'),
    (Join-Path $releases 'assets.win.json'),
    (Join-Path $releases 'RELEASES'),
    (Join-Path $releases "$PackId-win-Portable.zip")
) | Where-Object { Test-Path $_ }
$assets | Set-Content -Encoding utf8 (Join-Path $out 'upload-assets.txt')
# LF line endings so `sha256sum -c` works on Linux/macOS as well as Get-FileHash on Windows.
$sums = Get-FileHash -Algorithm SHA256 $assets | ForEach-Object { "$($_.Hash.ToLower())  $(Split-Path -Leaf $_.Path)`n" }
[IO.File]::WriteAllText((Join-Path $releases 'SHA256SUMS.txt'), (-join $sums), [Text.Encoding]::ASCII)
Add-Content -Encoding utf8 (Join-Path $out 'upload-assets.txt') (Join-Path $releases 'SHA256SUMS.txt')

Write-Host "== Assets:"
Get-Content (Join-Path $out 'upload-assets.txt') | ForEach-Object { Write-Host "   $_" }
