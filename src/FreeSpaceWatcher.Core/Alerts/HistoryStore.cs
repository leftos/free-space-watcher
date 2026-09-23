using System.Text.Json;
using FreeSpaceWatcher.Core.Serialization;
using static System.FormattableString;

namespace FreeSpaceWatcher.Core.Alerts;

/// <summary>Stores alerts as one JSON file each, named after the alert id.</summary>
/// <param name="directory">The folder holding the alert files; created on the first save.</param>
/// <param name="onError">Receives a message for each alert file that cannot be read; such files are skipped.</param>
public sealed class HistoryStore(string directory, Action<string>? onError)
{
    private const int MaxIdAttempts = 1000;

    /// <summary>Writes an alert, appending -2, -3... to its id when a file with that id already exists.</summary>
    /// <param name="alert">The alert to store.</param>
    /// <returns>The alert as stored, with its final id.</returns>
    /// <exception cref="ArgumentException">The id contains characters other than letters, digits and '-'.</exception>
    public Alert Save(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        if (!IsSafeId(alert.Id))
        {
            throw new ArgumentException(Invariant($"Alert id '{alert.Id}' may contain only letters, digits and '-'."), nameof(alert));
        }

        Directory.CreateDirectory(directory);
        for (int attempt = 1; attempt <= MaxIdAttempts; attempt++)
        {
            string id = attempt == 1 ? alert.Id : Invariant($"{alert.Id}-{attempt}");
            string target = PathFor(id);
            if (File.Exists(target))
            {
                continue;
            }

            Alert saved = alert with { Id = id };
            string temp = WriteTemp(saved);
            try
            {
                File.Move(temp, target, overwrite: false);
                return saved;
            }
            catch (IOException) when (File.Exists(target))
            {
                // Another writer took this id between the check and the move; the next suffix is tried.
                continue;
            }
            finally
            {
                DeleteIfPresent(temp);
            }
        }

        throw new IOException(Invariant($"No free file name for alert '{alert.Id}' in '{directory}' after {MaxIdAttempts} attempts."));
    }

    /// <summary>Lists every readable alert, newest first; unreadable files are reported and skipped.</summary>
    /// <returns>The alerts.</returns>
    public IReadOnlyList<Alert> List()
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        List<Alert> alerts = [];
        foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (TryRead(file) is Alert alert)
            {
                alerts.Add(alert);
            }
        }

        return [.. alerts.OrderByDescending(a => a.Time).ThenByDescending(a => a.Id, StringComparer.Ordinal)];
    }

    /// <summary>Reads one alert.</summary>
    /// <param name="id">The alert id.</param>
    /// <returns>The alert, or null when it does not exist, cannot be read, or the id is not a valid alert id.</returns>
    public Alert? Get(string id)
    {
        if (!IsSafeId(id))
        {
            return null;
        }

        string path = PathFor(id);
        return File.Exists(path) ? TryRead(path) : null;
    }

    /// <summary>Marks an alert as acknowledged.</summary>
    /// <param name="id">The alert id.</param>
    /// <returns>True when the alert exists (acknowledged now or before); false otherwise.</returns>
    public bool Acknowledge(string id)
    {
        Alert? alert = Get(id);
        if (alert is null)
        {
            return false;
        }

        if (!alert.Acknowledged)
        {
            string temp = WriteTemp(alert with { Acknowledged = true });
            try
            {
                File.Move(temp, PathFor(id), overwrite: true);
            }
            finally
            {
                DeleteIfPresent(temp);
            }
        }

        return true;
    }

    /// <summary>Deletes alerts raised more than <paramref name="days"/> days before <paramref name="now"/>; exactly that old is kept.</summary>
    /// <param name="now">The current time.</param>
    /// <param name="days">How many days of alerts to keep.</param>
    /// <returns>How many alerts were deleted.</returns>
    public int Prune(DateTimeOffset now, int days)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var maxAge = TimeSpan.FromDays(days);
        int deleted = 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*.json").ToList())
        {
            if (TryRead(file) is Alert alert && now - alert.Time > maxAge)
            {
                File.Delete(file);
                deleted++;
            }
        }

        return deleted;
    }

    private static bool IsSafeId(string? id) => !string.IsNullOrEmpty(id) && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string PathFor(string id) => Path.Combine(directory, id + ".json");

    private string WriteTemp(Alert alert)
    {
        string temp = Path.Combine(directory, Invariant($"{alert.Id}.{Guid.NewGuid():N}.tmp"));
        File.WriteAllText(temp, JsonSerializer.Serialize(alert, CoreJson.Alert));
        return temp;
    }

    private Alert? TryRead(string file)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(file), CoreJson.Alert) ?? throw new JsonException("The file contains null.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            onError?.Invoke(Invariant($"Skipped unreadable alert file '{file}': {ex.Message}"));
            return null;
        }
    }
}
