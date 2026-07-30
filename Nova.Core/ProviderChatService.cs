using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nova.Core;

public sealed class ProviderChatService
{
    private static readonly string ClientVersion = ResolveClientVersion();
    private readonly HttpClient _httpClient;

    public ProviderChatService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(3)
        };
    }

    public async Task<AgentChatResult> SendAsync(
        AgentChatRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            throw new InvalidOperationException("请先输入 API Key。Mac Preview 只在当前进程内存中使用它。");
        }
        if (request.Messages.Count == 0)
        {
            throw new InvalidOperationException("至少需要一条消息。");
        }

        var stopwatch = Stopwatch.StartNew();
        var text = request.Provider.Equals("deepseek", StringComparison.OrdinalIgnoreCase)
                   || request.Provider.Equals("kimi", StringComparison.OrdinalIgnoreCase)
            ? await SendChatCompletionsAsync(request, cancellationToken)
            : await SendOpenAiAsync(request, cancellationToken);
        stopwatch.Stop();
        return new AgentChatResult(
            text,
            request.Provider,
            request.Model,
            stopwatch.Elapsed);
    }

    private async Task<string> SendOpenAiAsync(
        AgentChatRequest request,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.openai.com/v1/responses");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
        message.Headers.UserAgent.ParseAdd($"NOVA-Mac/{ClientVersion}");
        var input = new JsonArray();
        input.Add(new JsonObject
        {
            ["role"] = "developer",
            ["content"] = BuildWorkspaceInstruction(request.Workspace)
        });
        foreach (var item in request.Messages)
        {
            input.Add(new JsonObject
            {
                ["role"] = item.Role,
                ["content"] = item.Content
            });
        }
        message.Content = JsonContent(new JsonObject
        {
            ["model"] = request.Model,
            ["input"] = input
        });
        using var response = await _httpClient.SendAsync(message, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, payload);
        var root = JsonNode.Parse(payload)?.AsObject()
                   ?? throw new InvalidOperationException("OpenAI 返回了无法解析的响应。");
        var outputText = root["output_text"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(outputText))
        {
            return outputText;
        }
        var pieces = root["output"]?.AsArray()
            .SelectMany(item => item?["content"]?.AsArray() ?? [])
            .Select(item => item?["text"]?.GetValue<string>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray() ?? [];
        return pieces.Length > 0
            ? string.Join(Environment.NewLine, pieces)
            : throw new InvalidOperationException("OpenAI 响应中没有可显示的文本。");
    }

    private async Task<string> SendChatCompletionsAsync(
        AgentChatRequest request,
        CancellationToken cancellationToken)
    {
        var isKimi = request.Provider.Equals("kimi", StringComparison.OrdinalIgnoreCase);
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            isKimi
                ? "https://api.moonshot.cn/v1/chat/completions"
                : "https://api.deepseek.com/chat/completions");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
        message.Headers.UserAgent.ParseAdd($"NOVA-Mac/{ClientVersion}");
        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = BuildWorkspaceInstruction(request.Workspace)
            }
        };
        foreach (var item in request.Messages)
        {
            messages.Add(new JsonObject
            {
                ["role"] = item.Role,
                ["content"] = item.Content
            });
        }
        message.Content = JsonContent(new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["stream"] = false
        });
        using var response = await _httpClient.SendAsync(message, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, payload);
        return JsonNode.Parse(payload)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
               ?? throw new InvalidOperationException("DeepSeek 响应中没有可显示的文本。");
    }

    private static string BuildWorkspaceInstruction(WorkspaceContext workspace)
        => "你是 NOVA Mac Preview。给出直接、可验证的回答，不要声称执行了尚未提供的工具。"
           + $"当前工作区：{workspace.Root}；类型：{workspace.Technology}；"
           + $"已识别文件约 {workspace.FileCount} 个；工程信号：{string.Join(", ", workspace.Signals)}。";

    private static StringContent JsonContent(JsonObject body)
        => new(
            body.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
            Encoding.UTF8,
            "application/json");

    private static void EnsureSuccess(HttpResponseMessage response, string payload)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        string detail;
        try
        {
            detail = JsonNode.Parse(payload)?["error"]?["message"]?.GetValue<string>()
                     ?? response.ReasonPhrase
                     ?? "未知错误";
        }
        catch (JsonException)
        {
            detail = response.ReasonPhrase ?? "未知错误";
        }
        throw new InvalidOperationException(
            $"模型请求失败（{(int)response.StatusCode}）：{detail}");
    }

    private static string ResolveClientVersion()
    {
        var version = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? "0.1.0-preview";
        var normalized = new string(version
            .TakeWhile(character => character is not '+' and not ' ')
            .Where(character => char.IsAsciiLetterOrDigit(character)
                                || character is '.' or '-')
            .ToArray());
        return string.IsNullOrWhiteSpace(normalized)
            ? "0.1.0-preview"
            : normalized;
    }
}
