using System.Text;
using System.Text.RegularExpressions;

namespace NovaDesktop.Services;

public sealed record VerificationCouncilResult(
    string Provider,
    string Model,
    string Verdict,
    int Confidence,
    string Summary,
    string RawResponse,
    DateTimeOffset CompletedAt)
{
    public bool Passed => Verdict == "PASS";
    public bool IsBlocking => Verdict is "CONCERNS" or "FAIL";

    public static VerificationCouncilResult Skipped(
        string provider,
        string model,
        string reason)
        => new(
            provider,
            model,
            "SKIPPED",
            0,
            reason,
            string.Empty,
            DateTimeOffset.Now);
}

public static class IndependentVerificationCouncilService
{
    private static readonly Regex VerdictPattern = new(
        @"(?im)^\s*VERDICT\s*:\s*(PASS|CONCERNS|FAIL)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ConfidencePattern = new(
        @"(?im)^\s*CONFIDENCE\s*:\s*(\d{1,3})\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SummaryPattern = new(
        @"(?ims)^\s*SUMMARY\s*:\s*(?<summary>.+?)(?:^\s*FINDINGS\s*:|\z)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ApiKeyPattern = new(
        @"\bsk-[A-Za-z0-9_-]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BearerPattern = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string BuildPrompt(
        string originalGoal,
        TaskOutcomeContract contract,
        EngineeringWorkspaceSnapshot snapshot,
        EngineeringCodeReviewResult? localReview,
        string verificationSummary)
    {
        var reviewSummary = localReview is null
            ? "No local review result."
            : $"Score {localReview.Score}/100. {localReview.Summary}";
        var diff = RedactAndLimit(snapshot.Diff, 32_000);
        var goalSignalCriteria = contract.Criteria
            .Where(item => item.Id.StartsWith("goal-signal-", StringComparison.Ordinal))
            .ToArray();
        var goalSignalProtocol = goalSignalCriteria.Length == 0
            ? string.Empty
            : Environment.NewLine
              + "SIGNALS:" + Environment.NewLine
              + string.Join(
                  Environment.NewLine,
                  goalSignalCriteria.Select((item, index) =>
                      $"SIGNAL {index + 1}: <PASS|UNVERIFIED|FAIL|BLOCKED> | "
                      + $"one concrete evidence locator for: {item.Title}"))
              + Environment.NewLine
              + "Every SIGNAL line is mandatory. Choose exactly one status before the first |. "
              + "PASS requires inspectable evidence, not the implementer's statement.";
        return
            $"""
            [NOVA INDEPENDENT VERIFICATION COUNCIL]
            You are an adversarial read-only verifier. Another agent implemented the change.
            Do not modify files, run commands, delegate work, or trust the implementer's claims.
            Use read-only workspace tools only when the supplied evidence is insufficient.

            Original goal:
            {Limit(originalGoal, 6000)}

            Completion contract:
            {TaskOutcomeContractService.FormatForPrompt(contract)}

            Verification evidence:
            {verificationSummary}

            Local deterministic review:
            {reviewSummary}

            Current workspace diff:
            {diff}

            Judge whether the actual diff and evidence satisfy the original goal without unrelated
            regressions, fake tests, weakened assertions, skipped validation, exposed credentials,
            or unsupported completion claims.

            Return exactly this structure:
            VERDICT: PASS | CONCERNS | FAIL
            CONFIDENCE: 0-100
            SUMMARY: one concise Chinese paragraph
            FINDINGS:
            - concrete issue or "none"
            {goalSignalProtocol}

            PASS requires evidence that every applicable contract criterion is satisfied.
            CONCERNS means the implementation may work but has a material unresolved risk.
            FAIL means the goal is not met, verification is contradicted, or a serious regression exists.
            """;
    }

    public static VerificationCouncilResult Parse(
        string provider,
        string model,
        string response)
    {
        response ??= string.Empty;
        var verdictMatch = VerdictPattern.Match(response);
        if (!verdictMatch.Success)
        {
            return new VerificationCouncilResult(
                provider,
                model,
                "UNAVAILABLE",
                0,
                "独立验证 Agent 未返回可解析的结构化裁决。",
                response,
                DateTimeOffset.Now);
        }
        var confidenceMatch = ConfidencePattern.Match(response);
        var confidence = confidenceMatch.Success
                         && int.TryParse(confidenceMatch.Groups[1].Value, out var parsed)
            ? Math.Clamp(parsed, 0, 100)
            : 50;
        var summaryMatch = SummaryPattern.Match(response);
        var summary = summaryMatch.Success
            ? Compact(summaryMatch.Groups["summary"].Value, 800)
            : Compact(response, 800);
        return new VerificationCouncilResult(
            provider,
            model,
            verdictMatch.Groups[1].Value.ToUpperInvariant(),
            confidence,
            summary,
            response,
            DateTimeOffset.Now);
    }

    public static string Format(VerificationCouncilResult result)
        => $"Independent Council · {result.Verdict} · confidence {result.Confidence}%"
           + Environment.NewLine
           + $"{result.Provider} · {result.Model}"
           + Environment.NewLine
           + result.Summary;

    private static string RedactAndLimit(string value, int maximum)
        => Limit(
            BearerPattern.Replace(
                ApiKeyPattern.Replace(value ?? string.Empty, "[REDACTED_API_KEY]"),
                "Bearer [REDACTED]"),
            maximum);

    private static string Limit(string value, int maximum)
        => value.Length <= maximum
            ? value
            : value[..maximum] + Environment.NewLine + "… EVIDENCE TRUNCATED …";

    private static string Compact(string value, int maximum)
    {
        var compact = string.Join(
            " ",
            value.Split(
                ['\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return compact.Length <= maximum ? compact : compact[..maximum] + "…";
    }
}
