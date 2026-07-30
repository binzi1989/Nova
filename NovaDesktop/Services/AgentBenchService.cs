using System.IO;
using System.Text.Json;
using NovaDesktop.Models;

namespace NovaDesktop.Services;

public sealed record AgentBenchRun(
    string TaskId,
    string Provider,
    string Model,
    AgentExecutionMode ExecutionMode,
    bool IsEngineeringTask,
    bool MutationRequired,
    string OutcomeStatus,
    int ProofScore,
    bool VerificationAttempted,
    bool VerificationPassed,
    int ToolCalls,
    int MutatingToolCalls,
    int ContextFiles,
    int ContextCharacters,
    TimeSpan Duration,
    DateTimeOffset CompletedAt);

public sealed record AgentBenchSnapshot(
    int SchemaVersion,
    IReadOnlyList<AgentBenchRun> Runs,
    DateTimeOffset UpdatedAt);

public sealed record AgentBenchModelScore(
    string Provider,
    string Model,
    int Runs,
    double ProvenRate,
    double FailureRate,
    double AverageProofScore,
    double AverageDurationSeconds,
    double AverageToolCalls,
    DateTimeOffset LastRunAt);

public sealed class AgentBenchService
{
    private const int MaximumRuns = 500;
    private readonly string _storagePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public AgentBenchService(string? storagePath = null)
    {
        _storagePath = storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "agent-bench.json");
    }

    public string StoragePath => _storagePath;

    public AgentBenchSnapshot GetSnapshot()
    {
        if (!File.Exists(_storagePath))
        {
            return new AgentBenchSnapshot(1, [], DateTimeOffset.MinValue);
        }
        try
        {
            return JsonSerializer.Deserialize<AgentBenchSnapshot>(
                       File.ReadAllText(_storagePath),
                       _jsonOptions)
                   ?? new AgentBenchSnapshot(1, [], DateTimeOffset.MinValue);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new InvalidOperationException(
                $"AgentBench 账本无法读取：{_storagePath}",
                exception);
        }
    }

    public async Task RecordAsync(
        AgentBenchRun run,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = GetSnapshot();
            var runs = existing.Runs
                .Where(item => !(
                    item.TaskId.Equals(run.TaskId, StringComparison.Ordinal)
                    && item.CompletedAt == run.CompletedAt))
                .Append(run)
                .OrderByDescending(item => item.CompletedAt)
                .Take(MaximumRuns)
                .ToArray();
            var snapshot = new AgentBenchSnapshot(1, runs, DateTimeOffset.Now);
            var directory = Path.GetDirectoryName(_storagePath)
                            ?? throw new InvalidOperationException("AgentBench 路径没有父目录。");
            Directory.CreateDirectory(directory);
            var temporaryPath = _storagePath + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(snapshot, _jsonOptions),
                cancellationToken);
            File.Move(temporaryPath, _storagePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<AgentBenchModelScore> Summarize(
        bool engineeringOnly = true,
        int recentRuns = 200)
    {
        var runs = GetSnapshot().Runs
            .Where(item => !engineeringOnly || item.IsEngineeringTask)
            .OrderByDescending(item => item.CompletedAt)
            .Take(Math.Clamp(recentRuns, 10, MaximumRuns))
            .ToArray();
        return runs
            .GroupBy(
                item => (item.Provider.ToLowerInvariant(), item.Model.ToLowerInvariant()))
            .Select(group =>
            {
                var items = group.ToArray();
                return new AgentBenchModelScore(
                    items[0].Provider,
                    items[0].Model,
                    items.Length,
                    Math.Round(
                        items.Count(item => item.OutcomeStatus == "PROVEN") * 100d / items.Length,
                        1),
                    Math.Round(
                        items.Count(item =>
                            item.OutcomeStatus is "FAILED" or "RUNTIME_FAILED") * 100d / items.Length,
                        1),
                    Math.Round(items.Average(item => item.ProofScore), 1),
                    Math.Round(items.Average(item => item.Duration.TotalSeconds), 2),
                    Math.Round(items.Average(item => item.ToolCalls), 1),
                    items.Max(item => item.CompletedAt));
            })
            .OrderByDescending(item => item.AverageProofScore)
            .ThenByDescending(item => item.ProvenRate)
            .ThenBy(item => item.AverageDurationSeconds)
            .ToArray();
    }
}

public sealed record ModelRouteCandidate(
    string Provider,
    string Model,
    bool Available,
    int BenchRuns,
    double RouteScore,
    string Reason);

public sealed record ModelRouteRecommendation(
    string Provider,
    string Model,
    bool ShouldSwitch,
    string Summary,
    IReadOnlyList<ModelRouteCandidate> Candidates);

public sealed class AdaptiveModelRouterService
{
    private const int MinimumEvidenceRuns = 3;
    private const double MinimumSwitchMargin = 6;

    public ModelRouteRecommendation Recommend(
        string selectedProvider,
        string selectedModel,
        AgentExecutionMode executionMode,
        EngineeringTaskProfile profile,
        IReadOnlyDictionary<string, bool> providerAvailability,
        IReadOnlyList<AgentBenchModelScore> scores)
    {
        selectedProvider = NormalizeProvider(selectedProvider);
        var candidates = BuildCandidates(
            selectedProvider,
            selectedModel,
            profile,
            providerAvailability,
            scores);
        var selected = candidates.First(item =>
            item.Provider.Equals(selectedProvider, StringComparison.OrdinalIgnoreCase)
            && item.Model.Equals(selectedModel, StringComparison.OrdinalIgnoreCase));
        if (executionMode is not (AgentExecutionMode.Autopilot or AgentExecutionMode.Goal))
        {
            return new ModelRouteRecommendation(
                selected.Provider,
                selected.Model,
                false,
                "当前执行模式不允许自动切换模型；保留用户选择。",
                candidates);
        }

        var best = candidates
            .Where(item => item.Available && item.BenchRuns >= MinimumEvidenceRuns)
            .OrderByDescending(item => item.RouteScore)
            .FirstOrDefault();
        if (best is null
            || best.Provider.Equals(selected.Provider, StringComparison.OrdinalIgnoreCase)
               && best.Model.Equals(selected.Model, StringComparison.OrdinalIgnoreCase))
        {
            return new ModelRouteRecommendation(
                selected.Provider,
                selected.Model,
                false,
                best is null
                    ? "AgentBench 样本不足；保留用户选择，至少需要同一模型 3 次真实工程记录。"
                    : $"用户选择仍为当前最佳证据路线：{best.Reason}",
                candidates);
        }
        if (best.RouteScore - selected.RouteScore < MinimumSwitchMargin)
        {
            return new ModelRouteRecommendation(
                selected.Provider,
                selected.Model,
                false,
                $"候选优势不足 {MinimumSwitchMargin:0} 分；避免为微小差异切换提供商。",
                candidates);
        }
        return new ModelRouteRecommendation(
            best.Provider,
            best.Model,
            true,
            $"AgentBench 推荐切换：{best.Reason}",
            candidates);
    }

    private static IReadOnlyList<ModelRouteCandidate> BuildCandidates(
        string selectedProvider,
        string selectedModel,
        EngineeringTaskProfile profile,
        IReadOnlyDictionary<string, bool> availability,
        IReadOnlyList<AgentBenchModelScore> scores)
    {
        var definitions = new[]
        {
            (Provider: selectedProvider, Model: selectedModel),
            (Provider: "openai", Model: selectedProvider == "openai" ? selectedModel : "gpt-5.6-terra"),
            (Provider: "deepseek", Model: selectedProvider == "deepseek" ? selectedModel : "deepseek-v4-pro"),
            (Provider: "kimi", Model: selectedProvider == "kimi" ? selectedModel : "kimi-k3")
        }
        .Distinct()
        .ToArray();
        return definitions.Select(definition =>
        {
            var benchmark = scores.FirstOrDefault(item =>
                item.Provider.Equals(definition.Provider, StringComparison.OrdinalIgnoreCase)
                && item.Model.Equals(definition.Model, StringComparison.OrdinalIgnoreCase));
            var available = availability.TryGetValue(definition.Provider, out var ready) && ready;
            var evidenceScore = benchmark is null
                ? 0
                : benchmark.AverageProofScore * .65
                  + benchmark.ProvenRate * .25
                  - benchmark.FailureRate * .20
                  - Math.Min(10, benchmark.AverageDurationSeconds / 30);
            var selectionStability = definition.Provider == selectedProvider
                                     && definition.Model.Equals(selectedModel, StringComparison.OrdinalIgnoreCase)
                ? 3
                : 0;
            var complexityFit = profile.Risk == "HIGH"
                                && (definition.Model.Contains("pro", StringComparison.OrdinalIgnoreCase)
                                    || definition.Model.Equals("gpt-5.6", StringComparison.OrdinalIgnoreCase)
                                    || definition.Model.Equals("kimi-k3", StringComparison.OrdinalIgnoreCase))
                ? 2
                : 0;
            var routeScore = available
                ? Math.Round(evidenceScore + selectionStability + complexityFit, 2)
                : double.NegativeInfinity;
            var reason = benchmark is null
                ? "没有真实任务样本"
                : $"{benchmark.Runs} 次任务 · Proof {benchmark.AverageProofScore:0.0} · "
                  + $"PROVEN {benchmark.ProvenRate:0.0}% · "
                  + $"平均 {benchmark.AverageDurationSeconds:0.0}s";
            return new ModelRouteCandidate(
                definition.Provider,
                definition.Model,
                available,
                benchmark?.Runs ?? 0,
                routeScore,
                reason);
        }).ToArray();
    }

    private static string NormalizeProvider(string provider)
        => provider.Trim().ToLowerInvariant() switch
        {
            "deepseek" => "deepseek",
            "kimi" or "moonshot" => "kimi",
            _ => "openai"
        };
}
