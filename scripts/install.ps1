#Requires -Version 7.0
<#
.SYNOPSIS
Installs Free Space Watcher, or upgrades it in place: the service, the tray app and the elevation helper.

.DESCRIPTION
Publishes the three programs from this repository, copies them into one folder, registers the FreeSpaceWatcher service
(LocalSystem, automatic start, restart on failure), creates the Event Log source and the data folder, starts the tray at
every logon, then starts the service and the tray. Every step can run again; re-running upgrades the installed copy.
Must run elevated.

.PARAMETER InstallDir
The folder the programs are copied into.
#>
# The functions below are this script's own steps, not commands for callers; the script as a whole offers no -WhatIf.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Private steps')]
[CmdletBinding()]
param([string]$InstallDir = "$env:ProgramFiles\FreeSpaceWatcher")

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$InformationPreference = 'Continue'

$serviceName = 'FreeSpaceWatcher'
$trayProcessName = 'FreeSpaceWatcher.Tray'
$runKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
$runValueName = 'FreeSpaceWatcherTray'
$projects = 'FreeSpaceWatcher.Service', 'FreeSpaceWatcher.Tray', 'FreeSpaceWatcher.Elevate'
$repoRoot = Split-Path -Parent $PSScriptRoot
$stagingRoot = Join-Path $repoRoot '.tmp\publish'
$serviceExe = Join-Path $InstallDir 'FreeSpaceWatcher.Service.exe'
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

function Publish-Project {
    param([Parameter(Mandatory)][string]$Name)
    $output = Join-Path $stagingRoot $Name
    if (Test-Path -LiteralPath $output) {
        Remove-Item -LiteralPath $output -Recurse -Force
    }

    $project = Join-Path $repoRoot "src\$Name\$Name.csproj"
    & dotnet publish $project --configuration Release --self-contained false --output $output --nologo --verbosity quiet | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish $Name failed with exit code $LASTEXITCODE."
    }

    return $output
}

function Stop-Running {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -ne $service -and $service.Status -ne 'Stopped') {
        Write-Information "Stopping the $serviceName service."
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }

    $trays = @(Get-Process -Name $trayProcessName -ErrorAction SilentlyContinue)
    if ($trays.Count -gt 0) {
        Write-Information "Stopping $($trays.Count) running tray app(s)."
        $trays | Stop-Process -Force
        $trays | Wait-Process -Timeout 10
    }
}

function Register-Service {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    $binaryPath = "`"$serviceExe`""
    if ($null -eq $service) {
        $description = 'Watches free disk space and alerts when a drive fills fast, naming the processes writing to it.'
        New-Service -Name $serviceName -DisplayName 'Free Space Watcher' -BinaryPathName $binaryPath -StartupType Automatic `
            -Description $description | Out-Null
        Write-Information "Created the $serviceName service."
    }
    else {
        Invoke-Sc -Arguments 'config', $serviceName, 'binPath=', $binaryPath
        Write-Information "Updated the $serviceName service's program path."
    }

    Invoke-Sc -Arguments 'failure', $serviceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/30000/restart/60000'
}

function Register-EventSource {
    if ([System.Diagnostics.EventLog]::SourceExists($serviceName)) {
        Write-Information "The Event Log source $serviceName already exists."
        return
    }

    [System.Diagnostics.EventLog]::CreateEventSource($serviceName, 'Application')
    Write-Information "Created the Event Log source $serviceName."
}

function New-DirectoryRule {
    param([Parameter(Mandatory)][string]$Sid, [Parameter(Mandatory)][System.Security.AccessControl.FileSystemRights]$Rights)
    $inheritance = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    return [System.Security.AccessControl.FileSystemAccessRule]::new(
        [System.Security.Principal.SecurityIdentifier]::new($Sid),
        $Rights,
        $inheritance,
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow)
}

function Initialize-DataDirectory {
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
    $acl = Get-Acl -LiteralPath $dataDir
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($rule in @($acl.Access)) {
        [void]$acl.RemoveAccessRule($rule)
    }

    $acl.AddAccessRule((New-DirectoryRule -Sid 'S-1-5-18' -Rights FullControl))
    $acl.AddAccessRule((New-DirectoryRule -Sid 'S-1-5-32-544' -Rights FullControl))
    $acl.AddAccessRule((New-DirectoryRule -Sid 'S-1-5-32-545' -Rights ReadAndExecute))
    Set-Acl -LiteralPath $dataDir -AclObject $acl
    Write-Information "Set the access rules on $dataDir."
}

function Start-WatcherService {
    $service = Get-Service -Name $serviceName
    try {
        if ($service.Status -ne 'Running') {
            $service.Start()
        }

        $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(15))
    }
    catch {
        $host.UI.WriteErrorLine("The $serviceName service did not reach Running within 15 s: $($_.Exception.Message)")
        $filter = @{ LogName = 'Application'; ProviderName = $serviceName }
        $events = @(Get-WinEvent -FilterHashtable $filter -MaxEvents 5 -ErrorAction SilentlyContinue)
        if ($events.Count -eq 0) {
            $host.UI.WriteErrorLine("The Application log has no entries from $serviceName.")
        }

        foreach ($logEvent in $events) {
            $host.UI.WriteErrorLine("$($logEvent.TimeCreated) $($logEvent.LevelDisplayName): $($logEvent.Message)")
        }

        exit 1
    }
}

if (-not (Test-Elevated)) {
    $host.UI.WriteErrorLine('install.ps1 must run elevated: start PowerShell 7 with "Run as administrator" and run it again. Nothing was changed.')
    exit 1
}

$staged = foreach ($name in $projects) {
    Write-Information "Publishing $name."
    Publish-Project -Name $name
}

Stop-Running
if (Test-Path -LiteralPath (Join-Path $InstallDir 'FreeSpaceWatcher.Service.exe')) {
    # Only a folder holding an earlier install is emptied, so files an older build shipped do not linger.
    Get-ChildItem -LiteralPath $InstallDir -Force | Remove-Item -Recurse -Force
}

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
foreach ($folder in $staged) {
    Copy-Item -Path (Join-Path $folder '*') -Destination $InstallDir -Recurse -Force
}

Write-Information "Copied the programs into $InstallDir."
Register-Service
Register-EventSource
Initialize-DataDirectory
New-ItemProperty -Path $runKey -Name $runValueName -Value "`"$trayExe`"" -PropertyType String -Force | Out-Null
Write-Information "The tray app starts at every logon ($runKey\$runValueName)."
Start-WatcherService

# Explorer starts the tray with the shell's own token, so it runs unelevated in the interactive user's session.
Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$trayExe`""

Write-Information ''
Write-Information 'Free Space Watcher is installed.'
Write-Information "  Programs:  $InstallDir"
Write-Information "  Service:   $serviceName ($((Get-Service -Name $serviceName).Status), automatic start)"
Write-Information "  Data:      $dataDir"
Write-Information '  Tray app:  started, and starts at every logon'
