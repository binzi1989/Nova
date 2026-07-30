using NovaDesktop.Models;

namespace NovaDesktop.Services;

public sealed record EngineeringTaskProfile(
    bool IsEngineeringTask,
    string Intent,
    string Risk,
    string Verification);

public static class EngineeringTaskRouter
{
    private static readonly string[] EngineeringSignals =
    [
        "代码", "编程", "开发", "实现", "修复", "重构", "测试", "构建", "编译", "提交",
        "小程序", "微信小程序", ".wxml", ".wxss", "project.config.json",
        "bug", "code", "coding", "implement", "fix", "refactor", "test", "build", "compile",
        ".cs", ".ts", ".js", ".py", ".rs", "xaml", "wpf", "api", "sdk", "git"
    ];

    private static readonly string[] HighRiskSignals =
    [
        "删除", "迁移", "数据库", "生产", "部署", "密钥", "权限", "认证",
        "delete", "migration", "database", "production", "deploy", "secret", "permission", "auth"
    ];

    private static readonly string[] MutationSignals =
    [
        "开发", "实现", "创建", "生成", "新增", "添加", "修改", "修复", "重构", "优化", "改一下", "做一个", "搞一个",
        "develop", "implement", "create", "generate", "add", "change", "modify", "fix", "refactor", "optimize", "build"
    ];

    public static EngineeringTaskProfile Classify(string prompt)
    {
        var text = prompt?.Trim() ?? string.Empty;
        var isEngineering = EngineeringSignals.Any(signal =>
            text.Contains(signal, StringComparison.OrdinalIgnoreCase));
        if (!isEngineering)
        {
            return new EngineeringTaskProfile(false, "GENERAL", "LOW", "按任务目标验证");
        }

        var highRisk = HighRiskSignals.Any(signal =>
            text.Contains(signal, StringComparison.OrdinalIgnoreCase));
        var verification = text.Contains("测试", StringComparison.OrdinalIgnoreCase)
                           || text.Contains("test", StringComparison.OrdinalIgnoreCase)
            ? "运行目标测试并保留原始退出码"
            : "先构建，再运行与改动最接近的测试";

        return new EngineeringTaskProfile(
            true,
            "ENGINEERING",
            highRisk ? "HIGH" : "MEDIUM",
            verification);
    }

    public static string EnrichPrompt(string prompt)
    {
        var profile = Classify(prompt);
        if (!profile.IsEngineeringTask)
        {
            return prompt;
        }

        return
            """
            [NOVA PROFESSIONAL ENGINEERING MODE]
            Treat the workspace as the source of truth. Inspect relevant files before proposing edits.
            Keep changes scoped to the stated goal and preserve unrelated user work.
            All writes and commands remain behind NOVA's approval broker; never claim an action ran without tool evidence.
            After editing, run the narrowest relevant build or test, report its exact result, and distinguish verified facts from inference.
            If verification cannot run, state the concrete blocker and do not label the change complete.

            USER GOAL:
            """ + Environment.NewLine + prompt;
    }

    public static bool RequiresWorkspaceMutation(string prompt)
    {
        var text = prompt?.Trim() ?? string.Empty;
        return Classify(text).IsEngineeringTask
               && MutationSignals.Any(signal =>
                    text.Contains(signal, StringComparison.OrdinalIgnoreCase));
    }

    public static string GetRuntimeEngineeringContract(AgentExecutionMode mode)
        => AgentExecutionPolicy.CanMutateWorkspace(mode)
            ?
            """
            [ENGINEERING COMPLETION CONTRACT]
            For coding work, do not optimize for the smallest number of files or the fastest plausible answer. Optimize for
            a coherent, runnable, maintainable result that closes the user's actual goal.
            - Inspect project manifests, entry points, relevant implementations, call sites, configuration and nearest tests
              before editing. Search for existing conventions and reuse them.
            - For a new project or feature, implement the complete vertical slice: entry/configuration, core behavior,
              integration wiring, errors and edge cases, plus the nearest meaningful automated verification.
            - Do not substitute a one-file demo, TODO, placeholder, fake data path or explanatory prose for required behavior.
              Never hide unfinished work behind a successful build.
            - After every repair, re-read affected files and trace the changed call path. Continue until build/tests and the
              engineering completeness gate pass, or report the exact external blocker.
            - Preserve unrelated user changes. A broad goal may require multiple coherent files; a narrow fix should remain narrow.
            """
            :
            """
            [READ-ONLY ENGINEERING CONTRACT]
            Inspect manifests, implementations, call sites and tests before concluding. Cite concrete workspace evidence and
            distinguish a verified diagnosis from a proposed implementation.
            """;
}
