using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NovaDesktop.Models;

namespace NovaDesktop.Services;

public enum EvolutionExperimentState
{
    Proposed,
    Ready,
    Running,
    Evaluating,
    Passed,
    Failed,
    Adopted,
    Rejected
}

public sealed record EvolutionLabPolicy(
    bool Enabled,
    bool ScheduledDiscoveryEnabled,
    int MaxTokensPerExperiment,
    int MonthlyTokenBudget,
    int MaxExperimentsPerWeek,
    int MaxModelRounds,
    DateTimeOffset UpdatedAt)
{
    public static EvolutionLabPolicy Default => new(
        Enabled: false,
        ScheduledDiscoveryEnabled: false,
        MaxTokensPerExperiment: 16_000,
        MonthlyTokenBudget: 100_000,
        MaxExperimentsPerWeek: 3,
        MaxModelRounds: 4,
        UpdatedAt: DateTimeOffset.Now);
}

public sealed record EvolutionChangedFile(
    string Path,
    string Kind,
    long SizeBytes);

public sealed record EvolutionExperiment(
    string Id,
    string Objective,
    string Hypothesis,
    string SourceWorkspace,
    string? IsolatedWorkspace,
    EvolutionExperimentState State,
    string IsolationMode,
    string AgentPrompt,
    IReadOnlyDictionary<string, string> BaselineHashes,
    IReadOnlyList<EvolutionChangedFile> ChangedFiles,
    string VerificationCommand,
    bool? VerificationPassed,
    string Evidence,
    IReadOnlyList<string> Blockers,
    int TokenBudget,
    int ReservedTokens,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? AdoptedAt);

public sealed record EvolutionLabSnapshot(
    EvolutionLabPolicy Policy,
    IReadOnlyList<EvolutionExperiment> Experiments,
    int ActiveExperiments,
    int PassedExperiments,
    int AdoptedExperiments,
    int UsedTokensThisMonth,
    int RemainingTokensThisMonth,
    string UsageMonth,
    DateTimeOffset? LastDiscoveryAt,
    DateTimeOffset? NextDiscoveryAt,
    string DiscoveryStatus,
    string? LastDiscoveryCandidateId);

public sealed record EvolutionDiscoveryResult(
    EvolutionLabSnapshot Snapshot,
    EvolutionExperiment? Candidate,
    bool Scanned);

public sealed record EvolutionRuntimeBudget(
    string ExperimentId,
    int MaxModelRounds,
    int MaxTokensPerRequest,
    int ReservedTokens);

internal sealed record EvolutionLabState(
    EvolutionLabPolicy Policy,
    IReadOnlyList<EvolutionExperiment> Experiments,
    string UsageMonth,
    int UsedTokensThisMonth,
    DateTimeOffset? LastDiscoveryAt = null,
    string DiscoveryStatus = "自动发现尚未开启",
    string? LastDiscoveryFingerprint = null,
    string? LastDiscoveryCandidateId = null);

public sealed class EvolutionLabService
{
    private const long MaximumPluginBytes = 2L * 1024 * 1024;
    private static readonly TimeSpan FirstDiscoveryDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromHours(6);
    private static readonly HashSet<string> AllowedPluginFiles = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "nova.plugin.json",
        "SKILL.md",
        "README.md",
        "NOVA_PLUGIN_SDK.md"
    };
    private static readonly string[] ForbiddenInstructionFragments =
    [
        "bypass approval",
        "ignore approval",
        "disable safety",
        "read credentials",
        "steal credential",
        "exfiltrate",
        "越过审批",
        "绕过审批",
        "关闭安全",
        "读取密钥",
        "窃取凭据"
    ];

    private readonly string _statePath;
    private readonly string _workspaceRoot;
    private readonly SkillRegistryService _skills;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public EvolutionLabService(
        string? rootPath = null,
        SkillRegistryService? skills = null)
    {
        var root = rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "evolution-lab");
        _statePath = Path.Combine(root, "state.json");
        _workspaceRoot = Path.Combine(root, "plugin-workspaces");
        _skills = skills ?? new SkillRegistryService();
    }

    public EvolutionLabSnapshot GetSnapshot()
        => Project(NormalizeMonth(Load()));

    public async Task<EvolutionLabSnapshot> ConfigureAsync(
        bool enabled,
        bool scheduledDiscoveryEnabled,
        int maxTokensPerExperiment,
        int monthlyTokenBudget,
        int maxExperimentsPerWeek,
        int maxModelRounds,
        CancellationToken cancellationToken = default)
    {
        if (maxTokensPerExperiment is < 2_000 or > 64_000)
        {
            throw new InvalidOperationException("单次实验预算必须在 2,000 到 64,000 Token 之间。");
        }
        if (monthlyTokenBudget is < 5_000 or > 2_000_000)
        {
            throw new InvalidOperationException("月度预算必须在 5,000 到 2,000,000 Token 之间。");
        }
        if (maxTokensPerExperiment > monthlyTokenBudget)
        {
            throw new InvalidOperationException("单次实验预算不能高于月度总预算。");
        }
        if (maxExperimentsPerWeek is < 1 or > 20)
        {
            throw new InvalidOperationException("每周实验上限必须在 1 到 20 次之间。");
        }
        if (maxModelRounds is < 1 or > 12)
        {
            throw new InvalidOperationException("单次实验模型轮数必须在 1 到 12 轮之间。");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = NormalizeMonth(Load());
            var wasScheduled = state.Policy.Enabled
                               && state.Policy.ScheduledDiscoveryEnabled;
            var willSchedule = enabled && scheduledDiscoveryEnabled;
            state = state with
            {
                Policy = new EvolutionLabPolicy(
                    enabled,
                    willSchedule,
                    maxTokensPerExperiment,
                    monthlyTokenBudget,
                    maxExperimentsPerWeek,
                    maxModelRounds,
                    DateTimeOffset.Now),
                LastDiscoveryAt = willSchedule && wasScheduled
                    ? state.LastDiscoveryAt
                    : null,
                DiscoveryStatus = willSchedule
                    ? wasScheduled
                        ? state.DiscoveryStatus
                        : "等待应用空闲 10 分钟后进行首次本地扫描"
                    : "自动发现已关闭",
                LastDiscoveryFingerprint = willSchedule && wasScheduled
                    ? state.LastDiscoveryFingerprint
                    : null,
                LastDiscoveryCandidateId = willSchedule && wasScheduled
                    ? state.LastDiscoveryCandidateId
                    : null
            };
            await SaveAsync(state, cancellationToken);
            return Project(state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EvolutionLabSnapshot> ProposeAsync(
        string workspaceRoot,
        string objective,
        CancellationToken cancellationToken = default)
    {
        var contextWorkspace = ValidateContextWorkspace(workspaceRoot);
        var trimmedObjective = objective.Trim();
        if (trimmedObjective.Length is < 8 or > 1200)
        {
            throw new InvalidOperationException("进化目标需要在 8 到 1200 个字符之间。");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = NormalizeMonth(Load());
            EnsureEnabled(state.Policy);
            var recentCount = state.Experiments.Count(item =>
                item.CreatedAt >= DateTimeOffset.Now.AddDays(-7));
            if (recentCount >= state.Policy.MaxExperimentsPerWeek)
            {
                throw new InvalidOperationException(
                    $"本周插件实验已达到 {state.Policy.MaxExperimentsPerWeek} 次上限。");
            }
            if (state.UsedTokensThisMonth + state.Policy.MaxTokensPerExperiment
                > state.Policy.MonthlyTokenBudget)
            {
                throw new InvalidOperationException(
                    "月度自进化预算不足；可以等待下月或由用户调整预算。");
            }

            var experiment = CreateProposedExperiment(
                contextWorkspace,
                trimmedObjective,
                state.Policy.MaxTokensPerExperiment,
                "仅创建了实验记录；没有调用模型，也没有读取或复制核心源码。",
                DateTimeOffset.Now);
            state = state with
            {
                Experiments = state.Experiments.Append(experiment).ToArray()
            };
            await SaveAsync(state, cancellationToken);
            return Project(state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EvolutionDiscoveryResult> TryDiscoverCandidateAsync(
        IReadOnlyList<TaskSnapshot> tasks,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        var timestamp = now ?? DateTimeOffset.Now;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = NormalizeMonth(Load());
            if (!state.Policy.Enabled || !state.Policy.ScheduledDiscoveryEnabled)
            {
                return new EvolutionDiscoveryResult(Project(state), null, false);
            }

            var nextDiscoveryAt = NextDiscoveryAt(state);
            if (nextDiscoveryAt is not null && timestamp < nextDiscoveryAt)
            {
                return new EvolutionDiscoveryResult(Project(state), null, false);
            }

            var pendingReview = state.Experiments.Any(item => item.State is
                EvolutionExperimentState.Proposed
                or EvolutionExperimentState.Ready
                or EvolutionExperimentState.Running
                or EvolutionExperimentState.Evaluating
                or EvolutionExperimentState.Passed);
            if (pendingReview)
            {
                state = MarkDiscovery(
                    state,
                    timestamp,
                    "等待现有候选完成审阅，不会继续堆积实验");
                await SaveAsync(state, cancellationToken);
                return new EvolutionDiscoveryResult(Project(state), null, true);
            }

            var recentCount = state.Experiments.Count(item =>
                item.CreatedAt >= timestamp.AddDays(-7));
            if (recentCount >= state.Policy.MaxExperimentsPerWeek)
            {
                state = MarkDiscovery(
                    state,
                    timestamp,
                    $"本周已达到 {state.Policy.MaxExperimentsPerWeek} 个实验上限");
                await SaveAsync(state, cancellationToken);
                return new EvolutionDiscoveryResult(Project(state), null, true);
            }

            if (state.UsedTokensThisMonth + state.Policy.MaxTokensPerExperiment
                > state.Policy.MonthlyTokenBudget)
            {
                state = MarkDiscovery(
                    state,
                    timestamp,
                    "月度预算不足，本轮没有创建候选");
                await SaveAsync(state, cancellationToken);
                return new EvolutionDiscoveryResult(Project(state), null, true);
            }

            var signal = FindDiscoverySignal(tasks, timestamp);
            if (signal is null)
            {
                state = MarkDiscovery(
                    state,
                    timestamp,
                    "本轮未发现足够强的重复或恢复信号");
                await SaveAsync(state, cancellationToken);
                return new EvolutionDiscoveryResult(Project(state), null, true);
            }

            var duplicate = state.LastDiscoveryFingerprint == signal.Fingerprint
                            || state.Experiments.Any(item =>
                                item.SourceWorkspace.Equals(
                                    signal.WorkspaceRoot,
                                    StringComparison.OrdinalIgnoreCase)
                                && item.Objective.Equals(
                                    signal.Objective,
                                    StringComparison.Ordinal)
                                && item.CreatedAt >= timestamp.AddDays(-30));
            if (duplicate)
            {
                state = MarkDiscovery(
                    state,
                    timestamp,
                    "近期同类候选已经存在，本轮已自动去重",
                    signal.Fingerprint);
                await SaveAsync(state, cancellationToken);
                return new EvolutionDiscoveryResult(Project(state), null, true);
            }

            var experiment = CreateProposedExperiment(
                signal.WorkspaceRoot,
                signal.Objective,
                state.Policy.MaxTokensPerExperiment,
                $"由定时本地发现生成：{signal.Evidence}。"
                + "仅分析任务快照元数据，未调用模型、未复制源码、未安装能力。",
                timestamp);
            state = MarkDiscovery(
                state with
                {
                    Experiments = state.Experiments.Append(experiment).ToArray()
                },
                timestamp,
                "已生成 1 个本地候选，等待用户审阅",
                signal.Fingerprint,
                experiment.Id);
            await SaveAsync(state, cancellationToken);
            return new EvolutionDiscoveryResult(Project(state), experiment, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EvolutionLabSnapshot> PrepareAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = NormalizeMonth(Load());
            EnsureEnabled(state.Policy);
            var experiments = state.Experiments.ToList();
            var index = FindIndex(experiments, id);
            var experiment = experiments[index];
            if (experiment.State is EvolutionExperimentState.Adopted
                or EvolutionExperimentState.Rejected)
            {
                throw new InvalidOperationException("已结束的实验不能重新准备。");
            }

            var pluginRoot = Path.GetFullPath(Path.Combine(_workspaceRoot, experiment.Id));
            EnsureContained(_workspaceRoot, pluginRoot);
            if (Directory.Exists(pluginRoot))
            {
                throw new InvalidOperationException("插件实验目录已经存在。");
            }
            Directory.CreateDirectory(pluginRoot);

            var pluginId = "nova-evolved-" + experiment.Id[4..];
            await File.WriteAllTextAsync(
                Path.Combine(pluginRoot, "nova.plugin.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = 1,
                        id = pluginId,
                        name = "NOVA Evolved Capability",
                        version = "0.1.0",
                        type = "instruction-extension",
                        entry = "SKILL.md",
                        permissions = Array.Empty<string>(),
                        capabilities = new[] { "task-guidance" },
                        generatedBy = "NOVA Evolution Lab"
                    },
                    _options),
                new UTF8Encoding(false),
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(pluginRoot, "SKILL.md"),
                BuildSkillScaffold(pluginId, experiment.Objective),
                new UTF8Encoding(false),
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(pluginRoot, "README.md"),
                $"# {pluginId}\n\n实验目标：{experiment.Objective}\n\n"
                + "这是声明式插件实验，不包含可执行代码，也不能访问 NOVA 核心源码。\n",
                new UTF8Encoding(false),
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(pluginRoot, "NOVA_PLUGIN_SDK.md"),
                PluginContract,
                new UTF8Encoding(false),
                cancellationToken);

            var baseline = await CaptureHashesAsync(pluginRoot, cancellationToken);
            experiments[index] = experiment with
            {
                IsolatedWorkspace = pluginRoot,
                State = EvolutionExperimentState.Ready,
                BaselineHashes = baseline,
                VerificationCommand = "NOVA declarative plugin validator",
                VerificationPassed = true,
                Evidence =
                    "已生成公开的声明式 Plugin SDK、manifest 与 SKILL.md 脚手架；"
                    + "核心源码、凭据和内部服务均未进入实验目录。",
                UpdatedAt = DateTimeOffset.Now
            };
            state = state with { Experiments = experiments };
            await SaveAsync(state, cancellationToken);
            return Project(state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EvolutionRuntimeBudget?> ReserveRuntimeBudgetAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = NormalizeMonth(Load());
            var root = Path.GetFullPath(workspaceRoot);
            var experiments = state.Experiments.ToList();
            var index = experiments.FindIndex(item =>
                !string.IsNullOrWhiteSpace(item.IsolatedWorkspace)
                && Path.GetFullPath(item.IsolatedWorkspace)
                    .Equals(root, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return null;
            }
            EnsureEnabled(state.Policy);
            var experiment = experiments[index];
            if (experiment.State is not (
                EvolutionExperimentState.Ready
                or EvolutionExperimentState.Running
                or EvolutionExperimentState.Failed))
            {
                throw new InvalidOperationException(
                    $"实验当前状态为 {experiment.State}，不能调用模型。");
            }

            if (experiment.ReservedTokens > 0
                && experiment.State != EvolutionExperimentState.Failed)
            {
                throw new InvalidOperationException(
                    "本实验的模型预算已经整笔预留并使用。请先静态检查产物；需要再次调用模型时，新建一个实验。");
            }
            var reserve = experiment.TokenBudget;
            if (state.UsedTokensThisMonth + reserve > state.Policy.MonthlyTokenBudget)
            {
                throw new InvalidOperationException("月度自进化 Token 预算已用尽。");
            }
            experiment = experiment with
            {
                ReservedTokens = reserve,
                State = EvolutionExperimentState.Running,
                Evidence = experiment.Evidence
                           + $"\n已为本实验硬预留 {reserve:N0} Token；本实验只允许一次模型运行。",
                UpdatedAt = DateTimeOffset.Now
            };
            experiments[index] = experiment;
            state = state with
            {
                Experiments = experiments,
                UsedTokensThisMonth = state.UsedTokensThisMonth + reserve
            };
            await SaveAsync(state, cancellationToken);

            var rounds = Math.Clamp(state.Policy.MaxModelRounds, 1, 12);
            var perRequest = Math.Clamp(experiment.TokenBudget / rounds, 512, 4096);
            return new EvolutionRuntimeBudget(
                experiment.Id,
                rounds,
                perRequest,
                experiment.ReservedTokens);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EvolutionLabSnapshot> EvaluateAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = NormalizeMonth(Load());
            var experiments = state.Experiments.ToList();
            var index = FindIndex(experiments, id);
            var experiment = experiments[index];
            var pluginRoot = RequirePluginWorkspace(experiment);
            var current = await CaptureHashesAsync(pluginRoot, cancellationToken);
            var changes = Compare(experiment.BaselineHashes, current, pluginRoot);
            var blockers = ValidatePlugin(pluginRoot, experiment, changes);
            var passed = blockers.Count == 0;
            experiments[index] = experiment with
            {
                State = passed
                    ? EvolutionExperimentState.Passed
                    : EvolutionExperimentState.Failed,
                ChangedFiles = changes,
                VerificationCommand = "NOVA declarative plugin validator",
                VerificationPassed = passed,
                Evidence = BuildEvidence(changes, blockers),
                Blockers = blockers,
                UpdatedAt = DateTimeOffset.Now
            };
            state = state with { Experiments = experiments };
            await SaveAsync(state, cancellationToken);
            return Project(state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EvolutionLabSnapshot> AdoptAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = NormalizeMonth(Load());
            var experiments = state.Experiments.ToList();
            var index = FindIndex(experiments, id);
            var experiment = experiments[index];
            if (experiment.State != EvolutionExperimentState.Passed
                || experiment.VerificationPassed != true)
            {
                throw new InvalidOperationException("只有通过插件沙箱验证的实验可以安装。");
            }
            var pluginRoot = RequirePluginWorkspace(experiment);
            var blockers = ValidatePlugin(
                pluginRoot,
                experiment,
                Compare(
                    experiment.BaselineHashes,
                    await CaptureHashesAsync(pluginRoot, cancellationToken),
                    pluginRoot));
            if (blockers.Count > 0)
            {
                throw new InvalidOperationException("插件在审阅后发生变化，请重新验证。");
            }

            var installed = await _skills.InstallFromFolderAsync(
                pluginRoot,
                cancellationToken);
            experiments[index] = experiment with
            {
                State = EvolutionExperimentState.Adopted,
                Evidence = experiment.Evidence
                           + $"\n插件已作为可停用能力安装：{installed.Id}。NOVA 核心没有被修改。",
                Blockers = [],
                UpdatedAt = DateTimeOffset.Now,
                AdoptedAt = DateTimeOffset.Now
            };
            state = state with { Experiments = experiments };
            await SaveAsync(state, cancellationToken);
            return Project(state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EvolutionLabSnapshot> RejectAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = NormalizeMonth(Load());
            var experiments = state.Experiments.ToList();
            var index = FindIndex(experiments, id);
            var experiment = experiments[index];
            if (experiment.State == EvolutionExperimentState.Adopted)
            {
                throw new InvalidOperationException("已安装插件请在 Skills 页面停用或卸载。");
            }
            experiments[index] = experiment with
            {
                State = EvolutionExperimentState.Rejected,
                Evidence = experiment.Evidence
                           + "\n用户已放弃该插件实验；核心与能力仓均未被修改。",
                UpdatedAt = DateTimeOffset.Now
            };
            state = state with { Experiments = experiments };
            await SaveAsync(state, cancellationToken);
            return Project(state);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IReadOnlyList<string> ValidatePlugin(
        string root,
        EvolutionExperiment experiment,
        IReadOnlyList<EvolutionChangedFile> changes)
    {
        var blockers = new List<string>();
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => NormalizeRelative(Path.GetRelativePath(root, path)))
            .ToArray();
        var totalBytes = files.Sum(path => new FileInfo(ResolveContained(root, path)).Length);
        if (totalBytes > MaximumPluginBytes)
        {
            blockers.Add("插件包超过 2 MB 上限。");
        }
        foreach (var file in files)
        {
            if (!AllowedPluginFiles.Contains(file))
            {
                blockers.Add($"插件包含未授权文件：{file}");
            }
        }
        if (changes.Count == 0
            || !changes.Any(item => item.Path.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)))
        {
            blockers.Add("实验没有对 SKILL.md 产生可审阅的能力变化。");
        }
        if (changes.Any(item => item.Kind == "deleted"))
        {
            blockers.Add("声明式插件不允许删除 SDK 或 manifest 文件。");
        }

        try
        {
            var manifest = JsonNode.Parse(
                               File.ReadAllText(Path.Combine(root, "nova.plugin.json")))
                           ?.AsObject()
                           ?? throw new JsonException();
            if (manifest["schemaVersion"]?.GetValue<int>() != 1
                || manifest["type"]?.GetValue<string>() != "instruction-extension"
                || manifest["entry"]?.GetValue<string>() != "SKILL.md")
            {
                blockers.Add("插件 manifest 不符合 NOVA 声明式能力契约。");
            }
            if (manifest["permissions"] is not JsonArray permissions
                || permissions.Count != 0)
            {
                blockers.Add("自进化插件不得声明核心、网络、凭据、文件或桌面权限。");
            }
        }
        catch (Exception exception) when (exception is JsonException
                                          or InvalidOperationException
                                          or IOException)
        {
            blockers.Add("nova.plugin.json 无法解析。");
        }

        var skillPath = Path.Combine(root, "SKILL.md");
        if (!File.Exists(skillPath))
        {
            blockers.Add("插件缺少 SKILL.md。");
        }
        else
        {
            var skill = File.ReadAllText(skillPath);
            if (skill.Length is < 120 or > 24_000)
            {
                blockers.Add("SKILL.md 必须在 120 到 24,000 字符之间。");
            }
            if (ForbiddenInstructionFragments.Any(fragment =>
                    skill.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            {
                blockers.Add("SKILL.md 包含试图扩大权限或绕过安全边界的指令。");
            }
            if (!skill.Contains("不得扩大权限", StringComparison.Ordinal)
                || !skill.Contains("人工确认", StringComparison.Ordinal))
            {
                blockers.Add("SKILL.md 缺少不扩权与人工确认约束。");
            }
        }

        return blockers.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string BuildAgentPrompt(string id, string pluginId, string objective)
        => $"""
           NOVA Plugin Evolution experiment {id}

           Objective:
           {objective}

           You are inside a declarative plugin sandbox. NOVA core source code is not present
           and must not be requested. Read NOVA_PLUGIN_SDK.md, then improve only SKILL.md,
           README.md and the non-permission metadata in nova.plugin.json.

           This is an implementation run, not a research or advisory task. The objective above
           is already the approved evidence summary. Do not query personal memory, productivity,
           knowledge graphs, artifacts, MCP, the network, or the source workspace. Use only the
           files in this sandbox. Read the SDK and existing SKILL.md first, then you MUST call
           write_text_file or replace_text_in_file to make a substantive, objective-specific
           change to SKILL.md before giving a final response. A text-only answer or an unchanged
           SKILL.md is a failed experiment. Keep enough model rounds for the write and final check.

           Hard boundaries:
           - Plugin id: {pluginId}
           - No executable code, scripts, dependencies, network calls or credential access.
           - permissions must remain an empty array.
           - Do not claim to bypass approvals, workspace containment or user confirmation.
           - Keep the change small, reviewable and directly connected to the objective.
           - Preserve the required non-escalation and human-confirmation safety statements.
           - Finish by summarizing the changed plugin files. Installation remains a separate
             user-confirmed Evolution Lab action.
           """;

    private static string BuildSkillScaffold(string pluginId, string objective)
        => $"""
           ---
           name: {pluginId}
           description: 由 NOVA 插件进化舱生成的声明式能力候选。
           ---

           # 目标

           {objective}

           # 工作方式

           - 先理解用户本轮目标，再提供与目标直接相关的帮助。
           - 只把本插件内容作为建议，不把建议伪装成已经执行的结果。
           - 不得扩大权限、读取凭据、操作核心更新或绕过工作区边界。
           - 涉及写入、网络、桌面、安装与外部账号时，继续经过 NOVA 的人工确认。

           # 完成标准

           输出应当可检查、与用户目标对应，并明确区分事实、推断和未完成项。
           """;

    private const string PluginContract =
        """
        # NOVA Declarative Plugin SDK 1.0

        Evolution Lab intentionally exposes this contract instead of NOVA core source.

        ## Allowed package

        - `nova.plugin.json`: identity and permission-free capability declaration.
        - `SKILL.md`: plain-language task guidance loaded through the managed Skill registry.
        - `README.md`: human review notes.
        - `NOVA_PLUGIN_SDK.md`: this immutable public contract.

        ## Security boundary

        Generated plugins cannot contain executable code, dependencies, network clients,
        native binaries, credentials, file-system privileges, desktop privileges or updater
        privileges. A plugin cannot weaken approval, recovery, evidence or workspace rules.

        ## Lifecycle

        draft -> static validation -> human review -> install as a disableable Skill.
        The core application is never patched by this workflow.
        """;

    private static IReadOnlyList<EvolutionChangedFile> Compare(
        IReadOnlyDictionary<string, string> baseline,
        IReadOnlyDictionary<string, string> current,
        string root)
    {
        var changes = new List<EvolutionChangedFile>();
        foreach (var path in baseline.Keys.Concat(current.Keys)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var beforeExists = baseline.TryGetValue(path, out var before);
            var afterExists = current.TryGetValue(path, out var after);
            if (beforeExists && afterExists
                && string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var kind = !beforeExists ? "added" : !afterExists ? "deleted" : "modified";
            var file = ResolveContained(root, path);
            changes.Add(new EvolutionChangedFile(
                path,
                kind,
                File.Exists(file) ? new FileInfo(file).Length : 0));
        }
        return changes;
    }

    private static string BuildEvidence(
        IReadOnlyList<EvolutionChangedFile> changes,
        IReadOnlyList<string> blockers)
    {
        var fileSummary = changes.Count == 0
            ? "没有变化"
            : string.Join("、", changes.Select(item => $"{item.Kind}:{item.Path}"));
        return $"插件差异：{fileSummary}\n"
               + (blockers.Count == 0
                   ? "静态验证：PASS；无执行代码、无权限声明、核心未修改。"
                   : "静态验证：FAIL\n" + string.Join("\n", blockers));
    }

    private static void EnsureEnabled(EvolutionLabPolicy policy)
    {
        if (!policy.Enabled)
        {
            throw new InvalidOperationException(
                "插件自进化当前处于关闭状态；请先在 Evolution Lab 明确开启。");
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> CaptureHashesAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizeRelative(Path.GetRelativePath(root, file));
            hashes[relative] = await HashFileAsync(file, cancellationToken);
        }
        return hashes;
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static string ValidateContextWorkspace(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new InvalidOperationException("请先选择一个任务工作区作为实验上下文。");
        }
        var root = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"工作区不存在：{root}");
        }
        return root;
    }

    private static string RequirePluginWorkspace(EvolutionExperiment experiment)
    {
        if (string.IsNullOrWhiteSpace(experiment.IsolatedWorkspace)
            || !Directory.Exists(experiment.IsolatedWorkspace))
        {
            throw new InvalidOperationException("插件实验尚未准备。");
        }
        return Path.GetFullPath(experiment.IsolatedWorkspace);
    }

    private static EvolutionExperiment CreateProposedExperiment(
        string workspaceRoot,
        string objective,
        int tokenBudget,
        string evidence,
        DateTimeOffset timestamp)
    {
        var id = "evo-" + timestamp.ToString("yyyyMMdd-HHmmss")
                 + "-" + Guid.NewGuid().ToString("N")[..6];
        var pluginId = "nova-evolved-" + id[4..];
        var hypothesis =
            $"把“{Limit(objective, 180)}”封装成一个声明式能力插件，"
            + "可以在不暴露、不复制且不修改 NOVA 核心源码的前提下验证它是否有价值。";
        return new EvolutionExperiment(
            id,
            objective,
            hypothesis,
            workspaceRoot,
            null,
            EvolutionExperimentState.Proposed,
            "declarative-plugin",
            BuildAgentPrompt(id, pluginId, objective),
            new Dictionary<string, string>(),
            [],
            "等待插件沙箱验证",
            null,
            evidence,
            [],
            tokenBudget,
            0,
            timestamp,
            timestamp,
            null);
    }

    private static EvolutionDiscoverySignal? FindDiscoverySignal(
        IReadOnlyList<TaskSnapshot> tasks,
        DateTimeOffset timestamp)
    {
        var candidates = tasks
            .Where(task =>
                !task.IsArchived
                && task.UpdatedAt >= timestamp.AddDays(-30)
                && !string.IsNullOrWhiteSpace(task.WorkspaceRoot)
                && Directory.Exists(task.WorkspaceRoot))
            .GroupBy(
                task => Path.GetFullPath(task.WorkspaceRoot),
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var items = group.OrderByDescending(item => item.UpdatedAt).ToArray();
                var friction = items.Count(item => item.State is
                    TaskState.Failed
                    or TaskState.BudgetExhausted
                    or TaskState.Stale
                    or TaskState.Cancelled);
                return new
                {
                    WorkspaceRoot = group.Key,
                    Items = items,
                    Friction = friction,
                    Score = friction * 10 + items.Length
                };
            })
            .Where(group => group.Friction > 0 || group.Items.Length >= 2)
            .OrderByDescending(group => group.Score)
            .ThenByDescending(group => group.Items[0].UpdatedAt)
            .ToArray();

        var selected = candidates.FirstOrDefault();
        if (selected is null)
        {
            return null;
        }

        var workspaceName = Path.GetFileName(
            selected.WorkspaceRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(workspaceName))
        {
            workspaceName = "当前工作区";
        }

        var kind = selected.Friction > 0 ? "recovery" : "workflow";
        var objective = selected.Friction > 0
            ? $"为 {workspaceName} 提炼可复用的任务恢复能力：根据近期 "
              + $"{selected.Friction} 次失败、预算耗尽或中断记录，"
              + "保留上下文和证据并给出最小可继续步骤。"
            : $"为 {workspaceName} 提炼可复用的工作流能力：总结近期 "
              + $"{selected.Items.Length} 个任务中的重复目标和操作习惯，"
              + "减少重复说明并保持结果验证。";
        var fingerprintSource = string.Join(
            "\n",
            new[]
            {
                selected.WorkspaceRoot.ToUpperInvariant(),
                kind
            }.Concat(selected.Items
                .Take(8)
                .Select(item => $"{item.TaskId}|{item.State}|{item.Title}")));
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource)));
        var evidence = selected.Friction > 0
            ? $"近 30 天在该工作区发现 {selected.Friction} 个恢复信号"
            : $"近 30 天在该工作区发现 {selected.Items.Length} 个可归纳任务";
        return new EvolutionDiscoverySignal(
            selected.WorkspaceRoot,
            objective,
            evidence,
            fingerprint);
    }

    private static EvolutionLabState MarkDiscovery(
        EvolutionLabState state,
        DateTimeOffset timestamp,
        string status,
        string? fingerprint = null,
        string? candidateId = null)
        => state with
        {
            LastDiscoveryAt = timestamp,
            DiscoveryStatus = status,
            LastDiscoveryFingerprint = fingerprint ?? state.LastDiscoveryFingerprint,
            LastDiscoveryCandidateId = candidateId ?? state.LastDiscoveryCandidateId
        };

    private static DateTimeOffset? NextDiscoveryAt(EvolutionLabState state)
    {
        if (!state.Policy.Enabled || !state.Policy.ScheduledDiscoveryEnabled)
        {
            return null;
        }
        return state.LastDiscoveryAt is null
            ? state.Policy.UpdatedAt + FirstDiscoveryDelay
            : state.LastDiscoveryAt + DiscoveryInterval;
    }

    private EvolutionLabState Load()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return EmptyState();
            }
            return JsonSerializer.Deserialize<EvolutionLabState>(
                       File.ReadAllText(_statePath),
                       _options)
                   ?? EmptyState();
        }
        catch (JsonException)
        {
            return EmptyState();
        }
    }

    private EvolutionLabState EmptyState()
        => new(
            EvolutionLabPolicy.Default,
            [],
            CurrentMonth(),
            0);

    private static EvolutionLabState NormalizeMonth(EvolutionLabState state)
    {
        var normalized = state.UsageMonth == CurrentMonth()
            ? state
            : state with { UsageMonth = CurrentMonth(), UsedTokensThisMonth = 0 };
        if (string.IsNullOrWhiteSpace(normalized.DiscoveryStatus)
            || (normalized.DiscoveryStatus == "自动发现尚未开启"
                && normalized.Policy.Enabled
                && normalized.Policy.ScheduledDiscoveryEnabled))
        {
            normalized = normalized with
            {
                DiscoveryStatus = normalized.Policy.Enabled
                                  && normalized.Policy.ScheduledDiscoveryEnabled
                    ? "等待应用空闲 10 分钟后进行首次本地扫描"
                    : "自动发现已关闭"
            };
        }
        return normalized;
    }

    private async Task SaveAsync(
        EvolutionLabState state,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temporary = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(state, _options),
            new UTF8Encoding(false),
            cancellationToken);
        File.Move(temporary, _statePath, overwrite: true);
    }

    private static EvolutionLabSnapshot Project(EvolutionLabState state)
    {
        var experiments = state.Experiments
            .OrderByDescending(item => item.UpdatedAt)
            .ToArray();
        return new EvolutionLabSnapshot(
            state.Policy,
            experiments,
            experiments.Count(item => item.State is
                EvolutionExperimentState.Proposed
                or EvolutionExperimentState.Ready
                or EvolutionExperimentState.Running
                or EvolutionExperimentState.Evaluating),
            experiments.Count(item => item.State == EvolutionExperimentState.Passed),
            experiments.Count(item => item.State == EvolutionExperimentState.Adopted),
            state.UsedTokensThisMonth,
            Math.Max(0, state.Policy.MonthlyTokenBudget - state.UsedTokensThisMonth),
            state.UsageMonth,
            state.LastDiscoveryAt,
            NextDiscoveryAt(state),
            state.DiscoveryStatus,
            state.LastDiscoveryCandidateId);
    }

    private static int FindIndex(IReadOnlyList<EvolutionExperiment> items, string id)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index].Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }
        throw new InvalidOperationException($"插件实验不存在：{id}");
    }

    private sealed record EvolutionDiscoverySignal(
        string WorkspaceRoot,
        string Objective,
        string Evidence,
        string Fingerprint);

    private static string ResolveContained(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("插件文件必须使用相对路径。");
        }
        var target = Path.GetFullPath(
            Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        EnsureContained(root, target);
        return target;
    }

    private static void EnsureContained(string root, string target)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("插件路径越过了实验沙箱。");
        }
    }

    private static string NormalizeRelative(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/');

    private static string CurrentMonth()
        => DateTimeOffset.Now.ToString("yyyy-MM");

    private static string Limit(string value, int maximum)
        => value.Length <= maximum ? value : value[..maximum] + "…";
}
