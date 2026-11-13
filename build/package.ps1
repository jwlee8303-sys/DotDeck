<#
.SYNOPSIS
  Builds the Windows release: self-contained folder publish -> Velopack package + machine-wide MSI -> setup EXE.

.DESCRIPTION
  Output (<OutputDir>\releases):
    DotDeck-Setup.exe             first install: folder (default Program Files\DotDeck), autostart choice, one UAC
                                  prompt, then the embedded per-machine MSI
    DotDeck-<v>-full.nupkg        update package (+ -delta.nupkg when a previous release was found)
    releases.win.json, assets.win.json, RELEASES   update feed index
    DotDeck-win-Portable.zip      portable build
  upload-assets.txt lists the files to attach to the GitHub Release. Velopack's own per-user Setup.exe and the bare
  MSI are left out: the first-install link is DotDeck-Setup.exe.
  The app is published as a folder (DotDeck.exe + DLLs + runtime, never single-file) into an empty app\ folder that
  is created fresh for every run; only that folder is packaged.

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

# 1. App: self-contained folder publish (separate DLLs and runtime) into the empty app\ folder created above.
Invoke-Checked dotnet @('publish', 'src/DotDeck.App', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=false', "-p:Version=$Version", '-o', $appDir)
foreach ($required in 'DotDeck.exe', 'DotDeck.dll', 'DotDeck.Core.dll', 'Velopack.dll', 'coreclr.dll', 'PresentationFramework.dll') {
    if (-not (Test-Path (Join-Path $appDir $required))) { throw "Publish output is missing $required (single-file or framework-dependent build?)" }
}
$published = (Get-Item (Join-Path $appDir 'DotDeck.Core.dll')).VersionInfo.ProductVersion
if (-not $published.StartsWith($Version)) { throw "DotDeck.Core.dll version $published does not match $Version" }

# 2. Release notes: the CHANGELOG section of this version (stable versions only; test builds get a short line).
$notes = Join-Path $out 'release-notes.md'
$base = $Version.Split('-')[0]
$section = [regex]::Match((Get-Content -Raw -Encoding utf8 CHANGELOG.md), "(?ms)^## \[$([regex]::Escape($base))\][^\n]*\n(.*?)(?=^## \[|\z)")
$text = if ($section.Success -and $Version -eq $base) { $section.Groups[1].Value.Trim() } else { "DotDeck $Version" }
[IO.File]::WriteAllText($notes, $text + "`n", [Text.UTF8Encoding]::new($false))
$install = @"
## 설치
**[$PackId-Setup.exe]($RepoUrl/releases/download/v$Version/$PackId-Setup.exe)**를 내려받아 더블클릭하세요. 설치 폴더(기본 ``C:\Program Files\$PackId``)와 'Windows 로그인 시 자동 실행'(기본 선택, 현재 Windows 계정에만 적용)을 확인하고 [설치]를 누르면 Windows 관리자 권한 확인(UAC) 창이 열립니다. 승인하면 이 PC의 모든 사용자용으로 설치되고 바탕화면·시작 메뉴 바로가기가 만들어지며, DotDeck은 일반 권한으로 실행됩니다. 취소하면 아무것도 설치하지 않고 끝납니다. .NET 런타임 설치는 필요하지 않습니다.

- **업데이트**: 앱이 시작할 때 새 버전을 확인합니다(도움말 > 업데이트 확인). [지금 업데이트]를 누르면 관리자 권한 확인 후 원래 설치 폴더에 적용하고 일반 권한으로 다시 시작합니다. 자동 실행 선택은 유지됩니다.
- **제거**: Windows 설정 > 앱 > 설치된 앱에서 DotDeck을 제거합니다. 프로그램 파일·바로가기·자동 실행 등록은 정리되고, 설정과 작업물(``%APPDATA%\$PackId``, ``%LOCALAPPDATA%\$PackId``)은 남습니다.
- **기존 설치 사용자(1.5.x, 사용자용 설치)**: 새 $PackId-Setup.exe를 실행하세요. 예전 설치의 프로그램 파일·제거 항목·바로가기만 정리하고 설정·백업·세션은 그대로 둡니다. 예전 설치를 Windows 설정에서 먼저 제거하지 마세요(예전 제거기는 ``%LOCALAPPDATA%\$PackId`` 폴더 전체를 지울 수 있습니다). 앱 안의 자동 업데이트를 받아도 동작하지만, 그 경우 사용자용 설치로 남습니다.
- **기존 ZIP 사용자(1.4.1 이하 단일 EXE)**: $PackId-Setup.exe로 설치한 뒤 예전 압축 해제 폴더를 지우세요. 설정은 같은 위치에 있어 그대로 이어집니다.

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

# 4. Velopack package, feed index, per-machine MSI (Program Files, HKLM uninstall entry, desktop + Start menu
#    shortcuts to the root DotDeck.exe launcher) and portable ZIP.
Invoke-Checked dotnet @('vpk', 'pack', '--packId', $PackId, '--packVersion', $Version, '--packDir', $appDir,
    '--mainExe', 'DotDeck.exe', '--packTitle', 'DotDeck', '--packAuthors', 'DotDeck', '--runtime', 'win-x64',
    '--channel', 'win', '--shortcuts', 'Desktop,StartMenuRoot', '--msi', '--instLocation', 'PerMachine',
    '--releaseNotes', $notes, '--outputDir', $releases)

# 4b. Keep user data on uninstall. Velopack's MSI cleanup action (RustCleanup) deletes %LOCALAPPDATA%\<RustAppId>
#     on uninstall and on every MSI upgrade; RustAppId is the pack title "DotDeck", i.e. DotDeck's own settings,
#     backups and sessions. Point it at a folder nothing uses (it is read only by that action). The app's uninstall
#     hook (UninstallCleanup) removes the update cache that Velopack keeps there instead.
$msi = Join-Path $releases "$PackId-win.msi"
$cleanupId = "$PackId.msi-cleanup-unused"
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class MsiSql {
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiOpenDatabaseW(string path, IntPtr persist, out IntPtr db);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiDatabaseOpenViewW(IntPtr db, string query, out IntPtr view);
    [DllImport("msi.dll")] static extern uint MsiViewExecute(IntPtr view, IntPtr record);
    [DllImport("msi.dll")] static extern uint MsiViewFetch(IntPtr view, out IntPtr record);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiRecordGetStringW(IntPtr record, uint field, StringBuilder value, ref uint length);
    [DllImport("msi.dll")] static extern uint MsiDatabaseCommit(IntPtr db);
    [DllImport("msi.dll")] static extern uint MsiCloseHandle(IntPtr handle);
    static void Check(uint code, string what) { if (code != 0) throw new InvalidOperationException(what + " failed: " + code); }
    // Runs one query; returns the first column of the first row (null for none), committing when writable.
    public static string Run(string path, string query, bool write) {
        IntPtr db, view, record;
        Check(MsiOpenDatabaseW(path, new IntPtr(write ? 1 : 0), out db), "MsiOpenDatabase");
        try {
            Check(MsiDatabaseOpenViewW(db, query, out view), "MsiDatabaseOpenView");
            try {
                Check(MsiViewExecute(view, IntPtr.Zero), "MsiViewExecute");
                string value = null;
                if (!write && MsiViewFetch(view, out record) == 0) {
                    var buffer = new StringBuilder(4096); uint length = 4096;
                    Check(MsiRecordGetStringW(record, 1, buffer, ref length), "MsiRecordGetString");
                    value = buffer.ToString();
                    MsiCloseHandle(record);
                }
                if (write) Check(MsiDatabaseCommit(db), "MsiDatabaseCommit");
                return value;
            } finally { MsiCloseHandle(view); }
        } finally { MsiCloseHandle(db); }
    }
}
'@
$cleanupData = [MsiSql]::Run($msi, "SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action``='SetRustCleanupData'", $false)
if ($cleanupData -ne '[INSTALLFOLDER]"[RustAppId]"[TempFolder]') { throw "Unexpected Velopack MSI cleanup data '$cleanupData': check RustAppId handling before releasing." }
[void][MsiSql]::Run($msi, "UPDATE ``Property`` SET ``Value``='$cleanupId' WHERE ``Property``='RustAppId'", $true)
$appId = [MsiSql]::Run($msi, "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='RustAppId'", $false)
if ($appId -ne $cleanupId) { throw "MSI RustAppId is '$appId', expected '$cleanupId'" }
Write-Host "MSI cleanup no longer targets %LOCALAPPDATA%\$PackId (RustAppId=$appId)"

# 5. DotDeck-Setup.exe: folder/autostart choice that embeds the MSI.
$installerOut = Join-Path $out 'installer'
Invoke-Checked dotnet @('build', 'src/DotDeck.Installer', '-c', 'Release', '--no-incremental', "-p:SetupMsi=$msi", "-p:PackId=$PackId",
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
