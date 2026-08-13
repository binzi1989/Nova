using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NovaDesktop.Services;

/// <summary>
/// Migrates the short-lived workshop draft format used by early Electron builds
/// into the loader-native Agent Pack contract. The source remains untouched.
/// </summary>
internal static class LegacyAgentPackConverter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static bool CanConvert(string root)
    {
        var manifestPath = Path.Combine(root, "manifest.json");
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 128 * 1024)
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var manifest = document.RootElement;
            return String(manifest, "schema").StartsWith("nova.agent-pack.manifest/", StringComparison.OrdinalIgnoreCase)
                   && !string.IsNullOrWhiteSpace(String(manifest, "packId"))
                   && !string.IsNullOrWhiteSpace(String(manifest, "name"));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static async Task ConvertAsync(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        if (!CanConvert(sourceRoot))
        {
            throw new InvalidOperationException("所选目录既不是标准 Agent Pack，也不是可迁移的旧版工坊 Pack。");
        }

        Directory.CreateDirectory(destinationRoot);
        using var manifestDocument = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(sourceRoot, "manifest.json"), cancellationToken));
        var legacyManifest = manifestDocument.RootElement;
        var contract = await ReadObjectAsync(sourceRoot, LegacyFile(legacyManifest, "contract", "契约.json"), cancellationToken);
        var workflow = await ReadObjectAsync(sourceRoot, LegacyFile(legacyManifest, "workflow", "工作流.json"), cancellationToken);

        var id = SafeId(String(legacyManifest, "packId"));
        var name = Value(legacyManifest, "name", "迁移的专业 Agent");
        var version = Value(legacyManifest, "version", "0.1.0");
        var category = Value(legacyManifest, "category", "专业 Agent");
        var description = Value(legacyManifest, "description", "由旧版 NOVA Agent 工坊迁移。 ");
        var objective = Value(legacyManifest, "objective", "完成用户确认的目标并生成可检查的交付物");
        var primaryArtifact = Value(legacyManifest, "primaryArtifact", "结果交付.md");
        var scenarioProfile = Value(legacyManifest, "scenarioProfile", "research");
        var autonomyLevel = Value(legacyManifest, "autonomyLevel", "assist");
        var requiredInputs = Strings(contract, "requiredInputs");
        var recommendedInputs = Strings(contract, "recommendedInputs");
        var roles = Roles(contract);
        var steps = Steps(workflow, roles, primaryArtifact);
        var starterPrompts = BuildStarterPrompts(objective, name);

        Directory.CreateDirectory(Path.Combine(destinationRoot, "agents"));
        Directory.CreateDirectory(Path.Combine(destinationRoot, "workflows"));
        Directory.CreateDirectory(Path.Combine(destinationRoot, "delivery-templates"));
        Directory.CreateDirectory(Path.Combine(destinationRoot, "knowledge"));
        Directory.CreateDirectory(Path.Combine(destinationRoot, "evaluations"));

        var onboardingSteps = new List<object>
        {
            new
            {
                id = "goal", title = "你最终想拿到什么结果？", description = "直接描述结果，不必使用专业术语。",
                kind = "text", required = true, placeholder = objective, options = Array.Empty<string>(),
                whyItMatters = "目标决定工作流和最终验收条件。", example = objective
            }
        };
        foreach (var (input, index) in requiredInputs.Take(7).Select((value, index) => (value, index)))
        {
            var attachment = LooksLikeAttachment(input);
            onboardingSteps.Add(new
            {
                id = $"required-{index + 1}", title = input, description = "提供现有信息即可；没有时可以明确写未知。",
                kind = attachment ? "attachment" : "text", required = true,
                placeholder = attachment ? $"选择文件：{input}" : $"填写：{input}", options = Array.Empty<string>(),
                whyItMatters = "这是形成可靠结果所需的核心输入。", example = "资料不足时，Agent 会降低结论级别而不是编造。"
            });
        }

        var nativeManifest = new
        {
            manifestVersion = "1.0",
            id,
            name,
            version,
            status = "incubating",
            category,
            description,
            novaCompatibility = ">=1.0.4 <2.0.0",
            creationStandard = new
            {
                version = "1.0",
                scenarioProfile,
                autonomyLevel,
                lifecycle = Value(legacyManifest, "lifecycle", "single-run"),
                collaborationMode = Value(legacyManifest, "collaborationMode", "independent"),
                deliveryMode = Value(legacyManifest, "deliveryMode", "document"),
                decisionStyle = Value(legacyManifest, "decisionStyle", "balanced")
            },
            inputContract = new { required = requiredInputs, recommended = recommendedInputs, missingDataPolicy = "preserve-unknowns-and-lower-confidence" },
            outputContract = new { schema = "nova.delivery/1.0", declaration = "delivery-contract.json", primaryArtifact, mustPersist = true, evidenceRequired = true, proofOfDoneRequired = true },
            declaredCapabilities = new[] { scenarioProfile, "legacy-workshop-migrated", "proof-of-done" },
            permissions = Array.Empty<string>(),
            starterPrompts,
            onboarding = new
            {
                version = "1.0",
                headline = $"开始使用 {name}",
                description = "从现有资料开始，缺失内容会被保留为未知项。",
                steps = onboardingSteps,
                outcomes = new[]
                {
                    new { id = "start", title = "开始完成目标", description = "执行标准工作流并生成真实交付物。", promptTemplate = "目标：{{goal}}。请先检查输入完整性，再执行工作流。" }
                }
            },
            externalActions = new { publishing = "approval-required", accountAccess = "approval-required", purchasing = "not-supported", desktopControl = "approval-required" },
            entryWorkflow = "workflows/entry-workflow.json",
            evaluationSuite = "evaluations/standard-cases.json",
            migration = new { sourceSchema = String(legacyManifest, "schema"), migratedAt = DateTimeOffset.UtcNow }
        };

        var agentCard = new
        {
            cardVersion = "1.0", id, name, version, objective, intents = starterPrompts,
            inputSchema = new { required = requiredInputs, recommended = recommendedInputs },
            outputSchema = new { artifacts = new[] { primaryArtifact, "proof-of-done.json" } },
            trust = new { level = "local-migrated", signed = false },
            interoperability = new { taskContract = "1.0", artifactContract = "1.0", a2aAdapter = false }
        };
        var nativeWorkflow = new
        {
            id = "entry-workflow", name = $"{name}标准工作流",
            executionMode = autonomyLevel.Equals("goal-autonomous", StringComparison.OrdinalIgnoreCase) ? "Goal" : "Build",
            resultContract = "delivery-templates/result.md",
            steps
        };
        var deliveryContract = new
        {
            schema = "nova.delivery/1.0", primaryArtifact,
            requiredSections = new[] { "outcome", "artifacts", "evidence", "incomplete", "nextActions" },
            artifactManifest = new { required = true, source = "runtime-observed-files", fields = new[] { "title", "path", "kind", "size", "modifiedAt" } },
            evidence = new { required = true, proofOfDone = "proof-of-done.json", distinguishFactsInferencesUnknowns = true },
            review = new { requiredBeforeFinalAcceptance = true, scopes = new[] { "delivery", "artifact" }, actions = new[] { "accept", "request-changes", "calibrate-agent" }, repairInSameTask = true },
            emptyArtifactPolicy = "partial-not-complete"
        };
        var evaluations = new
        {
            suiteVersion = "1.0", standard = "1.0",
            cases = new object[]
            {
                new { id = "canonical", name = "标准输入", input = starterPrompts[0], expectedBehavior = "生成主交付物与完成证据", assertions = new[] { $"存在 {primaryArtifact}", "存在 proof-of-done.json" }, mustNot = new[] { "只返回聊天文字", "伪造完成" } },
                new { id = "missing-input", name = "资料缺失", input = objective, expectedBehavior = "保留未知项并降低结论级别", assertions = new[] { "明确资料缺口", "没有编造事实" }, mustNot = new[] { "虚构输入" } },
                new { id = "correction", name = "中途纠正", input = "修改一个约束后继续。", expectedBehavior = "只重做受影响步骤", assertions = new[] { "保留已有成果" }, mustNot = new[] { "丢失上下文" } },
                new { id = "permission-denied", name = "拒绝授权", input = "拒绝外部写入。", expectedBehavior = "安全停止并保留本地结果", assertions = new[] { "没有绕过授权" }, mustNot = new[] { "伪称执行成功" } },
                new { id = "resume", name = "中断恢复", input = "从检查点继续。", expectedBehavior = "不重复已完成步骤", assertions = new[] { "证据链连续" }, mustNot = new[] { "重复副作用" } }
            },
            releaseThreshold = new { requiredPassRate = 1.0, falseCompletionAllowed = false }
        };
        var certification = new
        {
            standardVersion = "1.0", level = "Runnable", score = 100,
            checks = new[]
            {
                new { id = "native-contract", name = "已迁移为原生 Agent Pack 契约", passed = true, detail = "标准身份、工作流、交付和评测文件齐备" },
                new { id = "source-preserved", name = "旧版源目录保持不变", passed = true, detail = "迁移只生成安装副本" }
            },
            nextActions = new[] { "使用真实案例试运行后再提升稳定等级" }
        };

        await WriteJsonAsync(Path.Combine(destinationRoot, "nova.industry.json"), nativeManifest, cancellationToken);
        await WriteJsonAsync(Path.Combine(destinationRoot, "agent-card.json"), agentCard, cancellationToken);
        await WriteJsonAsync(Path.Combine(destinationRoot, "delivery-contract.json"), deliveryContract, cancellationToken);
        await WriteJsonAsync(Path.Combine(destinationRoot, "certification.json"), certification, cancellationToken);
        await WriteJsonAsync(Path.Combine(destinationRoot, "workflows", "entry-workflow.json"), nativeWorkflow, cancellationToken);
        await WriteJsonAsync(Path.Combine(destinationRoot, "evaluations", "standard-cases.json"), evaluations, cancellationToken);
        await WriteJsonAsync(Path.Combine(destinationRoot, "evaluations", "contract-dry-run.json"), new { version = "1.0", verdict = "legacy-migrated", primaryArtifact, steps = steps.Length }, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(destinationRoot, "INDUSTRY_CHARTER.md"), Charter(name, objective, contract), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(destinationRoot, "agents", "AGENT_ROSTER.md"), Roster(roles), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(destinationRoot, "delivery-templates", "result.md"), DeliveryTemplate(name, primaryArtifact), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(destinationRoot, "knowledge", "README.md"), "# 知识边界\n\n只使用用户提供或可追溯的资料；未知项不得虚构。\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(destinationRoot, "README.md"), $"# {name}\n\n此 Agent Pack 已由 NOVA 从旧版工坊格式迁移为原生 1.0 契约。\n", cancellationToken);
    }

    private static async Task<JsonElement> ReadObjectAsync(string root, string relativePath, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
        return document.RootElement.Clone();
    }

    private static string LegacyFile(JsonElement manifest, string property, string fallback)
        => manifest.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Object
           && files.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static object[] Roles(JsonElement contract)
    {
        if (!contract.TryGetProperty("roles", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return new object[] { new { id = "specialist", name = "专业执行 Agent", responsibility = "完成用户目标", deliverables = new[] { "结果交付.md" } }, new { id = "reviewer", name = "独立审查 Agent", responsibility = "验证交付物", deliverables = new[] { "proof-of-done.json" } } };
        }
        return values.EnumerateArray().Take(12).Select((role, index) => new
        {
            id = SafeRoleId(Value(role, "id", $"role-{index + 1}")),
            name = Value(role, "name", $"专业角色 {index + 1}"),
            responsibility = Value(role, "responsibility", "按契约完成本角色工作"),
            deliverables = Strings(role, "deliverables").DefaultIfEmpty("阶段成果.md").ToArray()
        }).Cast<object>().ToArray();
    }

    private static object[] Steps(JsonElement workflow, object[] roles, string primaryArtifact)
    {
        var roleIds = roles.Select(role => role.GetType().GetProperty("id")?.GetValue(role)?.ToString() ?? "specialist").ToArray();
        if (!workflow.TryGetProperty("entryWorkflow", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return new object[]
            {
                new { id = "step-1", agent = roleIds[0], title = "完成目标", outputs = new[] { primaryArtifact }, acceptance = new[] { "交付物已真实生成", "结果可以打开检查" } },
                new { id = "step-2", agent = roleIds[^1], title = "独立验证", outputs = new[] { "proof-of-done.json" }, acceptance = new[] { "证据、未知项和未完成边界已记录", "未以声明代替验证" } }
            };
        }
        var result = values.EnumerateArray().Take(24).Select((step, index) =>
        {
            var owner = SafeRoleId(Value(step, "owner", roleIds[Math.Min(index, roleIds.Length - 1)]));
            if (!roleIds.Contains(owner, StringComparer.OrdinalIgnoreCase)) owner = roleIds[Math.Min(index, roleIds.Length - 1)];
            return new
            {
                id = $"step-{index + 1}", agent = owner,
                title = Value(step, "title", $"执行步骤 {index + 1}"),
                outputs = new[] { Value(step, "output", index == 0 ? primaryArtifact : $"阶段成果-{index + 1}.md") },
                acceptance = Strings(step, "acceptance").DefaultIfEmpty("结果可以直接检查").Take(8).ToArray()
            };
        }).Cast<object>().ToList();
        if (result.Count == 0) return Steps(JsonDocument.Parse("{}").RootElement, roles, primaryArtifact);
        return result.ToArray();
    }

    private static string[] Strings(JsonElement element, string name)
        => element.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()?.Trim()).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().Take(32).ToArray()
            : Array.Empty<string>();

    private static string[] BuildStarterPrompts(string objective, string name)
        => new[] { $"请使用{name}完成这个目标：{objective}。先检查资料是否齐全，再生成可检查的交付物。", $"请先告诉我完成{objective}最值得补充的资料，然后从现有资料开始。" };

    private static string Charter(string name, string objective, JsonElement contract)
    {
        var boundaries = Strings(contract, "boundaries");
        return $"# {name}\n\n## 目标\n\n{objective}\n\n## 工作边界\n\n{string.Join("\n", boundaries.Select(value => "- " + value))}\n\n## 完成标准\n\n必须生成真实交付物和 proof-of-done.json，不以聊天声明代替完成。\n";
    }

    private static string Roster(object[] roles)
    {
        var builder = new StringBuilder("# Agent Roster\n\n| Agent | 名称 | 职责 |\n|---|---|---|\n");
        foreach (var role in roles)
        {
            var type = role.GetType();
            builder.Append('|').Append(type.GetProperty("id")?.GetValue(role)).Append('|')
                .Append(type.GetProperty("name")?.GetValue(role)).Append('|')
                .Append(type.GetProperty("responsibility")?.GetValue(role)?.ToString()?.Replace('|', ' ')).AppendLine("|");
        }
        return builder.ToString();
    }

    private static string DeliveryTemplate(string name, string primaryArtifact)
        => $"# {name}交付结果\n\n## 结论\n\n## 已确认事实\n\n## 推断与判断\n\n## 未知项与风险\n\n## 交付物与验证\n\n- 主交付物：{primaryArtifact}\n- 完成证据：proof-of-done.json\n";

    private static bool LooksLikeAttachment(string text)
        => new[] { "文件", "图片", "文档", "表格", "数据", "附件", ".xlsx", ".csv", ".pdf" }.Any(text.Contains);

    private static string SafeId(string value)
        => Regex.IsMatch(value, "^nova\\.[a-z0-9][a-z0-9.-]{4,95}$", RegexOptions.IgnoreCase)
            ? value.ToLowerInvariant()
            : $"nova.user.migrated-{Guid.NewGuid():N}";

    private static string SafeRoleId(string value)
    {
        var normalized = Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9-]", "-").Trim('-');
        return Regex.IsMatch(normalized, "^[a-z][a-z0-9-]{1,39}$") ? normalized : $"role-{Guid.NewGuid():N}"[..13];
    }

    private static string String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? string.Empty : string.Empty;

    private static string Value(JsonElement element, string name, string fallback)
        => string.IsNullOrWhiteSpace(String(element, name)) ? fallback : String(element, name);

    private static Task WriteJsonAsync(string path, object value, CancellationToken cancellationToken)
        => File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), cancellationToken);
}
