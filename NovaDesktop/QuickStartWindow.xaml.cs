using System.Windows;
using System.Windows.Input;

namespace NovaDesktop;

public sealed record QuickStartSnapshot(
    string WorkspaceRoot,
    bool IsModelConnected,
    string ModelLabel,
    int McpCount,
    int SkillCount);

public partial class QuickStartWindow : Window
{
    private readonly Func<QuickStartSnapshot> _readSnapshot;
    private readonly Action _chooseWorkspace;
    private readonly Action _configureModel;
    private readonly Action _openExtensions;
    private readonly Action _useStarterGoal;

    public QuickStartWindow(
        Func<QuickStartSnapshot> readSnapshot,
        Action chooseWorkspace,
        Action configureModel,
        Action openExtensions,
        Action useStarterGoal)
    {
        InitializeComponent();
        _readSnapshot = readSnapshot;
        _chooseWorkspace = chooseWorkspace;
        _configureModel = configureModel;
        _openExtensions = openExtensions;
        _useStarterGoal = useStarterGoal;
        Loaded += (_, _) => RefreshSnapshot();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
        => Close();

    private void Workspace_Click(object sender, RoutedEventArgs e)
    {
        _chooseWorkspace();
        RefreshSnapshot();
    }

    private void Model_Click(object sender, RoutedEventArgs e)
    {
        _configureModel();
        RefreshSnapshot();
    }

    private void Extensions_Click(object sender, RoutedEventArgs e)
    {
        _openExtensions();
        RefreshSnapshot();
    }

    private void StarterGoal_Click(object sender, RoutedEventArgs e)
    {
        _useStarterGoal();
        DialogResult = true;
    }

    private void RefreshSnapshot()
    {
        var snapshot = _readSnapshot();
        WorkspaceStatusText.Text = snapshot.WorkspaceRoot;
        ModelStatusText.Text = snapshot.IsModelConnected
            ? $"已连接 · {snapshot.ModelLabel}"
            : "未连接 · 执行前必须完成";
        ModelStatusText.Foreground = snapshot.IsModelConnected
            ? System.Windows.Media.Brushes.LightGreen
            : System.Windows.Media.Brushes.LightSalmon;
        ExtensionStatusText.Text = snapshot.McpCount + snapshot.SkillCount == 0
            ? "可选 · 尚未绑定 MCP 或 Skill"
            : $"{snapshot.McpCount} MCP · {snapshot.SkillCount} Skills";
    }
}
