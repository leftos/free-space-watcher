namespace FreeSpaceWatcher.Core.Config;

/// <summary>Whether one drive is watched, and its threshold overrides.</summary>
/// <param name="Letter">The drive letter without a colon, e.g. "C".</param>
/// <param name="Enabled">Whether the drive is watched.</param>
/// <param name="Overrides">Thresholds that replace the defaults for this drive; blank fields use the defaults.</param>
public sealed record DriveConfig(string Letter, bool Enabled, Thresholds Overrides);
