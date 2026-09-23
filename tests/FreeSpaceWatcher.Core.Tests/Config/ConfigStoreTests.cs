using System.Text.Json.Nodes;
using FreeSpaceWatcher.Core.Config;

namespace FreeSpaceWatcher.Core.Tests.Config;

public sealed class ConfigStoreTests : IDisposable
{
    private static readonly string[] FixedDrives = [@"C:\", @"D:\"];
    private readonly TempDirectory _temp = new();

    private string ConfigPath => Path.Combine(_temp.Path, "config.json");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void MissingFile_WritesDefaults()
    {
        ConfigLoadResult result = new ConfigStore(ConfigPath).Load(FixedDrives);

        Assert.Null(result.Error);
        Assert.True(File.Exists(ConfigPath));
        Assert.Equal(ResolvedThresholds.Default, result.Config.Defaults);
        Assert.Collection(
            result.Config.Drives,
            c => Assert.Equal(new DriveConfig("C", true, new Thresholds()), c),
            d => Assert.Equal(new DriveConfig("D", false, new Thresholds()), d)
        );
        ConfigLoadResult reloaded = new ConfigStore(ConfigPath).Load(FixedDrives);
        Assert.Null(reloaded.Error);
        Assert.Equivalent(result.Config, reloaded.Config, strict: true);
    }

    [Fact]
    public void RoundTrip_PreservesValues()
    {
        WatcherConfig config = new()
        {
            Defaults = ResolvedThresholds.Default with
            {
                DropRateBytesPerMinute = 123_456_789,
                TimeToFullMinutes = 7.5,
                FloorPercent = 2.5,
                ProcessWriteEnabled = false,
                GraceSeconds = 0,
                ResolveMinutes = 30,
            },
            Drives =
            [
                new(
                    "C",
                    true,
                    new Thresholds
                    {
                        FloorBytes = 42,
                        TimeToFullEnabled = false,
                        GraceSeconds = 45,
                        ResolveMinutes = 0,
                    }
                ),
                new("E", false, new Thresholds()),
            ],
            SampleIntervalSeconds = 2,
            RateWindowSeconds = 30,
            WriteWindowSeconds = 120,
            CooldownMinutes = 0,
            HistoryDays = 7,
            PerProcessFileCap = 10,
            TopProcesses = 3,
            TopFolders = 4,
            TopFiles = 6,
        };

        new ConfigStore(ConfigPath).Save(config);
        ConfigLoadResult loaded = new ConfigStore(ConfigPath).Load(FixedDrives);

        Assert.Null(loaded.Error);
        Assert.Equivalent(config, loaded.Config, strict: true);
        Assert.Equal(config.Drives, loaded.Config.Drives);
        Assert.Contains("\"dropRateBytesPerMinute\": 123456789", File.ReadAllText(ConfigPath));
        ResolvedThresholds c = loaded.Config.For("C");
        Assert.Equal((45, 0), (c.GraceSeconds, c.ResolveMinutes));
        ResolvedThresholds e = loaded.Config.For("E");
        Assert.Equal((0, 30), (e.GraceSeconds, e.ResolveMinutes));
    }

    [Fact]
    public void GraceAndResolve_DefaultTo20SecondsAnd5Minutes_WhenAbsentFromTheFile()
    {
        new ConfigStore(ConfigPath).Save(WatcherConfig.CreateDefault(FixedDrives));
        JsonObject root = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
        JsonObject defaults = root["defaults"]!.AsObject();
        Assert.True(defaults.Remove("graceSeconds"));
        Assert.True(defaults.Remove("resolveMinutes"));
        foreach (JsonNode? drive in root["drives"]!.AsArray())
        {
            JsonObject overrides = drive!["overrides"]!.AsObject();
            overrides.Remove("graceSeconds");
            overrides.Remove("resolveMinutes");
        }

        string json = root.ToJsonString();
        Assert.DoesNotContain("graceSeconds", json);
        Assert.DoesNotContain("resolveMinutes", json);
        File.WriteAllText(ConfigPath, json);

        ConfigLoadResult result = new ConfigStore(ConfigPath).Load(FixedDrives);

        Assert.Null(result.Error);
        Assert.Equal(20, ResolvedThresholds.Default.GraceSeconds);
        Assert.Equal(5, ResolvedThresholds.Default.ResolveMinutes);
        Assert.Equal((20, 5), (result.Config.Defaults.GraceSeconds, result.Config.Defaults.ResolveMinutes));
        Assert.Equal((20, 5), (result.Config.For("D").GraceSeconds, result.Config.For("D").ResolveMinutes));
    }

    [Fact]
    public void MalformedJson_RenamedToBad_ReturnsDefaults()
    {
        File.WriteAllText(ConfigPath, "{ not json");
        File.WriteAllText(ConfigPath + ".bad", "an older bad file");

        ConfigLoadResult result = new ConfigStore(ConfigPath).Load(FixedDrives);

        Assert.Contains("not valid JSON", result.Error);
        Assert.False(File.Exists(ConfigPath));
        Assert.Equal("{ not json", File.ReadAllText(ConfigPath + ".bad"));
        Assert.Equal(ResolvedThresholds.Default, result.Config.Defaults);
        Assert.Equal(WatcherConfig.CreateDefault(FixedDrives).Drives, result.Config.Drives);
    }

    [Fact]
    public void InvalidValues_RenamedToBad()
    {
        new ConfigStore(ConfigPath).Save(WatcherConfig.CreateDefault(FixedDrives));
        string json = File.ReadAllText(ConfigPath).Replace("\"historyDays\": 30", "\"historyDays\": 0", StringComparison.Ordinal);
        Assert.Contains("\"historyDays\": 0", json);
        File.WriteAllText(ConfigPath, json);

        ConfigLoadResult result = new ConfigStore(ConfigPath).Load(FixedDrives);

        Assert.Contains("History days must be at least 1", result.Error);
        Assert.False(File.Exists(ConfigPath));
        Assert.True(File.Exists(ConfigPath + ".bad"));
        Assert.Equal(30, result.Config.HistoryDays);
    }

    [Fact]
    public void Save_RejectsInvalid()
    {
        WatcherConfig invalid = WatcherConfig.CreateDefault(FixedDrives) with { HistoryDays = 0 };

        ArgumentException error = Assert.Throws<ArgumentException>(() => new ConfigStore(ConfigPath).Save(invalid));

        Assert.Contains("History days must be at least 1", error.Message);
        Assert.Empty(Directory.GetFiles(_temp.Path));
    }
}
