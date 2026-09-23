using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Config;
using FreeSpaceWatcher.Core.Ipc;

namespace FreeSpaceWatcher.Core.Serialization;

/// <summary>Source-generated JSON contracts for config.json, alert files and pipe messages: camelCase, enums as strings.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true,
    AllowOutOfOrderMetadataProperties = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true
)]
[JsonSerializable(typeof(WatcherConfig))]
[JsonSerializable(typeof(Alert))]
[JsonSerializable(typeof(PipeMessage))]
internal sealed partial class CoreJsonContext : JsonSerializerContext { }

/// <summary>The type metadata each store and the pipe protocol serialize with.</summary>
internal static class CoreJson
{
    /// <summary>Gets the indented contract for config.json.</summary>
    public static JsonTypeInfo<WatcherConfig> Config => CoreJsonContext.Default.WatcherConfig;

    /// <summary>Gets the indented contract for alert history files.</summary>
    public static JsonTypeInfo<Alert> Alert => CoreJsonContext.Default.Alert;

    /// <summary>Gets the single-line contract for pipe messages.</summary>
    public static JsonTypeInfo<PipeMessage> WireMessage { get; } = CreateWireMessage();

    private static JsonTypeInfo<PipeMessage> CreateWireMessage()
    {
        JsonSerializerOptions options = new(CoreJsonContext.Default.Options) { WriteIndented = false };
        options.MakeReadOnly();
        return (JsonTypeInfo<PipeMessage>)options.GetTypeInfo(typeof(PipeMessage));
    }
}
