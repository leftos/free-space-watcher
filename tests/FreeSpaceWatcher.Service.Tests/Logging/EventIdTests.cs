using System.Reflection;
using FreeSpaceWatcher.Service.Config;
using Microsoft.Extensions.Logging;

namespace FreeSpaceWatcher.Service.Tests.Logging;

/// <summary>
/// Guards the explicit event ids of the service's source-generated log methods. The Windows Event Log keeps only an
/// event id's low 16 bits as qualifiers, so a generated 32-bit id has no entry in the message file and the entry shows
/// no text at all.
/// </summary>
public sealed class EventIdTests
{
    [Fact]
    public void LoggerMessageMethods_UseUniqueEventIds()
    {
        List<(MethodInfo Method, int EventId)> logged = LoggerMessageMethods();

        List<string> repeated =
        [
            .. logged
                .GroupBy(entry => entry.EventId)
                .Where(group => group.Count() > 1)
                .Select(group => $"EventId={group.Key}: {string.Join(", ", group.Select(entry => entry.Method.Name))}"),
        ];

        Assert.True(
            repeated.Count == 0,
            $"{repeated.Count} event ids are shared by more than one [LoggerMessage] method: {string.Join("; ", repeated)}"
        );
    }

    [Fact]
    public void LoggerMessageMethods_UseEventIdsTheEventLogRenders()
    {
        List<(MethodInfo Method, int EventId)> logged = LoggerMessageMethods();

        List<string> outOfRange =
        [
            .. logged.Where(entry => entry.EventId is < 1 or > 65535).Select(entry => $"{entry.Method.Name} (EventId={entry.EventId})"),
        ];

        Assert.True(
            outOfRange.Count == 0,
            $"{outOfRange.Count} of {logged.Count} [LoggerMessage] methods have an event id outside 1-65535: {string.Join(", ", outOfRange)}"
        );
    }

    private static List<(MethodInfo Method, int EventId)> LoggerMessageMethods()
    {
        const BindingFlags Flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        List<(MethodInfo, int)> found = [];
        foreach (Type type in typeof(ConfigService).Assembly.GetTypes())
        {
            foreach (MethodInfo method in type.GetMethods(Flags))
            {
                if (method.GetCustomAttribute<LoggerMessageAttribute>() is { } attribute)
                {
                    found.Add((method, attribute.EventId));
                }
            }
        }

        Assert.NotEmpty(found);
        return found;
    }
}
