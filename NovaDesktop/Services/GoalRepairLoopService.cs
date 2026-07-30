using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovaDesktop.Models;

namespace NovaDesktop.Services;

public sealed class GoalRepairLoopService
{
    public const int MaximumRounds = 3;
    private readonly string _storageRoot;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public GoalRepairLoopService(string? storageRoot = null)
    {
        _storageRoot = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "goal-repairs");
    }

    public GoalRepairLedger? Load(string taskId)
    {
        var path = GetPath(taskId);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<GoalRepairLedger>(
                File.ReadAllText(path),
                _jsonOptions);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException)
        {
            return null;
        }
    }

    public async Task<GoalRepairAttempt?> PlanNextAsync(
        GoalMissionCharter mission,
        GoalOutcomeLedger outcome,
        WorkspaceEvidenceFingerprint beforeEvidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mission);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(beforeEvidence);
        if (outcome.Phase != GoalRunPhase.Partial)
        {
            return null;
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.Now;
            var ledger = Load(mission.TaskId)
                         ?? new GoalRepairLedger(
                             mission.TaskId,
                             mission.MissionHash,
                             [],
                             now,
                             now);
            if (!ledger.MissionHash.Equals(
                    mission.MissionHash,
                    StringComparison.Ordinal))
            {
                ledger = new GoalRepairLedger(
                    mission.TaskId,
                    mission.MissionHash,
                    [],
                    now,
                    now);
            }
            if (!ledger.HasRemainingRounds(MaximumRounds))
            {
                return null;
            }

            var targets = outcome.Signals
                .Where(signal => signal.Status != GoalSignalStatus.Pass)
                .OrderBy(signal => signal.Index)
                .Select(signal => new GoalRepairTarget(
                    signal.Index,
                    signal.Id,
                    signal.Description,
                    signal.Status,
                    signal.Evidence))
                .ToArray();
            if (targets.Length == 0)
            {
                return null;
            }

            var attempt = new GoalRepairAttempt(
                Guid.NewGuid().ToString("N"),
                mission.TaskId,
                mission.MissionHash,
                ledger.UsedRounds + 1,
                MaximumRounds,
                targets,
                outcome.Signals.Count(signal =>
                    signal.Status == GoalSignalStatus.Pass),
                GoalRepairAttemptStatus.Planned,
                $"Targeted {targets.Length} unmet success signal(s); "
                + $"{outcome.Signals.Count - targets.Length} passing signal(s) frozen.",
                beforeEvidence.Sha256,
                string.Empty,
                now,
                now,
                null);
            ledger = ledger with
            {
                Attempts = ledger.Attempts.Append(attempt).ToArray(),
                UpdatedAt = now
            };
            await PersistUnsafeAsync(ledger, cancellationToken);
            return attempt;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<GoalRepairAttempt?> UpdateAsync(
        string taskId,
        string attemptId,
        GoalRepairAttemptStatus status,
        string detail,
        string? afterFingerprint = null,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var ledger = Load(taskId);
            if (ledger is null)
            {
                return null;
            }
            var current = ledger.Attempts.FirstOrDefault(attempt =>
                attempt.AttemptId.Equals(
                    attemptId,
                    StringComparison.Ordinal));
            if (current is null)
            {
                return null;
            }
            var now = DateTimeOffset.Now;
            var terminal = status is GoalRepairAttemptStatus.Proven
                or GoalRepairAttemptStatus.Partial
                or GoalRepairAttemptStatus.Declined
                or GoalRepairAttemptStatus.Failed;
            var updated = current with
            {
                Status = status,
                Detail = Limit(detail),
                AfterFingerprint = string.IsNullOrWhiteSpace(afterFingerprint)
                    ? current.AfterFingerprint
                    : afterFingerprint,
                UpdatedAt = now,
                CompletedAt = terminal ? now : current.CompletedAt
            };
            ledger = ledger with
            {
                Attempts = ledger.Attempts
                    .Select(attempt => attempt.AttemptId.Equals(
                        attemptId,
                        StringComparison.Ordinal)
                        ? updated
                        : attempt)
                    .ToArray(),
                UpdatedAt = now
            };
            await PersistUnsafeAsync(ledger, cancellationToken);
            return updated;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public static string BuildPrompt(
        GoalMissionCharter mission,
        GoalOutcomeLedger outcome,
        GoalRepairAttempt attempt,
        string outcomeContract)
    {
        var targets = string.Join(
            Environment.NewLine,
            attempt.Targets.Select(target =>
                $"- SIGNAL {target.SignalIndex}: {target.Description}\n"
                + $"  Previous status: {target.PreviousStatus}\n"
                + $"  Previous evidence: "
                + $"{(string.IsNullOrWhiteSpace(target.PreviousEvidence) ? "none" : target.PreviousEvidence)}"));
        var preserved = string.Join(
            Environment.NewLine,
            outcome.Signals
                .Where(signal => signal.Status == GoalSignalStatus.Pass)
                .OrderBy(signal => signal.Index)
                .Select(signal =>
                    $"- SIGNAL {signal.Index}: {signal.Description}\n"
                    + $"  Frozen evidence: {signal.Evidence}"));
        return $"""
                [NOVA TARGETED GOAL REPAIR · ROUND {attempt.Round}/{attempt.MaximumRounds}]

                Mission:
                {mission.Title}

                Required outcome:
                {mission.Outcome}

                Repair only these unmet success signals:
                {targets}

                Already passing signals are frozen:
                {(string.IsNullOrWhiteSpace(preserved) ? "- none" : preserved)}

                Rules:
                - Inspect the current workspace as the source of truth.
                - Change only what is necessary to satisfy the listed unmet signals.
                - Do not rewrite, remove, weaken, or merely restate already passing behavior.
                - Do not delete tests, lower assertions, invent evidence, or claim success without tool evidence.
                - Reuse existing implementation and continue from the current task; this is not a new project.
                - All writes and commands remain behind NOVA approval boundaries.
                - Finish with a concise mapping from each targeted SIGNAL to concrete files, tests, or observable evidence.

                Constraints:
                {string.Join(Environment.NewLine, mission.Constraints.Select(item => $"- {item}"))}

                Completion contract:
                {outcomeContract}
                """;
    }

    private async Task PersistUnsafeAsync(
        GoalRepairLedger ledger,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_storageRoot);
        var path = GetPath(ledger.TaskId);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(ledger, _jsonOptions),
                new UTF8Encoding(false),
                cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                    // The committed ledger remains authoritative.
                }
            }
        }
    }

    private string GetPath(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)
            || taskId.Length > 256
            || taskId.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Task ID is empty or invalid.", nameof(taskId));
        }
        var stem = string.Concat(taskId
            .Where(character => char.IsAsciiLetterOrDigit(character)
                                || character is '-' or '_')
            .Take(64));
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "task";
        }
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(taskId)))[..12]
            .ToLowerInvariant();
        return Path.Combine(_storageRoot, $"{stem}-{hash}.json");
    }

    private static string Limit(string? value)
    {
        var safe = (value ?? string.Empty).Replace('\0', ' ').Trim();
        return safe.Length <= 2400 ? safe : safe[..2400];
    }
}
