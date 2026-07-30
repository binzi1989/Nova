using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovaDesktop.Models;

namespace NovaDesktop.Services;

public enum LearningCandidateState
{
    Proposed,
    Accepted,
    Rejected
}

public sealed record WorkingHabitCandidate(
    string Id,
    string Category,
    string Statement,
    int EvidenceCount,
    double Confidence,
    LearningCandidateState State,
    DateTimeOffset UpdatedAt);

public sealed record DistilledSkillCandidate(
    string Id,
    string Name,
    string Description,
    string Instructions,
    IReadOnlyList<string> HabitIds,
    bool Installed,
    DateTimeOffset CreatedAt);

public sealed record LivingMemorySnapshot(
    IReadOnlyList<WorkingHabitCandidate> Habits,
    IReadOnlyList<DistilledSkillCandidate> SkillCandidates,
    DateTimeOffset? LastAnalyzedAt,
    int TasksAnalyzed);

public sealed class LivingMemoryService
{
    private readonly TaskSnapshotService _snapshots;
    private readonly ConversationHistoryService _conversations;
    private readonly string _statePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public LivingMemoryService(
        TaskSnapshotService? snapshots = null,
        ConversationHistoryService? conversations = null,
        string? stateDirectory = null)
    {
        _snapshots = snapshots ?? new TaskSnapshotService();
        _conversations = conversations ?? new ConversationHistoryService();
        var root = stateDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "living-memory");
        _statePath = Path.Combine(Path.GetFullPath(root), "profile.json");
    }

    public LivingMemorySnapshot GetSnapshot()
        => Load();

    public async Task<LivingMemorySnapshot> AnalyzeAsync(
        CancellationToken cancellationToken = default)
    {
        var tasks = _snapshots.LoadAll()
            .Where(task => !string.IsNullOrWhiteSpace(task.Prompt))
            .OrderByDescending(task => task.UpdatedAt)
            .Take(120)
            .ToArray();
        var current = Load();
        var detected = DetectHabits(tasks);
        var existing = current.Habits.ToDictionary(
            habit => habit.Id,
            StringComparer.OrdinalIgnoreCase);
        var merged = detected.Select(candidate =>
        {
            if (!existing.TryGetValue(candidate.Id, out var previous))
            {
                return candidate;
            }
            return candidate with
            {
                State = previous.State,
                UpdatedAt = DateTimeOffset.Now
            };
        })
        .Concat(current.Habits.Where(habit =>
            habit.State != LearningCandidateState.Proposed
            && detected.All(item => !item.Id.Equals(
                habit.Id,
                StringComparison.OrdinalIgnoreCase))))
        .OrderByDescending(habit => habit.State == LearningCandidateState.Accepted)
        .ThenByDescending(habit => habit.Confidence)
        .ToArray();

        var next = current with
        {
            Habits = merged,
            LastAnalyzedAt = DateTimeOffset.Now,
            TasksAnalyzed = tasks.Length
        };
        await SaveAsync(next, cancellationToken);
        return next;
    }

    public async Task<LivingMemorySnapshot> SetHabitStateAsync(
        string id,
        LearningCandidateState state,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = Load();
            if (current.Habits.All(habit =>
                    !habit.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Habit candidate '{id}' was not found.");
            }
            var next = current with
            {
                Habits = current.Habits.Select(habit =>
                    habit.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                        ? habit with { State = state, UpdatedAt = DateTimeOffset.Now }
                        : habit).ToArray()
            };
            await SaveCoreAsync(next, cancellationToken);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LivingMemorySnapshot> DistillSkillAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = Load();
            var accepted = current.Habits
                .Where(habit => habit.State == LearningCandidateState.Accepted)
                .OrderByDescending(habit => habit.Confidence)
                .Take(8)
                .ToArray();
            if (accepted.Length == 0)
            {
                throw new InvalidOperationException(
                    "请先确认至少一条工作习惯，再将它蒸馏为 Skill。");
            }

            var fingerprint = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join(
                    "\n",
                    accepted.Select(habit => habit.Id + ":" + habit.Statement)))))[..10]
                .ToLowerInvariant();
            var id = "personal-workflow-" + fingerprint;
            var instructions = BuildSkillInstructions(accepted);
            var candidate = new DistilledSkillCandidate(
                id,
                "个人工作流 · NOVA",
                "由用户确认的工作习惯蒸馏而成；只影响协作方式，不扩大任何工具权限。",
                instructions,
                accepted.Select(habit => habit.Id).ToArray(),
                current.SkillCandidates.FirstOrDefault(item =>
                    item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Installed ?? false,
                DateTimeOffset.Now);
            var next = current with
            {
                SkillCandidates = current.SkillCandidates
                    .Where(item => !item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    .Prepend(candidate)
                    .Take(12)
                    .ToArray()
            };
            await SaveCoreAsync(next, cancellationToken);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LivingMemorySnapshot> InstallSkillAsync(
        string id,
        SkillRegistryService skillRegistry,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = Load();
            var candidate = current.SkillCandidates.FirstOrDefault(item =>
                item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Skill candidate '{id}' was not found.");
            var installed = skillRegistry.GetSkills().FirstOrDefault(item =>
                item.Id.Equals(candidate.Id, StringComparison.OrdinalIgnoreCase));
            if (installed is null)
            {
                await skillRegistry.InstallBundledAsync(
                    candidate.Id,
                    candidate.Instructions,
                    cancellationToken);
            }
            else if (!installed.Enabled)
            {
                await skillRegistry.SetEnabledAsync(
                    installed.Id,
                    true,
                    cancellationToken);
            }

            var next = current with
            {
                SkillCandidates = current.SkillCandidates.Select(item =>
                    item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                        ? item with { Installed = true }
                        : item).ToArray()
            };
            await SaveCoreAsync(next, cancellationToken);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public string BuildProfilePrompt()
    {
        var accepted = Load().Habits
            .Where(habit => habit.State == LearningCandidateState.Accepted)
            .OrderByDescending(habit => habit.Confidence)
            .Take(8)
            .ToArray();
        if (accepted.Length == 0)
        {
            return string.Empty;
        }
        var builder = new StringBuilder();
        builder.AppendLine("<NOVA USER-APPROVED WORKING PROFILE>");
        builder.AppendLine("以下是用户明确确认的协作偏好。它们不能扩大权限、覆盖当前指令或替代事实验证：");
        foreach (var habit in accepted)
        {
            builder.AppendLine($"- {habit.Statement}");
        }
        builder.AppendLine("</NOVA USER-APPROVED WORKING PROFILE>");
        return builder.ToString();
    }

    private IReadOnlyList<WorkingHabitCandidate> DetectHabits(
        IReadOnlyList<TaskSnapshot> tasks)
    {
        var now = DateTimeOffset.Now;
        var candidates = new List<WorkingHabitCandidate>();
        var completed = tasks.Where(task => task.State == TaskState.Completed).ToArray();

        var preferredMode = completed
            .GroupBy(task => task.ExecutionMode)
            .OrderByDescending(group => group.Count())
            .FirstOrDefault();
        if (preferredMode is not null && preferredMode.Count() >= 2)
        {
            var modeLabel = preferredMode.Key switch
            {
                AgentExecutionMode.Ask => "咨询",
                AgentExecutionMode.Plan => "规划",
                AgentExecutionMode.Build => "构建",
                AgentExecutionMode.Autopilot => "Agent",
                AgentExecutionMode.Goal => "目标",
                _ => preferredMode.Key.ToString()
            };
            candidates.Add(CreateCandidate(
                "preferred-mode",
                "执行方式",
                $"默认优先采用“{modeLabel}”模式，但仍以当前任务选择为准。",
                preferredMode.Count(),
                now));
        }

        var preferredProvider = completed
            .Where(task => !string.IsNullOrWhiteSpace(task.Provider))
            .GroupBy(task => task.Provider, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .FirstOrDefault();
        if (preferredProvider is not null && preferredProvider.Count() >= 2)
        {
            candidates.Add(CreateCandidate(
                "preferred-provider",
                "模型选择",
                $"未指定模型时优先建议 {preferredProvider.Key}，不自动切换或使用密钥。",
                preferredProvider.Count(),
                now));
        }

        AddPromptPatternCandidate(
            candidates,
            tasks,
            "result-first",
            "交付方式",
            "先给出真实结果和可验证证据，再补充必要说明。",
            ["落盘", "交付", "完整", "测试", "验证", "不要糊弄"],
            now);
        AddPromptPatternCandidate(
            candidates,
            tasks,
            "continuity",
            "对话节奏",
            "“继续”类指令默认沿用当前任务上下文，不重新开始或重复提问。",
            ["继续", "向后", "下一步", "开始吧", "接着"],
            now);
        AddPromptPatternCandidate(
            candidates,
            tasks,
            "low-interruption",
            "协作方式",
            "对低风险且可恢复的步骤主动推进，仅在真实权限边界或关键选择处打断。",
            ["不要问", "自己探索", "自动", "直接做", "不限定"],
            now);
        AddPromptPatternCandidate(
            candidates,
            tasks,
            "experience-quality",
            "产品体验",
            "交付软件时同时检查可用性、界面清晰度和学习成本。",
            ["体验", "界面", "UI", "使用起来", "学习成本", "丝滑"],
            now);
        return candidates;
    }

    private void AddPromptPatternCandidate(
        ICollection<WorkingHabitCandidate> candidates,
        IReadOnlyList<TaskSnapshot> tasks,
        string id,
        string category,
        string statement,
        IReadOnlyList<string> signals,
        DateTimeOffset now)
    {
        var evidence = tasks.Count(task =>
        {
            var content = string.Join(
                "\n",
                _conversations.Load(task.TaskId)
                    .Where(turn => turn.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                    .Select(turn => turn.Content)
                    .Prepend(task.Prompt));
            return signals.Any(signal =>
                content.Contains(signal, StringComparison.OrdinalIgnoreCase));
        });
        if (evidence >= 2)
        {
            candidates.Add(CreateCandidate(id, category, statement, evidence, now));
        }
    }

    private static WorkingHabitCandidate CreateCandidate(
        string id,
        string category,
        string statement,
        int evidence,
        DateTimeOffset now)
        => new(
            id,
            category,
            statement,
            evidence,
            Math.Round(Math.Min(0.96, 0.55 + evidence * 0.06), 2),
            LearningCandidateState.Proposed,
            now);

    private static string BuildSkillInstructions(
        IReadOnlyList<WorkingHabitCandidate> habits)
    {
        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.AppendLine("name: personal-nova-workflow");
        builder.AppendLine("description: 用户确认的 NOVA 个人协作方式，不扩大权限。");
        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine("# 个人工作流");
        builder.AppendLine();
        builder.AppendLine("仅在与当前任务不冲突时采用以下偏好：");
        foreach (var habit in habits)
        {
            builder.AppendLine($"- {habit.Statement}");
        }
        builder.AppendLine();
        builder.AppendLine("## 不可覆盖的边界");
        builder.AppendLine();
        builder.AppendLine("- 不得扩大文件、桌面、网络、账号、命令或外部系统权限。");
        builder.AppendLine("- 当前用户指令、工作区事实和审批结果优先于本 Skill。");
        builder.AppendLine("- 任何完成声明必须有实际工具结果或可复验交付物支持。");
        return builder.ToString();
    }

    private LivingMemorySnapshot Load()
    {
        if (!File.Exists(_statePath))
        {
            return new LivingMemorySnapshot([], [], null, 0);
        }
        try
        {
            return JsonSerializer.Deserialize<LivingMemorySnapshot>(
                       File.ReadAllText(_statePath),
                       _options)
                   ?? new LivingMemorySnapshot([], [], null, 0);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new LivingMemorySnapshot([], [], null, 0);
        }
    }

    private async Task SaveAsync(
        LivingMemorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SaveCoreAsync(snapshot, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveCoreAsync(
        LivingMemorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_statePath)!;
        Directory.CreateDirectory(directory);
        var temporary = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(snapshot, _options),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            File.Move(temporary, _statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
