<#
.SYNOPSIS
  End-to-end installer test for a disposable Windows machine (GitHub Actions windows runner). Never run it on a PC
  you use: it installs, updates and uninstalls DotDeck machine-wide, creates local users and changes UAC policy.

.DESCRIPTION
  Runs elevated (the runner account has no UAC split token, so it cannot act as a normal user). Every user step
  runs as a separate local administrator "dotdeck" started through the Secondary Logon service on the console
  desktop: like a user signed in to Windows, it gets the filtered (Medium, not elevated) token, and its UAC
  requests are auto-approved by the runner policy (ConsentPromptBehaviorAdmin=0). A standard user "dotdeckstd"
  with auto-deny (ConsentPromptBehaviorUser=0) exercises the cancel path. The real UAC dialog is never shown, and
  the "double-click" is ShellExecute from the user's Medium process, not a click in Explorer.

  Scenarios: setup window cancel; 1.5.0 per-user install (LocalAppData) → in-app update → migration by the new setup;
  default install via the setup window; launch from shortcuts; in-app update A→B (UAC, restart with normal rights);
  uninstall the way Settings > Apps does; custom Korean/space path with autostart off, update and reinstall;
  1.5.0 per-user install in Program Files → migration; standard user refusing UAC.

.PARAMETER Work
  Folder holding A\DotDeck-Setup.exe, B\DotDeck-Setup.exe, feedA\, feedB\ and legacy\Setup.exe (1.5.0).

.PARAMETER Scenarios
  Scenarios to run: S0 (cancel), S1 (1.5.0 in LocalAppData), S2 (default install, then S3 shortcuts, S4 update,
  S5 uninstall), S6 (custom path), S7 (1.5.0 in Program Files), S8 (standard user, elevation denied by policy). Default: all.

  Every wait has a time limit. A timeout fails the scenario with the step it was in and the windows that were open
  (e.g. a message box waiting for a click), stops leftover setup/app processes and goes on with the next scenario.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Work,
    [Parameter(Mandatory)] [string]$VersionA,
    [Parameter(Mandatory)] [string]$VersionB,
    [string[]]$Scenarios = @('S0', 'S1', 'S2', 'S6', 'S7', 'S8')
)
$Scenarios = @($Scenarios | ForEach-Object { $_ -split '[,\s]+' } | Where-Object { $_ } | ForEach-Object { $_.ToUpperInvariant() })

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class E2ENative {
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int length, out int returned);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
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
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }
    // Window text and check state through window messages: the setup runs as another user, so its MSAA/UIA
    // patterns (ValuePattern, TogglePattern) are not available to this process, but messages are.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, StringBuilder l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    public static string GetText(IntPtr hwnd) {
        IntPtr length;
        if (SendMessageTimeout(hwnd, 0x000E, IntPtr.Zero, IntPtr.Zero, 2, 3000, out length) == IntPtr.Zero) return null;
        var buffer = new StringBuilder(length.ToInt32() + 1);
        IntPtr copied;
        if (SendMessageTimeout(hwnd, 0x000D, new IntPtr(buffer.Capacity), buffer, 2, 3000, out copied) == IntPtr.Zero) return null;
        return buffer.ToString();
    }
    delegate bool EnumProc(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    // Text of the first child window whose class contains the given name (WinForms: "WindowsForms10.EDIT...").
    public static string ChildText(IntPtr parent, string classPart) {
        string found = null;
        EnumChildWindows(parent, (h, d) => {
            var name = new StringBuilder(256);
            GetClassName(h, name, name.Capacity);
            if (name.ToString().IndexOf(classPart, StringComparison.OrdinalIgnoreCase) >= 0) { found = GetText(h); return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    // BM_GETCHECK: 1 checked, 0 unchecked, -1 no answer.
    public static int GetCheck(IntPtr hwnd) {
        IntPtr result;
        if (SendMessageTimeout(hwnd, 0x00F0, IntPtr.Zero, IntPtr.Zero, 2, 3000, out result) == IntPtr.Zero) return -1;
        return result.ToInt32();
    }
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class ShellLink { }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW { void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr data, uint flags); }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    interface IPersistFile { void GetClassID(out Guid id); [PreserveSig] int IsDirty(); void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode); }
    // Unicode target of a .lnk (WScript.Shell turns Korean characters into '?').
    public static string LinkTarget(string link) {
        object o = new ShellLink();
        try {
            ((IPersistFile)o).Load(link, 0);
            var buffer = new StringBuilder(1024);
            ((IShellLinkW)o).GetPath(buffer, buffer.Capacity, IntPtr.Zero, 4);
            return buffer.ToString();
        } finally { Marshal.FinalReleaseComObject(o); }
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
# The test users read the setups and write logs here.
icacls $Work /grant '*S-1-1-0:(OI)(CI)F' | Out-Null

$ProgramFiles = [Environment]::GetFolderPath('ProgramFiles')
$DefaultDir = Join-Path $ProgramFiles 'DotDeck'
$CustomDir = 'C:\테스트 폴더\닷덱 앱'
$PublicDesktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
$CommonPrograms = [Environment]::GetFolderPath('CommonPrograms')
$CommonStartup = [Environment]::GetFolderPath('CommonStartup')
$ArpKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MSI:DotDeck'
$Policy = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'

function New-TestUser([string]$Name, [bool]$Admin) {
    $secure = ConvertTo-SecureString ('E2e-' + [guid]::NewGuid().ToString('N').Substring(0, 16) + '!') -AsPlainText -Force
    New-LocalUser -Name $Name -Password $secure -PasswordNeverExpires -AccountNeverExpires | Out-Null
    Add-LocalGroupMember -Group (Get-LocalGroup -SID 'S-1-5-32-545') -Member $Name -ErrorAction SilentlyContinue
    if ($Admin) { Add-LocalGroupMember -Group (Get-LocalGroup -SID 'S-1-5-32-544') -Member $Name }
    return New-Object Management.Automation.PSCredential("$env:COMPUTERNAME\$Name", $secure)
}
$UserCred = New-TestUser 'dotdeck' $true
$Account = "$env:COMPUTERNAME\dotdeck"

$results = [System.Collections.Generic.List[object]]::new()
$failures = 0
$Progress = Join-Path $Work 'e2e-progress.txt'
Set-Content -Path $Progress -Value "start $(Get-Date -Format o) scenarios $($Scenarios -join ',')" -Encoding utf8
function Check([string]$Scenario, [string]$Name, [bool]$Ok, [string]$Detail = '') {
    $result = $(if ($Ok) { 'PASS' } else { 'FAIL' })
    $results.Add([pscustomobject]@{ Scenario = $Scenario; Check = $Name; Result = $result; Detail = $Detail })
    if (-not $Ok) { $script:failures++ }
    $line = "{0} [{1}] {2} / {3} {4}" -f (Get-Date -Format 'HH:mm:ss'), $result, $Scenario, $Name, $Detail
    Write-Host $line
    Add-Content -Path $Progress -Value $line -Encoding utf8 # survives a cancelled run
}

# The step being run: named in the failure when a wait times out.
$script:Step = ''
function Step([string]$Name) {
    $script:Step = $Name
    $line = "{0} -- {1}: {2}" -f (Get-Date -Format 'HH:mm:ss'), $s, $Name
    Write-Host $line
    Add-Content -Path $Progress -Value $line -Encoding utf8
}

function Stop-Step([int]$TimeoutSec, [string]$What) {
    throw "TIMEOUT after ${TimeoutSec}s in step '$($script:Step)'$What. Open windows: $(Get-OpenWindows)"
}

# ---------- user jobs (Secondary Logon: the user's filtered token on the console desktop) ----------
$script:jobCount = 0
function Start-UserJob([string]$Body, [Management.Automation.PSCredential]$Credential = $UserCred) {
    $script:jobCount++
    $n = $script:jobCount
    $file = Join-Path $Jobs "job$n.ps1"
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
    $text = $text.Replace('__LOG__', (Join-Path $Jobs "job$n.log")).Replace('__EXIT__', (Join-Path $Jobs "job$n.exit")).Replace('__BODY__', $Body)
    [IO.File]::WriteAllText($file, $text, [Text.UTF8Encoding]::new($true))
    Start-Process -FilePath powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$file`"" `
        -Credential $Credential -LoadUserProfile -WorkingDirectory $Jobs -WindowStyle Hidden | Out-Null
    return $n
}

function Wait-UserJob([int]$n, [int]$TimeoutSec) {
    $exit = Join-Path $Jobs "job$n.exit"
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while (-not (Test-Path $exit)) {
        if ((Get-Date) -gt $deadline) { Stop-Step $TimeoutSec " (job $n)" }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Milliseconds 300
    return [int](Get-Content $exit | Select-Object -First 1)
}

# Top-level windows of setup/app/installer processes with their texts: shows a message box waiting for a click.
function Get-OpenWindows {
    $names = 'DotDeck-Setup', 'DotDeck', 'msiexec', 'Update', 'Setup', 'WerFault'
    $ids = @{}
    Get-Process -ErrorAction SilentlyContinue | Where-Object { $names -contains $_.ProcessName } | ForEach-Object { $ids[$_.Id] = $_.ProcessName }
    $out = foreach ($top in $UIA::RootElement.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)) {
        $owner = $top.Current.ProcessId
        if (-not $ids.ContainsKey($owner)) { continue }
        $texts = @($top.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) |
            ForEach-Object { $_.Current.Name } | Where-Object { $_ } | Select-Object -First 12)
        "[$($ids[$owner]) $owner '$($top.Current.Name)' ($($top.Current.ClassName)): $($texts -join ' | ')]"
    }
    $procs = ($ids.GetEnumerator() | ForEach-Object { "$($_.Value)#$($_.Key)" }) -join ', '
    return "$($out -join ' ') processes: $procs"
}

function Stop-Leftovers {
    foreach ($name in 'DotDeck-Setup', 'Setup', 'WerFault') { Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue }
    Stop-DotDeck
    # A running Windows Installer client would block the next scenario.
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Process msiexec -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -ne 0 }) -and (Get-Date) -lt $deadline) { Start-Sleep 2 }
}

function Get-JobIntegrity([int]$n) {
    $line = Get-Content (Join-Path $Jobs "job$n.log") -ErrorAction SilentlyContinue | Where-Object { $_ -match 'Mandatory Label' } | Select-Object -First 1
    if ($line -match 'Mandatory Label\\(\w+) Mandatory Level') { return $Matches[1] } else { return "?($line)" }
}

function Invoke-UserJob([string]$Body, [int]$TimeoutSec) {
    $n = Start-UserJob $Body
    $code = Wait-UserJob $n $TimeoutSec
    return [pscustomobject]@{ Job = $n; ExitCode = $code; Integrity = (Get-JobIntegrity $n) }
}

# The user's known folders and HKCU values, read as that user.
function Get-UserState {
    $out = Join-Path $Jobs ("state-" + [guid]::NewGuid().ToString('N') + '.json')
    $body = @'
$pref = (Get-ItemProperty 'HKCU:\Software\DotDeck\Setup' -ErrorAction SilentlyContinue).AutoStart
[pscustomobject]@{
    Desktop = [Environment]::GetFolderPath('DesktopDirectory'); Programs = [Environment]::GetFolderPath('Programs')
    Startup = [Environment]::GetFolderPath('Startup', 'Create'); AppData = $env:APPDATA; LocalAppData = $env:LOCALAPPDATA
    Temp = $env:TEMP
    LegacyEntry = (Test-Path 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\DotDeck')
    AutoStartPref = $pref
} | ConvertTo-Json | Set-Content -Encoding utf8 '__OUT__'
'@
    [void](Invoke-UserJob $body.Replace('__OUT__', $out) 120)
    $state = Get-Content -Raw $out | ConvertFrom-Json
    if (-not $state.Startup -or -not $state.Desktop) { throw "user folders not resolved: $($state | ConvertTo-Json -Compress)" }
    return $state
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

function Find-Control($Window, [string]$Name, $ControlType = $null) {
    $byName = [Windows.Automation.PropertyCondition]::new($UIA::NameProperty, $Name)
    if ($ControlType) {
        $typed = [Windows.Automation.AndCondition]::new($byName, [Windows.Automation.PropertyCondition]::new($UIA::ControlTypeProperty, $ControlType))
        $found = $Window.FindFirst([Windows.Automation.TreeScope]::Descendants, $typed)
        if ($found) { return $found }
    }
    $Window.FindFirst([Windows.Automation.TreeScope]::Descendants, $byName)
}

function Invoke-Button($Window, [string]$Name) {
    $button = Find-Control $Window $Name ([Windows.Automation.ControlType]::Button)
    if (-not $button) { return $false }
    $pattern = $null
    if ($button.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        $pattern.Invoke()
    } else {
        # Some WinForms buttons only expose MSAA: click the centre of the button.
        [void][E2ENative]::SetForegroundWindow([IntPtr]$Window.Current.NativeWindowHandle)
        Start-Sleep -Milliseconds 300
        $r = $button.Current.BoundingRectangle
        [E2ENative]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
    }
    return $true
}

function Get-Toggle($Element) {
    if (-not $Element) { return 'not found' }
    switch ([E2ENative]::GetCheck([IntPtr]$Element.Current.NativeWindowHandle)) { 1 { 'On' } 0 { 'Off' } default { "?($_)" } }
}

# Text of a control's window (TextBox value, label text even when an accessible name replaces it).
function Get-Value($Element) {
    if (-not $Element) { return $null }
    return [E2ENative]::GetText([IntPtr]$Element.Current.NativeWindowHandle)
}

# ---------- helpers ----------
function Get-DotDeckProcesses { @(Get-CimInstance Win32_Process -Filter "Name='DotDeck.exe'" | Where-Object { $_.ExecutablePath }) }

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
    if (-not $Link -or -not (Test-Path -LiteralPath $Link)) { return $null }
    return [E2ENative]::LinkTarget($Link)
}

function Get-AppVersion([string]$Root) {
    $dll = Join-Path $Root 'current\DotDeck.dll'
    if (Test-Path -LiteralPath $dll) { return (Get-Item -LiteralPath $dll).VersionInfo.ProductVersion.Split('+')[0] }
    $exe = Join-Path $Root 'current\DotDeck.exe' # 1.5.0 single-file build
    if (Test-Path -LiteralPath $exe) { return (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion.Split('+')[0] }
    return $null
}

function Get-DataSnapshot {
    # Settings and user data (everything except Velopack's update cache) as "path|length|hash".
    $items = foreach ($root in $Roaming, $Local) {
        if (Test-Path $root) {
            Get-ChildItem $root -Recurse -File -Force | Where-Object {
                $rel = $_.FullName.Substring($root.Length + 1)
                # Program files of a 1.5 per-user install and Velopack's cache/logs are not user data.
                -not ($rel -like 'packages\*' -or $rel -like 'WebView2\*' -or $rel -like 'logs\*' -or $rel -like 'current\*' -or $rel -like '*.log' -or
                      @('Update.exe', 'DotDeck.exe', 'sq.version', '.velopack_lock', '.betaId') -contains $rel)
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
    Check $Scenario 'desktop shortcut (all users) -> launcher' ((Get-LinkTarget "$PublicDesktop\DotDeck.lnk") -eq $launcher) "$(Get-LinkTarget "$PublicDesktop\DotDeck.lnk")"
    Check $Scenario 'Start menu shortcut (all users) -> launcher' ((Get-LinkTarget "$CommonPrograms\DotDeck.lnk") -eq $launcher) "$(Get-LinkTarget "$CommonPrograms\DotDeck.lnk")"
    $userLinks = @(Get-ChildItem $U.Desktop, $U.Programs -Filter 'DotDeck*.lnk' -ErrorAction SilentlyContinue)
    Check $Scenario 'no duplicate per-user desktop/Start menu shortcuts' ($userLinks.Count -eq 0) ($userLinks.FullName -join ', ')
    $startupTarget = Get-LinkTarget "$($U.Startup)\DotDeck.lnk"
    if ($AutoStart) {
        Check $Scenario 'autostart shortcut (current user) -> launcher' ($startupTarget -eq $launcher) "$startupTarget"
    } else {
        Check $Scenario 'no autostart shortcut' ($null -eq $startupTarget) "$startupTarget"
    }
    $allStartup = @(Get-ChildItem $U.Startup, $CommonStartup -Filter '*.lnk' -ErrorAction SilentlyContinue | Where-Object { (Get-LinkTarget $_.FullName) -like "$Root\*" })
    Check $Scenario 'at most one autostart entry' ($allStartup.Count -le 1) ($allStartup.FullName -join ', ')
}

function Test-Installed([string]$Scenario, [string]$Root, [string]$Version, [bool]$AutoStart) {
    foreach ($file in 'DotDeck.exe', 'Update.exe', 'current\DotDeck.exe', 'current\DotDeck.dll', 'current\DotDeck.Core.dll', 'current\Velopack.dll', 'current\coreclr.dll') {
        Check $Scenario "file $file" (Test-Path -LiteralPath (Join-Path $Root $file))
    }
    Check $Scenario 'app version' ((Get-AppVersion $Root) -eq $Version) "$(Get-AppVersion $Root)"
    $arp = Get-ItemProperty $ArpKey -ErrorAction SilentlyContinue
    Check $Scenario 'Installed apps entry (HKLM)' ($null -ne $arp -and $arp.DisplayName -eq 'DotDeck') "$($arp.DisplayName) $($arp.DisplayVersion) $($arp.UninstallString)"
    Check $Scenario 'Installed apps entry location' ($arp -and $arp.InstallLocation.TrimEnd('\') -eq $Root) "$($arp.InstallLocation)"
    Check $Scenario 'Installed apps entry version' ($arp -and $arp.DisplayVersion -eq $Version) "$($arp.DisplayVersion)"
    Test-Shortcuts $Scenario $Root $AutoStart
}

function Test-AppRunsAsUser([string]$Scenario, [string]$Root, [string]$What) {
    Step "wait for DotDeck ($What)"
    $p = Wait-DotDeck $Root 120
    Check $Scenario "$What starts DotDeck" ($null -ne $p) "$($p.ExecutablePath)"
    if ($p) {
        $elevation = [E2ENative]::Elevation([int]$p.ProcessId)
        $owner = (Invoke-CimMethod -InputObject $p -MethodName GetOwner)
        Check $Scenario "$What runs with normal rights (not elevated)" ($elevation -eq 0) "TokenElevation=$elevation owner=$($owner.User)"
    }
    Stop-DotDeck
}

function Invoke-Uninstall([string]$Scenario) {
    # Settings > Apps > Uninstall runs the entry's UninstallString ("msiexec.exe /x {code}") as the user.
    $arp = Get-ItemProperty $ArpKey -ErrorAction SilentlyContinue
    if (-not $arp) { Check $Scenario 'uninstall entry present' $false; return }
    $log = Join-Path $Work ("uninstall-" + ($Scenario -replace '\W', '') + ".log")
    $arguments = ($arp.UninstallString -replace '^(?i)"?msiexec(\.exe)?"?\s*', '') + " /passive /norestart /l*v `"$log`""
    Step 'uninstall (msiexec /x as the user)'
    $r = Invoke-UserJob "`$p = Start-Process -FilePath msiexec.exe -ArgumentList '$arguments' -PassThru; `$p.WaitForExit(); `$code = `$p.ExitCode" 300
    Check $Scenario 'uninstall started with normal rights' ($r.Integrity -eq 'Medium') $r.Integrity
    Check $Scenario 'uninstall exit code 0' ($r.ExitCode -eq 0) "$($r.ExitCode)"
}

function Test-Removed([string]$Scenario, [string]$Root, $Before) {
    Check $Scenario 'install folder removed' (-not (Test-Path -LiteralPath $Root)) $Root
    Check $Scenario 'Installed apps entry removed' (-not (Test-Path $ArpKey))
    Check $Scenario 'desktop shortcut removed' (-not (Test-Path "$PublicDesktop\DotDeck.lnk"))
    Check $Scenario 'Start menu shortcut removed' (-not (Test-Path "$CommonPrograms\DotDeck.lnk"))
    Check $Scenario 'autostart shortcut removed' (-not (Test-Path "$($U.Startup)\DotDeck.lnk"))
    Check $Scenario 'update cache removed' (-not (Test-Path "$Local\packages") -and -not (Test-Path "$Local\Update.exe"))
    $after = Get-DataSnapshot
    $missing = @($Before | Where-Object { $after -notcontains $_ })
    Check $Scenario "settings and user data preserved ($($Before.Count) files)" ($missing.Count -eq 0 -and $Before.Count -gt 0) ($missing -join '; ')
}

function Invoke-AppUpdate([string]$Scenario, [string]$Root, [string]$Feed, [string]$Expected, [string]$Launch) {
    # The app reads DOTDECK_UPDATE_FEED (a local test feed); Update.exe and the restarted app inherit it.
    Step "start the app ($Launch)"
    $n = Start-UserJob "`$env:DOTDECK_UPDATE_FEED = '$Feed'; Start-Process -FilePath '$Launch'"
    [void](Wait-UserJob $n 120)
    Check $Scenario 'app started by the user (normal rights)' ((Get-JobIntegrity $n) -eq 'Medium') (Get-JobIntegrity $n)
    $before = Wait-DotDeck $Root 120
    if ($before) { Check $Scenario 'app before update runs with normal rights' ([E2ENative]::Elevation([int]$before.ProcessId) -eq 0) }
    Step 'wait for the update offer'
    $dialog = Find-Window 'DotDeck 업데이트' 180
    Check $Scenario 'update offer shown' ($null -ne $dialog)
    if (-not $dialog) { Stop-DotDeck; return }
    Check $Scenario '[지금 업데이트] clicked' (Invoke-Button $dialog '지금 업데이트')
    Step 'wait for the update to apply and the app to restart'
    $deadline = (Get-Date).AddSeconds(300)
    $after = $null
    while ((Get-Date) -lt $deadline) {
        $p = Wait-DotDeck $Root 5
        if ($p -and $before -and $p.ProcessId -ne $before.ProcessId -and (Get-AppVersion $Root) -eq $Expected) { $after = $p; break }
        Start-Sleep 2
    }
    Check $Scenario "update applied in place ($Expected)" ((Get-AppVersion $Root) -eq $Expected) "$(Get-AppVersion $Root) in $Root"
    if (-not $after) { Stop-Step 300 ": version $(Get-AppVersion $Root), app not restarted" }
    Check $Scenario 'app restarted after update' $true "$($after.ExecutablePath)"
    # Velopack's elevated helper also runs the new DotDeck.exe briefly for its update hook: judge the app that stays.
    Start-Sleep 8
    $running = @(Get-DotDeckProcesses | Where-Object { $_.ExecutablePath -like "$Root\current\DotDeck.exe" })
    $levels = @($running | ForEach-Object { "$($_.ProcessId):$([E2ENative]::Elevation([int]$_.ProcessId))" })
    Check $Scenario 'restarted app runs with normal rights' ($running.Count -ge 1 -and @($levels | Where-Object { $_ -notlike '*:0' }).Count -eq 0) "pid:TokenElevation $($levels -join ', ')"
    Stop-DotDeck
}

function Install-Legacy([string]$Scenario, [string]$Root, [bool]$Elevated) {
    # DotDeck 1.5.0's per-user Velopack setup (extracted from its DotDeck-Setup.exe), as the user ran it.
    $verb = if ($Elevated) { '-Verb RunAs' } else { '' }
    Step "1.5.0 per-user setup into $Root"
    $r = Invoke-UserJob "`$p = Start-Process -FilePath '$LegacySetup' -ArgumentList '--silent --installto `"$Root`"' $verb -PassThru; `$p.WaitForExit(); `$code = `$p.ExitCode" 300
    Start-Sleep 5
    Stop-DotDeck
    $state = Get-UserState
    Check $Scenario '1.5.0 per-user install' ($r.ExitCode -eq 0 -and (Test-Path -LiteralPath "$Root\current\DotDeck.exe") -and $state.LegacyEntry) "exit $($r.ExitCode), entry $($state.LegacyEntry)"
}

# =====================================================================================================
# A scenario runs as one block: a failed wait or unexpected error ends that scenario (with the step it was in),
# leftovers are stopped and removed, and the next scenario starts from a clean machine.
function Invoke-Scenario([string]$Id, [string]$Name, [scriptblock]$Body) {
    if ($Scenarios -notcontains $Id) { return }
    $script:s = "$Id $Name"
    $started = Get-Date
    Step 'start'
    try {
        & $Body
    } catch {
        Check $script:s "stopped in step '$($script:Step)'" $false ("$_ @ " + $_.InvocationInfo.PositionMessage -replace '\r?\n', ' ')
        Stop-Leftovers
        Reset-Machine
    }
    Write-Host ("== {0}: {1:n0}s" -f $script:s, ((Get-Date) - $started).TotalSeconds)
}

# Removes what a failed scenario left behind (as the runner admin, quietly), so the next one starts clean.
function Reset-Machine {
    $arp = Get-ItemProperty $ArpKey -ErrorAction SilentlyContinue
    if ($arp -and $arp.UninstallString -match '\{[0-9A-Fa-f-]+\}') {
        $p = Start-Process msiexec.exe -ArgumentList "/x $($Matches[0]) /qn /norestart" -PassThru
        if (-not $p.WaitForExit(180000)) { Write-Host 'reset: msiexec /x did not finish in 180s' }
    }
    foreach ($dir in $DefaultDir, $CustomDir) { if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue } }
}

function Save-Logs {
    $logs = New-Item -ItemType Directory -Force (Join-Path $Work 'logs')
    $sources = @("$($U.Temp)\DotDeck-Setup-*", "$Local\*.log", "$DefaultDir\*.log", "$CustomDir\*.log")
    foreach ($pattern in $sources) {
        Get-ChildItem -Path $pattern -File -ErrorAction SilentlyContinue | Copy-Item -Destination $logs -Force -ErrorAction SilentlyContinue
    }
}

try {
Get-ItemProperty $Policy | Select-Object EnableLUA, ConsentPromptBehaviorAdmin, ConsentPromptBehaviorUser | Format-List | Out-String | Write-Host
$s = 'setup'
Step 'read the test user folders'
$U = Get-UserState
$Roaming = Join-Path $U.AppData 'DotDeck'
$Local = Join-Path $U.LocalAppData 'DotDeck'
Write-Host "== user $Account, profile $($U.LocalAppData), startup $($U.Startup); A=$VersionA B=$VersionB; scenarios $($Scenarios -join ',')"
Check 'setup' 'test user runs with normal rights (Medium)' ((Get-JobIntegrity 1) -eq 'Medium') (Get-JobIntegrity 1)

Invoke-Scenario 'S0' 'setup cancel' {
    Step 'double-click the setup'
    $n = Start-UserJob "`$p = Start-Process -FilePath '$SetupA' -PassThru; `$p.WaitForExit(); `$code = `$p.ExitCode"
    $window = Find-Window "DotDeck $VersionA 설치" 60
    Check $s 'setup window opens' ($null -ne $window)
    if ($window) { Check $s '[취소] clicked' (Invoke-Button $window '취소') }
    Step 'wait for the setup to exit after [취소]'
    $code = Wait-UserJob $n 60
    Check $s 'double-click runs with normal rights' ((Get-JobIntegrity $n) -eq 'Medium') (Get-JobIntegrity $n)
    Check $s 'cancel exits 1602' ($code -eq 1602) "$code"
    Check $s 'nothing installed' (-not (Test-Path $DefaultDir) -and -not (Test-Path $ArpKey))
}

Invoke-Scenario 'S1' 'legacy LocalAppData' {
    Install-Legacy $s $Local $false
    Add-Sentinels 's1'
    Invoke-AppUpdate $s $Local $FeedA $VersionA "$Local\current\DotDeck.exe"
    Check $s 'per-user install updated: DotDeck.Core.dll is a separate file' (Test-Path "$Local\current\DotDeck.Core.dll")
    Check $s 'legacy user shortcut exists before migration' (Test-Path "$($U.Desktop)\DotDeck.lnk")
    $before = Get-DataSnapshot
    Step 'new setup --silent (migration)'
    $r = Invoke-UserJob "`$p = Start-Process -FilePath '$SetupA' -ArgumentList '--silent --no-launch --log `"$Work\s1-setup.log`"' -PassThru; `$p.WaitForExit(); `$code = `$p.ExitCode" 300
    Check $s 'setup (normal rights) exit 0' ($r.ExitCode -eq 0 -and $r.Integrity -eq 'Medium') "$($r.ExitCode) $($r.Integrity); $(Get-Content "$Work\s1-setup.log" -ErrorAction SilentlyContinue)"
    Test-Installed $s $DefaultDir $VersionA $true
    Check $s 'old per-user program removed' (-not (Test-Path "$Local\current") -and -not (Test-Path "$Local\Update.exe"))
    Check $s 'old per-user uninstall entry removed' (-not (Get-UserState).LegacyEntry)
    $after = Get-DataSnapshot
    $lost = @($before | Where-Object { $after -notcontains $_ })
    Check $s "settings and user data kept by migration ($($before.Count) files)" ($lost.Count -eq 0 -and $before.Count -gt 0) ($lost -join '; ')
    Invoke-Uninstall $s
    Test-Removed $s $DefaultDir $before
}

Invoke-Scenario 'S2' 'default install' {
    Add-Sentinels 's2'
    Step 'double-click the setup'
    $n = Start-UserJob "`$p = Start-Process -FilePath '$SetupA' -PassThru; `$p.WaitForExit(); `$code = `$p.ExitCode"
    $window = Find-Window "DotDeck $VersionA 설치" 60
    Check $s 'setup window opens' ($null -ne $window)
    if (-not $window) { Stop-Step 60 ': no setup window' }
    $value = [E2ENative]::ChildText([IntPtr]$window.Current.NativeWindowHandle, 'EDIT') # the form's only text box
    Check $s 'default folder is C:\Program Files\DotDeck' ($value -eq 'C:\Program Files\DotDeck' -and $value -eq $DefaultDir) "$value"
    $state = Get-Toggle (Find-Control $window 'Windows 로그인 시 자동 실행' ([Windows.Automation.ControlType]::CheckBox))
    Check $s 'autostart checked by default' ($state -eq 'On') "$state"
    $scope = Get-Value (Find-Control $window '자동 실행 적용 범위')
    Check $s 'autostart scope names the current account' ($scope -like "*$Account*") "$scope"
    Check $s '[설치] clicked' (Invoke-Button $window '설치')
    Step 'wait for the setup to finish after [설치] (UAC auto-approved, MSI, start DotDeck)'
    $code = Wait-UserJob $n 300
    Check $s 'double-click runs with normal rights' ((Get-JobIntegrity $n) -eq 'Medium') (Get-JobIntegrity $n)
    Check $s 'setup exit 0' ($code -eq 0) "$code"
    Test-Installed $s $DefaultDir $VersionA $true
    Test-AppRunsAsUser $s $DefaultDir 'setup'
    Get-ChildItem $DefaultDir -Recurse -File | Select-Object -ExpandProperty FullName | Set-Content (Join-Path $Work 'installed-files.txt')

    $script:s = 'S3 shortcuts'
    foreach ($link in "$PublicDesktop\DotDeck.lnk", "$CommonPrograms\DotDeck.lnk", "$($U.Startup)\DotDeck.lnk") {
        Step "open $link"
        [void](Invoke-UserJob "Start-Process -FilePath '$link'" 60)
        Test-AppRunsAsUser $s $DefaultDir (Split-Path -Leaf (Split-Path -Parent $link))
    }

    $script:s = 'S4 update A->B'
    Invoke-AppUpdate $s $DefaultDir $FeedB $VersionB "$PublicDesktop\DotDeck.lnk"
    Test-Installed $s $DefaultDir $VersionB $true
    Check $s 'autostart choice kept after update' ((Get-UserState).AutoStartPref -eq 1 -and (Test-Path "$($U.Startup)\DotDeck.lnk"))

    $script:s = 'S5 uninstall'
    Stop-DotDeck
    $before = Get-DataSnapshot
    Invoke-Uninstall $s
    Test-Removed $s $DefaultDir $before
}

Invoke-Scenario 'S6' 'custom path' {
    Step 'setup --silent --installto (Korean/space path, autostart off)'
    $r = Invoke-UserJob "`$p = Start-Process -FilePath '$SetupA' -ArgumentList '--silent --installto `"$CustomDir`" --no-autostart --no-launch --log `"$Work\s6-setup.log`"' -PassThru; `$p.WaitForExit(); `$code = `$p.ExitCode" 300
    Check $s 'setup (normal rights) exit 0' ($r.ExitCode -eq 0 -and $r.Integrity -eq 'Medium') "$($r.ExitCode) $($r.Integrity)"
    Test-Installed $s $CustomDir $VersionA $false
    Check $s 'nothing in Program Files\DotDeck' (-not (Test-Path $DefaultDir))
    Check $s 'autostart choice remembered (off)' ((Get-UserState).AutoStartPref -eq 0)
    Step 'normal user tries to write into the install folder'
    $probe = Join-Path $CustomDir 'e2e-user-write.txt'
    [void](Invoke-UserJob "Set-Content -LiteralPath '$probe' -Value x -ErrorAction Stop" 60)
    Check $s 'install folder is read-only for normal users' (-not (Test-Path -LiteralPath $probe))
    Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
    Invoke-AppUpdate $s $CustomDir $FeedB $VersionB "$CommonPrograms\DotDeck.lnk"
    Test-Installed $s $CustomDir $VersionB $false
    Step 'double-click setup B (reinstall)'
    $n = Start-UserJob "`$p = Start-Process -FilePath '$SetupB' -PassThru; `$p.WaitForExit(); `$code = `$p.ExitCode"
    $window = Find-Window "DotDeck $VersionB 설치" 60
    Check $s 'reinstall window opens' ($null -ne $window)
    if (-not $window) { Stop-Step 60 ': no setup window' }
    $value = [E2ENative]::ChildText([IntPtr]$window.Current.NativeWindowHandle, 'EDIT') # the form's only text box
    Check $s 'reinstall offers the existing folder' ($value -eq $CustomDir) "$value"
    $state = Get-Toggle (Find-Control $window 'Windows 로그인 시 자동 실행' ([Windows.Automation.ControlType]::CheckBox))
    Check $s 'reinstall keeps autostart off' ($state -eq 'Off') "$state"
    Check $s '[설치] clicked' (Invoke-Button $window '설치')
    Step 'wait for the reinstall to finish after [설치]'
    $code = Wait-UserJob $n 300
    Check $s 'reinstall exit 0' ($code -eq 0) "$code"
    Test-Installed $s $CustomDir $VersionB $false
    Test-AppRunsAsUser $s $CustomDir 'reinstall'
    $before = Get-DataSnapshot
    Invoke-Uninstall $s
    Test-Removed $s $CustomDir $before
}

Invoke-Scenario 'S7' 'legacy Program Files' {
    Install-Legacy $s $DefaultDir $true
    Add-Sentinels 's7'
    $before = Get-DataSnapshot
    $pref = (Get-UserState).AutoStartPref
    $expectAutoStart = ($null -eq $pref -or $pref -ne 0) # silent setup: the remembered choice, else on
    Step 'new setup --silent (migration)'
    $r = Invoke-UserJob "`$p = Start-Process -FilePath '$SetupB' -ArgumentList '--silent --no-launch --log `"$Work\s7-setup.log`"' -PassThru; `$p.WaitForExit(); `$code = `$p.ExitCode" 300
    Check $s 'setup (normal rights) exit 0' ($r.ExitCode -eq 0 -and $r.Integrity -eq 'Medium') "$($r.ExitCode) $($r.Integrity); $(Get-Content "$Work\s7-setup.log" -ErrorAction SilentlyContinue)"
    Test-Installed $s $DefaultDir $VersionB $expectAutoStart
    Check $s 'old per-user uninstall entry removed' (-not (Get-UserState).LegacyEntry)
    Check $s 'no leftover migration backup' (@(Get-ChildItem $ProgramFiles -Directory -Force -Filter '.DotDeck-setup-*').Count -eq 0)
    $after = Get-DataSnapshot
    $lost = @($before | Where-Object { $after -notcontains $_ })
    Check $s "settings and user data kept by migration ($($before.Count) files)" ($lost.Count -eq 0 -and $before.Count -gt 0) ($lost -join '; ')
    Invoke-Uninstall $s
    Test-Removed $s $DefaultDir $before
}

Invoke-Scenario 'S8' 'UAC refused' {
    $std = New-TestUser 'dotdeckstd' $false
    Set-ItemProperty $Policy ConsentPromptBehaviorUser 0
    Step 'standard user runs the setup (UAC auto-deny)'
    $n = Start-UserJob "`$p = Start-Process -FilePath '$SetupA' -ArgumentList '--silent --no-launch --log `"$Jobs\s8-setup.log`"' -PassThru; `$p.WaitForExit(); `$code = `$p.ExitCode" $std
    $code = Wait-UserJob $n 120
    # With auto-deny the elevation fails as 'blocked by policy' (1260), not as a refused prompt (1223, untestable here).
    $log = "$(Get-Content "$Jobss8-setup.log" -ErrorAction SilentlyContinue)"
    Check $s 'standard user: setup ends without installing (failure code, reason logged)' ($code -in 1602, 1603 -and $log -match '관리자 권한') "$code; $log"
    Check $s 'nothing installed' (-not (Test-Path $DefaultDir) -and -not (Test-Path $ArpKey))
}

} catch {
    Check $s "stopped in step '$($script:Step)'" $false ("$_ @ " + $_.InvocationInfo.PositionMessage -replace '\r?\n', ' ')
} finally {
    Stop-Leftovers
    if ($U) { Save-Logs }
}

# =====================================================================================================
$summary = Join-Path $Work 'e2e-results.md'
$lines = @("Scenarios: $($Scenarios -join ', ')", '', "| Scenario | Check | Result | Detail |", "|---|---|---|---|")
$lines += $results | ForEach-Object { "| $($_.Scenario) | $($_.Check) | $($_.Result) | $(($_.Detail -replace '\|', '/') -replace '\r?\n', ' ') |" }
[IO.File]::WriteAllLines($summary, $lines, [Text.UTF8Encoding]::new($false))
$results | ConvertTo-Json -Depth 3 | Set-Content -Encoding utf8 (Join-Path $Work 'e2e-results.json')
Write-Host "== $($results.Count) checks, $failures failed"
if ($failures -gt 0) { exit 1 }
