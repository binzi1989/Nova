using System.Diagnostics;
using System.Text;

namespace Nova.Core;

public sealed record ParallelChatTask(
    string Title,
    string Instruction);

public sealed record ParallelChatWorkerResult(
    string Title,
    string Text,
    TimeSpan Duration);

public sealed record ParallelChatResult(
    AgentChatResult Commander,
    IReadOnlyList<ParallelChatWorkerResult> Workers,
    TimeSpan Duration);

public static class CrossPlatformParallelPlanner
{
    private static readonly string[] ResearchSignals =
    [
        "调研", "研究", "市场", "竞品", "比较", "对比", "趋势",
        "research", "market", "competitor", "compare"
    ];

    private static readonly string[] ExperienceSignals =
    [
        "ui", "ux", "界面", "交互", "动画", "体验", "性能", "响应",
        "design", "animation", "performance"
    ];

    public static IReadOnlyList<ParallelChatTask> Create(string goal)
    {
        var boundedGoal = string.IsNullOrWhiteSpace(goal)
            ? "审查当前工作区"
            : goal.Trim()[..Math.Min(goal.Trim().Length, 5000)];
        if (ContainsAny(boundedGoal, ResearchSignals))
        {
            return
            [
                new("证据研究员", WorkerGoal(boundedGoal, "基于提供的工程信号梳理事实、缺失证据和不确定性。")),
                new("产品策略师", WorkerGoal(boundedGoal, "从用户价值、差异化、采用成本和可执行性独立分析。")),
                new("反方审查员", WorkerGoal(boundedGoal, "主动寻找反例、竞争风险、技术限制和验证方法。"))
            ];
        }
        if (ContainsAny(boundedGoal, ExperienceSignals))
        {
            return
            [
                new("体验审查员", WorkerGoal(boundedGoal, "审查首次使用、任务清晰度、状态反馈和错误恢复。")),
                new("性能架构师", WorkerGoal(boundedGoal, "分析渲染、异步、状态管理和资源风险，按影响排序。")),
                new("验证设计师", WorkerGoal(boundedGoal, "设计可复现的验收场景、边界条件和回归测试。"))
            ];
        }
        return
        [
            new("工程探索员", WorkerGoal(boundedGoal, "梳理相关入口、依赖、工程约束和待确认事实。")),
            new("方案架构师", WorkerGoal(boundedGoal, "提出最小可行方案、影响范围、取舍和实施顺序。")),
            new("测试审查员", WorkerGoal(boundedGoal, "列出风险、验收标准、验证命令和失败回滚点。"))
        ];
    }

    private static string WorkerGoal(string goal, string specialty)
        => $"目标：{goal}\n你的角色任务：{specialty}"
           + "当前 Mac Preview 只有只读工程信号，没有文件读取或命令工具。"
           + "不得声称检查了具体文件或执行了操作；明确区分事实、推断与待验证项。";

    private static bool ContainsAny(string value, IEnumerable<string> signals)
        => signals.Any(signal => value.Contains(signal, StringComparison.OrdinalIgnoreCase));
}

public sealed class ParallelChatService
{
    private readonly ProviderChatService _chatService;

    public ParallelChatService(ProviderChatService? chatService = null)
    {
        _chatService = chatService ?? new ProviderChatService();
    }

    public async Task<ParallelChatResult> RunAsync(
        AgentChatRequest request,
        IReadOnlyList<ParallelChatTask> tasks,
        CancellationToken cancellationToken)
    {
        if (tasks.Count is < 2 or > 4)
        {
            throw new InvalidOperationException("并行工作组必须包含 2 至 4 个子 Agent。");
        }

        var stopwatch = Stopwatch.StartNew();
        var workerTasks = tasks.Select(async task =>
        {
            var result = await _chatService.SendAsync(
                request with
                {
                    Messages =
                    [
                        new AgentMessage(
                            "user",
                            $"你是 NOVA 的只读子 Agent「{task.Title}」。\n{task.Instruction}")
                    ]
                },
                cancellationToken);
            return new ParallelChatWorkerResult(task.Title, result.Text, result.Duration);
        });
        var workers = await Task.WhenAll(workerTasks);

        var evidence = new StringBuilder();
        evidence.AppendLine("以下是三个独立只读子 Agent 的结果。请交叉核对冲突，明确不确定性，");
        evidence.AppendLine("然后针对用户原始目标给出一个统一、可执行的最终回答；不要声称执行了工具或修改。");
        foreach (var worker in workers)
        {
            evidence.AppendLine();
            evidence.AppendLine($"## {worker.Title}");
            evidence.AppendLine(worker.Text);
        }
        var commander = await _chatService.SendAsync(
            request with
            {
                Messages =
                [
                    .. request.Messages,
                    new AgentMessage("user", evidence.ToString())
                ]
            },
            cancellationToken);
        stopwatch.Stop();
        return new ParallelChatResult(commander, workers, stopwatch.Elapsed);
    }
}
