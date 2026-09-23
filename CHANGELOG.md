# Changelog

## Unreleased

### Added

- A boot-time service watches chosen drives and alerts when free space falls fast, is about to run out, or drops below a floor.
- Alerts name the processes writing to the drive, with their top folders and files, from always-on write tracing.
- A per-process write-volume alert catches runaway writers even while free space is still plentiful.
- A tray app shows toasts, per-drive free space and time to full, and the alert history.
- A settings window picks watched drives and per-drive thresholds.
- Suspend, resume or kill a writing process from an alert, with a UAC retry when your account can't touch it.
- Open a writer's folder or show a file in Explorer from an alert or its toast.
- Acknowledge all, Clear and Clear all in the Alerts window and its right-click menu, plus Acknowledge all in the tray menu.
- Alerts are kept for 30 days and written to the Windows event log.
- Install, uninstall and fill-test scripts.
