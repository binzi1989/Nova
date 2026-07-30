using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NovaDesktop.Services;

namespace NovaDesktop;

public partial class CognitionCenterWindow : Window
{
    private readonly ProductivityInsightsService _productivity;
    private readonly KnowledgeGraphService _knowledgeGraph;
    private readonly KnowledgeIndexService _knowledgeIndex;
    private readonly ArtifactRepositoryService _artifacts;
    private readonly TaskSnapshotService _snapshots;
    private readonly SkillRegistryService _skills;
    private readonly McpRegistryService _mcp;
    private readonly AgentScheduleService _schedules;
    private readonly string _workspaceRoot;
    private KnowledgeGraphSnapshot _currentGraph = new(DateTimeOffset.MinValue, [], []);

    public CognitionCenterWindow(
        ProductivityInsightsService productivity,
        KnowledgeGraphService knowledgeGraph,
        KnowledgeIndexService knowledgeIndex,
        ArtifactRepositoryService artifacts,
        TaskSnapshotService snapshots,
        SkillRegistryService skills,
        McpRegistryService mcp,
        AgentScheduleService schedules,
        string workspaceRoot)
    {
        InitializeComponent();
        _productivity = productivity;
        _knowledgeGraph = knowledgeGraph;
        _knowledgeIndex = knowledgeIndex;
        _artifacts = artifacts;
        _snapshots = snapshots;
        _skills = skills;
        _mcp = mcp;
        _schedules = schedules;
        _workspaceRoot = workspaceRoot;
        Loaded += async (_, _) =>
        {
            RefreshSummary();
            RefreshKnowledgeLibrary();
            await SynchronizeGraphAsync();
        };
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

    private void RefreshSummary_Click(object sender, RoutedEventArgs e)
        => RefreshSummary();

    private void RefreshSummary()
    {
        try
        {
            var days = int.TryParse(
                (PeriodBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                out var parsed)
                ? parsed
                : 7;
            var summary = _productivity.Generate(days);
            ScoreText.Text = summary.ProductivityScore.ToString();
            CompletionText.Text = $"{summary.CompletionRate:0.#}%";
            CompletedDetailText.Text = $"{summary.CompletedTasks} / {summary.TotalTasks} tasks";
            FocusText.Text = summary.FocusMinutes >= 60
                ? $"{summary.FocusMinutes / 60:0.0} h"
                : $"{summary.FocusMinutes:0.#} min";
            ActiveDaysText.Text = $"{summary.ActiveDays} active days";
            CycleText.Text = summary.AverageCycleMinutes <= 0
                ? "—"
                : summary.AverageCycleMinutes >= 60
                    ? $"{summary.AverageCycleMinutes / 60:0.0} h"
                    : $"{summary.AverageCycleMinutes:0} min";
            BlockedText.Text = summary.BlockedTasks.ToString();
            ScheduleText.Text = $"{summary.EnabledSchedules} schedules";
            PeakDayText.Text = $"峰值 {summary.PeakDay}";
            TrendCanvas.Days = summary.DailyTrend;
            InsightsList.ItemsSource = summary.Insights;
            SummaryStatusText.Text =
                $"生成于 {summary.GeneratedAt:yyyy-MM-dd HH:mm:ss} · {summary.ActivityCount} 条真实活动记录";
        }
        catch (Exception exception)
        {
            SummaryStatusText.Text = exception.Message;
        }
    }

    private async void SyncGraph_Click(object sender, RoutedEventArgs e)
        => await SynchronizeGraphAsync();

    private async Task SynchronizeGraphAsync()
    {
        try
        {
            GraphStatusText.Text = "正在同步任务、Skills、MCP 与计划任务…";
            _currentGraph = await _knowledgeGraph.SynchronizeAsync(
                _snapshots.LoadAll(),
                _skills.GetSkills(),
                _mcp.GetServers(),
                _schedules.GetSchedules(),
                CancellationToken.None,
                _knowledgeIndex.GetDocuments(),
                _artifacts.GetLatest());
            ApplyGraphFilter();
            GraphStatusText.Text =
                $"已同步 · {_currentGraph.UpdatedAt:yyyy-MM-dd HH:mm:ss} · {_knowledgeGraph.GraphPath}";
        }
        catch (Exception exception)
        {
            GraphStatusText.Text = exception.Message;
        }
    }

    private async void IndexKnowledge_Click(object sender, RoutedEventArgs e)
    {
        IndexKnowledgeButton.IsEnabled = false;
        try
        {
            KnowledgeLibraryStatusText.Text = "正在安全扫描并增量索引工作区…";
            var result = await _knowledgeIndex.IndexWorkspaceAsync(
                _workspaceRoot,
                CancellationToken.None);
            RefreshKnowledgeLibrary();
            KnowledgeLibraryStatusText.Text =
                $"完成：新增 {result.IndexedFiles} · 复用 {result.ReusedFiles} · 移除 {result.RemovedFiles} · 跳过 {result.SkippedFiles}";
            await SynchronizeGraphAsync();
        }
        catch (Exception exception)
        {
            KnowledgeLibraryStatusText.Text = exception.Message;
        }
        finally
        {
            IndexKnowledgeButton.IsEnabled = true;
        }
    }

    private void SearchKnowledge_Click(object sender, RoutedEventArgs e)
        => SearchKnowledge();

    private void KnowledgeSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SearchKnowledge();
            e.Handled = true;
        }
    }

    private void SearchKnowledge()
    {
        try
        {
            var results = _knowledgeIndex.Search(
                KnowledgeSearchBox.Text,
                _workspaceRoot,
                20);
            KnowledgeResultList.ItemsSource = results;
            KnowledgeSearchStatusText.Text = $"找到 {results.Count} 条引用结果";
        }
        catch (Exception exception)
        {
            KnowledgeSearchStatusText.Text = exception.Message;
        }
    }

    private void RefreshKnowledgeLibrary()
    {
        try
        {
            var documents = _knowledgeIndex.GetDocuments(_workspaceRoot);
            KnowledgeDocumentList.ItemsSource = documents.Take(100).ToArray();
            KnowledgeDocumentCountText.Text = documents.Count.ToString();
            KnowledgeChunkCountText.Text = documents.Sum(document => document.ChunkCount).ToString();
            KnowledgeSizeText.Text = FormatBytes(documents.Sum(document => document.SizeBytes));
            KnowledgeWorkspaceText.Text = _workspaceRoot;
            KnowledgeLibraryStatusText.Text = documents.Count == 0
                ? "尚未索引当前工作区。"
                : $"最近更新：{documents.Max(document => document.IndexedAt):yyyy-MM-dd HH:mm:ss}";
        }
        catch (Exception exception)
        {
            KnowledgeLibraryStatusText.Text = exception.Message;
        }
    }

    private static string FormatBytes(long bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / 1024d / 1024d:0.0} MB"
            : $"{bytes / 1024d:0.0} KB";

    private void GraphSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => ApplyGraphFilter();

    private void ApplyGraphFilter()
    {
        var filter = GraphSearchBox.Text.Trim();
        GraphCanvas.Graph = _currentGraph;
        GraphCanvas.Filter = filter;
        var nodes = filter.Length == 0
            ? _currentGraph.Nodes
            : _currentGraph.Nodes.Where(node =>
                node.Label.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || node.Kind.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || node.Detail.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                .ToArray();
        GraphNodeList.ItemsSource = nodes
            .OrderByDescending(node => node.IsManual)
            .ThenByDescending(node => node.Weight)
            .Take(100)
            .ToArray();
        GraphCountText.Text = $"{nodes.Count} nodes · {_currentGraph.Edges.Count} edges";
    }

    private async void AddKnowledge_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var related = (GraphNodeList.SelectedItem as KnowledgeNode)?.Id;
            var node = await _knowledgeGraph.AddKnowledgeAsync(
                KnowledgeLabelBox.Text,
                KnowledgeDetailBox.Text,
                related,
                CancellationToken.None);
            _currentGraph = _knowledgeGraph.GetSnapshot();
            KnowledgeLabelBox.Clear();
            KnowledgeDetailBox.Clear();
            ApplyGraphFilter();
            GraphNodeList.SelectedItem = _currentGraph.Nodes.FirstOrDefault(item => item.Id == node.Id);
            GraphStatusText.Text = related is null
                ? $"已保存知识“{node.Label}”。"
                : $"已保存知识“{node.Label}”并连接到所选节点。";
        }
        catch (Exception exception)
        {
            GraphStatusText.Text = exception.Message;
        }
    }
}
