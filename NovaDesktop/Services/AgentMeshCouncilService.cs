using System.Text;
using System.Text.RegularExpressions;

namespace NovaDesktop.Services;

public sealed record AgentMeshCouncilDecision(
    string Provider,
    string Model,
    string Verdict,
    int Confidence,
    string Summary,
    string RawResponse,
    DateTimeOffset CompletedAt)
{
    public bool Accepted => Verdict == "ACCEPT";
}

public static class AgentMeshCouncilService
{
    private static readonly Regex ApiKeyPattern = new(
        @"\bsk-[A-Za-z0-9_-]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BearerPattern = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string BuildPrompt(
        string objective,
        TaskOutcomeContract contract,
        AgentMeshRunResult mesh)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are NOVA Agent Mesh Integration Council.");
        builder.AppendLine("Perform an adversarial read-only review of the combined multi-agent change.");
        builder.AppendLine("Do not call tools, write files, run commands or delegate.");
        builder.AppendLine("Reject ownership leaks, missing integration, contract gaps, test bypasses or unsafe scope.");
        builder.AppendLine();
        builder.AppendLine("OBJECTIVE:");
        builder.AppendLine(LimitAndRedact(objective, 8000));
        builder.AppendLine();
        builder.AppendLine("PROOF-OF-DONE:");
        builder.AppendLine(TaskOutcomeContractService.FormatForPrompt(contract));
        builder.AppendLine();
        builder.AppendLine("MESH PLAN:");
        builder.AppendLine(AgentMeshPlannerService.Format(mesh.Plan));
        builder.AppendLine();
        builder.AppendLine(
            $"Verification: {(mesh.Verification is null ? "not available" : $"{mesh.Verification.Passed} · exit {mesh.Verification.ExitCode} · {mesh.Verification.Command}")}");
        builder.AppendLine($"Local review: {mesh.Review.Score}/100");
        builder.AppendLine($"Combined patch: +{mesh.Additions} / -{mesh.Deletions}");
        builder.AppendLine();
        builder.AppendLine("COMBINED PATCH:");
        builder.AppendLine(LimitAndRedact(mesh.CombinedPatch, 50000));
        builder.AppendLine();
        builder.AppendLine("Return exactly:");
        builder.AppendLine("VERDICT: ACCEPT | REJECT");
        builder.AppendLine("CONFIDENCE: <0-100>");
        builder.AppendLine("SUMMARY: <one concise paragraph>");
        builder.AppendLine("FINDINGS:");
        builder.AppendLine("- <evidence-based finding>");
        return builder.ToString();
    }

    public static AgentMeshCouncilDecision Parse(
        string provider,
        string model,
        string response)
    {
        response ??= string.Empty;
        var verdict = Match(response, @"(?im)^\s*VERDICT\s*:\s*(ACCEPT|REJECT)\s*$")
                      ?.ToUpperInvariant()
                      ?? "UNAVAILABLE";
        var confidenceText = Match(
            response,
            @"(?im)^\s*CONFIDENCE\s*:\s*(\d{1,3})\s*%?\s*$");
        var confidence = int.TryParse(confidenceText, out var parsed)
            ? Math.Clamp(parsed, 0, 100)
            : 0;
        var summary = Match(response, @"(?im)^\s*SUMMARY\s*:\s*(.+?)\s*$")
                      ?? "Agent Mesh Council did not return a structured decision.";
        if (verdict == "UNAVAILABLE")
        {
            confidence = 0;
        }
        return new AgentMeshCouncilDecision(
            provider,
            model,
            verdict,
            confidence,
            summary,
            response,
            DateTimeOffset.Now);
    }

    public static string Format(AgentMeshCouncilDecision decision)
        => $"""
           Agent Mesh Council · {decision.Verdict}
           Judge: {decision.Provider} · {decision.Model}
           Confidence: {decision.Confidence}%

           {decision.Summary}

           {decision.RawResponse}
           """;

    private static string LimitAndRedact(string value, int maximum)
    {
        var redacted = BearerPattern.Replace(
            ApiKeyPattern.Replace(value ?? string.Empty, "[REDACTED_API_KEY]"),
            "Bearer [REDACTED]");
        return redacted.Length <= maximum
            ? redacted
            : redacted[..maximum] + "\n… truncated by Agent Mesh Council budget …";
    }

    private static string? Match(string value, string pattern)
    {
        var match = Regex.Match(value, pattern, RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
}
