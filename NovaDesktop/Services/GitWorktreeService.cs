using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace NovaDesktop.Services;

public sealed record GitWorktreeSession(
    string SessionId,
    string SourceRepository,
    string WorkspaceRoot,
    string Head,
    bool Created,
    string Detail,
    DateTimeOffset CreatedAt);

public sealed record GitWorktreeRecycleResult(
    bool Succeeded,
    string SourceRepository,
    string RemovedWorkspace,
    string? RecoveryPath,
    string Detail,
    int ExitCode);

public sealed class GitWorktreeService
{
    private readonly string _worktreeRoot;
    private readonly string _recoveryRoot;
    private readonly EngineeringEvidenceLedgerService _evidenceLedger;

    public GitWorktreeService(
        string? worktreeRoot = null,
        EngineeringEvidenceLedgerService? evidenceLedger = null,
        string? recoveryRoot = null)
    {
        _worktreeRoot = worktreeRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "worktrees");
        _recoveryRoot = recoveryRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "worktree-recovery");
        _evidenceLedger = evidenceLedger ?? new EngineeringEvidenceLedgerService();
    }

    public bool IsManagedWorktree(string workspaceRoot)
    {
        try
        {
            var root = Path.GetFullPath(_worktreeRoot).TrimEnd(Path.DirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            var workspace = Path.GetFullPath(workspaceRoot);
            return workspace.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                   && File.Exists(Path.Combine(workspace, ".git"));
        }
        catch
        {
            return false;
        }
    }

    public async Task<GitWorktreeSession> CreateAsync(
        string repositoryRoot,
        string? sessionLabel = null,
        CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Git 工作区不存在：{source}");
        }

        var topLevel = await RunGitAsync(
            source,
            ["rev-parse", "--show-toplevel"],
            TimeSpan.FromSeconds(10),
            cancellationToken);
        if (!topLevel.Started || topLevel.ExitCode != 0)
        {
            throw new InvalidOperationException(
                FirstLine(topLevel.Error) ?? "当前工作区不是可用的 Git 仓库。");
        }

        var repository = Path.GetFullPath(
            FirstLine(topLevel.Output)
            ?? throw new InvalidOperationException("Git 未返回仓库根目录。"));
        var headResult = await RunGitAsync(
            repository,
            ["rev-parse", "HEAD"],
            TimeSpan.FromSeconds(10),
            cancellationToken);
        if (!headResult.Started || headResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                FirstLine(headResult.Error) ?? "仓库还没有可用于隔离的提交。");
        }

        var head = FirstLine(headResult.Output) ?? "HEAD";
        var sessionId = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss")
                        + "-"
                        + SafeName(sessionLabel ?? "engineering")
                        + "-"
                        + Guid.NewGuid().ToString("N")[..6];
        var repositoryId = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(repository.ToUpperInvariant())))
            .ToLowerInvariant()[..16];
        var repositorySessions = Path.GetFullPath(Path.Combine(_worktreeRoot, repositoryId));
        var target = Path.GetFullPath(Path.Combine(repositorySessions, sessionId));
        var allowedPrefix = repositorySessions.TrimEnd(Path.DirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;
        var repositoryPrefix = repository.TrimEnd(Path.DirectorySeparatorChar)
                               + Path.DirectorySeparatorChar;
        if (!target.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase)
            || repositorySessions.Equals(repository, StringComparison.OrdinalIgnoreCase)
            || repositorySessions.StartsWith(repositoryPrefix, StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(target)
            || File.Exists(target))
        {
            throw new InvalidOperationException("隔离工作区目标无效或已经存在。");
        }

        Directory.CreateDirectory(repositorySessions);
        var startedAt = Stopwatch.GetTimestamp();
        var createResult = await RunGitAsync(
            repository,
            ["worktree", "add", "--detach", target, head],
            TimeSpan.FromMinutes(2),
            cancellationToken);
        var duration = Stopwatch.GetElapsedTime(startedAt);
        var created = createResult.Started
                      && createResult.ExitCode == 0
                      && Directory.Exists(target);
        var detail = created
            ? "隔离 Worktree 已从已提交 HEAD 创建；主工作区未提交变更没有被复制。"
            : FirstLine(createResult.Error) ?? "Git Worktree 创建失败。";

        try
        {
            await _evidenceLedger.AppendAsync(
                "manual-engineering",
                repository,
                "worktree",
                "create-isolated-worktree",
                target,
                created ? "created" : "failed",
                true,
                createResult.ExitCode,
                duration,
                JoinOutput(createResult.Output, createResult.Error),
                detail,
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException
                                          or System.Security.SecurityException)
        {
            // Worktree outcome remains authoritative if optional evidence persistence fails.
        }

        return new GitWorktreeSession(
            sessionId,
            repository,
            target,
            head,
            created,
            detail,
            DateTimeOffset.Now);
    }

    public async Task<GitWorktreeRecycleResult> RecycleAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var target = Path.GetFullPath(workspaceRoot);
        if (!IsManagedWorktree(target))
        {
            throw new InvalidOperationException("当前目录不是 NOVA 管理的隔离 Worktree，拒绝回收。");
        }

        var listResult = await RunGitAsync(
            target,
            ["worktree", "list", "--porcelain"],
            TimeSpan.FromSeconds(15),
            cancellationToken);
        var registered = ParseWorktreePaths(listResult.Output);
        var source = registered.FirstOrDefault(path =>
            !Path.GetFullPath(path).Equals(target, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            throw new InvalidOperationException("无法确定主 Git 工作区，拒绝执行破坏性回收。");
        }

        var statusResult = await RunGitAsync(
            target,
            ["status", "--porcelain"],
            TimeSpan.FromSeconds(15),
            cancellationToken);
        string? recoveryPath = null;
        if (!string.IsNullOrWhiteSpace(statusResult.Output))
        {
            recoveryPath = await CreateRecoveryAsync(
                target,
                statusResult.Output,
                cancellationToken);
        }

        var startedAt = Stopwatch.GetTimestamp();
        var removeResult = await RunGitAsync(
            source,
            ["worktree", "remove", "--force", target],
            TimeSpan.FromMinutes(2),
            cancellationToken);
        var duration = Stopwatch.GetElapsedTime(startedAt);
        var succeeded = removeResult.Started
                        && removeResult.ExitCode == 0
                        && !Directory.Exists(target);
        var detail = succeeded
            ? recoveryPath is null
                ? "隔离 Worktree 已安全回收；该工作区没有未提交变更。"
                : $"隔离 Worktree 已回收，未提交内容已保存到：{recoveryPath}"
            : FirstLine(removeResult.Error) ?? "Git Worktree 回收失败。";

        try
        {
            await _evidenceLedger.AppendAsync(
                "manual-engineering",
                source,
                "worktree",
                "recycle-isolated-worktree",
                target,
                succeeded ? "recycled" : "failed",
                true,
                removeResult.ExitCode,
                duration,
                JoinOutput(removeResult.Output, removeResult.Error),
                detail,
                cancellationToken);
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            // Git outcome and recovery package remain authoritative.
        }

        return new GitWorktreeRecycleResult(
            succeeded,
            source,
            target,
            recoveryPath,
            detail,
            removeResult.ExitCode);
    }

    public async Task<GitWorktreeRecycleResult> DiscardAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var target = Path.GetFullPath(workspaceRoot);
        if (!IsManagedWorktree(target))
        {
            throw new InvalidOperationException(
                "当前目录不是 NOVA 管理的隔离 Worktree，拒绝丢弃。");
        }

        var listResult = await RunGitAsync(
            target,
            ["worktree", "list", "--porcelain"],
            TimeSpan.FromSeconds(15),
            cancellationToken);
        var source = ParseWorktreePaths(listResult.Output).FirstOrDefault(path =>
            !Path.GetFullPath(path).Equals(target, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            throw new InvalidOperationException("无法确定主 Git 工作区，拒绝丢弃候选。");
        }

        var startedAt = Stopwatch.GetTimestamp();
        var removeResult = await RunGitAsync(
            source,
            ["worktree", "remove", "--force", target],
            TimeSpan.FromMinutes(2),
            cancellationToken);
        var duration = Stopwatch.GetElapsedTime(startedAt);
        var succeeded = removeResult.Started
                        && removeResult.ExitCode == 0
                        && !Directory.Exists(target);
        var detail = succeeded
            ? "隔离候选已丢弃；其 Tournament Patch 与清单仍保留在交付物目录。"
            : FirstLine(removeResult.Error) ?? "隔离候选丢弃失败。";

        try
        {
            await _evidenceLedger.AppendAsync(
                "worktree-tournament",
                source,
                "worktree",
                "discard-tournament-candidate",
                target,
                succeeded ? "discarded" : "failed",
                true,
                removeResult.ExitCode,
                duration,
                JoinOutput(removeResult.Output, removeResult.Error),
                detail,
                cancellationToken);
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            // The managed Worktree removal remains authoritative.
        }

        return new GitWorktreeRecycleResult(
            succeeded,
            source,
            target,
            null,
            detail,
            removeResult.ExitCode);
    }

    private async Task<string> CreateRecoveryAsync(
        string worktree,
        string status,
        CancellationToken cancellationToken)
    {
        var recoveryPath = Path.Combine(
            _recoveryRoot,
            DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss")
            + "-"
            + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(recoveryPath);

        var diffResult = await RunGitAsync(
            worktree,
            ["diff", "HEAD", "--binary", "--no-ext-diff", "--no-textconv"],
            TimeSpan.FromSeconds(30),
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(recoveryPath, "changes.patch"),
            diffResult.Output,
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(recoveryPath, "status.txt"),
            status,
            cancellationToken);

        foreach (var line in status.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("?? ", StringComparison.Ordinal))
            {
                continue;
            }
            var relative = line[3..].Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
            CopyUntrackedEntry(worktree, recoveryPath, relative);
        }
        return recoveryPath;
    }

    private static void CopyUntrackedEntry(string worktree, string recoveryPath, string relative)
    {
        if (Path.IsPathRooted(relative)
            || relative.Split(Path.DirectorySeparatorChar).Any(segment => segment == ".."))
        {
            throw new InvalidOperationException("Git 返回了不安全的未跟踪路径，回收已停止。");
        }

        var source = Path.GetFullPath(Path.Combine(worktree, relative));
        var workspacePrefix = Path.GetFullPath(worktree).TrimEnd(Path.DirectorySeparatorChar)
                              + Path.DirectorySeparatorChar;
        if (!source.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("未跟踪文件越出 Worktree，回收已停止。");
        }

        var destination = Path.Combine(recoveryPath, "untracked", relative);
        if (File.Exists(source))
        {
            if (File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("未跟踪文件是链接，回收已停止。");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: false);
            return;
        }

        if (!Directory.Exists(source))
        {
            return;
        }
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((source, destination));
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (File.GetAttributes(current.Source).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("未跟踪目录包含链接，回收已停止。");
            }
            Directory.CreateDirectory(current.Destination);
            foreach (var file in Directory.EnumerateFiles(current.Source))
            {
                if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidOperationException("未跟踪目录包含链接文件，回收已停止。");
                }
                File.Copy(file, Path.Combine(current.Destination, Path.GetFileName(file)), overwrite: false);
            }
            foreach (var directory in Directory.EnumerateDirectories(current.Source))
            {
                pending.Push((
                    directory,
                    Path.Combine(current.Destination, Path.GetFileName(directory))));
            }
        }
    }

    private static IReadOnlyList<string> ParseWorktreePaths(string output)
        => output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(line => line[9..].Trim())
            .ToArray();

    private static async Task<GitResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
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
                return new GitResult(true, -1, await outputTask, "Git Worktree 操作超时，已终止。");
            }

            return new GitResult(true, process.ExitCode, await outputTask, await errorTask);
        }
        catch (Exception exception)
        {
            return new GitResult(false, -1, string.Empty, exception.Message);
        }
    }

    private static string SafeName(string value)
    {
        var safe = string.Concat(value
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-'));
        safe = safe.Trim('-');
        return string.IsNullOrWhiteSpace(safe) ? "engineering" : safe[..Math.Min(safe.Length, 40)];
    }

    private static string? FirstLine(string value)
        => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

    private static string JoinOutput(string output, string error)
        => string.Join(
            Environment.NewLine,
            new[] { output.Trim(), error.Trim() }.Where(value => value.Length > 0));

    private static bool IsPersistenceFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException;

    private sealed record GitResult(bool Started, int ExitCode, string Output, string Error);
}
