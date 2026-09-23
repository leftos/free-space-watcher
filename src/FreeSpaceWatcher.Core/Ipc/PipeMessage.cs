using System.Text.Json.Serialization;

namespace FreeSpaceWatcher.Core.Ipc;

/// <summary>A message on the service pipe; the JSON "type" property names the concrete message.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(GetStatusRequest), "getStatusRequest")]
[JsonDerivedType(typeof(GetConfigRequest), "getConfigRequest")]
[JsonDerivedType(typeof(SetConfigRequest), "setConfigRequest")]
[JsonDerivedType(typeof(SubscribeRequest), "subscribeRequest")]
[JsonDerivedType(typeof(ListAlertsRequest), "listAlertsRequest")]
[JsonDerivedType(typeof(GetAlertRequest), "getAlertRequest")]
[JsonDerivedType(typeof(AckAlertRequest), "ackAlertRequest")]
[JsonDerivedType(typeof(ProcessActionRequest), "processActionRequest")]
[JsonDerivedType(typeof(GetProcessStatesRequest), "getProcessStatesRequest")]
[JsonDerivedType(typeof(StatusResponse), "statusResponse")]
[JsonDerivedType(typeof(ConfigResponse), "configResponse")]
[JsonDerivedType(typeof(SetConfigResponse), "setConfigResponse")]
[JsonDerivedType(typeof(AlertListResponse), "alertListResponse")]
[JsonDerivedType(typeof(AlertResponse), "alertResponse")]
[JsonDerivedType(typeof(AckResponse), "ackResponse")]
[JsonDerivedType(typeof(ProcessActionResponse), "processActionResponse")]
[JsonDerivedType(typeof(ProcessStatesResponse), "processStatesResponse")]
[JsonDerivedType(typeof(AlertPush), "alertPush")]
[JsonDerivedType(typeof(StatusPush), "statusPush")]
[JsonDerivedType(typeof(ErrorResponse), "errorResponse")]
public abstract record PipeMessage
{
    /// <summary>Gets the id a client puts on a request and the service echoes on its response; 0 for pushes.</summary>
    public int RequestId { get; init; }
}
