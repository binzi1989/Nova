using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaDesktop.Services;

public sealed record AgentMeshWorkPackage(
    string Id,
    string Title,
    string Instruction,
    IReadOnlyList<string> OwnedPaths,
    IReadOnlyList<string> DependsOn);

public sealed record AgentMeshPlan(
    string Strategy,
    IReadOnlyList<AgentMeshWorkPackage> Packages)
{
    public IReadOnlyList<IReadOnlyList<AgentMeshWorkPackage>> BuildWaves()
    {
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remaining = Packages.ToDictionary(
            item => item.Id,
            StringComparer.OrdinalIgnoreCase);
        var waves = new List<IReadOnlyList<AgentMeshWorkPackage>>();
        while (remaining.Count > 0)
        {
            var wave = remaining.Values
                .Where(item => item.DependsOn.All(completed.Contains))
                .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (wave.Length == 0)
            {
                throw new InvalidOperationException(
                    "Agent Mesh plan contains a dependency cycle.");
            }
            waves.Add(wave);
            foreach (var item in wave)
            {
                completed.Add(item.Id);
                remaining.Remove(item.Id);
            }
        }
        return waves;
    }
}

public static class AgentMeshPlannerService
{
    private static readonly Regex IdPattern = new(
        "^[a-z][a-z0-9-]{1,31}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string BuildPrompt(
        string objective,
        TaskOutcomeContract contract,
        AdaptiveContextPack? contextPack,
        EngineeringWorkspaceSnapshot snapshot)
    {
        var paths = contextPack?.Selections
            .Select(item => item.RelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToArray() ?? [];
        return $$"""
                You are NOVA Agent Mesh Planner. Produce a safe implementation DAG for multiple isolated coding workers.
                Do not implement anything and do not call tools. Return JSON only.

                OBJECTIVE:
                {{objective}}

                FROZEN PROOF-OF-DONE:
                {{TaskOutcomeContractService.FormatForPrompt(contract)}}

                REPOSITORY:
                Branch: {{snapshot.GitBranch}}
                Projects: {{string.Join(", ", snapshot.Projects)}}
                Verification: {{snapshot.VerificationCommand}}
                High-signal paths: {{string.Join(", ", paths)}}

                Requirements:
                - Return 2 to 4 work packages.
                - At least one dependency wave must contain 2 or more packages that can execute in parallel.
                - Every package owns explicit workspace-relative paths.
                - A path ending in "/" owns that directory prefix; every other path owns exactly one file.
                - No glob patterns, absolute paths, "..", ".git", generated output, dependency folders, or overlapping ownership.
                - Assign every shared integration file to exactly one package.
                - Dependencies must reference package IDs and form an acyclic graph.
                - Instructions must be self-contained and include an observable completion condition.

                JSON schema:
                {
                  "strategy": "short explanation",
                  "packages": [
                    {
                      "id": "lowercase-id",
                      "title": "short role/title",
                      "instruction": "precise implementation assignment and completion condition",
                      "owned_paths": ["src/Feature/", "tests/FeatureTests.cs"],
                      "depends_on": []
                    }
                  ]
                }
                """;
    }

    public static AgentMeshPlan Parse(string response)
    {
        var jsonText = ExtractJson(response);
        var root = JsonNode.Parse(jsonText)?.AsObject()
                   ?? throw new InvalidOperationException(
                       "Agent Mesh planner returned an empty plan.");
        var strategy = root["strategy"]?.GetValue<string>()?.Trim() ?? string.Empty;
        if (strategy.Length is < 3 or > 1000)
        {
            throw new InvalidOperationException(
                "Agent Mesh strategy is missing or exceeds the safety limit.");
        }

        var packages = new List<AgentMeshWorkPackage>();
        foreach (var node in root["packages"]?.AsArray() ?? [])
        {
            if (node is not JsonObject item)
            {
                continue;
            }
            var id = item["id"]?.GetValue<string>()?.Trim() ?? string.Empty;
            var title = item["title"]?.GetValue<string>()?.Trim() ?? string.Empty;
            var instruction = item["instruction"]?.GetValue<string>()?.Trim() ?? string.Empty;
            var ownedPaths = ReadStrings(item["owned_paths"]);
            var dependencies = ReadStrings(item["depends_on"]);
            if (!IdPattern.IsMatch(id)
                || title.Length is < 2 or > 100
                || instruction.Length is < 20 or > 6000
                || ownedPaths.Count is < 1 or > 16)
            {
                throw new InvalidOperationException(
                    $"Agent Mesh package '{id}' is incomplete or exceeds a safety limit.");
            }
            packages.Add(new AgentMeshWorkPackage(
                id,
                title,
                instruction,
                ownedPaths.Select(NormalizeScope).ToArray(),
                dependencies));
        }
        if (packages.Count is < 2 or > 4)
        {
            throw new InvalidOperationException(
                "Agent Mesh requires two to four valid work packages.");
        }
        if (packages.Select(item => item.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != packages.Count)
        {
            throw new InvalidOperationException(
                "Agent Mesh work package IDs must be unique.");
        }

        var ids = packages.Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            if (package.DependsOn.Any(dependency =>
                    !ids.Contains(dependency)
                    || dependency.Equals(package.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Agent Mesh package '{package.Id}' has an unknown or self dependency.");
            }
        }
        EnsureExclusiveOwnership(packages);
        var plan = new AgentMeshPlan(strategy, packages);
        var waves = plan.BuildWaves();
        if (!waves.Any(wave => wave.Count >= 2))
        {
            throw new InvalidOperationException(
                "Agent Mesh plan has no parallel wave and would add cost without concurrency.");
        }
        return plan;
    }

    public static string Format(AgentMeshPlan plan)
    {
        var lines = new List<string>
        {
            $"Strategy: {plan.Strategy}",
            $"Packages: {plan.Packages.Count}",
            $"Waves: {plan.BuildWaves().Count}",
            string.Empty
        };
        foreach (var package in plan.Packages)
        {
            lines.Add(
                $"{package.Id} · {package.Title} · owns [{string.Join(", ", package.OwnedPaths)}] "
                + $"· depends [{string.Join(", ", package.DependsOn)}]");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyList<string> ReadStrings(JsonNode? node)
        => node?.AsArray()
               .Select(item => item?.GetValue<string>()?.Trim())
               .Where(item => !string.IsNullOrWhiteSpace(item))
               .Select(item => item!)
               .Distinct(StringComparer.OrdinalIgnoreCase)
               .ToArray()
           ?? [];

    private static string NormalizeScope(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope) || Path.IsPathRooted(scope))
        {
            throw new InvalidOperationException(
                "Agent Mesh owned paths must be workspace-relative.");
        }
        var normalized = scope.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }
        if (normalized.Length == 0
            || normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or "..")
            || normalized.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains('*')
            || normalized.Contains('?')
            || normalized.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("node_modules/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Agent Mesh owned path is unsafe or ambiguous: '{scope}'.");
        }
        return normalized;
    }

    private static void EnsureExclusiveOwnership(
        IReadOnlyList<AgentMeshWorkPackage> packages)
    {
        var owned = new List<(string PackageId, string Scope)>();
        foreach (var package in packages)
        {
            foreach (var scope in package.OwnedPaths)
            {
                foreach (var existing in owned)
                {
                    if (Overlaps(scope, existing.Scope))
                    {
                        throw new InvalidOperationException(
                            $"Agent Mesh ownership overlap: '{scope}' ({package.Id}) and "
                            + $"'{existing.Scope}' ({existing.PackageId}).");
                    }
                }
                owned.Add((package.Id, scope));
            }
        }
    }

    private static bool Overlaps(string left, string right)
    {
        if (left.Equals(right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (left.EndsWith("/", StringComparison.Ordinal)
            && right.StartsWith(left, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return right.EndsWith("/", StringComparison.Ordinal)
               && left.StartsWith(right, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractJson(string response)
    {
        response ??= string.Empty;
        var trimmed = response.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine >= 0 && lastFence > firstNewLine)
            {
                trimmed = trimmed[(firstNewLine + 1)..lastFence].Trim();
            }
        }
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException(
                "Agent Mesh planner did not return a JSON object.");
        }
        var json = trimmed[start..(end + 1)];
        if (json.Length > 50_000)
        {
            throw new InvalidOperationException(
                "Agent Mesh plan exceeds the 50 KB safety limit.");
        }
        try
        {
            JsonDocument.Parse(json);
            return json;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Agent Mesh plan is not valid JSON: {exception.Message}");
        }
    }
}
