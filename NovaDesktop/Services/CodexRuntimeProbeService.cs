using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace NovaDesktop.Services;

public enum CodexRuntimeAvailability
{
    Unavailable,
    Detected,
    Ready,
    Blocked
}

public sealed record CodexRuntimeProbe(
    CodexRuntimeAvailability Availability,
    string Status,
    string Detail,
    string? ExecutablePath,
    string? Version,
    bool SupportsExec);

public sealed record CodexReadOnlyReviewResult(
    bool Started,
    bool Succeeded,
    string Review,
    int ExitCode,
    TimeSpan Duration,
    string Detail);

public sealed class CodexRuntimeProbeService
{
    private readonly string? _explicitPath;

    public CodexRuntimeProbeService(string? explicitPath = null)
    {
        _explicitPath = explicitPath;
    }

    public Task<CodexRuntimeProbe> ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = ResolveExecutable();
        if (string.IsNullOrWhiteSpace(executable))
        {
            return Task.FromResult(new CodexRuntimeProbe(
                CodexRuntimeAvailability.Unavailable,
                "未安装独立 Codex CLI",
                "NOVA 仍可使用 OpenAI / DeepSeek 工程模式。安装独立 CLI 后可自动探测。",
                null,
                null,
                false));
        }

        return Task.FromResult(new CodexRuntimeProbe(
            CodexRuntimeAvailability.Detected,
            "已发现 Codex 可执行文件",
            "NOVA 尚未启动此程序。点击“验证 CLI”后才会只读运行版本与能力检查。",
            executable,
            null,
            false));
    }

    public async Task<CodexRuntimeProbe> ProbeExecutableAsync(
        CancellationToken cancellationToken = default)
    {
        var executable = ResolveExecutable();
        if (string.IsNullOrWhiteSpace(executable))
        {
            return await ProbeAsync(cancellationToken);
        }

        var versionResult = await RunAsync(executable, ["--version"], null, TimeSpan.FromSeconds(5), cancellationToken);
        if (!versionResult.Started)
        {
            return new CodexRuntimeProbe(
                CodexRuntimeAvailability.Blocked,
                "已发现，但当前不可调用",
                versionResult.Error
                ?? "Windows 阻止了第三方进程调用此 Codex 可执行文件。",
                executable,
                null,
                false);
        }

        if (versionResult.ExitCode != 0)
        {
            return new CodexRuntimeProbe(
                CodexRuntimeAvailability.Detected,
                "已发现，能力检查未通过",
                FirstMeaningfulLine(versionResult.Error) ?? $"退出码 {versionResult.ExitCode}",
                executable,
                FirstMeaningfulLine(versionResult.Output),
                false);
        }

        var helpResult = await RunAsync(executable, ["exec", "--help"], null, TimeSpan.FromSeconds(5), cancellationToken);
        var supportsExec = helpResult.Started
                           && helpResult.ExitCode == 0
                           && helpResult.Output.Contains("exec", StringComparison.OrdinalIgnoreCase);
        return new CodexRuntimeProbe(
            supportsExec ? CodexRuntimeAvailability.Ready : CodexRuntimeAvailability.Detected,
            supportsExec ? "Codex Runtime 就绪" : "Codex CLI 已连接",
            supportsExec
                ? "支持受控非交互工程会话；正式执行仍需经过 NOVA 授权代理。"
                : "已读取版本，但尚未确认受控 exec 能力。",
            executable,
            FirstMeaningfulLine(versionResult.Output),
            supportsExec);
    }

    public async Task<CodexReadOnlyReviewResult> RunReadOnlyReviewAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var probe = await ProbeExecutableAsync(cancellationToken);
        if (probe.Availability != CodexRuntimeAvailability.Ready
            || string.IsNullOrWhiteSpace(probe.ExecutablePath))
        {
            return new CodexReadOnlyReviewResult(
                false,
                false,
                string.Empty,
                -1,
                TimeSpan.Zero,
                probe.Detail);
        }

        const string prompt =
            "Review the current Git changes in this workspace. Operate read-only. "
            + "Prioritize correctness, regressions, security, concurrency, data loss, and missing tests. "
            + "Cite file paths and line numbers. Do not modify files or run mutating commands. "
            + "Respond concisely in Chinese.";
        var startedAt = Stopwatch.GetTimestamp();
        var result = await RunAsync(
            probe.ExecutablePath,
            ["exec", "--json", "--sandbox", "read-only", "--skip-git-repo-check", prompt],
            Path.GetFullPath(workspaceRoot),
            TimeSpan.FromMinutes(5),
            cancellationToken);
        var duration = Stopwatch.GetElapsedTime(startedAt);
        var review = ExtractReview(result.Output);
        var succeeded = result.Started && result.ExitCode == 0 && !string.IsNullOrWhiteSpace(review);
        return new CodexReadOnlyReviewResult(
            result.Started,
            succeeded,
            review,
            result.ExitCode,
            duration,
            succeeded
                ? "Codex 只读代码审查完成。"
                : FirstMeaningfulLine(result.Error) ?? "Codex 没有返回可显示的审查结果。");
    }

    private string? ResolveExecutable()
    {
        var configured = _explicitPath;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = Environment.GetEnvironmentVariable("NOVA_CODEX_PATH");
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                var fullPath = Path.GetFullPath(configured);
                return File.Exists(fullPath) ? fullPath : null;
            }
            catch
            {
                return null;
            }
        }

        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var entry in pathEntries)
        {
            try
            {
                var candidate = Path.Combine(entry, OperatingSystem.IsWindows() ? "codex.exe" : "codex");
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static async Task<ProbeProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory ?? string.Empty,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new ProbeProcessResult(false, -1, string.Empty, "进程未能启动。");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return new ProbeProcessResult(true, -1, await outputTask, "能力探测超时。");
            }

            return new ProbeProcessResult(
                true,
                process.ExitCode,
                await outputTask,
                await errorTask);
        }
        catch (Exception exception)
        {
            return new ProbeProcessResult(false, -1, string.Empty, exception.Message);
        }
    }

    private static string? FirstMeaningfulLine(string? value)
        => value?
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

    private static string ExtractReview(string output)
    {
        var messages = new List<string>();
        var plain = new StringBuilder();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var node = JsonNode.Parse(line);
                var item = node?["item"];
                var itemType = item?["type"]?.GetValue<string>();
                var text = item?["text"]?.GetValue<string>()
                           ?? node?["message"]?["content"]?.GetValue<string>();
                if (itemType == "agent_message" && !string.IsNullOrWhiteSpace(text))
                {
                    messages.Add(text);
                }
            }
            catch
            {
                plain.AppendLine(line);
            }
        }

        if (messages.Count > 0)
        {
            return messages[^1];
        }
        return plain.ToString().Trim();
    }

    private sealed record ProbeProcessResult(bool Started, int ExitCode, string Output, string? Error);
}
