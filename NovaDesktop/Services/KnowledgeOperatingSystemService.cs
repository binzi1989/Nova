using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaDesktop.Services;

public sealed record OntologyEntityType(
    string Id,
    string Label,
    string Description,
    IReadOnlyList<string> Properties);

public sealed record OntologyRelationType(
    string Id,
    string Label,
    string SourceType,
    string TargetType,
    bool AllowsInference);

public sealed record KnowledgeWikiPage(
    string Id,
    string Title,
    string EntityType,
    string Summary,
    IReadOnlyList<string> ConfirmedFacts,
    IReadOnlyList<string> PossibleConnections,
    IReadOnlyList<string> SourceLabels,
    string PagePath,
    DateTimeOffset UpdatedAt);

public sealed record KnowledgeRuleDefinition(
    string Id,
    string Title,
    string Description,
    string Severity,
    string Recommendation);

public sealed record KnowledgeRuleResult(
    string RuleId,
    string Title,
    string Status,
    string Severity,
    string Explanation,
    IReadOnlyList<string> Evidence,
    string Recommendation);

public sealed record KnowledgeOperatingSystemSnapshot(
    string SchemaVersion,
    DateTimeOffset CompiledAt,
    string Scope,
    string WikiRoot,
    IReadOnlyList<OntologyEntityType> EntityTypes,
    IReadOnlyList<OntologyRelationType> RelationTypes,
    IReadOnlyList<KnowledgeWikiPage> WikiPages,
    IReadOnlyList<KnowledgeRuleDefinition> Rules,
    IReadOnlyList<KnowledgeRuleResult> Decisions);

public sealed class KnowledgeOperatingSystemService
{
    private const string SchemaVersion = "nova.knowledge-os/1.0";
    private readonly string _root;
    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly IReadOnlyList<OntologyEntityType> EntityTypes =
    [
        new("knowledge-space", "知识空间", "一个项目、企业或个人知识边界。", ["name", "workspace", "updatedAt"]),
        new("goal", "目标", "用户希望获得并能够验证的结果。", ["objective", "status", "updatedAt"]),
        new("input", "用户输入", "用户提供的意图、事实、约束或补充线索。", ["content", "source", "createdAt"]),
        new("document", "资料", "工作区内可追溯的原始文件或文档。", ["path", "format", "modifiedAt"]),
        new("artifact", "交付物", "任务执行产生的报告、代码、数据或其他成果。", ["path", "role", "createdAt"]),
        new("knowledge", "确认知识", "已经由用户或可靠来源确认的知识。", ["statement", "source", "confidence"]),
        new("concept", "关键概念", "连接不同资料、目标和成果的主题。", ["name", "aliases"]),
        new("capability", "能力", "可被 Agent 使用的 Skill、MCP、模型或工具。", ["name", "enabled", "provider"])
    ];

    private static readonly IReadOnlyList<OntologyRelationType> RelationTypes =
    [
        new("belongs-to", "属于", "*", "knowledge-space", false),
        new("supports", "支持目标", "input|document|artifact|knowledge", "goal", false),
        new("produces", "产生", "goal", "artifact", false),
        new("mentions", "提到", "input|document|artifact|knowledge", "concept", false),
        new("uses", "使用", "goal|artifact", "capability", false),
        new("related", "相关", "*", "*", false),
        new("potential-mapping", "可能有关", "*", "*", true),
        new("confirmed-mapping", "已确认关联", "*", "*", false)
    ];

    private static readonly IReadOnlyList<KnowledgeRuleDefinition> Rules =
    [
        new("evidence-gap", "目标缺少证据", "存在目标或输入，但没有可追溯资料和交付物。", "warning", "补充原始文件、数据或可验证的交付物后再形成强结论。"),
        new("mapping-review", "候选关系等待判断", "知识网络中存在尚未由用户确认的可能映射。", "info", "确认可靠关系，忽略错误联系，避免推测污染企业知识。"),
        new("source-lineage", "知识来源不完整", "部分高价值知识没有清晰来源标签。", "warning", "补充来源、观察时间和适用范围。"),
        new("delivery-grounding", "交付物与目标未闭环", "存在任务目标，但没有直接关联的交付成果。", "warning", "让 Agent 生成可检查的文件并明确验收标准。")
    ];

    public KnowledgeOperatingSystemService(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "knowledge-wiki");
    }

    public KnowledgeOperatingSystemSnapshot Compile(
        KnowledgeGraphSnapshot graph,
        string? workspaceRoot)
    {
        var scope = string.IsNullOrWhiteSpace(workspaceRoot)
            ? "all"
            : "workspace-" + ShortHash(Path.GetFullPath(workspaceRoot));
        var wikiRoot = Path.Combine(_root, scope);
        var compiledAt = graph.UpdatedAt == DateTimeOffset.MinValue ? DateTimeOffset.Now : graph.UpdatedAt;
        try
        {
            Directory.CreateDirectory(wikiRoot);
        }
        catch (UnauthorizedAccessException)
        {
            wikiRoot = string.Empty;
        }
        catch (IOException)
        {
            wikiRoot = string.Empty;
        }
        var nodeById = graph.Nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        var pages = graph.Nodes
            .Where(node => node.Kind is "Goal" or "Input" or "Document" or "Artifact" or "Knowledge")
            .OrderByDescending(node => node.Weight)
            .ThenByDescending(node => node.UpdatedAt)
            .Take(48)
            .Select(node => BuildPage(node, graph.Edges, nodeById, wikiRoot))
            .ToArray();
        var decisions = EvaluateRules(graph, nodeById);
        if (!string.IsNullOrWhiteSpace(wikiRoot))
        {
            PersistMetadata(wikiRoot, pages, decisions, compiledAt);
        }
        return new KnowledgeOperatingSystemSnapshot(
            SchemaVersion,
            compiledAt,
            scope,
            wikiRoot,
            EntityTypes,
            RelationTypes,
            pages,
            Rules,
            decisions);
    }

    private KnowledgeWikiPage BuildPage(
        KnowledgeNode node,
        IReadOnlyList<KnowledgeEdge> edges,
        IReadOnlyDictionary<string, KnowledgeNode> nodes,
        string wikiRoot)
    {
        var related = edges
            .Where(edge => edge.SourceId.Equals(node.Id, StringComparison.OrdinalIgnoreCase)
                           || edge.TargetId.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
            .Select(edge => new
            {
                Edge = edge,
                Node = nodes.GetValueOrDefault(
                    edge.SourceId.Equals(node.Id, StringComparison.OrdinalIgnoreCase)
                        ? edge.TargetId
                        : edge.SourceId)
            })
            .Where(item => item.Node is not null)
            .ToArray();
        var facts = related
            .Where(item => !item.Edge.IsInferred)
            .Select(item => $"{RelationLabel(item.Edge)}：{item.Node!.Label}")
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(10)
            .ToArray();
        var possible = related
            .Where(item => item.Edge.IsInferred)
            .Select(item => $"{item.Node!.Label}（{Math.Round(item.Edge.Confidence * 100)}%，{item.Edge.Evidence}）")
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(8)
            .ToArray();
        var sources = new[] { node.SourceLabel, node.SourceType }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var fileName = SafeFileName(EntityLabel(node.Kind) + "-" + node.Label, node.Id) + ".md";
        var pagePath = string.IsNullOrWhiteSpace(wikiRoot) ? string.Empty : Path.Combine(wikiRoot, fileName);
        var summary = string.IsNullOrWhiteSpace(node.Detail)
            ? $"这是 NOVA 从{(sources.FirstOrDefault() ?? "本地知识")}整理出的{EntityLabel(node.Kind)}。"
            : Trim(node.Detail, 480);
        var markdown = BuildMarkdown(node, summary, facts, possible, sources);
        if (!string.IsNullOrWhiteSpace(pagePath)) WriteIfChanged(pagePath, markdown);
        return new KnowledgeWikiPage(
            node.Id,
            node.Label,
            EntityLabel(node.Kind),
            summary,
            facts,
            possible,
            sources,
            pagePath,
            node.UpdatedAt);
    }

    private static IReadOnlyList<KnowledgeRuleResult> EvaluateRules(
        KnowledgeGraphSnapshot graph,
        IReadOnlyDictionary<string, KnowledgeNode> nodes)
    {
        var goals = graph.Nodes.Where(node => node.Kind is "Goal" or "Input").ToArray();
        var documents = graph.Nodes.Count(node => node.Kind == "Document");
        var artifacts = graph.Nodes.Count(node => node.Kind == "Artifact");
        var inferred = graph.Edges.Count(edge => edge.IsInferred);
        var missingSources = graph.Nodes
            .Where(node => node.Kind is "Goal" or "Input" or "Document" or "Artifact" or "Knowledge")
            .Where(node => string.IsNullOrWhiteSpace(node.SourceLabel) && string.IsNullOrWhiteSpace(node.SourceType))
            .Take(8)
            .ToArray();
        var goalArtifactLinks = graph.Edges.Count(edge =>
            nodes.TryGetValue(edge.SourceId, out var source)
            && nodes.TryGetValue(edge.TargetId, out var target)
            && ((source.Kind == "Goal" && target.Kind == "Artifact")
                || (source.Kind == "Artifact" && target.Kind == "Goal")));
        return
        [
            Result(Rules[0], goals.Length > 0 && documents + artifacts == 0,
                $"发现 {goals.Length} 条目标/输入，资料 {documents} 个，交付物 {artifacts} 个。",
                goals.Take(4).Select(node => node.Label)),
            Result(Rules[1], inferred > 0,
                $"当前有 {inferred} 条候选关系尚未确认。",
                graph.Edges.Where(edge => edge.IsInferred).Take(4).Select(edge => edge.Evidence)),
            Result(Rules[2], missingSources.Length > 0,
                $"有 {missingSources.Length} 个高价值节点缺少清晰来源。",
                missingSources.Select(node => node.Label)),
            Result(Rules[3], goals.Length > 0 && (artifacts == 0 || goalArtifactLinks == 0),
                $"目标/输入 {goals.Length} 条，交付物 {artifacts} 个，目标与交付物直接关系 {goalArtifactLinks} 条。",
                goals.Take(4).Select(node => node.Label))
        ];
    }

    private static KnowledgeRuleResult Result(
        KnowledgeRuleDefinition rule,
        bool triggered,
        string explanation,
        IEnumerable<string> evidence)
        => new(
            rule.Id,
            rule.Title,
            triggered ? "triggered" : "clear",
            triggered ? rule.Severity : "success",
            explanation,
            evidence.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().Take(6).ToArray(),
            triggered ? rule.Recommendation : "当前未发现需要处理的问题。");

    private void PersistMetadata(
        string wikiRoot,
        IReadOnlyList<KnowledgeWikiPage> pages,
        IReadOnlyList<KnowledgeRuleResult> decisions,
        DateTimeOffset compiledAt)
    {
        WriteIfChanged(Path.Combine(wikiRoot, "ontology.schema.json"), JsonSerializer.Serialize(new
        {
            schema = SchemaVersion,
            entityTypes = EntityTypes,
            relationTypes = RelationTypes
        }, _json));
        WriteIfChanged(Path.Combine(wikiRoot, "rules.json"), JsonSerializer.Serialize(Rules, _json));
        WriteIfChanged(Path.Combine(wikiRoot, "index.json"), JsonSerializer.Serialize(new
        {
            schema = SchemaVersion,
            compiledAt,
            pages,
            decisions
        }, _json));
    }

    private static string BuildMarkdown(
        KnowledgeNode node,
        string summary,
        IReadOnlyList<string> facts,
        IReadOnlyList<string> possible,
        IReadOnlyList<string> sources)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {node.Label}").AppendLine();
        builder.AppendLine($"> 类型：{EntityLabel(node.Kind)} · 更新时间：{node.UpdatedAt:yyyy-MM-dd HH:mm}").AppendLine();
        builder.AppendLine("## 当前认知").AppendLine().AppendLine(summary).AppendLine();
        builder.AppendLine("## 已确认联系").AppendLine();
        foreach (var fact in facts.DefaultIfEmpty("暂无已确认联系。")) builder.AppendLine("- " + fact);
        builder.AppendLine().AppendLine("## 可能联系").AppendLine();
        foreach (var item in possible.DefaultIfEmpty("暂无等待确认的联系。")) builder.AppendLine("- " + item);
        builder.AppendLine().AppendLine("## 来源").AppendLine();
        foreach (var source in sources.DefaultIfEmpty("NOVA 本地知识网络")) builder.AppendLine("- " + source);
        builder.AppendLine().AppendLine($"<!-- nova-node:{node.Id} -->");
        return builder.ToString();
    }

    private static string RelationLabel(KnowledgeEdge edge)
        => edge.Relation switch
        {
            "belongs to" => "属于",
            "contributes to" => "支持",
            "produced" => "产生",
            "mentions" => "提到",
            "uses" => "使用",
            "confirmed mapping" => "已确认关联",
            _ => edge.Relation
        };

    private static string EntityLabel(string kind)
        => kind switch
        {
            "Goal" => "目标",
            "Input" => "用户输入",
            "Document" => "资料",
            "Artifact" => "交付物",
            "Knowledge" => "确认知识",
            _ => "知识"
        };

    private static string SafeFileName(string value, string id)
    {
        foreach (var character in Path.GetInvalidFileNameChars()) value = value.Replace(character, '-');
        value = string.Join("-", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        value = value.Length > 52 ? value[..52] : value;
        var suffix = id.Length <= 6 ? id : id[^6..];
        return string.IsNullOrWhiteSpace(value) ? id : value + "-" + suffix;
    }

    private static void WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path).Equals(content, StringComparison.Ordinal)) return;
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    private static string ShortHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();

    private static string Trim(string value, int maximum)
        => value.Length <= maximum ? value : value[..maximum] + "…";
}
