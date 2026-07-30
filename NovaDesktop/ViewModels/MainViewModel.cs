using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using NovaDesktop.Infrastructure;
using NovaDesktop.Models;
using NovaDesktop.Services;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace NovaDesktop.ViewModels;

public sealed record GoalSignalDisplayItem(
    string Description,
    string Status,
    string Evidence);

public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly Regex DiagnosticApiKeyPattern = new(
        @"\bsk-[A-Za-z0-9_-]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DiagnosticBearerPattern = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly RelayCommand _submitCommand;
    private readonly RelayCommand _pauseCommand;
    private readonly RelayCommand _cancelCommand;
    private readonly TaskJournalService _journal = new();
    private readonly TaskSnapshotService _snapshots = new();
    private readonly McpRegistryService _mcpRegistry = new();
    private readonly SkillRegistryService _skillRegistry = new();
    private readonly CapabilityCompassService _capabilityCompass;
    private readonly AgentScheduleService _scheduleService = new();
    private readonly ProductivityInsightsService _productivityInsights;
    private readonly KnowledgeGraphService _knowledgeGraph = new();
    private readonly KnowledgeIndexService _knowledgeIndex = new();
    private readonly ArtifactRepositoryService _artifactRepository = new();
    private readonly DeliveryManifestService _deliveryManifest = new();
    private readonly InputAttachmentService _inputAttachments = new();
    private readonly EngineeringWorkspaceService _engineeringWorkspace = new();
    private readonly EngineeringCompletenessService _engineeringCompleteness = new();
    private readonly WorkspaceProfileService _workspaceProfiles = new();
    private readonly ConversationHistoryService _conversationHistory = new();
    private readonly EngineeringCheckpointService _engineeringCheckpoints;
    private readonly TaskOutcomeContractService _outcomeContracts;
    private readonly WorktreeTournamentService _worktreeTournament;
    private readonly AgentMeshService _agentMesh;
    private readonly GoalMissionService _goalMissions = new();
    private readonly GoalOutcomeLedgerService _goalOutcomes = new();
    private readonly GoalRepairLoopService _goalRepairs = new();
    private readonly WorkspaceEvidenceFingerprintService _workspaceEvidence = new();
    private readonly TaskFailureLedgerService _failureLedger = new();
    private readonly AdaptiveContextCompilerService _contextCompiler = new();
    private readonly AgentBenchService _agentBench = new();
    private readonly AdaptiveModelRouterService _modelRouter = new();
    private readonly WindowsCredentialVault _credentialVault = new();
    private readonly AgentOsKernel _agentOsKernel = new();
    private readonly AgentTaskGraphService _agentTaskGraph = new();
    private readonly AgentResourceGovernor _agentResourceGovernor = new();
    private readonly AgentSupervisorService _agentSupervisor = new();
    private readonly TaskApprovalPolicy _taskApprovalPolicy = new();
    private readonly DispatcherTimer _scheduleTimer;
    private readonly IAgentRuntime _openAiRuntime = new OpenAIResponsesAgentRuntime();
    private readonly IAgentRuntime _deepSeekRuntime = new DeepSeekChatAgentRuntime();
    private readonly Dictionary<string, string> _apiKeys;
    private readonly StringBuilder _streamingBuffer = new();
    private readonly System.Diagnostics.Stopwatch _streamingFlushClock =
        System.Diagnostics.Stopwatch.StartNew();
    private CancellationTokenSource? _runCancellation;
    private TaskCompletionSource<bool>? _approvalSource;
    private TaskItem? _selectedTask;
    private string _promptText = string.Empty;
    private string _coreStatus = "READY";
    private string _coreMessage = "你说想做成什么，我来把路走清楚";
    private string _currentStage = "系统就绪";
    private double _overallProgress;
    private int _currentStep;
    private int _activeAgentCount;
    private bool _isRunning;
    private bool _isPaused;
    private bool _isApprovalVisible;
    private string _approvalTitle = "允许访问公开网页？";
    private string _approvalDescription = "研究员需要打开外部网页读取产品资料。此操作仅访问公开内容，不会登录账户或提交数据。";
    private string _approvalPreview = string.Empty;
    private string _approvalStats = string.Empty;
    private bool _isApprovalPreviewVisible;
    private bool _isApprovalTrustVisible;
    private string _approvalAllowLabel = "本次允许";
    private string _approvalRejectLabel = "拒绝";
    private string _approvalTrustLabel = "本轮信任同类操作";
    private string _approvalSafetyNote = "权限只在当前步骤有效。";
    private string _approvalPolicyStatus = "按风险确认 · 低风险可合并";
    private ApprovalScope? _activeApprovalScope;
    private bool _approvalTrustRequested;
    private string _pauseLabel = "暂停";
    private string _runTime = "00:00";
    private string _provider = "openai";
    private string _model = "gpt-5.6";
    private string _runtimeMode = "本地工具已连接";
    private string _runtimeDetail = "模型待配置 · 原生 Windows";
    private string _workspaceRoot = Environment.CurrentDirectory;
    private string _workspaceSummary = "正在识别工程…";
    private string _conversationRoundLabel = "新会话";
    private string _streamingText = string.Empty;
    private ArtifactItem? _selectedArtifact;
    private ArtifactItem? _selectedArtifactVersion;
    private bool _isDeliveryVisible;
    private bool _isDeliveryEvidenceExpanded;
    private bool _isCompletedConversationExpanded;
    private DateTimeOffset _runStartedAt;
    private bool _scheduleTickBusy;
    private Task? _shutdownPreparation;
    private string _scheduleStatus = "0 个计划任务";
    private string _mcpStatus = "0 MCP · 0 SKILLS";
    private AgentExecutionMode _selectedExecutionMode = AgentExecutionMode.Build;
    private string _agentOsStatus = "AGENTOS BOOTING";
    private string _goalMissionTitle = string.Empty;
    private string _goalMissionOutcome = string.Empty;
    private string _goalMissionMeta = string.Empty;
    private string _goalMissionPhase = string.Empty;
    private string _budgetStatusLabel = "预算待机";
    private bool _budgetWarningRaised;
    private string _selectedConversationChoice = string.Empty;
    private bool _showArchivedTasks;

    public MainViewModel()
    {
        _capabilityCompass = new CapabilityCompassService(_mcpRegistry, _skillRegistry);
        _engineeringCheckpoints = new EngineeringCheckpointService(_engineeringWorkspace);
        _outcomeContracts = new TaskOutcomeContractService(_engineeringWorkspace);
        _worktreeTournament = new WorktreeTournamentService(
            engineering: _engineeringWorkspace);
        _agentMesh = new AgentMeshService(
            engineering: _engineeringWorkspace);
        _productivityInsights = new ProductivityInsightsService(
            _snapshots,
            _journal,
            _scheduleService);
        _apiKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"] = LoadProviderKey("openai", "OPENAI_API_KEY"),
            ["deepseek"] = LoadProviderKey("deepseek", "DEEPSEEK_API_KEY"),
            ["kimi"] = LoadProviderKey("kimi", "MOONSHOT_API_KEY")
        };
        var recentWorkspace = _workspaceProfiles.LoadRecent().FirstOrDefault(item => item.Exists);
        if (recentWorkspace is not null)
        {
            _workspaceRoot = recentWorkspace.Root;
        }
        UpdateWorkspaceProfile(remember: false);
        TaskView = CollectionViewSource.GetDefaultView(Tasks);
        TaskView.Filter = item =>
            item is TaskItem task && task.IsArchived == ShowArchivedTasks;
        Tasks.CollectionChanged += (_, _) => RefreshTaskLibrary();

        _submitCommand = new RelayCommand(
            () => _ = StartNewTaskAsync(),
            () => !IsRunning
                  && (!string.IsNullOrWhiteSpace(PromptText)
                      || PendingAttachments.Count > 0));
        _pauseCommand = new RelayCommand(TogglePause, () => IsRunning);
        _cancelCommand = new RelayCommand(CancelRun, () => IsRunning);

        SubmitCommand = _submitCommand;
        PauseCommand = _pauseCommand;
        CancelCommand = _cancelCommand;
        ApproveCommand = new RelayCommand(() => ResolveApproval(true));
        ApproveForRunCommand = new RelayCommand(ResolveApprovalForRun);
        RejectCommand = new RelayCommand(() => ResolveApproval(false));
        NewTaskCommand = new RelayCommand(NewTask);
        ResumeSelectedCommand = new RelayCommand(
            () => _ = ResumeSelectedTaskAsync(),
            () => CanResumeSelected);
        UseSuggestionCommand = new RelayCommand(UseSuggestion);
        ShowDeliveryCommand = new RelayCommand(ShowDelivery, () => HasArtifacts);
        HideDeliveryCommand = new RelayCommand(() => IsDeliveryVisible = false);
        OpenDeliveryWorkspaceCommand = new RelayCommand(
            OpenDeliveryWorkspace,
            () => Directory.Exists(WorkspaceRoot));
        OpenArtifactCommand = new RelayCommand(OpenSelectedArtifact, CanUseSelectedArtifactFile);
        RevealArtifactCommand = new RelayCommand(RevealSelectedArtifact, CanUseSelectedArtifactFile);
        CopyArtifactPathCommand = new RelayCommand(CopySelectedArtifactPath, CanUseSelectedArtifactFile);
        ContinueFromArtifactCommand = new RelayCommand(
            ContinueFromSelectedArtifact,
            () => SelectedArtifactVersion is not null);
        ContinueConversationCommand = new RelayCommand(
            ContinueConversation,
            () => !IsRunning && SelectedTask is not null);
        ToggleCompletedConversationCommand = new RelayCommand(
            ToggleCompletedConversation);
        SelectConversationChoiceCommand = new ParameterRelayCommand<ConversationChoice>(
            SelectConversationChoice);
        ClearConversationChoiceCommand = new RelayCommand(ClearConversationChoice);
        RemoveAttachmentCommand = new ParameterRelayCommand<AgentInputAttachment>(
            RemoveInputAttachment);
        ArchiveTaskCommand = new ParameterRelayCommand<TaskItem>(
            task => _ = SetTaskArchivedAsync(task, true),
            task => !IsRunning && !task.IsArchived);
        RestoreTaskCommand = new ParameterRelayCommand<TaskItem>(
            task => _ = SetTaskArchivedAsync(task, false),
            task => !IsRunning && task.IsArchived);
        ToggleArchivedTasksCommand = new RelayCommand(
            ToggleArchivedTasks,
            () => !IsRunning);

        SeedHistory();
        UpdateRuntimeLabels();
        RefreshExtensionStatus();
        UpdateScheduleStatus();
        _ = InitializeAgentOsAsync();
        _scheduleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _scheduleTimer.Tick += ScheduleTimer_Tick;
        _scheduleTimer.Start();
        Activity.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasActivity));
            OnPropertyChanged(nameof(IsTraceVisible));
        };
        RecoveryStatus = App.HadUncleanShutdown ? "RECOVERED" : "RECOVERY READY";
        if (App.HadUncleanShutdown)
        {
            AddActivity(
                "恢复代理",
                "检测到异常退出",
                $"未完成任务已恢复为暂停状态；崩溃报告目录：{App.CrashDirectory}",
                ActivityKind.System);
        }
    }

    public ObservableCollection<TaskItem> Tasks { get; } = [];
    public ICollectionView TaskView { get; }
    public ObservableCollection<ActivityEntry> Activity { get; } = [];
    public ObservableCollection<ArtifactItem> Artifacts { get; } = [];
    public ObservableCollection<ArtifactItem> DeliveryArtifacts { get; } = [];
    public ObservableCollection<ArtifactItem> DeliveryEvidenceArtifacts { get; } = [];
    public ObservableCollection<ArtifactItem> ArtifactVersions { get; } = [];
    public ObservableCollection<ConversationTurn> ConversationTurns { get; } = [];
    public ObservableCollection<GoalSignalDisplayItem> GoalSignals { get; } = [];
    public ObservableCollection<AgentInputAttachment> PendingAttachments { get; } = [];
    public IReadOnlyList<AgentExecutionMode> ExecutionModes { get; } =
        Enum.GetValues<AgentExecutionMode>();

    public RelayCommand SubmitCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ApproveCommand { get; }
    public RelayCommand ApproveForRunCommand { get; }
    public RelayCommand RejectCommand { get; }
    public RelayCommand NewTaskCommand { get; }
    public RelayCommand ResumeSelectedCommand { get; }
    public RelayCommand UseSuggestionCommand { get; }
    public RelayCommand ShowDeliveryCommand { get; }
    public RelayCommand HideDeliveryCommand { get; }
    public RelayCommand OpenDeliveryWorkspaceCommand { get; }
    public RelayCommand OpenArtifactCommand { get; }
    public RelayCommand RevealArtifactCommand { get; }
    public RelayCommand CopyArtifactPathCommand { get; }
    public RelayCommand ContinueFromArtifactCommand { get; }
    public RelayCommand ContinueConversationCommand { get; }
    public RelayCommand ToggleCompletedConversationCommand { get; }
    public ParameterRelayCommand<ConversationChoice> SelectConversationChoiceCommand { get; }
    public RelayCommand ClearConversationChoiceCommand { get; }
    public ParameterRelayCommand<AgentInputAttachment> RemoveAttachmentCommand { get; }
    public ParameterRelayCommand<TaskItem> ArchiveTaskCommand { get; }
    public ParameterRelayCommand<TaskItem> RestoreTaskCommand { get; }
    public RelayCommand ToggleArchivedTasksCommand { get; }

    public bool ShowArchivedTasks
    {
        get => _showArchivedTasks;
        private set
        {
            if (!SetField(ref _showArchivedTasks, value))
            {
                return;
            }
            RefreshTaskLibrary();
        }
    }

    public int VisibleTaskCount
        => Tasks.Count(task => task.IsArchived == ShowArchivedTasks);

    public int ArchivedTaskCount
        => Tasks.Count(task => task.IsArchived);

    public bool HasVisibleTasks
        => VisibleTaskCount > 0;

    public string TaskLibraryTitle
        => ShowArchivedTasks ? "已归档" : "任务空间";

    public string TaskArchiveToggleLabel
        => ShowArchivedTasks
            ? "返回任务"
            : ArchivedTaskCount > 0
                ? $"归档 {ArchivedTaskCount}"
                : "归档";

    public string TaskSpaceEmptyTitle
        => ShowArchivedTasks ? "还没有归档任务" : "从一个真实目标开始";

    public string TaskSpaceEmptyDetail
        => ShowArchivedTasks
            ? "完成或暂时不需要的任务可以归档到这里，之后仍可恢复。"
            : "点击“新建任务”，描述你希望真正完成的结果。";

    public bool HasPendingAttachments => PendingAttachments.Count > 0;

    public string AttachmentSummary
    {
        get
        {
            if (PendingAttachments.Count == 0)
            {
                return "添加图片或文件 · 也可以拖到输入框";
            }
            var totalBytes = PendingAttachments.Sum(item => item.SizeBytes);
            var size = totalBytes >= 1024 * 1024
                ? $"{totalBytes / 1024d / 1024d:0.0} MB"
                : $"{Math.Max(1, totalBytes / 1024d):0} KB";
            return $"{PendingAttachments.Count} 个附件 · {size}";
        }
    }

    public AgentExecutionMode SelectedExecutionMode
    {
        get => _selectedExecutionMode;
        set
        {
            if (IsRunning || !SetField(ref _selectedExecutionMode, value))
            {
                return;
            }
            OnPropertyChanged(nameof(ExecutionModeDetail));
            OnPropertyChanged(nameof(EmptyStateTitle));
            OnPropertyChanged(nameof(EmptyStateDetail));
            OnPropertyChanged(nameof(SuggestionLabel));
            RefreshExecutionReadiness();
            _ = _agentOsKernel.SetExecutionModeAsync(value);
            RefreshAgentOsStatus();
        }
    }

    public string ExecutionModeDetail
    {
        get
        {
            var capability = SelectedExecutionMode switch
            {
                AgentExecutionMode.Ask => "只读证据回答",
                AgentExecutionMode.Plan => "只读规划与风险",
                AgentExecutionMode.Build => "审批后修改并验证",
                AgentExecutionMode.Autopilot => "自动拆分 · 并行闭环",
                _ => "自主探索 · 以可验证结果为终点"
            };
            return capability;
        }
    }

    public string ShellStatus
        => IsLiveConfigured
            ? $"{GetProviderLabel()} 已连接 · 本地执行"
            : "需要连接模型";

    public string HumanStatusLabel
        => IsApprovalVisible
            ? "需要你确认"
            : IsPaused
                ? "已安全暂停"
                : IsRunning
                    ? "NOVA 正在处理"
                    : SelectedTask?.State switch
                    {
                        TaskState.Completed => "已经交付",
                        TaskState.Failed => "需要处理",
                        TaskState.Stale => "证据需要更新",
                        TaskState.BudgetExhausted => "已停在安全点",
                        TaskState.Paused => "可以继续",
                        TaskState.Cancelled => "已经停止",
                        _ => IsLiveConfigured ? "可以开始" : "还差一步"
                    };

    public string HumanGuidanceTitle
        => IsApprovalVisible
            ? ApprovalTitle
            : IsPaused
                ? "任务已停在安全点，随时可以继续"
                : IsRunning
                    ? $"正在{FriendlyStage(CurrentStage)}，你可以先不用操作"
                    : SelectedTask?.State switch
                    {
                        TaskState.Completed => "结果已经交付，先查看实际文件和验证结论",
                        TaskState.Failed => "这一步没有完成，原因和恢复入口都还在",
                        TaskState.Stale => "工作区发生了变化，重新验证后才能继续算完成",
                        TaskState.BudgetExhausted => "已有成果已经保存，可以增加预算后继续",
                        TaskState.Paused => "从保存的进度继续，不需要重新开始",
                        TaskState.Cancelled => "任务已停止，已有记录仍然保留",
                        _ => IsLiveConfigured
                            ? "描述你想做成的结果，NOVA 会自己检查工程"
                            : "先连接 OpenAI、DeepSeek 或 Kimi，然后就可以开始"
                    };

    public string HumanGuidanceDetail
        => IsApprovalVisible
            ? "选择一次允许、信任本轮同类低风险操作，或者先不做；拒绝不会丢失现有进度。"
            : IsRunning
                ? "只有需要权限、缺少外部条件或结果无法证明时，NOVA 才会停下来找你。"
                : SelectedTask?.State switch
                {
                    TaskState.Completed => "打开“本轮交付”即可接手；完整对话、Proof 和 Council 仍可按需展开。",
                    TaskState.Failed or TaskState.Paused
                        or TaskState.BudgetExhausted => "点击“从这里继续”，NOVA 会从持久化安全点恢复，不会重新开始。",
                    TaskState.Stale => "继续当前任务只会重做受影响的验证，不会从头改写项目。",
                    _ => IsLiveConfigured
                        ? "不需要学习模式或 Agent 术语；一句结果目标就够了。"
                        : "点击“连接模型”；密钥可以只存内存，也可以保存到 Windows 凭据管理器。"
                };

    public string AgentOsStatus
    {
        get => _agentOsStatus;
        private set => SetField(ref _agentOsStatus, value);
    }

    public string BudgetStatusLabel
    {
        get => _budgetStatusLabel;
        private set => SetField(ref _budgetStatusLabel, value);
    }

    public bool HasGoalMission => !string.IsNullOrWhiteSpace(GoalMissionTitle);

    public string GoalMissionTitle
    {
        get => _goalMissionTitle;
        private set
        {
            if (SetField(ref _goalMissionTitle, value))
            {
                OnPropertyChanged(nameof(HasGoalMission));
            }
        }
    }

    public string GoalMissionOutcome
    {
        get => _goalMissionOutcome;
        private set => SetField(ref _goalMissionOutcome, value);
    }

    public string GoalMissionMeta
    {
        get => _goalMissionMeta;
        private set => SetField(ref _goalMissionMeta, value);
    }

    public string GoalMissionPhase
    {
        get => _goalMissionPhase;
        private set => SetField(ref _goalMissionPhase, value);
    }

    public TaskItem? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (IsRunning && !ReferenceEquals(value, _selectedTask))
            {
                OnPropertyChanged();
                return;
            }
            if (_selectedTask is not null)
            {
                _selectedTask.Draft = _promptText;
                _selectedTask.PropertyChanged -= SelectedTask_PropertyChanged;
            }
            if (SetField(ref _selectedTask, value))
            {
                if (_selectedTask is not null)
                {
                    _selectedTask.PropertyChanged += SelectedTask_PropertyChanged;
                }
                IsCompletedConversationExpanded = false;
                OnPropertyChanged(nameof(CanResumeSelected));
                NotifyCompletionSurfaceChanged();
                NotifyHumanGuidanceChanged();
                ResumeSelectedCommand.RaiseCanExecuteChanged();
                ContinueConversationCommand.RaiseCanExecuteChanged();
                RefreshExecutionReadiness();
                ApplySelectedTaskView(value);
            }
        }
    }

    public string PromptText
    {
        get => _promptText;
        set
        {
            if (SetField(ref _promptText, value))
            {
                if (SelectedTask is not null)
                {
                    SelectedTask.Draft = value;
                }
                _submitCommand.RaiseCanExecuteChanged();
                RefreshExecutionReadiness();
            }
        }
    }

    public string CoreStatus
    {
        get => _coreStatus;
        private set
        {
            if (SetField(ref _coreStatus, value))
            {
                NotifyHumanGuidanceChanged();
            }
        }
    }

    public string CoreMessage
    {
        get => _coreMessage;
        private set
        {
            if (SetField(ref _coreMessage, value))
            {
                NotifyHumanGuidanceChanged();
            }
        }
    }

    public string CurrentStage
    {
        get => _currentStage;
        private set
        {
            if (SetField(ref _currentStage, value))
            {
                NotifyHumanGuidanceChanged();
            }
        }
    }

    public double OverallProgress
    {
        get => _overallProgress;
        private set
        {
            if (SetField(ref _overallProgress, value))
            {
                OnPropertyChanged(nameof(IsTraceVisible));
            }
        }
    }

    public int CurrentStep
    {
        get => _currentStep;
        private set => SetField(ref _currentStep, value);
    }

    public int ActiveAgentCount
    {
        get => _activeAgentCount;
        private set => SetField(ref _activeAgentCount, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetField(ref _isRunning, value))
            {
                return;
            }
            _pauseCommand.RaiseCanExecuteChanged();
            _cancelCommand.RaiseCanExecuteChanged();
            _submitCommand.RaiseCanExecuteChanged();
            ContinueConversationCommand.RaiseCanExecuteChanged();
            ArchiveTaskCommand.RaiseCanExecuteChanged();
            RestoreTaskCommand.RaiseCanExecuteChanged();
            ToggleArchivedTasksCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CanResumeSelected));
            OnPropertyChanged(nameof(IsTraceVisible));
            ResumeSelectedCommand.RaiseCanExecuteChanged();
            NotifyHumanGuidanceChanged();
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (SetField(ref _isPaused, value))
            {
                NotifyHumanGuidanceChanged();
            }
        }
    }

    public bool IsApprovalVisible
    {
        get => _isApprovalVisible;
        private set
        {
            if (SetField(ref _isApprovalVisible, value))
            {
                OnPropertyChanged(nameof(IsTraceVisible));
                NotifyHumanGuidanceChanged();
            }
        }
    }

    public string ApprovalTitle
    {
        get => _approvalTitle;
        private set
        {
            if (SetField(ref _approvalTitle, value))
            {
                NotifyHumanGuidanceChanged();
            }
        }
    }

    public string ApprovalDescription
    {
        get => _approvalDescription;
        private set => SetField(ref _approvalDescription, value);
    }

    public string ApprovalPreview
    {
        get => _approvalPreview;
        private set => SetField(ref _approvalPreview, value);
    }

    public string ApprovalStats
    {
        get => _approvalStats;
        private set => SetField(ref _approvalStats, value);
    }

    public bool IsApprovalPreviewVisible
    {
        get => _isApprovalPreviewVisible;
        private set => SetField(ref _isApprovalPreviewVisible, value);
    }

    public bool IsApprovalTrustVisible
    {
        get => _isApprovalTrustVisible;
        private set => SetField(ref _isApprovalTrustVisible, value);
    }

    public string ApprovalAllowLabel
    {
        get => _approvalAllowLabel;
        private set => SetField(ref _approvalAllowLabel, value);
    }

    public string ApprovalRejectLabel
    {
        get => _approvalRejectLabel;
        private set => SetField(ref _approvalRejectLabel, value);
    }

    public string ApprovalTrustLabel
    {
        get => _approvalTrustLabel;
        private set => SetField(ref _approvalTrustLabel, value);
    }

    public string ApprovalSafetyNote
    {
        get => _approvalSafetyNote;
        private set => SetField(ref _approvalSafetyNote, value);
    }

    public string ApprovalPolicyStatus
    {
        get => _approvalPolicyStatus;
        private set => SetField(ref _approvalPolicyStatus, value);
    }

    public string PauseLabel
    {
        get => _pauseLabel;
        private set => SetField(ref _pauseLabel, value);
    }

    public string RunTime
    {
        get => _runTime;
        private set => SetField(ref _runTime, value);
    }

    public bool HasArtifacts => Artifacts.Count > 0;
    public int DeliveryArtifactCount => DeliveryArtifacts.Count;
    public int DeliveryEvidenceCount => DeliveryEvidenceArtifacts.Count;
    public bool HasActivity => Activity.Count > 0;
    public bool IsTraceVisible
        => IsRunning || IsApprovalVisible || (HasActivity && OverallProgress < 100);
    public ArtifactItem? SelectedArtifact
    {
        get => _selectedArtifact;
        set
        {
            if (SetField(ref _selectedArtifact, value))
            {
                RefreshArtifactVersions(value);
            }
        }
    }

    public ArtifactItem? SelectedArtifactVersion
    {
        get => _selectedArtifactVersion;
        set
        {
            if (SetField(ref _selectedArtifactVersion, value))
            {
                OpenArtifactCommand.RaiseCanExecuteChanged();
                RevealArtifactCommand.RaiseCanExecuteChanged();
                CopyArtifactPathCommand.RaiseCanExecuteChanged();
                ContinueFromArtifactCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsDeliveryVisible
    {
        get => _isDeliveryVisible;
        private set => SetField(ref _isDeliveryVisible, value);
    }

    public bool IsDeliveryEvidenceExpanded
    {
        get => _isDeliveryEvidenceExpanded;
        set => SetField(ref _isDeliveryEvidenceExpanded, value);
    }

    public bool IsCompletedConversationExpanded
    {
        get => _isCompletedConversationExpanded;
        private set
        {
            if (SetField(ref _isCompletedConversationExpanded, value))
            {
                OnPropertyChanged(nameof(IsConversationTranscriptVisible));
                OnPropertyChanged(nameof(IsCompletedSummaryVisible));
                OnPropertyChanged(nameof(CompletedConversationToggleLabel));
            }
        }
    }

    public bool IsCompletedTask
        => SelectedTask?.State == TaskState.Completed && HasArtifacts;

    public bool IsConversationTranscriptVisible
        => !IsCompletedTask || IsCompletedConversationExpanded;

    public bool IsCompletedSummaryVisible
        => IsCompletedTask && !IsCompletedConversationExpanded;

    public string CompletedConversationToggleLabel
        => IsCompletedConversationExpanded ? "收起完整对话" : "查看完整对话";

    public bool HasStreamingText => !string.IsNullOrWhiteSpace(StreamingText);
    public bool HasConversationTurns => ConversationTurns.Count > 0;
    public string ConversationRoundLabel
    {
        get => _conversationRoundLabel;
        private set => SetField(ref _conversationRoundLabel, value);
    }
    public string SelectedConversationChoice
    {
        get => _selectedConversationChoice;
        private set
        {
            if (SetField(ref _selectedConversationChoice, value))
            {
                OnPropertyChanged(nameof(HasSelectedConversationChoice));
            }
        }
    }
    public bool HasSelectedConversationChoice
        => !string.IsNullOrWhiteSpace(SelectedConversationChoice);

    private void SelectConversationChoice(ConversationChoice choice)
    {
        if (IsRunning)
        {
            return;
        }

        SelectedConversationChoice = choice.Title;
        PromptText = choice.Prompt;
        CoreStatus = "CHOICE READY";
        CoreMessage = $"已选择「{choice.Title}」";
        CurrentStage = "可以直接执行，也可以在输入框继续补充要求";
    }

    private void ClearConversationChoice()
    {
        SelectedConversationChoice = string.Empty;
    }
    public bool IsLiveConfigured => !string.IsNullOrWhiteSpace(GetCurrentApiKey());
    public bool RequiresRuntimeForCurrentPrompt
        => !IsLiveConfigured;
    public string SubmitActionLabel
        => RequiresRuntimeForCurrentPrompt
            ? "连接模型"
            : HasPendingAttachments && string.IsNullOrWhiteSpace(PromptText)
                ? "分析附件"
            : SelectedExecutionMode == AgentExecutionMode.Goal
                ? "追踪目标"
                : "开始处理";
    public string ExecutionReadinessTitle
        => IsLiveConfigured
            ? HasPendingAttachments
                ? $"{PendingAttachments.Count} 个附件已就绪"
                : SelectedExecutionMode == AgentExecutionMode.Goal
                ? "目标模式已就绪"
                : "已经准备好，随时可以开始"
            : "尚未连接执行模型";
    public string ExecutionReadinessDetail
        => IsLiveConfigured
            ? HasPendingAttachments
                ? _provider == "deepseek" && PendingAttachments.Any(item => item.IsImage)
                    ? "图片需要切换到 Kimi 或 OpenAI；附件仍保留在输入区"
                    : $"{GetProviderLabel()} · {_model} · 附件只在本轮任务中发送"
                : SelectedExecutionMode == AgentExecutionMode.Goal
                ? $"{GetProviderLabel()} · {_model} · 只需描述结果，NOVA 会探索未知项并冻结成功标准"
                : $"{GetProviderLabel()} · {_model} · 写入与命令执行前会请求授权"
            : "先连接 OpenAI、DeepSeek 或 Kimi；未连接时不会创建任务或伪造交付物";
    public string EmptyStateTitle
        => SelectedExecutionMode == AgentExecutionMode.Goal
            ? "说一个你真心想抵达的结果"
            : "把想做成的事交给我";
    public string EmptyStateDetail
        => SelectedExecutionMode == AgentExecutionMode.Goal
            ? "不用先把需求想得滴水不漏。告诉我你想抵达哪里，我会查清未知项、立下成功标准，再一步步把结果做实。"
            : "不必学习复杂术语。选好工作区，直接说你想做成什么；对话、修改、授权和验证，我会替你收在同一条脉络里。";
    public string SuggestionLabel
        => SelectedExecutionMode == AgentExecutionMode.Goal
            ? "填入一个结果导向目标"
            : "填入一个可验证的工程目标";
    public bool CanResumeSelected => !IsRunning
        && SelectedTask?.State is TaskState.Paused
            or TaskState.Failed
            or TaskState.BudgetExhausted
            or TaskState.Stale;
    public string SelectedProvider => _provider;
    public string SelectedModel => _model;
    public string CapabilityIntent
        => !string.IsNullOrWhiteSpace(PromptText)
            ? PromptText.Trim()
            : SelectedTask?.Description ?? string.Empty;
    public AgentScheduleService ScheduleService => _scheduleService;
    public McpRegistryService McpRegistry => _mcpRegistry;
    public SkillRegistryService SkillRegistry => _skillRegistry;
    public TaskSnapshotService SnapshotService => _snapshots;
    public ProductivityInsightsService ProductivityInsights => _productivityInsights;
    public KnowledgeGraphService KnowledgeGraph => _knowledgeGraph;
    public KnowledgeIndexService KnowledgeIndex => _knowledgeIndex;
    public ArtifactRepositoryService ArtifactRepository => _artifactRepository;
    public EngineeringWorkspaceService EngineeringWorkspace => _engineeringWorkspace;
    public WorkspaceProfileService WorkspaceProfiles => _workspaceProfiles;
    public AgentOsKernel AgentOsKernel => _agentOsKernel;
    public AgentTaskGraphService AgentTaskGraph => _agentTaskGraph;
    public AgentResourceGovernor AgentResourceGovernor => _agentResourceGovernor;
    public AgentSupervisorService AgentSupervisor => _agentSupervisor;
    public string McpStatus
    {
        get => _mcpStatus;
        private set => SetField(ref _mcpStatus, value);
    }
    public string CapabilityStatus => "ENGINEERING · MULTI-AGENT · PC CONTROL";
    public string RecoveryStatus { get; }
    public string ScheduleStatus
    {
        get => _scheduleStatus;
        private set => SetField(ref _scheduleStatus, value);
    }
    public int RecoverableCount { get; private set; }

    public void RefreshExtensionStatus()
        => McpStatus = GetMcpStatus();

    public bool HasProviderKey(string provider)
        => _apiKeys.TryGetValue(provider, out var key) && !string.IsNullOrWhiteSpace(key);

    public bool IsProviderKeyPersisted(string provider)
    {
        try
        {
            return _credentialVault.IsStored(provider);
        }
        catch
        {
            return false;
        }
    }

    public string WorkspaceRoot
    {
        get => _workspaceRoot;
        private set => SetField(ref _workspaceRoot, value);
    }

    public string WorkspaceSummary
    {
        get => _workspaceSummary;
        private set => SetField(ref _workspaceSummary, value);
    }

    public string StreamingText
    {
        get => _streamingText;
        private set
        {
            SetField(ref _streamingText, value);
            OnPropertyChanged(nameof(HasStreamingText));
        }
    }

    public string RuntimeMode
    {
        get => _runtimeMode;
        private set => SetField(ref _runtimeMode, value);
    }

    public string RuntimeDetail
    {
        get => _runtimeDetail;
        private set => SetField(ref _runtimeDetail, value);
    }

    public void ConfigureLiveRuntime(
        string? apiKey,
        string provider,
        string model,
        bool clearRequested = false,
        bool persistRequested = false)
    {
        _provider = NormalizeProvider(provider);
        try
        {
            if (clearRequested)
            {
                _apiKeys[_provider] = string.Empty;
                _credentialVault.Delete(_provider);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    _apiKeys[_provider] = apiKey.Trim();
                }

                if (persistRequested && !string.IsNullOrWhiteSpace(_apiKeys[_provider]))
                {
                    _credentialVault.Write(_provider, _apiKeys[_provider]);
                }
                else if (!persistRequested)
                {
                    _credentialVault.Delete(_provider);
                }
            }
        }
        catch (Exception exception)
        {
            AddActivity(
                "凭据代理",
                "Windows 凭据操作失败",
                exception.Message,
                ActivityKind.System);
        }
        _model = string.IsNullOrWhiteSpace(model)
            ? _provider switch
            {
                "deepseek" => "deepseek-v4-flash",
                "kimi" => "kimi-k3",
                _ => "gpt-5.6"
            }
            : model.Trim();
        UpdateRuntimeLabels();
        OnPropertyChanged(nameof(IsLiveConfigured));
        RefreshExecutionReadiness();
        OnPropertyChanged(nameof(SelectedProvider));
        OnPropertyChanged(nameof(SelectedModel));
        AddActivity(
            "系统",
            IsLiveConfigured ? "真实模型已连接" : "执行模型已断开",
            IsLiveConfigured
                ? $"{GetProviderLabel()} · {_model} · "
                  + (IsProviderKeyPersisted(_provider)
                      ? "密钥由 Windows 凭据管理器保护"
                      : "密钥仅保存在当前进程内存")
                : $"{GetProviderLabel()} 未配置 API 密钥；NOVA 不会启动或伪造任务",
            ActivityKind.System);
        _ = _agentOsKernel.ReportServiceAsync(
            "runtime",
            "Model Runtime",
            IsLiveConfigured ? AgentOsServiceHealth.Ready : AgentOsServiceHealth.Degraded,
            IsLiveConfigured ? $"{GetProviderLabel()} · {_model}" : "No provider credential connected");
        RefreshAgentOsStatus();
    }

    public void SetWorkspaceRoot(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
        {
            return;
        }

        WorkspaceRoot = Path.GetFullPath(workspaceRoot);
        UpdateWorkspaceProfile(remember: true);
        AddActivity(
            "工作区路由器",
            "任务根目录已切换",
            $"{WorkspaceRoot} · {WorkspaceSummary}",
            ActivityKind.System);
        _ = _agentOsKernel.ReportServiceAsync(
            "workspace",
            "Workspace Router",
            AgentOsServiceHealth.Ready,
            WorkspaceSummary);
    }

    public void AddInputAttachments(IEnumerable<string> paths)
    {
        var validated = _inputAttachments.ValidateSelection(paths, PendingAttachments);
        PendingAttachments.Clear();
        foreach (var attachment in validated)
        {
            PendingAttachments.Add(attachment);
        }
        NotifyAttachmentStateChanged();
    }

    private void RemoveInputAttachment(AgentInputAttachment attachment)
    {
        PendingAttachments.Remove(attachment);
        NotifyAttachmentStateChanged();
    }

    private void ClearInputAttachments()
    {
        PendingAttachments.Clear();
        NotifyAttachmentStateChanged();
    }

    private void NotifyAttachmentStateChanged()
    {
        OnPropertyChanged(nameof(HasPendingAttachments));
        OnPropertyChanged(nameof(AttachmentSummary));
        _submitCommand.RaiseCanExecuteChanged();
        RefreshExecutionReadiness();
    }

    public Task StartNewTaskAsync()
    {
        var prompt = string.IsNullOrWhiteSpace(PromptText)
            ? "请查看并分析我添加的附件，结合当前工作区完成必要处理。"
            : PromptText.Trim();
        var continuation = SelectedTask is not null
                           && SelectedTask.State is TaskState.Completed
                               or TaskState.Failed
                               or TaskState.BudgetExhausted
                               or TaskState.Stale
                           && SelectedTask.WorkspaceRoot.Equals(
                               WorkspaceRoot,
                               StringComparison.OrdinalIgnoreCase);
        return StartTaskAsync(
            continuation ? SelectedTask : null,
            prompt,
            isContinuation: continuation,
            isRecovery: false);
    }

    public Task PrepareForShutdownAsync()
        => _shutdownPreparation ??= PrepareForShutdownCoreAsync();

    private async Task PrepareForShutdownCoreAsync()
    {
        _scheduleTimer.Stop();
        _agentResourceGovernor.SetPaused(false);
        CancelRun();

        while (IsRunning)
        {
            await Task.Delay(50);
        }

        if (SelectedTask is null)
        {
            return;
        }

        await _snapshots.SaveAsync(SelectedTask, CancellationToken.None);
        await _agentSupervisor.HeartbeatAsync(
            SelectedTask.Id,
            SelectedTask.Stage,
            forcePersist: true,
            cancellationToken: CancellationToken.None);
    }

    private Task ResumeSelectedTaskAsync()
        => SelectedTask is null
            ? Task.CompletedTask
            : StartTaskAsync(
                SelectedTask,
                string.IsNullOrWhiteSpace(PromptText)
                    ? "继续完成尚未完成的任务；先检查已有文件和验证证据，从中断点恢复，不要重新开始或重复已完成工作。"
                    : PromptText.Trim(),
                isContinuation: true,
                isRecovery: true);

    private async Task StartTaskAsync(
        TaskItem? resumedTask,
        string requestedPrompt,
        bool isContinuation,
        bool isRecovery,
        IReadOnlyList<AgentInputAttachment>? inputAttachmentsOverride = null)
    {
        var prompt = requestedPrompt.Trim();
        if (prompt.Length == 0)
        {
            return;
        }

        if (resumedTask is not null)
        {
            if (!string.IsNullOrWhiteSpace(resumedTask.WorkspaceRoot)
                && Directory.Exists(resumedTask.WorkspaceRoot))
            {
                WorkspaceRoot = Path.GetFullPath(resumedTask.WorkspaceRoot);
                UpdateWorkspaceProfile(remember: true);
            }
            if (isRecovery)
            {
                _provider = NormalizeProvider(resumedTask.Provider);
                _model = resumedTask.Model;
                _selectedExecutionMode = resumedTask.ExecutionMode;
                UpdateRuntimeLabels();
                OnPropertyChanged(nameof(SelectedProvider));
                OnPropertyChanged(nameof(SelectedModel));
                OnPropertyChanged(nameof(IsLiveConfigured));
                OnPropertyChanged(nameof(SelectedExecutionMode));
                OnPropertyChanged(nameof(ExecutionModeDetail));
                RefreshExecutionReadiness();
            }
        }

        if (!IsLiveConfigured)
        {
            CoreStatus = "SETUP REQUIRED";
            CoreMessage = "真实任务需要连接模型";
            CurrentStage = "点击“连接模型”，配置 OpenAI、DeepSeek 或 Kimi 后即可执行";
            AddActivity(
                "执行守门器",
                "已阻止伪执行",
                "当前没有可用的模型密钥。NOVA 没有创建虚假任务、交付物或代码变更。",
                ActivityKind.Waiting);
            return;
        }

        var pendingAttachments = inputAttachmentsOverride?.ToArray()
                                 ?? PendingAttachments.ToArray();
        var recoveryAttachments = isRecovery && pendingAttachments.Length == 0
            ? resumedTask?.Attachments ?? []
            : [];
        var turnAttachments = pendingAttachments.Length > 0
            ? pendingAttachments
            : recoveryAttachments;
        if (_provider.Equals("deepseek", StringComparison.OrdinalIgnoreCase)
            && turnAttachments.Any(item => item.IsImage))
        {
            CoreStatus = "MODEL CHANGE NEEDED";
            CoreMessage = "图片已保留，还没有发送";
            CurrentStage = "请切换到 Kimi 或 OpenAI 后继续；当前 DeepSeek 连接不支持图片输入";
            return;
        }

        CancelRun();
        await Task.Delay(80);

        Activity.Clear();
        Artifacts.Clear();
        DeliveryArtifacts.Clear();
        DeliveryEvidenceArtifacts.Clear();
        ArtifactVersions.Clear();
        SelectedArtifact = null;
        SelectedArtifactVersion = null;
        IsDeliveryVisible = false;
        IsDeliveryEvidenceExpanded = false;
        IsCompletedConversationExpanded = false;
        _streamingBuffer.Clear();
        _streamingFlushClock.Restart();
        StreamingText = string.Empty;
        OnPropertyChanged(nameof(HasArtifacts));
        ShowDeliveryCommand.RaiseCanExecuteChanged();
        IsApprovalVisible = false;
        IsPaused = false;
        PauseLabel = "暂停";
        CurrentStep = 0;
        OverallProgress = 0;
        RunTime = "00:00";

        var task = resumedTask ?? new TaskItem
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Title = CreateTitle(prompt),
            Description = prompt
        };
        task.WorkspaceRoot = WorkspaceRoot;
        task.Provider = _provider;
        task.Model = _model;
        task.ExecutionMode = SelectedExecutionMode;
        task.Attachments = pendingAttachments.Length > 0
            ? await _inputAttachments.PersistAsync(
                task.Id,
                pendingAttachments,
                CancellationToken.None)
            : recoveryAttachments;
        if (task.Attachments.Count > 0)
        {
            prompt += Environment.NewLine
                      + Environment.NewLine
                      + "[本轮附件] "
                      + string.Join(
                          "、",
                          task.Attachments.Select(item =>
                              $"{item.FileName}（{item.KindLabel}，{item.SizeLabel}）"));
        }
        task.State = TaskState.Running;
        task.Stage = isContinuation
            ? "续接对话上下文"
            : resumedTask is null
                ? "理解目标"
                : "从快照恢复目标";
        task.Progress = 0;
        if (resumedTask is null)
        {
            Tasks.Insert(0, task);
        }
        SelectedTask = task;
        _taskApprovalPolicy.BeginRun(task.Id);
        ApprovalPolicyStatus = "按风险确认 · 低风险可合并";
        ClearConversationChoice();
        PromptText = string.Empty;
        if (inputAttachmentsOverride is null)
        {
            ClearInputAttachments();
        }
        var userTurn = await _conversationHistory.AppendAsync(
            task.Id,
            "user",
            prompt);
        ConversationTurns.Add(userTurn);
        UpdateConversationLabels(task.Id);
        IsRunning = true;
        _runStartedAt = DateTimeOffset.Now;
        _runCancellation = new CancellationTokenSource();
        _agentResourceGovernor.BeginTask(task.Id, SelectedExecutionMode);
        _budgetWarningRaised = false;
        UpdateBudgetStatus();
        await _snapshots.SaveAsync(task, _runCancellation.Token);
        try
        {
            await StartAgentOsTaskAsync(task, isRecovery, _runCancellation.Token);
        }
        catch (AgentLeaseConflictException exception)
        {
            var failure = TaskFailureClassifier.Classify(
                task.Id,
                exception,
                task.Stage);
            await _failureLedger.RecordAsync(failure, CancellationToken.None);
            task.State = TaskState.Paused;
            task.Stage = "任务正在另一个宿主执行";
            CoreStatus = "LEASE CONFLICT";
            CoreMessage = exception.Message;
            CurrentStage = "等待现有宿主释放任务，NOVA 未启动第二条执行链";
            AddActivity(
                "Agent Supervisor",
                "已阻止双宿主执行",
                exception.Message,
                ActivityKind.Waiting);
            IsRunning = false;
            ActiveAgentCount = 0;
            _agentResourceGovernor.EndTask(task.Id);
            _taskApprovalPolicy.EndRun();
            ApprovalPolicyStatus = "按风险确认 · 低风险可合并";
            _runCancellation.Dispose();
            _runCancellation = null;
            await _snapshots.SaveAsync(task, CancellationToken.None);
            OnPropertyChanged(nameof(CanResumeSelected));
            ResumeSelectedCommand.RaiseCanExecuteChanged();
            return;
        }
        RefreshAgentOsStatus();

        CoreStatus = "THINKING";
            CoreMessage = "我先听懂你真正想做成什么";
        CurrentStage = "构建任务边界";
        ActiveAgentCount = 1;
        AddActivity(
            "NOVA",
            isContinuation ? "续接对话" : resumedTask is null ? "接收目标" : "恢复任务",
            isContinuation
                ? $"已载入 {ConversationTurns.Count} 条本地对话记录，并保持同一任务与工作区"
                : resumedTask is null
                    ? "建立任务上下文与成功标准"
                    : "从持久化快照重新建立安全执行上下文",
            ActivityKind.System);
        var routingPrompt = isContinuation
            ? _conversationHistory.BuildContextPrompt(task.Id, prompt)
            : task.Description;
        var engineeringProfile = EngineeringTaskRouter.Classify(routingPrompt);
        if (engineeringProfile.IsEngineeringTask
            && AgentExecutionPolicy.CanMutateWorkspace(SelectedExecutionMode))
        {
            AddActivity(
                "工程路由器",
                "已进入专业工程模式",
                $"风险 {engineeringProfile.Risk} · {engineeringProfile.Verification} · 写入与命令继续经过授权代理",
                ActivityKind.System);
            var beforeCheckpoint = await _engineeringCheckpoints.CaptureAsync(
                task.Id,
                "before",
                WorkspaceRoot,
                _runCancellation.Token);
            if (beforeCheckpoint is not null)
            {
                AddActivity(
                    "证据账本",
                    "已保存任务前检查点",
                    $"{beforeCheckpoint.GitBranch} · {beforeCheckpoint.ChangedFiles.Count} 个既有变更",
                    ActivityKind.Completed);
            }
        }
        await _snapshots.SaveAsync(task);

        try
        {
            await RunLivePipelineAsync(
                task,
                prompt,
                isRecovery,
                _runCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            var failure = TaskFailureClassifier.CreateHostInterruption(
                task.Id,
                "用户取消了当前执行；已完成动作和最近安全点仍然保留。",
                task.Stage);
            await _failureLedger.RecordAsync(failure, CancellationToken.None);
            if (task.ExecutionMode == AgentExecutionMode.Goal)
            {
                await _goalOutcomes.MarkInterruptedAsync(
                    task.Id,
                    "用户取消或主机在完成证明前停止。",
                    CancellationToken.None);
                LoadGoalMissionView(task.Id);
            }
            task.State = TaskState.Cancelled;
            task.Stage = "任务已取消";
            CoreStatus = "CANCELLED";
            CoreMessage = "已安全停止所有执行";
            CurrentStage = "没有正在运行的工具";
            AddActivity("系统", "任务取消", "所有本地执行单元已停止", ActivityKind.System);
        }
        finally
        {
            if (engineeringProfile.IsEngineeringTask
                && AgentExecutionPolicy.CanMutateWorkspace(SelectedExecutionMode))
            {
                var afterCheckpoint = await _engineeringCheckpoints.CaptureAsync(
                    task.Id,
                    "after",
                    WorkspaceRoot,
                    CancellationToken.None);
                if (afterCheckpoint is not null)
                {
                    AddActivity(
                        "证据账本",
                        "已保存任务后检查点",
                        $"{afterCheckpoint.ChangedFiles.Count} 个变更 · +{afterCheckpoint.Additions} / -{afterCheckpoint.Deletions}",
                        ActivityKind.Completed);
                }
            }
            IsRunning = false;
            IsPaused = false;
            IsApprovalVisible = false;
            IsApprovalTrustVisible = false;
            ActiveAgentCount = 0;
            _approvalSource = null;
            _activeApprovalScope = null;
            _approvalTrustRequested = false;
            _taskApprovalPolicy.EndRun();
            ApprovalPolicyStatus = "按风险确认 · 低风险可合并";
            _runCancellation?.Dispose();
            _runCancellation = null;
            _agentResourceGovernor.EndTask(task.Id);
            await CompleteAgentOsTaskAsync(task);
            RefreshAgentOsStatus();
            await _snapshots.SaveAsync(task);
            OnPropertyChanged(nameof(CanResumeSelected));
            ResumeSelectedCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task RunLivePipelineAsync(
        TaskItem task,
        string turnPrompt,
        bool isRecovery,
        CancellationToken cancellationToken)
    {
        var runStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var executionProvider = _provider;
        var executionModel = _model;
        var isEngineeringTask = false;
        var conversationPrompt = _conversationHistory.BuildContextPrompt(
            task.Id,
            turnPrompt);
        CoreStatus = "LIVE";
        CoreMessage = $"正在连接 {GetProviderLabel()} · {_model}";
        CurrentStage = "真实 Agent Runtime";
        task.Stage = "连接模型";

        try
        {
            var engineeringProfile = EngineeringTaskRouter.Classify(conversationPrompt);
            isEngineeringTask = engineeringProfile.IsEngineeringTask;
            var route = task.Attachments.Count > 0
                ? new ModelRouteRecommendation(
                    executionProvider,
                    executionModel,
                    false,
                    "本轮包含附件；保持用户选择的多模态模型，避免附件在跨提供商路由中丢失。",
                    [
                        new ModelRouteCandidate(
                            executionProvider,
                            executionModel,
                            true,
                            0,
                            0,
                            "附件锁定当前提供商")
                    ])
                : RecommendModelRoute(engineeringProfile);
            AddActivity(
                "模型路由器",
                route.ShouldSwitch ? "发现更优证据路线" : "保持当前模型",
                route.Summary,
                route.ShouldSwitch ? ActivityKind.Waiting : ActivityKind.System);
            if (route.ShouldSwitch)
            {
                var approved = await RequestToolApprovalAsync(
                    task,
                    new ToolApprovalRequest(
                        "adaptive_model_route",
                        $"切换到 {GetProviderLabel(route.Provider)} · {route.Model}？",
                        "AgentBench 根据本机真实任务记录推荐更换本轮指挥官。"
                        + "这不会增加并行请求，但当前目标和 Context Pack 将发送给所选提供商。拒绝后继续使用原模型。",
                        string.Join(
                            Environment.NewLine,
                            route.Candidates.Select(candidate =>
                                $"{candidate.Provider} · {candidate.Model} · "
                                + $"{candidate.BenchRuns} runs · score {candidate.RouteScore:0.##} · "
                                + candidate.Reason))),
                    cancellationToken);
                if (approved)
                {
                    executionProvider = route.Provider;
                    executionModel = route.Model;
                    task.Provider = executionProvider;
                    task.Model = executionModel;
                    CoreMessage =
                        $"AgentBench 路由到 {GetProviderLabel(executionProvider)} · {executionModel}";
                }
            }
            var runtime = GetRuntime(executionProvider);
            var executionApiKey = _apiKeys[executionProvider];
            TaskOutcomeContract? outcomeContract = null;
            AdaptiveContextPack? contextPack = null;
            EngineeringWorkspaceSnapshot? engineeringSnapshot = null;
            GoalMissionCharter? goalMission = null;
            GoalOutcomeLedger? goalOutcomeLedger = null;
            if (isEngineeringTask || SelectedExecutionMode == AgentExecutionMode.Goal)
            {
                engineeringSnapshot = await _engineeringWorkspace.InspectAsync(
                    WorkspaceRoot,
                    cancellationToken);
                outcomeContract = await _outcomeContracts.CreateAsync(
                    task.Id,
                    conversationPrompt,
                    SelectedExecutionMode,
                    engineeringSnapshot,
                    cancellationToken);
                contextPack = await _contextCompiler.CompileAsync(
                    task.Id,
                    WorkspaceRoot,
                    conversationPrompt,
                    engineeringSnapshot,
                    cancellationToken: cancellationToken);
                AddActivity(
                    "完成契约",
                    $"已建立 {outcomeContract.Criteria.Count} 项 Proof-of-Done",
                    $"目标与证据要求已冻结 · {outcomeContract.ExecutionMode} · "
                    + (outcomeContract.RequiresWorkspaceMutation ? "需要真实修改" : "保持只读"),
                    ActivityKind.Completed);
                AddActivity(
                    "上下文编译器",
                    $"已选择 {contextPack.Selections.Count} 个高信号文件",
                    $"{contextPack.UsedCharacters:N0}/{contextPack.CharacterBudget:N0} 字符 · "
                    + $"{contextPack.CompileDuration.TotalMilliseconds:N0} ms · "
                    + $"fingerprint {contextPack.Fingerprint[..10]}",
                    ActivityKind.Completed);
            }
            if (SelectedExecutionMode == AgentExecutionMode.Goal
                && outcomeContract is not null
                && engineeringSnapshot is not null)
            {
                goalMission = isRecovery ? _goalMissions.Load(task.Id) : null;
                if (goalMission is null)
                {
                    goalMission = await DiscoverGoalMissionAsync(
                        task,
                        conversationPrompt,
                        outcomeContract,
                        contextPack,
                        engineeringSnapshot,
                        executionProvider,
                        executionModel,
                        cancellationToken);
                }
                else
                {
                    AddActivity(
                        "Goal Recovery",
                        $"已恢复 Mission v{goalMission.MissionVersion}",
                        $"hash {goalMission.MissionHash[..12]} · "
                        + "从原成功信号继续，不重新探索目标",
                        ActivityKind.Completed);
                }
                goalOutcomeLedger = await _goalOutcomes.InitializeAsync(
                    goalMission,
                    cancellationToken);
                if (!isRecovery)
                {
                    goalOutcomeLedger = await _goalOutcomes.SetPhaseAsync(
                        task.Id,
                        GoalRunPhase.Chartered,
                        "Mission Charter 已冻结，成功信号等待执行与独立验证。",
                        cancellationToken)
                        ?? goalOutcomeLedger;
                }
                ApplyGoalMissionView(goalMission, goalOutcomeLedger);
                outcomeContract = await _outcomeContracts.CreateGoalAsync(
                    task.Id,
                    goalMission,
                    engineeringSnapshot,
                    cancellationToken);
                if (goalMission.RequiresWorkspaceChange)
                {
                    var goalBaseline = await _engineeringCheckpoints.CaptureAsync(
                        task.Id,
                        "before-goal",
                        WorkspaceRoot,
                        cancellationToken);
                    if (goalBaseline is not null)
                    {
                        AddActivity(
                            "Goal Baseline",
                            "已补建工程安全基线",
                            $"{goalBaseline.GitBranch} · "
                            + $"{goalBaseline.ChangedFiles.Count} 个既有变更",
                            ActivityKind.Completed);
                    }
                }
                isEngineeringTask = isEngineeringTask || goalMission.RequiresWorkspaceChange;
                engineeringProfile = EngineeringTaskRouter.Classify(
                    $"{conversationPrompt}\n{goalMission.ObjectiveForContract}");
                AddActivity(
                    "Goal Mission",
                    $"{goalMission.ExecutionKind} · confidence {goalMission.Confidence}%",
                    $"{goalMission.SuccessSignals.Count} 个成功信号 · "
                    + $"{goalMission.Unknowns.Count} 个待探索未知项",
                    ActivityKind.Completed);
            }
            var baseRuntimePrompt = goalMission is not null
                ? BuildGoalRuntimePrompt(
                    conversationPrompt,
                    goalMission,
                    outcomeContract!,
                    contextPack,
                    engineeringSnapshot!)
                : isEngineeringTask
                    ? BuildEngineeringRuntimePrompt(
                    conversationPrompt,
                    outcomeContract,
                    contextPack,
                    engineeringSnapshot!)
                    : conversationPrompt;
            var capabilityReport = _capabilityCompass.Analyze(
                conversationPrompt,
                WorkspaceRoot);
            var runtimePrompt =
                $"""
                {CapabilityCompassService.FormatForPrompt(capabilityReport)}

                {baseRuntimePrompt}
                """;
            AddActivity(
                "能力司南",
                capabilityReport.ReadyCount > 0
                    ? $"最小挂载 {capabilityReport.ReadyCount} 项相关能力"
                    : "内建能力优先",
                capabilityReport.SuggestedCount > 0
                    ? $"{capabilityReport.WorkspaceSignal} · {capabilityReport.SuggestedCount} 项扩展等待用户确认，不会静默启用"
                    : $"{capabilityReport.WorkspaceSignal} · 没有扩大本轮权限面",
                capabilityReport.SuggestedCount > 0
                    ? ActivityKind.Waiting
                    : ActivityKind.System);
            var effectiveExecutionMode = goalMission?.ExecutionKind == "RESEARCH"
                ? AgentExecutionMode.Ask
                : SelectedExecutionMode;
            if (goalMission is not null)
            {
                goalOutcomeLedger = await _goalOutcomes.SetPhaseAsync(
                    task.Id,
                    GoalRunPhase.Executing,
                    isRecovery
                        ? "已从持久化 Mission 和未证明信号恢复执行。"
                        : "正在执行策略并收集成功信号证据。",
                    cancellationToken)
                    ?? goalOutcomeLedger;
                ApplyGoalMissionView(goalMission, goalOutcomeLedger);
            }
            AgentMeshExecutionOutcome? meshOutcome = null;
            TournamentExecutionOutcome? tournamentOutcome = null;
            if (isEngineeringTask
                && SelectedExecutionMode is
                    AgentExecutionMode.Autopilot or AgentExecutionMode.Goal
                && outcomeContract?.RequiresWorkspaceMutation == true
                && engineeringSnapshot is not null
                && task.Attachments.Count == 0)
            {
                meshOutcome = await TryRunAgentMeshAsync(
                    task,
                    runtimePrompt,
                    outcomeContract,
                    contextPack,
                    engineeringSnapshot,
                    executionProvider,
                    executionModel,
                    cancellationToken);
                if (meshOutcome is null)
                {
                    tournamentOutcome = await TryRunWorktreeTournamentAsync(
                        task,
                        runtimePrompt,
                        outcomeContract,
                        engineeringSnapshot,
                        executionProvider,
                        executionModel,
                        cancellationToken);
                }
            }
            var result = meshOutcome?.Result
                         ?? tournamentOutcome?.Result
                         ?? await runtime.RunAsync(
                             new AgentRunRequest(
                                 task.Id,
                                 runtimePrompt,
                                 WorkspaceRoot,
                                 executionApiKey,
                                 executionProvider,
                                 executionModel,
                                 effectiveExecutionMode,
                                 Attachments: task.Attachments),
                             async runtimeEvent =>
                             {
                                 await HandleRuntimeEventAsync(
                                     task,
                                     runtimeEvent,
                                     cancellationToken);
                             },
                             approval => RequestToolApprovalAsync(
                                 task,
                                 approval,
                                 cancellationToken),
                             cancellationToken);
            var workspaceMutationObserved = result.MutatingToolCalls > 0
                                            || meshOutcome?.Applied == true
                                            || tournamentOutcome?.Applied == true;
            var closureProvider = result.Provider;
            var closureModel = result.Model;
            var closureRuntime = GetRuntime(closureProvider);
            EngineeringClosureResult closure;
            if (meshOutcome is { Applied: true }
                && meshOutcome.Mesh.Verification is { Passed: true } meshVerification)
            {
                closure = new EngineeringClosureResult(
                    result with
                    {
                        FinalText = result.FinalText
                                    + Environment.NewLine
                                    + Environment.NewLine
                                    + $"NOVA Mesh 验证：通过（{meshVerification.Command}，"
                                    + $"{meshVerification.Duration.TotalSeconds:F1}s）；"
                                    + "Combined Patch 哈希和源工作区基线已在 Merge Gate 复核。"
                    },
                    true,
                    true,
                    meshOutcome.Mesh.Review,
                    "Mesh 集成结果已验证并通过哈希保护应用");
            }
            else if (tournamentOutcome is { Applied: true }
                     && tournamentOutcome.Tournament.Candidates.FirstOrDefault(item =>
                         item.Spec.Id.Equals(
                             tournamentOutcome.Decision.WinnerId,
                             StringComparison.OrdinalIgnoreCase))
                     is { Verification.Passed: true } winner)
            {
                closure = new EngineeringClosureResult(
                    result with
                    {
                        FinalText = result.FinalText
                                    + Environment.NewLine
                                    + Environment.NewLine
                                    + $"NOVA Winner 验证：通过（{winner.Verification.Command}，"
                                    + $"{winner.Verification.Duration.TotalSeconds:F1}s）；"
                                    + "Winner Patch 哈希和源工作区基线已在 Merge Gate 复核。"
                    },
                    true,
                    true,
                    winner.Review,
                    "Winner 隔离结果已验证并通过哈希保护应用");
            }
            else if (isEngineeringTask
                     && AgentExecutionPolicy.CanMutateWorkspace(SelectedExecutionMode)
                     && result.MutatingToolCalls > 0)
            {
                closure = await RunEngineeringClosureAsync(
                    task,
                    closureRuntime,
                    result,
                    outcomeContract,
                    engineeringSnapshot!,
                    closureProvider,
                    closureModel,
                    _apiKeys[closureProvider],
                    cancellationToken);
            }
            else
            {
                closure = new EngineeringClosureResult(
                    result,
                    false,
                    result.MutatingToolCalls > 0
                    || outcomeContract?.RequiresWorkspaceMutation != true,
                    null,
                    AgentExecutionPolicy.CanMutateWorkspace(SelectedExecutionMode)
                    && outcomeContract?.RequiresWorkspaceMutation == true
                    && result.MutatingToolCalls == 0
                        ? "没有真实文件变更，工程验证未启动。"
                        : AgentExecutionPolicy.CanMutateWorkspace(SelectedExecutionMode)
                            ? "非工程任务无需自动验证。"
                            : $"{SelectedExecutionMode} 模式保持只读。");
            }
            if (isEngineeringTask
                && engineeringSnapshot is not null
                && outcomeContract?.RequiresWorkspaceMutation == true
                && workspaceMutationObserved
                && closure.Completeness is null)
            {
                var completionSnapshot = await _engineeringWorkspace.InspectAsync(
                    WorkspaceRoot,
                    cancellationToken);
                var completeness = await _engineeringCompleteness.AssessAndPersistAsync(
                    task.Id,
                    conversationPrompt,
                    engineeringSnapshot,
                    completionSnapshot,
                    closure.VerificationAttempted,
                    closure.Passed,
                    closure.Review,
                    cancellationToken);
                AddActivity(
                    "工程完整性审查官",
                    completeness.ReadyForDelivery
                        ? "工程完整性达到交付线"
                        : "工程完整性阻止交付",
                    completeness.Summary,
                    completeness.ReadyForDelivery
                        ? ActivityKind.Completed
                        : ActivityKind.Waiting);
                closure = closure with
                {
                    Summary = completeness.ReadyForDelivery
                        ? closure.Summary
                        : "实现存在，但工程完整性尚未闭合",
                    Completeness = completeness,
                    Result = closure.Result with
                    {
                        FinalText = closure.Result.FinalText
                                    + Environment.NewLine
                                    + Environment.NewLine
                                    + EngineeringCompletenessService.Format(completeness)
                    }
                };
            }
            result = closure.Result;
            if (goalMission is not null)
            {
                goalOutcomeLedger = await _goalOutcomes.SetPhaseAsync(
                    task.Id,
                    GoalRunPhase.Verifying,
                    "执行阶段结束，正在为每个成功信号建立独立证据。",
                    cancellationToken)
                    ?? goalOutcomeLedger;
                ApplyGoalMissionView(goalMission, goalOutcomeLedger);
            }
            VerificationCouncilResult? council = meshOutcome?.VerificationCouncil
                                                  ?? tournamentOutcome?.VerificationCouncil;
            var requiresSignalCouncil = goalMission is not null
                                        && council?.RawResponse.Contains(
                                            "SIGNAL 1:",
                                            StringComparison.OrdinalIgnoreCase) != true;
            if ((council is null || requiresSignalCouncil)
                && outcomeContract?.Criteria.Any(item =>
                    item.Id == "independent-council") == true)
            {
                council = await RunIndependentVerificationCouncilAsync(
                    task,
                    outcomeContract,
                    result,
                    closure,
                    executionProvider,
                    executionModel,
                    cancellationToken);
            }
            TaskOutcomeAssessment? outcomeAssessment = null;
            if (outcomeContract is not null)
            {
                outcomeAssessment = await _outcomeContracts.AssessAsync(
                    outcomeContract,
                    result,
                    closure.VerificationAttempted,
                    closure.Passed,
                    closure.Review,
                    council,
                    cancellationToken);
                if (goalMission is null)
                {
                    result = result with
                    {
                        FinalText = result.FinalText
                                    + Environment.NewLine
                                    + Environment.NewLine
                                    + TaskOutcomeContractService.FormatAssessment(outcomeAssessment)
                    };
                }
                AddActivity(
                    "完成证明",
                    $"{outcomeAssessment.Status} · {outcomeAssessment.ProofScore}/100",
                    $"{outcomeAssessment.Criteria.Count(item => item.Status == "PASS")}/"
                    + $"{outcomeAssessment.Criteria.Count} 项已有证据",
                    outcomeAssessment.Status == "PROVEN"
                        ? ActivityKind.Completed
                        : ActivityKind.System);
            }
            if (goalMission is not null)
            {
                var workspaceEvidence = await _workspaceEvidence.CaptureAsync(
                    task.WorkspaceRoot,
                    cancellationToken);
                goalOutcomeLedger = await _goalOutcomes.ReconcileAsync(
                    goalMission,
                    outcomeAssessment,
                    council,
                    workspaceEvidence,
                    cancellationToken);
                ApplyGoalMissionView(goalMission, goalOutcomeLedger);
                if (goalOutcomeLedger.Phase == GoalRunPhase.Partial
                    && outcomeContract is not null
                    && engineeringSnapshot is not null)
                {
                    var repairExecution = await RunGoalRepairLoopAsync(
                        task,
                        goalMission,
                        outcomeContract,
                        engineeringSnapshot,
                        runtime,
                        executionApiKey,
                        executionProvider,
                        executionModel,
                        result,
                        closure,
                        council,
                        outcomeAssessment,
                        goalOutcomeLedger,
                        workspaceEvidence,
                        cancellationToken);
                    result = repairExecution.Result;
                    closure = repairExecution.Closure;
                    council = repairExecution.Council;
                    outcomeAssessment = repairExecution.Assessment;
                    goalOutcomeLedger = repairExecution.Ledger;
                    workspaceMutationObserved =
                        workspaceMutationObserved
                        || repairExecution.WorkspaceMutationObserved;
                }
                result = result with
                {
                    FinalText = result.FinalText
                                + Environment.NewLine
                                + Environment.NewLine
                                + (outcomeAssessment is null
                                    ? string.Empty
                                    : TaskOutcomeContractService.FormatAssessment(
                                        outcomeAssessment)
                                      + Environment.NewLine
                                      + Environment.NewLine)
                                + FormatGoalOutcomeLedger(goalOutcomeLedger)
                };
                AddActivity(
                    "Goal Evidence Matrix",
                    $"{goalOutcomeLedger.Phase} · "
                    + $"{goalOutcomeLedger.Signals.Count(item => item.Status == GoalSignalStatus.Pass)}/"
                    + $"{goalOutcomeLedger.Signals.Count} success signals",
                    goalOutcomeLedger.Detail,
                    goalOutcomeLedger.IsProven
                        ? ActivityKind.Completed
                        : ActivityKind.Waiting);
            }
            var mutationRequired = outcomeContract?.RequiresWorkspaceMutation
                                   ?? (AgentExecutionPolicy.CanMutateWorkspace(
                                           SelectedExecutionMode)
                                       && EngineeringTaskRouter.RequiresWorkspaceMutation(
                                           conversationPrompt));
            var implementationAccepted = !mutationRequired || workspaceMutationObserved;

            _agentResourceGovernor.ValidateFinalOutput(
                task.Id,
                result.FinalText.Length);
            var assistantTurn = await _conversationHistory.AppendAsync(
                task.Id,
                "assistant",
                result.FinalText,
                cancellationToken);
            ConversationTurns.Add(assistantTurn);
            StreamingText = string.Empty;
            UpdateConversationLabels(task.Id);
            var outputPath = await SaveAgentOutputAsync(
                task,
                result,
                _conversationHistory.GetResponseCount(task.Id),
                cancellationToken);
            if (engineeringSnapshot is not null)
            {
                var deliverySnapshot = await _engineeringWorkspace.InspectAsync(
                    task.WorkspaceRoot,
                    cancellationToken);
                var delivery = await _deliveryManifest.CreateAsync(
                    task.Id,
                    task.Title,
                    engineeringSnapshot,
                    deliverySnapshot,
                    outcomeAssessment?.Status
                    ?? (implementationAccepted ? "DELIVERED" : "FAILED"),
                    outcomeAssessment?.ProofScore
                    ?? (implementationAccepted ? 100 : 0),
                    closure.VerificationAttempted,
                    closure.Passed,
                    closure.Summary,
                    cancellationToken);
                Artifacts.Add(new ArtifactItem(
                    "交付",
                    "本轮实际交付",
                    delivery.Summary,
                    "\uE8F1",
                    delivery.ResultStatus == "PROVEN" ? "#6BE5A9" : "#75F0FF",
                    delivery.Preview,
                    delivery.ArtifactPath));
            }
            Artifacts.Add(new ArtifactItem(
                "回答",
                "NOVA 真实任务结果",
                $"{GetProviderLabel(result.Provider)} · {result.Model} · {result.ToolCalls} 次工具调用",
                "\uE8A5",
                "#75F0FF",
                result.FinalText,
                outputPath));
            Artifacts.Add(new ArtifactItem(
                "记录",
                "本地结果文件",
                outputPath,
                "\uE7C3",
                "#6BE5A9",
                "完整回答、运行元数据和完成时间已经写入本地文件，可用于继续编辑、归档或再次交给 Agent 处理。",
                outputPath));
            if (outcomeAssessment is not null)
            {
                Artifacts.Add(new ArtifactItem(
                    "证明",
                    $"Proof-of-Done · {outcomeAssessment.Status}",
                    $"{outcomeAssessment.ProofScore}/100 · "
                    + $"{outcomeAssessment.Criteria.Count(item => item.Status == "PASS")} 项通过",
                    "\uE73E",
                    outcomeAssessment.Status == "PROVEN" ? "#6BE5A9" : "#FFC470",
                    TaskOutcomeContractService.FormatAssessment(outcomeAssessment),
                    outcomeAssessment.ArtifactPath));
            }
            if (closure.Completeness is not null)
            {
                Artifacts.Add(new ArtifactItem(
                    "工程",
                    $"Engineering Completeness · "
                    + (closure.Completeness.ReadyForDelivery ? "READY" : "NOT READY"),
                    $"{closure.Completeness.Score}/100 · "
                    + $"{closure.Completeness.ChangedFileCount} files · "
                    + $"{closure.Completeness.Findings.Count(item => item.Severity == "BLOCKER")} blockers",
                    "\uE9D9",
                    closure.Completeness.ReadyForDelivery ? "#6BE5A9" : "#FF7187",
                    EngineeringCompletenessService.Format(closure.Completeness),
                    closure.Completeness.ArtifactPath));
            }
            if (contextPack is not null)
            {
                Artifacts.Add(new ArtifactItem(
                    "上下文",
                    "Adaptive Context Pack",
                    $"{contextPack.Selections.Count} 文件 · "
                    + $"{contextPack.UsedCharacters:N0} 字符 · "
                    + $"{contextPack.CompileDuration.TotalMilliseconds:N0} ms",
                    "\uE71D",
                    "#9B8AFB",
                    string.Join(
                        Environment.NewLine,
                        contextPack.Selections.Select(item =>
                            $"{item.RelativePath}:{item.StartLine}-{item.EndLine} · "
                            + $"{string.Join("; ", item.Reasons)}")),
                    contextPack.ArtifactPath));
            }
            if (goalMission is not null)
            {
                Artifacts.Add(new ArtifactItem(
                    "目标",
                    $"Mission Charter · {goalMission.Title}",
                    $"{goalMission.ExecutionKind} · "
                    + $"{goalMission.SuccessSignals.Count} success signals · "
                    + $"confidence {goalMission.Confidence}%",
                    "\uE7C1",
                    "#75F0FF",
                    GoalMissionService.Format(goalMission),
                    goalMission.ArtifactPath));
            }
            if (goalOutcomeLedger is not null)
            {
                Artifacts.Add(new ArtifactItem(
                    "证据",
                    $"Goal Evidence Matrix · {goalOutcomeLedger.Phase}",
                    $"{goalOutcomeLedger.Signals.Count(item => item.Status == GoalSignalStatus.Pass)}/"
                    + $"{goalOutcomeLedger.Signals.Count} success signals · "
                    + $"Proof {goalOutcomeLedger.AssessmentProofScore}/100",
                    "\uE73E",
                    goalOutcomeLedger.IsProven ? "#6BE5A9" : "#FFC470",
                    FormatGoalOutcomeLedger(goalOutcomeLedger)));
            }
            if (council is not null)
            {
                Artifacts.Add(new ArtifactItem(
                    "审查",
                    $"Independent Council · {council.Verdict}",
                    $"{GetProviderLabel(council.Provider)} · {council.Model} · "
                    + $"confidence {council.Confidence}%",
                    "\uE8D7",
                    council.Passed ? "#6BE5A9" : council.IsBlocking ? "#FF7187" : "#FFC470",
                    IndependentVerificationCouncilService.Format(council)));
            }
            if (tournamentOutcome is not null)
            {
                var tournament = tournamentOutcome.Tournament;
                var winnerLabel = tournamentOutcome.Decision.Selected
                    ? tournamentOutcome.Decision.WinnerId
                    : "NONE";
                Artifacts.Add(new ArtifactItem(
                    "竞赛",
                    $"Worktree Tournament · {winnerLabel}",
                    $"{tournament.Candidates.Count} candidates · "
                    + $"{tournamentOutcome.Decision.Verdict} · "
                    + (tournamentOutcome.Applied ? "Patch 已应用" : "主工作区未修改"),
                    "\uE9D9",
                    tournamentOutcome.Applied ? "#6BE5A9" : "#FFC470",
                    FormatTournamentOutcome(tournamentOutcome),
                    Path.Combine(tournament.ArtifactDirectory, "decision.json")));
            }
            if (meshOutcome is not null)
            {
                var mesh = meshOutcome.Mesh;
                Artifacts.Add(new ArtifactItem(
                    "协作",
                    $"Agent Mesh · {meshOutcome.Decision.Verdict}",
                    $"{mesh.Packages.Count} packages · {mesh.Waves.Count} waves · "
                    + (meshOutcome.Applied ? "Combined Patch 已应用" : "主工作区未修改"),
                    "\uE950",
                    meshOutcome.Applied ? "#6BE5A9" : "#FFC470",
                    FormatAgentMeshOutcome(meshOutcome),
                    Path.Combine(mesh.ArtifactDirectory, "decision.json")));
            }
            OnPropertyChanged(nameof(HasArtifacts));
            RefreshDeliveryCollections();
            NotifyCompletionSurfaceChanged();
            ShowDeliveryCommand.RaiseCanExecuteChanged();
            IsDeliveryVisible = false;
            CoreStatus = "FINALIZING";
            CoreMessage = "成果已可查看 · 正在后台完成版本登记";
            await PersistArtifactsAsync(task, cancellationToken);
            await RecordAgentBenchAsync(
                task,
                result.Provider,
                result.Model,
                isEngineeringTask,
                mutationRequired,
                outcomeAssessment?.Status
                    ?? (implementationAccepted ? "DELIVERED" : "FAILED"),
                outcomeAssessment?.ProofScore ?? (implementationAccepted ? 100 : 0),
                closure.VerificationAttempted,
                closure.Passed,
                result.ToolCalls,
                result.MutatingToolCalls,
                contextPack,
                System.Diagnostics.Stopwatch.GetElapsedTime(runStartedAt),
                cancellationToken);
            AddActivity(
                "AgentBench",
                "已记录真实任务样本",
                $"{GetProviderLabel(result.Provider)} · {result.Model} · "
                + $"{outcomeAssessment?.Status ?? "DELIVERED"} · "
                + $"Proof {outcomeAssessment?.ProofScore ?? 100}/100",
                ActivityKind.Completed);

            var proofFailed = outcomeAssessment?.Status == "FAILED";
            var goalNotProven = goalMission is not null
                                && goalOutcomeLedger?.IsProven != true;
            var goalBlocked = goalOutcomeLedger?.Phase == GoalRunPhase.Blocked;
            var goalFailed = goalOutcomeLedger?.Phase == GoalRunPhase.Failed;
            var goalStale = goalOutcomeLedger?.Phase == GoalRunPhase.Stale;
            var completenessBlocked = closure.Completeness is
            {
                ReadyForDelivery: false
            };
            task.State = !implementationAccepted
                         || completenessBlocked
                         || closure.VerificationAttempted && !closure.Passed
                         || proofFailed
                         || goalFailed
                ? TaskState.Failed
                : goalStale
                    ? TaskState.Stale
                : goalNotProven
                    ? TaskState.Paused
                    : TaskState.Completed;
            task.Stage = !implementationAccepted
                ? "未完成 · 没有真实文件变更"
                : completenessBlocked
                    ? "成果已保存 · 工程完整性未达到交付线"
                : closure.VerificationAttempted && !closure.Passed
                ? "成果已保存 · 验证未通过"
                : goalBlocked
                    ? "结果已保存 · 等待外部条件或用户权限"
                : goalStale
                    ? "证据已过期 · 需要重新验证当前工作区"
                : goalNotProven
                    ? "结果已保存 · 成功信号尚未全部证明"
                : proofFailed
                    ? "成果已保存 · 完成证明未通过"
                : closure.VerificationAttempted
                    ? "真实成果已验证并交付"
                    : "真实成果已交付 · 未自动验证";
            task.Progress = task.State == TaskState.Completed
                ? 100
                : Math.Min(92, Math.Max(1, OverallProgress));
            OverallProgress = task.Progress;
            CoreStatus = !implementationAccepted
                ? "INCOMPLETE"
                : completenessBlocked
                    ? "ENGINEERING NOT READY"
                : closure.VerificationAttempted && !closure.Passed
                    ? "VERIFY FAILED"
                    : goalNotProven
                        ? goalOutcomeLedger?.Phase.ToString().ToUpperInvariant()
                          ?? "UNVERIFIED"
                    : proofFailed
                        ? "PROOF FAILED"
                    : closure.Completeness?.ReadyForDelivery == true
                        ? "ENGINEERING READY"
                        : outcomeAssessment?.Status ?? "COMPLETE";
            CoreMessage = !implementationAccepted
                ? "模型没有修改任何文件，本轮不计为代码交付"
                : completenessBlocked
                    ? $"构建结果存在，但仍有 "
                      + $"{closure.Completeness!.Findings.Count(item => item.Severity == "BLOCKER")} "
                      + $"个工程阻塞项 · Completeness {closure.Completeness.Score}/100"
                : closure.VerificationAttempted && !closure.Passed
                ? "修改已保存，但自动验证仍未通过"
                : goalNotProven
                    ? $"目标结果已保存，但仅有 "
                      + $"{goalOutcomeLedger?.Signals.Count(item => item.Status == GoalSignalStatus.Pass) ?? 0}/"
                      + $"{goalOutcomeLedger?.Signals.Count ?? 0} 个成功信号获得独立证据；"
                      + "任务保持可恢复状态，不宣称完成。"
                : proofFailed
                    ? $"任务结果未满足完成契约 · Proof {outcomeAssessment?.ProofScore ?? 0}/100"
                : closure.Completeness?.ReadyForDelivery == true
                    ? $"这一步已经做稳 · {closure.Completeness.ChangedFileCount} 个变更文件 · "
                      + $"Completeness {closure.Completeness.Score}/100 · "
                      + $"{result.ToolCalls} 次工具调用"
                    : $"事情已经办妥 · {result.MutatingToolCalls} 次文件变更 · {result.ToolCalls} 次工具调用";
            CurrentStage = goalNotProven
                ? task.Stage
                : implementationAccepted
                    ? closure.Summary
                    : "请继续任务并明确要求 NOVA 在工作区落盘实现";
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          || !cancellationToken.IsCancellationRequested)
        {
            var failure = TaskFailureClassifier.Classify(
                task.Id,
                exception,
                task.Stage);
            await _failureLedger.RecordAsync(failure, CancellationToken.None);
            var budgetExceeded = failure.Kind == TaskFailureKind.Budget;
            var uncertainSideEffect =
                failure.Kind == TaskFailureKind.SideEffectUncertain;
            if (task.ExecutionMode == AgentExecutionMode.Goal)
            {
                await _goalOutcomes.SetPhaseAsync(
                    task.Id,
                    budgetExceeded || uncertainSideEffect
                        ? GoalRunPhase.Blocked
                        : GoalRunPhase.Failed,
                    uncertainSideEffect
                        ? $"副作用结果需要人工复核：{exception.Message}"
                        : budgetExceeded
                        ? $"执行预算在安全点耗尽：{exception.Message}"
                        : $"运行在安全阶段边界失败：{exception.Message}",
                    CancellationToken.None);
                LoadGoalMissionView(task.Id);
            }
            await RecordAgentBenchAsync(
                task,
                executionProvider,
                executionModel,
                isEngineeringTask,
                EngineeringTaskRouter.RequiresWorkspaceMutation(
                    conversationPrompt),
                uncertainSideEffect
                    ? "ACTION_REVIEW_REQUIRED"
                    : budgetExceeded
                        ? "BUDGET_EXHAUSTED"
                        : failure.Code,
                0,
                false,
                false,
                0,
                0,
                null,
                System.Diagnostics.Stopwatch.GetElapsedTime(runStartedAt),
                CancellationToken.None);
            task.State = uncertainSideEffect
                ? TaskState.Paused
                : budgetExceeded
                    ? TaskState.BudgetExhausted
                    : TaskState.Failed;
            task.Stage = uncertainSideEffect
                ? "动作结果待复核 · 已阻止自动重放"
                : budgetExceeded
                    ? "预算已用尽 · 可安全继续"
                    : $"这次没有顺利完成 · {failure.Title}";
            CoreStatus = uncertainSideEffect
                ? "ACTION REVIEW"
                : budgetExceeded
                    ? "BUDGET EXHAUSTED"
                    : failure.StatusLabel;
            CoreMessage = $"{failure.Title} · {failure.UserMessage}";
            CurrentStage = uncertainSideEffect
                ? "检查目标文件或外部系统后，再创建新的显式尝试"
                : budgetExceeded
                    ? "任务停在下一动作之前，恢复后预算会重新建立"
                    : failure.RecoveryLabel;
            AddActivity(
                uncertainSideEffect ? "副作用账本" : "AgentOS 资源治理",
                uncertainSideEffect
                    ? "需要人工复核"
                    : budgetExceeded
                        ? "预算硬限制已生效"
                        : "真实运行失败",
                $"{failure.Code} · {failure.UserMessage}",
                budgetExceeded || uncertainSideEffect
                    ? ActivityKind.Waiting
                    : ActivityKind.System);
            var failureTurn = await _conversationHistory.AppendAsync(
                task.Id,
                "system",
                uncertainSideEffect
                    ? $"本轮已暂停：副作用结果需要人工复核。\n\n{LimitDiagnostic(exception.Message, 1200)}"
                    : budgetExceeded
                        ? $"本轮停在安全点：{LimitDiagnostic(exception.Message, 1200)}\n\n可以使用“恢复所选任务”继续。"
                        : $"本轮没有完成：{failure.Title}\n\n"
                          + $"故障代码：`{failure.Code}`\n\n"
                          + $"{LimitDiagnostic(failure.UserMessage, 1200)}\n\n"
                          + $"建议操作：{failure.RecoveryLabel}\n\n"
                          + $"事件 ID：`{failure.IncidentId}`",
                CancellationToken.None);
            ConversationTurns.Add(failureTurn);
            UpdateConversationLabels(task.Id);
            UpdateBudgetStatus();
        }
    }

    private async Task<GoalMissionCharter> DiscoverGoalMissionAsync(
        TaskItem task,
        string rawGoal,
        TaskOutcomeContract preliminaryContract,
        AdaptiveContextPack? contextPack,
        EngineeringWorkspaceSnapshot snapshot,
        string provider,
        string model,
        CancellationToken cancellationToken)
    {
        var approved = await RequestToolApprovalAsync(
            task,
            new ToolApprovalRequest(
                "goal_mission_discovery",
                "开始目标探索并生成 Mission Charter？",
                "Goal 模式会先进行一次额外的只读模型探索，可能产生 Token 费用。"
                + "Explorer 可以读取工作区证据，但不能写文件、运行命令、操作应用、调用 MCP 或继续委派。"
                + "它会自行补齐可调查的未知项，不会用偏好问题打断你；只有权限、外部状态、不可逆风险或目标冲突才成为停止条件。",
                $"Explorer：{GetProviderLabel(provider)} · {model}\n"
                + $"原始目标：{LimitDiagnostic(rawGoal, 1000)}\n"
                + $"工作区：{snapshot.WorkspaceName}\n"
                + $"高信号文件：{contextPack?.Selections.Count ?? 0}"),
            cancellationToken);
        GoalMissionCharter charter;
        if (!approved)
        {
            charter = GoalMissionService.Fallback(
                task.Id,
                rawGoal,
                snapshot,
                "用户未授权额外的目标探索模型请求。");
            return await _goalMissions.SaveAsync(charter, cancellationToken);
        }

        CoreStatus = "GOAL EXPLORATION";
        CoreMessage = "正在把目标冻结为可验证结果";
        CurrentStage = "探索证据、未知项与最短解题路径";
        var runtime = GetRuntime(provider);
        var discovery = await runtime.RunAsync(
            new AgentRunRequest(
                $"{task.Id}-goal-explorer",
                GoalMissionService.BuildDiscoveryPrompt(
                    rawGoal,
                    preliminaryContract,
                    snapshot,
                    contextPack),
                WorkspaceRoot,
                _apiKeys[provider],
                provider,
                model,
                AgentExecutionMode.Ask,
                AllowParallelDelegation: false),
            async runtimeEvent =>
            {
                if (runtimeEvent.Kind == AgentRuntimeEventKind.TextDelta)
                {
                    return;
                }
                var mapped = runtimeEvent with
                {
                    Agent = $"Goal Explorer · {runtimeEvent.Agent}",
                    Kind = runtimeEvent.Kind == AgentRuntimeEventKind.Completed
                        ? AgentRuntimeEventKind.ToolCompleted
                        : runtimeEvent.Kind,
                    Progress = Math.Clamp(5 + runtimeEvent.Progress * .13, 5, 18),
                    ActiveUnits = 1
                };
                await HandleRuntimeEventAsync(task, mapped, cancellationToken);
            },
            _ => Task.FromResult(false),
            cancellationToken);
        try
        {
            charter = GoalMissionService.Parse(
                task.Id,
                rawGoal,
                discovery.FinalText);
        }
        catch (Exception exception) when (exception is JsonException
                                          or InvalidOperationException)
        {
            charter = GoalMissionService.Fallback(
                task.Id,
                rawGoal,
                snapshot,
                exception.Message);
            AddActivity(
                "Goal Explorer",
                "结构化结果不可用，已安全降级",
                exception.Message,
                ActivityKind.System);
        }
        return await _goalMissions.SaveAsync(charter, cancellationToken);
    }

    private string BuildGoalRuntimePrompt(
        string conversationPrompt,
        GoalMissionCharter mission,
        TaskOutcomeContract outcomeContract,
        AdaptiveContextPack? contextPack,
        EngineeringWorkspaceSnapshot snapshot)
    {
        var missionText = GoalMissionService.Format(mission);
        if (mission.RequiresWorkspaceChange)
        {
            return
                $"""
                [NOVA GOAL MODE]
                {missionText}

                The Mission Charter is the result contract, not a suggestion.
                Investigate unknowns autonomously from local evidence. Prefer the shortest strategy that can satisfy
                every success signal. Do not ask the user for preferences already resolvable from the workspace.
                Stop only at a listed stop condition or a real approval boundary.

                {BuildEngineeringRuntimePrompt(
                    conversationPrompt,
                    outcomeContract,
                    contextPack,
                    snapshot)}
                """;
        }

        var context = contextPack is null
            ? "[NOVA ADAPTIVE CONTEXT PACK]\nNo context pack was generated."
            : AdaptiveContextCompilerService.FormatForPrompt(contextPack);
        return
            $"""
            [NOVA GOAL MODE]
            {missionText}

            {TaskOutcomeContractService.FormatForPrompt(outcomeContract)}
            {context}

            Policy: {AgentExecutionPolicy.GetSystemContract(AgentExecutionMode.Goal)}
            Drive the task toward the observable outcome. Investigate unknowns using evidence before committing to a
            conclusion. Map the final result to every success signal, explicitly label remaining uncertainty, and do
            not confuse activity with achievement.
            {(mission.ExecutionKind == "RESEARCH"
                ? "This mission is research-only: do not modify files or perform external actions."
                : "Use external or desktop actions only when they are necessary to the outcome and separately approved.")}

            CONVERSATION:
            {conversationPrompt}
            """;
    }

    private async Task<AgentMeshExecutionOutcome?> TryRunAgentMeshAsync(
        TaskItem task,
        string runtimePrompt,
        TaskOutcomeContract outcomeContract,
        AdaptiveContextPack? contextPack,
        EngineeringWorkspaceSnapshot sourceSnapshot,
        string executionProvider,
        string executionModel,
        CancellationToken cancellationToken)
    {
        if (!sourceSnapshot.IsGitRepository || sourceSnapshot.ChangedFiles.Count > 0)
        {
            return null;
        }

        var plannerApproved = await RequestToolApprovalAsync(
            task,
            new ToolApprovalRequest(
                "agent_mesh_plan",
                "让 Agent Mesh 规划多 Agent 协作 DAG？",
                "这会向当前模型发送一次额外的只读规划请求并产生 Token 费用。"
                + "规划器只能返回 2–4 个工作包、文件所有权和依赖关系，不能修改文件或运行命令。"
                + "规划失败、没有真实并行波次或所有权重叠时，NOVA 会回退到 Worktree Tournament。",
                $"规划模型：{GetProviderLabel(executionProvider)} · {executionModel}\n"
                + $"Context Pack：{contextPack?.Selections.Count ?? 0} 个文件\n"
                + $"完成契约：{outcomeContract.Criteria.Count} 项"),
            cancellationToken);
        if (!plannerApproved)
        {
            return null;
        }

        CoreStatus = "MESH PLANNING";
        CoreMessage = "正在划分工作包与文件所有权";
        var plannerRuntime = GetRuntime(executionProvider);
        var plannerPrompt = AgentMeshPlannerService.BuildPrompt(
            task.Description,
            outcomeContract,
            contextPack,
            sourceSnapshot);
        var plannerResult = await plannerRuntime.RunAsync(
            new AgentRunRequest(
                $"{task.Id}-mesh-planner",
                plannerPrompt,
                WorkspaceRoot,
                _apiKeys[executionProvider],
                executionProvider,
                executionModel,
                AgentExecutionMode.Ask,
                AllowParallelDelegation: false),
            async runtimeEvent =>
            {
                if (runtimeEvent.Kind == AgentRuntimeEventKind.TextDelta)
                {
                    return;
                }
                var mapped = runtimeEvent with
                {
                    Agent = $"Mesh Planner · {runtimeEvent.Agent}",
                    Kind = runtimeEvent.Kind == AgentRuntimeEventKind.Completed
                        ? AgentRuntimeEventKind.ToolCompleted
                        : runtimeEvent.Kind,
                    Progress = Math.Clamp(8 + runtimeEvent.Progress * .1, 8, 18),
                    ActiveUnits = 1
                };
                await HandleRuntimeEventAsync(task, mapped, cancellationToken);
            },
            _ => Task.FromResult(false),
            cancellationToken);

        AgentMeshPlan plan;
        try
        {
            plan = AgentMeshPlannerService.Parse(plannerResult.FinalText);
        }
        catch (Exception exception) when (exception is JsonException
                                          or InvalidOperationException)
        {
            AddActivity(
                "Mesh Planner",
                "规划不满足安全约束",
                $"{exception.Message} · 将回退到 Worktree Tournament。",
                ActivityKind.System);
            return null;
        }

        var assignments = plan.Packages
            .Select((package, index) =>
            {
                var (provider, model) = ResolveMeshWorker(
                    index,
                    executionProvider,
                    executionModel);
                return (Package: package, Provider: provider, Model: model);
            })
            .ToArray();
        var canVerify = sourceSnapshot.VerificationCommand is not
            ("NO VERIFICATION TARGET" or "MANUAL VERIFICATION REQUIRED");
        var crossProvider = assignments
            .Select(item => item.Provider)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() > 1;
        var meshApproved = await RequestToolApprovalAsync(
            task,
            new ToolApprovalRequest(
                "start_agent_mesh",
                $"启动 {plan.Packages.Count} 个所有权隔离的 Mesh Agent？",
                $"执行计划包含 {plan.BuildWaves().Count} 个依赖波次。"
                + "每个 Agent 只允许写入列出的文件所有权范围；越权写入会在工具层拒绝。"
                + "工作包在隔离 Worktree 中执行，每一波合并到独立 Integration Worktree 后，"
                + "下一波才会看到真实的前序实现。模型发起的命令、MCP、桌面、网络、计划任务和继续委派一律拒绝。"
                + (canVerify
                    ? $"最终集成会运行：{sourceSnapshot.VerificationCommand}。项目构建 Target 可能执行代码。"
                    : "当前没有自动验证目标，集成将依赖本地审查与 Council。")
                + (crossProvider
                    ? "工作包会跨多个已配置提供商分配，目标、上下文和对应工作包将发送给对应模型服务。"
                    : "当前只使用一个提供商，但每个工作包保持独立上下文。")
                + "该流程会产生额外 Token、CPU、磁盘和构建成本；Combined Patch 进入主工作区前仍需再次确认。",
                AgentMeshPlannerService.Format(plan)
                + Environment.NewLine
                + Environment.NewLine
                + string.Join(
                    Environment.NewLine,
                    assignments.Select(item =>
                        $"{item.Package.Id} => {GetProviderLabel(item.Provider)} · {item.Model}"))),
            cancellationToken);
        if (!meshApproved)
        {
            AddActivity(
                "Agent Mesh",
                "协作执行已跳过",
                "用户保留了规划但拒绝额外实现 Agent；将回退到 Worktree Tournament。",
                ActivityKind.System);
            return null;
        }

        CoreStatus = "AGENT MESH";
        CoreMessage = $"{plan.Packages.Count} 个工作包按依赖波次执行";
        CurrentStage = "所有权隔离的并行工程协作";
        AddActivity(
            "Agent Mesh",
            $"启动 {plan.Packages.Count} 个实现 Agent",
            $"{plan.BuildWaves().Count} waves · "
            + string.Join(
                " · ",
                plan.Packages.Select(item =>
                    $"{item.Id}[{string.Join(",", item.OwnedPaths)}]")),
            ActivityKind.Working);

        AgentMeshRunResult mesh;
        try
        {
            mesh = await _agentMesh.RunAsync(
                WorkspaceRoot,
                task.Id,
                plan,
                async (package, packageRoot, waveIndex, waveCount, token) =>
                {
                    var assignment = assignments.First(item =>
                        item.Package.Id.Equals(
                            package.Id,
                            StringComparison.OrdinalIgnoreCase));
                    var packageRuntime = GetRuntime(assignment.Provider);
                    var packagePrompt =
                        $"""
                        {runtimePrompt}

                        AGENT MESH WORK PACKAGE:
                        ID: {package.Id}
                        Title: {package.Title}
                        Wave: {waveIndex + 1}/{waveCount}
                        Active isolated workspace: {packageRoot}
                        Exclusive write ownership: {string.Join(", ", package.OwnedPaths)}
                        Dependencies already integrated into this workspace: {string.Join(", ", package.DependsOn)}

                        Assignment:
                        {package.Instruction}

                        Implement only this work package. You may read the repository, but write only inside the exact
                        ownership scopes. Do not run commands, call MCP, use desktop controls, access external networks,
                        create schedules, delegate agents, commit, merge, or edit generated/dependency output.
                        Do not modify another package's integration files even if that seems convenient; report the
                        dependency in your final response instead. Produce real files, not just an implementation plan.
                        """;
                    return await packageRuntime.RunAsync(
                        new AgentRunRequest(
                            $"{task.Id}-mesh-{package.Id}",
                            packagePrompt,
                            packageRoot,
                            _apiKeys[assignment.Provider],
                            assignment.Provider,
                            assignment.Model,
                            AgentExecutionMode.Build,
                            AllowParallelDelegation: false,
                            AllowedWriteScopes: package.OwnedPaths),
                        async runtimeEvent =>
                        {
                            if (runtimeEvent.Kind == AgentRuntimeEventKind.TextDelta)
                            {
                                return;
                            }
                            var waveStart = 20 + waveIndex * (42d / waveCount);
                            var waveSpan = 42d / waveCount;
                            var mapped = runtimeEvent with
                            {
                                Agent = $"Mesh Worker {package.Id} · {runtimeEvent.Agent}",
                                Kind = runtimeEvent.Kind == AgentRuntimeEventKind.Completed
                                    ? AgentRuntimeEventKind.ToolCompleted
                                    : runtimeEvent.Kind,
                                Progress = Math.Clamp(
                                    waveStart + runtimeEvent.Progress * waveSpan / 100,
                                    waveStart,
                                    waveStart + waveSpan),
                                ActiveUnits = plan.BuildWaves()[waveIndex].Count
                            };
                            await HandleRuntimeEventAsync(task, mapped, token);
                        },
                        packageApproval => Task.FromResult(
                            packageApproval.ToolName is
                                "write_text_file" or "replace_text_in_file"),
                        token);
                },
                canVerify,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AddActivity(
                "Agent Mesh",
                "协作集成失败",
                exception.Message,
                ActivityKind.System);
            throw;
        }

        foreach (var package in mesh.Packages)
        {
            AddActivity(
                $"Mesh {package.Package.Id}",
                $"{package.Status} · +{package.Additions}/-{package.Deletions}",
                $"{package.AgentResult?.Provider} · {package.AgentResult?.Model} · "
                + package.Detail,
                package.Status == "READY" ? ActivityKind.Completed : ActivityKind.System);
        }
        AddActivity(
            "Mesh Integration",
            mesh.IsEligible ? "集成验证通过" : "集成资格未通过",
            mesh.Verification is null
                ? $"无自动验证目标 · local review {mesh.Review.Score}/100"
                : $"{mesh.Verification.Command} · exit {mesh.Verification.ExitCode} · "
                  + $"local review {mesh.Review.Score}/100",
            mesh.IsEligible ? ActivityKind.Completed : ActivityKind.System);
        var integrationEvent = new AgentRuntimeEvent(
            AgentRuntimeEventKind.ToolCompleted,
            "Mesh Integrator",
            "依赖波次已合并",
            $"{mesh.Packages.Count} packages · +{mesh.Additions}/-{mesh.Deletions}",
            68,
            1);
        await HandleRuntimeEventAsync(task, integrationEvent, cancellationToken);

        var judgeProvider = executionProvider == "openai" && HasProviderKey("deepseek")
            ? "deepseek"
            : executionProvider == "deepseek" && HasProviderKey("openai")
                ? "openai"
                : executionProvider;
        var judgeModel = judgeProvider switch
        {
            "deepseek" => "deepseek-v4-pro",
            "openai" => "gpt-5.6-terra",
            _ => executionModel
        };
        AgentMeshCouncilDecision decision;
        if (!mesh.IsEligible)
        {
            decision = new AgentMeshCouncilDecision(
                judgeProvider,
                judgeModel,
                "REJECT",
                100,
                "集成验证或工作包资格闸门未通过，Council 未产生额外模型费用。",
                "VERDICT: REJECT\nCONFIDENCE: 100\n"
                + "SUMMARY: Agent Mesh integration gate failed.",
                DateTimeOffset.Now);
        }
        else
        {
            CoreStatus = "MESH COUNCIL";
            CoreMessage = "独立 Council 正在审查 Combined Patch";
            var judgeRuntime = GetRuntime(judgeProvider);
            var judgeResult = await judgeRuntime.RunAsync(
                new AgentRunRequest(
                    $"{task.Id}-mesh-council",
                    AgentMeshCouncilService.BuildPrompt(
                        task.Description,
                        outcomeContract,
                        mesh),
                    WorkspaceRoot,
                    _apiKeys[judgeProvider],
                    judgeProvider,
                    judgeModel,
                    AgentExecutionMode.Ask,
                    AllowParallelDelegation: false),
                async runtimeEvent =>
                {
                    if (runtimeEvent.Kind == AgentRuntimeEventKind.TextDelta)
                    {
                        return;
                    }
                    var mapped = runtimeEvent with
                    {
                        Agent = $"Mesh Council · {runtimeEvent.Agent}",
                        Kind = runtimeEvent.Kind == AgentRuntimeEventKind.Completed
                            ? AgentRuntimeEventKind.ToolCompleted
                            : runtimeEvent.Kind,
                        Progress = Math.Clamp(
                            70 + runtimeEvent.Progress * .14,
                            70,
                            84),
                        ActiveUnits = 1
                    };
                    await HandleRuntimeEventAsync(task, mapped, cancellationToken);
                },
                _ => Task.FromResult(false),
                cancellationToken);
            decision = AgentMeshCouncilService.Parse(
                judgeProvider,
                judgeModel,
                judgeResult.FinalText);
        }
        AddActivity(
            "Agent Mesh Council",
            $"{decision.Verdict} · confidence {decision.Confidence}%",
            decision.Summary,
            decision.Accepted ? ActivityKind.Completed : ActivityKind.System);

        var applied = false;
        try
        {
            if (decision.Accepted)
            {
                var mergeApproved = await RequestToolApprovalAsync(
                    task,
                    new ToolApprovalRequest(
                        "apply_agent_mesh",
                        "应用 Agent Mesh Combined Patch？",
                        $"{mesh.Packages.Count} 个所有权隔离工作包已经在独立 Integration Worktree 中合并。"
                        + $"验证：{(mesh.Verification?.Passed == true ? "通过" : "未自动验证")}；"
                        + $"本地审查：{mesh.Review.Score}/100；Council：{decision.Verdict}。"
                        + "NOVA 会重新校验主工作区仍处于同一个干净 HEAD，再应用 Patch；不会自动提交。",
                        $"Patch SHA-256：{mesh.CombinedPatchSha256}\n"
                        + $"变更：+{mesh.Additions} / -{mesh.Deletions}\n"
                        + $"Waves：{mesh.Waves.Count}",
                        "patch",
                        mesh.CombinedPatch,
                        mesh.Additions,
                        mesh.Deletions),
                    cancellationToken);
                if (mergeApproved)
                {
                    var apply = await _agentMesh.ApplyAsync(mesh, cancellationToken);
                    applied = apply.Applied;
                    AddActivity(
                        "Mesh Merge Gate",
                        applied ? "Combined Patch 已应用" : "Combined Patch 被拦截",
                        apply.Detail,
                        applied ? ActivityKind.Completed : ActivityKind.System);
                }
                else
                {
                    AddActivity(
                        "Mesh Merge Gate",
                        "Combined Patch 未应用",
                        "工作包、Patch 和 Council Decision 已保留，主工作区保持不变。",
                        ActivityKind.System);
                }
            }
        }
        finally
        {
            try
            {
                await _agentMesh.PersistDecisionAsync(
                    mesh,
                    decision,
                    applied,
                    CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or NotSupportedException
                                              or System.Security.SecurityException)
            {
                AddActivity(
                    "Agent Mesh",
                    "Decision 账本保存失败",
                    exception.Message,
                    ActivityKind.System);
            }
            finally
            {
                try
                {
                    await _agentMesh.CleanupAsync(mesh, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    AddActivity(
                        "Agent Mesh",
                        "Integration Worktree 待手动回收",
                        exception.Message,
                        ActivityKind.System);
                }
            }
        }

        var verificationCouncil = new VerificationCouncilResult(
            decision.Provider,
            decision.Model,
            applied && decision.Accepted ? "PASS" : "CONCERNS",
            decision.Confidence,
            applied
                ? "Agent Mesh Council 接受 Combined Patch，且已经用户授权应用。"
                : decision.Summary,
            decision.RawResponse,
            decision.CompletedAt);
        var finalText =
            $"""
            # Agent Mesh

            {AgentMeshPlannerService.Format(plan)}

            {AgentMeshCouncilService.Format(decision)}

            Packages:
            {string.Join(
                Environment.NewLine,
                mesh.Packages.Select(item =>
                    $"- {item.Package.Id}: {item.Status} · +{item.Additions}/-{item.Deletions} · {item.AgentResult?.FinalText}"))}

            Merge Gate: {(applied ? "Combined Patch 已应用到主工作区，尚未提交。" : "主工作区未修改。")}
            Mesh evidence: {mesh.ArtifactDirectory}
            """;
        var totalTools = mesh.Packages.Sum(item => item.AgentResult?.ToolCalls ?? 0);
        var totalMutations = mesh.Packages.Sum(item =>
            item.AgentResult?.MutatingToolCalls ?? 0);
        var result = new AgentRunResult(
            mesh.MeshId,
            finalText,
            totalTools,
            executionProvider,
            executionModel)
        {
            MutatingToolCalls = applied ? Math.Max(1, totalMutations) : 0
        };
        return new AgentMeshExecutionOutcome(
            mesh,
            decision,
            result,
            verificationCouncil,
            applied);
    }

    private (string Provider, string Model) ResolveMeshWorker(
        int packageIndex,
        string executionProvider,
        string executionModel)
    {
        if (packageIndex % 2 == 1)
        {
            if (executionProvider == "openai" && HasProviderKey("deepseek"))
            {
                return ("deepseek", "deepseek-v4-pro");
            }
            if (executionProvider == "deepseek" && HasProviderKey("openai"))
            {
                return ("openai", "gpt-5.6-terra");
            }
        }
        return (executionProvider, executionModel);
    }

    private static string FormatAgentMeshOutcome(AgentMeshExecutionOutcome outcome)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Mesh: {outcome.Mesh.MeshId}");
        builder.AppendLine($"Base HEAD: {outcome.Mesh.BaseHead}");
        builder.AppendLine($"Integration HEAD: {outcome.Mesh.IntegrationHead}");
        builder.AppendLine($"Waves: {outcome.Mesh.Waves.Count}");
        builder.AppendLine($"Decision: {outcome.Decision.Verdict}");
        builder.AppendLine($"Applied: {outcome.Applied}");
        builder.AppendLine();
        builder.AppendLine(AgentMeshPlannerService.Format(outcome.Mesh.Plan));
        builder.AppendLine();
        foreach (var package in outcome.Mesh.Packages)
        {
            builder.AppendLine(
                $"{package.Package.Id} · {package.AgentResult?.Provider}/{package.AgentResult?.Model} · "
                + $"{package.Status} · +{package.Additions}/-{package.Deletions}");
        }
        builder.AppendLine();
        builder.AppendLine(AgentMeshCouncilService.Format(outcome.Decision));
        return builder.ToString();
    }

    private async Task<TournamentExecutionOutcome?> TryRunWorktreeTournamentAsync(
        TaskItem task,
        string runtimePrompt,
        TaskOutcomeContract outcomeContract,
        EngineeringWorkspaceSnapshot sourceSnapshot,
        string executionProvider,
        string executionModel,
        CancellationToken cancellationToken)
    {
        if (!sourceSnapshot.IsGitRepository)
        {
            AddActivity(
                "Worktree Tournament",
                "已回退到标准主 Agent",
                "当前工作区不是 Git 仓库，无法建立同源隔离候选。",
                ActivityKind.System);
            return null;
        }
        if (sourceSnapshot.ChangedFiles.Count > 0)
        {
            AddActivity(
                "Worktree Tournament",
                "已回退到标准主 Agent",
                $"主工作区已有 {sourceSnapshot.ChangedFiles.Count} 个未提交变更；"
                + "为避免候选忽略或覆盖用户内容，本轮不创建竞赛。",
                ActivityKind.System);
            return null;
        }

        var specs = BuildTournamentCandidates(executionProvider, executionModel);
        var canVerify = sourceSnapshot.VerificationCommand is not
            ("NO VERIFICATION TARGET" or "MANUAL VERIFICATION REQUIRED");
        var crossProvider = specs
            .Select(item => item.Provider)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() > 1;
        var approved = await RequestToolApprovalAsync(
            task,
            new ToolApprovalRequest(
                "start_worktree_tournament",
                $"启动 {specs.Count} 路隔离实现竞赛？",
                "NOVA 将从当前已提交 HEAD 创建独立 Worktree，并发运行候选实现，"
                + "这会产生额外 Token、CPU、磁盘和构建成本。候选只能在各自隔离目录自动写文件；"
                + "模型发起的命令、MCP、桌面、网络和计划任务操作一律拒绝。"
                + (canVerify
                    ? "下方验证命令会由 NOVA 在每个隔离候选中执行，项目构建 Target 可能运行代码。"
                    : "当前未识别到自动验证目标，将使用 Patch 和本地静态审查比选。")
                + (crossProvider
                    ? "目标、Context Pack 和候选 Patch 将跨多个已配置提供商处理。"
                    : "当前只有一个可用提供商，将使用隔离上下文和不同实现策略。")
                + "Council 选出 Winner 后，应用到主工作区仍会再次显示完整 Patch 并单独确认。",
                string.Join(
                    Environment.NewLine,
                    specs.Select(item =>
                        $"{item.Id} · {GetProviderLabel(item.Provider)} · {item.Model} · {item.Strategy}"))
                + Environment.NewLine
                + $"验证：{sourceSnapshot.VerificationCommand}"),
            cancellationToken);
        if (!approved)
        {
            AddActivity(
                "Worktree Tournament",
                "竞赛已跳过",
                "用户拒绝额外候选执行；标准主 Agent 将继续。",
                ActivityKind.System);
            return null;
        }

        CoreStatus = "TOURNAMENT";
        CoreMessage = $"正在构建 {specs.Count} 个隔离候选";
        CurrentStage = "候选 Worktree 并行实现";
        AddActivity(
            "Worktree Tournament",
            $"启动 {specs.Count} 个候选实现",
            "所有候选基于同一个提交，主工作区保持只读。",
            ActivityKind.Working);

        WorktreeTournamentResult tournament;
        try
        {
            tournament = await _worktreeTournament.RunAsync(
                WorkspaceRoot,
                task.Id,
                specs,
                async (spec, candidateRoot, token) =>
                {
                    var candidateRuntime = GetRuntime(spec.Provider);
                    var candidatePrompt =
                        $"""
                        {runtimePrompt}

                        WORKTREE TOURNAMENT CANDIDATE:
                        Candidate ID: {spec.Id}
                        Strategy: {spec.Strategy}
                        Active isolated workspace: {candidateRoot}

                        Implement the frozen goal completely in this isolated workspace.
                        You may read files and write only the necessary workspace files.
                        Do not run commands, call MCP, operate desktop applications, create schedules,
                        delegate more agents, commit, merge, or access the source workspace.
                        Do not merely describe a solution: produce a coherent candidate implementation.
                        Another judge will compare your patch and verification evidence against another candidate.
                        """;
                    return await candidateRuntime.RunAsync(
                        new AgentRunRequest(
                            $"{task.Id}-tournament-{spec.Id}",
                            candidatePrompt,
                            candidateRoot,
                            _apiKeys[spec.Provider],
                            spec.Provider,
                            spec.Model,
                            AgentExecutionMode.Build,
                            AllowParallelDelegation: false),
                        async runtimeEvent =>
                        {
                            if (runtimeEvent.Kind == AgentRuntimeEventKind.TextDelta)
                            {
                                return;
                            }
                            var mapped = runtimeEvent with
                            {
                                Agent = $"候选 {spec.Id} · {runtimeEvent.Agent}",
                                Kind = runtimeEvent.Kind == AgentRuntimeEventKind.Completed
                                    ? AgentRuntimeEventKind.ToolCompleted
                                    : runtimeEvent.Kind,
                                Progress = Math.Clamp(
                                    22 + runtimeEvent.Progress * .48,
                                    22,
                                    70),
                                ActiveUnits = specs.Count
                            };
                            await HandleRuntimeEventAsync(task, mapped, token);
                        },
                        candidateApproval => Task.FromResult(
                            candidateApproval.ToolName is
                                "write_text_file" or "replace_text_in_file"),
                        token);
                },
                canVerify,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AddActivity(
                "Worktree Tournament",
                "隔离竞赛启动失败",
                exception.Message,
                ActivityKind.System);
            throw;
        }

        var eligible = tournament.Candidates
            .Where(item => item.IsEligible)
            .ToArray();
        foreach (var candidate in tournament.Candidates)
        {
            AddActivity(
                $"候选 {candidate.Spec.Id}",
                $"{candidate.Status} · +{candidate.Additions}/-{candidate.Deletions}",
                candidate.Verification is null
                    ? $"{candidate.Detail} · review {candidate.Review?.Score ?? 0}/100"
                    : $"{candidate.Detail} · {candidate.Verification.Command} · "
                      + $"review {candidate.Review?.Score ?? 0}/100",
                candidate.IsEligible ? ActivityKind.Completed : ActivityKind.System);
        }
        var arenaEvent = new AgentRuntimeEvent(
            AgentRuntimeEventKind.ToolCompleted,
            "候选验证竞技场",
            "隔离验证与静态审查完成",
            $"{eligible.Length}/{tournament.Candidates.Count} 个候选进入 Council",
            71,
            1);
        await HandleRuntimeEventAsync(task, arenaEvent, cancellationToken);

        TournamentCouncilDecision decision;
        var judgeProvider = executionProvider == "openai" && HasProviderKey("deepseek")
            ? "deepseek"
            : executionProvider == "deepseek" && HasProviderKey("openai")
                ? "openai"
                : executionProvider;
        var judgeModel = judgeProvider switch
        {
            "deepseek" => "deepseek-v4-pro",
            "openai" => "gpt-5.6-terra",
            _ => executionModel
        };
        if (eligible.Length == 0)
        {
            decision = new TournamentCouncilDecision(
                judgeProvider,
                judgeModel,
                "NONE",
                "REJECT",
                100,
                "没有候选同时满足真实修改与隔离验证要求。",
                "WINNER: NONE\nVERDICT: REJECT\nCONFIDENCE: 100\n"
                + "SUMMARY: No eligible candidate.",
                DateTimeOffset.Now);
        }
        else
        {
            CoreStatus = "JUDGING";
            CoreMessage = "Tournament Council 正在对比候选证据";
            var judgeRuntime = GetRuntime(judgeProvider);
            var judgePrompt = TournamentCouncilService.BuildPrompt(
                task.Description,
                outcomeContract,
                tournament);
            var judgeResult = await judgeRuntime.RunAsync(
                new AgentRunRequest(
                    $"{task.Id}-tournament-council",
                    judgePrompt,
                    WorkspaceRoot,
                    _apiKeys[judgeProvider],
                    judgeProvider,
                    judgeModel,
                    AgentExecutionMode.Ask,
                    AllowParallelDelegation: false),
                async runtimeEvent =>
                {
                    if (runtimeEvent.Kind == AgentRuntimeEventKind.TextDelta)
                    {
                        return;
                    }
                    var mapped = runtimeEvent with
                    {
                        Agent = $"Tournament Council · {runtimeEvent.Agent}",
                        Kind = runtimeEvent.Kind == AgentRuntimeEventKind.Completed
                            ? AgentRuntimeEventKind.ToolCompleted
                            : runtimeEvent.Kind,
                        Progress = Math.Clamp(
                            72 + runtimeEvent.Progress * .14,
                            72,
                            86),
                        ActiveUnits = 1
                    };
                    await HandleRuntimeEventAsync(task, mapped, cancellationToken);
                },
                _ => Task.FromResult(false),
                cancellationToken);
            decision = TournamentCouncilService.Parse(
                judgeProvider,
                judgeModel,
                judgeResult.FinalText,
                eligible.Select(item => item.Spec.Id).ToArray());
        }

        AddActivity(
            "Tournament Council",
            $"{decision.Verdict} · Winner {decision.WinnerId}",
            $"{GetProviderLabel(decision.Provider)} · confidence {decision.Confidence}% · "
            + decision.Summary,
            decision.Selected ? ActivityKind.Completed : ActivityKind.System);

        var applied = false;
        TournamentCandidateResult? winner = null;
        try
        {
            if (decision.Selected)
            {
                winner = eligible.FirstOrDefault(item =>
                    item.Spec.Id.Equals(
                        decision.WinnerId,
                        StringComparison.OrdinalIgnoreCase));
            }
            if (winner is not null)
            {
                var mergeApproved = await RequestToolApprovalAsync(
                    task,
                    new ToolApprovalRequest(
                        "apply_tournament_winner",
                        $"应用 Winner {winner.Spec.Id} 到主工作区？",
                        $"{GetProviderLabel(winner.Spec.Provider)} · {winner.Spec.Model}；"
                        + $"隔离验证：{(winner.Verification?.Passed == true ? "通过" : "未自动验证")}；"
                        + $"本地审查：{winner.Review?.Score ?? 0}/100。"
                        + "NOVA 会先校验主工作区仍处于同一个干净 HEAD，再应用 Patch；不会自动提交。",
                        $"Patch SHA-256：{winner.PatchSha256}\n"
                        + $"变更：+{winner.Additions} / -{winner.Deletions}",
                        "patch",
                        winner.Patch,
                        winner.Additions,
                        winner.Deletions),
                    cancellationToken);
                if (mergeApproved)
                {
                    var apply = await _worktreeTournament.ApplyWinnerAsync(
                        tournament,
                        winner.Spec.Id,
                        cancellationToken);
                    applied = apply.Applied;
                    AddActivity(
                        "Merge Gate",
                        applied ? "Winner Patch 已应用" : "Winner Patch 被安全拦截",
                        apply.Detail,
                        applied ? ActivityKind.Completed : ActivityKind.System);
                    var mergeEvent = new AgentRuntimeEvent(
                        applied
                            ? AgentRuntimeEventKind.ToolCompleted
                            : AgentRuntimeEventKind.Message,
                        "Merge Gate",
                        applied ? "Winner Patch 已应用" : "Winner Patch 被安全拦截",
                        apply.Detail,
                        88,
                        1);
                    await HandleRuntimeEventAsync(task, mergeEvent, cancellationToken);
                }
                else
                {
                    AddActivity(
                        "Merge Gate",
                        "Winner Patch 未应用",
                        "候选证据和 Patch 已保留为交付物，主工作区保持不变。",
                        ActivityKind.System);
                    var mergeEvent = new AgentRuntimeEvent(
                        AgentRuntimeEventKind.Message,
                        "Merge Gate",
                        "Winner Patch 未应用",
                        "用户拒绝合并；主工作区保持不变。",
                        88,
                        1);
                    await HandleRuntimeEventAsync(task, mergeEvent, cancellationToken);
                }
            }
        }
        finally
        {
            try
            {
                await _worktreeTournament.PersistDecisionAsync(
                    tournament,
                    decision,
                    applied,
                    CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or NotSupportedException
                                              or System.Security.SecurityException)
            {
                AddActivity(
                    "Worktree Tournament",
                    "Decision 账本保存失败",
                    exception.Message,
                    ActivityKind.System);
            }
            finally
            {
                await _worktreeTournament.CleanupAsync(
                    tournament,
                    CancellationToken.None);
            }
        }

        var verificationCouncil = new VerificationCouncilResult(
            decision.Provider,
            decision.Model,
            applied && decision.Selected ? "PASS" : "CONCERNS",
            decision.Confidence,
            applied
                ? $"Tournament Council 选择 {decision.WinnerId}，Winner Patch 已经用户授权应用。"
                : decision.Summary,
            decision.RawResponse,
            decision.CompletedAt);
        var finalText =
            $"""
            # Worktree Tournament

            {TournamentCouncilService.Format(decision)}

            {(winner?.AgentResult?.FinalText ?? "没有候选被选中。")}

            Merge Gate: {(applied ? "Winner Patch 已应用到主工作区，尚未提交。" : "主工作区未修改。")}
            Tournament evidence: {tournament.ArtifactDirectory}
            """;
        var totalToolCalls = tournament.Candidates.Sum(item =>
            item.AgentResult?.ToolCalls ?? 0);
        var result = new AgentRunResult(
            tournament.TournamentId,
            finalText,
            totalToolCalls,
            winner?.Spec.Provider ?? executionProvider,
            winner?.Spec.Model ?? executionModel)
        {
            MutatingToolCalls = applied
                ? Math.Max(1, winner?.AgentResult?.MutatingToolCalls ?? 0)
                : 0
        };
        return new TournamentExecutionOutcome(
            tournament,
            decision,
            result,
            verificationCouncil,
            applied);
    }

    private IReadOnlyList<TournamentCandidateSpec> BuildTournamentCandidates(
        string executionProvider,
        string executionModel)
    {
        var candidates = new List<TournamentCandidateSpec>
        {
            new(
                "candidate-a",
                executionProvider,
                executionModel,
                "最小风险实现：优先复用现有架构，以最小连贯变更满足完成契约。")
        };
        if (executionProvider == "openai" && HasProviderKey("deepseek"))
        {
            candidates.Add(new TournamentCandidateSpec(
                "candidate-b",
                "deepseek",
                "deepseek-v4-pro",
                "独立重构实现：寻找更清晰的边界、失败恢复和验证路径。"));
        }
        else if (executionProvider == "deepseek" && HasProviderKey("openai"))
        {
            candidates.Add(new TournamentCandidateSpec(
                "candidate-b",
                "openai",
                "gpt-5.6-terra",
                "独立重构实现：寻找更清晰的边界、失败恢复和验证路径。"));
        }
        else
        {
            candidates.Add(new TournamentCandidateSpec(
                "candidate-b",
                executionProvider,
                executionModel,
                "反方实现：从边界条件、可测试性和最坏失败模式重新设计最小方案。"));
        }
        return candidates;
    }

    private static string FormatTournamentOutcome(TournamentExecutionOutcome outcome)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Tournament: {outcome.Tournament.TournamentId}");
        builder.AppendLine($"Base HEAD: {outcome.Tournament.BaseHead}");
        builder.AppendLine($"Decision: {outcome.Decision.Verdict}");
        builder.AppendLine($"Winner: {outcome.Decision.WinnerId}");
        builder.AppendLine($"Applied: {outcome.Applied}");
        builder.AppendLine();
        foreach (var candidate in outcome.Tournament.Candidates)
        {
            builder.AppendLine(
                $"{candidate.Spec.Id} · {candidate.Spec.Provider}/{candidate.Spec.Model} · "
                + $"{candidate.Status} · +{candidate.Additions}/-{candidate.Deletions} · "
                + $"verify {candidate.Verification?.Passed.ToString() ?? "N/A"} · "
                + $"review {candidate.Review?.Score.ToString() ?? "N/A"}");
        }
        builder.AppendLine();
        builder.AppendLine(TournamentCouncilService.Format(outcome.Decision));
        return builder.ToString();
    }

    private async Task<EngineeringClosureResult> RunEngineeringClosureAsync(
        TaskItem task,
        IAgentRuntime runtime,
        AgentRunResult initialResult,
        TaskOutcomeContract? outcomeContract,
        EngineeringWorkspaceSnapshot baselineSnapshot,
        string executionProvider,
        string executionModel,
        string executionApiKey,
        CancellationToken cancellationToken)
    {
        var maximumRepairRounds = Math.Max(
            1,
            AgentBudgetPolicy.ForMode(SelectedExecutionMode).MaxRepairRounds);
        var result = initialResult;
        var snapshot = await _engineeringWorkspace.InspectAsync(WorkspaceRoot, cancellationToken);

        for (var repairRound = 0; repairRound <= maximumRepairRounds; repairRound++)
        {
            var hasVerificationTarget = snapshot.VerificationCommand
                is not ("NO VERIFICATION TARGET" or "MANUAL VERIFICATION REQUIRED");
            EngineeringVerificationResult verification;
            var verificationAttempted = false;
            if (hasVerificationTarget)
            {
                var approved = await RequestToolApprovalAsync(
                    task,
                    new ToolApprovalRequest(
                        "engineering_verification",
                        repairRound == 0 ? "运行交付验证？" : $"运行第 {repairRound} 次修复后验证？",
                        "NOVA 将执行下方构建或测试命令。项目定义的构建 Target 可能运行代码；每次验证都单独授权。",
                        snapshot.VerificationCommand),
                    cancellationToken);
                if (!approved)
                {
                    AddActivity(
                        "验证闸门",
                        "自动验证已跳过",
                        "用户拒绝了本次验证；NOVA 不会把结果描述为已验证。",
                        ActivityKind.System);
                    return new EngineeringClosureResult(
                        result with
                        {
                            FinalText = result.FinalText
                                        + Environment.NewLine
                                        + Environment.NewLine
                                        + "NOVA 工程验证：用户未授权运行验证命令，结果尚未验证。"
                        },
                        false,
                        false,
                        null,
                        "结果已保存 · 未经自动验证");
                }

                CoreStatus = "VERIFYING";
                CoreMessage = repairRound == 0 ? "正在验证工程结果" : $"正在验证修复轮次 {repairRound}";
                verification = await _engineeringWorkspace.VerifyAsync(
                    WorkspaceRoot,
                    cancellationToken);
                verificationAttempted = true;
                AddActivity(
                    "验证闸门",
                    verification.Passed ? "工程验证通过" : "工程验证失败",
                    $"{verification.Command} · exit {verification.ExitCode} · {verification.Duration.TotalSeconds:F1}s",
                    verification.Passed ? ActivityKind.Completed : ActivityKind.System);
            }
            else
            {
                verification = new EngineeringVerificationResult(
                    false,
                    false,
                    snapshot.VerificationCommand,
                    -1,
                    "没有工程清单或可重复验证入口。",
                    TimeSpan.Zero,
                    DateTimeOffset.Now);
                AddActivity(
                    "工程完整性审查官",
                    "缺少工程清单或验证入口",
                    "不会把散落文件当成完整项目；修复代理将先补齐真实工程结构。",
                    ActivityKind.Waiting);
            }

            EngineeringCodeReviewResult? review = null;
            if (verification.Passed)
            {
                review = await _engineeringWorkspace.RunLocalCodeReviewAsync(
                    WorkspaceRoot,
                    cancellationToken);
            }
            snapshot = await _engineeringWorkspace.InspectAsync(
                WorkspaceRoot,
                cancellationToken);
            var completeness = await _engineeringCompleteness.AssessAndPersistAsync(
                task.Id,
                task.Description,
                baselineSnapshot,
                snapshot,
                verificationAttempted,
                verification.Passed,
                review,
                cancellationToken);
            AddActivity(
                "工程完整性审查官",
                completeness.ReadyForDelivery
                    ? "工程完整性达到交付线"
                    : $"发现 {completeness.Findings.Count(item => item.Severity == "BLOCKER")} 个阻塞项",
                completeness.Summary,
                completeness.ReadyForDelivery
                    ? ActivityKind.Completed
                    : ActivityKind.Waiting);
            if (verification.Passed && completeness.ReadyForDelivery)
            {
                var summary =
                    $"NOVA 工程验证：通过（{verification.Command}，{verification.Duration.TotalSeconds:F1}s）。"
                    + $" 本地代码审查分数：{review?.Score ?? 0}/100。"
                    + $" 工程完整性：{completeness.Score}/100。";
                return new EngineeringClosureResult(
                    result with
                    {
                        FinalText = result.FinalText
                                    + Environment.NewLine
                                    + Environment.NewLine
                                    + summary
                                    + Environment.NewLine
                                    + Environment.NewLine
                                    + EngineeringCompletenessService.Format(completeness)
                    },
                    true,
                    true,
                    review,
                    "结果已验证、审查并达到工程完整性交付线")
                {
                    Completeness = completeness
                };
            }

            if (repairRound == maximumRepairRounds)
            {
                var failureSummary =
                    $"NOVA 工程闭环：经过 {maximumRepairRounds} 次定向修复后仍未达到交付线"
                    + $"（verify exit {verification.ExitCode}，completeness {completeness.Score}/100）。";
                return new EngineeringClosureResult(
                    result with
                    {
                        FinalText = result.FinalText
                                    + Environment.NewLine
                                    + Environment.NewLine
                                    + failureSummary
                                    + Environment.NewLine
                                    + Environment.NewLine
                                    + EngineeringCompletenessService.Format(completeness)
                    },
                    true,
                    verification.Passed,
                    review,
                    "验证或工程完整性未通过 · 已停止自动修复")
                {
                    Completeness = completeness
                };
            }

            var repairApproved = await RequestToolApprovalAsync(
                task,
                new ToolApprovalRequest(
                    "engineering_auto_repair",
                    $"允许自动修复 {repairRound + 1}/{maximumRepairRounds}？",
                    "NOVA 将只针对失败验证和工程完整性阻塞项继续修改，可能产生额外 Token 费用。任何文件写入仍会单独展示 Patch 并再次请求批准。",
                    $"验证命令：{verification.Command}\n退出码：{verification.ExitCode}\n"
                    + $"完整性：{completeness.Summary}"),
                cancellationToken);
            if (!repairApproved)
            {
                return new EngineeringClosureResult(
                    result with
                    {
                        FinalText = result.FinalText
                                    + Environment.NewLine
                                    + Environment.NewLine
                                    + $"NOVA 工程验证失败（exit {verification.ExitCode}）；用户未授权自动修复。"
                    },
                    true,
                    false,
                    null,
                    "验证失败 · 自动修复未授权");
            }

            CoreStatus = "AUTO REPAIR";
            CoreMessage = $"验证失败，启动自动修复 {repairRound + 1}/{maximumRepairRounds}";
            AddActivity(
                "修复代理",
                $"启动自动修复 {repairRound + 1}/{maximumRepairRounds}",
                "基于真实退出码和受限诊断输出定位根因；不会通过删除测试或降低断言来伪造通过。",
                ActivityKind.Working);
            var repairPrompt = EngineeringTaskRouter.EnrichPrompt(
                $"""
                原始目标：
                {task.Description}

                当前工程尚未达到交付线。请检查现有代码并修复根因，不要删除测试、跳过测试、降低断言或伪造成功。
                如果验证已经通过，则只修复下方工程完整性阻塞项。不要用说明文字代替代码、配置、测试或入口。
                所有文件写入仍需用户审批。修改后必须重新读取受影响文件，检查调用链与边界条件。

                验证命令：{verification.Command}
                退出码：{verification.ExitCode}
                诊断输出：
                {LimitDiagnostic(verification.Output, 12000)}

                工程完整性阻塞项：
                {EngineeringCompletenessService.BuildRepairPrompt(completeness)}

                完成契约：
                {(outcomeContract is null ? "未建立" : TaskOutcomeContractService.FormatForPrompt(outcomeContract))}
                """);
            var repairResult = await runtime.RunAsync(
                new AgentRunRequest(
                    task.Id,
                    repairPrompt,
                    WorkspaceRoot,
                    executionApiKey,
                    executionProvider,
                    executionModel,
                    SelectedExecutionMode),
                async runtimeEvent =>
                {
                    await HandleRuntimeEventAsync(task, runtimeEvent, cancellationToken);
                },
                approval => RequestToolApprovalAsync(task, approval, cancellationToken),
                cancellationToken);
            result = repairResult with
            {
                ToolCalls = result.ToolCalls + repairResult.ToolCalls,
                MutatingToolCalls = result.MutatingToolCalls + repairResult.MutatingToolCalls,
                FinalText = result.FinalText
                            + Environment.NewLine
                            + Environment.NewLine
                            + $"自动修复 {repairRound + 1}："
                            + Environment.NewLine
                            + repairResult.FinalText
            };
            snapshot = await _engineeringWorkspace.InspectAsync(WorkspaceRoot, cancellationToken);
        }

        return new EngineeringClosureResult(result, true, false, null, "验证闭环异常结束");
    }

    private async Task<GoalRepairExecutionResult> RunGoalRepairLoopAsync(
        TaskItem task,
        GoalMissionCharter mission,
        TaskOutcomeContract contract,
        EngineeringWorkspaceSnapshot baselineSnapshot,
        IAgentRuntime runtime,
        string executionApiKey,
        string executionProvider,
        string executionModel,
        AgentRunResult initialResult,
        EngineeringClosureResult initialClosure,
        VerificationCouncilResult? initialCouncil,
        TaskOutcomeAssessment? initialAssessment,
        GoalOutcomeLedger initialLedger,
        WorkspaceEvidenceFingerprint initialEvidence,
        CancellationToken cancellationToken)
    {
        var result = initialResult;
        var closure = initialClosure;
        var council = initialCouncil;
        var assessment = initialAssessment;
        var ledger = initialLedger;
        var evidence = initialEvidence;
        var workspaceMutationObserved = false;
        var effectiveExecutionMode = mission.ExecutionKind == "RESEARCH"
            ? AgentExecutionMode.Ask
            : AgentExecutionMode.Goal;

        while (ledger.Phase == GoalRunPhase.Partial)
        {
            var attempt = await _goalRepairs.PlanNextAsync(
                mission,
                ledger,
                evidence,
                cancellationToken);
            if (attempt is null)
            {
                var usedRounds = _goalRepairs.Load(task.Id)?.UsedRounds ?? 0;
                AddActivity(
                    "Goal 定向修复",
                    usedRounds >= GoalRepairLoopService.MaximumRounds
                        ? "已到达三轮安全上限"
                        : "没有可继续修复的目标信号",
                    usedRounds >= GoalRepairLoopService.MaximumRounds
                        ? "NOVA 保留当前 PARTIAL 证据，不会无限消耗预算或伪造完成。"
                        : "成功信号账本没有给出可执行的未满足项。",
                    ActivityKind.Waiting);
                break;
            }

            var targetPreview = string.Join(
                Environment.NewLine,
                attempt.Targets.Select(target =>
                    $"SIGNAL {target.SignalIndex} · {target.Description}"
                    + Environment.NewLine
                    + $"当前：{target.PreviousStatus} · "
                    + $"{target.PreviousEvidence}"));
            var approved = await RequestToolApprovalAsync(
                task,
                new ToolApprovalRequest(
                    "goal_targeted_repair",
                    $"只修复 {attempt.Targets.Count} 个未满足项 "
                    + $"{attempt.Round}/{attempt.MaximumRounds}？",
                    "这会启动一轮额外模型执行并产生 Token 费用。"
                    + $"已有 {attempt.PreservedPassCount} 个通过项会被冻结保护；"
                    + "此授权只启动定向修复，文件写入和命令仍按现有权限策略确认。",
                    targetPreview),
                cancellationToken);
            if (!approved)
            {
                await _goalRepairs.UpdateAsync(
                    task.Id,
                    attempt.AttemptId,
                    GoalRepairAttemptStatus.Declined,
                    "User declined this targeted repair round.",
                    cancellationToken: cancellationToken);
                AddActivity(
                    "Goal 定向修复",
                    "已保留当前结果",
                    "用户暂未授权额外模型执行；任务保持 PARTIAL，可从原成功信号继续。",
                    ActivityKind.Waiting);
                break;
            }

            await _goalRepairs.UpdateAsync(
                task.Id,
                attempt.AttemptId,
                GoalRepairAttemptStatus.Running,
                $"Repairing signals {string.Join(
                    ", ",
                    attempt.Targets.Select(target => target.SignalIndex))}.",
                cancellationToken: cancellationToken);
            ledger = await _goalOutcomes.SetPhaseAsync(
                         task.Id,
                         GoalRunPhase.Executing,
                         $"定向修复 {attempt.Round}/{attempt.MaximumRounds}："
                         + $"只处理 {attempt.Targets.Count} 个未满足成功信号。",
                         cancellationToken)
                     ?? ledger;
            ApplyGoalMissionView(mission, ledger);
            CoreStatus = "GOAL REPAIR";
            CoreMessage =
                $"只修复 {attempt.Targets.Count} 个未满足信号 · "
                + $"{attempt.Round}/{attempt.MaximumRounds}";
            AddActivity(
                "Goal 定向修复",
                $"启动 {attempt.Round}/{attempt.MaximumRounds}",
                $"{attempt.PreservedPassCount} 个通过项冻结 · "
                + $"{attempt.Targets.Count} 个未满足项进入修复",
                ActivityKind.Working);

            try
            {
                var repairPrompt = GoalRepairLoopService.BuildPrompt(
                    mission,
                    ledger,
                    attempt,
                    TaskOutcomeContractService.FormatForPrompt(contract));
                var repairResult = await runtime.RunAsync(
                    new AgentRunRequest(
                        task.Id,
                        repairPrompt,
                        WorkspaceRoot,
                        executionApiKey,
                        executionProvider,
                        executionModel,
                        effectiveExecutionMode,
                        AllowParallelDelegation: false),
                    runtimeEvent => HandleRuntimeEventAsync(
                        task,
                        runtimeEvent,
                        cancellationToken),
                    approval => RequestToolApprovalAsync(
                        task,
                        approval,
                        cancellationToken),
                    cancellationToken);
                workspaceMutationObserved =
                    workspaceMutationObserved || repairResult.MutatingToolCalls > 0;
                result = repairResult with
                {
                    ToolCalls = result.ToolCalls + repairResult.ToolCalls,
                    MutatingToolCalls =
                        result.MutatingToolCalls + repairResult.MutatingToolCalls,
                    FinalText = result.FinalText
                                + Environment.NewLine
                                + Environment.NewLine
                                + $"目标定向修复 {attempt.Round}："
                                + Environment.NewLine
                                + repairResult.FinalText
                };

                if (contract.RequiresWorkspaceMutation
                    && repairResult.MutatingToolCalls > 0)
                {
                    closure = await RunEngineeringClosureAsync(
                        task,
                        runtime,
                        result,
                        contract,
                        baselineSnapshot,
                        executionProvider,
                        executionModel,
                        executionApiKey,
                        cancellationToken);
                    result = closure.Result;
                }
                else
                {
                    closure = closure with { Result = result };
                }

                await _goalRepairs.UpdateAsync(
                    task.Id,
                    attempt.AttemptId,
                    GoalRepairAttemptStatus.Verifying,
                    "Repair execution completed; independent evidence is being rebuilt.",
                    cancellationToken: cancellationToken);
                ledger = await _goalOutcomes.SetPhaseAsync(
                             task.Id,
                             GoalRunPhase.Verifying,
                             $"定向修复 {attempt.Round} 已执行，"
                             + "正在重新验证目标信号并保护既有通过项。",
                             cancellationToken)
                         ?? ledger;
                ApplyGoalMissionView(mission, ledger);

                council = await RunIndependentVerificationCouncilAsync(
                    task,
                    contract,
                    result,
                    closure,
                    executionProvider,
                    executionModel,
                    cancellationToken);
                assessment = await _outcomeContracts.AssessAsync(
                    contract,
                    result,
                    closure.VerificationAttempted,
                    closure.Passed,
                    closure.Review,
                    council,
                    cancellationToken);
                evidence = await _workspaceEvidence.CaptureAsync(
                    task.WorkspaceRoot,
                    cancellationToken);
                ledger = await _goalOutcomes.ReconcileTargetedAsync(
                    mission,
                    assessment,
                    council,
                    evidence,
                    attempt.Targets
                        .Select(target => target.SignalIndex)
                        .ToArray(),
                    cancellationToken);
                var attemptStatus = ledger.IsProven
                    ? GoalRepairAttemptStatus.Proven
                    : ledger.Phase == GoalRunPhase.Partial
                        ? GoalRepairAttemptStatus.Partial
                        : GoalRepairAttemptStatus.Failed;
                await _goalRepairs.UpdateAsync(
                    task.Id,
                    attempt.AttemptId,
                    attemptStatus,
                    ledger.Detail,
                    evidence.Sha256,
                    cancellationToken);
                ApplyGoalMissionView(mission, ledger);
                AddActivity(
                    "Goal 定向修复",
                    ledger.IsProven
                        ? $"第 {attempt.Round} 轮已证明完成"
                        : $"第 {attempt.Round} 轮仍有未满足项",
                    $"{ledger.Signals.Count(signal => signal.Status == GoalSignalStatus.Pass)}/"
                    + $"{ledger.Signals.Count} 个成功信号通过",
                    ledger.IsProven
                        ? ActivityKind.Completed
                        : ActivityKind.Waiting);
            }
            catch
            {
                await _goalRepairs.UpdateAsync(
                    task.Id,
                    attempt.AttemptId,
                    GoalRepairAttemptStatus.Failed,
                    "Targeted repair was interrupted before evidence reconciliation.",
                    cancellationToken: CancellationToken.None);
                throw;
            }
        }

        return new GoalRepairExecutionResult(
            result,
            closure,
            council,
            assessment,
            ledger,
            workspaceMutationObserved);
    }

    private async Task<VerificationCouncilResult> RunIndependentVerificationCouncilAsync(
        TaskItem task,
        TaskOutcomeContract contract,
        AgentRunResult implementationResult,
        EngineeringClosureResult closure,
        string implementationProvider,
        string implementationModel,
        CancellationToken cancellationToken)
    {
        var councilProvider = implementationProvider;
        var councilModel = implementationModel;
        var crossProvider = false;
        if (implementationProvider == "openai" && HasProviderKey("deepseek"))
        {
            councilProvider = "deepseek";
            councilModel = "deepseek-v4-pro";
            crossProvider = true;
        }
        else if (implementationProvider == "deepseek" && HasProviderKey("openai"))
        {
            councilProvider = "openai";
            councilModel = "gpt-5.6-terra";
            crossProvider = true;
        }

        if (contract.RequiresWorkspaceMutation
            && implementationResult.MutatingToolCalls == 0)
        {
            return VerificationCouncilResult.Skipped(
                councilProvider,
                councilModel,
                "没有真实文件修改，独立 Council 无需重复判定。");
        }
        if (closure.VerificationAttempted && !closure.Passed)
        {
            return VerificationCouncilResult.Skipped(
                councilProvider,
                councilModel,
                "工程验证已经失败，独立 Council 未产生额外模型费用。");
        }

        var approved = await RequestToolApprovalAsync(
            task,
            new ToolApprovalRequest(
                "independent_verification_council",
                crossProvider
                    ? $"启动跨模型验证：{GetProviderLabel(councilProvider)} · {councilModel}？"
                    : $"启动独立验证 Agent：{GetProviderLabel(councilProvider)} · {councilModel}？",
                "这是一次额外的只读模型请求，会产生 Token 费用。验证 Agent 不能写文件、运行命令或继续委派；"
                + (crossProvider
                    ? "目标、脱敏 Diff 和验证证据将发送给另一家模型提供商，以降低同源自证偏差。"
                    : "当前只配置了一家提供商，因此使用独立上下文进行同模型交叉审查。"),
                $"实现模型：{implementationProvider} · {implementationModel}\n"
                + $"验证模型：{councilProvider} · {councilModel}\n"
                + $"完成契约：{contract.Criteria.Count} 项\n"
                + $"本地验证：{closure.Summary}"),
            cancellationToken);
        if (!approved)
        {
            return VerificationCouncilResult.Skipped(
                councilProvider,
                councilModel,
                "用户拒绝了额外的独立模型请求；完成证明将标记为未验证。");
        }

        var snapshot = await _engineeringWorkspace.InspectAsync(
            WorkspaceRoot,
            cancellationToken);
        var verificationEvidence = closure.Summary;
        if (contract.ExecutionMode == AgentExecutionMode.Goal)
        {
            verificationEvidence += Environment.NewLine
                                    + Environment.NewLine
                                    + "Candidate result to verify:"
                                    + Environment.NewLine
                                    + LimitDiagnostic(
                                        implementationResult.FinalText,
                                        24_000);
        }
        var prompt = IndependentVerificationCouncilService.BuildPrompt(
            task.Description,
            contract,
            snapshot,
            closure.Review,
            verificationEvidence);
        var runtime = GetRuntime(councilProvider);
        AddActivity(
            "独立验证 Council",
            crossProvider ? "跨提供商对抗审查" : "独立上下文审查",
            $"{GetProviderLabel(councilProvider)} · {councilModel} · 只读",
            ActivityKind.Working);
        var reviewResult = await runtime.RunAsync(
            new AgentRunRequest(
                $"{task.Id}-council",
                prompt,
                WorkspaceRoot,
                _apiKeys[councilProvider],
                councilProvider,
                councilModel,
                AgentExecutionMode.Ask,
                AllowParallelDelegation: false),
            async runtimeEvent =>
            {
                if (runtimeEvent.Kind == AgentRuntimeEventKind.TextDelta)
                {
                    return;
                }
                var mapped = runtimeEvent with
                {
                    Kind = runtimeEvent.Kind == AgentRuntimeEventKind.Completed
                        ? AgentRuntimeEventKind.ToolCompleted
                        : runtimeEvent.Kind,
                    Agent = $"验证 Council · {runtimeEvent.Agent}",
                    Progress = Math.Clamp(86 + runtimeEvent.Progress * .11, 86, 97),
                    ActiveUnits = 1
                };
                await HandleRuntimeEventAsync(task, mapped, cancellationToken);
            },
            _ => Task.FromResult(false),
            cancellationToken);
        var council = IndependentVerificationCouncilService.Parse(
            councilProvider,
            councilModel,
            reviewResult.FinalText);
        AddActivity(
            "独立验证 Council",
            $"{council.Verdict} · confidence {council.Confidence}%",
            council.Summary,
            council.Passed ? ActivityKind.Completed : ActivityKind.System);
        return council;
    }

    private static string LimitDiagnostic(string value, int maximum)
    {
        var redacted = DiagnosticBearerPattern.Replace(
            DiagnosticApiKeyPattern.Replace(value, "[REDACTED_API_KEY]"),
            "Bearer [REDACTED]");
        return redacted.Length <= maximum
            ? redacted
            : "… earlier diagnostic output omitted …"
              + Environment.NewLine
              + redacted[^maximum..];
    }

    private sealed record EngineeringClosureResult(
        AgentRunResult Result,
        bool VerificationAttempted,
        bool Passed,
        EngineeringCodeReviewResult? Review,
        string Summary)
    {
        public EngineeringCompletenessAssessment? Completeness { get; init; }
    }

    private sealed record GoalRepairExecutionResult(
        AgentRunResult Result,
        EngineeringClosureResult Closure,
        VerificationCouncilResult? Council,
        TaskOutcomeAssessment? Assessment,
        GoalOutcomeLedger Ledger,
        bool WorkspaceMutationObserved);

    private sealed record TournamentExecutionOutcome(
        WorktreeTournamentResult Tournament,
        TournamentCouncilDecision Decision,
        AgentRunResult Result,
        VerificationCouncilResult VerificationCouncil,
        bool Applied);

    private sealed record AgentMeshExecutionOutcome(
        AgentMeshRunResult Mesh,
        AgentMeshCouncilDecision Decision,
        AgentRunResult Result,
        VerificationCouncilResult VerificationCouncil,
        bool Applied);

    private void ApplyRuntimeEvent(TaskItem task, AgentRuntimeEvent runtimeEvent)
    {
        if (runtimeEvent.Kind == AgentRuntimeEventKind.TextDelta)
        {
            _streamingBuffer.Append(runtimeEvent.Detail);
            if (_streamingFlushClock.ElapsedMilliseconds >= 75)
            {
                FlushStreamingBuffer(task);
            }
            return;
        }

        FlushStreamingBuffer(task);
        var kind = runtimeEvent.Kind switch
        {
            AgentRuntimeEventKind.ToolCompleted
                or AgentRuntimeEventKind.ToolBatchCompleted
                or AgentRuntimeEventKind.BatchCompleted
                or AgentRuntimeEventKind.Completed => ActivityKind.Completed,
            AgentRuntimeEventKind.ToolRequested => ActivityKind.Waiting,
            AgentRuntimeEventKind.Message => ActivityKind.System,
            _ => ActivityKind.Working
        };

        if (runtimeEvent.Progress > 0)
        {
            OverallProgress = runtimeEvent.Progress;
            task.Progress = runtimeEvent.Progress;
        }

        task.State = TaskState.Running;
        task.Stage = runtimeEvent.Action;
        CurrentStage = runtimeEvent.Action;
        CoreStatus = runtimeEvent.Kind switch
        {
            AgentRuntimeEventKind.Thinking => "THINKING",
            AgentRuntimeEventKind.ToolRequested => "TOOL REQUEST",
            AgentRuntimeEventKind.ToolRunning => "TOOL RUNNING",
            AgentRuntimeEventKind.ToolCompleted => "WORKING",
            AgentRuntimeEventKind.ToolBatchStarted => "TOOLS",
            AgentRuntimeEventKind.ToolBatchCompleted => "WORKING",
            AgentRuntimeEventKind.BatchStarted => "PARALLEL",
            AgentRuntimeEventKind.BatchCompleted => "MERGING",
            AgentRuntimeEventKind.Completed => "COMPLETE",
            AgentRuntimeEventKind.Failed => "ERROR",
            _ => "LIVE"
        };
        CoreMessage = runtimeEvent.Detail;
        ActiveAgentCount = Math.Max(1, runtimeEvent.ActiveUnits);
        CurrentStep = runtimeEvent.Kind switch
        {
            AgentRuntimeEventKind.Thinking => 1,
            AgentRuntimeEventKind.ToolRequested
                or AgentRuntimeEventKind.ToolRunning
                or AgentRuntimeEventKind.ToolBatchStarted
                or AgentRuntimeEventKind.BatchStarted => 2,
            AgentRuntimeEventKind.ToolCompleted
                or AgentRuntimeEventKind.ToolBatchCompleted
                or AgentRuntimeEventKind.BatchCompleted => 5,
            AgentRuntimeEventKind.Completed => 7,
            _ => CurrentStep
        };
        AddActivity(runtimeEvent.Agent, runtimeEvent.Action, runtimeEvent.Detail, kind);
        UpdateElapsed(task);
    }

    private async Task HandleRuntimeEventAsync(
        TaskItem task,
        AgentRuntimeEvent runtimeEvent,
        CancellationToken cancellationToken)
    {
        await _agentResourceGovernor.ObserveRuntimeEventAsync(
            task.Id,
            runtimeEvent,
            cancellationToken);
        UpdateBudgetStatus();
        ApplyRuntimeEvent(task, runtimeEvent);
        await TrackAgentOsEventAsync(task, runtimeEvent, cancellationToken);
    }

    private async Task TrackAgentOsEventAsync(
        TaskItem task,
        AgentRuntimeEvent runtimeEvent,
        CancellationToken cancellationToken)
    {
        var resources = _agentResourceGovernor.GetSnapshot();
        ActiveAgentCount = resources.ActiveAgents;
        if (runtimeEvent.Kind == AgentRuntimeEventKind.TextDelta)
        {
            return;
        }

        try
        {
            var committed = await _agentOsKernel.PublishTaskEventAsync(
                "runtime",
                runtimeEvent.Agent,
                $"{runtimeEvent.Action}: {runtimeEvent.Detail}",
                task,
                runtimeEvent.Kind == AgentRuntimeEventKind.Failed ? "ERROR" : "INFO",
                cancellationToken);
            await _agentTaskGraph.ApplyRuntimeEventAsync(
                task.Id,
                runtimeEvent,
                cancellationToken,
                committed.Sequence);
            await _agentSupervisor.HeartbeatAsync(
                task.Id,
                runtimeEvent.Action,
                runtimeEvent.Kind is AgentRuntimeEventKind.ToolCompleted
                    or AgentRuntimeEventKind.ToolBatchCompleted
                    or AgentRuntimeEventKind.BatchCompleted
                    or AgentRuntimeEventKind.Completed
                    or AgentRuntimeEventKind.Failed,
                cancellationToken,
                committed.Sequence);
            await _snapshots.SaveAsync(task, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or System.Text.Json.JsonException)
        {
            AddActivity(
                "AgentOS 内核",
                "运行事件未能持久化",
                exception.Message,
                ActivityKind.System);
        }
    }

    private void FlushStreamingBuffer(TaskItem task)
    {
        if (_streamingBuffer.Length == 0
            || (_streamingFlushClock.ElapsedMilliseconds < 75
                && StreamingText.Length == _streamingBuffer.Length))
        {
            return;
        }

        StreamingText = _streamingBuffer.ToString();
        CoreStatus = "STREAMING";
        CoreMessage = StreamingText.Length <= 90
            ? StreamingText
            : "…" + StreamingText[^89..];
        UpdateElapsed(task);
        _streamingFlushClock.Restart();
    }

    private async Task<bool> RequestToolApprovalAsync(
        TaskItem task,
        ToolApprovalRequest approval,
        CancellationToken cancellationToken)
    {
        var approvalScope = _taskApprovalPolicy.Describe(approval);
        if (_taskApprovalPolicy.IsGranted(task.Id, approvalScope))
        {
            AddActivity(
                "权限管家",
                "沿用本轮信任",
                $"{approvalScope.Label} · 安全边界照常检查",
                ActivityKind.Completed);
            return true;
        }

        task.State = TaskState.Waiting;
        IsApprovalVisible = true;
        ApprovalTitle = approval.Title;
        IsApprovalPreviewVisible = approval.PreviewKind == "unified-diff"
                                   && !string.IsNullOrWhiteSpace(approval.ChangePreview);
        ApprovalPreview = approval.ChangePreview ?? string.Empty;
        ApprovalStats = IsApprovalPreviewVisible
            ? $"+{approval.Additions:N0} / -{approval.Deletions:N0}"
            : string.Empty;
        var isGoalDiscovery = approval.ToolName == "goal_mission_discovery";
        ApprovalAllowLabel = IsApprovalPreviewVisible
            ? "只批准这次"
            : isGoalDiscovery
                ? "允许一次只读探索"
                : "只允许这次";
        ApprovalRejectLabel = isGoalDiscovery
            ? "跳过探索，保守继续"
            : "先不做";
        _activeApprovalScope = approvalScope;
        _approvalTrustRequested = false;
        IsApprovalTrustVisible = approvalScope.CanTrustForRun && !isGoalDiscovery;
        ApprovalTrustLabel = approvalScope.TrustActionLabel;
        ApprovalSafetyNote = approvalScope.SafetyNote;
        ApprovalDescription = IsApprovalPreviewVisible
            ? approval.Description
            : BuildApprovalDescription(approval)
              + (isGoalDiscovery
                  ? "\n拒绝后不会取消任务：NOVA 将使用低置信度保守章程继续，"
                    + "所有成功信号仍需独立证明。"
                  : string.Empty);
        CoreStatus = "APPROVAL";
        CoreMessage = "我会停在这里等你，确认后再继续";
        AddActivity("权限管家", "想和你确认一下", approval.Title, ActivityKind.Waiting);
        await _snapshots.SaveAsync(task, cancellationToken);

        _approvalSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => _approvalSource.TrySetCanceled(cancellationToken));
        var approved = await _approvalSource.Task;
        IsApprovalVisible = false;
        IsApprovalPreviewVisible = false;
        ApprovalPreview = string.Empty;
        ApprovalStats = string.Empty;
        IsApprovalTrustVisible = false;
        ApprovalAllowLabel = "本次允许";
        ApprovalRejectLabel = "拒绝";
        if (approved && _approvalTrustRequested && _activeApprovalScope is not null)
        {
            _taskApprovalPolicy.GrantForRun(task.Id, _activeApprovalScope);
            ApprovalPolicyStatus = $"本轮已信任 · {_activeApprovalScope.Label}";
        }
        task.State = TaskState.Running;
        AddActivity(
            "权限管家",
            approved
                ? _approvalTrustRequested
                    ? "本轮已记住你的选择"
                    : "这一步可以继续"
                : "这一步先放下",
            approved
                ? _approvalTrustRequested && _activeApprovalScope is not null
                    ? $"{_activeApprovalScope.Label}将在本轮内安静执行；风险升级仍会再次确认"
                    : "仅允许当前操作，后续不会自动扩大权限"
                : "工具没有执行，任务会尝试换一条更保守的路",
            approved ? ActivityKind.Completed : ActivityKind.System);
        _activeApprovalScope = null;
        _approvalTrustRequested = false;
        await _snapshots.SaveAsync(task, cancellationToken);
        return approved;
    }

    private static string BuildApprovalDescription(ToolApprovalRequest approval)
    {
        var preview = approval.ArgumentsPreview?.Trim();
        if (string.IsNullOrWhiteSpace(preview))
        {
            return approval.Description;
        }

        const int maximumCharacters = 900;
        const int maximumLines = 14;
        var lines = preview
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');
        var selectedLines = lines.Take(maximumLines).ToArray();
        var compact = string.Join(Environment.NewLine, selectedLines);
        var wasTrimmed = lines.Length > maximumLines || compact.Length > maximumCharacters;
        if (compact.Length > maximumCharacters)
        {
            compact = compact[..maximumCharacters].TrimEnd();
        }
        compact = DiagnosticBearerPattern.Replace(
            DiagnosticApiKeyPattern.Replace(compact, "[REDACTED_API_KEY]"),
            "Bearer [REDACTED]");

        return $"{approval.Description}\n\n{compact}"
               + (wasTrimmed
                   ? "\n…详细参数已收起，授权范围不会因此扩大。"
                   : string.Empty);
    }

    private static async Task<string> SaveAgentOutputAsync(
        TaskItem task,
        AgentRunResult result,
        int conversationRound,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "outputs",
            task.Id);
        Directory.CreateDirectory(outputDirectory);
        var safeRound = Math.Max(1, conversationRound);
        var outputPath = Path.Combine(outputDirectory, $"response-v{safeRound}.md");
        var temporaryPath = outputPath + ".tmp";
        var content = $"""
                       # {task.Title}

                       - Conversation round: {safeRound}
                       - Model: {result.Model}
                       - Response: {result.ResponseId}
                       - Tool calls: {result.ToolCalls}
                       - Completed: {DateTimeOffset.Now:O}

                       {result.FinalText}
                       """;
        await File.WriteAllTextAsync(temporaryPath, content, cancellationToken);
        File.Move(temporaryPath, outputPath, overwrite: true);
        return Path.GetFullPath(outputPath);
    }

    private async Task RunPipelineAsync(
        TaskItem task,
        string turnPrompt,
        CancellationToken cancellationToken)
    {
        var steps = new[]
        {
            new PipelineStep("指挥官", "拆解目标", "生成研究、产品与体验三个并行工作流", 12, 1, false),
            new PipelineStep("研究员", "扫描市场", "建立代表性 Agent 产品样本与证据清单", 29, 2, false),
            new PipelineStep("系统", "权限检查", "准备访问公开产品页面与技术文档", 36, 2, true),
            new PipelineStep("研究员 × 3", "并行调查", "比较通用 Agent、编程 Agent 与企业平台", 57, 4, false),
            new PipelineStep("创意总监", "提炼机会", "将市场缺口转化为差异化体验原则", 71, 3, false),
            new PipelineStep("架构师", "设计系统", "建立原生桌面端、运行时、记忆与权限边界", 84, 3, false),
            new PipelineStep("审查官", "交叉验证", "检查论据、覆盖范围与交付完整性", 94, 2, false),
            new PipelineStep("NOVA", "生成成果", "装配研究简报、产品蓝图和实施清单", 100, 1, false)
        };

        for (var index = 0; index < steps.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitWhilePausedAsync(cancellationToken);

            var step = steps[index];
            CurrentStep = index + 1;
            ActiveAgentCount = step.AgentCount;
            CurrentStage = step.Action;
            CoreStatus = step.RequiresApproval ? "WAITING" : "WORKING";
            CoreMessage = step.Detail;
            task.Stage = step.Action;

            if (step.RequiresApproval)
            {
                task.State = TaskState.Waiting;
                IsApprovalVisible = true;
                IsApprovalPreviewVisible = false;
                IsApprovalTrustVisible = false;
                ApprovalPreview = string.Empty;
                ApprovalStats = string.Empty;
                ApprovalAllowLabel = "只允许这次";
                ApprovalRejectLabel = "先不做";
                ApprovalSafetyNote = "只读取公开网页，不使用登录态，也不会提交任何内容。";
                ApprovalTitle = "允许研究员访问公开网页？";
                ApprovalDescription = "将读取公开产品页面与官方文档。不会登录账户、发送消息、下载可执行文件或提交任何表单。";
                AddActivity(step.Agent, "想和你确认一下", step.Detail, ActivityKind.Waiting);

                _approvalSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = cancellationToken.Register(() => _approvalSource.TrySetCanceled(cancellationToken));
                var approved = await _approvalSource.Task;
                IsApprovalVisible = false;

                if (!approved)
                {
                    task.State = TaskState.Running;
                    AddActivity("系统", "已跳过外部访问", "继续使用本地演示数据完成任务", ActivityKind.System);
                }
                else
                {
                    task.State = TaskState.Running;
                    AddActivity("系统", "授权已确认", "权限仅对当前步骤有效", ActivityKind.Completed);
                }
            }
            else
            {
                AddActivity(step.Agent, step.Action, step.Detail);
            }

            var start = OverallProgress;
            var ticks = 12;
            for (var tick = 1; tick <= ticks; tick++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WaitWhilePausedAsync(cancellationToken);
                await Task.Delay(90, cancellationToken);
                OverallProgress = start + ((step.Progress - start) * tick / ticks);
                task.Progress = OverallProgress;
                UpdateElapsed(task);
            }

            ReplaceLatestActivityAsCompleted(step.Agent, step.Action, step.Detail);
        }

        Artifacts.Add(new ArtifactItem(
            "报告",
            "Agent 市场机会简报",
            "12 个产品 · 28 条证据 · 已核验",
            "\uE8A5",
            "#75F0FF",
            "市场正在从单轮助手转向可持续执行的工作代理。NOVA 的机会在于把本机操作、长期记忆、审批边界与多模型运行时整合成一个可信赖的原生工作台。",
            @"D:\Agent\outputs\agent-market-brief.md"));
        Artifacts.Add(new ArtifactItem(
            "蓝图",
            "NOVA 产品系统蓝图",
            "体验、能力、权限与运行时",
            "\uE9D2",
            "#BDA8FF",
            "产品由任务编排核心、执行单元、MCP 与 Skills 扩展层、本地知识引擎、认知图谱和效率总结器构成。所有高影响操作进入明确审批边界。",
            @"D:\Agent\outputs\nova-product-blueprint.md"));
        Artifacts.Add(new ArtifactItem(
            "计划",
            "8 周实施路线",
            "4 个阶段 · 17 个验收点",
            "\uE9D5",
            "#6BE5A9",
            "第 1–2 周完成运行时与任务恢复；第 3–4 周完成 MCP、Skills 与模型接入；第 5–6 周完善本地认知系统；第 7–8 周集中处理体验、性能和发布验证。",
            @"D:\Agent\outputs\nova-8-week-roadmap.md"));
        RefreshDeliveryCollections();
        OnPropertyChanged(nameof(HasArtifacts));
        NotifyCompletionSurfaceChanged();
        ShowDeliveryCommand.RaiseCanExecuteChanged();
        var demoReply =
            $"已完成本轮目标：“{turnPrompt}”。当前演示管线生成了市场简报、产品蓝图与实施路线，后续输入会在同一任务上下文中继续处理。";
        var assistantTurn = await _conversationHistory.AppendAsync(
            task.Id,
            "assistant",
            demoReply,
            cancellationToken);
        ConversationTurns.Add(assistantTurn);
        UpdateConversationLabels(task.Id);
        IsDeliveryVisible = false;
        CoreStatus = "FINALIZING";
        CoreMessage = "成果已经做好，我再替你把版本和证据收妥";
        await PersistArtifactsAsync(task, cancellationToken);

        task.State = TaskState.Completed;
        task.Stage = "成果已交付";
        task.Progress = 100;
        CoreStatus = "COMPLETE";
        CoreMessage = "这一步已经做稳，也替你核验过了";
        CurrentStage = "3 个成果已生成";
        AddActivity("NOVA", "任务完成", "所有成果已保存到本地工作区", ActivityKind.Completed);
    }

    private async Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        while (IsPaused)
        {
            await Task.Delay(120, cancellationToken);
        }
    }

    private void TogglePause()
    {
        if (!IsRunning || IsApprovalVisible)
        {
            return;
        }

        IsPaused = !IsPaused;
        _agentResourceGovernor.SetPaused(IsPaused);
        UpdateBudgetStatus();
        PauseLabel = IsPaused ? "继续" : "暂停";

        if (SelectedTask is not null)
        {
            SelectedTask.State = IsPaused ? TaskState.Paused : TaskState.Running;
        }

        CoreStatus = IsPaused ? "PAUSED" : "WORKING";
        CoreMessage = IsPaused ? "任务已冻结，状态安全保留" : "已恢复执行";
        AddActivity("系统", IsPaused ? "执行暂停" : "执行恢复",
            IsPaused
                ? "下一模型轮次、工具或并行批次将在统一安全门等待"
                : "已释放执行安全门，从最近检查点继续",
            ActivityKind.System);
        if (SelectedTask is not null)
        {
            _ = _snapshots.SaveAsync(SelectedTask);
        }
    }

    private void CancelRun()
    {
        _approvalSource?.TrySetCanceled();
        _runCancellation?.Cancel();
    }

    private void UpdateBudgetStatus()
    {
        var resources = _agentResourceGovernor.GetSnapshot();
        var prefix = resources.LimitReason is not null
            ? "已到安全上限"
            : resources.IsPaused
                ? "已暂停"
                : "弹性预算";
        BudgetStatusLabel =
            $"{prefix} · 模型 {resources.ModelRounds}/{resources.Policy.MaxModelRounds}"
            + $" · 工具 {resources.ToolCalls}/{resources.Policy.MaxToolCallsPerTask}";

        var modelPressure = resources.Policy.MaxModelRounds == 0
            ? 0
            : (double)resources.ModelRounds / resources.Policy.MaxModelRounds;
        var toolPressure = resources.Policy.MaxToolCallsPerTask == 0
            ? 0
            : (double)resources.ToolCalls / resources.Policy.MaxToolCallsPerTask;
        if (!_budgetWarningRaised
            && resources.LimitReason is null
            && Math.Max(modelPressure, toolPressure) >= .8)
        {
            _budgetWarningRaised = true;
            AddActivity(
                "AgentOS 资源治理",
                "任务接近安全上限",
                $"模型还可运行 {Math.Max(0, resources.Policy.MaxModelRounds - resources.ModelRounds)} 轮，"
                + $"工具还可调用 {Math.Max(0, resources.Policy.MaxToolCallsPerTask - resources.ToolCalls)} 次。"
                + "NOVA 会优先完成验证；若达到上限会保留恢复点，不会丢失成果。",
                ActivityKind.Waiting);
        }
    }

    private void ResolveApproval(bool approved)
    {
        _approvalTrustRequested = false;
        _approvalSource?.TrySetResult(approved);
    }

    private void ResolveApprovalForRun()
    {
        if (!IsApprovalTrustVisible || _activeApprovalScope is null)
        {
            return;
        }

        _approvalTrustRequested = true;
        _approvalSource?.TrySetResult(true);
    }

    private void NewTask()
    {
        ShowArchivedTasks = false;
        IsDeliveryVisible = false;
        ClearConversationChoice();
        SelectedTask = null;
        ConversationTurns.Clear();
        UpdateConversationLabels(null);
        PromptText = string.Empty;
        ClearInputAttachments();
        CoreStatus = "READY";
        CoreMessage = "你说想做成什么，我来把路走清楚";
        CurrentStage = "从这里起笔";
    }

    private void ToggleArchivedTasks()
    {
        ShowArchivedTasks = !ShowArchivedTasks;
        SelectedTask = TaskView.Cast<TaskItem>().FirstOrDefault();
    }

    private async Task SetTaskArchivedAsync(TaskItem task, bool archived)
    {
        if (IsRunning || task.IsArchived == archived)
        {
            return;
        }

        task.IsArchived = archived;
        await _snapshots.SaveAsync(task, CancellationToken.None);
        RefreshTaskLibrary();
        if (ReferenceEquals(SelectedTask, task))
        {
            SelectedTask = TaskView.Cast<TaskItem>().FirstOrDefault();
        }
        AddActivity(
            "任务库",
            archived ? "任务已归档" : "任务已恢复",
            archived
                ? $"{task.Title} 已从当前任务空间移入归档，可随时恢复。"
                : $"{task.Title} 已回到当前任务空间。",
            ActivityKind.System);
    }

    private void RefreshTaskLibrary()
    {
        TaskView.Refresh();
        OnPropertyChanged(nameof(VisibleTaskCount));
        OnPropertyChanged(nameof(ArchivedTaskCount));
        OnPropertyChanged(nameof(HasVisibleTasks));
        OnPropertyChanged(nameof(TaskLibraryTitle));
        OnPropertyChanged(nameof(TaskArchiveToggleLabel));
        OnPropertyChanged(nameof(TaskSpaceEmptyTitle));
        OnPropertyChanged(nameof(TaskSpaceEmptyDetail));
    }

    private void ContinueConversation()
    {
        IsDeliveryVisible = false;
        IsCompletedConversationExpanded = true;
        CoreStatus = "FOLLOW-UP";
        CoreMessage = "接着说，我记得前面的来龙去脉";
        CurrentStage = $"{ConversationRoundLabel} · 我们继续把它做完整";
    }

    private void ShowDelivery()
    {
        RefreshDeliveryCollections();
        IsDeliveryEvidenceExpanded = false;
        SelectedArtifact = DeliveryArtifacts.FirstOrDefault()
                           ?? Artifacts.FirstOrDefault();
        IsDeliveryVisible = true;
    }

    private void ToggleCompletedConversation()
        => IsCompletedConversationExpanded = !IsCompletedConversationExpanded;

    private void SelectedTask_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(TaskItem.State))
        {
            if (SelectedTask?.State != TaskState.Completed)
            {
                IsCompletedConversationExpanded = false;
            }
            NotifyCompletionSurfaceChanged();
            NotifyHumanGuidanceChanged();
        }
    }

    private void NotifyCompletionSurfaceChanged()
    {
        OnPropertyChanged(nameof(IsCompletedTask));
        OnPropertyChanged(nameof(IsConversationTranscriptVisible));
        OnPropertyChanged(nameof(IsCompletedSummaryVisible));
        OnPropertyChanged(nameof(CompletedConversationToggleLabel));
    }

    private void NotifyHumanGuidanceChanged()
    {
        OnPropertyChanged(nameof(ShellStatus));
        OnPropertyChanged(nameof(HumanStatusLabel));
        OnPropertyChanged(nameof(HumanGuidanceTitle));
        OnPropertyChanged(nameof(HumanGuidanceDetail));
    }

    private static string FriendlyStage(string stage)
    {
        var value = string.IsNullOrWhiteSpace(stage)
            ? "处理当前任务"
            : stage.Trim();
        if (value.StartsWith("正在", StringComparison.Ordinal))
        {
            value = value[2..].TrimStart();
        }
        return value switch
        {
            "真实 Agent Runtime" => "连接执行模型",
            "构建任务边界" => "理解目标和工作区",
            "所有权隔离的并行工程协作" => "分工处理工程",
            "候选 Worktree 并行实现" => "比较多个隔离方案",
            _ => value
        };
    }

    private void RefreshDeliveryCollections()
    {
        DeliveryArtifacts.Clear();
        DeliveryEvidenceArtifacts.Clear();
        var primary = Artifacts
            .Where(artifact => artifact.Type == "交付")
            .ToArray();
        if (primary.Length == 0 && Artifacts.FirstOrDefault() is { } fallback)
        {
            primary = [fallback];
        }
        var primaryIds = primary.ToHashSet();
        foreach (var artifact in primary)
        {
            DeliveryArtifacts.Add(artifact);
        }
        foreach (var artifact in Artifacts.Where(item => !primaryIds.Contains(item)))
        {
            DeliveryEvidenceArtifacts.Add(artifact);
        }
        OnPropertyChanged(nameof(DeliveryArtifactCount));
        OnPropertyChanged(nameof(DeliveryEvidenceCount));
    }

    private void LoadConversationForTask(string? taskId)
    {
        ConversationTurns.Clear();
        if (!string.IsNullOrWhiteSpace(taskId))
        {
            foreach (var turn in _conversationHistory.Load(taskId))
            {
                ConversationTurns.Add(turn);
            }
        }
        OnPropertyChanged(nameof(HasConversationTurns));
        UpdateConversationLabels(taskId);
    }

    private void ApplySelectedTaskView(TaskItem? task)
    {
        ClearConversationChoice();
        ClearInputAttachments();
        Activity.Clear();
        Artifacts.Clear();
        DeliveryArtifacts.Clear();
        DeliveryEvidenceArtifacts.Clear();
        ArtifactVersions.Clear();
        SelectedArtifact = null;
        SelectedArtifactVersion = null;
        IsDeliveryVisible = false;
        IsDeliveryEvidenceExpanded = false;
        IsCompletedConversationExpanded = false;
        _promptText = task?.Draft ?? string.Empty;
        OnPropertyChanged(nameof(PromptText));
        _submitCommand.RaiseCanExecuteChanged();
        LoadConversationForTask(task?.Id);
        if (task?.ExecutionMode == AgentExecutionMode.Goal)
        {
            LoadGoalMissionView(task.Id);
        }
        else
        {
            ClearGoalMissionView();
        }

        if (task is null)
        {
            OverallProgress = 0;
            RunTime = "00:00";
            CoreStatus = "READY";
            CoreMessage = "你说想做成什么，我来把路走清楚";
            CurrentStage = "从这里起笔";
        }
        else
        {
            var latestFailure = task.State is TaskState.Failed
                or TaskState.BudgetExhausted
                or TaskState.Cancelled
                or TaskState.Paused
                ? _failureLedger.LoadLatest(task.Id)
                : null;
            OverallProgress = task.Progress;
            RunTime = task.State == TaskState.Running ? task.Elapsed : "00:00";
            CurrentStage = latestFailure?.RecoveryLabel ?? task.Stage;
            CoreStatus = latestFailure?.StatusLabel ?? (task.State switch
            {
                TaskState.Completed => "COMPLETE",
                TaskState.Failed => "ATTENTION",
                TaskState.Stale => "STALE",
                TaskState.BudgetExhausted => "BUDGET EXHAUSTED",
                TaskState.Paused => "PAUSED",
                TaskState.Waiting => "WAITING",
                _ => "READY"
            });
            CoreMessage = latestFailure is not null
                ? $"{latestFailure.Title} · {latestFailure.UserMessage}"
                : task.State switch
            {
                TaskState.Completed => "这一步已经做好，想改哪里我们接着来",
                TaskState.Failed => "这一步没走通，原因和恢复点都还在",
                TaskState.Stale => "工作区已经变化，旧完成证明已安全失效",
                TaskState.BudgetExhausted => "先停在安全点，已经做好的内容没有丢",
                TaskState.Paused => "进度已经替你收好，随时接着来",
                _ => task.Stage
            };
            if (task.State == TaskState.Completed)
            {
                LoadArtifactsForTask(task.Id);
            }
        }

        OnPropertyChanged(nameof(HasArtifacts));
        NotifyCompletionSurfaceChanged();
        OnPropertyChanged(nameof(IsTraceVisible));
        ShowDeliveryCommand.RaiseCanExecuteChanged();
    }

    private void LoadGoalMissionView(string taskId)
    {
        var mission = _goalMissions.Load(taskId);
        if (mission is null)
        {
            ClearGoalMissionView();
            return;
        }
        var ledger = _goalOutcomes.Load(taskId);
        ApplyGoalMissionView(mission, ledger);
        if (ledger is
            {
                Phase: GoalRunPhase.Proven,
                EvidenceFingerprint.Length: > 0
            })
        {
            _ = RevalidateGoalEvidenceAsync(taskId, mission, ledger);
        }
    }

    private async Task RevalidateGoalEvidenceAsync(
        string taskId,
        GoalMissionCharter mission,
        GoalOutcomeLedger provenLedger)
    {
        var task = Tasks.FirstOrDefault(item => item.Id == taskId);
        var workspaceRoot = task?.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            workspaceRoot = provenLedger.EvidenceWorkspaceRoot;
        }

        WorkspaceEvidenceFingerprint fingerprint;
        try
        {
            fingerprint = await _workspaceEvidence.CaptureAsync(
                workspaceRoot,
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            fingerprint = new WorkspaceEvidenceFingerprint(
                workspaceRoot ?? string.Empty,
                string.Empty,
                0,
                0,
                false,
                DateTimeOffset.Now,
                exception.Message);
        }

        var current = await _goalOutcomes.ValidateFreshnessAsync(
            taskId,
            fingerprint,
            CancellationToken.None);
        if (current is null
            || SelectedTask?.Id != taskId
            || task?.State == TaskState.Running)
        {
            return;
        }

        ApplyGoalMissionView(mission, current);
        if (current.Phase != GoalRunPhase.Stale)
        {
            return;
        }

        if (task is not null)
        {
            task.State = TaskState.Stale;
            task.Stage = "证据已过期 · 工作区变化后需要重新验证";
            task.Progress = Math.Min(task.Progress, 96);
            await _snapshots.SaveAsync(task, CancellationToken.None);
        }
        CoreStatus = "STALE";
        CoreMessage = "我发现工作区在完成证明后发生了变化，旧结论已撤回，不会继续冒充完成。";
        CurrentStage = "可以从现有成果继续，NOVA 将只重跑受影响的验证";
        OnPropertyChanged(nameof(CanResumeSelected));
        ResumeSelectedCommand.RaiseCanExecuteChanged();
        AddActivity(
            "证据守卫",
            "旧完成证明已失效",
            current.Detail,
            ActivityKind.Waiting);
    }

    private void ApplyGoalMissionView(
        GoalMissionCharter mission,
        GoalOutcomeLedger? ledger)
    {
        GoalMissionTitle = mission.Title;
        GoalMissionOutcome = mission.Outcome;
        GoalMissionMeta =
            $"{mission.ExecutionKind} · confidence {mission.Confidence}% · "
            + $"{mission.Unknowns.Count} unknowns · Mission v{mission.MissionVersion}";
        GoalMissionPhase = (ledger?.Phase ?? GoalRunPhase.Chartered)
            .ToString()
            .ToUpperInvariant();
        GoalSignals.Clear();
        var signals = ledger?.Signals
                      ?? mission.SuccessSignals.Select((description, index) =>
                          new GoalOutcomeSignal(
                              $"pending-{index + 1}",
                              index + 1,
                              description,
                              GoalSignalStatus.Pending,
                              string.Empty,
                              0,
                              DateTimeOffset.Now))
                          .ToArray();
        foreach (var signal in signals.OrderBy(item => item.Index))
        {
            GoalSignals.Add(new GoalSignalDisplayItem(
                signal.Description,
                signal.Status.ToString().ToUpperInvariant(),
                string.IsNullOrWhiteSpace(signal.Evidence)
                    ? "等待独立证据"
                    : signal.Evidence));
        }
        OnPropertyChanged(nameof(GoalSignals));
    }

    private void ClearGoalMissionView()
    {
        GoalMissionTitle = string.Empty;
        GoalMissionOutcome = string.Empty;
        GoalMissionMeta = string.Empty;
        GoalMissionPhase = string.Empty;
        GoalSignals.Clear();
        OnPropertyChanged(nameof(GoalSignals));
    }

    private static string FormatGoalOutcomeLedger(GoalOutcomeLedger ledger)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            $"## NOVA Goal Evidence Matrix · {ledger.Phase}");
        builder.AppendLine(
            $"Signals: {ledger.Signals.Count(item => item.Status == GoalSignalStatus.Pass)}/"
            + $"{ledger.Signals.Count} PASS · Proof {ledger.AssessmentProofScore}/100 · "
            + $"Council {ledger.CouncilVerdict} · Evidence {ledger.Freshness}");
        if (ledger.EvidenceCapturedAt is { } capturedAt)
        {
            builder.AppendLine(
                $"Workspace evidence: {ledger.EvidenceFileCount} files · "
                + $"{ledger.EvidenceHashedBytes:N0} bytes · "
                + $"{capturedAt:yyyy-MM-dd HH:mm:ss zzz}");
        }
        foreach (var signal in ledger.Signals.OrderBy(item => item.Index))
        {
            builder.AppendLine(
                $"- **SIGNAL {signal.Index} · {signal.Status.ToString().ToUpperInvariant()}** "
                + $"{signal.Description}");
            builder.AppendLine(
                $"  Evidence: {(string.IsNullOrWhiteSpace(signal.Evidence) ? "none" : signal.Evidence)}");
        }
        if (!ledger.IsProven)
        {
            builder.AppendLine();
            builder.AppendLine(
                "NOVA 不会把该结果声明为完成；任务保持可恢复，直到每个成功信号都有独立证据。");
        }
        return builder.ToString().TrimEnd();
    }

    private void UpdateConversationLabels(string? taskId)
    {
        var rounds = string.IsNullOrWhiteSpace(taskId)
            ? 0
            : _conversationHistory.GetRoundCount(taskId);
        ConversationRoundLabel = rounds == 0
            ? "新会话"
            : $"连续会话 · 第 {rounds} 轮 · 上下文已保存";
        OnPropertyChanged(nameof(HasConversationTurns));
    }

    private void UpdateWorkspaceProfile(bool remember)
    {
        try
        {
            var profile = remember
                ? _workspaceProfiles.Remember(WorkspaceRoot, resolveProjectRoot: false)
                : _workspaceProfiles.Analyze(WorkspaceRoot, resolveProjectRoot: false);
            WorkspaceSummary = profile.BuildHint;
        }
        catch
        {
            WorkspaceSummary = "通用文件工作区";
        }
    }

    private void UseSuggestion()
    {
        PromptText = SelectedExecutionMode == AgentExecutionMode.Goal
            ? "让这个项目达到普通用户可以顺利上手、完成核心任务并愿意持续使用的状态"
            : "分析本地项目，找出体验与架构问题，生成改进计划并创建可运行原型";
    }

    private async Task PersistArtifactsAsync(TaskItem task, CancellationToken cancellationToken)
    {
        var persisted = await _artifactRepository.PersistAsync(
            task,
            Artifacts.ToArray(),
            cancellationToken);
        Artifacts.Clear();
        foreach (var artifact in persisted)
        {
            Artifacts.Add(artifact);
        }
        RefreshDeliveryCollections();
        await _knowledgeIndex.UpsertArtifactsAsync(
            task.WorkspaceRoot,
            persisted,
            cancellationToken);
        OnPropertyChanged(nameof(HasArtifacts));
        NotifyCompletionSurfaceChanged();
        ShowDeliveryCommand.RaiseCanExecuteChanged();
        SelectedArtifact = DeliveryArtifacts.FirstOrDefault()
                           ?? Artifacts.FirstOrDefault();
    }

    private void LoadArtifactsForTask(string taskId)
    {
        try
        {
            var artifacts = _artifactRepository.GetForTask(taskId);
            Artifacts.Clear();
            foreach (var artifact in artifacts)
            {
                Artifacts.Add(artifact);
            }
            RefreshDeliveryCollections();
            OnPropertyChanged(nameof(HasArtifacts));
            NotifyCompletionSurfaceChanged();
            ShowDeliveryCommand.RaiseCanExecuteChanged();
            SelectedArtifact = DeliveryArtifacts.FirstOrDefault()
                               ?? Artifacts.FirstOrDefault();
        }
        catch (InvalidOperationException exception)
        {
            Artifacts.Clear();
            DeliveryArtifacts.Clear();
            DeliveryEvidenceArtifacts.Clear();
            ArtifactVersions.Clear();
            SelectedArtifact = null;
            SelectedArtifactVersion = null;
            CoreMessage = exception.Message;
            CurrentStage = "交付物仓库需要检查";
            NotifyCompletionSurfaceChanged();
        }
    }

    private void RefreshArtifactVersions(ArtifactItem? artifact)
    {
        ArtifactVersions.Clear();
        if (artifact is not null && !string.IsNullOrWhiteSpace(artifact.Id))
        {
            foreach (var version in _artifactRepository.GetVersions(artifact.Id))
            {
                ArtifactVersions.Add(version);
            }
        }
        if (artifact is not null && ArtifactVersions.Count == 0)
        {
            ArtifactVersions.Add(artifact);
        }
        SelectedArtifactVersion = ArtifactVersions.FirstOrDefault(item =>
                                      item.Version == artifact?.Version)
                                  ?? ArtifactVersions.FirstOrDefault();
    }

    private bool CanUseSelectedArtifactFile()
        => SelectedArtifactVersion is { Location.Length: > 0 } artifact
           && File.Exists(artifact.Location);

    private void OpenDeliveryWorkspace()
    {
        if (!Directory.Exists(WorkspaceRoot))
        {
            return;
        }
        TryArtifactAction(
            () => Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true,
                ArgumentList = { WorkspaceRoot }
            }),
            "已打开本轮交付工作区");
    }

    private void OpenSelectedArtifact()
    {
        if (!CanUseSelectedArtifactFile())
        {
            return;
        }
        TryArtifactAction(() => Process.Start(new ProcessStartInfo
        {
            FileName = SelectedArtifactVersion!.Location,
            UseShellExecute = true
        }), "已使用默认应用打开交付物");
    }

    private void RevealSelectedArtifact()
    {
        if (!CanUseSelectedArtifactFile())
        {
            return;
        }
        var process = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true
        };
        process.ArgumentList.Add("/select,");
        process.ArgumentList.Add(SelectedArtifactVersion!.Location);
        TryArtifactAction(() => Process.Start(process), "已在资源管理器中定位交付物");
    }

    private void CopySelectedArtifactPath()
    {
        if (!CanUseSelectedArtifactFile())
        {
            return;
        }
        TryArtifactAction(
            () => Clipboard.SetText(SelectedArtifactVersion!.Location),
            "交付物路径已复制");
    }

    private void ContinueFromSelectedArtifact()
    {
        if (SelectedArtifactVersion is not { } artifact)
        {
            return;
        }
        PromptText =
            $"基于 NOVA 交付物“{artifact.Title}”（ID: {artifact.Id}，{artifact.VersionLabel}）继续：";
        IsDeliveryVisible = false;
        CoreStatus = "READY";
        CoreMessage = "补充你希望如何继续加工这个成果";
        CurrentStage = $"已关联 {artifact.VersionLabel}";
    }

    private void TryArtifactAction(Action action, string successMessage)
    {
        try
        {
            action();
            CurrentStage = successMessage;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            CoreMessage = exception.Message;
            CurrentStage = "交付物操作未完成";
        }
    }

    private void AddActivity(string agent, string action, string detail, ActivityKind kind = ActivityKind.Working)
    {
        Activity.Insert(0, new ActivityEntry(agent, action, detail, DateTime.Now.ToString("HH:mm:ss"), kind));
        _ = _journal.AppendAsync(
            SelectedTask?.Id ?? "system",
            agent,
            action,
            detail,
            kind,
            OverallProgress);
    }

    private void ReplaceLatestActivityAsCompleted(string agent, string action, string detail)
    {
        var item = Activity.FirstOrDefault(entry => entry.Agent == agent && entry.Action == action);
        if (item is null)
        {
            return;
        }

        var index = Activity.IndexOf(item);
        Activity[index] = item with { Kind = ActivityKind.Completed };
    }

    private void UpdateElapsed(TaskItem task)
    {
        var elapsed = DateTimeOffset.Now - _runStartedAt;
        RunTime = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
        task.Elapsed = elapsed.TotalSeconds < 60
            ? $"{Math.Max(1, (int)elapsed.TotalSeconds)} 秒"
            : $"{(int)elapsed.TotalMinutes} 分钟";
    }

    private void SeedHistory()
    {
        var snapshots = _snapshots.LoadAll();
        var recoverable = _snapshots.LoadRecoverable()
            .ToDictionary(snapshot => snapshot.TaskId, StringComparer.OrdinalIgnoreCase);
        foreach (var persisted in snapshots)
        {
            var snapshot = recoverable.TryGetValue(persisted.TaskId, out var resumed)
                ? resumed
                : persisted;
            Tasks.Add(new TaskItem
            {
                Id = snapshot.TaskId,
                Title = snapshot.Title,
                Description = snapshot.Prompt,
                CreatedAt = snapshot.CreatedAt,
                WorkspaceRoot = snapshot.WorkspaceRoot,
                Provider = snapshot.Provider,
                Model = snapshot.Model,
                ExecutionMode = snapshot.ExecutionMode,
                Draft = snapshot.Draft,
                Attachments = snapshot.Attachments ?? [],
                IsArchived = snapshot.IsArchived,
                ExecutionSequence = snapshot.ExecutionSequence,
                State = snapshot.State,
                Progress = snapshot.Progress,
                Stage = snapshot.Stage,
                Elapsed = recoverable.ContainsKey(snapshot.TaskId)
                    ? "已恢复"
                    : snapshot.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm")
            });
        }
        RecoverableCount = recoverable.Count;
        OnPropertyChanged(nameof(RecoverableCount));

        RefreshTaskLibrary();
        SelectedTask = TaskView.Cast<TaskItem>().FirstOrDefault();
    }

    private void UpdateRuntimeLabels()
    {
        RuntimeMode = IsLiveConfigured ? $"{GetProviderLabel()} Runtime 已连接" : "未连接执行模型";
        RuntimeDetail = IsLiveConfigured
            ? $"{_model} · 原生 Windows"
            : "不会修改代码 · 点击右上角模型设置";
        NotifyHumanGuidanceChanged();
    }

    private async Task InitializeAgentOsAsync()
    {
        try
        {
            var snapshot = await _agentOsKernel.BootAsync();
            ReconcileTaskStateFromExecutionLedger();
            var supervisor = await _agentSupervisor.BootAsync(snapshot.BootId);
            _selectedExecutionMode = snapshot.ExecutionMode;
            OnPropertyChanged(nameof(SelectedExecutionMode));
            OnPropertyChanged(nameof(ExecutionModeDetail));
            await _agentOsKernel.ReportServiceAsync(
                "runtime",
                "Model Runtime",
                IsLiveConfigured ? AgentOsServiceHealth.Ready : AgentOsServiceHealth.Degraded,
                IsLiveConfigured
                    ? $"{GetProviderLabel()} · {_model}"
                    : "No provider credential connected");
            await _agentOsKernel.ReportServiceAsync(
                "workspace",
                "Workspace Router",
                Directory.Exists(WorkspaceRoot)
                    ? AgentOsServiceHealth.Ready
                    : AgentOsServiceHealth.Offline,
                WorkspaceSummary);
            await _agentOsKernel.ReportServiceAsync(
                "supervisor",
                "Agent Supervisor",
                AgentOsServiceHealth.Ready,
                $"0.9 preview · {supervisor.ActiveCount} active · {supervisor.RecoverableCount} recoverable");
            RefreshAgentOsStatus();
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or System.Text.Json.JsonException)
        {
            AgentOsStatus = "AGENTOS DEGRADED";
            AddActivity(
                "AgentOS 内核",
                "内核状态持久化不可用",
                exception.Message,
                ActivityKind.System);
        }
    }

    private void RefreshAgentOsStatus()
    {
        var snapshot = _agentOsKernel.GetSnapshot();
        var ready = snapshot.Services.Count(service =>
            service.Health == AgentOsServiceHealth.Ready);
        AgentOsStatus =
            $"AGENTOS {snapshot.KernelVersion} · {SelectedExecutionMode.ToString().ToUpperInvariant()} · {ready}/{snapshot.Services.Count} SERVICES";
    }

    private void ReconcileTaskStateFromExecutionLedger()
    {
        foreach (var task in Tasks)
        {
            var projection = _agentOsKernel.GetTaskProjection(task.Id);
            if (projection?.TaskState is null
                || projection.Sequence <= task.ExecutionSequence)
            {
                continue;
            }

            task.ExecutionSequence = projection.Sequence;
            task.Progress = projection.Progress ?? task.Progress;
            task.State = projection.TaskState.Value is TaskState.Running or TaskState.Waiting
                ? TaskState.Paused
                : projection.TaskState.Value;
            task.Stage = projection.TaskState.Value is TaskState.Running or TaskState.Waiting
                ? $"可从执行事件 #{projection.Sequence} 恢复"
                : string.IsNullOrWhiteSpace(projection.Stage)
                    ? task.Stage
                    : projection.Stage;
        }
        RefreshTaskLibrary();
    }

    private async Task StartAgentOsTaskAsync(
        TaskItem task,
        bool isRecovery,
        CancellationToken cancellationToken)
    {
        try
        {
            await _agentSupervisor.AcquireAsync(task, cancellationToken);
            var committed = await _agentOsKernel.PublishTaskEventAsync(
                "task",
                "Mission Control",
                isRecovery
                    ? $"Task resumed from a durable phase boundary: {task.Title}"
                    : $"Task started in {SelectedExecutionMode} mode: {task.Title}",
                task,
                cancellationToken: cancellationToken);
            if (!isRecovery || _agentTaskGraph.GetSnapshot(task.Id) is null)
            {
                await _agentTaskGraph.CreateAsync(
                    task.Id,
                    task.Title,
                    SelectedExecutionMode,
                    cancellationToken,
                    committed.Sequence);
            }
            await _agentSupervisor.HeartbeatAsync(
                task.Id,
                task.Stage,
                forcePersist: true,
                cancellationToken: cancellationToken,
                executionSequence: committed.Sequence);
            await _snapshots.SaveAsync(task, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or System.Text.Json.JsonException)
        {
            AddActivity(
                "AgentOS 内核",
                "任务图降级为内存执行",
                exception.Message,
                ActivityKind.System);
        }
    }

    private async Task CompleteAgentOsTaskAsync(TaskItem task)
    {
        try
        {
            var committed = await _agentOsKernel.PublishTaskEventAsync(
                "task",
                "Mission Control",
                $"Task ended with state {task.State}: {task.Stage}",
                task,
                task.State == TaskState.Completed ? "INFO" : "WARN",
                CancellationToken.None);
            if (task.State is TaskState.Paused or TaskState.Waiting)
            {
                await _agentSupervisor.HeartbeatAsync(
                    task.Id,
                    task.Stage,
                    forcePersist: true,
                    cancellationToken: CancellationToken.None,
                    executionSequence: committed.Sequence);
            }
            else
            {
                await _agentTaskGraph.CompleteAsync(
                    task.Id,
                    task.State == TaskState.Completed,
                    task.Stage,
                    CancellationToken.None,
                    committed.Sequence);
            }
            await _agentSupervisor.ReleaseAsync(
                task,
                CancellationToken.None,
                committed.Sequence);
            await _snapshots.SaveAsync(task, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or System.Text.Json.JsonException)
        {
            AddActivity(
                "AgentOS 内核",
                "任务完成状态仅保留在当前会话",
                exception.Message,
                ActivityKind.System);
        }
    }

    private void RefreshExecutionReadiness()
    {
        OnPropertyChanged(nameof(RequiresRuntimeForCurrentPrompt));
        OnPropertyChanged(nameof(SubmitActionLabel));
        OnPropertyChanged(nameof(ExecutionReadinessTitle));
        OnPropertyChanged(nameof(ExecutionReadinessDetail));
    }

    private string BuildEngineeringRuntimePrompt(
        string conversationPrompt,
        TaskOutcomeContract? outcomeContract,
        AdaptiveContextPack? contextPack,
        EngineeringWorkspaceSnapshot snapshot)
    {
        var projects = snapshot.Projects.Count == 0
            ? "No recognized project manifest"
            : string.Join(", ", snapshot.Projects.Take(12));
        var command = string.IsNullOrWhiteSpace(snapshot.VerificationCommand)
            ? "No automatic verification command detected"
            : snapshot.VerificationCommand;
        var workspaceContext =
            $"""
            [AGENTOS WORKSPACE CONTEXT]
            Execution mode: {SelectedExecutionMode}
            Workspace: {snapshot.WorkspaceRoot}
            Detected projects: {projects}
            Recommended verification: {command}
            Current health: {snapshot.HealthStatus}
            Policy: {AgentExecutionPolicy.GetSystemContract(SelectedExecutionMode)}
            """;
        var proofContract = outcomeContract is null
            ? "[NOVA PROOF-OF-DONE CONTRACT]\nNo contract was generated."
            : TaskOutcomeContractService.FormatForPrompt(outcomeContract);
        var adaptiveContext = contextPack is null
            ? "[NOVA ADAPTIVE CONTEXT PACK]\nNo context pack was generated."
            : AdaptiveContextCompilerService.FormatForPrompt(contextPack);

        if (!AgentExecutionPolicy.CanMutateWorkspace(SelectedExecutionMode))
        {
            return
                $"""
                {workspaceContext}
                {proofContract}
                {adaptiveContext}

                Inspect only the evidence needed for this {SelectedExecutionMode} request.
                Clearly separate observed facts, inference, risks, and proposed next actions.
                Do not imply that a plan or code block changed the workspace.

                CONVERSATION:
                {conversationPrompt}
                """;
        }

        return EngineeringTaskRouter.EnrichPrompt(
            $"""
            [WORKSPACE EXECUTION CONTRACT]
            {workspaceContext}
            {proofContract}
            {adaptiveContext}

            This is an implementation task, not a code-writing suggestion.
            Read the relevant workspace files first.
            Unless the requested result already exists, use an approved write_text_file or replace_text_in_file call to make the change in the workspace.
            Prefer replace_text_in_file for focused changes to an existing file.
            Then run the narrowest allowlisted build or test command that proves the result.
            In the final response, list the files actually changed and the exact verification result.
            Never present a chat code block as if it were a workspace modification.

            CONVERSATION:
            {conversationPrompt}
            """);
    }

    private string GetCurrentApiKey()
        => _apiKeys.TryGetValue(_provider, out var key) ? key : string.Empty;

    private ModelRouteRecommendation RecommendModelRoute(
        EngineeringTaskProfile engineeringProfile)
    {
        try
        {
            return _modelRouter.Recommend(
                _provider,
                _model,
                SelectedExecutionMode,
                engineeringProfile,
                new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    ["openai"] = HasProviderKey("openai"),
                    ["deepseek"] = HasProviderKey("deepseek"),
                    ["kimi"] = HasProviderKey("kimi")
                },
                _agentBench.Summarize(engineeringOnly: engineeringProfile.IsEngineeringTask));
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            return new ModelRouteRecommendation(
                _provider,
                _model,
                false,
                $"AgentBench 暂不可用，保留用户选择：{exception.Message}",
                [
                    new ModelRouteCandidate(
                        _provider,
                        _model,
                        HasProviderKey(_provider),
                        0,
                        0,
                        "评测账本不可用")
                ]);
        }
    }

    private async Task RecordAgentBenchAsync(
        TaskItem task,
        string provider,
        string model,
        bool isEngineeringTask,
        bool mutationRequired,
        string outcomeStatus,
        int proofScore,
        bool verificationAttempted,
        bool verificationPassed,
        int toolCalls,
        int mutatingToolCalls,
        AdaptiveContextPack? contextPack,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        try
        {
            await _agentBench.RecordAsync(
                new AgentBenchRun(
                    task.Id,
                    provider,
                    model,
                    SelectedExecutionMode,
                    isEngineeringTask,
                    mutationRequired,
                    outcomeStatus,
                    Math.Clamp(proofScore, 0, 100),
                    verificationAttempted,
                    verificationPassed,
                    Math.Max(0, toolCalls),
                    Math.Max(0, mutatingToolCalls),
                    contextPack?.Selections.Count ?? 0,
                    contextPack?.UsedCharacters ?? 0,
                    duration,
                    DateTimeOffset.Now),
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.Security.SecurityException)
        {
            AddActivity(
                "AgentBench",
                "本轮评测仅保留在当前会话",
                exception.Message,
                ActivityKind.System);
        }
    }

    private string GetProviderLabel()
        => GetProviderLabel(_provider);

    private static string GetProviderLabel(string provider)
        => NormalizeProvider(provider) switch
        {
            "deepseek" => "DeepSeek",
            "kimi" => "Kimi",
            _ => "OpenAI"
        };

    private IAgentRuntime GetRuntime(string provider)
        => NormalizeProvider(provider) == "openai"
            ? _openAiRuntime
            : _deepSeekRuntime;

    private static string NormalizeProvider(string provider)
        => provider.Trim().ToLowerInvariant() switch
        {
            "deepseek" => "deepseek",
            "kimi" or "moonshot" => "kimi",
            _ => "openai"
        };

    private string GetMcpStatus()
    {
        try
        {
            var mcpCount = _mcpRegistry.GetEnabledServers().Count;
            var skillCount = _skillRegistry.GetSkills().Count(skill => skill.Enabled);
            return $"{mcpCount} MCP · {skillCount} SKILLS";
        }
        catch
        {
            return "扩展配置需要修复";
        }
    }

    private string LoadProviderKey(string provider, string environmentVariable)
    {
        var environmentKey = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentKey))
        {
            return environmentKey;
        }
        try
        {
            return _credentialVault.Read(provider) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async void ScheduleTimer_Tick(object? sender, EventArgs e)
    {
        if (_scheduleTickBusy || IsRunning)
        {
            return;
        }

        _scheduleTickBusy = true;
        try
        {
            var due = await _scheduleService.TryClaimNextDueAsync(
                DateTimeOffset.Now,
                CancellationToken.None);
            if (due is null)
            {
                UpdateScheduleStatus();
                return;
            }

            if (!HasProviderKey(due.Provider))
            {
                await _scheduleService.RequeueAsync(
                    due,
                    DateTimeOffset.Now.AddMinutes(5),
                    CancellationToken.None);
                ScheduleStatus = $"等待 {GetProviderLabel(due.Provider)} 密钥";
                AddActivity(
                    "计划调度器",
                    "计划任务已延期",
                    $"{due.Name} · 缺少 {GetProviderLabel(due.Provider)} API Key，5 分钟后重试",
                    ActivityKind.Waiting);
                return;
            }

            if (!Directory.Exists(due.WorkspaceRoot))
            {
                await _scheduleService.RequeueAsync(
                    due,
                    DateTimeOffset.Now.AddMinutes(30),
                    CancellationToken.None);
                ScheduleStatus = "计划任务工作区不可用";
                AddActivity(
                    "计划调度器",
                    "计划任务已延期",
                    $"{due.Name} · 工作区不存在，30 分钟后重试",
                    ActivityKind.Waiting);
                return;
            }

            var previousTask = SelectedTask;
            SelectedTask = null;
            if (previousTask is not null)
            {
                await _snapshots.SaveAsync(previousTask, CancellationToken.None);
            }

            _provider = NormalizeProvider(due.Provider);
            _model = due.Model;
            SelectedExecutionMode = due.ExecutionMode;
            WorkspaceRoot = Path.GetFullPath(due.WorkspaceRoot);
            UpdateRuntimeLabels();
            OnPropertyChanged(nameof(SelectedProvider));
            OnPropertyChanged(nameof(SelectedModel));
            OnPropertyChanged(nameof(IsLiveConfigured));
            PromptText = due.Prompt;
            AddActivity(
                "计划调度器",
                "启动计划任务",
                $"{due.Name} · {GetProviderLabel()} · {_model}",
                ActivityKind.System);
            await StartTaskAsync(
                null,
                due.Prompt,
                isContinuation: false,
                isRecovery: false,
                inputAttachmentsOverride: []);
        }
        catch (Exception exception)
        {
            ScheduleStatus = "计划调度器需要检查";
            AddActivity(
                "计划调度器",
                "调度失败",
                exception.Message,
                ActivityKind.System);
        }
        finally
        {
            _scheduleTickBusy = false;
            UpdateScheduleStatus();
        }
    }

    private void UpdateScheduleStatus()
        => ScheduleStatus = $"{_scheduleService.GetEnabledCount()} 个计划任务";

    public void RefreshScheduleStatus()
        => UpdateScheduleStatus();

    private static string CreateTitle(string prompt)
    {
        var trimmed = prompt.Trim();
        return trimmed.Length <= 18 ? trimmed : $"{trimmed[..18]}…";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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

    private sealed record PipelineStep(
        string Agent,
        string Action,
        string Detail,
        double Progress,
        int AgentCount,
        bool RequiresApproval);
}
