using System.Text.Json;
using System.Text.Json.Nodes;
using NovaDesktop.Services;

if (args.Length == 1 && args[0] == "--installed-mcp")
{
    var registration = new McpServerRegistration(
        "nova-maimai",
        "Nova.Maimai.Connector.exe",
        ["mcp"],
        null,
        true,
        new Dictionary<string, string>());
    await using var client = await McpStdioClient.ConnectAsync(
        registration,
        Directory.GetCurrentDirectory(),
        CancellationToken.None);
    var tools = await client.ListToolsAsync(CancellationToken.None);
    var stats = await client.CallToolAsync("maimai_stats", new JsonObject(), CancellationToken.None);
    var toolCount = tools["tools"]?.AsArray().Count ?? 0;
    if (toolCount != 7 || stats["isError"]?.GetValue<bool>() != false)
    {
        throw new InvalidOperationException("Installed MCP did not satisfy NOVA client contract.");
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = true,
        novaClient = "McpStdioClient",
        commandResolution = "PATH",
        initialize = true,
        tools = toolCount,
        statsCall = true
    }));
    return 0;
}

if (args.Length != 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: Nova.Maimai.Validation <agent-pack-directory>");
    return 2;
}

var sandbox = Path.Combine(Path.GetTempPath(), "nova-maimai-pack-validation-" + Guid.NewGuid().ToString("N"));
var installed = Path.Combine(sandbox, "installed");
var state = Path.Combine(sandbox, "state.json");
Directory.CreateDirectory(sandbox);
try
{
    var service = new AgentPackService([], state, installed);
    var summary = await service.InstallFromDirectoryAsync(Path.GetFullPath(args[0]));
    await service.SetEnabledAsync(summary.Id, true);
    var details = service.Get(summary.Id);
    if (details.Workflows.Count != 1 || details.Workflows[0].Steps.Count != 4)
    {
        throw new InvalidOperationException("Agent Pack workflow contract did not load as 1 workflow / 4 steps.");
    }
    if (details.CapabilityRequirements?.Items.Count != 1
        || details.CapabilityRequirements.Items[0].Kind != "mcp"
        || !details.CapabilityRequirements.Items[0].MatchIds.Contains("nova-maimai"))
    {
        throw new InvalidOperationException("Agent Pack MCP requirement did not load correctly.");
    }
    if (!details.Summary.Enabled || details.Summary.AgentCount != 3)
    {
        throw new InvalidOperationException("Agent Pack enablement or roster validation failed.");
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = true,
        id = details.Summary.Id,
        enabled = details.Summary.Enabled,
        agents = details.Summary.AgentCount,
        workflows = details.Workflows.Count,
        steps = details.Workflows[0].Steps.Count,
        requiredMcp = details.CapabilityRequirements.Items[0].MatchIds[0]
    }));
    return 0;
}
finally
{
    if (Directory.Exists(sandbox)
        && Path.GetFileName(sandbox).StartsWith("nova-maimai-pack-validation-", StringComparison.Ordinal))
    {
        Directory.Delete(sandbox, recursive: true);
    }
}
