using System.Text;
using System.Text.Json.Nodes;

namespace Nova.Maimai.Connector;

internal static class SmokeTests
{
    public static async Task<int> RunAsync(string? requestedRoot)
    {
        var root = requestedRoot ?? Path.Combine(
            Path.GetTempPath(), $"nova-maimai-smoke-{Guid.NewGuid():N}");
        root = Path.GetFullPath(root);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new InvalidOperationException("冒烟测试目录必须为空，防止覆盖现有数据。");
        }
        Directory.CreateDirectory(root);

        try
        {
            var vault = new EncryptedVault(root);
            var profiles = new ProfileService(vault);
            var nativeHost = new NativeHost(profiles);
            var mcp = new McpServer(profiles);
            var sample = new CaptureInput
            {
                SourceType = "person-profile",
                SourceUrl = "https://maimai.cn/profile/demo?tracking=removed#top",
                PageTitle = "张三 - 脉脉",
                DisplayName = "张三",
                CurrentTitle = "AI 产品负责人",
                CurrentCompany = "示例科技",
                Location = "上海",
                PublicSummary = "企业智能体产品；邮箱 zhangsan@example.com",
                VisibleSkills = ["AgentOS", "企业软件"],
                EvidenceText = "张三 AI 产品负责人 13800138000",
                Purpose = "经本人主动筛选的商务人才研究",
                Notes = "仅人工联系",
                RetentionDays = 90
            };

            var first = profiles.Capture(sample);
            sample.CurrentTitle = "智能体产品总监";
            var second = profiles.Capture(sample);
            Assert(first.Id == second.Id, "同一 URL 去重 ID 不稳定");
            Assert(profiles.List().Count == 1, "重复采集产生了重复记录");
            Assert(second.EvidenceText.Contains("[手机号已剔除]"), "手机号未剔除");
            Assert(second.PublicSummary.Contains("[邮箱已剔除]"), "邮箱未剔除");

            var rawVault = File.ReadAllBytes(vault.VaultPath);
            var rawKey = File.ReadAllBytes(vault.KeyPath);
            Assert(!Encoding.UTF8.GetString(rawVault).Contains("张三", StringComparison.Ordinal),
                "数据仓出现明文姓名");
            Assert(!Encoding.UTF8.GetString(rawVault).Contains("maimai.cn", StringComparison.OrdinalIgnoreCase),
                "数据仓出现明文 URL");
            Assert(rawKey.Length > 32 && !rawKey.AsSpan().SequenceEqual(new byte[rawKey.Length]),
                "DPAPI 密钥文件无效");
            Assert(profiles.Search("示例科技 AgentOS").Count == 1, "组合检索失败");

            var updated = profiles.RecordContactStatus(first.Id, "shortlisted", "等待人工确认");
            Assert(updated.ContactStatus == "shortlisted", "状态写入失败");

            var ping = nativeHost.Handle(new JsonObject { ["action"] = "ping" });
            Assert(ping["ok"]?.GetValue<bool>() == true, "Native Host ping 失败");
            await using var framed = new MemoryStream();
            await NativeHost.WriteMessageAsync(framed, ping, CancellationToken.None);
            framed.Position = 0;
            var roundTrip = await NativeHost.ReadMessageAsync(framed, CancellationToken.None);
            Assert(roundTrip?["version"]?.GetValue<string>() == "1.0.0", "Native Messaging 帧往返失败");

            var initialize = mcp.Handle(Request(1, "initialize", new JsonObject
            {
                ["protocolVersion"] = McpServer.ProtocolVersion
            }));
            Assert(initialize?["result"]?["protocolVersion"]?.GetValue<string>() == McpServer.ProtocolVersion,
                "MCP initialize 失败");
            var tools = mcp.Handle(Request(2, "tools/list", new JsonObject()));
            Assert(tools?["result"]?["tools"]?.AsArray().Count == 7, "MCP 工具数量不符");
            var search = mcp.Handle(Request(3, "tools/call", new JsonObject
            {
                ["name"] = "maimai_search_profiles",
                ["arguments"] = new JsonObject { ["query"] = "产品总监" }
            }));
            Assert(search?["result"]?["isError"]?.GetValue<bool>() == false, "MCP 工具调用失败");
            Assert(search?["result"]?["content"]?[0]?["text"]?.GetValue<string>()?.Contains(first.Id) == true,
                "MCP 检索未返回目标档案");

            var expired = new CaptureInput
            {
                SourceUrl = "https://maimai.cn/profile/expired",
                DisplayName = "过期样本",
                Purpose = "测试自动清理",
                RetentionDays = 1
            };
            profiles.Capture(expired, DateTimeOffset.UtcNow.AddDays(-2));
            Assert(profiles.PurgeExpired() == 1, "过期清理失败");
            Assert(profiles.Delete(first.Id), "删除档案失败");
            Assert(profiles.List().Count == 0, "删除后仍存在档案");

            Console.WriteLine(new JsonObject
            {
                ["ok"] = true,
                ["tests"] = 16,
                ["mcpTools"] = 7,
                ["encryption"] = "AES-256-GCM + Windows DPAPI CurrentUser",
                ["nativeMessaging"] = "framing-round-trip-passed",
                ["vaultPlaintextLeak"] = false
            }.ToJsonString());
            return 0;
        }
        finally
        {
            if (requestedRoot is null && Directory.Exists(root)
                && Path.GetFileName(root).StartsWith("nova-maimai-smoke-", StringComparison.Ordinal))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static JsonObject Request(int id, string method, JsonObject parameters) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = method,
        ["params"] = parameters
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"SMOKE FAILED: {message}");
        }
    }
}
