using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Triggers;
using FreeSpaceWatcher.Core.Writes;

namespace FreeSpaceWatcher.Core.Tests.Alerts;

public sealed class HistoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 14, 15, 0, TimeSpan.Zero);
    private readonly TempDirectory _temp = new();
    private readonly List<string> _errors = [];

    private string AlertsPath => Path.Combine(_temp.Path, "alerts");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void SaveListGetAck()
    {
        HistoryStore store = CreateStore();

        Alert older = store.Save(MakeAlert(T0, TriggerKind.DropRate));
        Alert newer = store.Save(MakeAlert(T0.AddMinutes(5), TriggerKind.Floor));

        Assert.Equal("20260923-141500-C-DropRate", older.Id);
        string[] expected = [newer.Id, older.Id];
        Assert.Equal(expected, store.List().Select(a => a.Id));
        Alert? loaded = store.Get(older.Id);
        Assert.NotNull(loaded);
        Assert.Equivalent(older, loaded, strict: true);
        Assert.False(loaded.Acknowledged);
        Assert.Contains("\"trigger\": \"DropRate\"", File.ReadAllText(Path.Combine(AlertsPath, older.Id + ".json")));

        Assert.True(store.Acknowledge(older.Id));
        Assert.True(store.Get(older.Id)?.Acknowledged);
        Assert.False(store.Acknowledge("20200101-000000-C-Floor"));
        Assert.Null(store.Get(@"..\config"));
        Assert.Empty(_errors);
    }

    [Fact]
    public void IdCollision_GetsSuffix()
    {
        HistoryStore store = CreateStore();
        Alert alert = MakeAlert(T0, TriggerKind.DropRate);

        string[] ids = [store.Save(alert).Id, store.Save(alert).Id, store.Save(alert).Id];

        string[] expected = [alert.Id, alert.Id + "-2", alert.Id + "-3"];
        Assert.Equal(expected, ids);
        Assert.Equal(3, store.List().Count);
    }

    [Fact]
    public void Prune_Boundary()
    {
        HistoryStore store = CreateStore();
        DateTimeOffset now = T0.AddDays(30);
        Alert exactlyThirtyDays = store.Save(MakeAlert(T0, TriggerKind.DropRate));
        store.Save(MakeAlert(T0.AddSeconds(-1), TriggerKind.Floor));
        Alert fresh = store.Save(MakeAlert(now, TriggerKind.TimeToFull));

        int deleted = store.Prune(now, 30);

        Assert.Equal(1, deleted);
        string[] expected = [fresh.Id, exactlyThirtyDays.Id];
        Assert.Equal(expected, store.List().Select(a => a.Id));
    }

    [Fact]
    public void CorruptFile_SkippedAndReported()
    {
        HistoryStore store = CreateStore();
        Alert saved = store.Save(MakeAlert(T0, TriggerKind.DropRate));
        File.WriteAllText(Path.Combine(AlertsPath, "20260101-000000-C-Floor.json"), "{ truncated");

        IReadOnlyList<Alert> alerts = store.List();

        Assert.Equal(saved.Id, Assert.Single(alerts).Id);
        Assert.Contains("20260101-000000-C-Floor.json", Assert.Single(_errors));
    }

    private HistoryStore CreateStore() => new(AlertsPath, _errors.Add);

    private static Alert MakeAlert(DateTimeOffset time, TriggerKind trigger) =>
        new()
        {
            Id = Alert.CreateId(time, "C", trigger),
            Time = time,
            Drive = "C",
            Trigger = trigger,
            IsEscalation = false,
            Reason = "C: losing 2.1 GB/min, full in ~9 min",
            FreeBytes = 10L << 30,
            TotalBytes = 100L << 30,
            DropRateBytesPerSecond = 3.5e7,
            TimeToFull = TimeSpan.FromMinutes(9),
            UnattributedBytes = 1234,
            Processes =
            [
                new ProcessWriteReport
                {
                    ProcessId = 42,
                    Name = "writer",
                    ExePath = @"C:\tools\writer.exe",
                    StartTime = new DateTimeOffset(2026, 9, 23, 14, 0, 0, TimeSpan.Zero),
                    BytesWritten = 100,
                    ExtendBytes = 50,
                    FilesCreated = 1,
                    FilesDeleted = 0,
                    Folders = [new FolderWrite(@"C:\logs", 100)],
                    Files =
                    [
                        new FileWrite
                        {
                            Path = @"C:\logs\a.log",
                            BytesWritten = 100,
                            ExtendBytes = 50,
                            Created = true,
                            Deleted = false,
                            CurrentSize = 4096,
                        },
                    ],
                },
            ],
        };
}
