using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using NovaDesktop.Models;
using NovaDesktop.Services;
using NovaDesktop.ViewModels;

namespace NovaDesktop;

public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private readonly MainViewModel _viewModel;
    private HwndSource? _windowSource;
    private Storyboard? _coreBreathing;
    private Storyboard? _orbitAnimation;
    private Storyboard? _flowAnimation;
    private bool _motionRunning;
    private bool _shutdownReady;
    private bool _shutdownInProgress;
    private static readonly Brush IdleBorder = new SolidColorBrush(Color.FromRgb(48, 58, 79));
    private static readonly Brush ActiveBorder = new SolidColorBrush(Color.FromRgb(117, 240, 255));

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        DataContext = _viewModel;

        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        StateChanged += (_, _) =>
        {
            UpdateMaximizeGlyph();
            UpdateMotionState();
            UpdateResponsiveLayout();
        };
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        Activated += (_, _) => UpdateMotionState();
        Deactivated += (_, _) => UpdateMotionState();
        Loaded += (_, _) =>
        {
            PromptBox.Focus();
            UpdateNodeStates(0);
            _coreBreathing = FindResource("CoreBreathing") as Storyboard;
            _orbitAnimation = FindResource("OrbitAnimation") as Storyboard;
            _flowAnimation = FindResource("FlowAnimation") as Storyboard;
            UpdateMotionState();
            UpdateResponsiveLayout();
        };
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowProcedure);
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _windowSource?.RemoveHook(WindowProcedure);
        _windowSource = null;
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownReady)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownInProgress)
        {
            return;
        }

        _shutdownInProgress = true;
        IsEnabled = false;
        try
        {
            await _viewModel.PrepareForShutdownAsync();
            _shutdownReady = true;
            Close();
        }
        catch (Exception exception)
        {
            _shutdownInProgress = false;
            IsEnabled = true;
            MessageBox.Show(
                $"NOVA 无法确认执行状态已经安全落盘，因此保留当前窗口。\n\n{exception.Message}",
                "NOVA 退出保护",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static IntPtr WindowProcedure(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        var monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return IntPtr.Zero;
        }

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var workArea = monitorInfo.WorkArea;
        var monitorArea = monitorInfo.MonitorArea;
        minMaxInfo.MaxPosition.X = Math.Abs(workArea.Left - monitorArea.Left);
        minMaxInfo.MaxPosition.Y = Math.Abs(workArea.Top - monitorArea.Top);
        minMaxInfo.MaxSize.X = Math.Abs(workArea.Right - workArea.Left);
        minMaxInfo.MaxSize.Y = Math.Abs(workArea.Bottom - workArea.Top);
        minMaxInfo.MaxTrackSize = minMaxInfo.MaxSize;
        Marshal.StructureToPtr(minMaxInfo, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Workspace_Click(object sender, RoutedEventArgs e)
        => OpenWorkspacePicker(this);

    private void Attachment_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "选择图片或文件",
            Multiselect = true,
            CheckFileExists = true,
            Filter =
                "NOVA 可理解的文件|*.png;*.jpg;*.jpeg;*.webp;*.txt;*.md;*.json;*.jsonc;*.xml;*.yaml;*.yml;*.toml;*.ini;*.cfg;*.conf;*.log;*.csv;*.tsv;*.cs;*.csproj;*.sln;*.js;*.jsx;*.ts;*.tsx;*.py;*.java;*.kt;*.go;*.rs;*.cpp;*.c;*.h;*.hpp;*.swift;*.php;*.rb;*.ps1;*.html;*.css;*.vue;*.sql;*.graphql;*.wxml;*.wxss|图片|*.png;*.jpg;*.jpeg;*.webp|文本与代码|*.txt;*.md;*.json;*.xml;*.yaml;*.yml;*.toml;*.csv;*.cs;*.js;*.ts;*.py;*.java;*.go;*.rs;*.html;*.css;*.sql",
            RestoreDirectory = true
        };
        if (picker.ShowDialog(this) == true)
        {
            TryAddInputAttachments(picker.FileNames);
        }
    }

    private void Composer_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Composer_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            TryAddInputAttachments(paths);
        }
        e.Handled = true;
    }

    private void TryAddInputAttachments(IEnumerable<string> paths)
    {
        try
        {
            _viewModel.AddInputAttachments(paths);
            PromptBox.Focus();
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            MessageBox.Show(
                exception.Message,
                "无法添加附件",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void OpenWorkspacePicker(Window owner)
    {
        var picker = new WorkspacePickerWindow(
            _viewModel.WorkspaceProfiles,
            _viewModel.WorkspaceRoot)
        {
            Owner = owner
        };
        if (picker.ShowDialog() == true
            && !string.IsNullOrWhiteSpace(picker.SelectedWorkspaceRoot))
        {
            _viewModel.SetWorkspaceRoot(picker.SelectedWorkspaceRoot);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
        => OpenSettings(this);

    private void OpenSettings(Window owner)
    {
        var settings = new SettingsWindow(
            _viewModel.SelectedProvider,
            _viewModel.SelectedModel,
            _viewModel.HasProviderKey("openai"),
            _viewModel.HasProviderKey("deepseek"),
            _viewModel.HasProviderKey("kimi"),
            _viewModel.IsProviderKeyPersisted("openai"),
            _viewModel.IsProviderKeyPersisted("deepseek"),
            _viewModel.IsProviderKeyPersisted("kimi"))
        {
            Owner = owner
        };
        if (settings.ShowDialog() == true)
        {
            _viewModel.ConfigureLiveRuntime(
                settings.ApiKey,
                settings.SelectedProvider,
                settings.SelectedModel,
                settings.ClearRequested,
                settings.RememberKey);
        }
    }

    private void Schedules_Click(object sender, RoutedEventArgs e)
    {
        var schedules = new ScheduleWindow(
            _viewModel.ScheduleService,
            new AgentScheduleCreationContext(
                _viewModel.WorkspaceRoot,
                _viewModel.SelectedProvider,
                _viewModel.SelectedModel,
                _viewModel.HasProviderKey(_viewModel.SelectedProvider),
                _viewModel.SelectedExecutionMode))
        {
            Owner = this
        };
        schedules.ShowDialog();
        _viewModel.RefreshScheduleStatus();
    }

    private void Extensions_Click(object sender, RoutedEventArgs e)
        => OpenExtensions(this);

    private void MoreTools_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is not { } menu)
        {
            return;
        }

        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = -116;
        menu.IsOpen = true;
    }

    private void OpenExtensions(Window owner)
    {
        var extensions = new ExtensionCenterWindow(
            _viewModel.McpRegistry,
            _viewModel.SkillRegistry,
            _viewModel.WorkspaceRoot,
            _viewModel.CapabilityIntent)
        {
            Owner = owner
        };
        extensions.ShowDialog();
        _viewModel.RefreshExtensionStatus();
    }

    private void QuickStart_Click(object sender, RoutedEventArgs e)
    {
        QuickStartWindow? quickStart = null;
        quickStart = new QuickStartWindow(
            ReadQuickStartSnapshot,
            () => OpenWorkspacePicker(quickStart!),
            () => OpenSettings(quickStart!),
            () => OpenExtensions(quickStart!),
            () =>
            {
                _viewModel.SelectedExecutionMode = AgentExecutionMode.Goal;
                _viewModel.PromptText =
                    "让当前本地项目达到普通用户可以顺利上手、完成核心任务，"
                    + "并愿意持续使用的状态。请自主探索问题、选择解法并用证据证明结果。";
            })
        {
            Owner = this
        };
        if (quickStart.ShowDialog() == true)
        {
            Dispatcher.BeginInvoke(() => PromptBox.Focus());
        }
    }

    private QuickStartSnapshot ReadQuickStartSnapshot()
    {
        try
        {
            return new QuickStartSnapshot(
                _viewModel.WorkspaceRoot,
                _viewModel.IsLiveConfigured,
                $"{_viewModel.SelectedProvider} · {_viewModel.SelectedModel}",
                _viewModel.McpRegistry.GetServers().Count,
                _viewModel.SkillRegistry.GetSkills().Count);
        }
        catch
        {
            return new QuickStartSnapshot(
                _viewModel.WorkspaceRoot,
                _viewModel.IsLiveConfigured,
                $"{_viewModel.SelectedProvider} · {_viewModel.SelectedModel}",
                0,
                0);
        }
    }

    private void Engineering_Click(object sender, RoutedEventArgs e)
    {
        var engineering = new EngineeringCenterWindow(
            _viewModel.EngineeringWorkspace,
            _viewModel.WorkspaceRoot,
            workspace => _viewModel.SetWorkspaceRoot(workspace))
        {
            Owner = this
        };
        engineering.ShowDialog();
    }

    private void AgentOs_Click(object sender, RoutedEventArgs e)
    {
        var agentOs = new AgentOsCenterWindow(
            _viewModel.AgentOsKernel,
            _viewModel.AgentTaskGraph,
            _viewModel.AgentResourceGovernor,
            _viewModel.SelectedTask?.Id)
        {
            Owner = this
        };
        agentOs.ShowDialog();
    }

    private void Cognition_Click(object sender, RoutedEventArgs e)
    {
        var cognition = new CognitionCenterWindow(
            _viewModel.ProductivityInsights,
            _viewModel.KnowledgeGraph,
            _viewModel.KnowledgeIndex,
            _viewModel.ArtifactRepository,
            _viewModel.SnapshotService,
            _viewModel.SkillRegistry,
            _viewModel.McpRegistry,
            _viewModel.ScheduleService,
            _viewModel.WorkspaceRoot)
        {
            Owner = this
        };
        cognition.ShowDialog();
    }

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdateMaximizeGlyph()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Data = Geometry.Parse(isMaximized
            ? "M4,1.5 H11.5 V9 H9 M1.5,4 H9 V11.5 H1.5 Z"
            : "M1.5,1.5 H11.5 V11.5 H1.5 Z");
        MaximizeButton.ToolTip = isMaximized ? "还原" : "最大化";
        AutomationProperties.SetName(MaximizeButton, isMaximized ? "还原" : "最大化");
    }

    private void PromptBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        TrySubmitTask();
        e.Handled = true;
    }

    private void Submit_Click(object sender, RoutedEventArgs e)
        => TrySubmitTask();

    private void TrySubmitTask()
    {
        if (_viewModel.RequiresRuntimeForCurrentPrompt)
        {
            Settings_Click(this, new RoutedEventArgs());
            if (_viewModel.RequiresRuntimeForCurrentPrompt)
            {
                return;
            }
        }

        if (_viewModel.SubmitCommand.CanExecute(null))
        {
            _viewModel.SubmitCommand.Execute(null);
        }
    }

    private void FocusPrompt_Click(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(() => PromptBox.Focus());
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentStep))
        {
            UpdateNodeStates(_viewModel.CurrentStep);
        }
        if (e.PropertyName is nameof(MainViewModel.IsRunning)
            or nameof(MainViewModel.IsPaused)
            or nameof(MainViewModel.IsDeliveryVisible))
        {
            UpdateMotionState();
        }
    }

    private void UpdateNodeStates(int step)
    {
        SetNodeState(PlanNode, step is 1);
        SetNodeState(ResearchNode, step is >= 2 and <= 4);
        SetNodeState(CreateNode, step is 5 or 6);
        SetNodeState(ReviewNode, step >= 7);
    }

    private static void SetNodeState(Border node, bool active)
    {
        node.BorderBrush = active ? ActiveBorder : IdleBorder;
        node.RenderTransformOrigin = new Point(.5, .5);
        if (node.RenderTransform is not ScaleTransform scale)
        {
            scale = new ScaleTransform(1, 1);
            node.RenderTransform = scale;
        }

        var duration = TimeSpan.FromMilliseconds(110);
        node.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(active ? 1 : .62, duration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        scale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(active ? 1.025 : 1, duration));
        scale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(active ? 1.025 : 1, duration));
    }

    private void UpdateMotionState()
    {
        if (!IsLoaded || _coreBreathing is null)
        {
            return;
        }

        // The legacy mission-orbit canvas is retained only for compatibility.
        // ConversationStage now owns the visible, state-driven motion layer.
        var shouldRun = false;
        if (shouldRun == _motionRunning)
        {
            return;
        }

        _motionRunning = shouldRun;
        if (shouldRun)
        {
            _coreBreathing.Begin(this, HandoffBehavior.SnapshotAndReplace, true);
            _orbitAnimation?.Begin(this, HandoffBehavior.SnapshotAndReplace, true);
            _flowAnimation?.Begin(this, HandoffBehavior.SnapshotAndReplace, true);
            return;
        }

        _coreBreathing.Remove(this);
        _orbitAnimation?.Remove(this);
        _flowAnimation?.Remove(this);
        CoreGlow.Opacity = _viewModel.IsRunning ? .34 : .22;
        FlowDash.StrokeDashOffset = 0;
    }

    private void UpdateResponsiveLayout()
    {
        if (!IsLoaded)
        {
            return;
        }

        var compact = ActualWidth < 1360;
        var narrow = ActualWidth < 1220;
        RightRailColumn.Width = compact ? new GridLength(0) : GridLength.Auto;
        LeftRailColumn.Width = new GridLength(narrow ? 210 : 242);

    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public Rectangle MonitorArea;
        public Rectangle WorkArea;
        public uint Flags;
    }
}
