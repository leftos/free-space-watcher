#Requires -Version 7.0
<#
.SYNOPSIS
Fills a drive at a steady rate, to set off Free Space Watcher's alerts on purpose.

.DESCRIPTION
Writes fill.bin into -Folder in 1 MB chunks at -MBPerSecond for -Seconds, printing the drive's free space every second,
then deletes the file (and the folder, when this script created it) unless -Keep is given. Refuses to start when the drive
would be left with less than 5 GB free.

.PARAMETER Folder
The folder to write fill.bin into.

.PARAMETER MBPerSecond
How many megabytes to write each second.

.PARAMETER Seconds
How many seconds to keep writing.

.PARAMETER Keep
Leave fill.bin (and the folder) in place afterwards.
#>
[CmdletBinding()]
param(
    [string]$Folder = "$env:TEMP\fsw-fill-test",
    [ValidateRange(1, 100000)][int]$MBPerSecond = 200,
    [ValidateRange(1, 86400)][int]$Seconds = 60,
    [switch]$Keep
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$InformationPreference = 'Continue'

$chunkBytes = 1MB
$reserveBytes = 5GB
$fullFolder = [System.IO.Path]::GetFullPath($Folder)
$drive = [System.IO.DriveInfo]::new([System.IO.Path]::GetPathRoot($fullFolder))
$neededBytes = [long]$MBPerSecond * $Seconds * $chunkBytes + $reserveBytes
if ($drive.AvailableFreeSpace -lt $neededBytes) {
    $message = 'Not enough free space on {0}: writing {1:N0} MB would leave less than 5 GB free ({2:N2} GB free now).' -f `
        $drive.Name, ([long]$MBPerSecond * $Seconds), ($drive.AvailableFreeSpace / 1GB)
    $host.UI.WriteErrorLine($message)
    exit 1
}

$createdFolder = -not (Test-Path -LiteralPath $fullFolder)
New-Item -ItemType Directory -Path $fullFolder -Force | Out-Null
$file = Join-Path $fullFolder 'fill.bin'
$chunk = [byte[]]::new($chunkBytes)
[System.Random]::Shared.NextBytes($chunk)
$stream = $null
Write-Information "Writing $MBPerSecond MB/s to $file for $Seconds s."
try {
    $stream = [System.IO.FileStream]::new(
        $file, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read, 4096, [System.IO.FileOptions]::None)
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    for ($second = 1; $second -le $Seconds; $second++) {
        for ($i = 0; $i -lt $MBPerSecond; $i++) {
            $stream.Write($chunk, 0, $chunk.Length)
        }

        $stream.Flush()
        Write-Information ('{0,4}/{1} s  written {2:N0} MB  {3} free {4:N2} GB' -f `
                $second, $Seconds, ($stream.Length / 1MB), $drive.Name, ($drive.AvailableFreeSpace / 1GB))
        $untilNextSecond = $second * 1000 - $clock.ElapsedMilliseconds
        if ($untilNextSecond -gt 0) {
            Start-Sleep -Milliseconds $untilNextSecond
        }
    }
}
finally {
    if ($null -ne $stream) {
        $stream.Dispose()
    }

    if ($Keep) {
        Write-Information "Kept $file."
    }
    else {
        if (Test-Path -LiteralPath $file) {
            Remove-Item -LiteralPath $file -Force
        }

        if ($createdFolder) {
            Remove-Item -LiteralPath $fullFolder
        }

        Write-Information "Deleted $file."
    }
}
