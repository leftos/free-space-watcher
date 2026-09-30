# Free Space Watcher — architecture

A Windows-only .NET 10 app: a LocalSystem service samples drive free space and traces per-process file writes through kernel ETW, and a per-user WPF tray app shows toasts, status and alert history. Five source projects: `Core` (pure logic and the IPC contracts), `Native` (P/Invoke), `Service`, `Elevate` and `Tray`; the service and the tray share no code beyond `Core` and talk only over the named pipe `FreeSpaceWatcher`. The rule that shapes it: decision logic lives in `Core` so it is testable without a service or ETW, and the service owns and writes `config.json`. Terms used in a project sense are in the glossary in [`README.md`](README.md).

## Task Index

| Task | Files, in order | Deep doc |
|---|---|---|
| Add or change a trigger condition | `src/FreeSpaceWatcher.Core/Triggers/TriggerTypes.cs` → `src/FreeSpaceWatcher.Core/Triggers/TriggerEvaluator.cs` → `src/FreeSpaceWatcher.Service/Alerts/AlertEngine.cs` → `src/FreeSpaceWatcher.Tray/Alerts/AlertFormat.cs` → `tests/FreeSpaceWatcher.Core.Tests/Triggers/TriggerEvaluatorTests.cs` | [`plans/free-space-watcher.md`](plans/free-space-watcher.md) |
| Add or change a threshold or config field | `src/FreeSpaceWatcher.Core/Config/Thresholds.cs` → `src/FreeSpaceWatcher.Core/Config/ResolvedThresholds.cs` → `src/FreeSpaceWatcher.Core/Config/ConfigValidator.cs` → `src/FreeSpaceWatcher.Tray/Settings/SettingsViewModel.cs` → `tests/FreeSpaceWatcher.Core.Tests/Config/ConfigValidatorTests.cs` | [`plans/free-space-watcher.md`](plans/free-space-watcher.md) |
| Add a pipe message | `src/FreeSpaceWatcher.Core/Ipc/Requests.cs` (or `Responses.cs`) → `src/FreeSpaceWatcher.Core/Ipc/PipeMessage.cs` → `src/FreeSpaceWatcher.Service/Pipe/PipeRequestHandler.cs` → `src/FreeSpaceWatcher.Tray/Pipe/IServiceChannel.cs` → `tests/FreeSpaceWatcher.Core.Tests/Ipc/PipeProtocolTests.cs` | [`plans/free-space-watcher.md`](plans/free-space-watcher.md) |
| Change what an alert records or how history is stored | `src/FreeSpaceWatcher.Core/Alerts/Alert.cs` → `src/FreeSpaceWatcher.Core/Alerts/HistoryStore.cs` → `src/FreeSpaceWatcher.Service/Alerts/AlertEngine.cs` → `tests/FreeSpaceWatcher.Core.Tests/Alerts/HistoryStoreTests.cs` | [`plans/auto-resolve.md`](plans/auto-resolve.md) |
| Change grace delay or auto-resolve | `src/FreeSpaceWatcher.Service/Alerts/AlertEngine.cs` → `src/FreeSpaceWatcher.Service/Alerts/TrackedGrowth.cs` → `src/FreeSpaceWatcher.Service/Alerts/IFileSizeProbe.cs` → `tests/FreeSpaceWatcher.Service.Tests/Alerts/AlertEngineTests.cs` | [`plans/auto-resolve.md`](plans/auto-resolve.md) |
| Change ETW write accounting | `src/FreeSpaceWatcher.Service/Etw/KernelEventRouter.cs` → `src/FreeSpaceWatcher.Service/Etw/WriteCollector.cs` → `src/FreeSpaceWatcher.Core/Writes/WriteAggregator.cs` → `tests/FreeSpaceWatcher.Service.Tests/Etw/KernelEventRouterTests.cs` | [`plans/free-space-watcher.md`](plans/free-space-watcher.md) |
| Change drive sampling or the status chart data | `src/FreeSpaceWatcher.Service/Sampling/DriveSampler.cs` → `src/FreeSpaceWatcher.Service/Sampling/RecentSamples.cs` → `src/FreeSpaceWatcher.Service/Status/StatusHub.cs` → `src/FreeSpaceWatcher.Tray/Status/StatusViewModel.cs` | [`plans/ui-design.md`](plans/ui-design.md) |
| Change suspend, resume or kill | `src/FreeSpaceWatcher.Service/Processes/ProcessActions.cs` → `src/FreeSpaceWatcher.Native/ProcessControl.cs` → `src/FreeSpaceWatcher.Native/ProcessGuard.cs` → `src/FreeSpaceWatcher.Elevate/Program.cs` → `tests/FreeSpaceWatcher.Service.Tests/Processes/ProcessActionsTests.cs` | [`plans/free-space-watcher.md`](plans/free-space-watcher.md) |
| Change a tray window (Status, Alerts, Settings) | `src/FreeSpaceWatcher.Tray/<Window>/<Window>ViewModel.cs` → `src/FreeSpaceWatcher.Tray/<Window>/<Window>Window.xaml` → `src/FreeSpaceWatcher.Tray/Themes/Styles.xaml` → `tests/FreeSpaceWatcher.Tray.Tests/<Window>/`, then `scripts/ui-smoke.ps1` | [`plans/ui-design.md`](plans/ui-design.md) |
| Change the tray icon or toasts | `src/FreeSpaceWatcher.Tray/Icons/TrayIconRenderer.cs` → `src/FreeSpaceWatcher.Tray/TrayViewModel.cs` → `src/FreeSpaceWatcher.Tray/Toasts/AlertToastContent.cs` → `tests/FreeSpaceWatcher.Tray.Tests/Icons/TrayIconRendererTests.cs` | [`plans/ui-design.md`](plans/ui-design.md) |
| Change service startup, single-instance or logging | `src/FreeSpaceWatcher.Service/Program.cs` → `src/FreeSpaceWatcher.Service/ServiceHost.cs` → `src/FreeSpaceWatcher.Service/Pipe/PipeServer.cs` → `tests/FreeSpaceWatcher.Service.Tests/ServiceHostTests.cs` | [`plans/free-space-watcher.md`](plans/free-space-watcher.md) |
| Change install or uninstall | `scripts/install.ps1` → `scripts/uninstall.ps1` | [`../README.md`](../README.md) |

## Layers

- **`FreeSpaceWatcher.Core`** (`src/FreeSpaceWatcher.Core/`): owns the config model and validation, `TriggerEvaluator`, `WriteAggregator`, `HistoryStore` (alert JSON files), the IPC contracts in `Ipc/` and the source-generated `CoreJsonContext`. References no other project; keeps no Windows I/O beyond its stores (CLAUDE.md: "pure logic, no Windows I/O").
- **`FreeSpaceWatcher.Native`** (`src/FreeSpaceWatcher.Native/`): owns the P/Invoke for process suspend, resume, kill and the critical-process guard. References nothing; shared by Service and Elevate, and Tray does not reference it.
- **`FreeSpaceWatcher.Service`** (`src/FreeSpaceWatcher.Service/`): owns the generic host run as the `FreeSpaceWatcher` Windows service: `DriveSampler`, `Etw/WriteCollector`, `AlertEngine`, `PipeServer`, `ProcessActions`, `ConfigService`, `StatusHub`. References Core and Native; never Tray.
- **`FreeSpaceWatcher.Elevate`** (`src/FreeSpaceWatcher.Elevate/`): a small console exe run through UAC only when the user's own token cannot touch a process. References Core and Native.
- **`FreeSpaceWatcher.Tray`** (`src/FreeSpaceWatcher.Tray/`): owns the WPF tray app: `Pipe/PipeClient` behind `IServiceChannel`, the Status, Alerts and Settings windows with their view models, `Icons/`, `Toasts/`, and the tray log. References Core only; never Service or Native. It reads config through `GetConfig` and changes it through `SetConfig`; it does not write `config.json`.

The pipe is the only runtime link between Service and Tray: newline-delimited JSON, one `PipeMessage` subtype per line. Process actions run while impersonating the pipe client.

## Integration Footguns

- **A new `PipeMessage` subtype** needs its `[JsonDerivedType]` line on `PipeMessage` in `src/FreeSpaceWatcher.Core/Ipc/PipeMessage.cs`, a case in `PipeRequestHandler.Handle` (a request only; an unhandled type gets an `ErrorResponse`) and a sample in `PipeProtocolTests`. `RoundTrip_EveryMessageType` iterates every registered derived type and needs exactly one sample of each, so a missing sample fails; a missing handler case does not.
- **A new type serialized to disk or the pipe** must be reachable from a `[JsonSerializable]` in `src/FreeSpaceWatcher.Core/Serialization/CoreJson.cs` (`WatcherConfig`, `Alert` and `PipeMessage` today); `CoreJsonContext` is source-generated, so an unregistered type fails at runtime, not at compile time.
- **A new threshold field** is spread over `Thresholds` (nullable per-drive override), `ResolvedThresholds` (the defaults) and `ConfigValidator`, plus the Settings view model (`DriveRow`, `SettingsViewModel.LoadDefaults`); no test enforces that they stay in step, and `SettingsConversionTests` covers only the fields it names.
- **A new `TriggerKind`** needs its label in `AlertFormat.TriggerName` and its wording in `AlertToastContent`; both are `switch` expressions in Tray.
- **A service log event id** must be unique and inside 1000-1599 when written as `new EventId(...)`, and inside 1-65535 for `[LoggerMessage]` methods, or the Windows Event Log shows no text; `EventIdTests` enforces it.
- **ETW accounting rules** (drop paging I/O, drop a fast-I/O retry, take growth from write extents, match files by name never by FileObject address) were measured; CLAUDE.md says not to undo them without a new measurement. `FastIoRetryFilterTests` and `KernelEventRouterTests` pin the first two.
- **`scripts/install.ps1` publishes** `FreeSpaceWatcher.Service`, `FreeSpaceWatcher.Tray` and `FreeSpaceWatcher.Elevate` from its `$projects` list; a new deployable exe must be added there.
- **After a tray change** run `scripts/ui-smoke.ps1`: unit tests do not catch layout-time exceptions (CLAUDE.md).

## Test locations

- `tests/FreeSpaceWatcher.Core.Tests/`: triggers, write aggregation, config store and validator, history store and the pipe protocol round trip.
- `tests/FreeSpaceWatcher.Service.Tests/`: alert engine, ETW router and filters, pipe server, process actions and guard, service host, event ids and the elevate argument parser. It references Service and Elevate. `Etw/WriteCollectorIntegrationTests.cs` opens a real ETW kernel session and skips unless elevated.
- `tests/FreeSpaceWatcher.Tray.Tests/`: view models, formatting, icons, toasts and the pipe client against `Pipe/FakePipeServer.cs`; `FakeTrayLog.cs` stands in for `ITrayLog`.

Tests are xUnit v3 on the VSTest runner and named `Subject_Condition_Result`. A new test goes in the project of the layer it pins, in the folder mirroring the source folder.

## Deep docs

- [`README.md`](README.md): docs start page and glossary.
- [`plans/MAIN.md`](plans/MAIN.md): plan index.
- [`plans/free-space-watcher.md`](plans/free-space-watcher.md): the approved design, including the ETW file-event findings.
- [`plans/auto-resolve.md`](plans/auto-resolve.md): grace delay and auto-resolve.
- [`plans/ui-design.md`](plans/ui-design.md): tray window and icon design.
