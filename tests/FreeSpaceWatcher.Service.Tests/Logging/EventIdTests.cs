using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using FreeSpaceWatcher.Service.Config;
using Microsoft.Extensions.Logging;

namespace FreeSpaceWatcher.Service.Tests.Logging;

/// <summary>
/// Guards the explicit event ids of the service's source-generated log methods, and of each <c>new EventId(...)</c> in the
/// service's source. The Windows Event Log keeps only an event id's low 16 bits as qualifiers, so a generated 32-bit id has
/// no entry in the message file and the entry shows no text at all.
/// </summary>
public sealed partial class EventIdTests
{
    private const int ServiceEventIdMin = 1000;
    private const int ServiceEventIdMax = 1599;

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

    [Fact]
    public void ExplicitEventIds_FollowTheServiceScheme_AndShareNoId()
    {
        List<(string Where, int EventId)> explicitIds = ExplicitEventIds();
        HashSet<int> loggerMessageIds = [.. LoggerMessageMethods().Select(entry => entry.EventId)];

        List<string> outOfScheme =
        [
            .. explicitIds
                .Where(entry => entry.EventId is < ServiceEventIdMin or > ServiceEventIdMax)
                .Select(entry => $"{entry.Where} (EventId={entry.EventId})"),
        ];
        List<string> shared =
        [
            .. explicitIds
                .Where(entry => loggerMessageIds.Contains(entry.EventId) || explicitIds.Count(other => other.EventId == entry.EventId) > 1)
                .Select(entry => $"{entry.Where} (EventId={entry.EventId})"),
        ];

        Assert.True(
            outOfScheme.Count == 0,
            $"{outOfScheme.Count} explicit new EventId(...) uses are outside {ServiceEventIdMin}-{ServiceEventIdMax}: {string.Join(", ", outOfScheme)}"
        );
        Assert.True(shared.Count == 0, $"{shared.Count} explicit new EventId(...) uses share an id: {string.Join(", ", shared)}");
    }

    private static List<(string Where, int EventId)> ExplicitEventIds()
    {
        string serviceSource = Path.Combine(RepositoryRoot(), "src", "FreeSpaceWatcher.Service");
        char separator = Path.DirectorySeparatorChar;
        List<(string, int)> found = [];
        foreach (string file in Directory.EnumerateFiles(serviceSource, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(serviceSource, file);
            if (relative.StartsWith($"obj{separator}", StringComparison.Ordinal) || relative.StartsWith($"bin{separator}", StringComparison.Ordinal))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match match in ExplicitEventIdPattern().Matches(lines[i]))
                {
                    found.Add(($"{relative}:{i + 1}", int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)));
                }
            }
        }

        Assert.NotEmpty(found);
        return found;
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "FreeSpaceWatcher.slnx")))
            {
                return folder.FullName;
            }
        }

        throw new DirectoryNotFoundException($"No folder above {AppContext.BaseDirectory} holds FreeSpaceWatcher.slnx.");
    }

    [GeneratedRegex(@"new EventId\((\d+)")]
    private static partial Regex ExplicitEventIdPattern();

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
