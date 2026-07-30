using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using NovaDesktop.Models;

namespace NovaDesktop.Services;

public sealed class InputAttachmentService
{
    public const int MaximumAttachmentCount = 6;
    public const long MaximumImageBytes = 10 * 1024 * 1024;
    public const long MaximumTextBytes = 1024 * 1024;
    public const long MaximumTotalBytes = 20 * 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, string> ImageTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".webp"] = "image/webp"
        };

    private static readonly HashSet<string> TextExtensions =
    [
        ".txt", ".md", ".markdown", ".json", ".jsonc", ".xml", ".yaml", ".yml",
        ".toml", ".ini", ".cfg", ".conf", ".log", ".csv", ".tsv",
        ".cs", ".csproj", ".sln", ".props", ".targets",
        ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs",
        ".py", ".java", ".kt", ".kts", ".go", ".rs", ".cpp", ".cc", ".c",
        ".h", ".hpp", ".swift", ".php", ".rb", ".sh", ".ps1", ".bat", ".cmd",
        ".html", ".htm", ".css", ".scss", ".less", ".vue", ".svelte",
        ".sql", ".graphql", ".gql", ".wxml", ".wxss"
    ];

    private readonly string _root;

    public InputAttachmentService(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "attachments");
    }

    public IReadOnlyList<AgentInputAttachment> ValidateSelection(
        IEnumerable<string> paths,
        IReadOnlyCollection<AgentInputAttachment> existing)
    {
        var selected = existing.ToList();
        foreach (var rawPath in paths)
        {
            if (selected.Count >= MaximumAttachmentCount)
            {
                throw new InvalidOperationException($"一次最多添加 {MaximumAttachmentCount} 个附件。");
            }

            var path = Path.GetFullPath(rawPath);
            if (selected.Any(item =>
                    item.LocalPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new FileNotFoundException("附件不存在或已经移动。", path);
            }
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"不支持符号链接附件：{info.Name}");
            }

            var extension = info.Extension;
            AgentAttachmentKind kind;
            string mediaType;
            long maximumBytes;
            if (ImageTypes.TryGetValue(extension, out var imageType))
            {
                kind = AgentAttachmentKind.Image;
                mediaType = imageType;
                maximumBytes = MaximumImageBytes;
            }
            else if (TextExtensions.Contains(extension))
            {
                kind = AgentAttachmentKind.Text;
                mediaType = "text/plain";
                maximumBytes = MaximumTextBytes;
            }
            else
            {
                throw new InvalidOperationException(
                    $"暂不支持 {extension} 文件。当前可直接理解 PNG/JPEG/WebP 和常见文本、代码、配置文件。");
            }

            if (info.Length <= 0 || info.Length > maximumBytes)
            {
                var limit = maximumBytes / 1024 / 1024;
                throw new InvalidOperationException(
                    $"{info.Name} 大小不符合要求；{(kind == AgentAttachmentKind.Image ? "图片" : "文本文件")}上限为 {limit} MB。");
            }
            if (selected.Sum(item => item.SizeBytes) + info.Length > MaximumTotalBytes)
            {
                throw new InvalidOperationException("本轮附件总大小不能超过 20 MB。");
            }

            selected.Add(new AgentInputAttachment(
                Guid.NewGuid().ToString("N")[..12],
                info.Name,
                path,
                mediaType,
                kind,
                info.Length));
        }
        return selected;
    }

    public async Task<IReadOnlyList<AgentInputAttachment>> PersistAsync(
        string taskId,
        IReadOnlyList<AgentInputAttachment> attachments,
        CancellationToken cancellationToken)
    {
        if (attachments.Count == 0)
        {
            return [];
        }

        var safeTaskId = string.Concat(taskId.Where(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
        if (safeTaskId.Length == 0)
        {
            throw new InvalidOperationException("任务 ID 无法转换为安全附件目录。");
        }

        var taskDirectory = Path.Combine(_root, safeTaskId);
        Directory.CreateDirectory(taskDirectory);
        var persisted = new List<AgentInputAttachment>(attachments.Count);
        foreach (var attachment in attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safeName = string.Concat(attachment.FileName.Select(character =>
                char.IsAsciiLetterOrDigit(character)
                || character is '.' or '-' or '_' or ' '
                    ? character
                    : '_'));
            var destination = Path.Combine(
                taskDirectory,
                $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{attachment.Id}-{safeName}");
            await using var source = new FileStream(
                attachment.LocalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var target = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous);
            await source.CopyToAsync(target, cancellationToken);
            persisted.Add(attachment with { LocalPath = destination });
        }
        return persisted;
    }

    public static async Task<JsonArray> BuildOpenAiContentAsync(
        string prompt,
        IReadOnlyList<AgentInputAttachment>? attachments,
        CancellationToken cancellationToken)
    {
        var content = new JsonArray
        {
            new JsonObject { ["type"] = "input_text", ["text"] = prompt }
        };
        foreach (var attachment in attachments ?? [])
        {
            if (attachment.IsImage)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "input_image",
                    ["image_url"] = await BuildDataUrlAsync(attachment, cancellationToken)
                });
            }
            else
            {
                content.Add(new JsonObject
                {
                    ["type"] = "input_text",
                    ["text"] = await BuildTextBlockAsync(attachment, cancellationToken)
                });
            }
        }
        return content;
    }

    public static async Task<JsonNode> BuildChatContentAsync(
        string prompt,
        string provider,
        IReadOnlyList<AgentInputAttachment>? attachments,
        CancellationToken cancellationToken)
    {
        if (attachments is not { Count: > 0 })
        {
            return JsonValue.Create(prompt)!;
        }

        var content = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = prompt }
        };
        foreach (var attachment in attachments)
        {
            if (attachment.IsImage)
            {
                if (provider.Equals("deepseek", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "当前 DeepSeek 连接不支持 NOVA 的图片输入。请切换到 Kimi、OpenAI、Ollama 或支持视觉的兼容模型。");
                }
                content.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] = await BuildDataUrlAsync(attachment, cancellationToken)
                    }
                });
            }
            else
            {
                content.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = await BuildTextBlockAsync(attachment, cancellationToken)
                });
            }
        }
        return content;
    }

    private static async Task<string> BuildDataUrlAsync(
        AgentInputAttachment attachment,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(attachment.LocalPath, cancellationToken);
        return $"data:{attachment.MediaType};base64,{Convert.ToBase64String(bytes)}";
    }

    private static async Task<string> BuildTextBlockAsync(
        AgentInputAttachment attachment,
        CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(attachment.LocalPath, Encoding.UTF8, cancellationToken);
        if (text.IndexOf('\0') >= 0)
        {
            throw new InvalidOperationException($"{attachment.FileName} 不是可安全读取的文本文件。");
        }
        return $"[附件：{attachment.FileName}]\n{text}";
    }
}
