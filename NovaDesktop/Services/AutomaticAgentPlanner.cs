using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using NovaDesktop.Models;

namespace NovaDesktop.Services;

public sealed record AutomaticAgentPlan(
    string Strategy,
    IReadOnlyList<ParallelAgentTask> Tasks)
{
    public JsonObject ToArguments()
        => new()
        {
            ["strategy"] = Strategy,
            ["tasks"] = new JsonArray(
                Tasks.Select(task => new JsonObject
                {
                    ["title"] = task.Title,
                    ["instruction"] = task.Instruction
                }).ToArray())
        };

    public string ToApprovalPreview()
        => JsonSerializer.Serialize(new
        {
            strategy = Strategy,
            workers = Tasks.Select((task, index) => new
            {
                number = index + 1,
                role = task.Title,
                boundary = "只读检查当前工作区，返回独立结论；不能写文件或执行命令"
            })
        }, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

    public string ToExecutionPlanPayload()
        => JsonSerializer.Serialize(new
        {
            strategy = Strategy,
            steps = Tasks.Select((task, index) => new
            {
                id = $"parallel-{index + 1}",
                title = task.Title,
                detail = task.Instruction,
                agent = $"子 Agent {index + 1}"
            })
        });
}

public sealed record ParallelAgentTask(
    string Title,
    string Instruction);

public static class AutomaticAgentPlanner
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

    public static AutomaticAgentPlan? Create(
        string prompt,
        AgentExecutionMode mode,
        bool allowParallelDelegation)
    {
        if (mode is not (AgentExecutionMode.Autopilot or AgentExecutionMode.Goal)
            || !allowParallelDelegation
            || string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        var goal = prompt.Trim();
        if (goal.Length > 5000)
        {
            goal = goal[..5000];
        }
        if (goal.Contains("[NOVA_AGENT_WORKSHOP]", StringComparison.OrdinalIgnoreCase))
        {
            return new AutomaticAgentPlan(
                "Agent 工坊多角色编排",
                [
                    new ParallelAgentTask(
                        "行业架构师",
                        $"目标：{goal}\n你处在并行设计阶段，不会看到其他子 Agent 的结果，也不需要等待或检查同伴产出。"
                        + "从目标行业、服务对象、真实使用场景、必要资料、未知项和责任边界进行独立分析。"
                        + "不要修改文件或编写代码，不要套用通用模板；明确事实、推断与需要用户验证的内容。"),
                    new ParallelAgentTask(
                        "工作流架构师",
                        $"目标：{goal}\n你处在并行设计阶段，不会看到其他子 Agent 的结果，也不需要等待或检查同伴产出。"
                        + "设计 2–6 个职责不重叠的 Agent 角色，以及 3–8 个存在清晰依赖关系的执行步骤。"
                        + "不要修改文件；每一步必须声明负责人、真实交付物和可检查的验收条件。"),
                    new ParallelAgentTask(
                        "信任审查官",
                        $"目标：{goal}\n你是并行阶段的独立风险审查员，不是最终裁决者。你不会看到其他子 Agent 的结果，"
                        + "不得因为同伴产出尚未展示而拒绝设计，也不要声称 Supervisor 漏交材料。"
                        + "只需检查角色重复、输入不足、虚假完成、越权风险、预算风险和不可验证交付；"
                        + "不要修改文件，列出最终协调 Agent 形成可审阅草案时必须吸收的风险与验收条件。")
                ]);
        }
        if (ContainsAny(goal, ResearchSignals))
        {
            return new AutomaticAgentPlan(
                "并行证据研究",
                [
                    new ParallelAgentTask(
                        "证据研究员",
                        $"目标：{goal}\n只读检查工作区中与目标相关的材料、数据和现有实现。"
                        + "给出带相对路径的事实、缺失证据和不确定性；不要修改文件。"),
                    new ParallelAgentTask(
                        "产品策略师",
                        $"目标：{goal}\n从用户价值、差异化、采用成本和可执行性角度独立分析。"
                        + "可读取必要的本地资料；明确哪些判断是推断。"),
                    new ParallelAgentTask(
                        "反方审查员",
                        $"目标：{goal}\n主动寻找反例、竞争风险、技术限制和容易被高估的结论。"
                        + "基于只读证据提出验证方式。")
                ]);
        }

        if (ContainsAny(goal, ExperienceSignals))
        {
            return new AutomaticAgentPlan(
                "体验与工程并行审查",
                [
                    new ParallelAgentTask(
                        "体验审查员",
                        $"目标：{goal}\n只读检查相关界面、状态与交互代码，找出最影响首次使用、"
                        + "任务清晰度和错误恢复的问题；引用相对文件路径。"),
                    new ParallelAgentTask(
                        "性能架构师",
                        $"目标：{goal}\n只读分析渲染、事件、异步、状态管理和资源使用风险。"
                        + "提出按影响排序的最小改进，不要修改文件。"),
                    new ParallelAgentTask(
                        "验证设计师",
                        $"目标：{goal}\n只读检查现有验证基础，设计可复现的验收场景、边界条件和回归测试，"
                        + "指出哪些现有测试能够复用。")
                ]);
        }

        return new AutomaticAgentPlan(
            "工程闭环并行预检",
            [
                new ParallelAgentTask(
                    "代码探索员",
                    $"目标：{goal}\n使用只读工作区工具定位相关文件、入口、依赖和已有约束。"
                    + "返回相对路径与关键事实，不要修改文件。"),
                new ParallelAgentTask(
                    "方案架构师",
                    $"目标：{goal}\n基于只读工作区证据独立提出最小可行实现、影响范围和取舍。"
                    + "不要假设不存在的组件。"),
                new ParallelAgentTask(
                    "测试审查员",
                    $"目标：{goal}\n只读检查现有测试和构建方式，列出风险、验收标准、"
                    + "建议验证命令和失败回滚点。")
            ]);
    }

    private static bool ContainsAny(string value, IEnumerable<string> signals)
        => signals.Any(signal => value.Contains(signal, StringComparison.OrdinalIgnoreCase));
}
