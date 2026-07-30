using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace NovaDesktop.Services;

public sealed record EngineeringDiffHunk(
    string Id,
    string FilePath,
    string Header,
    string Preview,
    int Additions,
    int Deletions)
{
    internal string PatchText { get; init; } = string.Empty;
    public string ChangeLabel => $"+{Additions} / -{Deletions}";
}

public sealed record GitHunkOperationResult(
    bool Succeeded,
    string Action,
    int HunkCount,
    string Detail,
    int ExitCode);

public sealed class GitHunkReviewService
{
    private readonly EngineeringEvidenceLedgerService _evidenceLedger;

    public GitHunkReviewService(EngineeringEvidenceLedgerService? evidenceLedger = null)
    {
        _evidenceLedger = evidenceLedger ?? new EngineeringEvidenceLedgerService();
    }

    public async Task<IReadOnlyList<EngineeringDiffHunk>> GetUnstagedHunksAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var result = await RunGitAsync(
            workspaceRoot,
            ["diff", "--no-ext-diff", "--no-textconv", "--no-color", "--unified=3"],
            null,
            TimeSpan.FromSeconds(20),
            cancellationToken);
        if (!result.Started || result.ExitCode != 0)
        {
            return [];
        }
        return Parse(result.Output);
    }

    public Task<GitHunkOperationResult> StageAsync(
        string workspaceRoot,
        IReadOnlyCollection<string> hunkIds,
        CancellationToken cancellationToken = default)
        => ApplyAsync(workspaceRoot, hunkIds, stage: true, cancellationToken);

    public Task<GitHunkOperationResult> RevertAsync(
        string workspaceRoot,
        IReadOnlyCollection<string> hunkIds,
        CancellationToken cancellationToken = default)
        => ApplyAsync(workspaceRoot, hunkIds, stage: false, cancellationToken);

    private async Task<GitHunkOperationResult> ApplyAsync(
        string workspaceRoot,
        IReadOnlyCollection<string> hunkIds,
        bool stage,
        CancellationToken cancellationToken)
    {
        if (hunkIds.Count == 0)
        {
            return new GitHunkOperationResult(false, stage ? "stage" : "revert", 0, "没有选择变更块。", -1);
        }

        var current = await GetUnstagedHunksAsync(workspaceRoot, cancellationToken);
        var selected = current.Where(hunk => hunkIds.Contains(hunk.Id)).ToArray();
        if (selected.Length != hunkIds.Distinct(StringComparer.Ordinal).Count())
        {
            throw new InvalidOperationException(
                "所选 Hunk 已经过期。工作区在审查后发生了变化，请刷新 Diff 后重新选择。");
        }

        var patch = string.Join(Environment.NewLine, selected.Select(hunk => hunk.PatchText));
        var action = stage ? "stage-selected-hunks" : "revert-selected-hunks";
        var arguments = stage
            ? new[] { "apply", "--cached", "--whitespace=nowarn", "-" }
            : new[] { "apply", "--reverse", "--whitespace=nowarn", "-" };
        var startedAt = Stopwatch.GetTimestamp();
        var result = await RunGitAsync(
            workspaceRoot,
            arguments,
            patch,
            TimeSpan.FromSeconds(30),
            cancellationToken);
        var duration = Stopwatch.GetElapsedTime(startedAt);
        var succeeded = result.Started && result.ExitCode == 0;
        var detail = succeeded
            ? stage
                ? $"已把 {selected.Length} 个 Hunk 加入暂存区。"
                : $"已从工作区撤销 {selected.Length} 个 Hunk。"
            : FirstLine(result.Error) ?? "Git apply 操作失败。";

        try
        {
            await _evidenceLedger.AppendAsync(
                "manual-engineering",
                workspaceRoot,
                "hunk-review",
                action,
                string.Join(", ", selected.Select(hunk => $"{hunk.FilePath}:{hunk.Header}")),
                succeeded ? "completed" : "failed",
                true,
                result.ExitCode,
                duration,
                result.Output + result.Error,
                detail,
                cancellationToken);
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            // Git outcome remains authoritative.
        }

        return new GitHunkOperationResult(
            succeeded,
            stage ? "stage" : "revert",
            selected.Length,
            detail,
            result.ExitCode);
    }

    private static IReadOnlyList<EngineeringDiffHunk> Parse(string diff)
    {
        var lines = diff.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var hunks = new List<EngineeringDiffHunk>();
        var fileHeaders = new List<string>();
        var filePath = string.Empty;
        for (var index = 0; index < lines.Length;)
        {
            if (lines[index].StartsWith("diff --git ", StringComparison.Ordinal))
            {
                fileHeaders.Clear();
                fileHeaders.Add(lines[index++]);
                filePath = string.Empty;
                while (index < lines.Length
                       && !lines[index].StartsWith("@@ ", StringComparison.Ordinal)
                       && !lines[index].StartsWith("diff --git ", StringComparison.Ordinal))
                {
                    var headerLine = lines[index++];
                    fileHeaders.Add(headerLine);
                    if (headerLine.StartsWith("+++ b/", StringComparison.Ordinal))
                    {
                        filePath = headerLine[6..];
                    }
                }
                continue;
            }

            if (!lines[index].StartsWith("@@ ", StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            var hunkLines = new List<string> { lines[index++] };
            while (index < lines.Length
                   && !lines[index].StartsWith("@@ ", StringComparison.Ordinal)
                   && !lines[index].StartsWith("diff --git ", StringComparison.Ordinal))
            {
                hunkLines.Add(lines[index++]);
            }

            var additions = hunkLines.Count(line =>
                line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal));
            var deletions = hunkLines.Count(line =>
                line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal));
            var patchText = string.Join('\n', fileHeaders.Concat(hunkLines)) + "\n";
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(patchText)))
                .ToLowerInvariant()[..20];
            hunks.Add(new EngineeringDiffHunk(
                id,
                string.IsNullOrWhiteSpace(filePath) ? "unknown" : filePath,
                hunkLines[0],
                string.Join('\n', hunkLines),
                additions,
                deletions)
            {
                PatchText = patchText
            });
        }
        return hunks;
    }

    private static async Task<GitResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string? standardInput,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = Path.GetFullPath(workingDirectory),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = standardInput is not null,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new GitResult(false, -1, string.Empty, "Git 进程未能启动。");
            }
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
                process.StandardInput.Close();
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
                return new GitResult(true, -1, await outputTask, "Git Hunk 操作超时，已终止。");
            }
            return new GitResult(true, process.ExitCode, await outputTask, await errorTask);
        }
        catch (Exception exception)
        {
            return new GitResult(false, -1, string.Empty, exception.Message);
        }
    }

    private static string? FirstLine(string value)
        => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

    private static bool IsPersistenceFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException;

    private sealed record GitResult(bool Started, int ExitCode, string Output, string Error);
}
