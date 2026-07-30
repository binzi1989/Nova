using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using NovaDesktop.Services;

namespace NovaDesktop;

public partial class ExtensionCenterWindow : Window
{
    private readonly McpRegistryService _mcpRegistry;
    private readonly SkillRegistryService _skillRegistry;
    private readonly string _workspaceRoot;
    private readonly string _capabilityIntent;
    private readonly McpDiscoveryService _mcpDiscovery;
    private readonly CapabilityCompassService _capabilityCompass;
    private readonly CapabilityMarketplaceService _marketplace;
    private readonly ObservableCollection<McpDiscoverySelection> _discoverySelections = [];
    private readonly ObservableCollection<CapabilityRecommendation> _capabilityRecommendations = [];
    private readonly ObservableCollection<MarketplaceCatalogItem> _marketplaceItems = [];

    public ExtensionCenterWindow(
        McpRegistryService mcpRegistry,
        SkillRegistryService skillRegistry,
        string workspaceRoot,
        string? capabilityIntent = null)
    {
        InitializeComponent();
        _mcpRegistry = mcpRegistry;
        _skillRegistry = skillRegistry;
        _workspaceRoot = workspaceRoot;
        _capabilityIntent = capabilityIntent?.Trim() ?? string.Empty;
        _mcpDiscovery = new McpDiscoveryService(workspaceRoot);
        _capabilityCompass = new CapabilityCompassService(mcpRegistry, skillRegistry);
        _marketplace = new CapabilityMarketplaceService(
            mcpRegistry,
            skillRegistry,
            workspaceRoot);
        Loaded += (_, _) =>
        {
            McpTransportBox.SelectedIndex = 0;
            MarketplaceKindBox.SelectedIndex = 0;
            DiscoveryList.ItemsSource = _discoverySelections;
            CapabilityList.ItemsSource = _capabilityRecommendations;
            MarketplaceList.ItemsSource = _marketplaceItems;
            CapabilityIntentBox.Text = _capabilityIntent;
            RefreshMcpServers();
            RefreshSkills();
            RefreshMarketplace();
            RefreshCapabilityCompass();
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

    private void AnalyzeCapabilities_Click(object sender, RoutedEventArgs e)
        => RefreshCapabilityCompass();

    private void RefreshCapabilityCompass()
    {
        try
        {
            var report = _capabilityCompass.Analyze(
                CapabilityIntentBox.Text,
                _workspaceRoot);
            _capabilityRecommendations.Clear();
            foreach (var recommendation in report.Recommendations)
            {
                _capabilityRecommendations.Add(recommendation);
            }
            CompassWorkspaceText.Text = report.WorkspaceSignal;
            CompassReadyText.Text = report.ReadyCount.ToString();
            CompassSuggestedText.Text = report.SuggestedCount.ToString();
            CompassSummaryText.Text = report.Summary;
            CompassStatusText.Text = string.IsNullOrWhiteSpace(report.Intent)
                ? "先在左侧写下任务目标，司南会按任务重新排序；当前展示已安装能力概况。"
                : "研判完成。已就绪能力会按需进入模型上下文；停用或缺失能力不会被静默挂载。";
        }
        catch (Exception exception)
        {
            CompassStatusText.Text = $"能力研判失败：{exception.Message}";
        }
    }

    private async void CapabilityAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CapabilityRecommendation recommendation })
        {
            return;
        }

        try
        {
            switch (recommendation.Action)
            {
                case CapabilityAction.EnableMcp:
                {
                    var server = _mcpRegistry.GetServers().FirstOrDefault(item =>
                        item.Name.Equals(recommendation.Id, StringComparison.OrdinalIgnoreCase));
                    if (server is null)
                    {
                        CompassStatusText.Text = "这个 MCP 绑定已经不存在，请重新研判。";
                        break;
                    }
                    if (MessageBox.Show(
                            this,
                            $"能力司南建议启用“{server.Name}”。\n\n"
                            + $"{recommendation.Reason}\n"
                            + $"{recommendation.PermissionSummary}\n\n"
                            + FormatExecutionImpact(server)
                            + "\n\n现在只启用绑定，不立即连接。是否允许？",
                            "由你确认 MCP 能力",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    {
                        CompassStatusText.Text = $"没有启用 {server.Name}，任务仍可使用内建能力继续。";
                        break;
                    }
                    await _mcpRegistry.SetEnabledAsync(
                        server.Name,
                        true,
                        CancellationToken.None);
                    RefreshMcpServers(server.Name);
                    CompassStatusText.Text = $"已启用 {server.Name}；真正连接和调用时仍会再次请求授权。";
                    RefreshCapabilityCompass();
                    break;
                }
                case CapabilityAction.EnableSkill:
                {
                    var skill = _skillRegistry.GetSkills().FirstOrDefault(item =>
                        item.Id.Equals(recommendation.Id, StringComparison.OrdinalIgnoreCase));
                    if (skill is null)
                    {
                        CompassStatusText.Text = "这个 Skill 已经不存在，请重新研判。";
                        break;
                    }
                    if (MessageBox.Show(
                            this,
                            $"允许 NOVA 为相关任务启用 Skill“{skill.Name}”吗？\n\n"
                            + $"{recommendation.Reason}\n"
                            + "启用后只允许按需读取 SKILL.md，不会自动执行脚本，也不能绕过任何工具审批。",
                            "由你确认 Skill 能力",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question) != MessageBoxResult.Yes)
                    {
                        CompassStatusText.Text = $"没有启用 {skill.Name}。";
                        break;
                    }
                    await _skillRegistry.SetEnabledAsync(
                        skill.Id,
                        true,
                        CancellationToken.None);
                    RefreshSkills(skill.Id);
                    CompassStatusText.Text = $"已启用 {skill.Name}；司南只会在相关任务中提示模型读取。";
                    RefreshCapabilityCompass();
                    break;
                }
                case CapabilityAction.OpenMarketplace:
                    MarketplaceTab.IsSelected = true;
                    MarketplaceSearchBox.Text = recommendation.Name switch
                    {
                        "代码协作" => "编程",
                        "网页与浏览器" => "浏览器",
                        "文档与表格" => "研究",
                        "设计与视觉" => "体验",
                        _ => string.Empty
                    };
                    RefreshMarketplace();
                    MarketplaceStatusText.Text =
                        $"能力司南已把“{recommendation.Name}”带到集市。请先看来源、前置条件与权限，再决定是否加载。";
                    break;
                case CapabilityAction.DiscoverMcp:
                    DiscoveryTab.IsSelected = true;
                    CompassStatusText.Text = "已转到安全发现。扫描前会先列出准确路径并请求你的许可。";
                    DiscoverMcp_Click(sender, e);
                    break;
                case CapabilityAction.InstallSkill:
                    SkillsTab.IsSelected = true;
                    CompassStatusText.Text = "请选择一个包含 SKILL.md 的文件夹；安装前会进行结构与安全审查。";
                    InstallSkill_Click(sender, e);
                    break;
                case CapabilityAction.Ready:
                    OpenCapability(recommendation);
                    break;
            }
        }
        catch (Exception exception)
        {
            CompassStatusText.Text = $"能力操作没有完成：{exception.Message}";
        }
    }

    private void OpenCapability(CapabilityRecommendation recommendation)
    {
        if (recommendation.Kind == CapabilityKind.Mcp)
        {
            ManualMcpTab.IsSelected = true;
            RefreshMcpServers(recommendation.Id);
            return;
        }
        if (recommendation.Kind == CapabilityKind.Skill)
        {
            SkillsTab.IsSelected = true;
            RefreshSkills(recommendation.Id);
            return;
        }
        CompassStatusText.Text = "当前内建能力足够，不需要为这次任务增加新的权限面。";
    }

    private void MarketplaceFilter_Changed(object sender, RoutedEventArgs e)
        => RefreshMarketplace();

    private void RefreshMarketplace_Click(object sender, RoutedEventArgs e)
    {
        RefreshMarketplace();
        MarketplaceStatusText.Text = "能力状态已刷新；NOVA 没有启动连接或访问外部网络。";
    }

    private void RefreshMarketplace()
    {
        if (MarketplaceList is null
            || MarketplaceSearchBox is null
            || MarketplaceKindBox is null)
        {
            return;
        }

        try
        {
            var search = MarketplaceSearchBox.Text.Trim();
            var kind = (MarketplaceKindBox.SelectedItem as ComboBoxItem)?.Content?.ToString()
                       ?? "全部能力";
            var filtered = _marketplace.GetCatalog()
                .Where(item =>
                    kind == "全部能力"
                    || (kind == "只看 MCP" && item.Kind == MarketplaceCapabilityKind.Mcp)
                    || (kind == "只看 Skills" && item.Kind == MarketplaceCapabilityKind.Skill))
                .Where(item =>
                    search.Length == 0
                    || $"{item.Name} {item.Publisher} {item.Category} {item.Description}"
                        .Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            _marketplaceItems.Clear();
            foreach (var item in filtered)
            {
                _marketplaceItems.Add(item);
            }
            MarketplaceVisibleText.Text = filtered.Length.ToString();
            MarketplaceLoadedText.Text = filtered.Count(item => item.IsEnabled).ToString();
        }
        catch (Exception exception)
        {
            MarketplaceStatusText.Text = $"能力集市状态读取失败：{exception.Message}";
        }
    }

    private async void MarketplaceLoad_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MarketplaceCatalogItem item })
        {
            return;
        }

        try
        {
            if (item.IsEnabled)
            {
                OpenMarketplaceCapability(item);
                return;
            }

            var missing = _marketplace.GetMissingPrerequisites(item);
            if (item.Kind == MarketplaceCapabilityKind.Mcp)
            {
                var registration = item.McpRegistration
                                   ?? throw new InvalidOperationException("MCP 集市条目缺少连接定义。");
                var exists = _mcpRegistry.GetServers().Any(server =>
                    server.Name.Equals(registration.Name, StringComparison.OrdinalIgnoreCase));
                if (exists && missing.Count > 0)
                {
                    MarketplaceStatusText.Text =
                        $"“{item.Name}”尚缺少 {string.Join("、", missing)}，已保留为停用连接。补齐后再启用即可。";
                    OpenMarketplaceCapability(item);
                    return;
                }

                var action = exists ? "启用" : missing.Count == 0 ? "登记并启用" : "登记为待配置";
                var acquireNotice = item.MayAcquireSoftware
                    ? "\n首次真实连接可能拉取容器镜像或软件包；本次操作不会下载或启动。"
                    : string.Empty;
                var missingNotice = missing.Count == 0
                    ? string.Empty
                    : $"\n当前缺少：{string.Join("、", missing)}。本次只登记，不启用。";
                if (MessageBox.Show(
                        this,
                        $"{action}“{item.Name}”吗？\n\n"
                        + $"发布者：{item.Publisher} · {item.TrustLabel}\n"
                        + $"风险面：{item.RiskLabel}\n"
                        + $"{item.PermissionSummary}\n"
                        + $"将登记：{FormatExecutionImpact(registration)}"
                        + acquireNotice
                        + missingNotice
                        + "\n\n真实连接、联网与工具调用仍会再次请求授权。",
                        "由你确认加载 MCP",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    MarketplaceStatusText.Text = $"已取消，{item.Name} 的状态没有改变。";
                    return;
                }

                await _mcpRegistry.UpsertAsync(
                    registration with { Enabled = missing.Count == 0 },
                    CancellationToken.None);
                MarketplaceStatusText.Text = missing.Count == 0
                    ? $"已加载 {item.Name}，但尚未启动。首次连接及每次工具调用仍需授权。"
                    : $"已登记 {item.Name}，保持停用；补齐 {string.Join("、", missing)} 后即可启用。";
            }
            else
            {
                var definition = item.SkillDefinition
                                 ?? throw new InvalidOperationException("Skill 集市条目缺少安装定义。");
                var installed = _skillRegistry.GetSkills().FirstOrDefault(skill =>
                    skill.Id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase));
                if (MessageBox.Show(
                        this,
                        $"{(installed is null ? "安装并启用" : "启用")} Skill“{item.Name}”吗？\n\n"
                        + $"{item.Description}\n\n"
                        + $"{item.PermissionSummary}\n"
                        + "该内置 Skill 只有 SKILL.md，不包含或执行脚本，也不能扩大任何工具权限。",
                        "由你确认加载 Skill",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    MarketplaceStatusText.Text = $"已取消，{item.Name} 的状态没有改变。";
                    return;
                }

                if (installed is null)
                {
                    await _skillRegistry.InstallBundledAsync(
                        definition.Id,
                        definition.Instructions,
                        CancellationToken.None);
                }
                else
                {
                    await _skillRegistry.SetEnabledAsync(
                        definition.Id,
                        true,
                        CancellationToken.None);
                }
                MarketplaceStatusText.Text =
                    $"已加载 {item.Name}；只有相关任务会读取它，工具权限保持不变。";
            }

            RefreshMcpServers();
            RefreshSkills();
            RefreshMarketplace();
            RefreshCapabilityCompass();
        }
        catch (Exception exception)
        {
            MarketplaceStatusText.Text = $"能力没有加载：{exception.Message}";
        }
    }

    private void OpenMarketplaceCapability(MarketplaceCatalogItem item)
    {
        if (item.Kind == MarketplaceCapabilityKind.Mcp)
        {
            ManualMcpTab.IsSelected = true;
            RefreshMcpServers(item.McpRegistration?.Name);
            return;
        }

        SkillsTab.IsSelected = true;
        RefreshSkills(item.SkillDefinition?.Id);
    }

    private async void DiscoverMcp_Click(object sender, RoutedEventArgs e)
    {
        var sources = _mcpDiscovery.GetAvailableDefaultSources();
        if (sources.Count == 0)
        {
            _discoverySelections.Clear();
            DiscoveryCountText.Text = "没有找到受支持的本机 MCP 配置文件";
            DiscoveryStatusText.Text =
                "你仍可使用“手动配置”。NOVA 没有读取目录内容，也没有启动任何外部程序。";
            ImportDiscoveredButton.IsEnabled = false;
            return;
        }

        var pathPreview = string.Join(
            Environment.NewLine,
            sources.Select(source => $"• {source.Product}：{source.Path}"));
        if (MessageBox.Show(
                this,
                $"允许 NOVA 只读扫描以下 {sources.Count} 个配置文件吗？\n\n{pathPreview}\n\n"
                + "本次扫描不会启动进程、访问网络、修改原文件或复制明文密钥。",
                "授权只读扫描 MCP 配置",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            DiscoveryStatusText.Text = "扫描已取消，没有读取任何 MCP 配置内容。";
            return;
        }

        try
        {
            DiscoveryStatusText.Text = "正在本机解析配置并执行安全检查…";
            var result = await _mcpDiscovery.DiscoverAsync(
                sources,
                _mcpRegistry.GetServers(),
                CancellationToken.None);
            _discoverySelections.Clear();
            foreach (var candidate in result.Candidates)
            {
                _discoverySelections.Add(new McpDiscoverySelection(candidate));
            }

            var importable = result.Candidates.Count(candidate => candidate.CanImport);
            DiscoveryCountText.Text =
                $"已扫描 {result.ScannedPaths.Count} 个文件 · 发现 {result.Candidates.Count} 项 · {importable} 项可导入";
            DiscoveryStatusText.Text = result.Warnings.Count == 0
                ? "扫描完成。请选择要导入的连接；存在明文密钥的值已自动忽略。"
                : $"扫描完成，但有 {result.Warnings.Count} 个文件无法解析：{string.Join("；", result.Warnings.Take(2))}";
            UpdateDiscoveryImportState();
        }
        catch (Exception exception)
        {
            DiscoveryStatusText.Text = $"扫描失败：{exception.Message}";
            ImportDiscoveredButton.IsEnabled = false;
        }
    }

    private void SelectSafeDiscoveries_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _discoverySelections)
        {
            item.IsSelected = item.Candidate.CanImport
                              && !item.Candidate.MayAcquireSoftware
                              && item.Candidate.OmittedSecretCount == 0;
        }
        UpdateDiscoveryImportState();
    }

    private void DiscoverySelectionChanged(object sender, RoutedEventArgs e)
        => UpdateDiscoveryImportState();

    private void UpdateDiscoveryImportState()
    {
        var selected = _discoverySelections.Count(item =>
            item.IsSelected && item.Candidate.CanImport);
        ImportDiscoveredButton.IsEnabled = selected > 0;
        ImportDiscoveredButton.Content = selected > 0
            ? $"授权导入所选（{selected}）"
            : "授权导入所选";
    }

    private async void ImportDiscoveries_Click(object sender, RoutedEventArgs e)
    {
        var selected = _discoverySelections
            .Where(item => item.IsSelected && item.Candidate.CanImport)
            .Select(item => item.Candidate)
            .ToArray();
        if (selected.Length == 0)
        {
            UpdateDiscoveryImportState();
            return;
        }

        var preview = string.Join(
            Environment.NewLine,
            selected.Select(candidate =>
                $"• {candidate.Name}（{candidate.SourceProduct} / {candidate.RiskLabel}）"));
        var elevatedRisk = selected.Count(candidate =>
            candidate.MayAcquireSoftware || candidate.OmittedSecretCount > 0);
        var riskNotice = elevatedRisk == 0
            ? string.Empty
            : $"\n其中 {elevatedRisk} 项需要你稍后补充配置或复核外部软件行为。";
        if (MessageBox.Show(
                this,
                $"确认把以下 {selected.Length} 个连接写入 NOVA 吗？\n\n{preview}\n\n"
                + $"所有连接都会保持停用，不会执行、联网或下载。{riskNotice}",
                "授权导入 MCP 绑定",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            DiscoveryStatusText.Text = "导入已取消，NOVA 注册表没有改变。";
            return;
        }

        var imported = 0;
        var errors = new List<string>();
        foreach (var candidate in selected)
        {
            try
            {
                await _mcpRegistry.UpsertAsync(
                    candidate.Registration with { Enabled = false },
                    CancellationToken.None);
                imported++;
            }
            catch (Exception exception)
            {
                errors.Add($"{candidate.Name}：{exception.Message}");
            }
        }

        RefreshMcpServers();
        RefreshCapabilityCompass();
        foreach (var item in _discoverySelections)
        {
            if (selected.Any(candidate =>
                    candidate.Name.Equals(item.Candidate.Name, StringComparison.OrdinalIgnoreCase)))
            {
                item.IsSelected = false;
            }
        }
        ImportDiscoveredButton.IsEnabled = false;
        DiscoveryStatusText.Text = errors.Count == 0
            ? $"已导入 {imported} 个停用连接。请在“MCP 连接”中逐个检查、测试并启用。"
            : $"已导入 {imported} 个；{errors.Count} 个失败：{string.Join("；", errors.Take(2))}";
        ManualMcpTab.IsSelected = true;
    }

    private void OpenManualMcp_Click(object sender, RoutedEventArgs e)
        => ManualMcpTab.IsSelected = true;

    private void NewMcp_Click(object sender, RoutedEventArgs e)
    {
        McpList.SelectedItem = null;
        McpNameBox.IsEnabled = true;
        McpNameBox.Clear();
        McpTransportBox.SelectedIndex = 0;
        McpCommandBox.Clear();
        McpUrlBox.Clear();
        McpArgumentsBox.Clear();
        McpWorkingDirectoryBox.Text = _workspaceRoot;
        McpEnvironmentBox.Clear();
        McpHeadersBox.Clear();
        McpEnabledBox.IsChecked = true;
        McpStatusText.Text = "填写连接信息后保存；测试连接会真实启动或访问该 MCP Server。";
        McpNameBox.Focus();
    }

    private void McpList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (McpList.SelectedItem is not McpServerRegistration server)
        {
            return;
        }

        McpNameBox.Text = server.Name;
        McpNameBox.IsEnabled = false;
        McpTransportBox.SelectedIndex = server.Transport == "http" ? 1 : 0;
        McpCommandBox.Text = server.Command;
        McpUrlBox.Text = server.Url ?? string.Empty;
        McpArgumentsBox.Text = string.Join(Environment.NewLine, server.Arguments);
        McpWorkingDirectoryBox.Text = server.WorkingDirectory ?? string.Empty;
        McpEnvironmentBox.Text = FormatMappings(server.EnvironmentVariables);
        McpHeadersBox.Text = FormatMappings(
            server.HttpHeaders ?? new Dictionary<string, string>());
        McpEnabledBox.IsChecked = server.Enabled;
        McpStatusText.Text = $"{server.Name} 已载入。修改后点击“保存绑定”。";
    }

    private void McpTransportBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (McpCommandBox is null || McpUrlBox is null)
        {
            return;
        }
        var isHttp = GetSelectedTransport() == "http";
        McpCommandBox.IsEnabled = !isHttp;
        McpArgumentsBox.IsEnabled = !isHttp;
        McpWorkingDirectoryBox.IsEnabled = !isHttp;
        McpEnvironmentBox.IsEnabled = !isHttp;
        McpUrlBox.IsEnabled = isHttp;
        McpHeadersBox.IsEnabled = isHttp;
    }

    private async void SaveMcp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var registration = BuildMcpRegistration();
            var previous = _mcpRegistry.GetServers().FirstOrDefault(server =>
                server.Name.Equals(registration.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (registration.Enabled
                && previous?.Enabled != true
                && MessageBox.Show(
                    this,
                    $"启用“{registration.Name.Trim()}”后，Agent 可在任务中连接它。\n\n"
                    + FormatExecutionImpact(registration)
                    + "\n\n保存配置本身不会立即启动连接。是否允许启用？",
                    "授权启用 MCP",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                registration = registration with { Enabled = false };
                McpEnabledBox.IsChecked = false;
            }
            await _mcpRegistry.UpsertAsync(registration, CancellationToken.None);
            RefreshMcpServers(registration.Name);
            RefreshCapabilityCompass();
            McpStatusText.Text = $"已保存 {registration.Name}。配置文件：{_mcpRegistry.ConfigPath}";
        }
        catch (Exception exception)
        {
            McpStatusText.Text = exception.Message;
        }
    }

    private async void TestMcp_Click(object sender, RoutedEventArgs e)
    {
        var name = McpNameBox.Text.Trim();
        if (name.Length == 0)
        {
            McpStatusText.Text = "请先选择或保存一个 MCP Server。";
            return;
        }
        try
        {
            var registration = _mcpRegistry.GetServers().FirstOrDefault(server =>
                server.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (registration is null)
            {
                McpStatusText.Text = "请先保存这个 MCP Server，再测试连接。";
                return;
            }
            if (!registration.Enabled)
            {
                McpStatusText.Text = "该连接目前停用。检查配置后勾选启用并保存，NOVA 会请求授权。";
                return;
            }
            if (MessageBox.Show(
                    this,
                    $"测试会真实连接“{name}”并读取它公布的工具列表。\n\n"
                    + FormatExecutionImpact(registration)
                    + "\n\n是否仅允许本次测试？",
                    "授权测试 MCP 连接",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                McpStatusText.Text = "测试已取消，没有启动或访问 MCP Server。";
                return;
            }
            McpStatusText.Text = $"正在连接 {name}…";
            var result = await _mcpRegistry.InspectToolsAsync(
                name,
                _workspaceRoot,
                CancellationToken.None);
            var tools = JsonNode.Parse(result)?["tools"]?.AsArray();
            var names = tools?
                .Select(item => item?["name"]?.GetValue<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Take(8)
                .ToArray() ?? [];
            McpStatusText.Text = names.Length == 0
                ? $"连接成功：{name} 当前未公布工具。"
                : $"连接成功：发现 {tools?.Count ?? names.Length} 个工具 · {string.Join(" · ", names)}";
        }
        catch (Exception exception)
        {
            McpStatusText.Text = $"连接失败：{exception.Message}";
        }
    }

    private async void DeleteMcp_Click(object sender, RoutedEventArgs e)
    {
        if (McpList.SelectedItem is not McpServerRegistration selected)
        {
            McpStatusText.Text = "请先选择要删除的 MCP Server。";
            return;
        }
        if (MessageBox.Show(
                this,
                $"确定删除 MCP 绑定“{selected.Name}”吗？",
                "删除 MCP 绑定",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            await _mcpRegistry.RemoveAsync(selected.Name, CancellationToken.None);
            NewMcp_Click(sender, e);
            RefreshMcpServers();
            RefreshCapabilityCompass();
            McpStatusText.Text = $"已删除 {selected.Name}。";
        }
        catch (Exception exception)
        {
            McpStatusText.Text = exception.Message;
        }
    }

    private McpServerRegistration BuildMcpRegistration()
        => new(
            McpNameBox.Text,
            McpCommandBox.Text,
            SplitLines(McpArgumentsBox.Text),
            string.IsNullOrWhiteSpace(McpWorkingDirectoryBox.Text)
                ? null
                : McpWorkingDirectoryBox.Text,
            McpEnabledBox.IsChecked == true,
            ParseMappings(McpEnvironmentBox.Text),
            GetSelectedTransport(),
            string.IsNullOrWhiteSpace(McpUrlBox.Text) ? null : McpUrlBox.Text,
            ParseMappings(McpHeadersBox.Text));

    private void RefreshMcpServers(string? selectName = null)
    {
        try
        {
            var servers = _mcpRegistry.GetServers();
            McpList.ItemsSource = servers;
            if (selectName is not null)
            {
                McpList.SelectedItem = servers.FirstOrDefault(server =>
                    server.Name.Equals(selectName, StringComparison.OrdinalIgnoreCase));
            }
            RefreshMarketplace();
        }
        catch (Exception exception)
        {
            McpStatusText.Text = exception.Message;
        }
    }

    private async void InstallSkill_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "选择包含 SKILL.md 的 Skill 文件夹",
            Multiselect = false
        };
        if (picker.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            SkillStatusText.Text = "正在审查并安装…";
            var installed = await _skillRegistry.InstallFromFolderAsync(
                picker.FolderName,
                CancellationToken.None);
            RefreshSkills(installed.Id);
            RefreshCapabilityCompass();
            SkillStatusText.Text = $"已安全安装 {installed.Name}；不会自动执行其中的脚本。";
        }
        catch (Exception exception)
        {
            SkillStatusText.Text = $"安装失败：{exception.Message}";
        }
    }

    private void SkillList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SkillList.SelectedItem is not InstalledSkill skill)
        {
            ShowEmptySkill();
            return;
        }
        SkillNameText.Text = skill.Name;
        SkillDescriptionText.Text = skill.Description;
        SkillStateText.Text = skill.Enabled ? "已启用" : "已停用";
        SkillFilesText.Text = $"{skill.FileCount} files";
        SkillSizeText.Text = FormatBytes(skill.SizeBytes);
        SkillPathText.Text = skill.DirectoryPath;
        ToggleSkillButton.Content = skill.Enabled ? "停用" : "启用";
        ToggleSkillButton.IsEnabled = true;
        UninstallSkillButton.IsEnabled = true;
        SkillStatusText.Text = $"ID: {skill.Id} · 安装于 {skill.InstalledAt.ToLocalTime():yyyy-MM-dd HH:mm}";
    }

    private async void ToggleSkill_Click(object sender, RoutedEventArgs e)
    {
        if (SkillList.SelectedItem is not InstalledSkill selected)
        {
            return;
        }
        try
        {
            await _skillRegistry.SetEnabledAsync(
                selected.Id,
                !selected.Enabled,
                CancellationToken.None);
            RefreshSkills(selected.Id);
            RefreshCapabilityCompass();
            SkillStatusText.Text = selected.Enabled
                ? $"已停用 {selected.Name}，Agent 将不再读取它。"
                : $"已启用 {selected.Name}。";
        }
        catch (Exception exception)
        {
            SkillStatusText.Text = exception.Message;
        }
    }

    private async void UninstallSkill_Click(object sender, RoutedEventArgs e)
    {
        if (SkillList.SelectedItem is not InstalledSkill selected)
        {
            return;
        }
        if (MessageBox.Show(
                this,
                $"卸载 Skill“{selected.Name}”？这会删除 NOVA 管理目录中的副本，不影响原始文件夹。",
                "卸载 Skill",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            await _skillRegistry.UninstallAsync(selected.Id, CancellationToken.None);
            RefreshSkills();
            RefreshCapabilityCompass();
            SkillStatusText.Text = $"已卸载 {selected.Name}。原始来源文件夹未改变。";
        }
        catch (Exception exception)
        {
            SkillStatusText.Text = exception.Message;
        }
    }

    private void RefreshSkills(string? selectId = null)
    {
        var skills = _skillRegistry.GetSkills();
        SkillList.ItemsSource = skills;
        if (selectId is not null)
        {
            SkillList.SelectedItem = skills.FirstOrDefault(skill =>
                skill.Id.Equals(selectId, StringComparison.OrdinalIgnoreCase));
        }
        else if (skills.Count == 0)
        {
            ShowEmptySkill();
        }
        RefreshMarketplace();
    }

    private void ShowEmptySkill()
    {
        SkillNameText.Text = "选择一个 Skill";
        SkillDescriptionText.Text = "Skill 会为 Agent 提供特定任务的执行说明。";
        SkillStateText.Text = "未选择";
        SkillFilesText.Text = "0 files";
        SkillSizeText.Text = "0 KB";
        SkillPathText.Text = "—";
        ToggleSkillButton.IsEnabled = false;
        UninstallSkillButton.IsEnabled = false;
    }

    private string GetSelectedTransport()
        => (McpTransportBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "stdio";

    private static string[] SplitLines(string value)
        => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Dictionary<string, string> ParseMappings(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in SplitLines(value))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
            {
                throw new InvalidOperationException($"映射“{line}”必须使用 目标=来源环境变量 格式。");
            }
            result[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return result;
    }

    private static string FormatMappings(IReadOnlyDictionary<string, string> mappings)
        => string.Join(Environment.NewLine, mappings.Select(pair => $"{pair.Key}={pair.Value}"));

    private static string FormatExecutionImpact(McpServerRegistration registration)
        => registration.Transport == "http"
            ? $"将访问：{registration.Url}"
            : $"将启动：{registration.Command} {string.Join(" ", registration.Arguments)}";

    private static string FormatBytes(long bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / 1024d / 1024d:0.0} MB"
            : $"{Math.Max(1, bytes / 1024d):0.0} KB";
}

public sealed class McpDiscoverySelection : INotifyPropertyChanged
{
    private bool _isSelected;

    public McpDiscoverySelection(McpDiscoveryCandidate candidate)
    {
        Candidate = candidate;
    }

    public McpDiscoveryCandidate Candidate { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
