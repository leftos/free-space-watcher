using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreeSpaceWatcher.Core.Serialization;

namespace FreeSpaceWatcher.Core.Ipc;

/// <summary>The service pipe's wire format: one JSON message per line.</summary>
public static class PipeProtocol
{
    /// <summary>The named pipe the service listens on.</summary>
    public const string PipeName = "FreeSpaceWatcher";

    private static readonly HashSet<string> KnownTypes =
    [
        .. typeof(PipeMessage).GetCustomAttributes<JsonDerivedTypeAttribute>().Select(a => a.TypeDiscriminator).OfType<string>(),
    ];

    /// <summary>Serializes a message to a single line of JSON, without the trailing newline.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The JSON line.</returns>
    public static string Serialize(PipeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.Serialize(message, CoreJson.WireMessage);
    }

    /// <summary>Parses one line of JSON into a message.</summary>
    /// <param name="line">The JSON line.</param>
    /// <returns>The message.</returns>
    /// <exception cref="InvalidDataException">The line is not JSON, has no known "type", or does not match that type's shape.</exception>
    public static PipeMessage Deserialize(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        string typeName = ReadTypeName(line);
        if (!KnownTypes.Contains(typeName))
        {
            throw new InvalidDataException($"Unknown pipe message type '{typeName}'.");
        }

        try
        {
            return JsonSerializer.Deserialize(line, CoreJson.WireMessage)
                ?? throw new InvalidDataException($"Pipe message of type '{typeName}' is null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Malformed pipe message of type '{typeName}': {ex.Message}", ex);
        }
    }

    private static string ReadTypeName(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            if (
                document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("type", out JsonElement type)
                && type.ValueKind == JsonValueKind.String
            )
            {
                return type.GetString() ?? "";
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Pipe message is not valid JSON: {ex.Message}", ex);
        }

        throw new InvalidDataException("Pipe message has no string \"type\" property.");
    }
}
