# Free Space Watcher — plan

## Context

A disk on this machine (C: in particular) can be filled by a runaway writer: a log loop, a build cache, a crash-dump storm, a sync client. By the time Windows says "low disk space" it is too late to find the culprit calmly. The goal is an always-on watcher that notices free space falling fast, before it hits 0 bytes, and tells the user **which processes are writing, into which folders and files**, with one-click ways to stop them. A UI picks which drives are watched and their thresholds.

`X:\dev\free-space-watcher` is empty (not yet a git repo). .NET SDK 10.0.401 is installed.

Decisions taken in the interview:
- **Architecture:** LocalSystem Windows service (runs from boot, no UAC) + a per-user WPF tray app started at logon, talking over a named pipe. The service cannot show toasts from session 0, so the tray app does all UI.
- **Attribution:** an always-on kernel ETW session keeps a rolling window of bytes written per process → folder → file, so an alert includes what happened *before* the drop was noticed.
- **Triggers (all four, per drive):** drop rate, time-to-full estimate, low free-space floor, per-process write volume.
- **Actions:** open folder / file location, suspend (and resume) process, kill process.
- **Install:** `install.ps1` / `uninstall.ps1` run elevated once.
- **History:** one JSON file per alert under `%ProgramData%\FreeSpaceWatcher\alerts`, pruned after N days.
- **No tray connected:** write to the Windows Event Log and history; the tray shows unacknowledged alerts when it connects.

## Stack and packages (latest stable, looked up on nuget.org 2026-09-23)

- C# / .NET 10, nullable on; conventions copied from `~/.claude/skills/language-conventions/csharp/` (`.editorconfig`, `.csharpierrc`, `Directory.Build.props` fragment with `TreatWarningsAsErrors`, prek hooks, `.config/dotnet-tools.json` with CSharpier).
- `Microsoft.Diagnostics.Tracing.TraceEvent` 3.2.6 — kernel ETW session and FileIO parsing.
- `Microsoft.Extensions.Hosting.WindowsServices` 10.0.12 — service host.
- `H.NotifyIcon.Wpf` 2.4.1 — tray icon.
- `Microsoft.Toolkit.Uwp.Notifications` 7.1.3 — toasts with buttons from an unpackaged Win32 app (tray TFM `net10.0-windows10.0.19041.0`).
- `CommunityToolkit.Mvvm` 8.4.2 — view models.
- `xunit.v3` 4.0.1 — tests.

## Solution layout

```
FreeSpaceWatcher.slnx
src/FreeSpaceWatcher.Core/      pure logic, no Windows I/O: config, triggers, aggregation, IPC contracts, history
src/FreeSpaceWatcher.Service/   Worker service: sampler, ETW collector, alert engine, pipe server, process actions
src/FreeSpaceWatcher.Tray/      WPF tray app: settings, live status, alerts + details, toasts
src/FreeSpaceWatcher.Elevate/   tiny console helper, run via UAC only to suspend/kill a process the user's token can't touch
tests/FreeSpaceWatcher.Core.Tests/
tests/FreeSpaceWatcher.Service.Tests/   ETW integration test, skipped when not elevated
scripts/install.ps1, scripts/uninstall.ps1, scripts/fill-test.ps1
docs/README.md (start page + glossary), docs/plans/MAIN.md, CHANGELOG.md
```

## Service design

**DriveSampler** — every 1 s, `GetDiskFreeSpaceEx` on each enabled drive into a ring buffer (≈10 min of samples). Drives that go away (USB unplugged) are marked unavailable, not errors.

**WriteCollector (ETW)** — one `TraceEventSession` named `FreeSpaceWatcher` with kernel keywords `FileIO | FileIOInit | Process` (Windows 10+ allows system providers in a private session, so it doesn't fight the single "NT Kernel Logger"). Handles `FileIOWrite`, `FileIOCreate`, `FileIOSetInfo` (end-of-file growth), `FileIODelete`, `ProcessStart/End`. Device paths (`\Device\HarddiskVolume3\...`) map to drive letters via `QueryDosDevice`, refreshed on volume change. Writes by the service's own process are ignored. If the session dies (another tool stopped it), it is restarted with backoff and logged.

**WriteAggregator (Core, pure)** — per-second buckets over a rolling window (default 5 min) keyed drive → pid → file → {bytesWritten, created, deleted, eofGrowth}. Process name/exe path captured at start so short-lived writers still resolve. Memory is bounded: past a per-process file cap (default 5 000) the rest fold into "other files in <folder>". Queries: top processes for a drive, top folders (roll up files to parent directories) and top files per process.

**TriggerEvaluator (Core, pure)** — per drive, fed samples and aggregator totals:
- *Drop rate:* least-squares slope over the window (default: faster than 1 GB/min over 60 s).
- *Time to full:* free ÷ rate below N minutes (default 15), only when the rate is above a noise floor (default 50 MB/min).
- *Floor:* free below X GB or Y % (default 5 GB / 5 %), re-armed once free climbs back above floor + 10 %.
- *Process write volume:* one process writes more than X GB to the drive within the window (default 10 GB / 5 min).
Each (drive, trigger) has a cooldown (default 10 min) that is bypassed when severity escalates (time-to-full halves, a new process becomes top writer).

**AlertEngine** — on a trigger, builds an `Alert`: drive, trigger, free space, rate, ETA, top 5 processes (pid, name, exe path, bytes), each with top 10 folders and files; top files get their current size stat'd at alert time. "Unattributed" = observed drop minus attributed growth, shown so VSS / pagefile / hibernation growth isn't blamed on an app. The alert is written to history, the Event Log (source `FreeSpaceWatcher`), and pushed to connected tray clients.

**PipeServer** — named pipe `FreeSpaceWatcher`, newline-delimited JSON; message contracts live in Core. ACL: SYSTEM, Administrators and INTERACTIVE. Messages: `GetStatus`, `GetConfig`, `SetConfig`, `Subscribe` (server pushes `DriveStatus` every second and `Alert`), `ListAlerts`, `GetAlert`, `AckAlert`, `SuspendProcess`, `ResumeProcess`, `KillProcess`.

**ProcessActions** — suspend/resume via `NtSuspendProcess`/`NtResumeProcess`, kill via `TerminateProcess`, all done **while impersonating the pipe client** (`ImpersonateNamedPipeClient`) so a non-elevated user can only act on processes they could already touch. Access denied goes back to the tray, which offers "Retry as administrator" through `FreeSpaceWatcher.Elevate` (UAC prompt, since killing an elevated or system process is a destructive action that deserves one). Critical processes (`IsProcessCritical`) and the service itself are refused outright.

**Config** — `%ProgramData%\FreeSpaceWatcher\config.json`, owned and written by the service (default: C: enabled, other fixed drives listed but off). A malformed file is kept as `config.json.bad`, defaults are loaded, and the error goes to the Event Log. Validation rejects nonsense (negative thresholds, window shorter than sample interval) with a message the tray displays.

**History** — `alerts\yyyyMMdd-HHmmss-<drive>-<trigger>.json` with an `acknowledged` flag; pruned after `historyDays` (default 30).

## Tray app design

- **Tray icon** with three states: OK, unacknowledged alert, service not reachable. Tooltip lists each watched drive's free space and ETA. Reconnects to the pipe with backoff.
- **Settings window** — table of ready fixed/removable drives: watch on/off, and per-drive overrides of the four thresholds (blank = default); a defaults section; window lengths, cooldown, history days. Live column: free, rate, ETA.
- **Alerts window** — history list (newest first, unacknowledged bold). Details pane: process → folders → files tree with bytes written and current size; buttons Open folder, Open file location (`explorer /select,`), Suspend/Resume, Kill (confirmation dialog), Acknowledge.
- **Toasts** — title "C: losing 2.1 GB/min — full in ~9 min", body names the top writer and its top folder; buttons Details, Suspend <process>, Open folder. Kill stays in the details window behind a confirmation.
- On connect, unacknowledged alerts produce one summary toast rather than a burst.

## Install scripts

`install.ps1` (checks elevation, pwsh 7): `dotnet publish` the four exes (framework-dependent, Release) into `C:\Program Files\FreeSpaceWatcher`, `New-Service` as LocalSystem / Automatic, `sc.exe failure` restart actions, create the Event Log source, create `%ProgramData%\FreeSpaceWatcher` (SYSTEM/Administrators full, Users read), add the tray to `HKLM\...\Run`, start the service, launch the tray. Idempotent: re-running upgrades in place (stop service, copy, start). `uninstall.ps1` reverses it and keeps ProgramData unless `-Purge`.

## Repo setup (first step)

`git init`, `.gitignore` (bin/obj/.tmp), copy the C# convention files, `prek install`, `docs/plans/MAIN.md` seeded with this plan's steps as checkboxes, `docs/README.md` with a glossary (terms such as *drop rate*, *time to full*, *unattributed*, *write window*, *re-arm*), `CHANGELOG.md`.

## Execution order

Executed through the `plan-execution` skill with the `implementer` agent:
1. Repo scaffold, conventions, empty projects building clean.
2. Core: config + validation, TriggerEvaluator, WriteAggregator, IPC contracts, HistoryStore — with tests.
3. Service: DriveSampler, WriteCollector, AlertEngine, PipeServer, ProcessActions, Event Log; ETW integration test.
4. Tray: pipe client, tray icon, settings window, alerts/details window, toasts.
5. Elevate helper; install/uninstall/fill-test scripts; README.

## Tests

- **TriggerEvaluator:** steady state never alerts; noise below the floor never alerts; a ramp crosses drop-rate at the right sample; ETA math; cooldown suppresses, escalation bypasses; floor re-arm hysteresis; drive disappearing mid-window.
- **WriteAggregator:** window eviction; per-process file cap overflow into "other files"; folder roll-up; top-N ordering and ties; pid reuse after process end.
- **Config:** round-trip; malformed JSON → defaults + `.bad` file; validation errors.
- **HistoryStore:** write/list/ack; pruning boundary.
- **IPC contracts:** every message type round-trips through the serializer.
- **ETW integration (elevated only):** the test writes 200 MB into a temp file and asserts the collector attributes it to the test's pid and exact path.

## Verification

1. `dotnet build` (warnings as errors), `dotnet csharpier check .`, `dotnet format style/analyzers --verify-no-changes --severity info`, `dotnet test`, and the ETW test from an elevated shell.
2. Run `install.ps1` elevated; `Get-Service FreeSpaceWatcher` is Running; the tray icon appears and shows C:.
3. Temporarily lower C: thresholds in Settings, run `scripts/fill-test.ps1` (writes a temp file at a chosen MB/s into a named folder, deletes it on exit). Expect a toast naming `pwsh` and that folder; the details window shows the file; Suspend freezes the write rate, Resume restarts it, Kill ends it.
4. Kill the tray, trigger again: an Event Log entry is written; relaunching the tray shows the unacknowledged alert.
5. Reboot: the service is running before logon and alerts from before logon appear on connect.
6. `uninstall.ps1`: service, Run entry and Event Log source gone.

## Known limits (documented in README)

- Bytes *written* ≠ space *consumed*: overwrites and deleted temp files inflate writes; the details show created/deleted and current size to compensate.
- Paging-I/O writes (lazy-writer cache flushes and mapped-page-writer flushes, `IRP_PAGING_IO` set in the FileIo event's IoFlags) are dropped so cached writes are not counted twice, once for the app and once for `System`. A program that writes only through memory-mapped views shows up through an explicit end-of-file change of its own, or, if it never makes one, only in the "unattributed" figure.
- Any interactive user can change which drives are watched (single-user machine assumption).

## ETW file-event findings (measured 2026-09-23, Windows 11 26200)

Measured with a raw capture of a cached, sequential 2 GiB writer, using the same kernel keywords as `WriteCollector` and losing no events.

- A `WriteFile` whose fast-I/O attempt a filter driver refuses (`STATUS_FLT_DISALLOW_FASTIO`, `0xC01C0004`; seen wherever the write extended the file's allocation) is logged as two Write events: IoFlags `0x0` for the attempt, then the IRP's flags (`0x60A00`) for the retry, on the same thread, FileObject, offset and size, within 0.3 ms. IrpPtr is not always equal. The collector drops the retry.
- A cached writer produces no EndOfFile (class 20) SetInfo event of its own: NTFS grows EOF inside the write path. The class-20 events that do arrive come from `System` and carry the valid data length after each lazy-writer flush, not the file size. Growth is therefore derived from the writer's own write extents, and `System`'s class-20 events are ignored.
- `FileObject` and `FileKey` addresses are reused by other files within milliseconds of a close. Match file events by resolved name, never by a remembered address.
- `FileInfo.Length` is exact for a file another process is still writing (zero lag in 17 samples), so an alert's `CurrentSize` is trustworthy ground truth.
