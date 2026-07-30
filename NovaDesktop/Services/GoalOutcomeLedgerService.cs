using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NovaDesktop.Models;

namespace NovaDesktop.Services;

public sealed class GoalOutcomeLedgerService
{
    private const int MaximumEvidenceLength = 4000;
    private static readonly Regex SignalResultPattern = new(
        @"^\s*SIGNAL\s+(?<index>\d+)\s*:\s*(?<status>PASS|UNVERIFIED|FAIL|BLOCKED)\s*\|\s*(?<evidence>\S(?:.*\S)?)\s*$",
        RegexOptions.Compiled
        | RegexOptions.CultureInvariant
        | RegexOptions.IgnoreCase
        | RegexOptions.Multiline);
    private static readonly Regex WhiteSpacePattern = new(
        @"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ApiKeyPattern = new(
        @"\bsk-[A-Za-z0-9_-]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BearerPattern = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _storageRoot;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public GoalOutcomeLedgerService(string? storageRoot = null)
    {
        _storageRoot = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "goal-outcomes");
    }

    public string StorageRoot => _storageRoot;

    public async Task<GoalOutcomeLedger> InitializeAsync(
        GoalMissionCharter mission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mission);
        ValidateTaskId(mission.TaskId);
        if (mission.SuccessSignals.Count == 0)
        {
            throw new ArgumentException(
                "A Goal Mission must contain at least one success signal.",
                nameof(mission));
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.Now;
            var missionHash = ComputeMissionHash(mission);
            var existing = LoadUnsafe(mission.TaskId, quarantineCorrupt: true);
            var existingById = existing?.Signals.ToDictionary(
                signal => signal.Id,
                StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, GoalOutcomeSignal>(
                    StringComparer.OrdinalIgnoreCase);
            var signals = mission.SuccessSignals
                .Select((description, index) =>
                {
                    var id = CreateSignalId(description);
                    return existingById.TryGetValue(id, out var previous)
                        ? previous with
                        {
                            Index = index + 1,
                            Description = description.Trim()
                        }
                        : new GoalOutcomeSignal(
                            id,
                            index + 1,
                            description.Trim(),
                            GoalSignalStatus.Pending,
                            string.Empty,
                            0,
                            now);
                })
                .ToArray();

            var sameMission = existing is not null
                              && existing.MissionHash.Equals(
                                  missionHash,
                                  StringComparison.Ordinal);
            var ledger = new GoalOutcomeLedger(
                mission.TaskId,
                missionHash,
                mission.Title,
                mission.Outcome,
                sameMission ? existing!.Phase : GoalRunPhase.Chartered,
                signals,
                sameMission ? existing!.AssessmentStatus : string.Empty,
                sameMission ? existing!.AssessmentProofScore : 0,
                sameMission ? existing!.CouncilVerdict : string.Empty,
                sameMission ? existing!.CouncilConfidence : 0,
                sameMission
                    ? existing!.Detail
                    : "Mission Charter initialized with pending success signals.",
                existing?.CreatedAt ?? now,
                now)
            {
                Freshness = sameMission
                    ? existing!.Freshness
                    : EvidenceFreshness.Untracked,
                EvidenceWorkspaceRoot = sameMission
                    ? existing!.EvidenceWorkspaceRoot
                    : string.Empty,
                EvidenceFingerprint = sameMission
                    ? existing!.EvidenceFingerprint
                    : string.Empty,
                EvidenceCapturedAt = sameMission
                    ? existing!.EvidenceCapturedAt
                    : null,
                EvidenceFileCount = sameMission
                    ? existing!.EvidenceFileCount
                    : 0,
                EvidenceHashedBytes = sameMission
                    ? existing!.EvidenceHashedBytes
                    : 0
            };
            await PersistUnsafeAsync(ledger, cancellationToken);
            return ledger;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public GoalOutcomeLedger? Load(string taskId)
    {
        ValidateTaskId(taskId);
        return LoadUnsafe(taskId, quarantineCorrupt: true);
    }

    public Task<GoalOutcomeLedger?> SetPhaseAsync(
        string taskId,
        GoalRunPhase phase,
        CancellationToken cancellationToken = default)
        => SetPhaseAsync(taskId, phase, null, cancellationToken);

    public async Task<GoalOutcomeLedger?> SetPhaseAsync(
        string taskId,
        GoalRunPhase phase,
        string? detail,
        CancellationToken cancellationToken = default)
    {
        ValidateTaskId(taskId);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var current = LoadUnsafe(taskId, quarantineCorrupt: true);
            if (current is null)
            {
                return null;
            }

            var updated = current with
            {
                Phase = phase,
                Detail = string.IsNullOrWhiteSpace(detail)
                    ? current.Detail
                    : LimitAndRedact(detail),
                UpdatedAt = DateTimeOffset.Now
            };
            await PersistUnsafeAsync(updated, cancellationToken);
            return updated;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<GoalOutcomeLedger> ReconcileAsync(
        GoalMissionCharter mission,
        TaskOutcomeAssessment? assessment,
        VerificationCouncilResult? council,
        CancellationToken cancellationToken = default)
        => await ReconcileCoreAsync(
            mission,
            assessment,
            council,
            null,
            null,
            cancellationToken);

    public async Task<GoalOutcomeLedger> ReconcileAsync(
        GoalMissionCharter mission,
        TaskOutcomeAssessment? assessment,
        VerificationCouncilResult? council,
        WorkspaceEvidenceFingerprint evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return await ReconcileCoreAsync(
            mission,
            assessment,
            council,
            evidence,
            null,
            cancellationToken);
    }

    public async Task<GoalOutcomeLedger> ReconcileTargetedAsync(
        GoalMissionCharter mission,
        TaskOutcomeAssessment? assessment,
        VerificationCouncilResult? council,
        WorkspaceEvidenceFingerprint evidence,
        IReadOnlyCollection<int> targetSignalIndexes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(targetSignalIndexes);
        if (targetSignalIndexes.Count == 0
            || targetSignalIndexes.Any(index =>
                index < 1 || index > mission.SuccessSignals.Count))
        {
            throw new ArgumentException(
                "Targeted reconciliation requires valid success-signal indexes.",
                nameof(targetSignalIndexes));
        }
        return await ReconcileCoreAsync(
            mission,
            assessment,
            council,
            evidence,
            targetSignalIndexes.ToHashSet(),
            cancellationToken);
    }

    public async Task<GoalOutcomeLedger?> ValidateFreshnessAsync(
        string taskId,
        WorkspaceEvidenceFingerprint currentEvidence,
        CancellationToken cancellationToken = default)
    {
        ValidateTaskId(taskId);
        ArgumentNullException.ThrowIfNull(currentEvidence);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var current = LoadUnsafe(taskId, quarantineCorrupt: true);
            if (current is null
                || current.Phase != GoalRunPhase.Proven
                || string.IsNullOrWhiteSpace(current.EvidenceFingerprint))
            {
                return current;
            }

            var now = DateTimeOffset.Now;
            var sameRoot = PathsEqual(
                current.EvidenceWorkspaceRoot,
                currentEvidence.WorkspaceRoot);
            var matches = currentEvidence.IsComplete
                          && sameRoot
                          && current.EvidenceFingerprint.Equals(
                              currentEvidence.Sha256,
                              StringComparison.Ordinal);
            if (matches)
            {
                if (current.Freshness == EvidenceFreshness.Fresh)
                {
                    return current;
                }
                var refreshed = current with
                {
                    Freshness = EvidenceFreshness.Fresh,
                    UpdatedAt = now
                };
                await PersistUnsafeAsync(refreshed, cancellationToken);
                return refreshed;
            }

            var reason = !currentEvidence.IsComplete
                ? "Workspace evidence could not be captured completely."
                : !sameRoot
                    ? "The task workspace no longer matches the proven workspace."
                    : "Workspace contents changed after proof was captured.";
            var staleSignals = current.Signals
                .Select(signal => signal.Status == GoalSignalStatus.Pass
                    ? signal with
                    {
                        Status = GoalSignalStatus.Stale,
                        Evidence = LimitAndRedact(
                            $"{signal.Evidence} [STALE: {reason}]"),
                        Confidence = 0,
                        UpdatedAt = now
                    }
                    : signal)
                .ToArray();
            var stale = current with
            {
                Phase = GoalRunPhase.Stale,
                Signals = staleSignals,
                Freshness = EvidenceFreshness.Stale,
                AssessmentStatus = "STALE",
                AssessmentProofScore = 0,
                Detail = LimitAndRedact(
                    $"{reason} Previous PROVEN evidence was invalidated; "
                    + "resume the task to verify the current workspace."),
                UpdatedAt = now
            };
            await PersistUnsafeAsync(stale, cancellationToken);
            return stale;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<GoalOutcomeLedger> ReconcileCoreAsync(
        GoalMissionCharter mission,
        TaskOutcomeAssessment? assessment,
        VerificationCouncilResult? council,
        WorkspaceEvidenceFingerprint? evidence,
        IReadOnlySet<int>? targetSignalIndexes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mission);
        ValidateTaskId(mission.TaskId);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var current = LoadUnsafe(mission.TaskId, quarantineCorrupt: true)
                          ?? CreateUnpersistedLedger(mission);
            var now = DateTimeOffset.Now;
            var parsed = ParseSignalResults(council?.RawResponse);
            var councilConfidence = Math.Clamp(council?.Confidence ?? 0, 0, 100);
            var currentById = current.Signals.ToDictionary(
                signal => signal.Id,
                StringComparer.OrdinalIgnoreCase);
            var signals = mission.SuccessSignals
                .Select((description, index) =>
                {
                    var oneBasedIndex = index + 1;
                    var id = CreateSignalId(description);
                    if (targetSignalIndexes is not null
                        && !targetSignalIndexes.Contains(oneBasedIndex)
                        && currentById.TryGetValue(id, out var frozen)
                        && frozen.Status == GoalSignalStatus.Pass)
                    {
                        return frozen with
                        {
                            Index = oneBasedIndex,
                            Description = description.Trim()
                        };
                    }
                    if (parsed.TryGetValue(oneBasedIndex, out var result))
                    {
                        return new GoalOutcomeSignal(
                            id,
                            oneBasedIndex,
                            description.Trim(),
                            result.Status,
                            result.Evidence,
                            councilConfidence,
                            now);
                    }

                    return new GoalOutcomeSignal(
                        id,
                        oneBasedIndex,
                        description.Trim(),
                        GoalSignalStatus.Unverified,
                        "Independent verification did not provide a strict SIGNAL result.",
                        0,
                        now);
                })
                .ToArray();

            var phase = DeterminePhase(signals, assessment, council);
            var updated = current with
            {
                MissionHash = ComputeMissionHash(mission),
                MissionTitle = mission.Title,
                MissionOutcome = mission.Outcome,
                Phase = phase,
                Signals = signals,
                AssessmentStatus = assessment?.Status ?? string.Empty,
                AssessmentProofScore = Math.Clamp(assessment?.ProofScore ?? 0, 0, 100),
                CouncilVerdict = council?.Verdict ?? string.Empty,
                CouncilConfidence = councilConfidence,
                Detail = BuildReconciliationDetail(signals, assessment, council)
                         + (targetSignalIndexes is null
                             ? string.Empty
                             : $" Targeted repair revalidated signals: "
                               + $"{string.Join(", ", targetSignalIndexes.Order())}."),
                Freshness = evidence is null
                    ? EvidenceFreshness.Untracked
                    : evidence.IsComplete
                        ? EvidenceFreshness.Fresh
                        : EvidenceFreshness.Stale,
                EvidenceWorkspaceRoot = evidence?.WorkspaceRoot ?? string.Empty,
                EvidenceFingerprint = evidence?.Sha256 ?? string.Empty,
                EvidenceCapturedAt = evidence?.CapturedAt,
                EvidenceFileCount = evidence?.FileCount ?? 0,
                EvidenceHashedBytes = evidence?.HashedBytes ?? 0,
                UpdatedAt = now
            };
            if (updated.Freshness == EvidenceFreshness.Stale
                && updated.Phase == GoalRunPhase.Proven)
            {
                updated = updated with
                {
                    Phase = GoalRunPhase.Stale,
                    Signals = updated.Signals
                        .Select(signal => signal.Status == GoalSignalStatus.Pass
                            ? signal with
                            {
                                Status = GoalSignalStatus.Stale,
                                Confidence = 0,
                                Evidence = LimitAndRedact(
                                    $"{signal.Evidence} [STALE: incomplete workspace fingerprint]"),
                                UpdatedAt = now
                            }
                            : signal)
                        .ToArray(),
                    AssessmentStatus = "STALE",
                    AssessmentProofScore = 0,
                    Detail = LimitAndRedact(
                        "Workspace fingerprint exceeded its safety boundary; "
                        + "NOVA will not claim durable completion.")
                };
            }
            await PersistUnsafeAsync(updated, cancellationToken);
            return updated;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task<GoalOutcomeLedger?> MarkInterruptedAsync(
        string taskId,
        CancellationToken cancellationToken = default)
        => MarkInterruptedCoreAsync(taskId, null, cancellationToken);

    public Task<GoalOutcomeLedger?> MarkInterruptedAsync(
        string taskId,
        string? detail,
        CancellationToken cancellationToken = default)
        => MarkInterruptedCoreAsync(taskId, detail, cancellationToken);

    private async Task<GoalOutcomeLedger?> MarkInterruptedCoreAsync(
        string taskId,
        string? detail,
        CancellationToken cancellationToken)
    {
        ValidateTaskId(taskId);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var current = LoadUnsafe(taskId, quarantineCorrupt: true);
            if (current is null
                || current.Phase is GoalRunPhase.Proven
                    or GoalRunPhase.Partial
                    or GoalRunPhase.Blocked
                    or GoalRunPhase.Failed)
            {
                return current;
            }

            var updated = current with
            {
                Phase = GoalRunPhase.Interrupted,
                Detail = LimitAndRedact(
                    string.IsNullOrWhiteSpace(detail)
                        ? "The host stopped before the Goal outcome reached a terminal proof state."
                        : detail),
                UpdatedAt = DateTimeOffset.Now
            };
            await PersistUnsafeAsync(updated, cancellationToken);
            return updated;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private GoalOutcomeLedger CreateUnpersistedLedger(GoalMissionCharter mission)
    {
        var now = DateTimeOffset.Now;
        return new GoalOutcomeLedger(
            mission.TaskId,
            ComputeMissionHash(mission),
            mission.Title,
            mission.Outcome,
            GoalRunPhase.Chartered,
            mission.SuccessSignals.Select((description, index) =>
                new GoalOutcomeSignal(
                    CreateSignalId(description),
                    index + 1,
                    description.Trim(),
                    GoalSignalStatus.Pending,
                    string.Empty,
                    0,
                    now)).ToArray(),
            string.Empty,
            0,
            string.Empty,
            0,
            "Mission Charter initialized during reconciliation.",
            now,
            now);
    }

    private GoalOutcomeLedger? LoadUnsafe(
        string taskId,
        bool quarantineCorrupt)
    {
        var path = GetPath(taskId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var ledger = JsonSerializer.Deserialize<GoalOutcomeLedger>(
                File.ReadAllText(path),
                _jsonOptions);
            if (ledger is null
                || !ledger.TaskId.Equals(taskId, StringComparison.Ordinal)
                || ledger.Signals.Count == 0
                || ledger.Signals.Any(signal => signal.Index < 1
                    || string.IsNullOrWhiteSpace(signal.Id)
                    || string.IsNullOrWhiteSpace(signal.Description)))
            {
                throw new JsonException("Goal outcome ledger is structurally invalid.");
            }
            return ledger;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException
                                          or NotSupportedException)
        {
            if (quarantineCorrupt)
            {
                TryQuarantine(path);
            }
            return null;
        }
    }

    private async Task PersistUnsafeAsync(
        GoalOutcomeLedger ledger,
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
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (IOException)
            {
                // A stale temporary file never invalidates the committed ledger.
            }
        }
    }

    private static IReadOnlyDictionary<int, ParsedSignalResult> ParseSignalResults(
        string? rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return new Dictionary<int, ParsedSignalResult>();
        }

        var results = new Dictionary<int, ParsedSignalResult>();
        foreach (Match match in SignalResultPattern.Matches(rawResponse))
        {
            if (!int.TryParse(match.Groups["index"].Value, out var index)
                || index < 1
                || results.ContainsKey(index))
            {
                continue;
            }

            var status = match.Groups["status"].Value.ToUpperInvariant() switch
            {
                "PASS" => GoalSignalStatus.Pass,
                "FAIL" => GoalSignalStatus.Fail,
                "BLOCKED" => GoalSignalStatus.Blocked,
                _ => GoalSignalStatus.Unverified
            };
            results[index] = new ParsedSignalResult(
                status,
                LimitAndRedact(match.Groups["evidence"].Value));
        }
        return results;
    }

    private static GoalRunPhase DeterminePhase(
        IReadOnlyList<GoalOutcomeSignal> signals,
        TaskOutcomeAssessment? assessment,
        VerificationCouncilResult? council)
    {
        if (signals.Any(signal => signal.Status == GoalSignalStatus.Fail)
            || assessment?.Status.Equals("FAILED", StringComparison.OrdinalIgnoreCase) == true
            || council?.Verdict.Equals("FAIL", StringComparison.OrdinalIgnoreCase) == true)
        {
            return GoalRunPhase.Failed;
        }
        if (signals.Any(signal => signal.Status == GoalSignalStatus.Blocked))
        {
            return GoalRunPhase.Blocked;
        }
        if (signals.Count > 0
            && signals.All(signal => signal.Status == GoalSignalStatus.Pass)
            && assessment?.Status.Equals(
                "PROVEN",
                StringComparison.OrdinalIgnoreCase) == true
            && council?.Verdict.Equals(
                "PASS",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            return GoalRunPhase.Proven;
        }
        return GoalRunPhase.Partial;
    }

    private static string BuildReconciliationDetail(
        IReadOnlyList<GoalOutcomeSignal> signals,
        TaskOutcomeAssessment? assessment,
        VerificationCouncilResult? council)
    {
        var passed = signals.Count(signal => signal.Status == GoalSignalStatus.Pass);
        var failed = signals.Count(signal => signal.Status == GoalSignalStatus.Fail);
        var blocked = signals.Count(signal => signal.Status == GoalSignalStatus.Blocked);
        var unverified = signals.Count - passed - failed - blocked;
        return $"Signals: {passed} pass, {unverified} unverified, {failed} fail, "
               + $"{blocked} blocked. Assessment: {assessment?.Status ?? "NONE"} "
               + $"({assessment?.ProofScore ?? 0}/100). Council: "
               + $"{council?.Verdict ?? "NONE"} ({council?.Confidence ?? 0}%).";
    }

    private string GetPath(string taskId)
    {
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

    private static string ComputeMissionHash(GoalMissionCharter mission)
    {
        var canonical = string.Join(
            "\n",
            new[]
            {
                Normalize(mission.TaskId),
                Normalize(mission.Title),
                Normalize(mission.Outcome),
                Normalize(mission.ExecutionKind),
                string.Join("\n", mission.SuccessSignals.Select(Normalize)),
                string.Join("\n", mission.Constraints.Select(Normalize)),
                string.Join("\n", mission.StopConditions.Select(Normalize))
            });
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static string CreateSignalId(string description)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(description))))[..12]
            .ToLowerInvariant();
        return $"signal-{hash}";
    }

    private static string Normalize(string? value)
        => WhiteSpacePattern.Replace(value?.Trim() ?? string.Empty, " ")
            .ToUpperInvariant();

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)
            || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }
        try
        {
            return Path.GetFullPath(left)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(
                    Path.GetFullPath(right)
                        .TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
        {
            return false;
        }
    }

    private static string LimitAndRedact(string? value)
    {
        var redacted = BearerPattern.Replace(
            ApiKeyPattern.Replace(value ?? string.Empty, "[REDACTED_API_KEY]"),
            "Bearer [REDACTED]");
        redacted = string.Join(
            " ",
            redacted.Split(
                ['\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries));
        return redacted.Length <= MaximumEvidenceLength
            ? redacted
            : redacted[..MaximumEvidenceLength];
    }

    private static void ValidateTaskId(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)
            || taskId.Length > 256
            || taskId.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Task ID is empty or invalid.", nameof(taskId));
        }
    }

    private static void TryQuarantine(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }
            var quarantine = path + ".corrupt-"
                             + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
            File.Move(path, quarantine, overwrite: false);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException)
        {
            // Isolation is best effort; callers still receive a clean null result.
        }
    }

    private sealed record ParsedSignalResult(
        GoalSignalStatus Status,
        string Evidence);
}
