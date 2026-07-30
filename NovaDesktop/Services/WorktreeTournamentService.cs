using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NovaDesktop.Services;

public sealed record TournamentCandidateSpec(
    string Id,
    string Provider,
    string Model,
    string Strategy);

public sealed record TournamentCandidateResult(
    TournamentCandidateSpec Spec,
    GitWorktreeSession Session,
    AgentRunResult? AgentResult,
    EngineeringVerificationResult? Verification,
    EngineeringCodeReviewResult? Review,
    string Status,
    string Detail,
    string Patch,
    string PatchPath,
    string PatchSha256,
    int Additions,
    int Deletions)
{
    public bool IsEligible
        => Status == "READY"
           && AgentResult is { MutatingToolCalls: > 0 }
           && Patch.Length > 0
           && (Verification is null || Verification.Passed);
}

public sealed record WorktreeTournamentResult(
    string TournamentId,
    string TaskId,
    string SourceRepository,
    string BaseHead,
    IReadOnlyList<TournamentCandidateResult> Candidates,
    string ArtifactDirectory,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

public sealed record TournamentPatchApplyResult(
    bool Applied,
    int ExitCode,
    string Detail);

public sealed class WorktreeTournamentService
{
    private readonly GitWorktreeService _worktrees;
    private readonly EngineeringWorkspaceService _engineering;
    private readonly string _artifactRoot;

    public WorktreeTournamentService(
        GitWorktreeService? worktrees = null,
        EngineeringWorkspaceService? engineering = null,
        string? artifactRoot = null)
    {
        _worktrees = worktrees ?? new GitWorktreeService();
        _engineering = engineering ?? new EngineeringWorkspaceService();
        _artifactRoot = artifactRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "tournaments");
    }

    public async Task<WorktreeTournamentResult> RunAsync(
        string sourceRepository,
        string taskId,
        IReadOnlyList<TournamentCandidateSpec> candidates,
        Func<TournamentCandidateSpec, string, CancellationToken, Task<AgentRunResult>> runCandidate,
        bool runVerification,
        CancellationToken cancellationToken = default)
    {
        if (candidates.Count is < 2 or > 3)
        {
            throw new InvalidOperationException("Worktree Tournament requires two or three candidates.");
        }
        if (candidates.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != candidates.Count)
        {
            throw new InvalidOperationException("Tournament candidate IDs must be unique.");
        }

        var source = Path.GetFullPath(sourceRepository);
        var sourceSnapshot = await _engineering.InspectAsync(source, cancellationToken);
        if (!sourceSnapshot.IsGitRepository)
        {
            throw new InvalidOperationException("Worktree Tournament requires a Git repository.");
        }
        if (sourceSnapshot.ChangedFiles.Count > 0)
        {
            throw new InvalidOperationException(
                "主工作区存在未提交修改。为避免候选忽略或覆盖用户工作，Tournament 已停止。");
        }

        var baseHeadResult = await RunGitAsync(
            source,
            ["rev-parse", "HEAD"],
            TimeSpan.FromSeconds(10),
            cancellationToken);
        EnsureGitSucceeded(baseHeadResult, "无法读取 Tournament 基准提交。");
        var baseHead = FirstLine(baseHeadResult.Output)
                       ?? throw new InvalidOperationException("Git 未返回基准提交。");
        var tournamentId = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss")
                           + "-"
                           + SafeName(taskId)
                           + "-"
                           + Guid.NewGuid().ToString("N")[..6];
        var artifactDirectory = Path.GetFullPath(Path.Combine(_artifactRoot, tournamentId));
        EnsureChildPath(_artifactRoot, artifactDirectory);
        Directory.CreateDirectory(artifactDirectory);
        var startedAt = DateTimeOffset.Now;

        var sessions = new List<(TournamentCandidateSpec Spec, GitWorktreeSession Session)>();
        try
        {
            foreach (var spec in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var session = await _worktrees.CreateAsync(
                    source,
                    $"tournament-{SafeName(spec.Id)}",
                    cancellationToken);
                if (!session.Created)
                {
                    throw new InvalidOperationException(
                        $"候选 {spec.Id} 的隔离 Worktree 创建失败：{session.Detail}");
                }
                if (!session.Head.Equals(baseHead, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"候选 {spec.Id} 没有基于同一个提交，Tournament 已停止。");
                }
                sessions.Add((spec, session));
            }

            var candidateTasks = sessions.Select(item => RunOneCandidateAsync(
                item.Spec,
                item.Session,
                artifactDirectory,
                runCandidate,
                runVerification,
                cancellationToken));
            var results = await Task.WhenAll(candidateTasks);
            var tournament = new WorktreeTournamentResult(
                tournamentId,
                taskId,
                source,
                baseHead,
                results,
                artifactDirectory,
                startedAt,
                DateTimeOffset.Now);
            await PersistManifestAsync(tournament, cancellationToken);
            return tournament;
        }
        catch
        {
            foreach (var item in sessions)
            {
                await TryRecycleAsync(item.Session.WorkspaceRoot);
            }
            throw;
        }
    }

    public async Task<TournamentPatchApplyResult> ApplyWinnerAsync(
        WorktreeTournamentResult tournament,
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        var winner = tournament.Candidates.FirstOrDefault(item =>
            item.Spec.Id.Equals(candidateId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Tournament winner does not exist.");
        if (!winner.IsEligible)
        {
            throw new InvalidOperationException("Selected Tournament candidate is not eligible for merge.");
        }

        var source = Path.GetFullPath(tournament.SourceRepository);
        var snapshot = await _engineering.InspectAsync(source, cancellationToken);
        if (!snapshot.IsGitRepository || snapshot.ChangedFiles.Count > 0)
        {
            return new TournamentPatchApplyResult(
                false,
                -1,
                "主工作区在竞赛期间发生了变化。为避免覆盖用户修改，Winner Patch 未应用。");
        }
        var headResult = await RunGitAsync(
            source,
            ["rev-parse", "HEAD"],
            TimeSpan.FromSeconds(10),
            cancellationToken);
        if (!headResult.Started
            || headResult.ExitCode != 0
            || !string.Equals(
                FirstLine(headResult.Output),
                tournament.BaseHead,
                StringComparison.OrdinalIgnoreCase))
        {
            return new TournamentPatchApplyResult(
                false,
                headResult.ExitCode,
                "主工作区 HEAD 已改变。Winner Patch 未应用。");
        }

        var actualHash = Convert.ToHexString(
            SHA256.HashData(await File.ReadAllBytesAsync(winner.PatchPath, cancellationToken)))
            .ToLowerInvariant();
        if (!actualHash.Equals(winner.PatchSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new TournamentPatchApplyResult(
                false,
                -1,
                "Winner Patch 哈希校验失败，拒绝应用可能被篡改的候选结果。");
        }

        var check = await RunGitAsync(
            source,
            ["apply", "--check", "--binary", winner.PatchPath],
            TimeSpan.FromSeconds(30),
            cancellationToken);
        if (!check.Started || check.ExitCode != 0)
        {
            return new TournamentPatchApplyResult(
                false,
                check.ExitCode,
                FirstLine(check.Error) ?? "Winner Patch 与当前工作区不兼容。");
        }
        var apply = await RunGitAsync(
            source,
            ["apply", "--binary", "--whitespace=nowarn", winner.PatchPath],
            TimeSpan.FromMinutes(1),
            cancellationToken);
        return new TournamentPatchApplyResult(
            apply.Started && apply.ExitCode == 0,
            apply.ExitCode,
            apply.Started && apply.ExitCode == 0
                ? $"候选 {winner.Spec.Id} 的 Patch 已应用到主工作区，但尚未提交。"
                : FirstLine(apply.Error) ?? "Winner Patch 应用失败。");
    }

    public async Task CleanupAsync(
        WorktreeTournamentResult tournament,
        CancellationToken cancellationToken = default)
    {
        foreach (var candidate in tournament.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await TryDiscardAsync(candidate.Session.WorkspaceRoot);
        }
    }

    public async Task<string> PersistDecisionAsync(
        WorktreeTournamentResult tournament,
        TournamentCouncilDecision decision,
        bool applied,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(tournament.ArtifactDirectory, "decision.json");
        var winner = tournament.Candidates.FirstOrDefault(item =>
            item.Spec.Id.Equals(decision.WinnerId, StringComparison.OrdinalIgnoreCase));
        var json = JsonSerializer.Serialize(new
        {
            schema_version = 1,
            tournament_id = tournament.TournamentId,
            decision.Provider,
            decision.Model,
            winner_id = decision.WinnerId,
            decision.Verdict,
            decision.Confidence,
            decision.Summary,
            applied,
            winner_patch_sha256 = winner?.PatchSha256,
            completed_at = decision.CompletedAt,
            raw_response = decision.RawResponse
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(
            path,
            json,
            new UTF8Encoding(false),
            cancellationToken);
        return path;
    }

    private async Task<TournamentCandidateResult> RunOneCandidateAsync(
        TournamentCandidateSpec spec,
        GitWorktreeSession session,
        string artifactDirectory,
        Func<TournamentCandidateSpec, string, CancellationToken, Task<AgentRunResult>> runCandidate,
        bool runVerification,
        CancellationToken cancellationToken)
    {
        AgentRunResult? agentResult = null;
        EngineeringVerificationResult? verification = null;
        EngineeringCodeReviewResult? review = null;
        var status = "FAILED";
        var detail = "Candidate did not run.";
        try
        {
            agentResult = await runCandidate(spec, session.WorkspaceRoot, cancellationToken);
            var snapshot = await _engineering.InspectAsync(session.WorkspaceRoot, cancellationToken);
            if (agentResult.MutatingToolCalls == 0 || snapshot.ChangedFiles.Count == 0)
            {
                detail = "候选没有产生真实文件变更。";
            }
            else
            {
                if (runVerification
                    && snapshot.VerificationCommand is not
                        ("NO VERIFICATION TARGET" or "MANUAL VERIFICATION REQUIRED"))
                {
                    verification = await _engineering.VerifyAsync(
                        session.WorkspaceRoot,
                        cancellationToken);
                }
                review = await _engineering.RunLocalCodeReviewAsync(
                    session.WorkspaceRoot,
                    cancellationToken);
                status = verification is { Passed: false } ? "VERIFY_FAILED" : "READY";
                detail = verification is null
                    ? "候选已生成，当前工程没有自动验证目标。"
                    : verification.Passed
                        ? "候选已通过隔离工程验证。"
                        : $"候选验证失败：exit {verification.ExitCode}";
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            detail = exception.Message;
        }

        var patch = await ExportPatchAsync(session.WorkspaceRoot, cancellationToken);
        var patchBytes = Encoding.UTF8.GetBytes(patch);
        var patchHash = Convert.ToHexString(SHA256.HashData(patchBytes)).ToLowerInvariant();
        var patchPath = Path.Combine(artifactDirectory, $"{SafeName(spec.Id)}.patch");
        await File.WriteAllTextAsync(patchPath, patch, new UTF8Encoding(false), cancellationToken);
        var (additions, deletions) = CountPatchLines(patch);
        if (patch.Length == 0 && status == "READY")
        {
            status = "FAILED";
            detail = "候选报告了修改，但没有可导出的 Patch。";
        }

        return new TournamentCandidateResult(
            spec,
            session,
            agentResult,
            verification,
            review,
            status,
            detail,
            patch,
            patchPath,
            patchHash,
            additions,
            deletions);
    }

    private static async Task<string> ExportPatchAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var addIntent = await RunGitAsync(
            workspaceRoot,
            ["add", "-N", "--", "."],
            TimeSpan.FromSeconds(30),
            cancellationToken);
        if (!addIntent.Started || addIntent.ExitCode != 0)
        {
            throw new InvalidOperationException(
                FirstLine(addIntent.Error) ?? "无法将新文件加入候选 Patch。");
        }
        try
        {
            var diff = await RunGitAsync(
                workspaceRoot,
                ["diff", "--binary", "--full-index", "HEAD", "--"],
                TimeSpan.FromSeconds(45),
                cancellationToken);
            EnsureGitSucceeded(diff, "无法导出候选 Patch。");
            if (diff.Output.Length > 2_000_000)
            {
                throw new InvalidOperationException(
                    "候选 Patch 超过 2 MB 安全上限；请缩小任务范围后重试。");
            }
            return diff.Output;
        }
        finally
        {
            await RunGitAsync(
                workspaceRoot,
                ["reset", "--mixed", "HEAD", "--"],
                TimeSpan.FromSeconds(20),
                CancellationToken.None);
        }
    }

    private static async Task PersistManifestAsync(
        WorktreeTournamentResult tournament,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(tournament.ArtifactDirectory, "tournament.json");
        var json = JsonSerializer.Serialize(new
        {
            schema_version = 1,
            tournament_id = tournament.TournamentId,
            task_id = tournament.TaskId,
            source_repository = tournament.SourceRepository,
            base_head = tournament.BaseHead,
            started_at = tournament.StartedAt,
            completed_at = tournament.CompletedAt,
            candidates = tournament.Candidates.Select(candidate => new
            {
                id = candidate.Spec.Id,
                provider = candidate.Spec.Provider,
                model = candidate.Spec.Model,
                strategy = candidate.Spec.Strategy,
                status = candidate.Status,
                detail = candidate.Detail,
                verification = candidate.Verification is null
                    ? null
                    : new
                    {
                        candidate.Verification.Passed,
                        candidate.Verification.Command,
                        candidate.Verification.ExitCode,
                        duration_ms = candidate.Verification.Duration.TotalMilliseconds
                    },
                review_score = candidate.Review?.Score,
                patch_file = Path.GetFileName(candidate.PatchPath),
                patch_sha256 = candidate.PatchSha256,
                candidate.Additions,
                candidate.Deletions
            })
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(
            manifestPath,
            json,
            new UTF8Encoding(false),
            cancellationToken);
    }

    private async Task TryRecycleAsync(string workspaceRoot)
    {
        try
        {
            await _worktrees.RecycleAsync(workspaceRoot, CancellationToken.None);
        }
        catch
        {
            // Candidate patches and the manifest are already persisted. Cleanup can be retried manually.
        }
    }

    private async Task TryDiscardAsync(string workspaceRoot)
    {
        try
        {
            await _worktrees.DiscardAsync(workspaceRoot, CancellationToken.None);
        }
        catch
        {
            // Cleanup is best effort; managed leftovers remain visible to the Engineering Center.
        }
    }

    private static (int Additions, int Deletions) CountPatchLines(string patch)
    {
        var additions = 0;
        var deletions = 0;
        foreach (var line in patch.Split('\n'))
        {
            if (line.StartsWith("+++", StringComparison.Ordinal)
                || line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }
            if (line.StartsWith('+'))
            {
                additions++;
            }
            else if (line.StartsWith('-'))
            {
                deletions++;
            }
        }
        return (additions, deletions);
    }

    private static void EnsureChildPath(string root, string target)
    {
        var rootPrefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(target).StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Tournament artifact path escaped its managed root.");
        }
    }

    private static string SafeName(string value)
    {
        var safe = string.Concat(value
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-'))
            .Trim('-');
        return string.IsNullOrWhiteSpace(safe)
            ? "candidate"
            : safe[..Math.Min(40, safe.Length)];
    }

    private static string? FirstLine(string value)
        => value.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

    private static void EnsureGitSucceeded(GitResult result, string fallback)
    {
        if (!result.Started || result.ExitCode != 0)
        {
            throw new InvalidOperationException(FirstLine(result.Error) ?? fallback);
        }
    }

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
                return new GitResult(false, -1, string.Empty, "Git process did not start.");
            }
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return new GitResult(true, -1, await outputTask, "Git operation timed out.");
            }
            return new GitResult(
                true,
                process.ExitCode,
                await outputTask,
                await errorTask);
        }
        catch (Exception exception)
        {
            return new GitResult(false, -1, string.Empty, exception.Message);
        }
    }

    private sealed record GitResult(bool Started, int ExitCode, string Output, string Error);
}
