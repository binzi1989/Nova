using System.IO;
using System.Text.Json;
using NovaDesktop.Models;

namespace NovaDesktop.Services;

public sealed record TaskJournalEntry(
    DateTimeOffset Timestamp,
    string TaskId,
    string Agent,
    string Action,
    string Detail,
    string Kind,
    double Progress);

public sealed class TaskJournalService
{
    private readonly string _journalPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public TaskJournalService(string? journalPath = null)
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA");
        _journalPath = journalPath ?? Path.Combine(dataDirectory, "task-journal.jsonl");
    }

    public string JournalPath => _journalPath;

    public async Task AppendAsync(
        string taskId,
        string agent,
        string action,
        string detail,
        ActivityKind kind,
        double progress)
    {
        var entry = new TaskJournalEntry(
            DateTimeOffset.Now,
            taskId,
            agent,
            action,
            detail,
            kind.ToString(),
            progress);

        var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
        await _writeLock.WaitAsync();
        try
        {
            var directory = Path.GetDirectoryName(_journalPath)
                            ?? throw new InvalidOperationException("Task journal path has no parent directory.");
            Directory.CreateDirectory(directory);
            await File.AppendAllTextAsync(_journalPath, line);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public IReadOnlyList<TaskJournalEntry> ReadRecent(
        DateTimeOffset? since = null,
        int maximumEntries = 5000)
    {
        if (!File.Exists(_journalPath))
        {
            return [];
        }

        var entries = new List<TaskJournalEntry>();
        try
        {
            foreach (var line in File.ReadLines(_journalPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                try
                {
                    var entry = JsonSerializer.Deserialize<TaskJournalEntry>(line);
                    if (entry is not null && (since is null || entry.Timestamp >= since))
                    {
                        entries.Add(entry);
                    }
                }
                catch (JsonException)
                {
                    // A malformed line never invalidates the complete journal.
                }
            }
        }
        catch (IOException)
        {
            return [];
        }

        return entries
            .OrderByDescending(entry => entry.Timestamp)
            .Take(Math.Clamp(maximumEntries, 1, 20_000))
            .OrderBy(entry => entry.Timestamp)
            .ToArray();
    }
}
