using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NovaDesktop.Services;

public sealed record EngineeringEvidenceEntry(
    string Id,
    DateTimeOffset Timestamp,
    string TaskId,
    string WorkspaceRoot,
    string Category,
    string Action,
    string Target,
    string Outcome,
    bool Mutating,
    int? ExitCode,
    long DurationMilliseconds,
    string? OutputSha256,
    string Summary)
{
    public string TimeLabel => Timestamp.ToLocalTime().ToString("HH:mm:ss");
    public string OutcomeLabel => Outcome.ToUpperInvariant();
}

public sealed class EngineeringEvidenceLedgerService
{
    private static readonly SemaphoreSlim SharedWriteLock = new(1, 1);
    private static readonly Regex ApiKeyPattern = new(
        @"\bsk-[A-Za-z0-9_-]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BearerPattern = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex QuerySecretPattern = new(
        @"(?i)\b(api[_-]?key|access[_-]?token|token|secret)=([^&\s]{6,})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly string _ledgerPath;

    public EngineeringEvidenceLedgerService(string? ledgerPath = null)
    {
        _ledgerPath = ledgerPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "engineering-evidence.jsonl");
    }

    public string LedgerPath => _ledgerPath;

    public async Task AppendAsync(
        string taskId,
        string workspaceRoot,
        string category,
        string action,
        string target,
        string outcome,
        bool mutating,
        int? exitCode,
        TimeSpan duration,
        string? output,
        string summary,
        CancellationToken cancellationToken = default)
    {
        var entry = new EngineeringEvidenceEntry(
            "evidence-" + Guid.NewGuid().ToString("N")[..16],
            DateTimeOffset.Now,
            string.IsNullOrWhiteSpace(taskId) ? "workspace" : taskId,
            NormalizeWorkspace(workspaceRoot),
            Limit(Redact(category), 40),
            Limit(Redact(action), 80),
            Limit(Redact(target), 500),
            Limit(outcome, 40),
            mutating,
            exitCode,
            Math.Max(0, (long)duration.TotalMilliseconds),
            string.IsNullOrEmpty(output) ? null : Hash(output),
            Limit(Redact(summary), 1000));
        var line = JsonSerializer.Serialize(entry) + Environment.NewLine;

        await SharedWriteLock.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_ledgerPath)
                            ?? throw new InvalidOperationException("Evidence ledger path has no parent directory.");
            Directory.CreateDirectory(directory);
            await File.AppendAllTextAsync(_ledgerPath, line, Encoding.UTF8, cancellationToken);
        }
        finally
        {
            SharedWriteLock.Release();
        }
    }

    public IReadOnlyList<EngineeringEvidenceEntry> ReadRecent(
        string? workspaceRoot = null,
        string? taskId = null,
        int maximumEntries = 100)
    {
        if (!File.Exists(_ledgerPath))
        {
            return [];
        }

        var normalizedWorkspace = string.IsNullOrWhiteSpace(workspaceRoot)
            ? null
            : NormalizeWorkspace(workspaceRoot);
        var entries = new List<EngineeringEvidenceEntry>();
        try
        {
            foreach (var line in File.ReadLines(_ledgerPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var entry = JsonSerializer.Deserialize<EngineeringEvidenceEntry>(line);
                    if (entry is null
                        || (normalizedWorkspace is not null
                            && !entry.WorkspaceRoot.Equals(normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
                        || (!string.IsNullOrWhiteSpace(taskId)
                            && !entry.TaskId.Equals(taskId, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    entries.Add(entry);
                }
                catch (JsonException)
                {
                    // A malformed line does not invalidate other evidence.
                }
            }
        }
        catch (IOException)
        {
            return [];
        }

        return entries
            .OrderByDescending(entry => entry.Timestamp)
            .Take(Math.Clamp(maximumEntries, 1, 1000))
            .ToArray();
    }

    private static string NormalizeWorkspace(string workspaceRoot)
    {
        try
        {
            return Path.GetFullPath(workspaceRoot).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return workspaceRoot.Trim();
        }
    }

    private static string Hash(string output)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output))).ToLowerInvariant();

    private static string Redact(string value)
        => QuerySecretPattern.Replace(
            BearerPattern.Replace(
                ApiKeyPattern.Replace(value, "[REDACTED_API_KEY]"),
                "Bearer [REDACTED]"),
            "$1=[REDACTED]");

    private static string Limit(string value, int maximum)
        => value.Length <= maximum ? value : value[..maximum] + "…";
}
