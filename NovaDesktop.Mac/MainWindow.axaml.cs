using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Nova.Core;
using NovaDesktop.Models;
using NovaDesktop.Services;

namespace NovaDesktop.Mac;

public sealed record MacChatTurn(
    string Role,
    string Speaker,
    string Content,
    string Time,
    IBrush Accent,
    IBrush Border);

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly WorkspaceContextService _workspaceService = new();
    private readonly CrossPlatformMcpProbe _mcpProbe = new();
    private readonly ProviderChatService _chatService = new();
    private readonly ParallelChatService _parallelChatService;
    private readonly MacAgentOsHost _agentOs = new();
    private WorkspaceContext _workspace;
    private bool _isBusy;
    private string _runtimeStatus = "模型待连接";
    private IBrush _runtimeDot = Brush.Parse("#69748A");
    private string _composerHint = "真实请求 · API Key 仅保存在当前进程";
    private string _agentOsStatus = "AGENTOS STARTING";

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _parallelChatService = new ParallelChatService(_chatService);
        _workspace = _workspaceService.Analyze(Environment.CurrentDirectory);
        DataContext = this;
        Opened += async (_, _) => await InitializeAgentOsAsync();
        Closed += (_, _) => _agentOs.Dispose();
    }

    private ComboBox ProviderBox => this.FindControl<ComboBox>("ProviderBox")
        ?? throw new InvalidOperationException("ProviderBox was not loaded.");
    private TextBox ModelBox => this.FindControl<TextBox>("ModelBox")
        ?? throw new InvalidOperationException("ModelBox was not loaded.");
    private TextBox ApiKeyBox => this.FindControl<TextBox>("ApiKeyBox")
        ?? throw new InvalidOperationException("ApiKeyBox was not loaded.");
    private ComboBox ModeBox => this.FindControl<ComboBox>("ModeBox")
        ?? throw new InvalidOperationException("ModeBox was not loaded.");
    private TextBox PromptBox => this.FindControl<TextBox>("PromptBox")
        ?? throw new InvalidOperationException("PromptBox was not loaded.");
    private Button SendButton => this.FindControl<Button>("SendButton")
        ?? throw new InvalidOperationException("SendButton was not loaded.");

    public ObservableCollection<MacChatTurn> Turns { get; } = [];
    public ObservableCollection<string> McpLocations { get; } = [];
    public ObservableCollection<TaskItem> Tasks { get; } = [];

    public string WorkspaceRoot => _workspace.Root;
    public string WorkspaceStatus => _workspace.Name;
    public string WorkspaceDetail
        => $"{_workspace.Technology} · 约 {_workspace.FileCount} 个文件 · 本地只读上下文";
    public string WorkspaceTechnology => _workspace.Technology;
    public string WorkspaceFileCount => $"已识别约 {_workspace.FileCount} 个文件";
    public bool IsConversationEmpty => Turns.Count == 0;
    public string TaskCountLabel => $"{Tasks.Count} 个任务";

    public string RuntimeStatus
    {
        get => _runtimeStatus;
        private set => SetField(ref _runtimeStatus, value);
    }

    public IBrush RuntimeDot
    {
        get => _runtimeDot;
        private set => SetField(ref _runtimeDot, value);
    }

    public string ComposerHint
    {
        get => _composerHint;
        private set => SetField(ref _composerHint, value);
    }

    public string AgentOsStatus
    {
        get => _agentOsStatus;
        private set => SetField(ref _agentOsStatus, value);
    }

    private async void ChooseWorkspace_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 NOVA 任务根目录",
            AllowMultiple = false
        });
        var localPath = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return;
        }

        _workspace = _workspaceService.Analyze(localPath);
        RaiseWorkspaceProperties();
        McpLocations.Clear();
        ComposerHint = "工作区已更新 · 描述你想得到的真实结果";
    }

    private void Provider_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        ModelBox.Text = ProviderBox.SelectedIndex switch
        {
            1 => "deepseek-chat",
            2 => "kimi-k3",
            _ => "gpt-5.6"
        };
    }

    private void Mode_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        ComposerHint = ModeBox.SelectedIndex == 1
            ? "Autopilot · 3 路并行 + 1 次汇总 · 当前只读"
            : "Ask · 单模型请求 · 支持连续多轮对话";
    }

    private void UseStarterGoal_Click(object? sender, RoutedEventArgs e)
    {
        PromptBox.Text =
            "审查当前项目的结构和用户体验，先给出基于现有工程信号的判断；"
            + "不要声称修改文件，并列出下一步最值得验证的三项工作。";
        PromptBox.Focus();
    }

    private async void Send_Click(object? sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }
        var prompt = PromptBox.Text?.Trim() ?? string.Empty;
        if (prompt.Length == 0)
        {
            ComposerHint = "请先描述一个目标";
            return;
        }

        var provider = ProviderBox.SelectedIndex switch
        {
            1 => "deepseek",
            2 => "kimi",
            _ => "openai"
        };
        var model = ModelBox.Text?.Trim() ?? string.Empty;
        var apiKey = ApiKeyBox.Text?.Trim() ?? string.Empty;
        if (model.Length == 0 || apiKey.Length == 0)
        {
            ComposerHint = "请先选择模型并输入 API Key";
            ApiKeyBox.Focus();
            return;
        }

        var autopilot = ModeBox.SelectedIndex == 1;
        var task = new TaskItem
        {
            Id = "mac-" + Guid.NewGuid().ToString("N")[..12],
            Title = CreateTaskTitle(prompt),
            Description = prompt,
            WorkspaceRoot = _workspace.Root,
            Provider = provider,
            Model = model,
            ExecutionMode = autopilot
                ? AgentExecutionMode.Autopilot
                : AgentExecutionMode.Ask,
            State = TaskState.Queued,
            Stage = "等待 AgentOS 接管",
            Progress = 0
        };
        Tasks.Insert(0, task);
        OnPropertyChanged(nameof(TaskCountLabel));

        Turns.Add(CreateTurn("user", "YOU", prompt));
        PromptBox.Clear();
        NotifyConversationChanged();
        var messages = Turns
            .Where(turn => turn.Role is "user" or "assistant")
            .Select(turn => new AgentMessage(turn.Role, turn.Content))
            .ToArray();
        SetBusy(
            true,
            autopilot
                ? $"{provider} · 3 个子 Agent 并行中"
                : $"{provider} · {model} 请求中");
        try
        {
            await _agentOs.BeginTaskAsync(task);
            AgentOsStatus = _agentOs.Status;
            await _agentOs.ObserveAsync(
                task,
                new AgentRuntimeEvent(
                    autopilot
                        ? AgentRuntimeEventKind.BatchStarted
                        : AgentRuntimeEventKind.Thinking,
                    "NOVA Mac",
                    autopilot ? "并行任务已调度" : "模型推理",
                    autopilot ? "3 个只读子 Agent + 1 次指挥官汇总" : $"{provider} · {model}",
                    12,
                    autopilot ? 3 : 1)
                {
                    ModelRoundCost = autopilot ? 4 : 1
                });

            var request = new AgentChatRequest(
                provider,
                model,
                apiKey,
                messages,
                _workspace);
            AgentChatResult result;
            if (autopilot)
            {
                var tasks = CrossPlatformParallelPlanner.Create(prompt);
                Turns.Add(CreateTurn(
                    "system",
                    "SUPERVISOR",
                    $"已自动创建 {tasks.Count} 个只读子 Agent："
                    + string.Join("、", tasks.Select(item => item.Title))
                    + "。将产生 3 次并行分析和 1 次指挥官汇总。"));
                NotifyConversationChanged();
                var parallel = await _parallelChatService.RunAsync(
                    request,
                    tasks,
                    CancellationToken.None);
                result = parallel.Commander;
                await _agentOs.ObserveAsync(
                    task,
                    new AgentRuntimeEvent(
                        AgentRuntimeEventKind.BatchCompleted,
                        "Agent Supervisor",
                        "并行工作组完成",
                        $"{parallel.Workers.Count}/{tasks.Count} 个子 Agent 已汇总",
                        82,
                        1));
                Turns.Add(CreateTurn(
                    "system",
                    "SUPERVISOR",
                    $"并行工作组完成 · {parallel.Workers.Count}/{tasks.Count} · "
                    + $"{parallel.Duration.TotalSeconds:0.0}s"));
                ComposerHint = "Autopilot 完成 · 子 Agent 已汇总 · 可继续追问";
            }
            else
            {
                result = await _chatService.SendAsync(request, CancellationToken.None);
                ComposerHint = $"完成 · {result.Duration.TotalSeconds:0.0}s · 可继续追问";
            }

            await _agentOs.ObserveAsync(
                task,
                new AgentRuntimeEvent(
                    AgentRuntimeEventKind.TextDelta,
                    "NOVA",
                    "生成最终回答",
                    result.Text,
                    94,
                    1));
            Turns.Add(CreateTurn("assistant", "NOVA", result.Text));
            task.Draft = result.Text;
            await _agentOs.CompleteTaskAsync(
                task,
                succeeded: true,
                "回答已持久化，可从任务空间恢复",
                result.Text.Length);
            await _agentOs.ReportRuntimeAsync(provider, model, ready: true);
            AgentOsStatus = _agentOs.Status;
            RuntimeStatus = autopilot
                ? $"{result.Provider} · {result.Model} · 3 Agent"
                : $"{result.Provider} · {result.Model}";
            RuntimeDot = Brush.Parse("#6BE5A9");
        }
        catch (Exception exception)
        {
            if (task.State == TaskState.Running)
            {
                try
                {
                    await _agentOs.CompleteTaskAsync(
                        task,
                        succeeded: false,
                        $"请求未完成：{exception.Message}",
                        0);
                }
                catch
                {
                    task.State = TaskState.Failed;
                    task.Stage = "故障状态已保留";
                }
            }
            try
            {
                await _agentOs.ReportRuntimeAsync(provider, model, ready: false);
                AgentOsStatus = _agentOs.Status;
            }
            catch
            {
                AgentOsStatus = "AGENTOS DEGRADED";
            }
            Turns.Add(CreateTurn(
                "system",
                "SYSTEM",
                $"请求未完成：{exception.Message}"));
            ComposerHint = "请求失败 · 检查 Key、网络和模型名称";
            RuntimeStatus = "模型连接失败";
            RuntimeDot = Brush.Parse("#FF7187");
        }
        finally
        {
            NotifyConversationChanged();
            SetBusy(false, RuntimeStatus);
        }
    }

    private void ProbeMcp_Click(object? sender, RoutedEventArgs e)
    {
        McpLocations.Clear();
        var locations = _mcpProbe.GetKnownLocations(_workspace.Root);
        foreach (var location in locations.Where(item => item.Exists))
        {
            McpLocations.Add($"● {location.Product} · {location.Path}");
        }
        if (McpLocations.Count == 0)
        {
            McpLocations.Add("未发现受支持的配置文件");
        }
        ComposerHint = $"只读位置检查完成 · {locations.Count(item => item.Exists)} 个可扫描配置";
    }

    private async Task InitializeAgentOsAsync()
    {
        try
        {
            await _agentOs.EnsureBootedAsync();
            Tasks.Clear();
            foreach (var task in _agentOs.LoadTasks())
            {
                Tasks.Add(task);
            }
            AgentOsStatus = _agentOs.Status;
            OnPropertyChanged(nameof(TaskCountLabel));
        }
        catch (Exception exception)
        {
            AgentOsStatus = "AGENTOS DEGRADED";
            ComposerHint = $"本地任务恢复暂不可用 · {exception.Message}";
        }
    }

    private static MacChatTurn CreateTurn(string role, string speaker, string content)
        => new(
            role,
            speaker,
            content,
            DateTime.Now.ToString("HH:mm"),
            Brush.Parse(role == "assistant" ? "#71EAF8" : role == "user" ? "#B8C2D3" : "#FFC470"),
            Brush.Parse(role == "assistant" ? "#2D5265" : role == "user" ? "#34435B" : "#5B4930"));

    private static string CreateTaskTitle(string prompt)
    {
        var firstLine = prompt
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()
            ?.Trim() ?? "新任务";
        return firstLine.Length <= 30
            ? firstLine
            : firstLine[..30] + "…";
    }

    private void SetBusy(bool value, string status)
    {
        _isBusy = value;
        SendButton.IsEnabled = !value;
        SendButton.Content = value ? "Agent 工作中…" : "发送到模型  →";
        RuntimeStatus = status;
        if (value)
        {
            RuntimeDot = Brush.Parse("#71EAF8");
        }
    }

    private void RaiseWorkspaceProperties()
    {
        OnPropertyChanged(nameof(WorkspaceRoot));
        OnPropertyChanged(nameof(WorkspaceStatus));
        OnPropertyChanged(nameof(WorkspaceDetail));
        OnPropertyChanged(nameof(WorkspaceTechnology));
        OnPropertyChanged(nameof(WorkspaceFileCount));
    }

    private void NotifyConversationChanged()
        => OnPropertyChanged(nameof(IsConversationEmpty));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public new event PropertyChangedEventHandler? PropertyChanged;
}
