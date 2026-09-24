# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Free Space Watcher is a Windows-only .NET 10 app: a LocalSystem service that samples drive free space and traces per-process file writes through kernel ETW, plus a WPF tray app that shows toasts, status and alert history. Start with [docs/README.md](docs/README.md): it links the plan index and the approved design, and its glossary defines the project's terms (drop rate, time to full, write window, unattributed, re-arm, escalation, grace delay, auto-resolve, remaining growth, etc.). Use those terms as defined there, and add any new term to the glossary in the commit that first uses it.

## Commands

Run from PowerShell 7 at the repo root.

```powershell
dotnet build FreeSpaceWatcher.slnx                      # warnings are errors (Directory.Build.props)
dotnet test FreeSpaceWatcher.slnx
dotnet test tests/FreeSpaceWatcher.Core.Tests --filter "FullyQualifiedName~TriggerEvaluatorTests"   # one class or test
dotnet csharpier check .                                 # formatter (local tool: dotnet tool restore)
dotnet format style --verify-no-changes --severity info FreeSpaceWatcher.slnx
dotnet format analyzers --verify-no-changes --severity info FreeSpaceWatcher.slnx
prek run                                                 # pre-commit: format style, csharpier, build with warnings as errors
```

- Tests are xUnit v3 on the VSTest runner (`xunit.v3.mtp-off`), so `--filter` uses VSTest syntax. Test names follow `Subject_Condition_Result` (CA1707 is suppressed in test projects for that).
- `WriteCollectorIntegrationTests` (real ETW kernel session) runs only from an elevated shell and calls `Assert.Skip` otherwise.
- `scripts/install.ps1` / `scripts/uninstall.ps1 [-Purge]` (elevated) publish to `C:\Program Files\FreeSpaceWatcher`, register the service and the tray's Run entry. Re-running install upgrades in place.
- `scripts/fill-test.ps1 -MBPerSecond 200 -Seconds 60` writes a temp file at a steady rate to set off alerts end to end.
- `scripts/ui-smoke.ps1` builds the Debug tray and drives it without the mouse. It posts icon clicks to the H.NotifyIcon message window, invokes menu items and closes windows through UI Automation. It opens each window from the menu and from a left- or double-click, then exits. It fails when a window or its key control doesn't appear, or when the tray exits, stops responding or logs a WARN to `tray.log`. Run it after every tray change; unit tests don't catch layout-time exceptions. It needs the service running and steals focus for about 20 s.
- `scripts/capture-ui.ps1 -Window status|alerts|settings|icons -Theme light|dark|system` starts the Debug tray with `--theme`/`--open`/`--select-latest` and saves PNGs of its windows, for reviewing UI changes. The tray is single-instance per session, so the script stops the running tray and restarts the installed one afterwards.
- The service accepts `--data-dir <path>` to use a folder other than `%ProgramData%\FreeSpaceWatcher` (tests and smoke runs use it).

## Architecture

Six projects (`src/`) and three test projects (`tests/`, one per Core/Service/Tray):

- **Core** — pure logic, no Windows I/O: config model and validation, `TriggerEvaluator` (the four triggers, cooldown, escalation, re-arm), `WriteAggregator` (rolling per-second buckets keyed drive → pid → file), `HistoryStore` (alert JSON files), and the IPC contracts. Keep new decision logic here so it is testable without a service or ETW.
- **Service** — generic host run as a Windows service (`ServiceHost.RunAsync`). It claims the named pipe *before* building the host, so a second instance exits (code 2) without touching the data folder. Components: `DriveSampler` (1 s free-space samples), `Etw/WriteCollector` (kernel `FileIO | FileIOInit | Process` session feeding the aggregator), `AlertEngine` (runs triggers, builds alerts, grace delay and auto-resolve via file-size probes), `PipeServer`, `ProcessActions`, `ConfigService`, `StatusHub`. Warnings and above go to the Application event log, source `FreeSpaceWatcher`.
- **Native** — P/Invoke for process suspend/resume/kill and the critical-process guard, shared by Service and Elevate.
- **Elevate** — small console exe run through UAC only when the user's own token cannot touch a process.
- **Tray** — WPF (CommunityToolkit.Mvvm, H.NotifyIcon, UWP toasts). `Pipe/PipeClient` behind `IServiceChannel` (tests use `FakePipeServer`); windows for Status, Alerts and Settings, each with a view model; `Icons/` renders the tray icon per state; logs to `%LOCALAPPDATA%\FreeSpaceWatcher\tray.log` through `ITrayLog` (tests use `FakeTrayLog`).

The service and tray talk only over the named pipe `FreeSpaceWatcher`: newline-delimited JSON, one `PipeMessage` subtype per line, with the polymorphic contracts in `Core/Ipc` and a source-generated `CoreJsonContext`. A new message needs its `[JsonDerivedType]` registration on `PipeMessage`, a handler in `Service/Pipe/PipeRequestHandler.cs`, and a round-trip test in `PipeProtocolTests`. Process actions run while impersonating the pipe client, so a non-elevated user can act only on processes they could already touch; access denied is surfaced to the tray, which offers the Elevate retry.

The service owns and writes `config.json`; the tray only reads and sends it via `GetConfig`/`SetConfig`. Alerts live as `alerts\yyyyMMdd-HHmmss-<drive>-<trigger>.json` under the data folder.

## ETW accounting rules

These were measured, not assumed (details in the design doc's "ETW file-event findings"); don't undo them without a new measurement:

- Paging-I/O writes (`IRP_PAGING_IO`) are dropped so lazy-writer flushes are not counted twice under `System`.
- A fast-I/O retry (a second Write event for the same thread, FileObject, offset and size) is dropped by `FastIoRetryFilter`.
- File growth comes from the writer's own write extents; `System`'s EndOfFile SetInfo events report valid data length, not size, and are ignored.
- FileObject/FileKey addresses are reused within milliseconds: match file events by resolved name, never by a remembered address.

## Conventions

- `Nullable`, `TreatWarningsAsErrors`, `EnforceCodeStyleInBuild` and `AnalysisLevel=latest-recommended` apply to every project; package versions are central in `Directory.Packages.props`. `.editorconfig` sets LF endings, 150-char lines, file-scoped namespaces, required braces and namespace-matches-folder.
- Public members carry XML doc comments.
- Work is tracked in [docs/plans/MAIN.md](docs/plans/MAIN.md) (checkbox list with linked subplans). `CHANGELOG.md` has an `Unreleased` section in user-facing wording.
