using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NovaDesktop.Services;

public sealed class ParallelAgentOrchestrator
{
    private readonly HttpClient _httpClient;
    private readonly Func<ParallelAgentTask, int, CancellationToken, Task<string>>? _workerRunner;

    public ParallelAgentOrchestrator(
        HttpClient httpClient,
        Func<ParallelAgentTask, int, CancellationToken, Task<string>>? workerRunner = null)
    {
        _httpClient = httpClient;
        _workerRunner = workerRunner;
    }

    public async Task<string> ExecuteAsync(
        AgentRunRequest request,
        JsonObject arguments,
        Func<AgentRuntimeEvent, Task> onEvent,
        CancellationToken cancellationToken)
    {
        var tasks = ParseTasks(arguments);
        await onEvent(new AgentRuntimeEvent(
            AgentRuntimeEventKind.BatchStarted,
            "多 Agent 编排器",
            $"启动 {tasks.Count} 个模型工作者",
            $"使用 {request.Model} 并行处理独立子目标",
            0,
            tasks.Count));

        var workerTasks = tasks.Select(async (task, index) =>
        {
            await onEvent(new AgentRuntimeEvent(
                AgentRuntimeEventKind.Thinking,
                $"子 Agent {index + 1}",
                task.Title,
                "独立分析中",
                0,
                tasks.Count)
            {
                ModelRoundCost = 1
            });
            try
            {
                var answer = _workerRunner is not null
                    ? await _workerRunner(task, index, cancellationToken)
                    : request.Provider.Equals("openai", StringComparison.OrdinalIgnoreCase)
                        ? await RunOpenAiWorkerAsync(request, task, cancellationToken)
                        : await RunChatWorkerAsync(request, task, cancellationToken);
                await onEvent(new AgentRuntimeEvent(
                    AgentRuntimeEventKind.ToolCompleted,
                    $"子 Agent {index + 1}",
                    $"{task.Title} 完成",
                    $"{answer.Length:N0} 个字符已交给指挥官\n\n阶段产出：{Preview(answer)}",
                    0,
                    tasks.Count));
                return new WorkerResult(index, task.Title, "completed", answer);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await onEvent(new AgentRuntimeEvent(
                    AgentRuntimeEventKind.Message,
                    $"子 Agent {index + 1}",
                    $"{task.Title} 失败",
                    exception.Message,
                    0,
                    tasks.Count));
                return new WorkerResult(index, task.Title, "failed", exception.Message);
            }
        });

        var results = await Task.WhenAll(workerTasks);
        await onEvent(new AgentRuntimeEvent(
            AgentRuntimeEventKind.BatchCompleted,
            "多 Agent 编排器",
            "工作组结果已汇总",
            $"{results.Count(result => result.Status == "completed")}/{results.Length} 个子 Agent 成功",
            0,
            1));
        return JsonSerializer.Serialize(new
        {
            provider = request.Provider,
            model = request.Model,
            worker_count = results.Length,
            results = results.OrderBy(result => result.Index).Select(result => new
            {
                result.Title,
                result.Status,
                output = result.Output
            })
        });
    }

    private static string Preview(string value)
    {
        var compact = string.Join(
            " ",
            value.Split(
                ['\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return compact.Length <= 520 ? compact : compact[..520] + "…";
    }

    private async Task<string> RunOpenAiWorkerAsync(
        AgentRunRequest request,
        ParallelAgentTask task,
        CancellationToken cancellationToken)
    {
        var requestBody = new JsonObject
        {
            ["model"] = request.Model,
            ["instructions"] = WorkerInstructions(request.WorkspaceRoot),
            ["input"] = task.Instruction,
            ["reasoning"] = new JsonObject { ["effort"] = "low" },
            ["store"] = false
        };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
        httpRequest.Headers.UserAgent.ParseAdd($"NOVA-Desktop/{NovaProductVersion.Current}");
        httpRequest.Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI worker API {(int)response.StatusCode}: {responseText}");
        }

        var json = JsonNode.Parse(responseText)?.AsObject()
                   ?? throw new InvalidOperationException("OpenAI worker returned an empty response.");
        var builder = new StringBuilder();
        foreach (var item in json["output"]?.AsArray() ?? [])
        {
            if (item is not JsonObject output || output["type"]?.GetValue<string>() != "message")
            {
                continue;
            }
            foreach (var content in output["content"]?.AsArray() ?? [])
            {
                if (content is JsonObject block && block["type"]?.GetValue<string>() == "output_text")
                {
                    builder.Append(block["text"]?.GetValue<string>());
                }
            }
        }
        return builder.Length == 0 ? "Worker returned no displayable text." : builder.ToString();
    }

    private async Task<string> RunChatWorkerAsync(
        AgentRunRequest request,
        ParallelAgentTask task,
        CancellationToken cancellationToken)
    {
        var requestBody = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = WorkerInstructions(request.WorkspaceRoot) },
                new JsonObject { ["role"] = "user", ["content"] = task.Instruction }
            },
            ["thinking"] = new JsonObject { ["type"] = "enabled", ["reasoning_effort"] = "high" },
            ["stream"] = false,
            ["max_tokens"] = 8192
        };
        var isKimi = request.Provider.Equals("kimi", StringComparison.OrdinalIgnoreCase);
        var isCompatible = request.Provider.Equals("custom", StringComparison.OrdinalIgnoreCase)
                           || request.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase);
        var providerLabel = request.Provider.ToLowerInvariant() switch
        {
            "kimi" => "Kimi",
            "ollama" => "Ollama",
            "custom" => "兼容模型",
            _ => "DeepSeek"
        };
        var endpoint = isCompatible
            ? request.Endpoint
            : isKimi
                ? "https://api.moonshot.cn/v1/chat/completions"
                : "https://api.deepseek.com/chat/completions";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
            || endpointUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"{providerLabel} 缺少有效的 HTTP(S) 接口。");
        }
        var isNativeOllama = request.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase)
                             && endpointUri.AbsolutePath.EndsWith("/api/chat", StringComparison.OrdinalIgnoreCase);
        if (isKimi)
        {
            requestBody.Remove("max_tokens");
            requestBody["max_completion_tokens"] = 8192;
        }
        else if (isCompatible)
        {
            requestBody.Remove("thinking");
            if (isNativeOllama)
            {
                requestBody.Remove("max_tokens");
                requestBody["options"] = new JsonObject
                {
                    ["num_predict"] = 4096,
                    ["num_ctx"] = 16384
                };
            }
        }
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpointUri);
        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
        }
        httpRequest.Headers.UserAgent.ParseAdd($"NOVA-Desktop/{NovaProductVersion.Current}");
        httpRequest.Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"{providerLabel} worker API {(int)response.StatusCode}: {responseText}");
        }

        var json = JsonNode.Parse(responseText);
        return (isNativeOllama
                   ? json?["message"]?["content"]?.GetValue<string>()
                   : json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>())
               ?? "Worker returned no displayable text.";
    }

    private static IReadOnlyList<ParallelAgentTask> ParseTasks(JsonObject arguments)
    {
        var tasks = new List<ParallelAgentTask>();
        foreach (var node in arguments["tasks"]?.AsArray() ?? [])
        {
            if (node is not JsonObject item)
            {
                continue;
            }
            var title = item["title"]?.GetValue<string>()?.Trim() ?? string.Empty;
            var instruction = item["instruction"]?.GetValue<string>()?.Trim() ?? string.Empty;
            if (title.Length == 0 || instruction.Length == 0)
            {
                continue;
            }
            if (title.Length > 80 || instruction.Length > 8000)
            {
                throw new InvalidOperationException("Parallel worker title or instruction exceeds the safety limit.");
            }
            tasks.Add(new ParallelAgentTask(title, instruction));
        }
        if (tasks.Count is < 2 or > 4)
        {
            throw new InvalidOperationException("Parallel delegation requires two to four valid tasks.");
        }
        return tasks;
    }

    private static string WorkerInstructions(string workspaceRoot)
        => $"""
           You are one specialist worker inside NOVA, a native Windows agent.
           Workspace context: {workspaceRoot}
           Complete only the assigned subtask. You have no tools and must not claim to have inspected files or performed
           external actions. Make assumptions explicit. Return a concise, structured analysis for the parent agent to merge.
           Respond in Chinese unless the subtask explicitly requests another language.
           """;

    private sealed record WorkerResult(int Index, string Title, string Status, string Output);
}
