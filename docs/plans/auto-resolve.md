# Cleaned-up ramps don't leave alerts

User request 2026-09-23: "if an app cleans up after itself it doesn't become an alert, or the alert is auto-acknowledged."

## Decisions (interview 2026-09-23)

- **Both** a grace delay and auto-resolve.
- **Criterion: the writers' files are deleted or shrunk back.** Free space coming back for other reasons does not count.
- **Resolve window: 5 minutes** after the alert.
- **The per-process write-volume trigger uses net growth** (growth minus what the process deleted or truncated), not bytes written.

## Design

- **Tracked growth.** When an alert is raised (or held), record each top writer's top files with `sizeBefore = CurrentSize − ExtendBytes` (floored at 0). A file's *remaining growth* is `max(0, currentSize − sizeBefore)`, or 0 if it no longer exists. The alert's *remaining fraction* is the sum of remaining growth over the sum of the original growth.
- **Grace delay** (default 20 s, per-drive override, 0 disables): drop-rate and time-to-full firings are held for the delay. Each sampler tick re-stats the tracked files. If the remaining fraction falls to 10 % or below before the delay ends, the firing is discarded (logged at Information, no history entry). Otherwise the alert is raised as today. Floor and write-volume alerts are not delayed: the floor is an absolute condition, and write volume already uses net growth.
- **Auto-resolve** (5 min default, per-drive override): after an alert is raised, the same check runs every tick until the window ends. When the remaining fraction reaches 10 % or below, the alert is marked `Resolved` (a new `ResolvedAt` time and `ResolvedReason`, e.g. "pwsh deleted 14.0 GB it had written") and acknowledged. The tray replaces the drive's toast with a quiet "Resolved" toast, or removes it, and greys it out in the Alerts list. An alert with no tracked growth (all unattributed) never auto-resolves.
- **Net growth for write volume.** The aggregator tracks, per process, the bytes it removed: delete of a file with a known size, or a truncation through an explicit end-of-file change. The trigger compares `ExtendBytes − RemovedBytes` with the threshold.

## Terms

*Grace delay*, *auto-resolve*, *remaining growth*, *net growth* go into the glossary with the change.
