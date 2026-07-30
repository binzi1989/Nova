using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NovaDesktop.Services;

namespace NovaDesktop;

public partial class EngineeringCenterWindow : Window
{
    private readonly EngineeringWorkspaceService _engineering;
    private readonly Action<string>? _activateWorkspace;
    private string _workspaceRoot;
    private CancellationTokenSource? _operationCancellation;
    private EngineeringWorkspaceSnapshot? _lastSnapshot;

    public EngineeringCenterWindow(
        EngineeringWorkspaceService engineering,
        string workspaceRoot,
        Action<string>? activateWorkspace = null)
    {
        InitializeComponent();
        _engineering = engineering;
        _workspaceRoot = workspaceRoot;
        _activateWorkspace = activateWorkspace;
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _operationCancellation?.Cancel();
    }

    private async Task RefreshAsync()
    {
        BeginOperation("正在收集工程证据…");
        try
        {
            var snapshot = await _engineering.InspectAsync(
                _workspaceRoot,
                _operationCancellation!.Token);
            _lastSnapshot = snapshot;
            RecycleWorktreeButton.IsEnabled = _engineering.IsManagedWorktree(_workspaceRoot);
            WorkspaceLabel.Text = $"{snapshot.WorkspaceName}  ·  {snapshot.WorkspaceRoot}";
            BranchLabel.Text = snapshot.GitBranch;
            ProjectCountLabel.Text = snapshot.Projects.Count.ToString();
            AdditionsLabel.Text = $"+{snapshot.Additions:N0}";
            DeletionsLabel.Text = $"-{snapshot.Deletions:N0}";
            ChangedCountLabel.Text = $"{snapshot.ChangedFiles.Count} FILES";
            ChangedFilesList.ItemsSource = snapshot.ChangedFiles;
            DiffBox.Text = snapshot.Diff;
            DiffBadge.Text = snapshot.IsGitRepository ? "GIT" : "NO GIT";
            VerificationCommandLabel.Text = snapshot.VerificationCommand;
            HealthLabel.Text =
                $"{snapshot.HealthStatus}{Environment.NewLine}"
                + $"证据快照：{snapshot.CapturedAt:yyyy-MM-dd HH:mm:ss}";
            RefreshEvidence();
            var hunks = await _engineering.GetUnstagedHunksAsync(
                _workspaceRoot,
                _operationCancellation.Token);
            HunkList.ItemsSource = hunks;
            HunkCountLabel.Text = $"{hunks.Count} HUNKS";

            CodexStatus.Text = snapshot.Codex.Status;
            CodexDetail.Text = snapshot.Codex.Detail;
            CodexPath.Text = snapshot.Codex.ExecutablePath ?? "NOVA_CODEX_PATH 未配置";
            CodexBadge.Text = snapshot.Codex.Availability switch
            {
                CodexRuntimeAvailability.Ready => "READY",
                CodexRuntimeAvailability.Blocked => "BLOCKED",
                CodexRuntimeAvailability.Detected => "DETECTED",
                _ => "OPTIONAL"
            };
            CodexBadge.Foreground = snapshot.Codex.Availability switch
            {
                CodexRuntimeAvailability.Ready => BrushFrom("#6BE5A9"),
                CodexRuntimeAvailability.Blocked => BrushFrom("#FFC470"),
                _ => BrushFrom("#8490A6")
            };
        }
        catch (OperationCanceledException)
        {
            // Window closed or another operation replaced this one.
        }
        catch (Exception exception)
        {
            HealthLabel.Text = exception.Message;
            DiffBox.Text = "工程证据读取失败。" + Environment.NewLine + exception;
        }
        finally
        {
            EndOperation();
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
        => await RefreshAsync();

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        var confirmation = MessageBox.Show(
            this,
            $"即将运行：{VerificationCommandLabel.Text}{Environment.NewLine}{Environment.NewLine}"
            + ".NET 构建和测试可能执行项目中定义的 MSBuild Target。仅在信任当前工作区时继续。",
            "NOVA 工程验证授权",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        BeginOperation("正在运行验证…");
        VerificationBadge.Text = "RUNNING";
        VerificationBadge.Foreground = BrushFrom("#75F0FF");
        VerificationOutputBox.Text = "验证进程已启动，正在保留标准输出、错误输出与退出码…";
        try
        {
            var result = await _engineering.VerifyAsync(
                _workspaceRoot,
                _operationCancellation!.Token);
            VerificationBadge.Text = !result.Started
                ? "UNAVAILABLE"
                : result.Passed ? "PASSED" : "FAILED";
            VerificationBadge.Foreground = result.Passed
                ? BrushFrom("#6BE5A9")
                : BrushFrom("#FF8B9E");
            VerificationCommandLabel.Text = result.Command;
            VerificationOutputBox.Text =
                $"COMMAND  {result.Command}{Environment.NewLine}"
                + $"EXIT     {result.ExitCode}{Environment.NewLine}"
                + $"DURATION {result.Duration.TotalSeconds:F1}s{Environment.NewLine}"
                + $"FINISHED {result.CompletedAt:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}"
                + Environment.NewLine
                + result.Output;
            RefreshEvidence();
        }
        catch (OperationCanceledException)
        {
            VerificationBadge.Text = "CANCELLED";
        }
        catch (Exception exception)
        {
            VerificationBadge.Text = "ERROR";
            VerificationBadge.Foreground = BrushFrom("#FF8B9E");
            VerificationOutputBox.Text = exception.ToString();
        }
        finally
        {
            EndOperation();
        }
    }

    private async void LocalReview_Click(object sender, RoutedEventArgs e)
    {
        BeginOperation("正在执行本地代码审查…");
        try
        {
            var review = await _engineering.RunLocalCodeReviewAsync(
                _workspaceRoot,
                _operationCancellation!.Token);
            VerificationOutputBox.Text = EngineeringCodeReviewService.Format(review);
            VerificationBadge.Text = $"REVIEW {review.Score}";
            VerificationBadge.Foreground = review.Score >= 85
                ? BrushFrom("#6BE5A9")
                : review.Score >= 60
                    ? BrushFrom("#FFC470")
                    : BrushFrom("#FF8B9E");
            RefreshEvidence();
        }
        catch (Exception exception)
        {
            VerificationOutputBox.Text = exception.ToString();
        }
        finally
        {
            EndOperation();
        }
    }

    private async void CodexReview_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                this,
                "NOVA 将启动已验证的独立 Codex CLI，并强制使用 read-only sandbox 审查当前 Git 变更。"
                + Environment.NewLine
                + Environment.NewLine
                + "该操作可能使用 Codex 账户和网络，但不会授予文件写入权限。是否继续？",
                "启动 Codex 只读审查",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information) != MessageBoxResult.OK)
        {
            return;
        }

        BeginOperation("正在运行 Codex 只读审查…");
        try
        {
            var result = await _engineering.RunCodexReadOnlyReviewAsync(
                _workspaceRoot,
                _operationCancellation!.Token);
            VerificationOutputBox.Text = result.Succeeded
                ? result.Review
                : $"CODEX REVIEW FAILED{Environment.NewLine}{Environment.NewLine}{result.Detail}";
            VerificationBadge.Text = result.Succeeded ? "CODEX REVIEW" : "CODEX FAILED";
            VerificationBadge.Foreground = result.Succeeded
                ? BrushFrom("#6BE5A9")
                : BrushFrom("#FF8B9E");
            RefreshEvidence();
        }
        catch (Exception exception)
        {
            VerificationOutputBox.Text = exception.ToString();
        }
        finally
        {
            EndOperation();
        }
    }

    private async void CreateWorktree_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSnapshot?.IsGitRepository != true)
        {
            MessageBox.Show(
                this,
                "当前工作区不是可用的 Git 仓库，无法创建隔离 Worktree。",
                "NOVA Worktree",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            "NOVA 将从当前仓库已经提交的 HEAD 创建独立 Worktree。"
            + Environment.NewLine
            + Environment.NewLine
            + "主工作区尚未提交的修改不会复制过去；Git 将更新仓库的 Worktree 元数据。是否继续？",
            "创建隔离工程会话",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        BeginOperation("正在创建隔离 Worktree…");
        try
        {
            var session = await _engineering.CreateIsolatedWorktreeAsync(
                _workspaceRoot,
                "nova-session",
                _operationCancellation!.Token);
            if (!session.Created)
            {
                MessageBox.Show(
                    this,
                    session.Detail,
                    "Worktree 创建失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            _workspaceRoot = session.WorkspaceRoot;
            _activateWorkspace?.Invoke(session.WorkspaceRoot);
            VerificationOutputBox.Text =
                $"ISOLATED WORKTREE CREATED{Environment.NewLine}"
                + $"HEAD    {session.Head}{Environment.NewLine}"
                + $"PATH    {session.WorkspaceRoot}{Environment.NewLine}"
                + $"SESSION {session.SessionId}{Environment.NewLine}{Environment.NewLine}"
                + session.Detail;
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            // Window closed or operation cancelled.
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Worktree 创建失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            EndOperation();
        }
    }

    private async void ProbeCodex_Click(object sender, RoutedEventArgs e)
    {
        CodexProbeButton.IsEnabled = false;
        CodexBadge.Text = "PROBING";
        try
        {
            var probe = await _engineering.ProbeCodexExecutableAsync(
                _operationCancellation?.Token ?? CancellationToken.None);
            CodexStatus.Text = probe.Status;
            CodexDetail.Text = probe.Detail;
            CodexPath.Text = probe.ExecutablePath ?? "NOVA_CODEX_PATH 未配置";
            CodexBadge.Text = probe.Availability switch
            {
                CodexRuntimeAvailability.Ready => "READY",
                CodexRuntimeAvailability.Blocked => "BLOCKED",
                CodexRuntimeAvailability.Detected => "DETECTED",
                _ => "OPTIONAL"
            };
            CodexBadge.Foreground = probe.Availability == CodexRuntimeAvailability.Ready
                ? BrushFrom("#6BE5A9")
                : probe.Availability == CodexRuntimeAvailability.Blocked
                    ? BrushFrom("#FFC470")
                    : BrushFrom("#8490A6");
        }
        catch (Exception exception)
        {
            CodexBadge.Text = "ERROR";
            CodexDetail.Text = exception.Message;
        }
        finally
        {
            CodexProbeButton.IsEnabled = true;
        }
    }

    private async void RecycleWorktree_Click(object sender, RoutedEventArgs e)
    {
        if (!_engineering.IsManagedWorktree(_workspaceRoot))
        {
            return;
        }

        if (MessageBox.Show(
                this,
                "NOVA 将回收当前隔离 Worktree。"
                + Environment.NewLine
                + Environment.NewLine
                + "若存在未提交变更，将先保存 binary Patch、Git 状态和未跟踪文件副本；随后 Git 会强制移除 Worktree。是否继续？",
                "安全回收隔离 Worktree",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        BeginOperation("正在创建恢复包并回收 Worktree…");
        try
        {
            var result = await _engineering.RecycleWorktreeAsync(
                _workspaceRoot,
                _operationCancellation!.Token);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(result.Detail);
            }

            _workspaceRoot = result.SourceRepository;
            _activateWorkspace?.Invoke(result.SourceRepository);
            VerificationOutputBox.Text =
                $"WORKTREE RECYCLED{Environment.NewLine}"
                + $"REMOVED  {result.RemovedWorkspace}{Environment.NewLine}"
                + $"RECOVERY {result.RecoveryPath ?? "NOT REQUIRED"}{Environment.NewLine}{Environment.NewLine}"
                + result.Detail;
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Worktree 回收失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            EndOperation();
        }
    }

    private async void StageHunks_Click(object sender, RoutedEventArgs e)
    {
        var selected = HunkList.SelectedItems
            .OfType<EngineeringDiffHunk>()
            .Select(hunk => hunk.Id)
            .ToArray();
        if (selected.Length == 0
            || MessageBox.Show(
                this,
                $"把选中的 {selected.Length} 个 Hunk 加入 Git 暂存区？这不会创建提交。",
                "暂存所选 Hunk",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }
        await ApplyHunkActionAsync(selected, revert: false);
    }

    private async void RevertHunks_Click(object sender, RoutedEventArgs e)
    {
        var selected = HunkList.SelectedItems
            .OfType<EngineeringDiffHunk>()
            .Select(hunk => hunk.Id)
            .ToArray();
        if (selected.Length == 0
            || MessageBox.Show(
                this,
                $"撤销选中的 {selected.Length} 个未暂存 Hunk？文件内容将恢复，操作结果会进入证据账本。",
                "撤销所选 Hunk",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }
        await ApplyHunkActionAsync(selected, revert: true);
    }

    private async Task ApplyHunkActionAsync(IReadOnlyCollection<string> hunkIds, bool revert)
    {
        BeginOperation(revert ? "正在撤销所选 Hunk…" : "正在暂存所选 Hunk…");
        try
        {
            var result = revert
                ? await _engineering.RevertHunksAsync(
                    _workspaceRoot,
                    hunkIds,
                    _operationCancellation!.Token)
                : await _engineering.StageHunksAsync(
                    _workspaceRoot,
                    hunkIds,
                    _operationCancellation!.Token);
            VerificationOutputBox.Text =
                $"{result.Action.ToUpperInvariant()}{Environment.NewLine}"
                + $"EXIT  {result.ExitCode}{Environment.NewLine}"
                + $"HUNKS {result.HunkCount}{Environment.NewLine}{Environment.NewLine}"
                + result.Detail;
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Hunk 操作失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            EndOperation();
        }
    }

    private void CopyDiff_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(DiffBox.Text))
        {
            Clipboard.SetText(DiffBox.Text);
            DiffBadge.Text = "COPIED";
        }
    }

    private void RefreshEvidence()
    {
        var evidence = _engineering.ReadRecentEvidence(_workspaceRoot, 80);
        EvidenceList.ItemsSource = evidence;
        EvidenceCountLabel.Text = $"{evidence.Count} EVENTS";
    }

    private void BeginOperation(string status)
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        RefreshButton.IsEnabled = false;
        VerifyButton.IsEnabled = false;
        CodexProbeButton.IsEnabled = false;
        WorktreeButton.IsEnabled = false;
        RecycleWorktreeButton.IsEnabled = false;
        LocalReviewButton.IsEnabled = false;
        CodexReviewButton.IsEnabled = false;
        WorkspaceLabel.Text = status;
    }

    private void EndOperation()
    {
        RefreshButton.IsEnabled = true;
        VerifyButton.IsEnabled = true;
        CodexProbeButton.IsEnabled = true;
        WorktreeButton.IsEnabled = true;
        RecycleWorktreeButton.IsEnabled = _engineering.IsManagedWorktree(_workspaceRoot);
        LocalReviewButton.IsEnabled = true;
        CodexReviewButton.IsEnabled = true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
        => Close();

    private static SolidColorBrush BrushFrom(string color)
        => new((Color)ColorConverter.ConvertFromString(color));
}
