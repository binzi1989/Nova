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
    DateTimeOffset UpdatedAt)
{
    public string SourceType { get; init; } = string.Empty;
    public string SourceId { get; init; } = string.Empty;
    public string SourceLabel { get; init; } = string.Empty;
    public bool IsDeletable { get; init; }
}

public sealed record KnowledgeEdge(
    string SourceId,
    string TargetId,
    string Relation,
    double Weight)
{
    public bool IsInferred { get; init; }
    public double Confidence { get; init; } = 1;
    public string Evidence { get; init; } = string.Empty;
    public string ReviewState { get; init; } = "evidence";
}

public sealed record KnowledgeGraphSnapshot(
    DateTimeOffset UpdatedAt,
    IReadOnlyList<KnowledgeNode> Nodes,
    IReadOnlyList<KnowledgeEdge> Edges);

public sealed record KnowledgeInputRecord(
    string Id,
    string TaskId,
    string TaskTitle,
    string WorkspaceRoot,
    string Content,
    DateTimeOffset CreatedAt);

public sealed class KnowledgeGraphService
{
    private const int MaximumNodes = 300;
    private const int MaximumEdges = 1000;
    private static readonly Regex ApiCredentialPattern = new(
        @"(?i)\b(sk-[a-z0-9_-]{8,}|bearer\s+[a-z0-9._~+/=-]{8,})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SecretAssignmentPattern = new(
        @"(?i)\b(api[_-]?key|access[_-]?token|secret|password)\s*[:=]\s*[\""']?[^\s,;\""']{5,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
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
                .Where(node => node.IsManual
                               || node.SourceType.Equals(
                                   "conversation",
                                   StringComparison.OrdinalIgnoreCase))
                .ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
            var edges = existing.Edges
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
                    task.UpdatedAt,
                    "task",
                    task.TaskId,
                    task.Title);
                var workspaceSource = NormalizeWorkspaceSource(task.WorkspaceRoot);
                var workspaceLabel = string.IsNullOrWhiteSpace(task.WorkspaceRoot)
                    ? "未指定工作区"
                    : Path.GetFileName(task.WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar))
                      ?? task.WorkspaceRoot;
                var workspace = PutNode(
                    nodes,
                    "workspace",
                    workspaceSource,
                    workspaceLabel,
                    "Project",
                    workspaceSource,
                    1.5,
                    false,
                    task.UpdatedAt,
                    "workspace",
                    workspaceSource,
                    workspaceLabel);
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
                    document.IndexedAt,
                    "document",
                    document.Id,
                    document.RelativePath);
                var workspaceSource = NormalizeWorkspaceSource(document.WorkspaceRoot);
                var workspaceLabel = Path.GetFileName(
                                         document.WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar))
                                     ?? document.WorkspaceRoot;
                var workspace = PutNode(
                    nodes,
                    "workspace",
                    workspaceSource,
                    workspaceLabel,
                    "Project",
                    workspaceSource,
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
                    artifact.CreatedAt ?? now,
                    "artifact",
                    artifact.Id,
                    artifact.Title);
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
                    artifact.CreatedAt ?? now,
                    "task",
                    artifact.TaskId,
                    artifact.Title);
                Link(edges, task, artifactNode, "delivers", 1.6);

                if (!string.IsNullOrWhiteSpace(artifact.WorkspaceRoot))
                {
                    var workspaceSource = NormalizeWorkspaceSource(artifact.WorkspaceRoot);
                    var workspaceLabel = Path.GetFileName(
                                             artifact.WorkspaceRoot.TrimEnd(
                                                 Path.DirectorySeparatorChar))
                                         ?? artifact.WorkspaceRoot;
                    var workspace = PutNode(
                        nodes,
                        "workspace",
                        workspaceSource,
                        workspaceLabel,
                        "Project",
                        workspaceSource,
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

            AddInferredMappings(nodes, edges);
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

    public async Task<KnowledgeNode> IngestInputAsync(
        KnowledgeInputRecord input,
        CancellationToken cancellationToken = default)
    {
        var content = SanitizeKnowledgeText(input.Content);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("用户输入在脱敏后没有可索引内容。");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = GetSnapshot();
            var nodes = snapshot.Nodes.ToDictionary(
                node => node.Id,
                StringComparer.OrdinalIgnoreCase);
            var edges = snapshot.Edges.ToDictionary(
                EdgeKey,
                StringComparer.OrdinalIgnoreCase);
            var taskNode = PutNode(
                nodes,
                "task",
                input.TaskId,
                input.TaskTitle,
                "Goal",
                $"用户输入来源：{input.TaskTitle}",
                1.5,
                false,
                input.CreatedAt,
                "task",
                input.TaskId,
                input.TaskTitle);
            var workspaceSource = NormalizeWorkspaceSource(input.WorkspaceRoot);
            var workspaceLabel = string.IsNullOrWhiteSpace(input.WorkspaceRoot)
                ? "我的知识"
                : Path.GetFileName(input.WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar))
                  ?? "当前工作区";
            var workspaceNode = PutNode(
                nodes,
                "workspace",
                workspaceSource,
                workspaceLabel,
                "KnowledgeSpace",
                "本机知识空间",
                1.8,
                false,
                input.CreatedAt,
                "workspace",
                workspaceSource,
                workspaceLabel);
            Link(edges, taskNode, workspaceNode, "belongs to", 1.4);

            var inputNode = PutNode(
                nodes,
                "input",
                input.Id,
                CreateInputLabel(content),
                "Input",
                content,
                2.1,
                false,
                input.CreatedAt,
                "conversation",
                input.Id,
                input.TaskTitle,
                true);
            Link(edges, inputNode, taskNode, "contributes to", 1.8);
            foreach (var concept in ExtractConcepts(content).Take(6))
            {
                var conceptNode = PutNode(
                    nodes,
                    "concept",
                    concept,
                    concept,
                    "Concept",
                    $"来自用户输入：{input.TaskTitle}",
                    1.15,
                    false,
                    input.CreatedAt,
                    "conversation",
                    input.Id,
                    input.TaskTitle);
                Link(edges, inputNode, conceptNode, "mentions", 1.2);
            }

            AddInferredMappings(nodes, edges);
            var updated = LimitGraph(nodes.Values, edges.Values, DateTimeOffset.Now);
            await SaveAsync(updated, cancellationToken);
            return updated.Nodes.First(node => node.Id == inputNode);
        }
        finally
        {
            _gate.Release();
        }
    }

    public KnowledgeGraphSnapshot CreateView(
        string? workspaceRoot,
        string? query = null,
        int maximumNodes = 120)
    {
        var snapshot = GetSnapshot();
        maximumNodes = Math.Clamp(maximumNodes, 1, 200);
        query = query?.Trim();
        var allowed = snapshot.Nodes.Select(node => node.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            var workspaceId = CreateId("workspace", NormalizeWorkspaceSource(workspaceRoot));
            // A workspace view is an isolation boundary. Inferred mappings may
            // connect similar knowledge across spaces, but they must not pull
            // another workspace's private nodes into the current view.
            allowed = ExpandRelated(snapshot, [workspaceId], maximumDepth: 3, includeInferred: false);
        }
        if (!string.IsNullOrWhiteSpace(query))
        {
            var matched = snapshot.Nodes
                .Where(node => allowed.Contains(node.Id)
                               && (node.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                   || node.Detail.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                   || node.Kind.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .Select(node => node.Id)
                .ToArray();
            allowed.IntersectWith(ExpandRelated(snapshot, matched, maximumDepth: 1));
        }
        var nodes = snapshot.Nodes
            .Where(node => allowed.Contains(node.Id))
            .OrderByDescending(node => node.Weight)
            .ThenByDescending(node => node.UpdatedAt)
            .Take(maximumNodes)
            .ToArray();
        var retained = nodes.Select(node => node.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new KnowledgeGraphSnapshot(
            snapshot.UpdatedAt,
            nodes,
            snapshot.Edges
                .Where(edge => retained.Contains(edge.SourceId)
                               && retained.Contains(edge.TargetId)
                               && !edge.ReviewState.Equals("rejected", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(edge => edge.Weight)
                .Take(MaximumEdges)
                .ToArray());
    }

    public async Task<bool> DeleteNodeAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = GetSnapshot();
            var node = snapshot.Nodes.FirstOrDefault(item =>
                item.Id.Equals(nodeId, StringComparison.OrdinalIgnoreCase));
            if (node is null) return false;
            if (!node.IsManual && !node.IsDeletable)
            {
                throw new InvalidOperationException("系统生成的任务、文档和关系节点不能单独删除。");
            }
            var removedIds = snapshot.Nodes
                .Where(item => item.Id.Equals(nodeId, StringComparison.OrdinalIgnoreCase)
                               || (node.SourceType.Equals("conversation", StringComparison.OrdinalIgnoreCase)
                                   && item.SourceType.Equals("conversation", StringComparison.OrdinalIgnoreCase)
                                   && item.SourceId.Equals(node.SourceId, StringComparison.OrdinalIgnoreCase)))
                .Select(item => item.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var updated = new KnowledgeGraphSnapshot(
                DateTimeOffset.Now,
                snapshot.Nodes.Where(item => !removedIds.Contains(item.Id)).ToArray(),
                snapshot.Edges.Where(edge =>
                    !removedIds.Contains(edge.SourceId)
                    && !removedIds.Contains(edge.TargetId)).ToArray());
            await SaveAsync(updated, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<KnowledgeEdge> ReviewMappingAsync(
        string sourceId,
        string targetId,
        bool accepted,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = GetSnapshot();
            var existing = snapshot.Edges.FirstOrDefault(edge =>
                ((edge.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase)
                  && edge.TargetId.Equals(targetId, StringComparison.OrdinalIgnoreCase))
                 || (edge.SourceId.Equals(targetId, StringComparison.OrdinalIgnoreCase)
                     && edge.TargetId.Equals(sourceId, StringComparison.OrdinalIgnoreCase)))
                && edge.Relation.Equals("potential mapping", StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                throw new InvalidOperationException("这条候选映射已经不存在，请刷新知识网络后重试。");
            }

            var edges = snapshot.Edges.ToDictionary(EdgeKey, StringComparer.OrdinalIgnoreCase);
            edges.Remove(EdgeKey(existing));
            var reviewed = new KnowledgeEdge(
                existing.SourceId,
                existing.TargetId,
                accepted ? "confirmed mapping" : "potential mapping",
                accepted ? Math.Max(existing.Weight, 1.35) : existing.Weight)
            {
                IsInferred = !accepted,
                Confidence = accepted ? 1 : existing.Confidence,
                Evidence = accepted
                    ? "用户已确认：" + existing.Evidence
                    : "用户已否决：" + existing.Evidence,
                ReviewState = accepted ? "accepted" : "rejected"
            };
            edges[EdgeKey(reviewed)] = reviewed;
            await SaveAsync(new KnowledgeGraphSnapshot(
                DateTimeOffset.Now,
                snapshot.Nodes,
                edges.Values.TakeLast(MaximumEdges).ToArray()), cancellationToken);
            return reviewed;
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
                DateTimeOffset.Now)
            {
                SourceType = "manual",
                SourceId = label,
                SourceLabel = "手动知识",
                IsDeletable = true
            };
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

    private static KnowledgeGraphSnapshot LimitGraph(
        IEnumerable<KnowledgeNode> sourceNodes,
        IEnumerable<KnowledgeEdge> sourceEdges,
        DateTimeOffset updatedAt)
    {
        var nodes = sourceNodes
            .OrderByDescending(node => node.IsManual || node.IsDeletable)
            .ThenByDescending(node => node.Weight)
            .ThenByDescending(node => node.UpdatedAt)
            .Take(MaximumNodes)
            .ToArray();
        var retained = nodes.Select(node => node.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new KnowledgeGraphSnapshot(
            updatedAt,
            nodes,
            sourceEdges
                .Where(edge => retained.Contains(edge.SourceId)
                               && retained.Contains(edge.TargetId))
                .OrderByDescending(edge => edge.Weight)
                .Take(MaximumEdges)
                .ToArray());
    }

    private static HashSet<string> ExpandRelated(
        KnowledgeGraphSnapshot snapshot,
        IEnumerable<string> seeds,
        int maximumDepth,
        bool includeInferred = true)
    {
        var adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var edge in snapshot.Edges)
            {
                if (edge.ReviewState.Equals("rejected", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!includeInferred && edge.IsInferred)
                {
                    continue;
                }

            if (!adjacency.TryGetValue(edge.SourceId, out var source))
            {
                source = [];
                adjacency[edge.SourceId] = source;
            }
            if (!adjacency.TryGetValue(edge.TargetId, out var target))
            {
                target = [];
                adjacency[edge.TargetId] = target;
            }
            source.Add(edge.TargetId);
            target.Add(edge.SourceId);
        }
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Id, int Depth)>();
        foreach (var seed in seeds.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            if (visited.Add(seed)) queue.Enqueue((seed, 0));
        }
        while (queue.TryDequeue(out var current))
        {
            if (current.Depth >= maximumDepth
                || !adjacency.TryGetValue(current.Id, out var related)) continue;
            foreach (var nodeId in related)
            {
                if (visited.Add(nodeId)) queue.Enqueue((nodeId, current.Depth + 1));
            }
        }
        return visited;
    }

    private static string SanitizeKnowledgeText(string value)
    {
        value = ApiCredentialPattern.Replace(value ?? string.Empty, "[已脱敏凭据]");
        value = SecretAssignmentPattern.Replace(value, match =>
            match.Groups[1].Value + "=[已脱敏]");
        return Trim(value, 2000);
    }

    private static string CreateInputLabel(string content)
    {
        var normalized = Regex.Replace(content, @"\s+", " ").Trim();
        if (normalized.Length <= 72) return normalized;
        return normalized[..72] + "…";
    }

    private static string NormalizeWorkspaceSource(string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return "personal";
        try
        {
            return Path.GetFullPath(workspaceRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return workspaceRoot.Trim();
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
        DateTimeOffset updatedAt,
        string sourceType = "",
        string metadataSourceId = "",
        string sourceLabel = "",
        bool isDeletable = false)
    {
        var id = CreateId(scope, sourceId);
        nodes[id] = new KnowledgeNode(
            id,
            string.IsNullOrWhiteSpace(label) ? kind : Trim(label, 100),
            kind,
            Trim(detail, 2000),
            weight,
            isManual,
            updatedAt)
        {
            SourceType = sourceType,
            SourceId = metadataSourceId,
            SourceLabel = Trim(sourceLabel, 160),
            IsDeletable = isDeletable
        };
        return id;
    }

    private static void Link(
        IDictionary<string, KnowledgeEdge> edges,
        string source,
        string target,
        string relation,
        double weight,
        bool isInferred = false,
        double confidence = 1,
        string evidence = "",
        string? reviewState = null)
    {
        var edge = new KnowledgeEdge(source, target, relation, weight)
        {
            IsInferred = isInferred,
            Confidence = Math.Clamp(confidence, 0, 1),
            Evidence = Trim(evidence, 500),
            ReviewState = reviewState ?? (isInferred ? "suggested" : "evidence")
        };
        edges[EdgeKey(edge)] = edge;
    }

    private static void AddInferredMappings(
        IReadOnlyDictionary<string, KnowledgeNode> nodes,
        IDictionary<string, KnowledgeEdge> edges)
    {
        var rejectedPairs = edges.Values
            .Where(edge => edge.ReviewState.Equals("rejected", StringComparison.OrdinalIgnoreCase))
            .Select(edge => PairKey(edge.SourceId, edge.TargetId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in edges
                     .Where(item => item.Value.IsInferred
                                    && !item.Value.ReviewState.Equals("rejected", StringComparison.OrdinalIgnoreCase))
                     .Select(item => item.Key)
                     .ToArray())
        {
            edges.Remove(key);
        }
        var conceptIds = nodes.Values
            .Where(node => node.Kind.Equals("Concept", StringComparison.OrdinalIgnoreCase))
            .Select(node => node.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conceptsByNode = edges.Values
            .Where(edge => !edge.IsInferred && conceptIds.Contains(edge.TargetId))
            .GroupBy(edge => edge.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(edge => edge.TargetId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        var candidates = conceptsByNode.Keys
            .Where(id => nodes.TryGetValue(id, out var node)
                         && node.Kind is "Input" or "Document" or "Artifact" or "Goal" or "Knowledge")
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();
        var created = 0;
        for (var leftIndex = 0; leftIndex < candidates.Length && created < 120; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1;
                 rightIndex < candidates.Length && created < 120;
                 rightIndex++)
            {
                var left = candidates[leftIndex];
                var right = candidates[rightIndex];
                if (rejectedPairs.Contains(PairKey(left, right))) continue;
                var shared = conceptsByNode[left]
                    .Intersect(conceptsByNode[right], StringComparer.OrdinalIgnoreCase)
                    .Take(4)
                    .ToArray();
                if (shared.Length < 2) continue;
                var labels = shared
                    .Select(id => nodes.TryGetValue(id, out var concept) ? concept.Label : id)
                    .ToArray();
                var confidence = Math.Min(0.88, 0.48 + shared.Length * 0.1);
                Link(
                    edges,
                    left,
                    right,
                    "potential mapping",
                    0.7 + shared.Length * 0.08,
                    true,
                    confidence,
                    "共享概念：" + string.Join("、", labels));
                created++;
            }
        }
    }

    private static string EdgeKey(KnowledgeEdge edge)
        => $"{edge.SourceId}|{edge.TargetId}|{edge.Relation}";

    private static string PairKey(string left, string right)
        => string.Compare(left, right, StringComparison.OrdinalIgnoreCase) <= 0
            ? $"{left}|{right}"
            : $"{right}|{left}";

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
