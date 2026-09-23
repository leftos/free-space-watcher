# UI design pass

User request 2026-09-23: make the tray UI look finished rather than scaffolded. Decisions from the interview: the built-in WPF Fluent theme (`ThemeMode`, no new dependency); scope covers the Alerts window, Settings window, a new Status window, and the tray icon and toasts; the tray gains `--open alerts|settings|status` and `--theme light|dark|system` so windows can be captured to PNG for review (and opened by scripts and agents).

## Design language

- Theme brushes only (`SystemFillColor*`, `TextFillColor*`, `CardBackgroundFillColor*`, `ControlStrokeColor*`, `AccentFillColor*`); no hard-coded colours, so light and dark both work. Severity colours: `SystemFillColorCriticalBrush` (alerting, Kill), `SystemFillColorCautionBrush` (low but not alerting), `SystemFillColorSuccessBrush` (healthy).
- Spacing on a 4 px grid: 8 inside controls, 12 between related items, 16 card padding, 24 between sections.
- Type ramp: Title 20 semibold, Subtitle 14 semibold, Body 14, Caption 12 in secondary text colour. Numbers that matter (free space, rate) use Title size.
- Cards: corner radius 8, card background and stroke brushes, 16 padding.
- Icons: Segoe Fluent Icons glyphs on icon buttons, each with a tooltip and an `AutomationProperties.Name`.
- Sizes: 8 GB style values via the existing `ByteFormat`; times relative ("2 min ago") with the absolute time in a tooltip.
- A free-space bar: a capsule showing the used fraction, coloured by state (critical when the drive has an unacknowledged alert or is below its floor, caution when below twice the floor, success otherwise).

## Passes

1. **Foundation + Status window** — theme and shared resources, `--open`/`--theme`, `scripts/capture-ui.ps1`, the Status window, and a service `GetDriveHistoryRequest` for its sparkline. Left-clicking the tray opens Status.
2. **Alerts + Settings** — master–detail Alerts with list items (severity dot, drive chip, two-line reason, relative time), a details header card with a metrics row, writer cards with bytes bars relative to the top writer, a state pill and actions, and Folders/Files expanders with middle-trimmed paths and icon buttons; the action status line becomes an info strip. Settings as drive cards (watch toggle, free bar, "Custom thresholds" expander with a two-column form and unit suffixes), a Defaults card and an Advanced expander, with a sticky Save/Cancel footer.
3. **Tray icon + toasts** — a drive glyph whose fill shows the most-used watched drive and whose colour shows state; toasts with a hero line ("C: full in ~6 min"), a body line ("Losing 11.2 GB/min · 61.5 GB free"), an attribution line naming the top writer and folder, and a progress bar showing the drive's used fraction.

Each pass ends with light and dark captures of every window it touched, reviewed before the next pass starts.
