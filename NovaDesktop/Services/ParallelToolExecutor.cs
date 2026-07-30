using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NovaDesktop.Services;

public sealed record AgentToolInvocation(
    string CallId,
    string Name,
    JsonObject Arguments);

public static class ParallelToolExecutor
{
    public static async Task<IReadOnlyDictionary<string, string>> ExecuteReadOnlyBatchAsync(
        WorkspaceToolHost tools,
        IReadOnlyList<AgentToolInvocation> calls,
        Func<AgentRuntimeEvent, Task> onEvent,
        Func<string, string> getAgent,
        Func<string, string> getLabel,
        Func<string, JsonObject, string> describe,
        CancellationToken cancellationToken)
    {
        if (calls.Count < 2 || calls.Any(call => tools.RequiresApproval(call.Name)))
        {
            throw new InvalidOperationException("Only multi-call read-only batches may execute in parallel.");
        }

        var degree = Math.Min(calls.Count, 4);
        await onEvent(new AgentRuntimeEvent(
            AgentRuntimeEventKind.ToolBatchStarted,
            "并行编排器",
            $"启动 {calls.Count} 个并行工具",
            $"最多 {degree} 个只读执行单元同时工作",
            0,
            degree));

        var outputs = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        using var gate = new SemaphoreSlim(degree, degree);
        var tasks = calls.Select(async call =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                await onEvent(new AgentRuntimeEvent(
                    AgentRuntimeEventKind.ToolRunning,
                    getAgent(call.Name),
                    $"并行执行 {getLabel(call.Name)}",
                    describe(call.Name, call.Arguments),
                    0,
                    degree));
                try
                {
                    var output = await tools.ExecuteAsync(call.Name, call.Arguments, cancellationToken);
                    outputs[call.CallId] = output;
                    await onEvent(new AgentRuntimeEvent(
                        AgentRuntimeEventKind.ToolCompleted,
                        getAgent(call.Name),
                        $"{getLabel(call.Name)} 完成",
                        output.Length <= 150 ? output : $"工具返回 {output.Length:N0} 个字符",
                        0,
                        degree));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    outputs[call.CallId] = JsonSerializer.Serialize(
                        new { status = "error", message = exception.Message });
                    await onEvent(new AgentRuntimeEvent(
                        AgentRuntimeEventKind.Message,
                        getAgent(call.Name),
                        $"{getLabel(call.Name)} 失败",
                        exception.Message,
                        0,
                        degree));
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        await onEvent(new AgentRuntimeEvent(
            AgentRuntimeEventKind.ToolBatchCompleted,
            "并行编排器",
            "并行批次完成",
            $"{calls.Count} 个只读结果已按调用 ID 汇总",
            0,
            1));
        return outputs;
    }
}
