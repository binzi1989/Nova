using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NovaDesktop.Services;

namespace NovaDesktop;

public partial class AgentOsCenterWindow : Window
{
    private readonly AgentOsKernel _kernel;
    private readonly AgentTaskGraphService _taskGraph;
    private readonly AgentResourceGovernor _resourceGovernor;
    private readonly string? _taskId;
    private readonly DispatcherTimer _refreshTimer;

    public AgentOsCenterWindow(
        AgentOsKernel kernel,
        AgentTaskGraphService taskGraph,
        AgentResourceGovernor resourceGovernor,
        string? taskId)
    {
        InitializeComponent();
        _kernel = kernel;
        _taskGraph = taskGraph;
        _resourceGovernor = resourceGovernor;
        _taskId = taskId;
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += (_, _) => RefreshState();
        Loaded += (_, _) =>
        {
            RefreshState();
            _refreshTimer.Start();
        };
        Closed += (_, _) => _refreshTimer.Stop();
    }

    private void RefreshState()
    {
        var kernel = _kernel.GetSnapshot();
        var graph = _taskGraph.GetSnapshot(_taskId);
        var resources = _resourceGovernor.GetSnapshot();
        var readyServices = kernel.Services.Count(service =>
            service.Health == Models.AgentOsServiceHealth.Ready);

        KernelStatusText.Text = $"ONLINE · v{kernel.KernelVersion}";
        KernelMetaText.Text = $"BOOT {kernel.BootId}  ·  UPTIME {kernel.UptimeLabel}";
        ModeText.Text = kernel.ExecutionMode.ToString().ToUpperInvariant();
        ServiceSummaryText.Text = $"{readyServices}/{kernel.Services.Count} READY";
        GraphProgressText.Text = graph is null
            ? "STANDBY"
            : $"{graph.OverallProgress:0}%";
        GraphTitleText.Text = graph is null
            ? "暂无活动任务 · 提交任务后将生成执行 DAG"
            : $"{graph.Title} · {graph.Mode} · {graph.Nodes.Count} 个执行节点";

        ServicesList.ItemsSource = kernel.Services;
        GraphList.ItemsSource = graph?.Nodes;
        EventsList.ItemsSource = kernel.RecentEvents.Take(60);

        AgentsText.Text = $"{resources.ActiveAgents}/{resources.Policy.MaxConcurrentAgents}";
        ToolsText.Text = $"{resources.ToolCalls}/{resources.Policy.MaxToolCallsPerTask}";
        RoundsText.Text = $"{resources.ModelRounds}/{resources.Policy.MaxModelRounds}";
        BudgetText.Text = resources.LimitReason is not null
            ? "LIMITED"
            : resources.IsPaused
                ? "PAUSED"
                : "READY";
        BudgetText.ToolTip = resources.LimitReason
            ?? (resources.IsPaused
                ? "下一模型轮次、工具和并行批次正在安全点等待"
                : "暂停与预算硬限制已启用");
        KernelPulse.Fill = readyServices == kernel.Services.Count
            ? new SolidColorBrush(Color.FromRgb(107, 229, 169))
            : new SolidColorBrush(Color.FromRgb(255, 196, 112));
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshState();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
