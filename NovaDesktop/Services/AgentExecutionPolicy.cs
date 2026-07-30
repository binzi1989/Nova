using System.Text.Json.Nodes;
using NovaDesktop.Models;

namespace NovaDesktop.Services;

public static class AgentExecutionPolicy
{
    private static readonly HashSet<string> MutatingTools = new(StringComparer.Ordinal)
    {
        "write_text_file",
        "replace_text_in_file",
        "run_workspace_command",
        "inspect_mcp_server_tools",
        "activate_desktop_window",
        "open_browser_url",
        "type_text_to_window",
        "send_window_key",
        "call_mcp_tool",
        "index_workspace_knowledge",
        "schedule_agent_task",
        "disable_scheduled_task"
    };

    public static bool CanMutateWorkspace(AgentExecutionMode mode)
        => mode is AgentExecutionMode.Build
            or AgentExecutionMode.Autopilot
            or AgentExecutionMode.Goal;

    public static IReadOnlyList<JsonObject> FilterTools(
        IReadOnlyList<JsonObject> definitions,
        AgentExecutionMode mode)
    {
        if (CanMutateWorkspace(mode))
        {
            return definitions;
        }

        return definitions
            .Where(definition =>
            {
                var name = definition["name"]?.GetValue<string>() ?? string.Empty;
                return !MutatingTools.Contains(name);
            })
            .ToArray();
    }

    public static string GetSystemContract(AgentExecutionMode mode)
        => mode switch
        {
            AgentExecutionMode.Ask =>
                "ASK MODE: answer from read-only evidence. Do not modify files, run commands, control apps, schedule work, or claim implementation.",
            AgentExecutionMode.Plan =>
                "PLAN MODE: inspect read-only context and produce an executable plan with risks, files, validation, and approval points. Do not modify the workspace.",
            AgentExecutionMode.Build =>
                "BUILD MODE: implement the goal in the workspace, request approval for changes, and verify the result before claiming completion.",
            AgentExecutionMode.Autopilot =>
                "AUTOPILOT MODE: drive a sufficiently specified task through planning, delegated analysis, implementation, verification, review, and recovery while honoring every approval boundary.",
            _ =>
                "GOAL MODE: the user supplies a desired outcome rather than a complete specification. Explore read-only evidence, freeze an observable Mission Charter, investigate unknowns, select the shortest provable strategy, and drive execution to the result. Do not ask preference questions; interrupt only for authority, material cost, external state, irreversible risk, or conflicting goals. Never replace evidence with confident assumptions."
        };
}
