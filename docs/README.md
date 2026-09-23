# Free Space Watcher docs

Free Space Watcher is a Windows service plus tray app that warns when a drive's free space is falling fast and names the processes, folders and files doing the writing.

- Plan index: [plans/MAIN.md](./plans/MAIN.md)
- Design: [plans/free-space-watcher.md](./plans/free-space-watcher.md)

## Glossary

- **Drop rate** — how fast a drive's free space is falling, as the least-squares slope of the free-space samples over the sample window, in bytes per second.
- **Time to full** — free space divided by the drop rate: the estimated time until the drive reaches 0 bytes free.
- **Noise floor** — the loss rate (default 50 MB/min) below which the time-to-full estimate is not computed, so ordinary background churn never produces an ETA.
- **Other files in &lt;folder&gt;** — the `folder\*` entry that collects a process's writes once it has touched more distinct files in the write window than the per-process file cap.
- **Floor** — a minimum free space (bytes or percent) below which a drive alerts regardless of rate.
- **Write window** — the rolling period (default 5 minutes) over which the service keeps per-process, per-file write totals from ETW.
- **Write volume trigger** — an alert raised when one process writes more than a set amount to a drive within the write window.
- **Unattributed** — the part of an observed free-space drop that no traced file growth explains (shadow copies, pagefile, hibernation file, lazy-writer flushes attributed to System).
- **Re-arm** — the point at which a fired trigger may fire again: the cooldown has passed, or for the floor, free space has climbed back above floor plus a margin.
- **Escalation** — a worse condition on an already-fired trigger (time to full halves, a new top writer appears) that bypasses the cooldown.
- **Cooldown** — the minimum time between two alerts of the same trigger on the same drive.
- **Fast-I/O retry** — the second ETW Write event Windows logs for one `WriteFile` when a filter driver refuses the fast path and the write is re-issued as an IRP; the collector drops it so the write counts once.
- **Valid data length** — how far into a file data has actually been written to disk; the lazy writer advances it after each flush, and `System`'s end-of-file events report it, which is why they are not used for growth.
- **Paging I/O** — writes the cache manager or mapped-page writer issues to flush data an app already wrote (`IRP_PAGING_IO`); dropped so cached writes are not counted twice.
- **Net growth** — how much a process grew its files in the write window, minus what it deleted or truncated; the write-volume trigger fires on it, so write-then-delete churn does not alert.
- **Grace delay** — how long (default 20 s) a drop-rate or time-to-full alert is held before it is raised; it is discarded if its writers remove what they wrote in that time.
- **Auto-resolve** — marking a raised alert resolved and acknowledged when its writers remove what they wrote within the resolve window (default 5 min).
- **Remaining growth** — for a tracked file, how much of the growth an alert saw is still on disk (current size minus the size before the growth; 0 once deleted). An alert is discarded or resolved when 10 % or less remains.
- **Elevate helper** — `FreeSpaceWatcher.Elevate.exe`, run through a UAC prompt only to suspend, resume or kill a process the user's own token cannot touch.
