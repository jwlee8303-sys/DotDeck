<#
.SYNOPSIS
  Imports the file/folder icons (Material Icon Theme, MIT) into src/DotDeck.App/Assets/FileIcons.

.DESCRIPTION
  1. Downloads the pinned npm package material-icon-theme and checks its sha512 integrity.
  2. Converts every SVG (plus DotDeck's own icons in Assets/FileIcons/dotdeck/*.svg) to WPF DrawingImage XAML with
     build/icons/IconGen. Icons listed in build/icons/file-icon-exclude.json (third-party logos) and icons that use
     unsupported SVG features are replaced by a generic file/folder icon in the same color family.
  3. Copies the mapping JSON and LICENSE, and writes SOURCE.md (version, integrity, import date, conversion report).

  Re-running with the same version gives the same files (SOURCE.md keeps its import date while version and integrity
  are unchanged). Needs the .NET SDK and tar.exe (Windows 10+). Material's SVG sources are not committed.

  To upgrade: change $Version and $Integrity (npm view material-icon-theme@<v> dist.integrity), run, review the diff
  and the report in SOURCE.md, and update THIRD-PARTY-NOTICES.txt.

.EXAMPLE
  pwsh -NoProfile -File build/icons/update-file-icons.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Version = '5.40.0'
$Integrity = 'sha512-IrvgusbwtqxhUtVgUy0nToJcfN7CYVRdbBxKMfbo0vab9flVBHA5INZ9UiE5SgRj+PM6opCl5nn5TZdpm2UaxA=='
$Repository = 'https://github.com/material-extensions/vscode-material-icon-theme'

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$assets = Join-Path $root 'src/DotDeck.App/Assets/FileIcons'
$iconsOut = Join-Path $assets 'icons'
$generator = Join-Path $PSScriptRoot 'IconGen/DotDeck.IconGen.csproj'
$exclude = Join-Path $PSScriptRoot 'file-icon-exclude.json'
$utf8 = New-Object System.Text.UTF8Encoding $false

# Work in a fresh, empty folder: the package is untrusted input and is only read, never executed.
$work = Join-Path ([IO.Path]::GetTempPath()) ("dotdeck-file-icons-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $tgz = Join-Path $work 'package.tgz'
    Write-Host "Downloading material-icon-theme@$Version"
    Invoke-WebRequest -UseBasicParsing -Uri "https://registry.npmjs.org/material-icon-theme/-/material-icon-theme-$Version.tgz" -OutFile $tgz

    $sha = [System.Security.Cryptography.SHA512]::Create()
    try {
        $stream = [IO.File]::OpenRead($tgz)
        try { $actual = 'sha512-' + [Convert]::ToBase64String($sha.ComputeHash($stream)) } finally { $stream.Dispose() }
    } finally { $sha.Dispose() }
    if ($actual -cne $Integrity) {
        throw "Integrity mismatch for material-icon-theme@${Version}: expected $Integrity, got $actual"
    }

    $extract = Join-Path $work 'x'
    New-Item -ItemType Directory -Path $extract | Out-Null
    # Windows' bsdtar, not another tar.exe that may be first on PATH (e.g. Git's, which misreads "C:\" paths).
    & (Join-Path $env:SystemRoot 'System32/tar.exe') -xzf $tgz -C $extract
    if ($LASTEXITCODE -ne 0) { throw "tar failed ($LASTEXITCODE)" }
    # npm tarballs hold plain files only; a link could make the generator read outside the package.
    $links = @(Get-ChildItem -LiteralPath $extract -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
    if ($links.Count -gt 0) { throw "Package contains links: $($links[0].FullName)" }
    $package = Join-Path $extract 'package'
    $manifest = Join-Path $package 'dist/material-icons.json'
    $license = Join-Path $package 'LICENSE'
    foreach ($required in $manifest, $license, (Join-Path $package 'icons')) {
        if (-not (Test-Path -LiteralPath $required)) { throw "Package layout changed: $required not found" }
    }

    # The generator deletes stale *.xaml only inside this folder; refuse anything else.
    $resolvedOut = [IO.Path]::GetFullPath($iconsOut)
    if (-not $resolvedOut.StartsWith([IO.Path]::GetFullPath($assets) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected output folder $resolvedOut"
    }
    # The generator writes and deletes there: refuse a junction or symlink anywhere on the way from the repository.
    for ($dir = $resolvedOut; $dir.Length -gt $root.Length; $dir = Split-Path -Parent $dir) {
        if ((Test-Path -LiteralPath $dir) -and ((Get-Item -LiteralPath $dir -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Output path contains a link: $dir"
        }
    }

    $report = Join-Path $work 'report.md'
    Write-Host 'Converting icons'
    & dotnet run --project $generator -c Release -- generate `
        --icons (Join-Path $package 'icons') --manifest $manifest --exclude $exclude `
        --extra (Join-Path $assets 'dotdeck') --out $resolvedOut --report $report
    if ($LASTEXITCODE -ne 0) { throw "IconGen failed ($LASTEXITCODE)" }

    function Copy-IfChanged([string]$From, [string]$To) {
        $bytes = [IO.File]::ReadAllBytes($From)
        if (-not (Test-Path -LiteralPath $To) -or [Convert]::ToBase64String([IO.File]::ReadAllBytes($To)) -cne [Convert]::ToBase64String($bytes)) {
            [IO.File]::WriteAllBytes($To, $bytes)
        }
    }

    Copy-IfChanged $manifest (Join-Path $assets 'material-icons.json')
    Copy-IfChanged $license (Join-Path $assets 'LICENSE-material-icon-theme.txt')

    # Keep the import date while the pinned package is unchanged, so re-runs are byte-identical.
    $sourceFile = Join-Path $assets 'SOURCE.md'
    $date = (Get-Date).ToString('yyyy-MM-dd')
    if (Test-Path -LiteralPath $sourceFile) {
        $old = [IO.File]::ReadAllText($sourceFile)
        if ($old.Contains("``$Version``") -and $old.Contains($Integrity) -and $old -match '가져온 날짜: (\d{4}-\d{2}-\d{2})') {
            $date = $Matches[1]
        }
    }

    $source = @"
# 파일 아이콘 원본

이 폴더는 ``build/icons/update-file-icons.ps1``이 생성한다. 직접 고치지 않는다(DotDeck 전용 원본 ``dotdeck/*.svg``와 ``dotdeck-icons.json`` 제외).

- 원본: Material Icon Theme — $Repository
- 패키지: npm ``material-icon-theme`` ``$Version``
- 무결성: ``$Integrity``
- 라이선스: MIT (``LICENSE-material-icon-theme.txt``)
- 가져온 날짜: $date
- 브랜드 제외 목록: ``build/icons/file-icon-exclude.json``

## 변환 결과

"@ + "`n" + [IO.File]::ReadAllText($report)
    $source = $source.Replace("`r`n", "`n")
    if (-not (Test-Path -LiteralPath $sourceFile) -or [IO.File]::ReadAllText($sourceFile) -cne $source) {
        [IO.File]::WriteAllText($sourceFile, $source, $utf8)
    }

    Write-Host "Done: $assets"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
