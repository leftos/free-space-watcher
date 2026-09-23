# Free Space Watcher — main plan

Design: [free-space-watcher.md](./free-space-watcher.md) (the approved design; read it before any step). Terms: [glossary](../README.md#glossary).

## Current focus: first build

- [x] 0. Public GitHub repo `leftos/free-space-watcher`, initial commit pushed (user request 2026-09-23)
- [x] 1. Repo scaffold: solution, six projects, conventions, empty build clean, prek installed
- [x] 2. Core: config + validation, TriggerEvaluator, WriteAggregator, IPC contracts, HistoryStore, with tests
- [x] 3. Service: DriveSampler, WriteCollector (ETW), AlertEngine, PipeServer, ProcessActions, Event Log; ETW integration test
- [x] 4. Tray: pipe client, tray icon, settings window, alerts/details window, toasts
- [x] 5. Elevate helper; install/uninstall/fill-test scripts; README usage
- [ ] 6. End-to-end verification on this machine (design doc, "Verification"); includes the first elevated run of `dotnet test tests/FreeSpaceWatcher.Service.Tests` (ETW integration test, never yet run) and settling the "Known limits" wording from it

## Backlog

(empty)
