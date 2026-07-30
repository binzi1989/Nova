using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NovaDesktop.Services;

namespace NovaDesktop;

public partial class WorkspacePickerWindow : Window
{
    private readonly WorkspaceProfileService _profiles;
    private string _candidatePath;
    private WorkspaceProfile? _selectedProfile;
    private bool _ready;

    public WorkspacePickerWindow(
        WorkspaceProfileService profiles,
        string currentWorkspace)
    {
        InitializeComponent();
        _profiles = profiles;
        _candidatePath = currentWorkspace;
        RecentList.ItemsSource = _profiles.LoadRecent();
        _ready = true;
        Loaded += async (_, _) => await AnalyzeCandidateAsync();
    }

    public string? SelectedWorkspaceRoot { get; private set; }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "选择任务根目录",
            InitialDirectory = Directory.Exists(_candidatePath)
                ? _candidatePath
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Multiselect = false
        };
        if (picker.ShowDialog(this) == true)
        {
            _candidatePath = picker.FolderName;
            RecentList.SelectedItem = null;
            await AnalyzeCandidateAsync();
        }
    }

    private async void RecentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecentList.SelectedItem is not WorkspaceProfile profile || !profile.Exists)
        {
            return;
        }
        _candidatePath = profile.Root;
        await AnalyzeCandidateAsync();
    }

    private async void ResolveRootCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready)
        {
            await AnalyzeCandidateAsync();
        }
    }

    private async Task AnalyzeCandidateAsync()
    {
        UseWorkspaceButton.IsEnabled = false;
        StatusText.Text = "正在快速识别工程边界…";
        var candidate = _candidatePath;
        var resolveRoot = ResolveRootCheck.IsChecked == true;
        try
        {
            var profile = await Task.Run(() => _profiles.Analyze(candidate, resolveRoot));
            if (!candidate.Equals(_candidatePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _selectedProfile = profile;
            SelectedNameText.Text = profile.Name;
            SelectedPathText.Text = profile.Root;
            SelectedProfileText.Text = profile.KindLabel
                                       + (profile.IsGitRepository ? " · Git 已识别" : " · 非 Git 工作区");
            BuildHintText.Text = profile.BuildHint;
            StatusText.Text = resolveRoot && !profile.Root.Equals(candidate, StringComparison.OrdinalIgnoreCase)
                ? "已从所选子目录自动提升到工程根目录"
                : "根目录已就绪";
            UseWorkspaceButton.IsEnabled = profile.Exists;
        }
        catch (Exception exception)
        {
            _selectedProfile = null;
            SelectedNameText.Text = "无法读取目录";
            SelectedPathText.Text = candidate;
            SelectedProfileText.Text = exception.Message;
            BuildHintText.Text = "构建计划不可用";
            StatusText.Text = "请选择另一个目录";
        }
    }

    private void UseWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is not { Exists: true } profile)
        {
            return;
        }
        SelectedWorkspaceRoot = _profiles.Remember(
            profile.Root,
            resolveProjectRoot: false).Root;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
