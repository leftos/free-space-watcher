#Requires -Version 7.0
<#
.SYNOPSIS
Drives the tray through its icon, menu and windows, and fails when a step breaks it.

.DESCRIPTION
Builds the tray (Debug), stops any running tray (it is single-instance per session), starts the build output, then:

  1. A left-click on the icon opens Status, which shows "Connected" (up to 20 s); then closed.
  2. Settings from the menu, then closed.
  3. Alerts from the menu, left open.
  4. Settings from the menu again, while Alerts is open, then closed.
  5. Status from the menu, then closed.
  6. A double-click on the icon opens Status.
  7. Alerts and Status closed.
  8. Exit from the menu: the tray must end within 5 s.

The mouse is never moved. Icon clicks are the notifications Windows sends a notification icon's message window (WM_USER with
WM_LBUTTONDOWN and so on), posted to the tray's hidden H.NotifyIcon window; menu items are invoked and windows closed through
UI Automation. Windows the tray opens still take the focus, so typing elsewhere during the run can land in them.

After every step the tray must still be running and responding, the window the step opened must show its key control on
screen, and %LOCALAPPDATA%\FreeSpaceWatcher\tray.log must have no new WARN entry (unhandled exceptions are logged there).
The first failure stops the run and exits 1. The installed tray is started again at the end when it was running before.
Needs the FreeSpaceWatcher service running.

.EXAMPLE
pwsh -NoProfile -File scripts/ui-smoke.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$InformationPreference = 'Continue'

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class FswTrayIcon
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static IntPtr FindMessageWindow(int processId)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            uint owner;
            GetWindowThreadProcessId(hWnd, out owner);
            StringBuilder text = new StringBuilder(128);
            GetWindowText(hWnd, text, text.Capacity);
            if (owner == processId && text.ToString().StartsWith("H.NotifyIcon_", StringComparison.Ordinal))
            {
                found = hWnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static void Post(IntPtr hWnd, uint mouseMessage)
    {
        if (!PostMessage(hWnd, 0x0400, IntPtr.Zero, new IntPtr(mouseMessage)))
        {
            throw new InvalidOperationException("PostMessage to the tray icon window failed: error " + Marshal.GetLastWin32Error());
        }
    }
}
'@

$repoRoot = Split-Path -Parent $PSScriptRoot
$trayProject = Join-Path $repoRoot 'src\FreeSpaceWatcher.Tray\FreeSpaceWatcher.Tray.csproj'
$trayExe = Join-Path $repoRoot 'src\FreeSpaceWatcher.Tray\bin\Debug\net10.0-windows10.0.19041.0\FreeSpaceWatcher.Tray.exe'
$installedTray = 'C:\Program Files\FreeSpaceWatcher\FreeSpaceWatcher.Tray.exe'
$trayProcessName = 'FreeSpaceWatcher.Tray'
$logPath = Join-Path $env:LOCALAPPDATA 'FreeSpaceWatcher\tray.log'
$statusTitle = 'Free Space Watcher'
# The menu items end in a horizontal ellipsis; spelled as a code point so this file stays ASCII.
$ellipsis = [char]0x2026
$Uia = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]
$ControlType = [System.Windows.Automation.ControlType]
$mouse = @{ LeftDown = 0x0201; LeftUp = 0x0202; LeftDoubleClick = 0x0203; RightDown = 0x0204; RightUp = 0x0205 }
$script:logOffset = 0
$script:trayProcess = $null
$script:iconWindow = [IntPtr]::Zero

function Wait-Until {
    param([Parameter(Mandatory)][scriptblock]$Probe, [Parameter(Mandatory)][string]$What, [double]$Seconds = 10)

    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $value = & $Probe
        if ($value) {
            return $value
        }

        Start-Sleep -Milliseconds 200
    }

    throw "Timed out after $Seconds s waiting for $What."
}

function Find-Descendant {
    param([Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$Element, [Parameter(Mandatory)][scriptblock]$Where)

    $Element.FindAll($Scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object $Where | Select-Object -First 1
}

function Get-TrayWindow {
    param([string]$Title)

    if ($script:trayProcess.HasExited) {
        throw "the tray exited (code $($script:trayProcess.ExitCode)); see $logPath."
    }

    $byProcess = [System.Windows.Automation.PropertyCondition]::new($Uia::ProcessIdProperty, $script:trayProcess.Id)
    $Uia::RootElement.FindAll($Scope::Children, $byProcess) |
        Where-Object { $_.Current.ControlType -eq $ControlType::Window -and (-not $Title -or $_.Current.Name -eq $Title) }
}

# A real click reaches the icon as down then up; a double-click adds WM_LBUTTONDBLCLK and a second up.
function Send-IconClick {
    param([Parameter(Mandatory)][ValidateSet('Left', 'Double', 'Right')][string]$Kind)

    $messages = switch ($Kind) {
        'Left' { $mouse.LeftDown, $mouse.LeftUp }
        'Double' { $mouse.LeftDown, $mouse.LeftUp, $mouse.LeftDoubleClick, $mouse.LeftUp }
        'Right' { $mouse.RightDown, $mouse.RightUp }
    }
    foreach ($message in $messages) {
        [FswTrayIcon]::Post($script:iconWindow, $message)
        Start-Sleep -Milliseconds 50
    }
}

function Invoke-TrayMenu {
    param([Parameter(Mandatory)][string]$Item)

    Send-IconClick Right
    $menuItem = Wait-Until -What "the tray menu's '$Item'" -Probe {
        Get-TrayWindow | ForEach-Object { Find-Descendant $_ { $_.Current.ControlType -eq $ControlType::MenuItem -and $_.Current.Name -eq $Item } } |
            Select-Object -First 1
    }
    $menuItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Read-NewLogEntry {
    if (-not (Test-Path $logPath)) {
        return , @()
    }

    if ((Get-Item $logPath).Length -lt $script:logOffset) {
        $script:logOffset = 0
    }

    $stream = [IO.File]::Open($logPath, 'Open', 'Read', 'ReadWrite')
    try {
        [void]$stream.Seek($script:logOffset, 'Begin')
        $text = [IO.StreamReader]::new($stream).ReadToEnd()
        $script:logOffset = $stream.Position
    } finally {
        $stream.Dispose()
    }

    return , @($text -split '\r?\n' | Where-Object { $_ -match '^\d{4}-\d\d-\d\d .* WARN ' })
}

function Confirm-TrayHealthy {
    param([Parameter(Mandatory)][string]$Step)

    $script:trayProcess.Refresh()
    if ($script:trayProcess.HasExited) {
        throw "$Step`: the tray exited (code $($script:trayProcess.ExitCode))."
    }

    if (-not $script:trayProcess.Responding) {
        throw "$Step`: the tray is not responding."
    }

    $warnings = Read-NewLogEntry
    if ($warnings.Count -gt 0) {
        throw "$Step`: the tray logged $($warnings.Count) warning(s), first: $($warnings[0])  (see $logPath)"
    }
}

function Wait-TrayWindow {
    param(
        [Parameter(Mandatory)][string]$Title,
        [Parameter(Mandatory)][scriptblock]$KeyControl,
        [Parameter(Mandatory)][string]$KeyName,
        [double]$Seconds = 10
    )

    $isKey = $KeyControl
    $window = Wait-Until -What "the '$Title' window" -Probe { Get-TrayWindow $Title | Select-Object -First 1 }
    [void](Wait-Until -What "'$KeyName' on screen in the '$Title' window" -Seconds $Seconds -Probe {
            Find-Descendant $window { (& $isKey) -and -not $_.Current.BoundingRectangle.IsEmpty -and -not $_.Current.IsOffscreen }
        })
}

function Close-TrayWindow {
    param([Parameter(Mandatory)][string]$Title)

    foreach ($window in @(Get-TrayWindow $Title)) {
        $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    }

    [void](Wait-Until -What "the '$Title' window to close" -Probe { -not (Get-TrayWindow $Title) })
}

function Invoke-Step {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][scriptblock]$Action)

    Write-Information "- $Name"
    & $Action
    Start-Sleep -Milliseconds 500
    Confirm-TrayHealthy $Name
}

function Invoke-SmokeRun {
    $showsSettings = { Wait-TrayWindow 'Settings' { $_.Current.ControlType -eq $ControlType::Button -and $_.Current.Name -eq 'Save' } 'Save' }
    $showsAlerts = { Wait-TrayWindow 'Alerts' { $_.Current.ControlType -eq $ControlType::List -and $_.Current.Name -eq 'Alert history' } 'Alert history' }
    $isConnected = { $_.Current.ControlType -eq $ControlType::Text -and $_.Current.Name -match '^Connected' }
    $showsStatus = { Wait-TrayWindow $statusTitle $isConnected 'Connected' }

    Invoke-Step 'Left-click on the icon opens Status, connected' {
        Send-IconClick Left
        Wait-TrayWindow $statusTitle $isConnected 'Connected' -Seconds 20
        Close-TrayWindow $statusTitle
    }
    Invoke-Step 'Settings from the menu, then closed' { Invoke-TrayMenu "Settings$ellipsis"; & $showsSettings; Close-TrayWindow 'Settings' }
    Invoke-Step 'Alerts from the menu' { Invoke-TrayMenu "Alerts$ellipsis"; & $showsAlerts }
    Invoke-Step 'Settings from the menu while Alerts is open' {
        Invoke-TrayMenu "Settings$ellipsis"; & $showsSettings; Close-TrayWindow 'Settings'
    }
    Invoke-Step 'Status from the menu, then closed' { Invoke-TrayMenu "Status$ellipsis"; & $showsStatus; Close-TrayWindow $statusTitle }
    Invoke-Step 'Double-click on the icon opens Status' { Send-IconClick Double; & $showsStatus }
    Invoke-Step 'Alerts and Status closed' { Close-TrayWindow 'Alerts'; Close-TrayWindow $statusTitle }

    Write-Information '- Exit from the menu'
    Invoke-TrayMenu 'Exit'
    if (-not $script:trayProcess.WaitForExit(5000)) {
        throw 'Exit from the menu: the tray was still running 5 s later.'
    }

    $warnings = Read-NewLogEntry
    if ($warnings.Count -gt 0) {
        throw "Exit from the menu: the tray logged $($warnings.Count) warning(s), first: $($warnings[0])"
    }
}

if ((Get-Service FreeSpaceWatcher -ErrorAction SilentlyContinue)?.Status -ne 'Running') {
    throw 'The FreeSpaceWatcher service is not running; install it (scripts/install.ps1) or start it, then run this again.'
}

dotnet build $trayProject --nologo -v:minimal
if ($LASTEXITCODE -ne 0) {
    throw "Building the tray failed (exit code $LASTEXITCODE)."
}

$installedWasRunning = [bool](Get-Process $trayProcessName -ErrorAction SilentlyContinue | Where-Object Path -EQ $installedTray)
Get-Process $trayProcessName -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
$script:logOffset = if (Test-Path $logPath) { (Get-Item $logPath).Length } else { 0 }
$failed = $false
try {
    $script:trayProcess = Start-Process $trayExe -PassThru
    $script:iconWindow = Wait-Until -What "the tray icon's message window" -Probe {
        $handle = [FswTrayIcon]::FindMessageWindow($script:trayProcess.Id)
        if ($handle -ne [IntPtr]::Zero) { $handle }
    }
    Invoke-SmokeRun
    Write-Information 'UI smoke passed.'
} catch {
    $failed = $true
    Write-Error "UI smoke failed: $_" -ErrorAction Continue
} finally {
    if ($script:trayProcess -and -not $script:trayProcess.HasExited) {
        $script:trayProcess | Stop-Process -Force
    }

    if ($installedWasRunning) {
        Write-Information 'Starting the installed tray again.'
        Start-Process $installedTray
    }
}

if ($failed) {
    exit 1
}
