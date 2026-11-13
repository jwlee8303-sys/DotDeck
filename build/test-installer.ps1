<#
.SYNOPSIS
  End-to-end installer test for a disposable Windows machine (GitHub Actions windows runner). Never run it on a PC
  you use: it installs, updates and uninstalls DotDeck machine-wide, creates a local user and changes UAC policy.

.DESCRIPTION
  Runs elevated (the runner). Every "user" step runs as a scheduled task with a LIMITED token in the interactive
  session — the same rights as double-clicking in Explorer — and its integrity level is recorded. UAC prompts are
  auto-approved for administrators (ConsentPromptBehaviorAdmin=0, the runner default) and auto-denied for standard
  users (ConsentPromptBehaviorUser=0, set here) to exercise the cancel path. The real UAC dialog is never shown.

  Scenarios: setup window cancel; 1.5.0 per-user install (LocalAppData) → in-app update → migration by the new setup;
  default install via the setup window; launch from shortcuts; in-app update A→B (UAC, restart with normal rights);
  uninstall the way Settings > Apps does; custom Korean/space path with autostart off, update and reinstall;
  1.5.0 per-user install in Program Files → migration; standard user refusing UAC.

.PARAMETER Work
  Folder holding A\DotDeck-Setup.exe, B\DotDeck-Setup.exe, feedA\, feedB\ and legacy\DotDeck-Setup.exe (1.5.0).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Work,
    [Parameter(Mandatory)] [string]$VersionA,
    [Parameter(Mandatory)] [string]$VersionB
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class E2EToken {
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int length, out int returned);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    // 1 elevated, 0 not elevated, negative: could not read.
    public static int Elevation(int pid) {
        IntPtr process = OpenProcess(0x1000, false, pid);
        if (process == IntPtr.Zero) return -1;
        IntPtr token;
        if (!OpenProcessToken(process, 8, out token)) { CloseHandle(process); return -2; }
        int value, returned;
        bool ok = GetTokenInformation(token, 20, out value, 4, out returned);
        CloseHandle(token); CloseHandle(process);
        return ok ? value : -3;
    }
}
'@

$Work = (Resolve-Path $Work).Path
$SetupA = Join-Path $Work 'A\DotDeck-Setup.exe'
$SetupB = Join-Path $Work 'B\DotDeck-Setup.exe'
$FeedA = Join-Path $Work 'feedA'
$FeedB = Join-Path $Work 'feedB'
$LegacySetup = Join-Path $Work 'legacy\Setup.exe'
$Jobs = Join-Path $Work 'jobs'
New-Item -ItemType Directory -Force $Jobs | Out-Null
# Limited-token jobs (and the standard user) read the setups and write logs here.
icacls $Work /grant '*S-1-1-0:(OI)(CI)F' | Out-Null

$ProgramFiles = [Environment]::GetFolderPath('ProgramFiles')
$DefaultDir = Join-Path $ProgramFiles 'DotDeck'
$CustomDir = 'C:\테스트 폴더\닷덱 앱'
$PublicDesktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
$CommonPrograms = [Environment]::GetFolderPath('CommonPrograms')
$UserDesktop = [Environment]::GetFolderPath('DesktopDirectory')
$UserPrograms = [Environment]::GetFolderPath('Programs')
$Startup = [Environment]::GetFolderPath('Startup')
$Roaming = Join-Path $env:APPDATA 'DotDeck'
$Local = Join-Path $env:LOCALAPPDATA 'DotDeck'
$ArpKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MSI:DotDeck'
$LegacyKey = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\DotDeck'
$Account = "$env:USERDOMAIN\$env:USERNAME"

$results = [System.Collections.Generic.List[object]]::new()
$failures = 0
function Check([string]$Scenario, [string]$Name, [bool]$Ok, [string]$Detail = '') {
    $results.Add([pscustomobject]@{ Scenario = $Scenario; Check = $Name; Result = $(if ($Ok) { 'PASS' } else { 'FAIL' }); Detail = $Detail })
    if (-not $Ok) { $script:failures++ }
    Write-Host ("[{0}] {1} / {2} {3}" -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Scenario, $Name, $Detail)
}

# ---------- user jobs (limited token, interactive session) ----------
$script:jobCount = 0
function Start-UserJob([string]$Body, [string]$User, [string]$Password) {
    $script:jobCount++
    $n = $script:jobCount
    $file = Join-Path $Jobs "job$n.ps1"
    $exit = Join-Path $Jobs "job$n.exit"
    $log = Join-Path $Jobs "job$n.log"
    $text = @'
$ErrorActionPreference = 'Continue'
$code = 0
(whoami) | Out-File -Encoding utf8 '__LOG__'
(whoami /groups | Select-String 'Mandatory Label') | ForEach-Object { $_.Line.Trim() } | Out-File -Append -Encoding utf8 '__LOG__'
try {
__BODY__
} catch { "ERROR: $_" | Out-File -Append -Encoding utf8 '__LOG__'; $code = 9999 }
Set-Content -Path '__EXIT__' -Value $code
'@
    $text = $text.Replace('__LOG__', $log).Replace('__EXIT__', $exit).Replace('__BODY__', $Body)
    [IO.File]::WriteAllText($file, $text, [Text.UTF8Encoding]::new($true))
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$file`""
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 30) -AllowStartIfOnBatteries
    if ($User) {
        Register-ScheduledTask -TaskName "dotdeck-e2e-$n" -Action $action -Settings $settings -User $User -Password $Password -RunLevel Limited -Force | Out-Null
    } else {
        $principal = New-ScheduledTaskPrincipal -UserId $Account -LogonType Interactive -RunLevel Limited
        Register-ScheduledTask -TaskName "dotdeck-e2e-$n" -Action $action -Settings $settings -Principal $principal -Force | Out-Null
    }
    Start-ScheduledTask -TaskName "dotdeck-e2e-$n"
    return $n
}

function Wait-UserJob([int]$n, [int]$TimeoutSec = 900) {
    $exit = Join-Path $Jobs "job$n.exit"
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while (-not (Test-Path $exit)) {
        if ((Get-Date) -gt $deadline) { throw "user job $n timed out" }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Milliseconds 200
    Unregister-ScheduledTask -TaskName "dotdeck-e2e-$n" -Confirm:$false -ErrorAction SilentlyContinue
    return [int](Get-Content $exit | Select-Object -First 1)
}

function Get-JobIntegrity([int]$n) {
    $line = Get-Content (Join-Path $Jobs "job$n.log") | Where-Object { $_ -match 'Mandatory Label' } | Select-Object -First 1
    if ($line -match 'Mandatory Label\\(\w+) Mandatory Level') { return $Matches[1] } else { return "?($line)" }
}

function Invoke-UserJob([string]$Body, [int]$TimeoutSec = 900) {
    $n = Start-UserJob $Body
    $code = Wait-UserJob $n $TimeoutSec
    return [pscustomobject]@{ Job = $n; ExitCode = $code; Integrity = (Get-JobIntegrity $n) }
}

# ---------- UI Automation ----------
$UIA = [Windows.Automation.AutomationElement]
function Find-Window([string]$Name, [int]$TimeoutSec = 60) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $condition = [Windows.Automation.PropertyCondition]::new($UIA::NameProperty, $Name)
    while ((Get-Date) -lt $deadline) {
        $window = $UIA::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $condition)
        if (-not $window) {
            # Owned dialogs can sit below their owner window.
            foreach ($top in $UIA::RootElement.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)) {
                if ($top.Current.Name -like 'DotDeck*') {
                    $window = $top.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
                    if ($window) { break }
                }
            }
        }
        if ($window) { return $window }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Find-Control($Window, [string]$Name) {
    $Window.FindFirst([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new($UIA::NameProperty, $Name))
}

function Invoke-Control($Element) {
    $Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}

# ---------- helpers ----------
function Get-DotDeckProcesses {
    @(Get-CimInstance Win32_Process -Filter "Name='DotDeck.exe'" | Where-Object { $_.ExecutablePath })
}

function Wait-DotDeck([string]$Root, [int]$TimeoutSec = 120) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $p = Get-DotDeckProcesses | Where-Object { $_.ExecutablePath -like "$Root\current\DotDeck.exe" } | Select-Object -First 1
        if ($p) { return $p }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Stop-DotDeck {
    foreach ($p in Get-DotDeckProcesses) {
        $proc = Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue
        if ($proc) { [void]$proc.CloseMainWindow(); [void]$proc.WaitForExit(15000); if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force } }
    }
    Start-Sleep 2
}

function Get-LinkTarget([string]$Link) {
    if (-not (Test-Path $Link)) { return $null }
    $shell = New-Object -ComObject WScript.Shell
    try { return $shell.CreateShortcut($Link).TargetPath } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}

function Get-AppVersion([string]$Root) {
    $dll = Join-Path $Root 'current\DotDeck.dll'
    if (Test-Path $dll) { return (Get-Item $dll).VersionInfo.ProductVersion.Split('+')[0] } else { return $null }
}

function Get-DataSnapshot {
    # Settings and user data (everything except Velopack's update cache) as "relative path|length|hash".
    $items = foreach ($root in $Roaming, $Local) {
        if (Test-Path $root) {
            Get-ChildItem $root -Recurse -File -Force | Where-Object {
                $rel = $_.FullName.Substring($root.Length + 1)
                -not ($rel -like 'packages\*' -or $rel -eq 'Update.exe' -or $rel -like 'WebView2\*' -or $rel -like 'logs\*')
            } | ForEach-Object { "{0}|{1}|{2}" -f $_.FullName, $_.Length, (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
        }
    }
    return @($items | Sort-Object)
}

function Add-Sentinels([string]$Tag) {
    New-Item -ItemType Directory -Force (Join-Path $Roaming 'templates'), (Join-Path $Local 'backups') | Out-Null
    Set-Content (Join-Path $Roaming "templates\e2e-$Tag.md") "user template $Tag"
    Set-Content (Join-Path $Local "backups\e2e-$Tag.bak") "user backup $Tag"
}

function Test-Shortcuts([string]$Scenario, [string]$Root, [bool]$AutoStart) {
    $launcher = Join-Path $Root 'DotDeck.exe'
    Check $Scenario 'desktop shortcut (all users) -> launcher' ((Get-LinkTarget "$PublicDesktop\DotDeck.lnk") -eq $launcher) (Get-LinkTarget "$PublicDesktop\DotDeck.lnk")
    Check $Scenario 'Start menu shortcut (all users) -> launcher' ((Get-LinkTarget "$CommonPrograms\DotDeck.lnk") -eq $launcher) (Get-LinkTarget "$CommonPrograms\DotDeck.lnk")
    $userLinks = @(Get-ChildItem $UserDesktop, $UserPrograms -Filter 'DotDeck*.lnk' -ErrorAction SilentlyContinue)
    Check $Scenario 'no duplicate per-user desktop/Start menu shortcuts' ($userLinks.Count -eq 0) ($userLinks.FullName -join ', ')
    $startupTarget = Get-LinkTarget "$Startup\DotDeck.lnk"
    if ($AutoStart) {
        Check $Scenario 'autostart shortcut (current user) -> launcher' ($startupTarget -eq $launcher) "$startupTarget"
    } else {
        Check $Scenario 'no autostart shortcut' ($null -eq $startupTarget) "$startupTarget"
    }
    $allStartup = @(Get-ChildItem $Startup, ([Environment]::GetFolderPath('CommonStartup')) -Filter '*.lnk' -ErrorAction SilentlyContinue | Where-Object { (Get-LinkTarget $_.FullName) -like "$Root\*" })
    Check $Scenario 'at most one autostart entry' ($allStartup.Count -le 1) ($allStartup.FullName -join ', ')
}

function Test-Installed([string]$Scenario, [string]$Root, [string]$Version, [bool]$AutoStart) {
    foreach ($file in 'DotDeck.exe', 'Update.exe', 'current\DotDeck.exe', 'current\DotDeck.dll', 'current\DotDeck.Core.dll', 'current\Velopack.dll', 'current\coreclr.dll') {
        Check $Scenario "file $file" (Test-Path (Join-Path $Root $file))
    }
    Check $Scenario 'app version' ((Get-AppVersion $Root) -eq $Version) "$(Get-AppVersion $Root)"
    $arp = Get-ItemProperty $ArpKey -ErrorAction SilentlyContinue
    Check $Scenario 'Installed apps entry (HKLM)' ($null -ne $arp -and $arp.DisplayName -eq 'DotDeck') "$($arp.DisplayName) $($arp.DisplayVersion) $($arp.UninstallString)"
    Check $Scenario 'Installed apps entry location' ($arp -and $arp.InstallLocation.TrimEnd('\') -eq $Root) "$($arp.InstallLocation)"
    Check $Scenario 'Installed apps entry version' ($arp -and $arp.DisplayVersion -eq $Version) "$($arp.DisplayVersion)"
    Test-Shortcuts $Scenario $Root $AutoStart
}

function Test-AppRunsAsUser([string]$Scenario, [string]$Root, [string]$What) {
    $p = Wait-DotDeck $Root 120
    Check $Scenario "$What starts DotDeck" ($null -ne $p) "$($p.ExecutablePath)"
    if ($p) {
        $elevation = [E2EToken]::Elevation([int]$p.ProcessId)
        Check $Scenario "$What runs with normal rights (not elevated)" ($elevation -eq 0) "TokenElevation=$elevation"
    }
    Stop-DotDeck
}

function Invoke-Uninstall([string]$Scenario) {
    # Settings > Apps > Uninstall runs the entry's UninstallString ("msiexec.exe /x {code}") as the user.
    $arp = Get-ItemProperty $ArpKey -ErrorAction SilentlyContinue
    if (-not $arp) { Check $Scenario 'uninstall entry present' $false; return }
    $arguments = ($arp.UninstallString -replace '^(?i)"?msiexec(\.exe)?"?\s*', '') + ' /passive /norestart /l*v "' + (Join-Path $Work "uninstall-$Scenario.log") + '"'
    $r = Invoke-UserJob "`$p = Start-Process -FilePath msiexec.exe -ArgumentList '$arguments' -PassThru -Wait; `$code = `$p.ExitCode"
    Check $Scenario 'uninstall started with normal rights' ($r.Integrity -eq 'Medium') $r.Integrity
    Check $Scenario 'uninstall exit code 0' ($r.ExitCode -eq 0) "$($r.ExitCode)"
}

function Test-Removed([string]$Scenario, [string]$Root, $Before) {
    Check $Scenario 'install folder removed' (-not (Test-Path $Root)) $Root
    Check $Scenario 'Installed apps entry removed' (-not (Test-Path $ArpKey))
    Check $Scenario 'desktop shortcut removed' (-not (Test-Path "$PublicDesktop\DotDeck.lnk"))
    Check $Scenario 'Start menu shortcut removed' (-not (Test-Path "$CommonPrograms\DotDeck.lnk"))
    Check $Scenario 'autostart shortcut removed' (-not (Test-Path "$Startup\DotDeck.lnk"))
    Check $Scenario 'update cache removed' (-not (Test-Path "$Local\packages") -and -not (Test-Path "$Local\Update.exe"))
    $after = Get-DataSnapshot
    $missing = @($Before | Where-Object { $after -notcontains $_ })
    Check $Scenario "settings and user data preserved ($($Before.Count) files)" ($missing.Count -eq 0 -and $Before.Count -gt 0) ($missing -join '; ')
}

function Invoke-AppUpdate([string]$Scenario, [string]$Root, [string]$Feed, [string]$Expected, [string]$Launch) {
    [Environment]::SetEnvironmentVariable('DOTDECK_UPDATE_FEED', $Feed, 'User')
    try {
        $r = Invoke-UserJob "Start-Process -FilePath '$Launch'"
        Check $Scenario 'app started by the user (normal rights)' ($r.Integrity -eq 'Medium') $r.Integrity
        $before = Wait-DotDeck $Root 120
        if ($before) { Check $Scenario 'app before update runs with normal rights' ([E2EToken]::Elevation([int]$before.ProcessId) -eq 0) }
        $dialog = Find-Window 'DotDeck 업데이트' 180
        Check $Scenario 'update offer shown' ($null -ne $dialog)
        if (-not $dialog) { Stop-DotDeck; return }
        $now = Find-Control $dialog '지금 업데이트'
        Check $Scenario '[지금 업데이트] found' ($null -ne $now)
        if (-not $now) { Stop-DotDeck; return }
        Invoke-Control $now
        $deadline = (Get-Date).AddSeconds(300)
        $after = $null
        while ((Get-Date) -lt $deadline) {
            $p = Wait-DotDeck $Root 5
            if ($p -and $before -and $p.ProcessId -ne $before.ProcessId -and (Get-AppVersion $Root) -eq $Expected) { $after = $p; break }
            Start-Sleep 2
        }
        Check $Scenario "update applied in place ($Expected)" ((Get-AppVersion $Root) -eq $Expected) "$(Get-AppVersion $Root) in $Root"
        Check $Scenario 'app restarted after update' ($null -ne $after) "$($after.ExecutablePath)"
        if ($after) {
            $elevation = [E2EToken]::Elevation([int]$after.ProcessId)
            Check $Scenario 'restarted app runs with normal rights' ($elevation -eq 0) "TokenElevation=$elevation"
        }
        Stop-DotDeck
    } finally {
        [Environment]::SetEnvironmentVariable('DOTDECK_UPDATE_FEED', $null, 'User')
    }
}

function Install-Legacy([string]$Scenario, [string]$Root, [bool]$Elevated) {
    # DotDeck 1.5.0's per-user Velopack setup (extracted from its DotDeck-Setup.exe), as 1.5.0 users have it.
    if ($Elevated) {
        $p = Start-Process -FilePath $LegacySetup -ArgumentList "--silent --installto `"$Root`"" -PassThru -Wait
        $code = $p.ExitCode
    } else {
        $code = (Invoke-UserJob "`$p = Start-Process -FilePath '$LegacySetup' -ArgumentList '--silent --installto `"$Root`"' -PassThru -Wait; `$code = `$p.ExitCode").ExitCode
    }
    Start-Sleep 5
    Stop-DotDeck
    Check $Scenario '1.5.0 per-user install' ($code -eq 0 -and (Test-Path "$Root\current\DotDeck.exe") -and (Test-Path $LegacyKey)) "exit $code"
}

# =====================================================================================================
Write-Host "== account $Account; A=$VersionA B=$VersionB"
$policy = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
Get-ItemProperty $policy | Select-Object EnableLUA, ConsentPromptBehaviorAdmin, ConsentPromptBehaviorUser | Format-List | Out-String | Write-Host

# --- S0: the setup window, [취소] ---
$s = 'S0 setup cancel'
$n = Start-UserJob "`$p = Start-Process -FilePath '$SetupA' -PassThru; `$p.WaitForExit(); `$code = `$p.ExitCode"
$window = Find-Window "DotDeck $VersionA 설치" 60
Check $s 'setup window opens' ($null -ne $window)
if ($window) { Invoke-Control (Find-Control $window '취소') }
$code = Wait-UserJob $n 120
Check $s 'double-click runs with normal rights' ((Get-JobIntegrity $n) -eq 'Medium') (Get-JobIntegrity $n)
Check $s 'cancel exits 1602' ($code -eq 1602) "$code"
Check $s 'nothing installed' (-not (Test-Path $DefaultDir) -and -not (Test-Path $ArpKey))

# --- S1: 1.5.0 per-user in %LOCALAPPDATA%\DotDeck → in-app update → migration ---
$s = 'S1 legacy LocalAppData'
Install-Legacy $s $Local $false
Add-Sentinels 's1'
Invoke-AppUpdate $s $Local $FeedA $VersionA "$Local\current\DotDeck.exe"
Check $s 'per-user install updated: DotDeck.Core.dll is a separate file' (Test-Path "$Local\current\DotDeck.Core.dll")
Check $s 'legacy user shortcut exists before migration' (Test-Path "$UserDesktop\DotDeck.lnk")
$before = Get-DataSnapshot
$r = Invoke-UserJob "`$p = Start-Process -FilePath '$SetupA' -ArgumentList '--silent --no-launch --log `"$Work\s1-setup.log`"' -PassThru -Wait; `$code = `$p.ExitCode"
Check $s 'setup (normal rights) exit 0' ($r.ExitCode -eq 0 -and $r.Integrity -eq 'Medium') "$($r.ExitCode) $($r.Integrity)"
Test-Installed $s $DefaultDir $VersionA $true
Check $s 'old per-user program removed' (-not (Test-Path "$Local\current") -and -not (Test-Path "$Local\Update.exe"))
Check $s 'old per-user uninstall entry removed' (-not (Test-Path $LegacyKey))
$after = Get-DataSnapshot
Check $s 'settings and user data kept by migration' (@($before | Where-Object { $after -notcontains $_ }).Count -eq 0 -and $before.Count -gt 0) "$($before.Count) files"
Invoke-Uninstall $s
Test-Removed $s $DefaultDir $before

# --- S2: default install through the window (double-click as the user) ---
$s = 'S2 default install'
Add-Sentinels 's2'
$n = Start-UserJob "`$p = Start-Process -FilePath '$SetupA' -PassThru; `$p.WaitForExit(); `$code = `$p.ExitCode"
$window = Find-Window "DotDeck $VersionA 설치" 60
Check $s 'setup window opens' ($null -ne $window)
if ($window) {
    $folder = Find-Control $window '설치 폴더'
    $value = $folder.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    Check $s 'default folder is C:\Program Files\DotDeck' ($value -eq 'C:\Program Files\DotDeck' -and $value -eq $DefaultDir) $value
    $check = Find-Control $window 'Windows 로그인 시 자동 실행'
    $state = $check.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
    Check $s 'autostart checked by default' ($state -eq [Windows.Automation.ToggleState]::On) "$state"
    $scope = Find-Control $window '자동 실행 적용 범위'
    Check $s 'autostart scope shows the account' ($null -ne $scope -and $scope.Current.Name -like "*$Account*") "$($scope.Current.Name)"
    Invoke-Control (Find-Control $window '설치')
}
$code = Wait-UserJob $n 900
Check $s 'double-click runs with normal rights' ((Get-JobIntegrity $n) -eq 'Medium') (Get-JobIntegrity $n)
Check $s 'setup exit 0' ($code -eq 0) "$code"
Test-Installed $s $DefaultDir $VersionA $true
Test-AppRunsAsUser $s $DefaultDir 'setup'
Get-ChildItem $DefaultDir -Recurse -File | Select-Object -ExpandProperty FullName | Set-Content (Join-Path $Work 'installed-files.txt')

$s = 'S3 shortcuts'
foreach ($link in "$PublicDesktop\DotDeck.lnk", "$CommonPrograms\DotDeck.lnk", "$Startup\DotDeck.lnk") {
    $r = Invoke-UserJob "Start-Process -FilePath '$link'"
    Test-AppRunsAsUser $s $DefaultDir (Split-Path -Leaf (Split-Path -Parent $link))
}

$s = 'S4 update A->B'
Invoke-AppUpdate $s $DefaultDir $FeedB $VersionB "$PublicDesktop\DotDeck.lnk"
Test-Installed $s $DefaultDir $VersionB $true

$s = 'S5 uninstall'
Stop-DotDeck
$before = Get-DataSnapshot
Invoke-Uninstall $s
Test-Removed $s $DefaultDir $before

# --- S6: custom Korean/space folder, autostart off, update, reinstall ---
$s = 'S6 custom path'
$r = Invoke-UserJob "`$p = Start-Process -FilePath '$SetupA' -ArgumentList '--silent --installto `"$CustomDir`" --no-autostart --no-launch --log `"$Work\s6-setup.log`"' -PassThru -Wait; `$code = `$p.ExitCode"
Check $s 'setup (normal rights) exit 0' ($r.ExitCode -eq 0 -and $r.Integrity -eq 'Medium') "$($r.ExitCode) $($r.Integrity)"
Test-Installed $s $CustomDir $VersionA $false
Check $s 'nothing in Program Files\DotDeck' (-not (Test-Path $DefaultDir))
Invoke-AppUpdate $s $CustomDir $FeedB $VersionB "$CommonPrograms\DotDeck.lnk"
Test-Installed $s $CustomDir $VersionB $false
$n = Start-UserJob "`$p = Start-Process -FilePath '$SetupB' -PassThru; `$p.WaitForExit(); `$code = `$p.ExitCode"
$window = Find-Window "DotDeck $VersionB 설치" 60
if ($window) {
    $value = (Find-Control $window '설치 폴더').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    Check $s 'reinstall offers the existing folder' ($value -eq $CustomDir) $value
    $state = (Find-Control $window 'Windows 로그인 시 자동 실행').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
    Check $s 'reinstall keeps autostart off' ($state -eq [Windows.Automation.ToggleState]::Off) "$state"
    Invoke-Control (Find-Control $window '설치')
}
$code = Wait-UserJob $n 900
Check $s 'reinstall exit 0' ($code -eq 0) "$code"
Test-Installed $s $CustomDir $VersionB $false
Test-AppRunsAsUser $s $CustomDir 'reinstall'
$before = Get-DataSnapshot
Invoke-Uninstall $s
Test-Removed $s $CustomDir $before

# --- S7: 1.5.0 per-user install inside Program Files → migration ---
$s = 'S7 legacy Program Files'
Install-Legacy $s $DefaultDir $true
$before = Get-DataSnapshot
$r = Invoke-UserJob "`$p = Start-Process -FilePath '$SetupB' -ArgumentList '--silent --no-launch --log `"$Work\s7-setup.log`"' -PassThru -Wait; `$code = `$p.ExitCode"
Check $s 'setup (normal rights) exit 0' ($r.ExitCode -eq 0 -and $r.Integrity -eq 'Medium') "$($r.ExitCode) $($r.Integrity)"
Test-Installed $s $DefaultDir $VersionB $false
Check $s 'old per-user uninstall entry removed' (-not (Test-Path $LegacyKey))
Check $s 'no leftover migration backup' (@(Get-ChildItem $ProgramFiles -Directory -Force -Filter '.DotDeck-setup-*').Count -eq 0)
Invoke-Uninstall $s
Test-Removed $s $DefaultDir $before

# --- S8: a standard user refuses UAC (auto-deny policy) ---
$s = 'S8 UAC refused'
$password = 'E2e-' + [guid]::NewGuid().ToString('N').Substring(0, 16) + '!'
net user dotdeckstd $password /add | Out-Null
Set-ItemProperty $policy ConsentPromptBehaviorUser 0
$n = Start-UserJob "`$p = Start-Process -FilePath '$SetupA' -ArgumentList '--silent --no-launch --log `"$Jobs\s8-setup.log`"' -PassThru -Wait; `$code = `$p.ExitCode" 'dotdeckstd' $password
$code = Wait-UserJob $n 300
Check $s 'standard user: setup exits 1602 (cancelled)' ($code -eq 1602) "$code; $(Get-Content "$Jobs\s8-setup.log" -ErrorAction SilentlyContinue)"
Check $s 'nothing installed' (-not (Test-Path $DefaultDir) -and -not (Test-Path $ArpKey))

# =====================================================================================================
$summary = Join-Path $Work 'e2e-results.md'
$lines = @("| Scenario | Check | Result | Detail |", "|---|---|---|---|")
$lines += $results | ForEach-Object { "| $($_.Scenario) | $($_.Check) | $($_.Result) | $(($_.Detail -replace '\|', '/') -replace '\r?\n', ' ') |" }
[IO.File]::WriteAllLines($summary, $lines, [Text.UTF8Encoding]::new($false))
$results | ConvertTo-Json -Depth 3 | Set-Content -Encoding utf8 (Join-Path $Work 'e2e-results.json')
Write-Host "== $($results.Count) checks, $failures failed"
if ($failures -gt 0) { exit 1 }
