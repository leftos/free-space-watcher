#Requires -Version 7.0
<#
.SYNOPSIS
Captures the tray's windows to PNG files, in each theme, for review.

.DESCRIPTION
For each theme and window, starts the tray from the build output (src/FreeSpaceWatcher.Tray/bin/Debug) with --theme and
--open (and --select-latest for the alerts window, so its details show the newest alert), waits up to 10 s for the window, holds
it open for -HoldSeconds, captures it with PrintWindow into <Out>/<window>-<theme>.png, and closes that tray.
The tray connects to whichever service is running; the installed one is fine.

A tray is single-instance per session, so any running tray is stopped first, and the installed tray is started again at the
end (also when a capture fails), so the user's tray comes back.

.PARAMETER Window
The windows to capture: status, alerts, settings; a comma-separated list such as alerts,settings also works under pwsh -File.

.PARAMETER Theme
The themes to capture them in: light, dark, system; comma-separated lists work as for -Window.

.PARAMETER Out
The folder the PNG files go to; a relative path is taken from the repository root.

.PARAMETER HoldSeconds
How long to keep each window open after it appears before capturing it, so live data (such as the status sparkline) can arrive.

.EXAMPLE
pwsh -NoProfile -File scripts/capture-ui.ps1 -Window status -Theme dark
#>
[CmdletBinding()]
param(
    [string[]]$Window = @('status', 'alerts', 'settings'),
    [string[]]$Theme = @('light', 'dark'),
    [string]$Out = '.tmp/ui',
    [ValidateRange(0, 600)][int]$HoldSeconds = 2
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$InformationPreference = 'Continue'

$repoRoot = Split-Path -Parent $PSScriptRoot
$installedTray = 'C:\Program Files\FreeSpaceWatcher\FreeSpaceWatcher.Tray.exe'
$windowTitles = @{
    status = 'Free Space Watcher'
    alerts = 'Alerts'
    settings = 'Settings'
}
$windowTimeout = [TimeSpan]::FromSeconds(10)

# pwsh -File passes "alerts,settings" as one string, so comma-separated names are split here rather than by the parser.
function Split-NameList {
    param(
        [Parameter(Mandatory)][string[]]$Value,
        [Parameter(Mandatory)][string[]]$Allowed,
        [Parameter(Mandatory)][string]$Parameter
    )

    $names = @($Value | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })
    foreach ($name in $names) {
        if ($name -notin $Allowed) {
            throw "-$Parameter '$name' is not one of: $($Allowed -join ', ')."
        }
    }

    return $names
}

$Window = Split-NameList -Value $Window -Allowed @('status', 'alerts', 'settings') -Parameter 'Window'
$Theme = Split-NameList -Value $Theme -Allowed @('light', 'dark', 'system') -Parameter 'Theme'

$drawingReferences = @(
    [System.Drawing.Bitmap].Assembly.Location
    'System.Drawing.Primitives'
    'System.Private.Windows.GdiPlus'
    'System.Private.Windows.Core'
)
Add-Type -ReferencedAssemblies $drawingReferences -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

public static class FswWindowCapture
{
    private const uint PwRenderFullContent = 2;
    private const int DwmwaExtendedFrameBounds = 9;
    private static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out Rect value, int size);

    public static IntPtr Find(int processId, string title)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            uint owner;
            GetWindowThreadProcessId(hWnd, out owner);
            if (owner != processId || !IsWindowVisible(hWnd))
            {
                return true;
            }

            StringBuilder text = new StringBuilder(256);
            GetWindowText(hWnd, text, text.Capacity);
            if (text.ToString() != title)
            {
                return true;
            }

            found = hWnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static void Save(IntPtr hWnd, string path)
    {
        IntPtr previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            Rect window;
            if (!GetWindowRect(hWnd, out window))
            {
                throw new InvalidOperationException("GetWindowRect failed for the window.");
            }

            Rect frame;
            if (DwmGetWindowAttribute(hWnd, DwmwaExtendedFrameBounds, out frame, Marshal.SizeOf(typeof(Rect))) != 0)
            {
                frame = window;
            }

            using (Bitmap full = new Bitmap(window.Right - window.Left, window.Bottom - window.Top, PixelFormat.Format24bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(full))
                {
                    IntPtr hdc = graphics.GetHdc();
                    try
                    {
                        if (!PrintWindow(hWnd, hdc, PwRenderFullContent))
                        {
                            throw new InvalidOperationException("PrintWindow failed for the window.");
                        }
                    }
                    finally
                    {
                        graphics.ReleaseHdc(hdc);
                    }
                }

                Rectangle visible = new Rectangle(frame.Left - window.Left, frame.Top - window.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
                using (Bitmap cropped = full.Clone(visible, full.PixelFormat))
                {
                    cropped.Save(path, ImageFormat.Png);
                }
            }
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }
}
'@

function Stop-TrayProcess {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][System.Diagnostics.Process[]]$Process)

    foreach ($tray in $Process) {
        if ($PSCmdlet.ShouldProcess("FreeSpaceWatcher.Tray ($($tray.Id))", 'Stop')) {
            Stop-Process -Id $tray.Id -Force
            $tray.WaitForExit(5000) | Out-Null
        }
    }
}

function Wait-TrayWindow {
    param(
        [Parameter(Mandatory)][System.Diagnostics.Process]$Tray,
        [Parameter(Mandatory)][string]$Title
    )

    $deadline = [DateTime]::UtcNow + $windowTimeout
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($Tray.HasExited) {
            throw "The tray exited (code $($Tray.ExitCode)) before showing '$Title'."
        }

        $handle = [FswWindowCapture]::Find($Tray.Id, $Title)
        if ($handle -ne [IntPtr]::Zero) {
            return $handle
        }

        Start-Sleep -Milliseconds 200
    }

    throw "The window '$Title' did not appear within $($windowTimeout.TotalSeconds) s."
}

$trayExe = Get-ChildItem -Path (Join-Path $repoRoot 'src/FreeSpaceWatcher.Tray/bin/Debug') -Filter 'FreeSpaceWatcher.Tray.exe' -Recurse -ErrorAction SilentlyContinue |
    Sort-Object -Property LastWriteTime -Descending |
    Select-Object -First 1
if ($null -eq $trayExe) {
    throw 'No FreeSpaceWatcher.Tray.exe under src/FreeSpaceWatcher.Tray/bin/Debug; run dotnet build FreeSpaceWatcher.slnx first.'
}

$outFolder = if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $repoRoot $Out }
New-Item -ItemType Directory -Force -Path $outFolder | Out-Null

$running = @(Get-Process -Name 'FreeSpaceWatcher.Tray' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Information "Stopping $($running.Count) running tray process(es)."
    Stop-TrayProcess -Process $running
}

try {
    foreach ($themeName in $Theme) {
        foreach ($windowName in $Window) {
            $arguments = @('--theme', $themeName, '--open', $windowName)
            if ($windowName -eq 'alerts') {
                $arguments += '--select-latest'
            }
            $tray = Start-Process -FilePath $trayExe.FullName -ArgumentList $arguments -PassThru
            try {
                $handle = Wait-TrayWindow -Tray $tray -Title $windowTitles[$windowName]
                Start-Sleep -Seconds $HoldSeconds
                $png = Join-Path $outFolder "$windowName-$themeName.png"
                [FswWindowCapture]::Save($handle, $png)
                Write-Information "Captured $png"
            }
            finally {
                if (-not $tray.HasExited) {
                    Stop-TrayProcess -Process $tray
                }
            }
        }
    }
}
finally {
    if (Test-Path -LiteralPath $installedTray) {
        Write-Information 'Starting the installed tray again.'
        Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$installedTray`""
    }
    else {
        Write-Information "No installed tray at $installedTray; nothing to start again."
    }
}
