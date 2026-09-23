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

        Assert.Equal(1, store.Acknowledge([older.Id]));
        Assert.True(store.Get(older.Id)?.Acknowledged);
        Assert.Null(store.Get(@"..\config"));
        Assert.Empty(_errors);
    }

    [Fact]
    public void Acknowledge_SomeAllAndNone_CountsOnlyAlertsThatChanged()
    {
        HistoryStore store = CreateStore();
        Alert first = store.Save(MakeAlert(T0, TriggerKind.DropRate));
        Alert second = store.Save(MakeAlert(T0.AddMinutes(1), TriggerKind.Floor));
        store.Save(MakeAlert(T0.AddMinutes(2), TriggerKind.TimeToFull));

        Assert.Equal(0, store.Acknowledge([]));
        Assert.Equal(2, store.Acknowledge([first.Id, second.Id, first.Id]));
        Assert.Equal(0, store.Acknowledge([first.Id]));
        Assert.Equal(1, store.Acknowledge(null));
        Assert.Equal(0, store.Acknowledge(null));
        Assert.All(store.List(), alert => Assert.True(alert.Acknowledged));
        Assert.Empty(_errors);
    }

    [Fact]
    public void Acknowledge_UnknownAndInvalidIds_AreSkipped()
    {
        HistoryStore store = CreateStore();
        Alert saved = store.Save(MakeAlert(T0, TriggerKind.DropRate));

        int count = store.Acknowledge(["20200101-000000-C-Floor", @"..\config", "", saved.Id]);

        Assert.Equal(1, count);
        Assert.True(store.Get(saved.Id)?.Acknowledged);
        Assert.Empty(_errors);
    }

    [Fact]
    public void MarkResolved_KnownAlert_StoresResolutionAndAcknowledges()
    {
        HistoryStore store = CreateStore();
        Alert saved = store.Save(MakeAlert(T0, TriggerKind.DropRate));

        bool marked = store.MarkResolved(saved.Id, T0.AddMinutes(2), "writer deleted 14.0 GB it had written");

        Assert.True(marked);
        Alert? loaded = store.Get(saved.Id);
        Assert.NotNull(loaded);
        Assert.True(loaded.Acknowledged);
        Assert.Equal(T0.AddMinutes(2), loaded.ResolvedAt);
        Assert.Equal("writer deleted 14.0 GB it had written", loaded.ResolvedReason);
        Assert.Equivalent(
            saved with
            {
                Acknowledged = true,
                ResolvedAt = T0.AddMinutes(2),
                ResolvedReason = loaded.ResolvedReason,
            },
            loaded,
            strict: true
        );
        Assert.Single(Directory.GetFiles(AlertsPath));
        Assert.Empty(_errors);
    }

    [Fact]
    public void MarkResolved_UnknownOrInvalidId_ReturnsFalse()
    {
        HistoryStore store = CreateStore();
        Alert saved = store.Save(MakeAlert(T0, TriggerKind.DropRate));

        Assert.False(store.MarkResolved("20200101-000000-C-Floor", T0, "gone"));
        Assert.False(store.MarkResolved(@"..\config", T0, "gone"));
        Assert.False(store.MarkResolved("", T0, "gone"));
        Alert? untouched = store.Get(saved.Id);
        Assert.NotNull(untouched);
        Assert.Null(untouched.ResolvedAt);
        Assert.False(untouched.Acknowledged);
        Assert.Single(Directory.GetFiles(AlertsPath));
    }

    [Fact]
    public void Delete_SomeThenAll_CountsDeletedAlerts()
    {
        HistoryStore store = CreateStore();
        Alert first = store.Save(MakeAlert(T0, TriggerKind.DropRate));
        Alert second = store.Save(MakeAlert(T0.AddMinutes(1), TriggerKind.Floor));
        Alert third = store.Save(MakeAlert(T0.AddMinutes(2), TriggerKind.TimeToFull));

        Assert.Equal(0, store.Delete([]));
        Assert.Equal(2, store.Delete([first.Id, second.Id, first.Id]));
        Assert.Equal(third.Id, Assert.Single(store.List()).Id);
        Assert.False(File.Exists(Path.Combine(AlertsPath, first.Id + ".json")));
        Assert.Equal(1, store.Delete(null));
        Assert.Empty(store.List());
        Assert.Equal(0, store.Delete(null));
    }

    [Fact]
    public void Delete_UnknownAndInvalidIds_AreSkipped()
    {
        HistoryStore store = CreateStore();
        Alert saved = store.Save(MakeAlert(T0, TriggerKind.DropRate));
        string outside = Path.Combine(_temp.Path, "config.json");
        File.WriteAllText(outside, "{}");

        int count = store.Delete(["20200101-000000-C-Floor", @"..\config", "", "*"]);

        Assert.Equal(0, count);
        Assert.True(File.Exists(outside));
        Assert.Equal(saved.Id, Assert.Single(store.List()).Id);
    }

    [Fact]
    public void Delete_All_WhenNothingWasSaved_ReturnsZero()
    {
        HistoryStore store = CreateStore();

        Assert.Equal(0, store.Delete(null));
        Assert.Equal(0, store.Acknowledge(null));
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
