#Requires -Version 7.0
<#
.SYNOPSIS
Removes Free Space Watcher: the service, the tray app's autostart and notifications, the Event Log source and the programs.

.DESCRIPTION
Reverses install.ps1. Each step tolerates what is already gone and says so. The data folder under ProgramData (the
configuration and the alert history) is kept unless -Purge is given. Must run elevated.

.PARAMETER Purge
Also delete the data folder: the configuration and the alert history.

.PARAMETER InstallDir
The folder install.ps1 copied the programs into.
#>
# The functions below are this script's own steps, not commands for callers; the script as a whole offers no -WhatIf.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Private steps')]
[CmdletBinding()]
param([switch]$Purge, [string]$InstallDir = "$env:ProgramFiles\FreeSpaceWatcher")

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$InformationPreference = 'Continue'

$serviceName = 'FreeSpaceWatcher'
$trayProcessName = 'FreeSpaceWatcher.Tray'
$runKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
$runValueName = 'FreeSpaceWatcherTray'
$trayExe = Join-Path $InstallDir 'FreeSpaceWatcher.Tray.exe'
$dataDir = Join-Path $env:ProgramData 'FreeSpaceWatcher'

function Test-Elevated {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-Sc {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $output = & sc.exe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe $($Arguments -join ' ') failed with exit code ${LASTEXITCODE}: $($output -join ' ')"
    }
}

function Stop-Tray {
    $trays = @(Get-Process -Name $trayProcessName -ErrorAction SilentlyContinue)
    if ($trays.Count -eq 0) {
        Write-Information 'No tray app is running.'
        return
    }

    $trays | Stop-Process -Force
    $trays | Wait-Process -Timeout 10
    Write-Information "Stopped $($trays.Count) running tray app(s)."
}

function Remove-ToastRegistration {
    if (-not (Test-Path -LiteralPath $trayExe)) {
        Write-Information 'The tray app is not installed; there is no notification registration to remove.'
        return
    }

    # Explorer starts what it opens with the shell's own token, so the tray cleans the interactive user's registration.
    # Explorer passes no arguments to a program it opens, so it opens a shortcut that carries the switch instead.
    $shortcutPath = Join-Path $InstallDir 'uninstall-notifications.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $trayExe
    $shortcut.Arguments = '--uninstall-notifications'
    $shortcut.Save()
    Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$shortcutPath`""

    Start-Sleep -Seconds 1
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Process -Name $trayProcessName -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }

    if (Get-Process -Name $trayProcessName -ErrorAction SilentlyContinue) {
        Write-Warning 'The tray app did not finish removing its notification registration within 10 s.'
        return
    }

    Write-Information 'Removed the tray app''s notification registration.'
}

function Remove-WatcherService {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        Write-Information "The $serviceName service is already gone."
        return
    }

    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }

    Invoke-Sc -Arguments 'delete', $serviceName
    Write-Information "Stopped and deleted the $serviceName service."
}

function Remove-TrayAutostart {
    if ($null -eq (Get-ItemProperty -Path $runKey -Name $runValueName -ErrorAction SilentlyContinue)) {
        Write-Information "The tray autostart entry ($runKey\$runValueName) is already gone."
        return
    }

    Remove-ItemProperty -Path $runKey -Name $runValueName
    Write-Information "Removed the tray autostart entry ($runKey\$runValueName)."
}

function Remove-EventSource {
    if (-not [System.Diagnostics.EventLog]::SourceExists($serviceName)) {
        Write-Information "The Event Log source $serviceName is already gone."
        return
    }

    [System.Diagnostics.EventLog]::DeleteEventSource($serviceName)
    Write-Information "Deleted the Event Log source $serviceName."
}

function Remove-Folder {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$What)
    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Information "The $What ($Path) is already gone."
        return
    }

    Remove-Item -LiteralPath $Path -Recurse -Force
    Write-Information "Deleted the $What ($Path)."
}

if (-not (Test-Elevated)) {
    $host.UI.WriteErrorLine('uninstall.ps1 must run elevated: start PowerShell 7 with "Run as administrator" and run it again. Nothing was changed.')
    exit 1
}

Stop-Tray
Remove-ToastRegistration
Remove-WatcherService
Remove-TrayAutostart
Remove-EventSource
Remove-Folder -Path $InstallDir -What 'program folder'
if ($Purge) {
    Remove-Folder -Path $dataDir -What 'data folder'
}
else {
    Write-Information "Kept the data folder ($dataDir); run with -Purge to delete it."
}

Write-Information 'Free Space Watcher is uninstalled.'
