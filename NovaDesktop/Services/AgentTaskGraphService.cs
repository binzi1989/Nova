using System.IO;
using System.Text.Json;
using NovaDesktop.Models;

namespace NovaDesktop.Services;

public sealed class AgentTaskGraphService
{
    private readonly object _stateLock = new();
    private readonly string _root;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly Dictionary<string, AgentTaskGraphSnapshot> _graphs =
        new(StringComparer.OrdinalIgnoreCase);

    public AgentTaskGraphService(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "agent-os",
            "task-graphs");
    }

    public async Task<AgentTaskGraphSnapshot> CreateAsync(
        string taskId,
        string title,
        AgentExecutionMode mode,
        CancellationToken cancellationToken = default,
        long executionSequence = 0)
    {
        var now = DateTimeOffset.Now;
        var blueprints = BuildBlueprint(mode);
        var nodes = blueprints.Select((blueprint, index) => new AgentGraphNode(
            $"{taskId}-{index + 1:D2}",
            blueprint.Title,
            blueprint.Role,
            blueprint.Dependencies.Select(dependencyIndex =>
                $"{taskId}-{dependencyIndex + 1:D2}").ToArray(),
            index == 0 ? AgentGraphNodeState.Ready : AgentGraphNodeState.Pending,
            0,
            index == 0 ? "Ready for dispatch" : "Waiting for dependencies",
            now)).ToArray();
        var graph = new AgentTaskGraphSnapshot(taskId, title, mode, nodes, now, now)
        {
            ExecutionSequence = executionSequence
        };
        lock (_stateLock)
        {
            _graphs[taskId] = graph;
        }
        await PersistAsync(graph, cancellationToken);
        return graph;
    }

    public AgentTaskGraphSnapshot? GetSnapshot(string? taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return null;
        }
        lock (_stateLock)
        {
            if (_graphs.TryGetValue(taskId, out var graph))
            {
                return graph;
            }
        }
        return Load(taskId);
    }

    public async Task ApplyRuntimeEventAsync(
        string taskId,
        AgentRuntimeEvent runtimeEvent,
        CancellationToken cancellationToken = default,
        long executionSequence = 0)
    {
        if (runtimeEvent.Kind == AgentRuntimeEventKind.TextDelta)
        {
            return;
        }

        AgentTaskGraphSnapshot? updated;
        lock (_stateLock)
        {
            if (!_graphs.TryGetValue(taskId, out var graph))
            {
                return;
            }
            updated = ApplyEvent(graph, runtimeEvent);
            updated = updated with
            {
                ExecutionSequence = Math.Max(
                    graph.ExecutionSequence,
                    executionSequence)
            };
            _graphs[taskId] = updated;
        }
        await PersistAsync(updated, cancellationToken);
    }

    public async Task CompleteAsync(
        string taskId,
        bool succeeded,
        string detail,
        CancellationToken cancellationToken = default,
        long executionSequence = 0)
    {
        AgentTaskGraphSnapshot? updated;
        lock (_stateLock)
        {
            if (!_graphs.TryGetValue(taskId, out var graph))
            {
                return;
            }
            var now = DateTimeOffset.Now;
            var nodes = graph.Nodes.Select(node =>
            {
                if (succeeded)
                {
                    return node with
                    {
                        State = AgentGraphNodeState.Completed,
                        Progress = 100,
                        Detail = detail,
                        UpdatedAt = now
                    };
                }
                if (node.State is AgentGraphNodeState.Running
                    or AgentGraphNodeState.Ready
                    or AgentGraphNodeState.Waiting)
                {
                    return node with
                    {
                        State = AgentGraphNodeState.Failed,
                        Detail = detail,
                        UpdatedAt = now
                    };
                }
                return node;
            }).ToArray();
            updated = graph with
            {
                Nodes = nodes,
                UpdatedAt = now,
                ExecutionSequence = Math.Max(
                    graph.ExecutionSequence,
                    executionSequence)
            };
            _graphs[taskId] = updated;
        }
        await PersistAsync(updated, cancellationToken);
    }

    private AgentTaskGraphSnapshot? Load(string taskId)
    {
        var path = GetPath(taskId);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var graph = JsonSerializer.Deserialize<AgentTaskGraphSnapshot>(
                File.ReadAllText(path),
                _jsonOptions);
            if (graph is not null)
            {
                lock (_stateLock)
                {
                    _graphs[taskId] = graph;
                }
            }
            return graph;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    private AgentTaskGraphSnapshot ApplyEvent(
        AgentTaskGraphSnapshot graph,
        AgentRuntimeEvent runtimeEvent)
    {
        var now = DateTimeOffset.Now;
        var nodes = graph.Nodes.ToArray();
        if (runtimeEvent.Kind == AgentRuntimeEventKind.Completed)
        {
            var completedIndex = Array.FindLastIndex(
                nodes,
                node => node.State is AgentGraphNodeState.Running
                    or AgentGraphNodeState.Waiting);
            if (completedIndex < 0)
            {
                completedIndex = Array.FindIndex(
                    nodes,
                    node => node.State == AgentGraphNodeState.Ready);
            }
            if (completedIndex < 0)
            {
                return graph;
            }
            nodes[completedIndex] = nodes[completedIndex] with
            {
                State = AgentGraphNodeState.Completed,
                Progress = 100,
                Detail = runtimeEvent.Detail,
                UpdatedAt = now
            };
            UnlockReadyNodes(nodes, now);
            return graph with { Nodes = nodes, UpdatedAt = now };
        }

        if (runtimeEvent.Kind == AgentRuntimeEventKind.Failed)
        {
            var runningIndex = Array.FindIndex(
                nodes,
                node => node.State is AgentGraphNodeState.Running
                    or AgentGraphNodeState.Waiting
                    or AgentGraphNodeState.Ready);
            if (runningIndex >= 0)
            {
                nodes[runningIndex] = nodes[runningIndex] with
                {
                    State = AgentGraphNodeState.Failed,
                    Detail = runtimeEvent.Detail,
                    UpdatedAt = now
                };
            }
            return graph with { Nodes = nodes, UpdatedAt = now };
        }

        var activeNode = nodes.FirstOrDefault(node =>
            node.State is AgentGraphNodeState.Running or AgentGraphNodeState.Waiting);
        var desiredRole = runtimeEvent.Agent.Contains(
            "Goal Explorer",
            StringComparison.OrdinalIgnoreCase)
            ? "goal-explorer"
            : runtimeEvent.Agent.Contains(
            "Mesh Planner",
            StringComparison.OrdinalIgnoreCase)
            ? "planner"
            : runtimeEvent.Agent.Contains(
                "Mesh Council",
                StringComparison.OrdinalIgnoreCase)
                || runtimeEvent.Agent.Contains(
                    "Tournament Council",
                    StringComparison.OrdinalIgnoreCase)
                ? "adjudicator"
            : runtimeEvent.Agent.Contains(
                "Mesh Integrator",
                StringComparison.OrdinalIgnoreCase)
                ? "reviewer"
            : runtimeEvent.Agent.Contains(
                "验证竞技场",
                StringComparison.OrdinalIgnoreCase)
                ? "reviewer"
            : runtimeEvent.Agent.Contains(
                "Mesh Worker",
                StringComparison.OrdinalIgnoreCase)
                ? "implementer"
            : runtimeEvent.Agent.Contains(
                "候选",
                StringComparison.OrdinalIgnoreCase)
                ? "implementer"
            : runtimeEvent.Agent.Contains(
                "Merge Gate",
                StringComparison.OrdinalIgnoreCase)
                ? "merge-guardian"
            : runtimeEvent.Agent.Contains(
                "Council",
                StringComparison.OrdinalIgnoreCase)
                ? "adversarial-reviewer"
            : runtimeEvent.Kind switch
            {
                AgentRuntimeEventKind.Thinking when activeNode is null =>
                    nodes.FirstOrDefault(node => node.State == AgentGraphNodeState.Ready)?.Role
                    ?? "planner",
                AgentRuntimeEventKind.Thinking => activeNode.Role,
                AgentRuntimeEventKind.BatchStarted or AgentRuntimeEventKind.BatchCompleted => "researcher",
                AgentRuntimeEventKind.ToolRequested
                    or AgentRuntimeEventKind.ToolRunning
                    or AgentRuntimeEventKind.ToolCompleted
                    or AgentRuntimeEventKind.ToolBatchStarted
                    or AgentRuntimeEventKind.ToolBatchCompleted => AgentExecutionPolicy.CanMutateWorkspace(graph.Mode)
                        ? "implementer"
                        : "analyst",
                AgentRuntimeEventKind.Message => "reviewer",
                _ => "planner"
            };

        var desiredIndex = Array.FindIndex(
            nodes,
            node => node.Role.Equals(desiredRole, StringComparison.OrdinalIgnoreCase)
                    && node.State is not AgentGraphNodeState.Completed
                    and not AgentGraphNodeState.Failed
                    and not AgentGraphNodeState.Skipped);
        if (desiredIndex >= 0 && nodes[desiredIndex].State == AgentGraphNodeState.Pending)
        {
            for (var index = 0; index < desiredIndex; index++)
            {
                if (nodes[index].State is AgentGraphNodeState.Pending
                    or AgentGraphNodeState.Ready
                    or AgentGraphNodeState.Running
                    or AgentGraphNodeState.Waiting)
                {
                    nodes[index] = nodes[index] with
                    {
                        State = AgentGraphNodeState.Completed,
                        Progress = 100,
                        Detail = "Phase boundary reached",
                        UpdatedAt = now
                    };
                }
            }
            UnlockReadyNodes(nodes, now);
        }

        var candidateIndex = Array.FindIndex(
            nodes,
            node => node.Role.Equals(desiredRole, StringComparison.OrdinalIgnoreCase)
                    && node.State is AgentGraphNodeState.Ready
                    or AgentGraphNodeState.Running
                    or AgentGraphNodeState.Waiting);
        if (candidateIndex < 0)
        {
            candidateIndex = Array.FindIndex(
                nodes,
                node => node.State is AgentGraphNodeState.Ready
                    or AgentGraphNodeState.Running
                    or AgentGraphNodeState.Waiting);
        }
        if (candidateIndex < 0)
        {
            return graph;
        }

        for (var index = 0; index < nodes.Length; index++)
        {
            if (index == candidateIndex)
            {
                var waiting = runtimeEvent.Kind == AgentRuntimeEventKind.ToolRequested;
                nodes[index] = nodes[index] with
                {
                    State = waiting ? AgentGraphNodeState.Waiting : AgentGraphNodeState.Running,
                    Progress = Math.Max(nodes[index].Progress, Math.Clamp(runtimeEvent.Progress, 8, 92)),
                    Detail = runtimeEvent.Action,
                    UpdatedAt = now
                };
                continue;
            }

            if (nodes[index].State == AgentGraphNodeState.Running
                && index < candidateIndex)
            {
                nodes[index] = nodes[index] with
                {
                    State = AgentGraphNodeState.Completed,
                    Progress = 100,
                    Detail = "Dependency completed",
                    UpdatedAt = now
                };
            }
        }

        UnlockReadyNodes(nodes, now);
        return graph with { Nodes = nodes, UpdatedAt = now };
    }

    private static void UnlockReadyNodes(AgentGraphNode[] nodes, DateTimeOffset now)
    {
        var completed = nodes
            .Where(node => node.State == AgentGraphNodeState.Completed)
            .Select(node => node.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < nodes.Length; index++)
        {
            var node = nodes[index];
            if (node.State == AgentGraphNodeState.Pending
                && node.Dependencies.All(completed.Contains))
            {
                nodes[index] = node with
                {
                    State = AgentGraphNodeState.Ready,
                    Detail = "Dependencies satisfied",
                    UpdatedAt = now
                };
            }
        }
    }

    private async Task PersistAsync(
        AgentTaskGraphSnapshot graph,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var path = GetPath(graph.TaskId);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(graph, _jsonOptions),
            cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    private string GetPath(string taskId)
    {
        var safe = string.Concat(taskId.Where(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
        if (safe.Length == 0)
        {
            throw new InvalidOperationException("Task graph ID cannot be converted to a safe filename.");
        }
        return Path.Combine(_root, safe + ".json");
    }

    private static IReadOnlyList<NodeBlueprint> BuildBlueprint(AgentExecutionMode mode)
        => mode switch
        {
            AgentExecutionMode.Ask =>
            [
                new NodeBlueprint("Evidence answer", "analyst", [])
            ],
            AgentExecutionMode.Plan =>
            [
                new NodeBlueprint("Map context", "analyst", []),
                new NodeBlueprint("Design execution plan", "planner", [0]),
                new NodeBlueprint("Challenge assumptions", "reviewer", [1])
            ],
            AgentExecutionMode.Build =>
            [
                new NodeBlueprint("Map repository", "analyst", []),
                new NodeBlueprint("Plan scoped change", "planner", [0]),
                new NodeBlueprint("Implement approved patch", "implementer", [1]),
                new NodeBlueprint("Verify and review", "reviewer", [2])
            ],
            AgentExecutionMode.Autopilot =>
            [
                new NodeBlueprint("Establish mission boundary", "planner", []),
                new NodeBlueprint("Parallel evidence scan", "researcher", [0]),
                new NodeBlueprint("Map repository", "analyst", [0]),
                new NodeBlueprint("Parallel isolated implementation", "implementer", [1, 2]),
                new NodeBlueprint("Integrate dependency waves and verify", "reviewer", [3]),
                new NodeBlueprint("Adjudication council", "adjudicator", [4]),
                new NodeBlueprint("User-controlled merge gate", "merge-guardian", [5]),
                new NodeBlueprint("Independent verification council", "adversarial-reviewer", [6]),
                new NodeBlueprint("Integrate and deliver", "integrator", [7])
            ],
            _ =>
            [
                new NodeBlueprint("Explore desired outcome", "goal-explorer", []),
                new NodeBlueprint("Scan evidence and unknowns", "researcher", [0]),
                new NodeBlueprint("Freeze Mission Charter", "planner", [1]),
                new NodeBlueprint("Map solution space", "analyst", [1, 2]),
                new NodeBlueprint("Parallel isolated execution", "implementer", [3]),
                new NodeBlueprint("Integrate and verify result", "reviewer", [4]),
                new NodeBlueprint("Outcome adjudication council", "adjudicator", [5]),
                new NodeBlueprint("User-controlled authority gate", "merge-guardian", [6]),
                new NodeBlueprint("Independent result verification", "adversarial-reviewer", [7]),
                new NodeBlueprint("Map evidence to success signals", "goal-auditor", [8]),
                new NodeBlueprint("Deliver achieved outcome", "integrator", [9])
            ]
        };

    private sealed record NodeBlueprint(
        string Title,
        string Role,
        IReadOnlyList<int> Dependencies);
}
