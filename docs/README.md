# Free Space Watcher docs

Free Space Watcher is a Windows service plus tray app that warns when a drive's free space is falling fast and names the processes, folders and files doing the writing.

- Plan index: [plans/MAIN.md](./plans/MAIN.md)
- Design: [plans/free-space-watcher.md](./plans/free-space-watcher.md)

## Glossary

- **Drop rate** — how fast a drive's free space is falling, as the least-squares slope of the free-space samples over the sample window, in bytes per second.
- **Time to full** — free space divided by the drop rate: the estimated time until the drive reaches 0 bytes free.
- **Floor** — a minimum free space (bytes or percent) below which a drive alerts regardless of rate.
- **Write window** — the rolling period (default 5 minutes) over which the service keeps per-process, per-file write totals from ETW.
- **Write volume trigger** — an alert raised when one process writes more than a set amount to a drive within the write window.
- **Unattributed** — the part of an observed free-space drop that no traced file growth explains (shadow copies, pagefile, hibernation file, lazy-writer flushes attributed to System).
- **Re-arm** — the point at which a fired trigger may fire again: the cooldown has passed, or for the floor, free space has climbed back above floor plus a margin.
- **Escalation** — a worse condition on an already-fired trigger (time to full halves, a new top writer appears) that bypasses the cooldown.
- **Cooldown** — the minimum time between two alerts of the same trigger on the same drive.
- **Elevate helper** — `FreeSpaceWatcher.Elevate.exe`, run through a UAC prompt only to suspend, resume or kill a process the user's own token cannot touch.
