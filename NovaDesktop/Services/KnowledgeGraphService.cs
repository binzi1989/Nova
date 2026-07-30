using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NovaDesktop.Models;

namespace NovaDesktop.Services;

public sealed record KnowledgeNode(
    string Id,
    string Label,
    string Kind,
    string Detail,
    double Weight,
    bool IsManual,
    DateTimeOffset UpdatedAt);

public sealed record KnowledgeEdge(
    string SourceId,
    string TargetId,
    string Relation,
    double Weight);

public sealed record KnowledgeGraphSnapshot(
    DateTimeOffset UpdatedAt,
    IReadOnlyList<KnowledgeNode> Nodes,
    IReadOnlyList<KnowledgeEdge> Edges);

public sealed class KnowledgeGraphService
{
    private const int MaximumNodes = 300;
    private const int MaximumEdges = 1000;
    private readonly string _graphPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public KnowledgeGraphService(string? graphPath = null)
    {
        _graphPath = graphPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "knowledge-graph.json");
    }

    public string GraphPath => _graphPath;

    public KnowledgeGraphSnapshot GetSnapshot()
    {
        if (!File.Exists(_graphPath))
        {
            return new KnowledgeGraphSnapshot(DateTimeOffset.MinValue, [], []);
        }
        try
        {
            return JsonSerializer.Deserialize<KnowledgeGraphSnapshot>(
                       File.ReadAllText(_graphPath),
                       _options)
                   ?? new KnowledgeGraphSnapshot(DateTimeOffset.MinValue, [], []);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new InvalidOperationException($"Unable to read knowledge graph '{_graphPath}'.", exception);
        }
    }

    public async Task<KnowledgeGraphSnapshot> SynchronizeAsync(
        IReadOnlyList<TaskSnapshot> tasks,
        IReadOnlyList<InstalledSkill> skills,
        IReadOnlyList<McpServerRegistration> mcpServers,
        IReadOnlyList<AgentScheduleItem> schedules,
        CancellationToken cancellationToken,
        IReadOnlyList<IndexedKnowledgeDocument>? indexedDocuments = null,
        IReadOnlyList<ArtifactItem>? artifacts = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = GetSnapshot();
            var nodes = existing.Nodes
                .Where(node => node.IsManual)
                .ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
            var edges = existing.Edges
                .Where(edge => nodes.ContainsKey(edge.SourceId) && nodes.ContainsKey(edge.TargetId))
                .ToDictionary(EdgeKey, StringComparer.OrdinalIgnoreCase);
            var now = DateTimeOffset.Now;

            foreach (var task in tasks
                         .OrderByDescending(item => item.UpdatedAt)
                         .Take(80))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var taskNode = PutNode(
                    nodes,
                    "task",
                    task.TaskId,
                    task.Title,
                    "Goal",
                    task.Prompt,
                    task.State == Models.TaskState.Completed ? 2 : 1.3,
                    false,
                    task.UpdatedAt);
                var workspaceLabel = string.IsNullOrWhiteSpace(task.WorkspaceRoot)
                    ? "未指定工作区"
                    : Path.GetFileName(task.WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar))
                      ?? task.WorkspaceRoot;
                var workspace = PutNode(
                    nodes,
                    "workspace",
                    task.WorkspaceRoot,
                    workspaceLabel,
                    "Project",
                    task.WorkspaceRoot,
                    1.5,
                    false,
                    task.UpdatedAt);
                Link(edges, taskNode, workspace, "belongs to", 1.4);

                var provider = PutNode(
                    nodes,
                    "provider",
                    task.Provider,
                    task.Provider,
                    "Provider",
                    task.Model,
                    1,
                    false,
                    task.UpdatedAt);
                Link(edges, taskNode, provider, "uses", 1);
                var model = PutNode(
                    nodes,
                    "model",
                    task.Model,
                    task.Model,
                    "Model",
                    task.Provider,
                    1,
                    false,
                    task.UpdatedAt);
                Link(edges, provider, model, "provides", 1);

                foreach (var concept in ExtractConcepts(task.Title + " " + task.Prompt).Take(3))
                {
                    var conceptNode = PutNode(
                        nodes,
                        "concept",
                        concept,
                        concept,
                        "Concept",
                        $"来自目标：{task.Title}",
                        1,
                        false,
                        task.UpdatedAt);
                    Link(edges, taskNode, conceptNode, "about", 1);
                }
            }

            foreach (var skill in skills.Where(item => item.Enabled))
            {
                var node = PutNode(
                    nodes,
                    "skill",
                    skill.Id,
                    skill.Name,
                    "Skill",
                    skill.Description,
                    1.4,
                    false,
                    now);
                foreach (var concept in ExtractConcepts(skill.Name + " " + skill.Description).Take(2))
                {
                    var conceptNode = PutNode(
                        nodes,
                        "concept",
                        concept,
                        concept,
                        "Concept",
                        skill.Description,
                        1,
                        false,
                        now);
                    Link(edges, node, conceptNode, "supports", 1);
                }
            }

            foreach (var server in mcpServers.Where(item => item.Enabled))
            {
                PutNode(
                    nodes,
                    "mcp",
                    server.Name,
                    server.Name,
                    "Tool",
                    $"{server.Transport} MCP Server",
                    1.2,
                    false,
                    now);
            }

            foreach (var schedule in schedules.Where(item => item.Enabled))
            {
                var routine = PutNode(
                    nodes,
                    "schedule",
                    schedule.Id,
                    schedule.Name,
                    "Routine",
                    schedule.Prompt,
                    1.2,
                    false,
                    schedule.CreatedAt);
                var provider = PutNode(
                    nodes,
                    "provider",
                    schedule.Provider,
                    schedule.Provider,
                    "Provider",
                    schedule.Model,
                    1,
                    false,
                    now);
                Link(edges, routine, provider, "runs with", 1);
            }

            foreach (var document in indexedDocuments ?? [])
            {
                var documentNode = PutNode(
                    nodes,
                    "document",
                    document.Id,
                    document.Title,
                    "Document",
                    document.RelativePath,
                    1.15,
                    false,
                    document.IndexedAt);
                var workspaceLabel = Path.GetFileName(
                                         document.WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar))
                                     ?? document.WorkspaceRoot;
                var workspace = PutNode(
                    nodes,
                    "workspace",
                    document.WorkspaceRoot,
                    workspaceLabel,
                    "Project",
                    document.WorkspaceRoot,
                    1.5,
                    false,
                    document.IndexedAt);
                Link(edges, documentNode, workspace, "in project", 1);
                foreach (var concept in ExtractConcepts(
                             document.Title + " " + document.RelativePath).Take(2))
                {
                    var conceptNode = PutNode(
                        nodes,
                        "concept",
                        concept,
                        concept,
                        "Concept",
                        $"来自文档：{document.RelativePath}",
                        1,
                        false,
                        document.IndexedAt);
                    Link(edges, documentNode, conceptNode, "contains", 1);
                }
            }

            foreach (var artifact in artifacts ?? [])
            {
                var artifactNode = PutNode(
                    nodes,
                    "artifact",
                    $"{artifact.Id}-v{artifact.Version}",
                    artifact.Title,
                    "Artifact",
                    $"{artifact.Type} · {artifact.Location}",
                    1.7,
                    false,
                    artifact.CreatedAt ?? now);
                var task = PutNode(
                    nodes,
                    "task",
                    artifact.TaskId,
                    tasks.FirstOrDefault(item =>
                        item.TaskId.Equals(
                            artifact.TaskId,
                            StringComparison.OrdinalIgnoreCase))?.Title
                    ?? artifact.TaskId,
                    "Goal",
                    $"交付物来源：{artifact.Title}",
                    1.4,
                    false,
                    artifact.CreatedAt ?? now);
                Link(edges, task, artifactNode, "delivers", 1.6);

                if (!string.IsNullOrWhiteSpace(artifact.WorkspaceRoot))
                {
                    var workspaceLabel = Path.GetFileName(
                                             artifact.WorkspaceRoot.TrimEnd(
                                                 Path.DirectorySeparatorChar))
                                         ?? artifact.WorkspaceRoot;
                    var workspace = PutNode(
                        nodes,
                        "workspace",
                        artifact.WorkspaceRoot,
                        workspaceLabel,
                        "Project",
                        artifact.WorkspaceRoot,
                        1.5,
                        false,
                        artifact.CreatedAt ?? now);
                    Link(edges, artifactNode, workspace, "stored for", 1.2);
                }

                foreach (var concept in ExtractConcepts(
                             artifact.Title + " " + artifact.Subtitle).Take(2))
                {
                    var conceptNode = PutNode(
                        nodes,
                        "concept",
                        concept,
                        concept,
                        "Concept",
                        $"来自交付物：{artifact.Title}",
                        1,
                        false,
                        artifact.CreatedAt ?? now);
                    Link(edges, artifactNode, conceptNode, "contains", 1);
                }
            }

            var snapshot = new KnowledgeGraphSnapshot(
                now,
                nodes.Values
                    .OrderByDescending(node => node.IsManual)
                    .ThenByDescending(node => node.Weight)
                    .ThenByDescending(node => node.UpdatedAt)
                    .Take(MaximumNodes)
                    .ToArray(),
                []);
            var retained = snapshot.Nodes.Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            snapshot = snapshot with
            {
                Edges = edges.Values
                    .Where(edge => retained.Contains(edge.SourceId) && retained.Contains(edge.TargetId))
                    .OrderByDescending(edge => edge.Weight)
                    .Take(MaximumEdges)
                    .ToArray()
            };
            await SaveAsync(snapshot, cancellationToken);
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<KnowledgeNode> AddKnowledgeAsync(
        string label,
        string detail,
        string? relatedNodeId,
        CancellationToken cancellationToken)
    {
        label = label.Trim();
        detail = detail.Trim();
        if (label.Length is < 2 or > 100)
        {
            throw new InvalidOperationException("Knowledge label must contain 2-100 characters.");
        }
        if (detail.Length > 2000)
        {
            throw new InvalidOperationException("Knowledge detail exceeds 2,000 characters.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = GetSnapshot();
            var node = new KnowledgeNode(
                CreateId("knowledge", label + Guid.NewGuid().ToString("N")),
                label,
                "Knowledge",
                detail,
                1.8,
                true,
                DateTimeOffset.Now);
            var nodes = snapshot.Nodes.Append(node).TakeLast(MaximumNodes).ToArray();
            var edges = snapshot.Edges.ToList();
            if (!string.IsNullOrWhiteSpace(relatedNodeId)
                && nodes.Any(item => item.Id.Equals(relatedNodeId, StringComparison.OrdinalIgnoreCase)))
            {
                edges.Add(new KnowledgeEdge(node.Id, relatedNodeId, "related to", 1.3));
            }
            var updated = new KnowledgeGraphSnapshot(
                DateTimeOffset.Now,
                nodes,
                edges.TakeLast(MaximumEdges).ToArray());
            await SaveAsync(updated, cancellationToken);
            return node;
        }
        finally
        {
            _gate.Release();
        }
    }

    public string ExportJson(int maximumNodes = 120)
    {
        var snapshot = GetSnapshot();
        var nodes = snapshot.Nodes.Take(Math.Clamp(maximumNodes, 1, 300)).ToArray();
        var retained = nodes.Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return JsonSerializer.Serialize(new
        {
            snapshot.UpdatedAt,
            nodes,
            edges = snapshot.Edges.Where(edge =>
                retained.Contains(edge.SourceId) && retained.Contains(edge.TargetId))
        });
    }

    public string QueryJson(string? query, int maximumNodes = 80)
    {
        var snapshot = GetSnapshot();
        query = query?.Trim();
        var nodes = snapshot.Nodes
            .Where(node => string.IsNullOrWhiteSpace(query)
                           || node.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                           || node.Kind.Contains(query, StringComparison.OrdinalIgnoreCase)
                           || node.Detail.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Take(Math.Clamp(maximumNodes, 1, 200))
            .ToArray();
        var retained = nodes.Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return JsonSerializer.Serialize(new
        {
            query,
            snapshot.UpdatedAt,
            count = nodes.Length,
            nodes,
            edges = snapshot.Edges.Where(edge =>
                retained.Contains(edge.SourceId) && retained.Contains(edge.TargetId))
        });
    }

    private async Task SaveAsync(
        KnowledgeGraphSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_graphPath)
                        ?? throw new InvalidOperationException("Knowledge graph path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = _graphPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(snapshot, _options),
                cancellationToken);
            File.Move(temporary, _graphPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string PutNode(
        IDictionary<string, KnowledgeNode> nodes,
        string scope,
        string sourceId,
        string label,
        string kind,
        string detail,
        double weight,
        bool isManual,
        DateTimeOffset updatedAt)
    {
        var id = CreateId(scope, sourceId);
        nodes[id] = new KnowledgeNode(
            id,
            string.IsNullOrWhiteSpace(label) ? kind : Trim(label, 100),
            kind,
            Trim(detail, 2000),
            weight,
            isManual,
            updatedAt);
        return id;
    }

    private static void Link(
        IDictionary<string, KnowledgeEdge> edges,
        string source,
        string target,
        string relation,
        double weight)
    {
        var edge = new KnowledgeEdge(source, target, relation, weight);
        edges[EdgeKey(edge)] = edge;
    }

    private static string EdgeKey(KnowledgeEdge edge)
        => $"{edge.SourceId}|{edge.TargetId}|{edge.Relation}";

    private static string CreateId(string scope, string source)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(scope + ":" + source));
        return scope + "-" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static IEnumerable<string> ExtractConcepts(string value)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "for", "with", "this", "that", "一个", "进行", "生成", "分析", "任务", "工作"
        };
        return Regex.Split(value, @"[\s,，。.;；:：!?！？、/\\|()\[\]{}""']+")
            .Select(item => item.Trim())
            .Where(item => item.Length is >= 2 and <= 24 && !ignored.Contains(item))
            .Distinct(StringComparer.CurrentCultureIgnoreCase);
    }

    private static string Trim(string value, int maximum)
    {
        value = value?.Trim() ?? string.Empty;
        return value.Length <= maximum ? value : value[..maximum];
    }
}
