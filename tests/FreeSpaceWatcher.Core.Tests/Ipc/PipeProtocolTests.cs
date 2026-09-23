using System.Reflection;
using System.Text.Json.Serialization;
using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Config;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Core.Triggers;
using FreeSpaceWatcher.Core.Writes;

namespace FreeSpaceWatcher.Core.Tests.Ipc;

public sealed class PipeProtocolTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 14, 15, 0, TimeSpan.FromHours(3));

    public static TheoryData<string> MessageTypes => [.. DerivedTypes().Select(a => (string)a.TypeDiscriminator!)];

    [Theory]
    [MemberData(nameof(MessageTypes))]
    public void RoundTrip_EveryMessageType(string discriminator)
    {
        Type type = DerivedTypes().Single(a => (string?)a.TypeDiscriminator == discriminator).DerivedType;
        PipeMessage sample = Assert.Single(Samples(), s => s.GetType() == type);

        string line = PipeProtocol.Serialize(sample);
        PipeMessage back = PipeProtocol.Deserialize(line);

        Assert.StartsWith($"{{\"type\":\"{discriminator}\"", line);
        Assert.IsType(type, back);
        Assert.Equivalent(sample, back, strict: true);
        Assert.Equal(line, PipeProtocol.Serialize(back));
    }

    [Fact]
    public void UnknownType_ThrowsInvalidData()
    {
        InvalidDataException unknown = Assert.Throws<InvalidDataException>(() =>
            PipeProtocol.Deserialize("{\"type\":\"formatDiskRequest\",\"requestId\":1}")
        );

        Assert.Contains("formatDiskRequest", unknown.Message);
        Assert.Throws<InvalidDataException>(() => PipeProtocol.Deserialize("{\"requestId\":1}"));
        Assert.Throws<InvalidDataException>(() => PipeProtocol.Deserialize("not json"));
    }

    [Fact]
    public void MalformedMessage_ThrowsInvalidDataNamingType()
    {
        InvalidDataException malformed = Assert.Throws<InvalidDataException>(() =>
            PipeProtocol.Deserialize("{\"type\":\"getAlertRequest\",\"requestId\":1}")
        );

        Assert.Contains("getAlertRequest", malformed.Message);
    }

    [Fact]
    public void Serialize_IsSingleLine()
    {
        AlertPush push = new(MakeAlert() with { Reason = "line one\nline two" }) { RequestId = 0 };

        string line = PipeProtocol.Serialize(push);

        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", line, StringComparison.Ordinal);
        Assert.Equal("line one\nline two", Assert.IsType<AlertPush>(PipeProtocol.Deserialize(line)).Alert.Reason);
    }

    private static IEnumerable<JsonDerivedTypeAttribute> DerivedTypes() => typeof(PipeMessage).GetCustomAttributes<JsonDerivedTypeAttribute>();

    private static PipeMessage[] Samples()
    {
        var config = WatcherConfig.CreateDefault(["D"]);
        Alert alert = MakeAlert();
        StatusResponse status = new(
            [
                new DriveStatus
                {
                    Letter = "C",
                    Watched = true,
                    Available = true,
                    FreeBytes = 10L << 30,
                    TotalBytes = 100L << 30,
                    DropRateBytesPerSecond = 1.5e6,
                    TimeToFull = TimeSpan.FromMinutes(12),
                },
            ],
            false,
            "Access denied"
        )
        {
            RequestId = 3,
        };
        AlertSummary summary = new()
        {
            Id = alert.Id,
            Time = alert.Time,
            Drive = alert.Drive,
            Trigger = alert.Trigger,
            Reason = alert.Reason,
            Acknowledged = true,
        };
        return
        [
            new GetStatusRequest { RequestId = 1 },
            new GetConfigRequest { RequestId = 2 },
            new SetConfigRequest(config) { RequestId = 3 },
            new SubscribeRequest { RequestId = 4 },
            new ListAlertsRequest { RequestId = 5 },
            new GetAlertRequest(alert.Id) { RequestId = 6 },
            new AckAlertsRequest([alert.Id, "20260923-141600-D-Floor"]) { RequestId = 7 },
            new DeleteAlertsRequest(null) { RequestId = 11 },
            new ProcessActionRequest(1234, T0, ProcessAction.Suspend) { RequestId = 8 },
            new GetProcessStatesRequest([1234, 5678]) { RequestId = 10 },
            new GetDriveHistoryRequest("C", 600) { RequestId = 12 },
            status,
            new ConfigResponse(config, "config.json was not valid JSON") { RequestId = 2 },
            new SetConfigResponse(false, ["History days must be at least 1 (got 0)."]) { RequestId = 3 },
            new AlertListResponse([summary]) { RequestId = 5 },
            new AlertResponse(alert) { RequestId = 6 },
            new AckResponse(2) { RequestId = 7 },
            new DeleteAlertsResponse(3) { RequestId = 11 },
            new ProcessActionResponse(false, true, "Access is denied.", ProcessState.Suspended) { RequestId = 8 },
            new ProcessStatesResponse(new Dictionary<int, ProcessState> { [1234] = ProcessState.Running, [5678] = ProcessState.Exited })
            {
                RequestId = 10,
            },
            new DriveHistoryResponse("C", [new DriveSample(T0, 10L << 30, 100L << 30), new DriveSample(T0.AddSeconds(1), 9L << 30, 100L << 30)])
            {
                RequestId = 12,
            },
            new AlertPush(alert),
            new AlertsChangedPush(),
            new AlertResolvedPush(
                alert with
                {
                    Acknowledged = true,
                    ResolvedAt = T0.AddMinutes(3),
                    ResolvedReason = "pwsh deleted 14.0 GB it had written",
                }
            ),
            new StatusPush(status with { RequestId = 0 }),
            new ErrorResponse("Unknown request") { RequestId = 9 },
        ];
    }

    private static Alert MakeAlert() =>
        new()
        {
            Id = Alert.CreateId(T0, "C", TriggerKind.TimeToFull),
            Time = T0,
            Drive = "C",
            Trigger = TriggerKind.TimeToFull,
            IsEscalation = true,
            Reason = "C: losing 2.1 GB/min, full in ~9 min",
            FreeBytes = 19L << 30,
            TotalBytes = 100L << 30,
            DropRateBytesPerSecond = 3.6e7,
            TimeToFull = TimeSpan.FromMinutes(9),
            UnattributedBytes = -512,
            Processes =
            [
                new ProcessWriteReport
                {
                    ProcessId = 42,
                    Name = "writer",
                    ExePath = null,
                    StartTime = null,
                    BytesWritten = 100,
                    ExtendBytes = 0,
                    FilesCreated = 0,
                    FilesDeleted = 1,
                    Folders = [new FolderWrite(@"C:\logs", 100)],
                    Files =
                    [
                        new FileWrite
                        {
                            Path = @"C:\logs\*",
                            BytesWritten = 100,
                            ExtendBytes = 0,
                            Created = false,
                            Deleted = true,
                        },
                    ],
                },
            ],
        };
}
