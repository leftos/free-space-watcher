# Free Space Watcher

A Windows service plus tray app that warns when a drive's free space is falling fast, before it reaches 0 bytes, and names the processes, folders and files doing the writing, with one-click suspend, kill and open-folder actions.

- A LocalSystem service starts at boot (no UAC prompt), samples free space every second and traces file writes per process through a kernel ETW session.
- Four triggers per drive: drop rate, time to full, a free-space floor, and one process writing too much within the write window.
- A tray app started at logon shows toasts and the alert history, and lets you pick which drives are watched and their thresholds.
- Alerts are kept as JSON in `%ProgramData%\FreeSpaceWatcher\alerts` and written to the Windows Application event log (source `FreeSpaceWatcher`).

## Requirements

Windows 10 or 11, the .NET 10 SDK (to build) and PowerShell 7.

## Install

From an elevated PowerShell 7 prompt in the repo:

```powershell
./scripts/install.ps1
```

It builds the three programs, installs them to `C:\Program Files\FreeSpaceWatcher`, registers and starts the `FreeSpaceWatcher` service, adds the tray to startup for all users and launches it. Re-running it upgrades in place.

To remove everything, `./scripts/uninstall.ps1` (elevated). Add `-Purge` to also delete settings and alert history from `%ProgramData%\FreeSpaceWatcher`.

## Try it

Lower C:'s thresholds in the tray's Settings window (e.g. drop rate 0.5 GB/min), then:

```powershell
./scripts/fill-test.ps1 -MBPerSecond 200 -Seconds 60
```

It writes a temporary file at the given rate and deletes it when it ends. A toast should name `pwsh` and the `fsw-fill-test` folder.

## Build and test

```powershell
dotnet build FreeSpaceWatcher.slnx
dotnet test FreeSpaceWatcher.slnx
```

The ETW integration test runs only from an elevated shell and is skipped otherwise.

## Limits

- Bytes written are not bytes consumed: overwrites and deleted temp files count as writes. The details show created and deleted files and each file's current size.
- Cache-flush and memory-mapped page writes are not attributed to a process, so they don't count twice. A program writing only through memory-mapped views shows up through its file growth, or else in the alert's "unattributed" figure.
- Any interactive user can change which drives are watched.

More: [docs/README.md](docs/README.md) (design, plan and glossary).
