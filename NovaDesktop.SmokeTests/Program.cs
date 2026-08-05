using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Nova.Core;
using NovaDesktop.Models;
using NovaDesktop.Services;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

if (args.Contains("--version", StringComparer.Ordinal))
{
    Console.WriteLine("codex-cli 1.0.0-smoke");
    return 0;
}

if (args.FirstOrDefault()?.Equals("exec", StringComparison.Ordinal) == true)
{
    if (args.Contains("--help", StringComparer.Ordinal))
    {
        Console.WriteLine("Usage: codex exec [OPTIONS] [PROMPT]");
        return 0;
    }
    Console.WriteLine("""{"type":"item.completed","item":{"type":"agent_message","text":"Codex 只读审查完成。"}}""");
    return 0;
}

if (args.Contains("--mcp-fixture", StringComparer.Ordinal))
{
    return await RunMcpFixtureAsync();
}

var failures = new List<string>();
var checks = 0;
var passed = 0;
var runtimeEvidencePath = Path.Combine(
    Path.GetTempPath(),
    "nova-runtime-evidence-" + Guid.NewGuid().ToString("N") + ".jsonl");
var runtimeEvidence = new EngineeringEvidenceLedgerService(runtimeEvidencePath);

static JsonObject DemandSignal(
    string dimension,
    decimal score,
    decimal confidence,
    string evidenceStatus,
    string rationale)
    => new()
    {
        ["dimension"] = dimension,
        ["score"] = score,
        ["confidence"] = confidence,
        ["evidence_status"] = evidenceStatus,
        ["rationale"] = rationale,
        ["source_refs"] = new JsonArray("user://smoke-fixture")
    };

static byte[] CreateMinimalPdf(string text)
{
    var safeText = text.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("(", "\\(", StringComparison.Ordinal)
        .Replace(")", "\\)", StringComparison.Ordinal);
    var contentStream = $"BT /F1 16 Tf 72 720 Td ({safeText}) Tj ET";
    var objects = new[]
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        $"<< /Length {Encoding.ASCII.GetByteCount(contentStream)} >>\nstream\n{contentStream}\nendstream",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
    };
    using var stream = new MemoryStream();
    using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true)
    {
        NewLine = "\n"
    };
    writer.WriteLine("%PDF-1.4");
    writer.Flush();
    var offsets = new List<long> { 0 };
    for (var index = 0; index < objects.Length; index++)
    {
        offsets.Add(stream.Position);
        writer.WriteLine($"{index + 1} 0 obj");
        writer.WriteLine(objects[index]);
        writer.WriteLine("endobj");
        writer.Flush();
    }
    var xrefOffset = stream.Position;
    writer.WriteLine("xref");
    writer.WriteLine($"0 {objects.Length + 1}");
    writer.WriteLine("0000000000 65535 f ");
    foreach (var offset in offsets.Skip(1))
    {
        writer.WriteLine($"{offset:0000000000} 00000 n ");
    }
    writer.WriteLine("trailer");
    writer.WriteLine($"<< /Size {objects.Length + 1} /Root 1 0 R >>");
    writer.WriteLine("startxref");
    writer.WriteLine(xrefOffset);
    writer.WriteLine("%%EOF");
    writer.Flush();
    return stream.ToArray();
}

await CheckAsync("workspace list/read/search", async () =>
{
    var host = new WorkspaceToolHost(@"D:\Agent");
    var listed = await host.ExecuteAsync(
        "list_workspace_files",
        new JsonObject { ["directory"] = "", ["max_depth"] = 2 },
        CancellationToken.None);
    Expect(listed.Contains("README.md", StringComparison.OrdinalIgnoreCase), "README.md was not listed.");

    var read = await host.ExecuteAsync(
        "read_text_file",
        new JsonObject { ["path"] = "README.md", ["max_chars"] = 10000 },
        CancellationToken.None);
    Expect(read.Contains("NOVA", StringComparison.Ordinal), "README content was not returned.");

    var searched = await host.ExecuteAsync(
        "search_workspace_text",
        new JsonObject { ["query"] = "NOVA", ["file_pattern"] = ".md", ["use_regex"] = false },
        CancellationToken.None);
    Expect(searched.Contains("README.md", StringComparison.OrdinalIgnoreCase), "Workspace search returned no README match.");
});

await CheckAsync("workspace path containment", async () =>
{
    var host = new WorkspaceToolHost(@"D:\Agent");
    try
    {
        await host.ExecuteAsync(
            "read_text_file",
            new JsonObject { ["path"] = @"..\outside.txt", ["max_chars"] = 2000 },
            CancellationToken.None);
        throw new Exception("Path escape was accepted.");
    }
    catch (InvalidOperationException exception) when (exception.Message.Contains("escapes", StringComparison.OrdinalIgnoreCase))
    {
    }
});

await CheckAsync("cross-border commerce deterministic tools", async () =>
{
    var genericHost = new WorkspaceToolHost(@"D:\Agent");
    Expect(
        !genericHost.Definitions.Any(item =>
            item["name"]?.GetValue<string>()?.StartsWith(
                "commerce_",
                StringComparison.Ordinal) == true),
        "Commerce tools leaked into the generic NOVA tool surface.");

    var host = new WorkspaceToolHost(
        @"D:\Agent",
        agentPackId: CrossBorderCommerceToolService.PackId);
    Expect(
        host.Definitions.Count(item =>
            item["name"]?.GetValue<string>()?.StartsWith(
                "commerce_",
                StringComparison.Ordinal) == true) == 4,
        "The commerce Agent Pack did not receive its four deterministic tools.");

    var passport = await host.ExecuteAsync(
        "commerce_normalize_product_passport",
        new JsonObject
        {
            ["product_name"] = "Rivoka 电动搅蒜器",
            ["sku"] = "RIV-GC-360",
            ["category"] = "电动食物切碎器",
            ["brand"] = "Rivoka",
            ["source_country"] = "China",
            ["target_market"] = "Mexico",
            ["platform"] = "TikTok Shop",
            ["currency"] = "MXN",
            ["sale_price"] = 432,
            ["unit_product_cost"] = 112,
            ["confirmed_facts"] = new JsonArray("用户提供售价 432 MXN", "提供一张产品图片"),
            ["assumptions"] = new JsonArray("玻璃杯是溢价理由"),
            ["unknowns"] = new JsonArray("食品接触认证"),
            ["source_refs"] = new JsonArray
            {
                new JsonObject
                {
                    ["url"] = "user://provided",
                    ["observed_at"] = "2026-08-01",
                    ["note"] = "售价和产品图片"
                }
            }
        },
        CancellationToken.None);
    using var passportJson = JsonDocument.Parse(passport);
    Expect(
        passportJson.RootElement.GetProperty("readinessScore").GetInt32() >= 70,
        "Product Passport readiness was not calculated.");
    Expect(
        passportJson.RootElement.GetProperty("factRegistry").GetProperty("unknowns")
            .EnumerateArray().Any(item => item.GetString() == "食品接触认证"),
        "Product Passport lost a declared unknown.");

    var profit = await host.ExecuteAsync(
        "commerce_calculate_landed_profit",
        new JsonObject
        {
            ["currency"] = "MXN",
            ["sale_price"] = 432,
            ["unit_product_cost"] = 112,
            ["domestic_shipping"] = 8,
            ["international_shipping"] = 48,
            ["packaging"] = 9,
            ["duty_rate_pct"] = 10,
            ["import_tax_rate_pct"] = 16,
            ["platform_fee_rate_pct"] = 6,
            ["payment_fee_rate_pct"] = 3,
            ["affiliate_rate_pct"] = 10,
            ["ad_cost_rate_pct"] = 12,
            ["return_rate_pct"] = 6,
            ["return_loss_rate_pct"] = 35,
            ["return_handling_cost"] = 42,
            ["other_variable_cost"] = 6
        },
        CancellationToken.None);
    using var profitJson = JsonDocument.Parse(profit);
    Expect(
        profitJson.RootElement.GetProperty("outcome").GetProperty("contributionProfit").GetDecimal() > 0,
        "Landed Profit Engine did not return a positive contribution for the fixture.");
    Expect(
        profitJson.RootElement.GetProperty("outcome").GetProperty("breakEvenRoas").GetDecimal() > 1,
        "Landed Profit Engine did not calculate break-even ROAS.");

    var ledger = await host.ExecuteAsync(
        "commerce_build_evidence_ledger",
        new JsonObject
        {
            ["as_of"] = "2026-08-01",
            ["max_age_days"] = 30,
            ["claims"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "market-price",
                    ["statement"] = "可见竞品价格",
                    ["value"] = "399 MXN",
                    ["source_url"] = "https://example.com/a",
                    ["source_title"] = "Marketplace A",
                    ["observed_at"] = "2026-07-28",
                    ["evidence_type"] = "marketplace",
                    ["confidence"] = 75,
                    ["market"] = "Mexico / TikTok Shop"
                },
                new JsonObject
                {
                    ["id"] = "market-price",
                    ["statement"] = "另一可见竞品价格",
                    ["value"] = "499 MXN",
                    ["source_url"] = "https://example.com/b",
                    ["source_title"] = "Marketplace B",
                    ["observed_at"] = "2026-07-29",
                    ["evidence_type"] = "marketplace",
                    ["confidence"] = 75,
                    ["market"] = "Mexico / TikTok Shop"
                }
            }
        },
        CancellationToken.None);
    using var ledgerJson = JsonDocument.Parse(ledger);
    Expect(
        ledgerJson.RootElement.GetProperty("summary").GetProperty("conflicts").GetInt32() == 1
        && !ledgerJson.RootElement.GetProperty("summary").GetProperty("launchGateReady").GetBoolean(),
        "Evidence Ledger hid conflicting market values.");

    var demand = await host.ExecuteAsync(
        "commerce_assess_market_demand",
        new JsonObject
        {
            ["product_name"] = "六头吸顶灯",
            ["target_market"] = "Mexico",
            ["platform"] = "Mercado Libre",
            ["as_of"] = "2026-08-01",
            ["identity_confidence"] = 88,
            ["signals"] = new JsonArray
            {
                DemandSignal("problem-urgency", 66, 72, "indicative", "需要覆盖较大空间的基础照明。"),
                DemandSignal("market-activity", 70, 75, "verified", "目标平台存在持续上新的同类供给。"),
                DemandSignal("differentiation", 58, 65, "indicative", "造型可见，但规格差异仍待确认。"),
                DemandSignal("local-fit", 62, 64, "indicative", "家用场景基本成立，电气制式待核验。"),
                DemandSignal("compliance-risk", 55, 70, "verified", "电气认证和标签要求需要进入清单。")
            }
        },
        CancellationToken.None);
    using var demandJson = JsonDocument.Parse(demand);
    Expect(
        demandJson.RootElement.GetProperty("schema").GetString() == "nova.commerce.market-demand-fit.v1"
        && demandJson.RootElement.GetProperty("summary").GetProperty("evidenceCoveragePct").GetDecimal() >= 40
        && demandJson.RootElement.GetProperty("dimensions").GetArrayLength() == 12,
        "Market Demand Fit did not expose its non-financial dimensions and evidence coverage.");
    Expect(
        demandJson.RootElement.GetProperty("reasoningBoundary").EnumerateArray()
            .Any(item => item.GetString()?.Contains("不是销量", StringComparison.Ordinal) == true),
        "Market Demand Fit did not preserve its no-sales-prediction boundary.");
});

await CheckAsync("Agent Creation Standard workshop", async () =>
{
    var sandbox = Path.Combine(
        Path.GetTempPath(),
        "nova-agent-workshop-smoke-" + Guid.NewGuid().ToString("N"));
    var installed = Path.Combine(sandbox, "installed");
    var state = Path.Combine(sandbox, "state.json");
    Directory.CreateDirectory(sandbox);
    try
    {
        var packs = new AgentPackService([], state, installed);
        var workshop = new AgentPackWorkshopService(packs);
        Expect(workshop.ListTemplates().Count >= 10, "Agent workshop lost scenario diversity templates.");
        var request = new AgentPackCreationRequest(
            "nova.user.smoke-research",
            "行业研究验证 Agent",
            "测试行业",
            "为测试用户生成可以落盘、可以校准并且可以审查的行业研究交付物。",
            "基于现有资料形成一份可核验的行业判断",
            "research",
            "assist",
            "project",
            "specialist-team",
            "document",
            "balanced",
            "行业判断报告.md",
            [],
            [],
            []);
        var recommendation = workshop.Recommend(request);
        Expect(recommendation.Summary.Contains("测试行业", StringComparison.Ordinal)
               && recommendation.Summary.Contains("项目持续", StringComparison.Ordinal)
               && recommendation.RequiredInputs.Any(item => item.Contains("行业判断", StringComparison.Ordinal)),
            "Agent workshop recommendations did not summarize the first three design stages.");
        Expect(recommendation.StarterPrompts.Any(item => item.Contains("行业判断报告.md", StringComparison.Ordinal)),
            "Agent workshop did not derive a concrete start path from the requested delivery.");
        var created = await workshop.CreateAsync(request);
        Expect(created.Certification.Level == "Runnable", "Generated Agent Pack was not certified Runnable.");
        Expect(created.Certification.Score == 100, "Generated Agent Pack did not pass all baseline checks.");
        var details = packs.Get(created.Pack.Id);
        Expect(details.Onboarding?.Steps.Count >= 3, "Generated Agent Pack lost guided inputs.");
        Expect(details.Onboarding?.Outcomes[0].PromptTemplate.Contains("{{required-1}}", StringComparison.Ordinal) == true,
            "Generated Agent Pack did not carry guided input values into the task prompt.");
        Expect(details.Workflows.FirstOrDefault()?.Steps.Count >= 4, "Generated Agent Pack lost its executable workflow contract.");
        Expect(details.Certification?.Score == 100, "Agent certification was not persisted with the installed pack.");
        Expect(File.Exists(Path.Combine(installed, created.Pack.Id, "agent-card.json")), "Generated Agent Card was not installed.");

        var reviewedDraft = new AgentWorkshopOrchestrationDraft(
            "A reviewed multi-role operating design.",
            ["Separate domain discovery from delivery review."],
            [
                new AgentWorkshopRoleDraft(
                    "domain-analyst", "Domain Analyst", "Collect and classify evidence.", ["evidence-map.md"]),
                new AgentWorkshopRoleDraft(
                    "workflow-lead", "Workflow Lead", "Synthesize the final result.", ["industry-report.md"]),
                new AgentWorkshopRoleDraft(
                    "quality-reviewer", "Quality Reviewer", "Independently verify the delivery.", ["proof-of-done.json"])
            ],
            [
                new AgentWorkshopStepDraft(1, "Discover evidence", "domain-analyst", "evidence-map.md", ["Sources are traceable"]),
                new AgentWorkshopStepDraft(2, "Build the result", "workflow-lead", "industry-report.md", ["The requested outcome is complete"]),
                new AgentWorkshopStepDraft(3, "Review independently", "quality-reviewer", "proof-of-done.json", ["Every claim has an explicit verdict"])
            ],
            ["Target market", "候选人简历文件"],
            ["Historical examples"],
            ["Research this market and produce industry-report.md"],
            ["Sparse evidence may lower confidence."],
            "approved",
            "smoke-provider",
            "review-model");
        var reviewedRequest = request with
        {
            Id = "nova.user.smoke-orchestrated",
            Name = "Reviewed Orchestration Agent",
            Orchestration = reviewedDraft
        };
        var reviewed = await workshop.CreateAsync(reviewedRequest);
        var reviewedDetails = packs.Get(reviewed.Pack.Id);
        Expect(reviewedDetails.Workflows[0].Steps.Select(step => step.Agent)
                .SequenceEqual(["domain-analyst", "workflow-lead", "quality-reviewer"]),
            "Reviewed orchestration roles were not persisted into the executable workflow.");
        Expect(reviewedDetails.Workflows[0].Steps.Count == reviewedDraft.Workflow.Count,
            "Reviewed orchestration lost one or more approved execution steps during Pack compilation.");
        Expect(reviewedDetails.Onboarding?.Steps.Any(step =>
                step.Title.Contains("简历", StringComparison.Ordinal)
                && step.Kind.Equals("attachment", StringComparison.OrdinalIgnoreCase)) == true,
            "File-oriented required inputs were rendered as text fields instead of upload controls.");
        Expect(reviewedDetails.AgentRoster.Contains("Domain Analyst", StringComparison.Ordinal)
               && reviewedDetails.AgentRoster.Contains("Quality Reviewer", StringComparison.Ordinal),
            "Reviewed orchestration roster was replaced by the legacy two-role template.");
        var reviewedManifest = await File.ReadAllTextAsync(
            Path.Combine(installed, reviewed.Pack.Id, "nova.industry.json"));
        Expect(reviewedManifest.Contains("review-model", StringComparison.Ordinal)
               && reviewedManifest.Contains("Sparse evidence", StringComparison.Ordinal),
            "The reviewer provenance and risks were not persisted with the Agent Pack.");
        _ = await packs.RemoveAsync(reviewed.Pack.Id);
        Expect(!Directory.Exists(Path.Combine(installed, reviewed.Pack.Id)),
            "A removable Agent Pack remained in the installed extension directory.");
        try
        {
            _ = packs.Get(reviewed.Pack.Id);
            throw new Exception("A removed Agent Pack remained available in the registry.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("不存在", StringComparison.Ordinal))
        {
        }
    }
    finally
    {
        if (Directory.Exists(sandbox))
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }
});

await CheckAsync("Agent calibration overlays are scoped and reversible", async () =>
{
    var sandbox = Path.Combine(
        Path.GetTempPath(),
        "nova-agent-calibration-smoke-" + Guid.NewGuid().ToString("N"));
    var state = Path.Combine(sandbox, "calibrations.json");
    var workspace = Path.Combine(sandbox, "project-a");
    var otherWorkspace = Path.Combine(sandbox, "project-b");
    Directory.CreateDirectory(workspace);
    Directory.CreateDirectory(otherWorkspace);
    try
    {
        var calibration = new AgentCalibrationService(state);
        const string packId = "nova.user.smoke-research";
        await calibration.CreateAsync(new CreateAgentCalibrationRequest(
            packId, "agent", "judgment",
            "行业结论必须同时检查真实需求和竞争密度。",
            null, null, "行业报告", "行业报告.md"));
        await calibration.CreateAsync(new CreateAgentCalibrationRequest(
            packId, "project", "format",
            "当前项目的交付文件使用中文名称。",
            null, workspace, "项目交付", "交付物.md"));
        await calibration.CreateAsync(new CreateAgentCalibrationRequest(
            packId, "turn", "evidence",
            "本轮所有判断都要标出证据等级。",
            "task-a", workspace, "本轮回答", null));

        var exact = calibration.BuildRuntimeContext(packId, "task-a", workspace);
        Expect(exact.Contains("真实需求和竞争密度", StringComparison.Ordinal)
               && exact.Contains("中文名称", StringComparison.Ordinal)
               && exact.Contains("证据等级", StringComparison.Ordinal),
            "Applicable Agent, project and turn calibrations were not composed.");

        var nextTurn = calibration.BuildRuntimeContext(packId, "task-b", workspace);
        Expect(nextTurn.Contains("真实需求和竞争密度", StringComparison.Ordinal)
               && nextTurn.Contains("中文名称", StringComparison.Ordinal)
               && !nextTurn.Contains("证据等级", StringComparison.Ordinal),
            "Turn calibration leaked into another task or project rules were lost.");

        var otherProject = calibration.BuildRuntimeContext(packId, "task-b", otherWorkspace);
        Expect(otherProject.Contains("真实需求和竞争密度", StringComparison.Ordinal)
               && !otherProject.Contains("中文名称", StringComparison.Ordinal),
            "Project calibration leaked into another workspace.");

        var snapshot = calibration.GetSnapshot(packId);
        var agentPatch = snapshot.Patches.Single(patch => patch.Scope == "agent");
        var rolledBack = await calibration.RollbackAsync(packId, agentPatch.Id);
        Expect(rolledBack.ActiveCount == 2, "Calibration rollback did not update the active count.");
        Expect(!calibration.BuildRuntimeContext(packId, "task-b", otherWorkspace)
                .Contains("真实需求和竞争密度", StringComparison.Ordinal),
            "Rolled-back calibration still affected runtime context.");
    }
    finally
    {
        if (Directory.Exists(sandbox))
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }
});

await CheckAsync("bounded task approval policy", async () =>
{
    var policy = new TaskApprovalPolicy();
    policy.BeginRun("task-a");

    var safePatch = new ToolApprovalRequest(
        "replace_text_in_file",
        "审查并编辑 app.cs？",
        "安全 Patch",
        """{"path":"app.cs"}""",
        "unified-diff",
        "@@ -1 +1 @@\n-old\n+new",
        1,
        1);
    var writeScope = policy.Describe(safePatch);
    Expect(writeScope.CanTrustForRun, "A bounded, reviewable patch was not eligible for run trust.");
    Expect(policy.GrantForRun("task-a", writeScope), "The run-scoped write grant was not stored.");
    Expect(policy.IsGranted("task-a", writeScope), "The current run did not reuse its explicit grant.");

    var desktopScope = policy.Describe(new ToolApprovalRequest(
        "type_text_to_window",
        "允许输入？",
        "桌面输入",
        "{}"));
    Expect(!desktopScope.CanTrustForRun, "Desktop input must remain a per-action approval.");
    Expect(!policy.GrantForRun("task-a", desktopScope), "A high-impact desktop action was granted run trust.");

    policy.BeginRun("task-b");
    Expect(!policy.IsGranted("task-b", writeScope), "A grant leaked into a later execution run.");
    var mainWindow = await File.ReadAllTextAsync(@"D:\Agent\NovaDesktop\MainWindow.xaml");
    var threadspace = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\Controls\ConversationStage.xaml");
    Expect(
        mainWindow.Contains("ApproveForRunCommand", StringComparison.Ordinal)
        && threadspace.Contains("ApprovalSafetyNote", StringComparison.Ordinal)
        && threadspace.Contains("IsApprovalTrustVisible", StringComparison.Ordinal)
        && threadspace.Contains("MaxHeight=\"390\"", StringComparison.Ordinal)
        && threadspace.Contains("MaxHeight=\"92\"", StringComparison.Ordinal),
        "The run-scoped choice is not exposed consistently in the native approval UI.");
});

await CheckAsync("cross-platform Mac core and honest shell", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-mac-core-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(temporaryDirectory, "node_modules", "ignored"));
    try
    {
        await File.WriteAllTextAsync(
            Path.Combine(temporaryDirectory, "package.json"),
            """{"name":"nova-mac-fixture"}""");
        await File.WriteAllTextAsync(
            Path.Combine(temporaryDirectory, ".mcp.json"),
            """{"mcpServers":{}}""");
        await File.WriteAllTextAsync(
            Path.Combine(temporaryDirectory, "node_modules", "ignored", "secret.txt"),
            "must not influence workspace signals");

        var workspace = new WorkspaceContextService().Analyze(temporaryDirectory);
        Expect(workspace.Technology == "Node.js / Web", "Mac core did not identify the workspace.");
        Expect(
            workspace.Signals.Contains("package.json", StringComparer.OrdinalIgnoreCase),
            "Mac core did not retain bounded project signals.");

        var locations = new CrossPlatformMcpProbe().GetKnownLocations(temporaryDirectory);
        Expect(
            locations.Any(item => item.Product == "当前工作区" && item.Exists),
            "Mac MCP probe did not find the explicit workspace configuration.");

        var runtime = new ProviderChatService(new HttpClient(new FakeMacOpenAiHandler()));
        var response = await runtime.SendAsync(
            new AgentChatRequest(
                "openai",
                "gpt-test",
                "sk-test-memory-only",
                [new AgentMessage("user", "检查工作区")],
                workspace),
            CancellationToken.None);
        Expect(response.Text == "Mac Core real response.", "Mac provider response was not parsed.");

        var kimiHandler = new FakeMacKimiHandler();
        var kimi = await new ProviderChatService(new HttpClient(kimiHandler)).SendAsync(
            new AgentChatRequest(
                "kimi",
                "kimi-k3",
                "sk-test-memory-only",
                [new AgentMessage("user", "Check Kimi routing")],
                workspace),
            CancellationToken.None);
        Expect(
            kimi.Text == "Mac Kimi real response."
            && kimiHandler.UsedMoonshotEndpoint,
            "Mac Kimi provider did not use the Moonshot chat-completions endpoint.");

        var parallelHandler = new FakeParallelMacHandler();
        var parallel = await new ParallelChatService(
            new ProviderChatService(new HttpClient(parallelHandler)))
            .RunAsync(
                new AgentChatRequest(
                    "openai",
                    "gpt-test",
                    "sk-test-memory-only",
                    [new AgentMessage("user", "优化 Mac UI 性能和交互")],
                    workspace),
                CrossPlatformParallelPlanner.Create("优化 Mac UI 性能和交互"),
                CancellationToken.None);
        Expect(parallel.Workers.Count == 3, "Mac Autopilot did not create three workers.");
        Expect(parallelHandler.PeakConcurrency == 3, "Mac child agents did not run concurrently.");
        Expect(parallelHandler.RequestCount == 4, "Mac Autopilot did not run three workers and one commander.");

        var macXaml = await File.ReadAllTextAsync(@"D:\Agent\NovaDesktop.Mac\MainWindow.axaml");
        var macCode = await File.ReadAllTextAsync(@"D:\Agent\NovaDesktop.Mac\MainWindow.axaml.cs");
        Expect(
            macXaml.Contains("NOVA THREADSPACE", StringComparison.Ordinal)
            && macXaml.Contains("不会伪装成交付", StringComparison.Ordinal)
            && macXaml.Contains("Autopilot · 3 子 Agent", StringComparison.Ordinal)
            && macXaml.Contains("Kimi · Moonshot AI", StringComparison.Ordinal)
            && macXaml.Contains("任务空间", StringComparison.Ordinal),
            "Mac shell does not expose its real capability boundary.");
        Expect(
            macCode.Contains("OpenFolderPickerAsync", StringComparison.Ordinal)
            && macCode.Contains("ProviderChatService", StringComparison.Ordinal)
            && macCode.Contains("MacAgentOsHost", StringComparison.Ordinal)
            && macCode.Contains("CompleteTaskAsync", StringComparison.Ordinal),
            "Mac shell is missing native workspace selection, model runtime or shared AgentOS.");
        Expect(
            !macXaml.Contains("System.Windows", StringComparison.Ordinal)
            && !macCode.Contains("System.Windows", StringComparison.Ordinal),
            "Mac shell retained a WPF dependency.");
    }
    finally
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
});

await CheckAsync("macOS Preview 4 synchronized bundle and release gates", async () =>
{
    var manifestPath = @"D:\Agent\dist\macos\macos-release-manifest.json";
    var manifest = JsonNode.Parse(
        await File.ReadAllTextAsync(manifestPath))?.AsObject()
        ?? throw new Exception("macOS release manifest is invalid.");
    var packages = manifest["packages"]?.AsArray()
                   ?? throw new Exception("macOS release manifest has no packages.");
    Expect(
        manifest["version"]?.GetValue<string>() == "0.1.0-preview.4"
        && manifest["release_gate"]?.GetValue<string>() == "CROSS_BUILT_UNSIGNED"
        && packages.Count == 2,
        "macOS release does not truthfully expose both Preview 4 architectures.");

    foreach (var package in packages)
    {
        var item = package?.AsObject()
                   ?? throw new Exception("macOS package entry is invalid.");
        var runtime = item["runtime"]?.GetValue<string>() ?? string.Empty;
        var tar = item["tar_gz"]?.AsObject()
                  ?? throw new Exception("macOS TAR.GZ entry is missing.");
        var tarPath = Path.Combine(
            @"D:\Agent\dist\macos",
            tar["file"]?.GetValue<string>() ?? string.Empty);
        var expectedHash = tar["sha256"]?.GetValue<string>() ?? string.Empty;
        var actualHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                await File.ReadAllBytesAsync(tarPath)))
            .ToLowerInvariant();
        Expect(actualHash == expectedHash, $"macOS {runtime} TAR.GZ hash drifted.");

        var executablePath = Path.Combine(
            @"D:\Agent\dist\macos",
            $"NOVA-Mac-0.1.0-preview.4-{runtime}",
            "NOVA.app",
            "Contents",
            "MacOS",
            "NovaDesktop.Mac");
        var packageRoot = Path.GetFullPath(
            Path.Combine(executablePath, "..", "..", "..", ".."));
        var packageManifest = JsonNode.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(packageRoot, "MAC-RELEASE.json")))?.AsObject()
            ?? throw new Exception($"macOS {runtime} package manifest is invalid.");
        var synchronizedCapabilities = packageManifest["synchronized_capabilities"]?.AsArray()
            ?? throw new Exception($"macOS {runtime} synchronized capability list is missing.");
        Expect(
            File.Exists(Path.Combine(
                Path.GetDirectoryName(executablePath)!,
                "Nova.AgentOS.dll"))
            && synchronizedCapabilities.Any(item =>
                item?.GetValue<string>() == "shared_agentos_kernel")
            && synchronizedCapabilities.Any(item =>
                item?.GetValue<string>() == "durable_supervisor"),
            $"macOS {runtime} package omitted the shared AgentOS runtime or capability truth.");
        var header = (await File.ReadAllBytesAsync(executablePath)).Take(8).ToArray();
        var expectedCpu = runtime == "osx-arm64" ? 0x0100000C : 0x01000007;
        Expect(
            header.Length == 8
            && header.Take(4).SequenceEqual(new byte[] { 0xCF, 0xFA, 0xED, 0xFE })
            && BitConverter.ToInt32(header, 4) == expectedCpu,
            $"macOS {runtime} package does not contain the expected Mach-O app host.");

        await using var file = File.OpenRead(tarPath);
        await using var gzip = new System.IO.Compression.GZipStream(
            file,
            System.IO.Compression.CompressionMode.Decompress);
        using var reader = new System.Formats.Tar.TarReader(gzip);
        System.Formats.Tar.TarEntry? entry;
        var executableMode = 0;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (entry.Name.Equals(
                "NOVA.app/Contents/MacOS/NovaDesktop.Mac",
                StringComparison.Ordinal))
            {
                executableMode = (int)entry.Mode;
                break;
            }
        }
        Expect(
            (executableMode & 0x49) == 0x49,
            $"macOS {runtime} archive did not preserve executable permission bits.");
    }

    var macProject = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop.Mac\NovaDesktop.Mac.csproj");
    var macProgram = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop.Mac\Program.cs");
    var crossBuild = await File.ReadAllTextAsync(@"D:\Agent\build-macos.ps1");
    var nativeBuild = await File.ReadAllTextAsync(@"D:\Agent\build-macos.sh");
    Expect(
        macProject.Contains("<InformationalVersion>$(Version)</InformationalVersion>", StringComparison.Ordinal)
        && macProject.Contains("Nova.AgentOS", StringComparison.Ordinal)
        && macProgram.Contains("--startup-smoke", StringComparison.Ordinal)
        && macProgram.Contains("--agentos-smoke", StringComparison.Ordinal)
        && crossBuild.Contains("xattr -dr com.apple.quarantine", StringComparison.Ordinal)
        && crossBuild.Contains("shared_agentos_kernel", StringComparison.Ordinal)
        && nativeBuild.Contains("notarytool submit", StringComparison.Ordinal)
        && nativeBuild.Contains("stapler validate", StringComparison.Ordinal)
        && nativeBuild.Contains("Stable macOS release blocked", StringComparison.Ordinal),
        "macOS startup, first-launch or Developer ID/notarization gates are incomplete.");
});

await CheckAsync("WPF read-only binding startup safety", async () =>
{
    XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    foreach (var path in Directory.EnumerateFiles(@"D:\Agent\NovaDesktop", "*.xaml"))
    {
        var document = XDocument.Load(path);
        var unsafeBindings = document
            .Descendants(presentation + "TextBox")
            .Where(element =>
                string.Equals(
                    element.Attribute("IsReadOnly")?.Value,
                    "True",
                    StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Attribute("Text")?.Value ?? string.Empty)
            .Where(value =>
                value.StartsWith("{Binding", StringComparison.Ordinal)
                && !value.Contains("Mode=OneWay", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Expect(
            unsafeBindings.Length == 0,
            $"{Path.GetFileName(path)} contains a read-only TextBox with a writable default binding.");

        var unsafeExecutionDetailBindings = document
            .Descendants()
            .Attributes()
            .Select(attribute => attribute.Value)
            .Where(value =>
                value.StartsWith("{Binding ExecutionModeDetail", StringComparison.Ordinal)
                && !value.Contains("Mode=OneWay", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Expect(
            unsafeExecutionDetailBindings.Length == 0,
            $"{Path.GetFileName(path)} binds the read-only ExecutionModeDetail property without Mode=OneWay.");
    }
    var mainWindowXaml = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\MainWindow.xaml");
    Expect(
        mainWindowXaml.Contains(
            "{Binding KindLabel, Mode=OneWay}",
            StringComparison.Ordinal)
        && mainWindowXaml.Contains(
            "{Binding SizeLabel, Mode=OneWay}",
            StringComparison.Ordinal),
        "Attachment chips can write back into computed labels and crash during rendering.");
    var appStartup = await File.ReadAllTextAsync(@"D:\Agent\NovaDesktop\App.xaml.cs");
    Expect(
        appStartup.Contains("--startup-smoke", StringComparison.Ordinal)
        && appStartup.Contains("--attachment-render-smoke", StringComparison.Ordinal)
        && appStartup.Contains(
            "IsRecoverablePresentationBindingException",
            StringComparison.Ordinal)
        && appStartup.Contains("ContentRendered", StringComparison.Ordinal),
        "Packaged WPF startup or attachment rendering cannot be exercised safely.");
    await Task.CompletedTask;
});

await CheckAsync("soft motion and interruptible conversation scrolling", async () =>
{
    var appXaml = await File.ReadAllTextAsync(@"D:\Agent\NovaDesktop\App.xaml");
    var mainXaml = await File.ReadAllTextAsync(@"D:\Agent\NovaDesktop\MainWindow.xaml");
    var conversationXaml = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\Controls\ConversationStage.xaml");
    var conversationCode = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\Controls\ConversationStage.xaml.cs");
    Expect(
        appXaml.Contains("CubicEase EasingMode=\"EaseOut\"", StringComparison.Ordinal)
        && appXaml.Contains("PreviewMouseLeftButtonDown", StringComparison.Ordinal),
        "Shared buttons are missing soft hover/press motion.");
    Expect(
        conversationXaml.Contains("From=\"12\" To=\"0\"", StringComparison.Ordinal)
        && mainXaml.Contains("行动脉络", StringComparison.Ordinal),
        "Conversation and execution events do not enter with the softened motion language.");
    Expect(
        conversationCode.Contains("SmoothScrollTo", StringComparison.Ordinal)
        && conversationCode.Contains("DispatcherPriority.Render", StringComparison.Ordinal)
        && conversationCode.Contains("_scrollAnimationTimer.Stop()", StringComparison.Ordinal),
        "Conversation scrolling is not eased or interruptible.");
});

await CheckAsync("modern technology visual system", async () =>
{
    var appXaml = await File.ReadAllTextAsync(@"D:\Agent\NovaDesktop\App.xaml");
    var mainXaml = await File.ReadAllTextAsync(@"D:\Agent\NovaDesktop\MainWindow.xaml");
    var conversationXaml = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\Controls\ConversationStage.xaml");
    Expect(
        appXaml.Contains("<Color x:Key=\"ColorBackground\">#070A12</Color>", StringComparison.Ordinal)
        && appXaml.Contains("<Color x:Key=\"ColorCyan\">#62E6FF</Color>", StringComparison.Ordinal)
        && appXaml.Contains("<Color x:Key=\"ColorViolet\">#9A86FF</Color>", StringComparison.Ordinal)
        && appXaml.Contains("ShadowDepth=\"0\"", StringComparison.Ordinal),
        "Shared resources are not using the NOVA graphite and electric-spectrum design system.");
    Expect(
        mainXaml.Contains("Text=\"LOCAL\"", StringComparison.Ordinal)
        && !mainXaml.Contains("Text=\"诺\"", StringComparison.Ordinal),
        "The title bar still exposes the decorative seal identity.");
    Expect(
        conversationXaml.Contains("Text=\"NOVA THREADSPACE\"", StringComparison.Ordinal)
        && conversationXaml.Contains(
            "Property=\"Background\" Value=\"#0D1522\"",
            StringComparison.Ordinal)
        && !conversationXaml.Contains("<DrawingBrush", StringComparison.Ordinal)
        && !conversationXaml.Contains(
            "<Rectangle IsHitTestVisible=\"False\"",
            StringComparison.Ordinal)
        && !conversationXaml.Contains("Text=\"和\"", StringComparison.Ordinal),
        "Threadspace still carries the patterned or warm ornamental visual language.");
    Expect(
        appXaml.Contains("BlurRadius=\"0\"", StringComparison.Ordinal)
        && appXaml.Contains("Opacity=\"0\"", StringComparison.Ordinal)
        && mainXaml.Contains(
            "BorderThickness=\"2,0,0,0\"",
            StringComparison.Ordinal)
        && mainXaml.Contains(
            "BorderThickness=\"0,1,0,0\"",
            StringComparison.Ordinal)
        && conversationXaml.Contains(
            "<SolidColorBrush Color=\"#080C13\"/>",
            StringComparison.Ordinal)
        && conversationXaml.Contains(
            "BorderThickness=\"0,0,0,1\"",
            StringComparison.Ordinal)
        && !mainXaml.Contains("Background=\"#FB111925\"", StringComparison.Ordinal),
        "The main shell still uses glass cards instead of the flat workstation system.");
});

await CheckAsync("Threadspace conversation-first shell", async () =>
{
    var mainWindow = await File.ReadAllTextAsync(@"D:\Agent\NovaDesktop\MainWindow.xaml");
    var threadspace = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\Controls\ConversationStage.xaml");
    var markdownRenderer = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\Controls\MarkdownMessageView.cs");
    Expect(
        mainWindow.Contains("<controls:ConversationStage Grid.Row=\"3\"/>", StringComparison.Ordinal)
        && mainWindow.Contains("Text=\"{Binding HumanGuidanceTitle}\"", StringComparison.Ordinal)
        && mainWindow.Contains("Text=\"{Binding HumanStatusLabel}\"", StringComparison.Ordinal)
        && mainWindow.Contains("Command=\"{Binding ResumeSelectedCommand}\"", StringComparison.Ordinal)
        && mainWindow.Contains("Text=\"从这里继续\"", StringComparison.Ordinal),
        "Main window did not mount the full-height conversation stage.");
    Expect(
        threadspace.Contains("NOVA THREADSPACE", StringComparison.Ordinal)
        && threadspace.Contains("ItemsSource=\"{Binding ConversationTurns}\"", StringComparison.Ordinal),
        "Threadspace identity or persistent conversation timeline is missing.");
    Expect(
        threadspace.Contains("本轮成果已进入线程", StringComparison.Ordinal)
        && threadspace.Contains("IsApprovalVisible", StringComparison.Ordinal),
        "Artifacts and approvals are not integrated into the conversation surface.");
    Expect(
        markdownRenderer.Contains("AddCodeBlock", StringComparison.Ordinal)
        && markdownRenderer.Contains("AddTable", StringComparison.Ordinal),
        "Rich Markdown code and table rendering are not available.");
    Expect(
        threadspace.Contains(
            "ScrollViewer.CanContentScroll=\"False\"",
            StringComparison.Ordinal)
        && threadspace.Contains(
            "VirtualizingPanel.IsVirtualizing=\"False\"",
            StringComparison.Ordinal)
        && threadspace.Contains(
            "PreviewMouseWheel=\"ConversationList_PreviewMouseWheel\"",
            StringComparison.Ordinal),
        "Oversized delivery messages cannot be traversed with pixel scrolling.");
    Expect(
        threadspace.Contains("Text=\"NOVA THREADSPACE\"", StringComparison.Ordinal)
        && threadspace.Contains("Text=\"选择下一步\"", StringComparison.Ordinal)
        && threadspace.Contains("ThreadSpectrum", StringComparison.Ordinal),
        "Modern technology identity or interactive decision language is missing.");
});

await CheckAsync("structured conversation choices and legacy option recovery", async () =>
{
    var explicitTurn = new ConversationTurn(
        "choice-explicit",
        "choice-task",
        "assistant",
        """
        请选择下一步：
        [[NOVA_CHOICE|推荐 · 健身习惯|我选择健身习惯方向，请继续构建完整方案。]]
        [[NOVA_CHOICE|个人记账|我选择个人记账方向，请继续构建完整方案。]]
        """,
        DateTimeOffset.Now);
    Expect(
        explicitTurn.HasChoices
        && explicitTurn.Choices.Count == 2
        && explicitTurn.Choices[0].Title.Contains("推荐", StringComparison.Ordinal),
        "Explicit NOVA choice markers were not converted into interactive choices.");
    Expect(
        !explicitTurn.DisplayContent.Contains("NOVA_CHOICE", StringComparison.Ordinal),
        "Internal choice markers leaked into the visible Markdown response.");

    var legacyTurn = new ConversationTurn(
        "choice-legacy",
        "choice-task",
        "assistant",
        """
        我建议几个方向：

        **方向一：健身助手小程序** 🏋️
        - 训练记录、动作库和习惯打卡

        **方向二：个人记账本**
        - 收支记录和分类统计

        ---
        请选择一个方向。
        """,
        DateTimeOffset.Now);
    Expect(
        legacyTurn.HasChoices
        && legacyTurn.Choices.Count == 2
        && legacyTurn.Choices[0].Description.Contains("训练记录", StringComparison.Ordinal),
        "Existing prose option lists were not recovered as interactive choices.");
    await Task.CompletedTask;
});

await CheckAsync("Responses API tool loop", async () =>
{
    var handler = new FakeResponsesHandler();
    var runtime = new OpenAIResponsesAgentRuntime(new HttpClient(handler), runtimeEvidence);
    var events = new List<AgentRuntimeEvent>();
    var approvals = 0;
    var result = await runtime.RunAsync(
        new AgentRunRequest("smoke", "列出工作区文件", @"D:\Agent", "sk-test", "openai", "gpt-5.6"),
        item =>
        {
            events.Add(item);
            return Task.CompletedTask;
        },
        _ =>
        {
            approvals++;
            return Task.FromResult(true);
        },
        CancellationToken.None);

    Expect(result.FinalText == "真实工具循环已完成。", "Final model text was not parsed.");
    Expect(result.ToolCalls == 1, "Expected exactly one tool call.");
    Expect(handler.RequestCount == 2, "Expected two Responses API requests.");
    Expect(handler.SecondRequestContainedToolOutput, "Tool output was not continued into the second request.");
    Expect(approvals == 0, "Read-only tool unexpectedly requested approval.");
    Expect(events.Any(item => item.Kind == AgentRuntimeEventKind.ToolCompleted), "No tool completion event was emitted.");
});

await CheckAsync("Responses API parallel tool routing", async () =>
{
    var handler = new FakeParallelResponsesHandler();
    var runtime = new OpenAIResponsesAgentRuntime(new HttpClient(handler), runtimeEvidence);
    var events = new List<AgentRuntimeEvent>();
    var result = await runtime.RunAsync(
        new AgentRunRequest("parallel-runtime", "并行检查工作区", @"D:\Agent", "sk-test", "openai", "gpt-5.6"),
        item =>
        {
            lock (events)
            {
                events.Add(item);
            }
            return Task.CompletedTask;
        },
        _ => throw new Exception("Parallel read-only calls must not request approval."),
        CancellationToken.None);

    Expect(result.ToolCalls == 2, "Parallel runtime did not count both tool calls.");
    Expect(handler.SecondRequestContainedBothOutputs, "Parallel tool outputs were not returned to the model.");
    Expect(events.Any(item => item.Kind == AgentRuntimeEventKind.ToolBatchStarted && item.ActiveUnits == 2), "Runtime did not route calls through parallel tool executor.");
});

await CheckAsync("DeepSeek SSE tool loop", async () =>
{
    var handler = new FakeDeepSeekHandler();
    var runtime = new DeepSeekChatAgentRuntime(new HttpClient(handler), runtimeEvidence);
    var events = new List<AgentRuntimeEvent>();
    var approvals = 0;
    var result = await runtime.RunAsync(
        new AgentRunRequest("deepseek-smoke", "列出工作区文件", @"D:\Agent", "ds-test", "deepseek", "deepseek-v4-flash"),
        item =>
        {
            events.Add(item);
            return Task.CompletedTask;
        },
        _ =>
        {
            approvals++;
            return Task.FromResult(true);
        },
        CancellationToken.None);

    var streamedText = string.Concat(
        events.Where(item => item.Kind == AgentRuntimeEventKind.TextDelta)
            .Select(item => item.Detail));
    Expect(result.FinalText == "DeepSeek 流式工具循环已完成。", "DeepSeek final text was not assembled.");
    Expect(result.Provider == "deepseek", "DeepSeek provider metadata was not retained.");
    Expect(result.ToolCalls == 1, "Expected exactly one DeepSeek tool call.");
    Expect(handler.RequestCount == 2, "Expected two DeepSeek chat completion requests.");
    Expect(handler.SecondRequestContainedToolOutput, "DeepSeek tool output was not continued into the second request.");
    Expect(streamedText == result.FinalText, "DeepSeek text deltas did not reconstruct the final answer.");
    Expect(approvals == 0, "DeepSeek read-only tool unexpectedly requested approval.");
    Expect(events.Any(item => item.Kind == AgentRuntimeEventKind.Thinking), "No DeepSeek thinking event was emitted.");
});

await CheckAsync("Evolution runtime exposes only bounded plugin tools", async () =>
{
    var handler = new FakeEvolutionToolFilterHandler();
    var runtime = new DeepSeekChatAgentRuntime(new HttpClient(handler), runtimeEvidence);
    var allowedTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "list_workspace_files",
        "read_text_file",
        "write_text_file",
        "replace_text_in_file"
    };
    var result = await runtime.RunAsync(
        new AgentRunRequest(
            "evolution-tool-filter",
            "Improve SKILL.md in this isolated plugin sandbox.",
            @"D:\Agent",
            "ds-test",
            "deepseek",
            "deepseek-v4-flash",
            AgentExecutionMode.Build,
            AllowParallelDelegation: false,
            AgentPackId: null,
            AllowedToolNames: allowedTools),
        _ => Task.CompletedTask,
        _ => throw new Exception("No tool was executed in the definition-filter smoke."),
        CancellationToken.None);

    Expect(result.FinalText == "Evolution tool boundary ready.",
        "Evolution tool-filter runtime did not complete.");
    Expect(handler.ToolNames.SetEquals(allowedTools),
        "Evolution runtime exposed unrelated workspace, memory or orchestration tools.");
});

await CheckAsync("Kimi transient stream recovery", async () =>
{
    var handler = new FakeTransientKimiHandler();
    var runtime = new DeepSeekChatAgentRuntime(new HttpClient(handler), runtimeEvidence);
    var events = new List<AgentRuntimeEvent>();
    var result = await runtime.RunAsync(
        new AgentRunRequest(
            "kimi-reconnect",
            "在连接波动后继续完成",
            @"D:\Agent",
            "moonshot-test",
            "kimi",
            "kimi-k3"),
        item =>
        {
            events.Add(item);
            return Task.CompletedTask;
        },
        _ => throw new Exception("Kimi reconnect smoke unexpectedly requested approval."),
        CancellationToken.None);

    Expect(handler.RequestCount == 2, "Kimi stream was not retried after a transient transport failure.");
    Expect(result.FinalText == "连接恢复后完成。", "Recovered Kimi stream did not return its final text.");
    Expect(
        events.Any(item => item.Action == "流式重连"),
        "Kimi transport recovery was not exposed as a visible runtime event.");
});

await CheckAsync("Ollama OpenAI-compatible Agent runtime", async () =>
{
    var handler = new FakeOllamaCompatibleHandler();
    var runtime = new DeepSeekChatAgentRuntime(new HttpClient(handler), runtimeEvidence);
    var result = await runtime.RunAsync(
        new AgentRunRequest(
            "ollama-smoke",
            "检查本地模型 Agent 工具协议",
            @"D:\Agent",
            string.Empty,
            "ollama",
            "qwen3:8b",
            AgentExecutionMode.Build,
            Endpoint: "http://127.0.0.1:11434/v1/chat/completions"),
        _ => Task.CompletedTask,
        _ => throw new Exception("Ollama no-tool smoke unexpectedly requested approval."),
        CancellationToken.None);

    Expect(result.Provider == "ollama", "Ollama provider metadata was not retained.");
    Expect(result.FinalText == "Ollama Agent 通道已连接。", "Ollama SSE response was not assembled.");
    Expect(handler.UsedConfiguredEndpoint, "Ollama did not use the configured OpenAI-compatible endpoint.");
    Expect(handler.AuthorizationWasOmitted, "Ollama received an unnecessary Authorization header.");
    Expect(handler.ThinkingExtensionWasOmitted, "Ollama received a provider-specific thinking extension.");
});

await CheckAsync("Ollama native API Agent runtime", async () =>
{
    var handler = new FakeOllamaNativeHandler();
    var runtime = new DeepSeekChatAgentRuntime(new HttpClient(handler), runtimeEvidence);
    var result = await runtime.RunAsync(
        new AgentRunRequest(
            "ollama-native-smoke",
            "Verify the native Ollama chat protocol.",
            @"D:\Agent",
            string.Empty,
            "ollama",
            "openbmb/minicpm5:latest",
            AgentExecutionMode.Build,
            Endpoint: "http://127.0.0.1:11434/api/chat"),
        _ => Task.CompletedTask,
        _ => throw new Exception("Ollama native smoke unexpectedly requested approval."),
        CancellationToken.None);

    Expect(result.Provider == "ollama", "Ollama native provider metadata was not retained.");
    Expect(result.FinalText == "Ollama native API connected.", "Ollama NDJSON response was not assembled.");
    Expect(handler.UsedNativeEndpoint, "Ollama native endpoint was not used.");
    Expect(handler.UsedNativeRequestShape, "Ollama native request fields were not normalized.");
    Expect(handler.UsedExpandedContextWindow, "Ollama native request did not reserve an expanded context window.");
    Expect(handler.AcceptedNdjson, "Ollama native request did not advertise NDJSON.");
    Expect(handler.AuthorizationWasOmitted, "Ollama native request received an unnecessary Authorization header.");
});

await CheckAsync("Kimi multimodal API and bounded attachments", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-kimi-attachments-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporaryDirectory);
    try
    {
        var imagePath = Path.Combine(temporaryDirectory, "screen.png");
        var textPath = Path.Combine(temporaryDirectory, "context.md");
        var pdfPath = Path.Combine(temporaryDirectory, "market-report.pdf");
        var wordPath = Path.Combine(temporaryDirectory, "requirements.docx");
        await File.WriteAllBytesAsync(
            imagePath,
            [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0]);
        await File.WriteAllTextAsync(textPath, "# 交付要求\n保留本地隐私边界。");

        await File.WriteAllBytesAsync(pdfPath, CreateMinimalPdf("PDF ATTACHMENT EVIDENCE"));
        using (var word = WordprocessingDocument.Create(
                   wordPath,
                   WordprocessingDocumentType.Document))
        {
            var mainPart = word.AddMainDocumentPart();
            mainPart.Document = new Document(
                new Body(
                    new Paragraph(
                        new Run(
                            new Text("WORD ATTACHMENT REQUIREMENTS")))));
            mainPart.Document.Save();
        }

        var attachmentService = new InputAttachmentService(
            Path.Combine(temporaryDirectory, "persisted"));
        var selected = attachmentService.ValidateSelection(
            [imagePath, textPath, pdfPath, wordPath],
            []);
        Expect(
            selected.Count(item => item.Kind == AgentAttachmentKind.Document) == 2,
            "PDF and Word attachments were not classified as documents.");
        var attachments = await attachmentService.PersistAsync(
            "kimi-smoke",
            selected,
            CancellationToken.None);
        var handler = new FakeKimiMultimodalHandler();
        var runtime = new DeepSeekChatAgentRuntime(new HttpClient(handler), runtimeEvidence);
        var result = await runtime.RunAsync(
            new AgentRunRequest(
                "kimi-smoke",
                "结合截图检查界面",
                @"D:\Agent",
                "moonshot-test",
                "kimi",
                "kimi-k3",
                AgentExecutionMode.Build,
                Attachments: attachments),
            _ => Task.CompletedTask,
            _ => throw new Exception("Kimi multimodal smoke unexpectedly requested approval."),
            CancellationToken.None);

        Expect(result.Provider == "kimi", "Kimi provider metadata was not retained.");
        Expect(result.FinalText == "Kimi 已理解图片和文件。", "Kimi SSE response was not assembled.");
        Expect(handler.UsedMoonshotEndpoint, "Kimi request did not use the Moonshot API endpoint.");
        Expect(handler.ContainedImageDataUrl, "Kimi request did not contain an image_url data URL.");
        Expect(handler.ContainedTextAttachment, "Kimi request did not contain the selected text file.");
        Expect(handler.UsedKimiTokenField, "Kimi request did not use max_completion_tokens.");

        var modelContent = await InputAttachmentService.BuildChatContentAsync(
            "Review the attached documents.",
            "kimi",
            attachments,
            CancellationToken.None);
        var serializedContent = modelContent.ToJsonString();
        Expect(
            serializedContent.Contains("PDF ATTACHMENT EVIDENCE", StringComparison.Ordinal)
            && serializedContent.Contains("WORD ATTACHMENT REQUIREMENTS", StringComparison.Ordinal)
            && serializedContent.Contains("market-report.pdf", StringComparison.Ordinal),
            "PDF or Word text was not extracted into model-readable content.");

        var settingsXaml = await File.ReadAllTextAsync(
            @"D:\Agent\NovaDesktop\SettingsWindow.xaml");
        var mainXaml = await File.ReadAllTextAsync(
            @"D:\Agent\NovaDesktop\MainWindow.xaml");
        Expect(
            settingsXaml.Contains("Tag=\"kimi\"", StringComparison.Ordinal)
            && mainXaml.Contains("Click=\"Attachment_Click\"", StringComparison.Ordinal)
            && mainXaml.Contains("ItemsSource=\"{Binding PendingAttachments}\"", StringComparison.Ordinal)
            && mainXaml.Contains("Drop=\"Composer_Drop\"", StringComparison.Ordinal),
            "Kimi or the native attachment interaction is missing from the desktop UI.");
    }
    finally
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
});

await CheckAsync("Build runtime continues beyond legacy twenty-round limit", async () =>
{
    var handler = new FakeLongDeepSeekHandler(toolRounds: 21);
    var runtime = new DeepSeekChatAgentRuntime(new HttpClient(handler), runtimeEvidence);
    var result = await runtime.RunAsync(
        new AgentRunRequest(
            "long-deepseek",
            "完成一个需要多轮工具读取的工程任务",
            @"D:\Agent",
            "ds-test",
            "deepseek",
            "deepseek-v4-flash",
            AgentExecutionMode.Build),
        _ => Task.CompletedTask,
        _ => throw new Exception("Read-only long loop unexpectedly requested approval."),
        CancellationToken.None);
    Expect(
        result.FinalText == "超过二十轮后正常完成。"
        && result.ToolCalls == 21
        && handler.RequestCount == 22,
        "Build runtime still terminated at the legacy 20-round shadow limit.");
});

await CheckAsync("mutating tool approval denial", async () =>
{
    var handler = new FakeWriteResponsesHandler();
    var runtime = new OpenAIResponsesAgentRuntime(new HttpClient(handler), runtimeEvidence);
    var approvals = 0;
    var result = await runtime.RunAsync(
        new AgentRunRequest("smoke-deny", "修改文件", @"D:\Agent", "sk-test", "openai", "gpt-5.6"),
        _ => Task.CompletedTask,
        _ =>
        {
            approvals++;
            return Task.FromResult(false);
        },
        CancellationToken.None);

    Expect(approvals == 1, "Mutating tool did not request approval exactly once.");
    Expect(handler.DenialWasContinued, "Denied tool result was not returned to the model.");
    Expect(result.FinalText == "已尊重用户拒绝。", "Final denial response was not parsed.");
});

await CheckAsync("side-effect intent commit and replay protection", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-side-effect-smoke-" + Guid.NewGuid().ToString("N"));
    var workspace = Path.Combine(temporaryDirectory, "workspace");
    var receiptRoot = Path.Combine(temporaryDirectory, "receipts");
    Directory.CreateDirectory(workspace);
    try
    {
        var receipts = new SideEffectReceiptService(receiptRoot);
        var host = new WorkspaceToolHost(
            workspace,
            taskId: "receipt-smoke",
            sideEffectReceipts: receipts);
        var arguments = new JsonObject
        {
            ["path"] = "result.txt",
            ["content"] = "exactly once"
        };
        var first = await host.ExecuteAsync(
            "write_text_file",
            arguments,
            "call-write-1",
            "call-write-1",
            CancellationToken.None);
        var resultPath = Path.Combine(workspace, "result.txt");
        var firstTimestamp = File.GetLastWriteTimeUtc(resultPath);
        await Task.Delay(25);
        var replay = await host.ExecuteAsync(
            "write_text_file",
            arguments,
            "call-write-1",
            "call-write-1",
            CancellationToken.None);
        var stored = receipts.LoadForTask("receipt-smoke");
        Expect(first == replay, "Committed replay did not return the original tool result.");
        Expect(
            File.GetLastWriteTimeUtc(resultPath) == firstTimestamp,
            "A committed side effect was executed twice.");
        Expect(
            stored.Count == 1
            && stored[0].State == SideEffectReceiptState.Committed
            && stored[0].BeforeFingerprint == "missing"
            && !string.IsNullOrWhiteSpace(stored[0].AfterFingerprint),
            "Intent/Commit receipt did not preserve the file transition.");

        var uncertain = await receipts.BeginAsync(
            "receipt-smoke",
            "call-uncertain-1",
            "call_mcp_tool",
            "external mutation",
            """{"value":1}""",
            "call-uncertain-1",
            null);
        Expect(
            !uncertain.IsCommittedReplay
            && uncertain.Receipt.State == SideEffectReceiptState.Intent,
            "The test could not establish an uncommitted intent.");
        try
        {
            await receipts.BeginAsync(
                "receipt-smoke",
                "call-uncertain-1",
                "call_mcp_tool",
                "external mutation",
                """{"value":1}""",
                "call-uncertain-1",
                null);
            throw new Exception("An uncommitted side effect was replayed automatically.");
        }
        catch (UncertainSideEffectException)
        {
        }
    }
    finally
    {
        DeleteGeneratedTestDirectory(temporaryDirectory);
    }
});

await CheckAsync("command escape flag blocking", async () =>
{
    var host = new WorkspaceToolHost(@"D:\Agent");
    try
    {
        await host.ExecuteAsync(
            "run_workspace_command",
            new JsonObject
            {
                ["executable"] = "rg",
                ["arguments"] = new JsonArray("--pre", "dangerous-command", "needle")
            },
            CancellationToken.None);
        throw new Exception("Dangerous rg --pre flag was accepted.");
    }
    catch (InvalidOperationException exception) when (exception.Message.Contains("escape", StringComparison.OrdinalIgnoreCase))
    {
    }
});

await CheckAsync("parallel read-only tool batch", async () =>
{
    var host = new WorkspaceToolHost(@"D:\Agent");
    var events = new List<AgentRuntimeEvent>();
    var calls = new[]
    {
        new AgentToolInvocation(
            "parallel-list",
            "list_workspace_files",
            new JsonObject { ["directory"] = "", ["max_depth"] = 1 }),
        new AgentToolInvocation(
            "parallel-read",
            "read_text_file",
            new JsonObject { ["path"] = "README.md", ["max_chars"] = 4000 })
    };
    var outputs = await ParallelToolExecutor.ExecuteReadOnlyBatchAsync(
        host,
        calls,
        item =>
        {
            lock (events)
            {
                events.Add(item);
            }
            return Task.CompletedTask;
        },
        _ => "测试执行单元",
        name => name,
        (name, _) => name,
        CancellationToken.None);

    Expect(outputs.Count == 2, "Parallel executor did not return both outputs.");
    Expect(outputs["parallel-list"].Contains("README.md", StringComparison.OrdinalIgnoreCase), "Parallel list output is missing.");
    Expect(outputs["parallel-read"].Contains("NOVA", StringComparison.Ordinal), "Parallel read output is missing.");
    Expect(events.Any(item => item.Kind == AgentRuntimeEventKind.ToolBatchStarted && item.ActiveUnits == 2), "No parallel tool batch start event.");
    Expect(events.Any(item => item.Kind == AgentRuntimeEventKind.ToolBatchCompleted), "No parallel tool batch completion event.");

    var askGovernor = new AgentResourceGovernor();
    askGovernor.BeginTask("ask-tool-batch", AgentExecutionMode.Ask);
    foreach (var runtimeEvent in events)
    {
        await askGovernor.ObserveRuntimeEventAsync(
            "ask-tool-batch",
            runtimeEvent,
            CancellationToken.None);
    }
    Expect(
        askGovernor.GetSnapshot().Policy.MaxConcurrentAgents == 1,
        "Ask mode did not retain its single-agent policy.");
    askGovernor.EndTask("ask-tool-batch");

    var rejectedRealAgentBatch = false;
    askGovernor.BeginTask("ask-agent-batch", AgentExecutionMode.Ask);
    try
    {
        await askGovernor.ObserveRuntimeEventAsync(
            "ask-agent-batch",
            new AgentRuntimeEvent(
                AgentRuntimeEventKind.BatchStarted,
                "多 Agent 编排器",
                "启动模型工作者",
                "两个真实子 Agent",
                0,
                2),
            CancellationToken.None);
    }
    catch (AgentBudgetExceededException)
    {
        rejectedRealAgentBatch = true;
    }
    finally
    {
        askGovernor.EndTask("ask-agent-batch");
    }
    Expect(
        rejectedRealAgentBatch,
        "Ask mode no longer rejected a real multi-agent batch.");
});

await CheckAsync("parallel model worker delegation", async () =>
{
    var handler = new FakeWorkerResponsesHandler();
    var orchestrator = new ParallelAgentOrchestrator(new HttpClient(handler));
    var events = new List<AgentRuntimeEvent>();
    var result = await orchestrator.ExecuteAsync(
        new AgentRunRequest("worker-smoke", "综合分析", @"D:\Agent", "sk-test", "openai", "gpt-5.6"),
        new JsonObject
        {
            ["tasks"] = new JsonArray
            {
                new JsonObject { ["title"] = "架构", ["instruction"] = "分析架构风险。" },
                new JsonObject { ["title"] = "体验", ["instruction"] = "分析交互机会。" }
            }
        },
        item =>
        {
            lock (events)
            {
                events.Add(item);
            }
            return Task.CompletedTask;
        },
        CancellationToken.None);

    Expect(handler.RequestCount == 2, "Parallel delegation did not create two model requests.");
    Expect(result.Contains("\"worker_count\":2", StringComparison.Ordinal), "Worker results were not merged.");
    var workerJson = JsonNode.Parse(result);
    Expect(
        workerJson?["results"]?.AsArray().All(
            item => item?["output"]?.GetValue<string>().Contains("工作者结果", StringComparison.Ordinal) == true) == true,
        "Worker output was not returned.");
    Expect(events.Any(item => item.Kind == AgentRuntimeEventKind.BatchStarted && item.ActiveUnits == 2), "No multi-agent batch event.");
    Expect(events.Any(item => item.Kind == AgentRuntimeEventKind.BatchCompleted), "No multi-agent merge event.");
});

await CheckAsync("desktop observation safety boundary", async () =>
{
    var service = new DesktopControlService();
    var windows = service.ListWindows();
    Expect(windows.Contains("\"count\"", StringComparison.Ordinal), "Desktop window observation returned invalid JSON.");

    var host = new WorkspaceToolHost(@"D:\Agent");
    Expect(!host.RequiresApproval("list_desktop_windows"), "Desktop observation should be read-only.");
    Expect(host.RequiresApproval("activate_desktop_window"), "Window activation must require approval.");
    Expect(host.RequiresApproval("open_browser_url"), "Browser opening must require approval.");
    Expect(host.RequiresApproval("type_text_to_window"), "Desktop text input must require approval.");
    Expect(host.RequiresApproval("send_window_key"), "Desktop key input must require approval.");
    Expect(host.RequiresApproval("delegate_parallel_tasks"), "Parallel model delegation must require approval.");
    Expect(host.RequiresApproval("schedule_agent_task"), "Schedule creation must require approval.");
    try
    {
        service.OpenBrowserUrl(new JsonObject { ["url"] = "http://example.com/unsafe" });
        throw new Exception("Non-HTTPS browser URL was accepted.");
    }
    catch (InvalidOperationException)
    {
    }
    await Task.CompletedTask;
});

await CheckAsync("desktop input validation before injection", async () =>
{
    var service = new DesktopControlService();
    try
    {
        await service.TypeTextAsync(
            new JsonObject { ["window_id"] = "0x0", ["text"] = "unsafe\ntext" },
            CancellationToken.None);
        throw new Exception("Control-character text was accepted.");
    }
    catch (InvalidOperationException exception) when (
        exception.Message.Contains("control", StringComparison.OrdinalIgnoreCase))
    {
    }

    try
    {
        await service.SendKeyAsync(
            new JsonObject { ["window_id"] = "0x0", ["key"] = "CTRL+ENTER" },
            CancellationToken.None);
        throw new Exception("Modifier shortcut was accepted.");
    }
    catch (InvalidOperationException exception) when (
        exception.Message.Contains("not enabled", StringComparison.OrdinalIgnoreCase))
    {
    }
});

await CheckAsync("MCP stdio initialize/list/call", async () =>
{
    var temporaryDirectory = Path.Combine(Path.GetTempPath(), "nova-mcp-smoke-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporaryDirectory);
    try
    {
        var assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var configPath = Path.Combine(temporaryDirectory, "mcp-servers.json");
        var config = new JsonObject
        {
            ["version"] = 1,
            ["servers"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "fixture",
                    ["command"] = "dotnet",
                    ["arguments"] = new JsonArray(assemblyPath, "--mcp-fixture"),
                    ["workingDirectory"] = @"D:\Agent",
                    ["enabled"] = true,
                    ["environmentVariables"] = new JsonObject()
                }
            }
        };
        await File.WriteAllTextAsync(configPath, config.ToJsonString());
        var registry = new McpRegistryService(configPath);
        var servers = registry.ListServers();
        Expect(servers.Contains("\"fixture\"", StringComparison.Ordinal), "MCP registry did not list fixture server.");

        var tools = await registry.InspectToolsAsync("fixture", @"D:\Agent", CancellationToken.None);
        Expect(tools.Contains("\"echo\"", StringComparison.Ordinal), "MCP tools/list did not return echo.");

        var result = await registry.CallToolAsync(
            "fixture",
            "echo",
            new JsonObject { ["text"] = "NOVA MCP ready" },
            @"D:\Agent",
            CancellationToken.None);
        Expect(result.Contains("NOVA MCP ready", StringComparison.Ordinal), "MCP tools/call result was not returned.");
    }
    finally
    {
        DeleteGeneratedTestDirectory(temporaryDirectory);
    }
});

await CheckAsync("MCP Streamable HTTP JSON/SSE", async () =>
{
    var temporaryDirectory = Path.Combine(Path.GetTempPath(), "nova-mcp-http-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporaryDirectory);
    try
    {
        var configPath = Path.Combine(temporaryDirectory, "mcp-servers.json");
        var config = new JsonObject
        {
            ["version"] = 1,
            ["servers"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "remote-fixture",
                    ["transport"] = "http",
                    ["url"] = "https://mcp.example.test/mcp",
                    ["enabled"] = true,
                    ["headers"] = new JsonObject()
                }
            }
        };
        await File.WriteAllTextAsync(configPath, config.ToJsonString());
        var handler = new FakeMcpHttpHandler();
        var registry = new McpRegistryService(configPath, new HttpClient(handler));

        var tools = await registry.InspectToolsAsync("remote-fixture", @"D:\Agent", CancellationToken.None);
        Expect(tools.Contains("\"remote_echo\"", StringComparison.Ordinal), "HTTP MCP SSE tools/list failed.");
        var result = await registry.CallToolAsync(
            "remote-fixture",
            "remote_echo",
            new JsonObject { ["text"] = "HTTP MCP ready" },
            @"D:\Agent",
            CancellationToken.None);
        Expect(result.Contains("HTTP MCP ready", StringComparison.Ordinal), "HTTP MCP tools/call failed.");
        Expect(handler.InitializedNotifications == 2, "HTTP MCP initialized notification was not sent for each session.");
        Expect(handler.SessionHeaderSeen, "HTTP MCP session header was not continued.");
        Expect(handler.ProtocolHeaderSeen, "HTTP MCP protocol version header was not sent.");
    }
    finally
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
});

await CheckAsync("MCP registry CRUD and validation", async () =>
{
    var temporaryDirectory = Path.Combine(Path.GetTempPath(), "nova-mcp-registry-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporaryDirectory);
    try
    {
        var registry = new McpRegistryService(Path.Combine(temporaryDirectory, "mcp.json"));
        var registration = new McpServerRegistration(
            "local-fixture",
            "dotnet",
            ["tool.dll"],
            @"D:\Agent",
            true,
            new Dictionary<string, string> { ["API_TOKEN"] = "NOVA_TEST_TOKEN" });
        await registry.UpsertAsync(registration, CancellationToken.None);
        Expect(registry.GetServers().Count == 1, "MCP upsert did not persist.");
        Expect(registry.GetEnabledServers().Count == 1, "MCP enabled state was not persisted.");

        await registry.SetEnabledAsync("local-fixture", false, CancellationToken.None);
        Expect(registry.GetEnabledServers().Count == 0, "MCP disable did not persist.");

        try
        {
            await registry.UpsertAsync(
                registration with { Name = "unsafe server", Command = @"C:\tool.exe" },
                CancellationToken.None);
            throw new Exception("Unsafe MCP registration was accepted.");
        }
        catch (InvalidOperationException)
        {
        }

        await registry.RemoveAsync("local-fixture", CancellationToken.None);
        Expect(registry.GetServers().Count == 0, "MCP remove did not persist.");
    }
    finally
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
});

await CheckAsync("MCP discovery sanitizes and imports disabled", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-mcp-discovery-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporaryDirectory);
    try
    {
        var jsonPath = Path.Combine(temporaryDirectory, "mcp.json");
        await File.WriteAllTextAsync(
            jsonPath,
            """
            {
              "mcpServers": {
                "safe-local": {
                  "command": "npx",
                  "args": ["-y", "@example/mcp"],
                  "env": {
                    "API_TOKEN": "${NOVA_TEST_TOKEN}",
                    "LITERAL_SECRET": "do-not-copy-this"
                  }
                },
                "safe-http": {
                  "type": "http",
                  "url": "https://mcp.example.test/mcp",
                  "headers": {
                    "Authorization": "%NOVA_TEST_AUTH%"
                  }
                },
                "unsafe-path": {
                  "command": "C:\\tools\\server.exe"
                }
              }
            }
            """);
        var tomlPath = Path.Combine(temporaryDirectory, "config.toml");
        await File.WriteAllTextAsync(
            tomlPath,
            """
            [mcp_servers.codex-local]
            command = "uvx"
            args = ["example-mcp"]

            [mcp_servers.codex-local.env]
            TOKEN = "${CODEX_MCP_TOKEN}"
            """);

        var registry = new McpRegistryService(Path.Combine(temporaryDirectory, "registry.json"));
        await registry.UpsertAsync(
            new McpServerRegistration(
                "safe-http",
                string.Empty,
                [],
                temporaryDirectory,
                false,
                new Dictionary<string, string>(),
                "http",
                "https://already.example.test/mcp"),
            CancellationToken.None);
        var discovery = new McpDiscoveryService(temporaryDirectory);
        var result = await discovery.DiscoverAsync(
            [
                new McpDiscoverySource("JSON Fixture", jsonPath, "json"),
                new McpDiscoverySource("Codex Fixture", tomlPath, "toml")
            ],
            registry.GetServers(),
            CancellationToken.None);

        Expect(result.ScannedPaths.Count == 2, "MCP discovery did not report both read files.");
        var safeLocal = result.Candidates.Single(item => item.Name == "safe-local");
        Expect(safeLocal.IsCompatible, "Safe stdio MCP was rejected.");
        Expect(safeLocal.MayAcquireSoftware, "npx acquisition risk was not disclosed.");
        Expect(safeLocal.OmittedSecretCount == 1, "Literal secret was not omitted.");
        Expect(
            safeLocal.Registration.EnvironmentVariables["API_TOKEN"] == "NOVA_TEST_TOKEN",
            "Environment reference was not converted to a name-only mapping.");
        Expect(
            !safeLocal.Registration.EnvironmentVariables.ContainsKey("LITERAL_SECRET"),
            "Literal MCP secret leaked into the candidate.");
        Expect(!safeLocal.Registration.Enabled, "Discovered MCP was unexpectedly enabled.");

        var safeHttp = result.Candidates.Single(item => item.Name == "safe-http");
        Expect(safeHttp.IsAlreadyRegistered && !safeHttp.CanImport, "Existing MCP was not protected from overwrite.");
        Expect(
            safeHttp.Registration.HttpHeaders?["Authorization"] == "NOVA_TEST_AUTH",
            "Header environment reference was not sanitized.");
        Expect(
            !result.Candidates.Single(item => item.Name == "unsafe-path").IsCompatible,
            "Absolute MCP command was accepted for automatic import.");
        Expect(
            result.Candidates.Single(item => item.Name == "codex-local")
                .Registration.EnvironmentVariables["TOKEN"] == "CODEX_MCP_TOKEN",
            "Codex TOML MCP environment mapping was not discovered.");

        var pastedRemote = discovery.PreviewConfiguration(
            """
            {
              "mcpServers": {
                "internet-mcp": {
                  "url": "https://mcp.example.test/mcp",
                  "headers": {
                    "Authorization": "${INTERNET_MCP_AUTH}"
                  }
                }
              }
            }
            """,
            registry.GetServers());
        Expect(pastedRemote.Count == 1 && pastedRemote[0].CanImport,
            "Pasted Internet MCP configuration was not accepted for review.");
        Expect(
            pastedRemote[0].Registration.HttpHeaders?["Authorization"] == "INTERNET_MCP_AUTH",
            "Pasted Internet MCP credential reference was not sanitized.");
        Expect(!pastedRemote[0].Registration.Enabled,
            "Pasted Internet MCP was enabled before user confirmation.");
        var documentationLink = discovery.PreviewConfiguration(
            "https://github.com/example/example-mcp-server",
            registry.GetServers());
        Expect(documentationLink.Count == 1 && !documentationLink[0].CanImport,
            "Repository documentation URL was mistaken for a live MCP endpoint.");

        await registry.UpsertAsync(
            safeLocal.Registration with { Enabled = false },
            CancellationToken.None);
        Expect(
            registry.GetServers().Single(item => item.Name == "safe-local").Enabled == false,
            "Imported discovery did not remain disabled.");
    }
    finally
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
});

await CheckAsync("MCP consent UI and quick-start onboarding", async () =>
{
    var extensionXaml = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\ExtensionCenterWindow.xaml");
    var extensionCode = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\ExtensionCenterWindow.xaml.cs");
    var quickStart = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\QuickStartWindow.xaml");
    Expect(
        extensionXaml.Contains("发现与导入", StringComparison.Ordinal)
        && extensionXaml.Contains("授权导入所选", StringComparison.Ordinal)
        && extensionXaml.Contains("MCP 与 Skills 能力集市", StringComparison.Ordinal)
        && extensionXaml.Contains("由你掌印", StringComparison.Ordinal),
        "MCP discovery, marketplace, or consent surface is missing.");
    Expect(
        !extensionXaml.Contains("ContentSource=\"Header\"", StringComparison.Ordinal)
        && extensionXaml.Contains("Content=\"{TemplateBinding Content}\"", StringComparison.Ordinal),
        "Extension Center ListBoxItem template binds a non-existent Header property.");
    Expect(
        extensionCode.Contains("授权只读扫描 MCP 配置", StringComparison.Ordinal)
        && extensionCode.Contains("Enabled = false", StringComparison.Ordinal)
        && extensionCode.Contains("授权测试 MCP 连接", StringComparison.Ordinal)
        && extensionCode.Contains("由你确认加载 MCP", StringComparison.Ordinal)
        && extensionCode.Contains("由你确认加载 Skill", StringComparison.Ordinal),
        "MCP scan/import/test/marketplace permission boundaries are missing.");
    Expect(
        quickStart.Contains("先完成一个小闭环", StringComparison.Ordinal)
        && quickStart.Contains("NOVA 的差异化工作方式", StringComparison.Ordinal),
        "First-run learning path or NOVA differentiation is missing.");
});

await CheckAsync("Skill install/read/toggle/uninstall", async () =>
{
    var temporaryDirectory = Path.Combine(Path.GetTempPath(), "nova-skills-" + Guid.NewGuid().ToString("N"));
    var source = Path.Combine(temporaryDirectory, "source");
    var installedRoot = Path.Combine(temporaryDirectory, "installed");
    Directory.CreateDirectory(Path.Combine(source, "references"));
    await File.WriteAllTextAsync(
        Path.Combine(source, "SKILL.md"),
        """
        ---
        name: release-auditor
        description: Verify a native desktop release.
        ---
        Inspect the build outputs and report evidence.
        """);
    await File.WriteAllTextAsync(Path.Combine(source, "references", "checklist.md"), "Build\nTest\nPackage");
    try
    {
        var registry = new SkillRegistryService(installedRoot);
        var installed = await registry.InstallFromFolderAsync(source, CancellationToken.None);
        Expect(installed.Name == "release-auditor", "Skill frontmatter name was not parsed.");
        Expect(registry.GetSkills().Single().Enabled, "Installed skill was not enabled.");
        Expect(registry.ReadInstructions(installed.Id).Contains("Inspect the build outputs", StringComparison.Ordinal), "Skill instructions were not readable.");

        var host = new WorkspaceToolHost(@"D:\Agent", skillRegistry: registry);
        var listed = await host.ExecuteAsync("list_installed_skills", new JsonObject(), CancellationToken.None);
        Expect(listed.Contains("release-auditor", StringComparison.Ordinal), "Skill was not exposed to the agent tool layer.");

        await registry.SetEnabledAsync(installed.Id, false, CancellationToken.None);
        Expect(!registry.GetSkills().Single().Enabled, "Skill disable did not persist.");
        Expect(!registry.ListForModel().Contains("release-auditor", StringComparison.Ordinal), "Disabled skill remained visible to the model.");

        await registry.UninstallAsync(installed.Id, CancellationToken.None);
        Expect(registry.GetSkills().Count == 0, "Skill uninstall did not remove the managed copy.");
        Expect(File.Exists(Path.Combine(source, "SKILL.md")), "Skill uninstall changed the original source folder.");
    }
    finally
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
});

await CheckAsync("Living Memory habits and safe Skill distillation", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-living-memory-" + Guid.NewGuid().ToString("N"));
    var taskRoot = Path.Combine(temporaryDirectory, "tasks");
    var conversationRoot = Path.Combine(temporaryDirectory, "conversations");
    var memoryRoot = Path.Combine(temporaryDirectory, "memory");
    var skillRoot = Path.Combine(temporaryDirectory, "skills");
    Directory.CreateDirectory(temporaryDirectory);
    try
    {
        var snapshots = new TaskSnapshotService(taskRoot);
        var conversations = new ConversationHistoryService(conversationRoot);
        for (var index = 1; index <= 3; index++)
        {
            var task = new TaskItem
            {
                Id = $"living-{index}",
                Title = $"完整交付 {index}",
                Description = index == 1
                    ? "继续任务，完成后落盘并验证"
                    : "直接做完整交付，运行测试并给出证据",
                WorkspaceRoot = @"D:\Agent",
                Provider = "deepseek",
                Model = "deepseek-v4-flash",
                ExecutionMode = AgentExecutionMode.Build,
                State = TaskState.Completed,
                Progress = 100,
                Stage = "完成"
            };
            await snapshots.SaveAsync(task);
            await conversations.AppendAsync(
                task.Id,
                "user",
                index == 1 ? "继续，落盘后验证" : "不要只说明，直接测试并交付");
        }

        var memory = new LivingMemoryService(
            snapshots,
            conversations,
            memoryRoot);
        var analyzed = await memory.AnalyzeAsync();
        Expect(analyzed.TasksAnalyzed == 3, "Living Memory did not inspect local task history.");
        Expect(
            analyzed.Habits.Any(habit => habit.Id == "result-first"),
            "Repeated result-first preference was not proposed.");
        var selected = analyzed.Habits.First(habit => habit.Id == "result-first");
        var accepted = await memory.SetHabitStateAsync(
            selected.Id,
            LearningCandidateState.Accepted);
        Expect(
            accepted.Habits.Single(habit => habit.Id == selected.Id).State
            == LearningCandidateState.Accepted,
            "User-confirmed habit state did not persist.");
        Expect(
            memory.BuildProfilePrompt().Contains(
                "先给出真实结果",
                StringComparison.Ordinal),
            "Accepted habit was not compiled into the runtime profile.");

        var distilled = await memory.DistillSkillAsync();
        var candidate = distilled.SkillCandidates.Single();
        Expect(
            candidate.Instructions.Contains(
                "不得扩大文件、桌面、网络",
                StringComparison.Ordinal),
            "Distilled Skill omitted the non-escalation boundary.");
        var registry = new SkillRegistryService(skillRoot);
        var installedState = await memory.InstallSkillAsync(
            candidate.Id,
            registry);
        Expect(
            installedState.SkillCandidates.Single().Installed
            && registry.GetSkills().Single().Enabled,
            "Confirmed personal Skill was not installed through the managed registry.");

        var toolHost = new WorkspaceToolHost(@"D:\Agent");
        Expect(
            toolHost.Definitions.Any(definition =>
                definition["name"]?.GetValue<string>() == "click_window_point"),
            "Desktop Pilot bounded click tool is missing.");
        Expect(
            toolHost.RequiresApproval("click_window_point"),
            "Desktop pointer injection no longer requires approval.");
    }
    finally
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
});

await CheckAsync("Evolution Lab plugin-only budget and guarded installation", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-evolution-" + Guid.NewGuid().ToString("N"));
    var contextWorkspace = Path.Combine(temporaryDirectory, "context");
    var labRoot = Path.Combine(temporaryDirectory, "lab");
    var skillRoot = Path.Combine(temporaryDirectory, "skills");
    Directory.CreateDirectory(contextWorkspace);
    try
    {
        await File.WriteAllTextAsync(
            Path.Combine(contextWorkspace, "private-core.cs"),
            "THIS MUST NEVER ENTER THE PLUGIN SANDBOX");
        var registry = new SkillRegistryService(skillRoot);
        var lab = new EvolutionLabService(labRoot, registry);
        Expect(
            !lab.GetSnapshot().Policy.Enabled,
            "Plugin self-evolution must be disabled by default.");
        try
        {
            await lab.ProposeAsync(contextWorkspace, "Improve task continuity without core changes.");
            throw new Exception("Disabled Evolution Lab accepted a new experiment.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("关闭", StringComparison.Ordinal))
        {
        }

        var configured = await lab.ConfigureAsync(
            enabled: true,
            scheduledDiscoveryEnabled: false,
            maxTokensPerExperiment: 8_000,
            monthlyTokenBudget: 16_000,
            maxExperimentsPerWeek: 2,
            maxModelRounds: 2);
        Expect(
            configured.Policy.Enabled
            && !configured.Policy.ScheduledDiscoveryEnabled
            && configured.RemainingTokensThisMonth == 16_000,
            "Evolution Lab hard-budget policy did not persist.");

        var proposed = await lab.ProposeAsync(
            contextWorkspace,
            "Improve task continuity without changing NOVA core.");
        var experiment = proposed.Experiments.Single();
        Expect(
            experiment.State == EvolutionExperimentState.Proposed
            && experiment.IsolatedWorkspace is null,
            "Evolution proposal touched a plugin workspace before approval.");

        var prepared = await lab.PrepareAsync(experiment.Id);
        experiment = prepared.Experiments.Single();
        Expect(
            experiment.State == EvolutionExperimentState.Ready
            && Directory.Exists(experiment.IsolatedWorkspace)
            && experiment.BaselineHashes.Count == 4,
            "Evolution Lab did not create a bounded declarative plugin sandbox.");
        Expect(
            !Directory.EnumerateFiles(
                    experiment.IsolatedWorkspace!,
                    "*",
                    SearchOption.AllDirectories)
                .Any(path => File.ReadAllText(path).Contains(
                    "THIS MUST NEVER ENTER THE PLUGIN SANDBOX",
                    StringComparison.Ordinal)),
            "Plugin sandbox exposed or copied private core source.");

        var runtimeBudget = await lab.ReserveRuntimeBudgetAsync(
            experiment.IsolatedWorkspace!);
        Expect(
            runtimeBudget is
            {
                MaxModelRounds: 2,
                MaxTokensPerRequest: 4000,
                ReservedTokens: 8000
            }
            && lab.GetSnapshot().UsedTokensThisMonth == 8_000,
            "Evolution model run did not reserve the hard Token budget.");
        try
        {
            await lab.ReserveRuntimeBudgetAsync(experiment.IsolatedWorkspace!);
            throw new Exception("Evolution experiment exceeded its one-run Token envelope.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("已经整笔预留", StringComparison.Ordinal))
        {
        }

        await File.AppendAllTextAsync(
            Path.Combine(experiment.IsolatedWorkspace!, "SKILL.md"),
            "\n- 保持任务上下文，并在需要新权限时请求人工确认。\n");
        var evaluated = await lab.EvaluateAsync(experiment.Id);
        experiment = evaluated.Experiments.Single();
        Expect(
            experiment.State == EvolutionExperimentState.Passed
            && experiment.VerificationPassed == true
            && experiment.ChangedFiles.Single().Path == "SKILL.md",
            "Declarative plugin did not produce verified, reviewable evidence.");

        var adopted = await lab.AdoptAsync(experiment.Id);
        experiment = adopted.Experiments.Single();
        Expect(
            experiment.State == EvolutionExperimentState.Adopted
            && registry.GetSkills().Single().Enabled
            && await File.ReadAllTextAsync(Path.Combine(contextWorkspace, "private-core.cs"))
            == "THIS MUST NEVER ENTER THE PLUGIN SANDBOX",
            "Verified plugin was not installed through the managed, core-safe registry.");

        var unsafeProposal = await lab.ProposeAsync(
            contextWorkspace,
            "Try a second bounded plugin experiment for security validation.");
        var unsafeExperiment = unsafeProposal.Experiments.First(item =>
            item.State == EvolutionExperimentState.Proposed);
        var unsafePrepared = await lab.PrepareAsync(unsafeExperiment.Id);
        unsafeExperiment = unsafePrepared.Experiments.First(item =>
            item.Id == unsafeExperiment.Id);
        var unsafeRuntimeBudget = await lab.ReserveRuntimeBudgetAsync(
            unsafeExperiment.IsolatedWorkspace!);
        Expect(
            unsafeRuntimeBudget is { ReservedTokens: 8000 }
            && lab.GetSnapshot().UsedTokensThisMonth == 16_000,
            "Second Evolution run did not reserve its bounded model budget.");
        await File.WriteAllTextAsync(
            Path.Combine(unsafeExperiment.IsolatedWorkspace!, "payload.js"),
            "require('child_process').exec('whoami')");
        var unsafeResult = await lab.EvaluateAsync(unsafeExperiment.Id);
        unsafeExperiment = unsafeResult.Experiments.First(item =>
            item.Id == unsafeExperiment.Id);
        Expect(
            unsafeExperiment.State == EvolutionExperimentState.Failed
            && unsafeExperiment.Blockers.Any(item =>
                item.Contains("未授权文件", StringComparison.Ordinal)),
            "Evolution Lab accepted executable plugin content.");

        await lab.ConfigureAsync(
            enabled: true,
            scheduledDiscoveryEnabled: false,
            maxTokensPerExperiment: 8_000,
            monthlyTokenBudget: 24_000,
            maxExperimentsPerWeek: 2,
            maxModelRounds: 2);
        var retryBudget = await lab.ReserveRuntimeBudgetAsync(
            unsafeExperiment.IsolatedWorkspace!);
        Expect(
            retryBudget is { ReservedTokens: 8000 }
            && lab.GetSnapshot().UsedTokensThisMonth == 24_000
            && lab.GetSnapshot().Experiments.First(item => item.Id == unsafeExperiment.Id).State
               == EvolutionExperimentState.Running,
            "A failed Evolution experiment could not be retried within the monthly budget.");
    }
    finally
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
});

await CheckAsync("Evolution Lab scheduled discovery is local throttled and deduplicated", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-evolution-discovery-" + Guid.NewGuid().ToString("N"));
    var contextWorkspace = Path.Combine(temporaryDirectory, "context");
    var labRoot = Path.Combine(temporaryDirectory, "lab");
    Directory.CreateDirectory(contextWorkspace);
    try
    {
        var lab = new EvolutionLabService(
            labRoot,
            new SkillRegistryService(Path.Combine(temporaryDirectory, "skills")));
        var configured = await lab.ConfigureAsync(
            enabled: true,
            scheduledDiscoveryEnabled: true,
            maxTokensPerExperiment: 8_000,
            monthlyTokenBudget: 16_000,
            maxExperimentsPerWeek: 3,
            maxModelRounds: 2);
        var discoveryAt = configured.Policy.UpdatedAt.AddMinutes(11);
        var tasks = new[]
        {
            new TaskSnapshot(
                "failed-task",
                "Recover a stalled build",
                "Resume the project from its last safe checkpoint.",
                contextWorkspace,
                "deepseek",
                "deepseek-v4-flash",
                TaskState.Failed,
                52,
                "Build failed",
                discoveryAt.AddDays(-2),
                discoveryAt.AddMinutes(-3)),
            new TaskSnapshot(
                "completed-task",
                "Continue the same project",
                "Keep the verified context and finish the remaining work.",
                contextWorkspace,
                "deepseek",
                "deepseek-v4-flash",
                TaskState.Completed,
                100,
                "Delivered",
                discoveryAt.AddDays(-1),
                discoveryAt.AddMinutes(-2))
        };

        var discovered = await lab.TryDiscoverCandidateAsync(tasks, discoveryAt);
        Expect(
            discovered.Scanned
            && discovered.Candidate is
            {
                State: EvolutionExperimentState.Proposed,
                IsolatedWorkspace: null
            }
            && discovered.Candidate.Evidence.Contains("定时本地发现", StringComparison.Ordinal)
            && discovered.Snapshot.UsedTokensThisMonth == 0
            && discovered.Snapshot.LastDiscoveryCandidateId == discovered.Candidate.Id
            && discovered.Snapshot.NextDiscoveryAt == discoveryAt.AddHours(6),
            "Scheduled discovery did not create a zero-Token local-only candidate.");
        Expect(
            !Directory.Exists(Path.Combine(labRoot, "plugin-workspaces")),
            "Scheduled discovery touched a plugin sandbox before user review.");

        var throttled = await lab.TryDiscoverCandidateAsync(
            tasks,
            discoveryAt.AddHours(1));
        Expect(
            !throttled.Scanned && throttled.Candidate is null,
            "Scheduled discovery ignored its six-hour throttle.");

        var pendingReview = await lab.TryDiscoverCandidateAsync(
            tasks,
            discoveryAt.AddHours(6).AddMinutes(1));
        Expect(
            pendingReview.Scanned
            && pendingReview.Candidate is null
            && pendingReview.Snapshot.Experiments.Count == 1
            && pendingReview.Snapshot.DiscoveryStatus.Contains(
                "等待现有候选",
                StringComparison.Ordinal),
            "Scheduled discovery stacked experiments while review was pending.");

        await lab.RejectAsync(discovered.Candidate!.Id);
        var duplicate = await lab.TryDiscoverCandidateAsync(
            tasks,
            discoveryAt.AddHours(12).AddMinutes(2));
        Expect(
            duplicate.Scanned
            && duplicate.Candidate is null
            && duplicate.Snapshot.Experiments.Count == 1
            && duplicate.Snapshot.DiscoveryStatus.Contains(
                "自动去重",
                StringComparison.Ordinal),
            "Scheduled discovery recreated an unchanged rejected signal.");
    }
    finally
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
});

await CheckAsync("Skill executable blocking", async () =>
{
    var temporaryDirectory = Path.Combine(Path.GetTempPath(), "nova-skill-block-" + Guid.NewGuid().ToString("N"));
    var source = Path.Combine(temporaryDirectory, "source");
    Directory.CreateDirectory(source);
    await File.WriteAllTextAsync(Path.Combine(source, "SKILL.md"), "---\nname: unsafe-skill\n---\nNever run this.");
    await File.WriteAllBytesAsync(Path.Combine(source, "payload.exe"), [0x4D, 0x5A]);
    try
    {
        var registry = new SkillRegistryService(Path.Combine(temporaryDirectory, "installed"));
        try
        {
            await registry.InstallFromFolderAsync(source, CancellationToken.None);
            throw new Exception("Executable skill payload was accepted.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("blocked", StringComparison.OrdinalIgnoreCase))
        {
        }
    }
    finally
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
});

await CheckAsync("Capability Compass task-aware minimal mounting", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-capability-compass-" + Guid.NewGuid().ToString("N"));
    var workspace = Path.Combine(temporaryDirectory, "workspace");
    var skillSource = Path.Combine(temporaryDirectory, "document-skill");
    Directory.CreateDirectory(workspace);
    Directory.CreateDirectory(skillSource);
    await File.WriteAllTextAsync(
        Path.Combine(workspace, "package.json"),
        """{"name":"compass-test"}""");
    await File.WriteAllTextAsync(
        Path.Combine(skillSource, "SKILL.md"),
        """
        ---
        name: document-craft
        description: Create and inspect Word, PDF, Excel and spreadsheet deliverables.
        ---
        Inspect document structure before editing.
        """);
    try
    {
        var mcpRegistry = new McpRegistryService(
            Path.Combine(temporaryDirectory, "mcp.json"));
        await mcpRegistry.UpsertAsync(
            new McpServerRegistration(
                "github",
                "npx",
                ["-y", "@modelcontextprotocol/server-github"],
                workspace,
                false,
                new Dictionary<string, string>()),
            CancellationToken.None);
        var skillRegistry = new SkillRegistryService(
            Path.Combine(temporaryDirectory, "skills"));
        var documentSkill = await skillRegistry.InstallFromFolderAsync(
            skillSource,
            CancellationToken.None);
        await skillRegistry.SetEnabledAsync(
            documentSkill.Id,
            false,
            CancellationToken.None);

        var compass = new CapabilityCompassService(mcpRegistry, skillRegistry);
        var githubReport = compass.Analyze(
            "检查 GitHub issue 并整理这个 Node 项目的修复方案",
            workspace);
        var github = githubReport.Recommendations.Single(item =>
            item.Id.Equals("github", StringComparison.OrdinalIgnoreCase));
        Expect(
            github.Action == CapabilityAction.EnableMcp && !github.IsReady,
            "Disabled GitHub MCP was silently treated as mounted.");
        Expect(
            githubReport.WorkspaceSignal.Contains("Node", StringComparison.Ordinal),
            "Workspace manifest signals were not included in capability routing.");
        Expect(
            !CapabilityCompassService.FormatForPrompt(githubReport)
                .Contains("MCP github:", StringComparison.Ordinal),
            "Disabled capability leaked into the approved runtime mount.");

        var documentReport = compass.Analyze(
            "把这份材料制作成 PDF 和 Word 文档",
            workspace);
        var document = documentReport.Recommendations.Single(item =>
            item.Id.Equals(documentSkill.Id, StringComparison.OrdinalIgnoreCase));
        Expect(
            document.Action == CapabilityAction.EnableSkill && !document.IsReady,
            "Relevant disabled Skill was not recommended behind approval.");

        await mcpRegistry.SetEnabledAsync("github", true, CancellationToken.None);
        var enabledReport = compass.Analyze("处理 GitHub issue", workspace);
        Expect(
            enabledReport.Recommendations.Any(item =>
                item.Id == "github" && item.IsReady && item.Action == CapabilityAction.Ready),
            "Approved MCP did not become a ready task capability.");

        var host = new WorkspaceToolHost(
            workspace,
            mcpRegistry: mcpRegistry,
            skillRegistry: skillRegistry);
        var toolResult = await host.ExecuteAsync(
            "recommend_task_capabilities",
            new JsonObject { ["objective"] = "处理 GitHub issue" },
            CancellationToken.None);
        Expect(
            toolResult.Contains("\"ReadyCount\":1", StringComparison.Ordinal),
            "Capability Compass was not available through the read-only agent tool layer.");
    }
    finally
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
});

await CheckAsync("Capability marketplace safe one-click loading", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-capability-market-" + Guid.NewGuid().ToString("N"));
    var workspace = Path.Combine(temporaryDirectory, "workspace");
    Directory.CreateDirectory(workspace);
    try
    {
        var mcpRegistry = new McpRegistryService(
            Path.Combine(temporaryDirectory, "mcp.json"));
        var skillRegistry = new SkillRegistryService(
            Path.Combine(temporaryDirectory, "skills"));
        var marketplace = new CapabilityMarketplaceService(
            mcpRegistry,
            skillRegistry,
            workspace);
        var catalog = marketplace.GetCatalog();

        Expect(
            catalog.Count(item => item.Kind == MarketplaceCapabilityKind.Mcp) >= 3
            && catalog.Count(item => item.Kind == MarketplaceCapabilityKind.Skill) >= 4,
            "Marketplace did not expose both MCP and Skill shelves.");
        var github = catalog.Single(item => item.Id == "github-official");
        var githubRegistration = github.McpRegistration
                                 ?? throw new Exception("Official GitHub MCP template was missing.");
        Expect(
            githubRegistration is
            {
                Enabled: false,
                Command: "docker"
            },
            "Official GitHub MCP was not registered as a disabled Docker template.");
        Expect(
            githubRegistration.EnvironmentVariables["GITHUB_PERSONAL_ACCESS_TOKEN"]
            == "GITHUB_PAT",
            "GitHub marketplace entry did not use a name-only secret mapping.");
        Expect(
            !githubRegistration.EnvironmentVariables.Values.Any(value =>
                value.StartsWith("ghp_", StringComparison.OrdinalIgnoreCase)),
            "A literal GitHub credential leaked into the marketplace.");

        var playwright = catalog.Single(item => item.Id == "playwright-official");
        var playwrightRegistration = playwright.McpRegistration
                                     ?? throw new Exception("Playwright MCP template was missing.");
        Expect(
            playwrightRegistration.Arguments.Contains("--headless")
            && playwrightRegistration.Arguments.Contains("--isolated"),
            "Playwright marketplace entry does not default to isolated background operation.");

        var engineering = catalog.Single(item => item.Id == "engineering-closure");
        var definition = engineering.SkillDefinition
                         ?? throw new Exception("Bundled Skill definition was missing.");
        await skillRegistry.InstallBundledAsync(
            definition.Id,
            definition.Instructions,
            CancellationToken.None);
        var refreshed = marketplace.GetCatalog().Single(item =>
            item.Id == "engineering-closure");
        Expect(
            refreshed.IsInstalled && refreshed.IsEnabled && refreshed.ActionLabel == "查看能力",
            "One-click bundled Skill installation did not refresh to a loaded state.");
        Expect(
            skillRegistry.GetSkills().Single(skill => skill.Id == definition.Id)
                .Description.Contains("真实", StringComparison.Ordinal)
            && skillRegistry.ReadInstructions(definition.Id)
                .Contains("engineering-closure", StringComparison.Ordinal),
            "Bundled engineering Skill metadata or instructions were not installed intact.");
    }
    finally
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
});

await CheckAsync("background web research approval and private-network guard", async () =>
{
    var publicPage = BackgroundWebResearchService.ParsePublicHttpsUri(
        "https://example.com/research");
    Expect(publicPage.Host == "example.com", "Public HTTPS research URL was rejected.");

    foreach (var refused in new[]
             {
                 "http://example.com",
                 "https://localhost/private",
                 "https://127.0.0.1/admin",
                 "https://user:pass@example.com/"
             })
    {
        try
        {
            BackgroundWebResearchService.ParsePublicHttpsUri(refused);
            throw new Exception($"Unsafe background research URL was accepted: {refused}");
        }
        catch (InvalidOperationException)
        {
        }
    }

    var host = new WorkspaceToolHost(@"D:\Agent");
    Expect(
        host.RequiresApproval("fetch_public_web_page"),
        "Background research was not placed behind user approval.");
    var approval = host.CreateApprovalRequest(
        "fetch_public_web_page",
        new JsonObject { ["url"] = "https://example.com/research" });
    Expect(
        approval.Title.Contains("example.com", StringComparison.Ordinal)
        && approval.Description.Contains("不会打开本地浏览器", StringComparison.Ordinal)
        && approval.Description.Contains("访问内网", StringComparison.Ordinal),
        "Background research approval does not clearly disclose domain and no-browser behavior.");
    await Task.CompletedTask;
});

await CheckAsync("productivity summary from local history", async () =>
{
    var temporaryDirectory = Path.Combine(Path.GetTempPath(), "nova-productivity-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporaryDirectory);
    try
    {
        var snapshots = new TaskSnapshotService(Path.Combine(temporaryDirectory, "tasks"));
        var journal = new TaskJournalService(Path.Combine(temporaryDirectory, "journal.jsonl"));
        var schedules = new AgentScheduleService(Path.Combine(temporaryDirectory, "schedules.json"));
        var completed = new TaskItem
        {
            Id = "summary-complete",
            Title = "Complete release",
            Description = "Build and verify release",
            WorkspaceRoot = @"D:\Agent",
            Provider = "openai",
            Model = "gpt-5.6",
            State = TaskState.Completed,
            Progress = 100,
            Stage = "Done",
            CreatedAt = DateTimeOffset.Now.AddMinutes(-25)
        };
        var blocked = new TaskItem
        {
            Id = "summary-blocked",
            Title = "Blocked research",
            Description = "Waiting for evidence",
            WorkspaceRoot = @"D:\Agent",
            Provider = "deepseek",
            Model = "deepseek-v4-flash",
            State = TaskState.Failed,
            Progress = 40,
            Stage = "Blocked",
            CreatedAt = DateTimeOffset.Now.AddMinutes(-10)
        };
        await snapshots.SaveAsync(completed);
        await snapshots.SaveAsync(blocked);
        await journal.AppendAsync(completed.Id, "NOVA", "Build", "Release built", ActivityKind.Completed, 100);

        var service = new ProductivityInsightsService(snapshots, journal, schedules);
        var summary = service.Generate(7);
        Expect(summary.TotalTasks == 2, "Productivity summary missed persisted tasks.");
        Expect(summary.CompletedTasks == 1, "Productivity completed count is incorrect.");
        Expect(summary.BlockedTasks == 1, "Productivity blocked count is incorrect.");
        Expect(Math.Abs(summary.CompletionRate - 50) < .01, "Productivity completion rate is incorrect.");
        Expect(summary.Insights.Count > 0, "Productivity summary produced no insights.");
    }
    finally
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
});

await CheckAsync("cognitive knowledge graph persistence", async () =>
{
    var temporaryDirectory = Path.Combine(Path.GetTempPath(), "nova-graph-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporaryDirectory);
    try
    {
        var service = new KnowledgeGraphService(Path.Combine(temporaryDirectory, "graph.json"));
        var tasks = new[]
        {
            new TaskSnapshot(
                "graph-task",
                "Native agent architecture",
                "Design a secure native Windows agent with skills and MCP",
                @"D:\Agent",
                "openai",
                "gpt-5.6",
                TaskState.Completed,
                100,
                "Done",
                DateTimeOffset.Now.AddHours(-1),
                DateTimeOffset.Now)
        };
        var graph = await service.SynchronizeAsync(
            tasks,
            [new InstalledSkill("release-auditor", "release-auditor", "Verify releases", @"D:\skills\release-auditor", true, DateTimeOffset.Now, 2, 100)],
            [new McpServerRegistration("local-tools", "dotnet", [], @"D:\Agent", true, new Dictionary<string, string>())],
            [],
            CancellationToken.None);
        Expect(graph.Nodes.Any(node => node.Kind == "Goal"), "Graph did not create a goal node.");
        Expect(graph.Nodes.Any(node => node.Kind == "Skill"), "Graph did not create a skill node.");
        Expect(graph.Edges.Count > 0, "Graph did not create relationships.");

        var related = graph.Nodes.First().Id;
        await service.AddKnowledgeAsync("Prefer native UI", "Avoid browser shells for the desktop client.", related, CancellationToken.None);
        var restored = new KnowledgeGraphService(Path.Combine(temporaryDirectory, "graph.json")).GetSnapshot();
        Expect(restored.Nodes.Any(node => node.IsManual && node.Label == "Prefer native UI"), "Manual knowledge did not persist.");
        Expect(service.QueryJson("native", 20).Contains("Native agent architecture", StringComparison.OrdinalIgnoreCase), "Graph query did not return matching knowledge.");
    }
    finally
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
});

await CheckAsync("incremental local knowledge indexing and cited search", async () =>
{
    var temporaryDirectory = Path.Combine(Path.GetTempPath(), "nova-index-" + Guid.NewGuid().ToString("N"));
    var workspace = Path.Combine(temporaryDirectory, "workspace");
    Directory.CreateDirectory(Path.Combine(workspace, "docs"));
    Directory.CreateDirectory(Path.Combine(workspace, "bin"));
    await File.WriteAllTextAsync(
        Path.Combine(workspace, "README.md"),
        "# Native Agent\nNOVA is a native Windows agent with a permission boundary.");
    await File.WriteAllTextAsync(
        Path.Combine(workspace, "docs", "security.md"),
        "# Security Model\nEvery mutating tool requires explicit approval and workspace containment.");
    await File.WriteAllTextAsync(
        Path.Combine(workspace, "bin", "ignored.txt"),
        "This build output must never be indexed.");
    try
    {
        var index = new KnowledgeIndexService(Path.Combine(temporaryDirectory, "index.json"));
        var first = await index.IndexWorkspaceAsync(workspace, CancellationToken.None);
        Expect(first.IndexedFiles == 2, "Knowledge index did not index both source documents.");
        Expect(index.GetDocuments(workspace).Count == 2, "Knowledge index included an ignored build directory.");

        var results = index.Search("permission boundary", workspace, 10);
        Expect(results.Count > 0, "Knowledge search returned no cited result.");
        Expect(results[0].RelativePath.Equals("README.md", StringComparison.OrdinalIgnoreCase), "Knowledge search ranked the wrong document.");
        Expect(results[0].StartLine >= 1, "Knowledge result did not include a valid start line.");

        var second = await index.IndexWorkspaceAsync(workspace, CancellationToken.None);
        Expect(second.ReusedFiles == 2 && second.IndexedFiles == 0, "Unchanged documents were not reused incrementally.");

        var host = new WorkspaceToolHost(workspace, knowledgeIndex: index);
        Expect(host.RequiresApproval("index_workspace_knowledge"), "Bulk knowledge indexing did not require approval.");
        Expect(!host.RequiresApproval("search_local_knowledge"), "Read-only knowledge search unexpectedly required approval.");
        var toolResult = await host.ExecuteAsync(
            "search_local_knowledge",
            new JsonObject { ["query"] = "explicit approval", ["max_results"] = 5 },
            CancellationToken.None);
        Expect(toolResult.Contains("security.md", StringComparison.OrdinalIgnoreCase), "Agent knowledge search did not return a file citation.");
    }
    finally
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
});

await CheckAsync("persistent versioned artifacts and knowledge integration", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-artifacts-" + Guid.NewGuid().ToString("N"));
    var workspace = Path.Combine(temporaryDirectory, "workspace");
    var outputRoot = Path.Combine(temporaryDirectory, "outputs");
    Directory.CreateDirectory(workspace);
    try
    {
        var repository = new ArtifactRepositoryService(
            Path.Combine(temporaryDirectory, "artifacts.json"),
            outputRoot);
        var task = new TaskItem
        {
            Id = "artifact-smoke",
            Title = "交付系统测试",
            Description = "验证持久化、版本和认知接入",
            WorkspaceRoot = workspace,
            Provider = "openai",
            Model = "gpt-5.6",
            State = TaskState.Completed,
            Stage = "成果已交付",
            Progress = 100
        };
        var versionOne = (await repository.PersistAsync(
            task,
            [
                new ArtifactItem(
                    "报告",
                    "测试报告",
                    "第一版",
                    "",
                    "#75F0FF",
                    "NOVA persistent artifact version one.")
            ]))[0];
        Expect(versionOne.Version == 1, "First artifact version was not v1.");
        Expect(File.Exists(versionOne.Location), "Artifact file was not created.");
        Expect(
            Path.GetFileName(versionOne.Location).Any(character =>
                character is >= '\u4e00' and <= '\u9fff'),
            "Non-programming artifact did not receive a readable Chinese file name.");

        var codeArtifact = (await repository.PersistAsync(
            task,
            [
                new ArtifactItem(
                    "code",
                    "settings-service",
                    "C# source",
                    "",
                    "#75F0FF",
                    "public sealed class SettingsService {}")
            ]))[0];
        Expect(
            Path.GetFileName(codeArtifact.Location).StartsWith(
                "code-settings-service",
                StringComparison.OrdinalIgnoreCase),
            "Programming artifact naming no longer follows engineering conventions.");

        var versionTwo = (await repository.PersistAsync(
            task,
            [
                new ArtifactItem(
                    "报告",
                    "测试报告",
                    "第二版",
                    "",
                    "#75F0FF",
                    "NOVA persistent artifact version two with knowledge indexing.")
            ]))[0];
        Expect(versionTwo.Version == 2, "Changed artifact did not create v2.");
        Expect(repository.GetVersions(versionTwo.Id).Count == 2, "Artifact version history did not persist.");
        Expect(
            repository.GetForTask(task.Id).Single(item => item.Id == versionTwo.Id).Version == 2,
            "Task artifact list did not return the latest version.");

        var index = new KnowledgeIndexService(
            Path.Combine(temporaryDirectory, "knowledge-index.json"),
            outputRoot);
        Expect(
            await index.UpsertArtifactsAsync(workspace, [versionTwo], CancellationToken.None) == 1,
            "Artifact was not inserted into the local knowledge index.");
        await File.WriteAllTextAsync(Path.Combine(workspace, "README.md"), "# Workspace source");
        await index.IndexWorkspaceAsync(workspace, CancellationToken.None);
        Expect(
            index.GetDocuments(workspace).Any(document =>
                document.RelativePath.StartsWith(".nova-artifacts", StringComparison.OrdinalIgnoreCase)),
            "Workspace reindex removed persisted artifact knowledge.");
        Expect(
            index.Search("knowledge indexing", workspace, 5).Any(result =>
                result.Title == "测试报告"),
            "Artifact content was not searchable with a citation.");

        var host = new WorkspaceToolHost(
            workspace,
            knowledgeIndex: index,
            artifactRepository: repository);
        Expect(!host.RequiresApproval("list_task_artifacts"), "Read-only artifact listing unexpectedly required approval.");
        var listed = await host.ExecuteAsync(
            "list_task_artifacts",
            new JsonObject { ["max_results"] = 10 },
            CancellationToken.None);
        Expect(listed.Contains(versionTwo.Id, StringComparison.Ordinal), "Agent artifact list omitted the persisted artifact.");
        var read = await host.ExecuteAsync(
            "read_task_artifact",
            new JsonObject { ["artifact_id"] = versionTwo.Id, ["version"] = 1 },
            CancellationToken.None);
        Expect(read.Contains("version one", StringComparison.OrdinalIgnoreCase), "Agent could not read a requested artifact version.");

        var graphService = new KnowledgeGraphService(Path.Combine(temporaryDirectory, "graph.json"));
        var graph = await graphService.SynchronizeAsync(
            [
                new TaskSnapshot(
                    task.Id,
                    task.Title,
                    task.Description,
                    workspace,
                    task.Provider,
                    task.Model,
                    task.State,
                    task.Progress,
                    task.Stage,
                    task.CreatedAt,
                    DateTimeOffset.Now)
            ],
            [],
            [],
            [],
            CancellationToken.None,
            index.GetDocuments(workspace),
            repository.GetLatest(workspace));
        Expect(graph.Nodes.Any(node => node.Kind == "Artifact"), "Knowledge graph did not create an artifact node.");
        Expect(graph.Edges.Any(edge => edge.Relation == "delivers"), "Knowledge graph did not link the task to its artifact.");
    }
    finally
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
});

await CheckAsync("result-first delivery manifest and quiet completion surface", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-delivery-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporaryDirectory);
    try
    {
        var codex = new CodexRuntimeProbe(
            CodexRuntimeAvailability.Unavailable,
            "optional",
            "",
            null,
            null,
            false);
        var before = new EngineeringWorkspaceSnapshot(
            temporaryDirectory,
            "delivery-smoke",
            codex,
            false,
            "NO REPOSITORY",
            [],
            0,
            0,
            "baseline",
            ["pyproject.toml"],
            "pytest",
            "ready",
            DateTimeOffset.Now)
        {
            WorkspaceFileCount = 1,
            WorkspaceFingerprint = "before",
            WorkspaceInventoryEntries = ["README.md|12|100"]
        };
        var after = before with
        {
            WorkspaceFileCount = 2,
            WorkspaceFingerprint = "after",
            WorkspaceInventoryEntries =
            [
                "README.md|24|200",
                "src/feature.py|80|200",
                ".nova/internal.json|10|200"
            ]
        };
        var manifest = await new DeliveryManifestService().CreateAsync(
            "delivery-smoke",
            "让工程达到可交付状态",
            before,
            after,
            "PROVEN",
            100,
            verificationAttempted: true,
            verificationPassed: true,
            "pytest · 11 passed",
            CancellationToken.None);
        Expect(
            manifest.ChangedFiles.Count == 2
            && manifest.ChangedFiles.Any(item =>
                item.Status == "M" && item.Path == "README.md")
            && manifest.ChangedFiles.Any(item =>
                item.Status == "A" && item.Path == "src/feature.py")
            && manifest.ChangedFiles.All(item =>
                !item.Path.StartsWith(".nova", StringComparison.OrdinalIgnoreCase)),
            "Delivery manifest did not isolate actual workspace changes.");
        Expect(
            File.Exists(manifest.ArtifactPath)
            && manifest.Preview.Contains("# 本轮交付", StringComparison.Ordinal)
            && manifest.Preview.Contains("pytest · 11 passed", StringComparison.Ordinal),
            "Delivery manifest was not persisted as an actionable handoff.");

        var mainWindow = await File.ReadAllTextAsync(
            @"D:\Agent\NovaDesktop\MainWindow.xaml");
        var threadspace = await File.ReadAllTextAsync(
            @"D:\Agent\NovaDesktop\Controls\ConversationStage.xaml");
        Expect(
            mainWindow.Contains(
                "ItemsSource=\"{Binding DeliveryArtifacts}\"",
                StringComparison.Ordinal)
            && mainWindow.Contains(
                "ItemsSource=\"{Binding DeliveryEvidenceArtifacts}\"",
                StringComparison.Ordinal)
            && mainWindow.Contains("证据与运行记录", StringComparison.Ordinal),
            "Delivery workbench does not prioritize the actual handoff.");
        Expect(
            threadspace.Contains("IsCompletedSummaryVisible", StringComparison.Ordinal)
            && threadspace.Contains("IsConversationTranscriptVisible", StringComparison.Ordinal)
            && threadspace.Contains("查看本轮交付", StringComparison.Ordinal),
            "Completed tasks do not collapse into a quiet result-first surface.");
    }
    finally
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
});

await CheckAsync("workspace root routing and recent projects", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-workspace-profile-" + Guid.NewGuid().ToString("N"));
    var repositoryRoot = Path.Combine(temporaryDirectory, "product");
    var selectedChild = Path.Combine(repositoryRoot, "src", "feature");
    Directory.CreateDirectory(Path.Combine(repositoryRoot, ".git"));
    Directory.CreateDirectory(selectedChild);
    await File.WriteAllTextAsync(
        Path.Combine(repositoryRoot, "Product.csproj"),
        """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
    try
    {
        var service = new WorkspaceProfileService(
            Path.Combine(temporaryDirectory, "workspaces.json"));
        var profile = service.Analyze(selectedChild);
        Expect(
            profile.Root.Equals(repositoryRoot, StringComparison.OrdinalIgnoreCase),
            "Selecting a source child did not resolve to the Git project root.");
        Expect(profile.Kind == ".NET 工程", "Workspace stack detection missed the .NET project.");
        Expect(
            profile.BuildHint.Contains("智能增量构建", StringComparison.Ordinal),
            "Workspace did not expose its smart build profile.");

        service.Remember(profile.Root, resolveProjectRoot: false);
        var recent = service.LoadRecent();
        Expect(recent.Count == 1, "Recent workspace history was not persisted.");
        Expect(recent[0].Exists, "Existing recent workspace was marked unavailable.");

        var nodeRoot = Path.Combine(temporaryDirectory, "node-product");
        Directory.CreateDirectory(nodeRoot);
        await File.WriteAllTextAsync(
            Path.Combine(nodeRoot, "package.json"),
            """{"scripts":{"build":"vite build"}}""");
        var engineering = new EngineeringWorkspaceService(
            new CodexRuntimeProbeService(Path.Combine(temporaryDirectory, "missing-codex.exe")),
            new EngineeringEvidenceLedgerService(Path.Combine(temporaryDirectory, "evidence.jsonl")));
        var nodeSnapshot = await engineering.InspectAsync(nodeRoot);
        Expect(
            nodeSnapshot.VerificationCommand.Contains("npm", StringComparison.OrdinalIgnoreCase)
            && nodeSnapshot.VerificationCommand.Contains("build", StringComparison.OrdinalIgnoreCase),
            "Node build script was not selected as a smart verification plan.");
    }
    finally
    {
        DeleteGeneratedTestDirectory(temporaryDirectory);
    }
});

await CheckAsync("persistent multi-turn conversation context", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-conversation-" + Guid.NewGuid().ToString("N"));
    try
    {
        var service = new ConversationHistoryService(temporaryDirectory);
        await service.AppendAsync("conversation-smoke", "user", "先分析当前项目。");
        await service.AppendAsync("conversation-smoke", "assistant", "已经识别项目结构。");
        await service.AppendAsync("conversation-smoke", "user", "继续优化构建速度。");

        var loaded = service.Load("conversation-smoke");
        Expect(loaded.Count == 3, "Conversation turns were not durably persisted.");
        Expect(service.GetRoundCount("conversation-smoke") == 2, "Conversation round count is incorrect.");
        Expect(service.GetResponseCount("conversation-smoke") == 1, "Conversation response count is incorrect.");
        var context = service.BuildContextPrompt(
            "conversation-smoke",
            "继续优化构建速度。");
        Expect(
            context.Contains("已经识别项目结构", StringComparison.Ordinal)
            && context.Contains("继续优化构建速度", StringComparison.Ordinal),
            "Previous assistant and current user turns were not carried into model context.");
        var resumedContext = service.BuildContextPrompt(
            "conversation-smoke",
            "这是恢复任务后的全新指令。");
        Expect(
            resumedContext.Contains("这是恢复任务后的全新指令", StringComparison.Ordinal),
            "A resumed task dropped the current user instruction from model context.");

        await service.AppendAsync(
            "conversation-smoke",
            "assistant",
            new string('推', 18_000)
            + "\n---\n### NOVA 交付护照\n- 状态：READY");
        await service.AppendAsync(
            "conversation-smoke",
            "user",
            "纠正：不要修改任何文件，改成只做咨询分析；预算不超过三轮。");
        var consultationContext = service.BuildContextPrompt(
            "conversation-smoke",
            "继续，按刚才确认的方向给我三个方案。");
        Expect(
            consultationContext.Contains("[NOVA THREAD MEMORY v2]", StringComparison.Ordinal)
            && consultationContext.Contains(
                "即使本任务没有产生任何本地文件",
                StringComparison.Ordinal)
            && consultationContext.Contains(
                "不要修改任何文件，改成只做咨询分析",
                StringComparison.Ordinal)
            && consultationContext.Contains(
                "继续，按刚才确认的方向给我三个方案",
                StringComparison.Ordinal),
            "Non-file consultation memory lost the user's correction or referential follow-up.");
        Expect(
            !consultationContext.Contains("NOVA 交付护照", StringComparison.Ordinal)
            && consultationContext.Length <= 36_000,
            "Thread memory retained delivery boilerplate or exceeded its context budget.");

        var transientOnly = new[]
        {
            new ConversationTurn(
                "transient-user",
                "legacy-task",
                "user",
                "我们讨论的是线下零售方案。",
                DateTimeOffset.UnixEpoch),
            new ConversationTurn(
                "transient-assistant",
                "legacy-task",
                "assistant",
                "已经比较了三个选址方向。",
                DateTimeOffset.UnixEpoch.AddSeconds(1))
        };
        var transientContext = service.BuildContextPrompt(
            "legacy-task",
            "继续第二个方向。",
            transientOnly,
            includeCurrentPrompt: false);
        Expect(
            transientContext.Contains("线下零售方案", StringComparison.Ordinal)
            && transientContext.Contains("已经比较了三个选址方向", StringComparison.Ordinal)
            && !transientContext.Contains("继续第二个方向", StringComparison.Ordinal),
            "Legacy transient conversation fallback did not compile a bounded prior-turn memory.");
    }
    finally
    {
        DeleteGeneratedTestDirectory(temporaryDirectory);
    }
});

await CheckAsync("durable task snapshot recovery", async () =>
{
    var temporaryDirectory = Path.Combine(Path.GetTempPath(), "nova-snapshot-smoke-" + Guid.NewGuid().ToString("N"));
    var service = new TaskSnapshotService(temporaryDirectory);
    try
    {
        var task = new TaskItem
        {
            Id = "snapshot-smoke",
            Title = "恢复测试",
            Description = "验证持久化目标",
            WorkspaceRoot = @"D:\Agent",
            Provider = "deepseek",
            Model = "deepseek-v4-flash",
            ExecutionMode = AgentExecutionMode.Autopilot,
            Draft = "continue with the isolated task draft",
            State = TaskState.Running,
            Stage = "执行工具",
            Progress = 42
        };
        await service.SaveAsync(task);
        var recovered = service.LoadRecoverable();
        Expect(recovered.Count == 1, "Running task snapshot was not recoverable.");
        Expect(recovered[0].State == TaskState.Paused, "Recovered task was not made safe and paused.");
        Expect(recovered[0].Provider == "deepseek", "Provider metadata was not retained.");
        Expect(
            recovered[0].ExecutionMode == AgentExecutionMode.Autopilot,
            "Recovered task lost its AgentOS execution mode.");
        Expect(
            recovered[0].Draft == "continue with the isolated task draft",
            "Recovered task lost its isolated composer draft.");

        task.State = TaskState.Completed;
        await service.SaveAsync(task);
        Expect(service.LoadRecoverable().Count == 0, "Completed task should not be recoverable.");
    }
    finally
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
});

await CheckAsync("persistent agent schedule lifecycle", async () =>
{
    var temporaryDirectory = Path.Combine(Path.GetTempPath(), "nova-schedule-smoke-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporaryDirectory);
    var service = new AgentScheduleService(Path.Combine(temporaryDirectory, "schedules.json"));
    var request = new AgentRunRequest(
        "schedule-smoke",
        "创建计划",
        @"D:\Agent",
        "sk-test",
        "openai",
        "gpt-5.6");
    try
    {
        var onceResult = await service.CreateAsync(
            new JsonObject
            {
                ["name"] = "一次任务",
                ["prompt"] = "检查项目状态",
                ["run_at"] = DateTimeOffset.Now.ToString("O")
            },
            request,
            CancellationToken.None);
        var onceId = JsonNode.Parse(onceResult)?["Id"]?.GetValue<string>()
                     ?? throw new Exception("One-time schedule ID missing.");
        var claimedOnce = await service.TryClaimNextDueAsync(
            DateTimeOffset.Now.AddMinutes(1),
            CancellationToken.None);
        Expect(claimedOnce?.Id == onceId, "One-time schedule was not claimed.");
        Expect(service.GetEnabledCount() == 0, "Claimed one-time schedule remained enabled.");

        await service.RequeueAsync(
            claimedOnce!,
            DateTimeOffset.Now.AddMinutes(5),
            CancellationToken.None);
        Expect(service.GetEnabledCount() == 1, "One-time schedule was not requeued.");
        await service.DisableAsync(onceId, CancellationToken.None);
        Expect(service.GetEnabledCount() == 0, "Schedule disable did not persist.");

        await service.CreateAsync(
            new JsonObject
            {
                ["name"] = "周期任务",
                ["prompt"] = "周期检查",
                ["interval_minutes"] = 5
            },
            request,
            CancellationToken.None);
        var claimedInterval = await service.TryClaimNextDueAsync(
            DateTimeOffset.Now.AddMinutes(6),
            CancellationToken.None);
        Expect(claimedInterval?.Mode == AgentScheduleMode.Interval, "Recurring schedule was not claimed.");
        Expect(service.GetEnabledCount() == 1, "Recurring schedule was not advanced and kept enabled.");
        Expect(service.ListSchedules().Contains("\"mode\":\"interval\"", StringComparison.Ordinal), "Recurring schedule metadata missing.");
    }
    finally
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
});

await CheckAsync("schedule center creation and task archive library", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-schedule-center-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporaryDirectory);
    try
    {
        var scheduleService = new AgentScheduleService(
            Path.Combine(temporaryDirectory, "schedules.json"));
        var created = await scheduleService.CreateAsync(
            new AgentScheduleDraft(
                "nightly verification",
                "inspect and verify the current workspace",
                @"D:\Agent",
                "deepseek",
                "deepseek-v4-pro",
                AgentScheduleMode.Interval,
                IntervalMinutes: 60,
                ExecutionMode: AgentExecutionMode.Goal),
            CancellationToken.None);
        Expect(
            created.ExecutionMode == AgentExecutionMode.Goal
            && created.IntervalMinutes == 60,
            "Direct schedule creation did not retain its execution contract.");

        var task = new TaskItem
        {
            Id = "archive-smoke",
            Title = "archive me",
            Description = "persistent archived task",
            WorkspaceRoot = @"D:\Agent",
            State = TaskState.Paused,
            IsArchived = true
        };
        var snapshotService = new TaskSnapshotService(
            Path.Combine(temporaryDirectory, "tasks"));
        await snapshotService.SaveAsync(task);
        Expect(
            snapshotService.LoadAll().Single().IsArchived,
            "Archived task state did not persist.");
        Expect(
            snapshotService.LoadRecoverable().Count == 0,
            "Archived task leaked back into automatic recovery.");

        var scheduleXaml = await File.ReadAllTextAsync(
            @"D:\Agent\NovaDesktop\ScheduleWindow.xaml");
        var scheduleCode = await File.ReadAllTextAsync(
            @"D:\Agent\NovaDesktop\ScheduleWindow.xaml.cs");
        var mainXaml = await File.ReadAllTextAsync(
            @"D:\Agent\NovaDesktop\MainWindow.xaml");
        var viewModel = await File.ReadAllTextAsync(
            @"D:\Agent\NovaDesktop\ViewModels\MainViewModel.cs");
        Expect(
            scheduleXaml.Contains("x:Name=\"PromptBox\"", StringComparison.Ordinal)
            && scheduleXaml.Contains("Click=\"Create_Click\"", StringComparison.Ordinal)
            && scheduleXaml.Contains("x:Name=\"EmptyState\"", StringComparison.Ordinal)
            && scheduleCode.Contains("AgentScheduleDraft", StringComparison.Ordinal),
            "The schedule center is still a read-only empty shell.");
        Expect(
            mainXaml.Contains("ItemsSource=\"{Binding TaskView}\"", StringComparison.Ordinal)
            && mainXaml.Contains("ArchiveTaskCommand", StringComparison.Ordinal)
            && mainXaml.Contains("RestoreTaskCommand", StringComparison.Ordinal)
            && viewModel.Contains("SetTaskArchivedAsync", StringComparison.Ordinal),
            "The left task space does not expose a persistent archive and restore workflow.");
    }
    finally
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
});

await CheckAsync("single runtime version source for 1.0 release alignment", async () =>
{
    var kernel = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\Services\AgentOsKernel.cs");
    var supervisor = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\Services\AgentSupervisorService.cs");
    var openAi = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\Services\OpenAIResponsesAgentRuntime.cs");
    var deepSeek = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\Services\DeepSeekChatAgentRuntime.cs");
    var mcp = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\Services\McpStreamableHttpClient.cs");
    var project = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\NovaDesktop.csproj");
    var installer = await File.ReadAllTextAsync(
        @"D:\Agent\installer\NOVA.iss");
    Expect(
        kernel.Contains("NovaProductVersion.Current", StringComparison.Ordinal)
        && supervisor.Contains("NovaProductVersion.Current", StringComparison.Ordinal)
        && openAi.Contains("NovaProductVersion.Current", StringComparison.Ordinal)
        && deepSeek.Contains("NovaProductVersion.Current", StringComparison.Ordinal)
        && mcp.Contains("NovaProductVersion.Current", StringComparison.Ordinal),
        "Runtime components still advertise independent hard-coded versions.");
    Expect(
        project.Contains(
            "<InformationalVersion>$(Version)</InformationalVersion>",
            StringComparison.Ordinal)
        && installer.Contains(
            "#define MyAppVersion \"1.0.0\"",
            StringComparison.Ordinal),
        "Package and installer version definitions are still drifting.");
});

await CheckAsync("GA benchmark catalog and trusted release gate", async () =>
{
    var catalogJson = await File.ReadAllTextAsync(
        @"D:\Agent\ga\benchmark-catalog.json");
    var catalog = JsonNode.Parse(catalogJson)?.AsObject()
                  ?? throw new Exception("GA benchmark catalog is not valid JSON.");
    var tasks = catalog["tasks"]?.AsArray()
                ?? throw new Exception("GA benchmark catalog has no task list.");
    var ids = tasks
        .Select(item => item?["id"]?.GetValue<string>() ?? string.Empty)
        .ToArray();
    var families = tasks
        .Select(item => item?["family"]?.GetValue<string>() ?? string.Empty)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    Expect(
        tasks.Count == 30
        && ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 30
        && families.IsSupersetOf(
            ["engineering", "recovery", "security", "goal", "multi-agent", "delivery"]),
        "The frozen GA benchmark is not a unique 30-task cross-boundary suite.");
    Expect(
        catalog["runs_per_task"]?.GetValue<int>() == 3
        && catalog["minimum_proven_rate"]?.GetValue<double>() >= .80
        && catalog["minimum_terminal_accuracy"]?.GetValue<double>() >= .90,
        "GA benchmark thresholds drifted below the frozen acceptance contract.");

    var releaseScript = await File.ReadAllTextAsync(
        @"D:\Agent\build-release.ps1");
    var installer = await File.ReadAllTextAsync(
        @"D:\Agent\installer\NOVA.iss");
    var benchmarkScript = await File.ReadAllTextAsync(
        @"D:\Agent\tools\Measure-GaBenchmark.ps1");
    Expect(
        releaseScript.Contains("CodeSigningCertificateThumbprint", StringComparison.Ordinal)
        && releaseScript.Contains("GaBenchmarkReportPath", StringComparison.Ordinal)
        && releaseScript.Contains("Set-TrustedAuthenticodeSignature", StringComparison.Ordinal)
        && releaseScript.Contains("execution-events.jsonl", StringComparison.Ordinal) == false
        && releaseScript.Contains("release-manifest.sig.json", StringComparison.Ordinal)
        && releaseScript.Contains(".ga-install-smoke", StringComparison.Ordinal)
        && benchmarkScript.Contains("total_runs", StringComparison.Ordinal)
        && installer.Contains("#ifndef MyAppVersion", StringComparison.Ordinal),
        "Trusted release no longer enforces benchmark, signing, detached manifest or fresh-install gates.");
});

await CheckAsync("Windows credential vault roundtrip", async () =>
{
    var prefix = "NOVA/SmokeTests/" + Guid.NewGuid().ToString("N");
    var vault = new WindowsCredentialVault(prefix);
    const string secret = "sk-smoke-only-not-a-real-key-123456";
    try
    {
        try
        {
            vault.Write("openai", secret);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"Credential Manager write failed with Win32 error {exception.NativeErrorCode}.",
                exception);
        }
        Expect(vault.IsStored("openai"), "Credential was not persisted.");
        Expect(vault.Read("openai") == secret, "Credential roundtrip changed the secret.");
        vault.Delete("openai");
        Expect(vault.Read("openai") is null, "Credential was not deleted.");
    }
    finally
    {
        vault.Delete("openai");
        vault.Delete("deepseek");
    }
    await Task.CompletedTask;
});

await CheckAsync("crash report redaction and recovery marker", async () =>
{
    var temporaryDirectory = Path.Combine(Path.GetTempPath(), "nova-crash-smoke-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporaryDirectory);
    try
    {
        var recovery = new CrashRecoveryService(temporaryDirectory);
        Expect(!recovery.HadUncleanShutdown, "Fresh recovery directory reported an unclean shutdown.");
        recovery.StartSession();
        var restarted = new CrashRecoveryService(temporaryDirectory);
        Expect(restarted.HadUncleanShutdown, "Session marker was not detected.");

        const string secret = "sk-super-secret-smoke-value";
        var reportPath = recovery.WriteCrashReport(
            new InvalidOperationException($"Failure with {secret} and Bearer token-value-123456"),
            "smoke",
            fatal: true)
            ?? throw new Exception("Crash report was not written.");
        var report = await File.ReadAllTextAsync(reportPath);
        Expect(!report.Contains(secret, StringComparison.Ordinal), "Crash report leaked an API key.");
        Expect(!report.Contains("token-value-123456", StringComparison.Ordinal), "Crash report leaked a bearer token.");
        Expect(report.Contains("[REDACTED", StringComparison.Ordinal), "Crash report did not record redaction.");

        recovery.MarkCleanExit();
        Expect(!new CrashRecoveryService(temporaryDirectory).HadUncleanShutdown, "Clean exit marker was not cleared.");
    }
    finally
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
});

await CheckAsync("unified failure taxonomy and durable recovery", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-failure-ledger-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporaryDirectory);
    try
    {
        var network = TaskFailureClassifier.Classify(
            "failure-smoke",
            new HttpRequestException("connection reset"),
            "连接模型");
        var modelAuth = TaskFailureClassifier.Classify(
            "failure-smoke",
            new HttpRequestException(
                "provider rejected sk-failure-secret-123456",
                null,
                System.Net.HttpStatusCode.Unauthorized),
            "连接模型");
        var budget = TaskFailureClassifier.Classify(
            "failure-smoke",
            new AgentBudgetExceededException("模型轮次", 24, 25),
            "深度推理");
        var permission = TaskFailureClassifier.Classify(
            "failure-smoke",
            new UnauthorizedAccessException("workspace denied"),
            "写入文件");
        var build = TaskFailureClassifier.Classify(
            "failure-smoke",
            new InvalidOperationException("compiler exited 1"),
            "dotnet build");
        var verification = TaskFailureClassifier.Classify(
            "failure-smoke",
            new InvalidOperationException("assertion failed"),
            "dotnet test verification");
        var configuration = TaskFailureClassifier.Classify(
            "failure-smoke",
            new DirectoryNotFoundException("workspace missing"),
            "载入任务");

        Expect(
            network.Kind == TaskFailureKind.Network
            && network.RecoveryAction == FailureRecoveryAction.Retry
            && modelAuth.Kind == TaskFailureKind.Model
            && modelAuth.RecoveryAction == FailureRecoveryAction.ReconnectModel
            && budget.Kind == TaskFailureKind.Budget
            && budget.StatusLabel == "BUDGET EXHAUSTED"
            && permission.Kind == TaskFailureKind.Permission
            && build.Kind == TaskFailureKind.Build
            && verification.Kind == TaskFailureKind.Verification
            && configuration.Kind == TaskFailureKind.Configuration,
            "Runtime failures were not mapped to stable AgentOS categories.");

        var ledger = new TaskFailureLedgerService(temporaryDirectory);
        await ledger.RecordAsync(modelAuth);
        await Task.Delay(5);
        await ledger.RecordAsync(verification with
        {
            UserMessage =
                "Bearer token-failure-secret-123456 and sk-another-secret-123456"
        });
        var records = ledger.Load("failure-smoke");
        var persistedJson = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(temporaryDirectory, "*.json")
                .Select(File.ReadAllText));
        Expect(
            records.Count == 2
            && ledger.LoadLatest("failure-smoke")?.Kind
                == TaskFailureKind.Verification
            && persistedJson.Contains("[REDACTED", StringComparison.Ordinal)
            && !persistedJson.Contains(
                "sk-another-secret",
                StringComparison.Ordinal)
            && !persistedJson.Contains(
                "token-failure-secret",
                StringComparison.Ordinal),
            "Failure recovery records were not durable or safely redacted.");
    }
    finally
    {
        DeleteGeneratedTestDirectory(temporaryDirectory);
    }
});

await CheckAsync("professional engineering task routing", async () =>
{
    var general = EngineeringTaskRouter.Classify("总结本周工作效率");
    Expect(!general.IsEngineeringTask, "General productivity prompt was misrouted to engineering.");

    var engineering = EngineeringTaskRouter.Classify("修复 WPF 最大化布局并运行测试");
    Expect(engineering.IsEngineeringTask, "Coding prompt was not routed to engineering mode.");
    Expect(engineering.Intent == "ENGINEERING", "Engineering intent label was not retained.");
    var enriched = EngineeringTaskRouter.EnrichPrompt("修复 WPF 最大化布局并运行测试");
    Expect(
        enriched.Contains("NOVA PROFESSIONAL ENGINEERING MODE", StringComparison.Ordinal),
        "Engineering execution contract was not injected.");
    Expect(
        enriched.Contains("never claim an action ran without tool evidence", StringComparison.Ordinal),
        "Engineering evidence requirement was omitted.");
    Expect(
        EngineeringTaskRouter.RequiresWorkspaceMutation("开发一个坦克大战小游戏"),
        "Implementation intent did not require a real workspace mutation.");
    Expect(
        EngineeringTaskRouter.RequiresWorkspaceMutation(
            "用户：搞一个小程序\nNOVA：你想做哪种微信小程序？\n用户：方向1和方向2的结合版本"),
        "A terse multi-turn WeChat Mini Program continuation lost its engineering mutation intent.");
    Expect(
        !EngineeringTaskRouter.RequiresWorkspaceMutation("分析这段代码有什么风险"),
        "Read-only code analysis was incorrectly forced to mutate the workspace.");
    await Task.CompletedTask;
});

await CheckAsync("Proof-of-Done outcome contract", async () =>
{
    var snapshot = new EngineeringWorkspaceSnapshot(
        @"D:\Agent",
        "Agent",
        new CodexRuntimeProbe(
            CodexRuntimeAvailability.Unavailable,
            "optional",
            "",
            null,
            null,
            false),
        true,
        "main",
        [],
        0,
        0,
        "clean",
        ["NovaDesktop/NovaDesktop.csproj"],
        "dotnet test NovaDesktop.SmokeTests --nologo",
        "ready",
        DateTimeOffset.Now);
    var contract = TaskOutcomeContractService.Create(
        "proof-smoke",
        "实现完成证明，不要删除测试，并运行验证",
        AgentExecutionMode.Autopilot,
        snapshot);
    Expect(contract.RequiresWorkspaceMutation, "Implementation goal did not require material change evidence.");
    Expect(contract.Criteria.Count == 6, "Autopilot contract omitted the independent Council criterion.");
    Expect(
        contract.Constraints.Any(item => item.Contains("不要删除测试", StringComparison.Ordinal)),
        "Explicit negative constraint was not frozen into the contract.");

    var result = new AgentRunResult(
        "proof-response",
        "已修改服务并通过测试。",
        4,
        "openai",
        "gpt-test")
    {
        MutatingToolCalls = 1
    };
    var review = new EngineeringCodeReviewResult(
        92,
        [],
        "No high-risk findings.",
        DateTimeOffset.Now);
    var council = IndependentVerificationCouncilService.Parse(
        "deepseek",
        "deepseek-v4-pro",
        """
        VERDICT: PASS
        CONFIDENCE: 91
        SUMMARY: 修改与验证证据一致，没有发现阻断交付的问题。
        FINDINGS:
        - none
        """);
    var proven = TaskOutcomeContractService.Assess(
        contract,
        result,
        verificationAttempted: true,
        verificationPassed: true,
        review,
        council);
    Expect(proven.Status == "PROVEN" && proven.ProofScore == 100, "Valid evidence did not prove completion.");
    Expect(
        TaskOutcomeContractService.FormatAssessment(proven).Contains("Proof-of-Done", StringComparison.Ordinal),
        "Proof artifact was not formatted for delivery.");

    var unproven = TaskOutcomeContractService.Assess(
        contract,
        result with { MutatingToolCalls = 0 },
        verificationAttempted: false,
        verificationPassed: false,
        review: null);
    Expect(unproven.Status == "FAILED", "Missing material change was not rejected.");
    Expect(
        unproven.Criteria.Any(item => item.Id == "verification" && item.Status == "UNVERIFIED"),
        "Skipped verification was incorrectly reported as passed.");

    var secretSnapshot = snapshot with
    {
        Diff = "+var apiKey = \"sk-council-secret-123456\";"
    };
    var councilPrompt = IndependentVerificationCouncilService.BuildPrompt(
        "实现完成证明",
        contract,
        secretSnapshot,
        review,
        "dotnet test passed");
    Expect(
        !councilPrompt.Contains("sk-council-secret", StringComparison.Ordinal),
        "Council prompt leaked an inline API key to the verification provider.");
    var concerns = IndependentVerificationCouncilService.Parse(
        "openai",
        "gpt-test",
        """
        VERDICT: CONCERNS
        CONFIDENCE: 74
        SUMMARY: 核心路径缺少回归测试。
        FINDINGS:
        - add a regression test
        """);
    Expect(concerns.IsBlocking && !concerns.Passed, "Council concerns did not block proven completion.");
    Expect(
        IndependentVerificationCouncilService.Parse("openai", "gpt-test", "普通回答").Verdict
        == "UNAVAILABLE",
        "Unstructured Council response was treated as authoritative.");
    await Task.CompletedTask;
});

await CheckAsync("Goal Mode mission charter and result contract", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-goal-mode-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporaryDirectory);
    try
    {
        var mission = GoalMissionService.Parse(
            "goal-smoke",
            "让这个产品真正好用起来",
            """
            {
              "mission_title": "降低首次使用阻力",
              "outcome": "新用户能够在不阅读文档的情况下完成第一个可验证工程任务。",
              "execution_kind": "BUILD",
              "success_signals": [
                "首次任务入口能够解释下一步操作",
                "完成一次真实文件修改并显示验证证据",
                "失败状态提供可以继续执行的恢复入口"
              ],
              "constraints": [
                "不降低现有工具审批边界",
                "不使用演示数据伪造成功",
                "api_key=sk-goal-secret-123456"
              ],
              "unknowns": [
                "当前首次任务的主要流失点",
                "哪些现有组件可以复用"
              ],
              "strategy": "先读取真实首次使用路径，验证最高风险断点，再实现最小闭环并用现有回归证明。",
              "stop_conditions": [
                "需要新的外部凭据、不可逆发布或目标与现有安全约束冲突"
              ],
              "confidence": 82
            }
            """);
        Expect(
            mission.RequiresWorkspaceChange
            && mission.SuccessSignals.Count == 3
            && mission.Unknowns.Count == 2
            && !mission.RawResponse.Contains("sk-goal-secret", StringComparison.Ordinal)
            && mission.Constraints.All(item =>
                !item.Contains("sk-goal-secret", StringComparison.Ordinal)),
            "Goal Explorer did not parse a result-oriented BUILD charter.");
        var missionService = new GoalMissionService(
            Path.Combine(temporaryDirectory, "missions"));
        mission = await missionService.SaveAsync(mission);
        Expect(
            File.Exists(mission.ArtifactPath)
            && GoalMissionService.Format(mission).Contains(
                "Success signals",
                StringComparison.Ordinal)
            && mission.MissionHash.Length == 64
            && missionService.Load(mission.TaskId)?.MissionHash == mission.MissionHash,
            "Goal Mission Charter was not persisted or formatted.");

        var snapshot = new EngineeringWorkspaceSnapshot(
            temporaryDirectory,
            "goal-workspace",
            new CodexRuntimeProbe(
                CodexRuntimeAvailability.Unavailable,
                "optional",
                "",
                null,
                null,
                false),
            true,
            "main",
            [],
            0,
            0,
            "clean",
            ["app.csproj"],
            "dotnet test",
            "ready",
            DateTimeOffset.Now);
        var contract = TaskOutcomeContractService.CreateGoal(
            "goal-smoke",
            mission,
            snapshot);
        Expect(
            contract.RequiresWorkspaceMutation
            && contract.Criteria.Count == 10
            && contract.Criteria.Count(item =>
                item.Id.StartsWith("goal-signal-", StringComparison.Ordinal)) == 3,
            "Goal Mode did not create a criterion for every success signal.");
        var result = new AgentRunResult(
            "goal-result",
            "三个成功信号均已获得文件、测试和恢复证据。",
            6,
            "openai",
            "goal-model")
        {
            MutatingToolCalls = 2
        };
        var council = IndependentVerificationCouncilService.Parse(
            "deepseek",
            "goal-judge",
            """
            VERDICT: PASS
            CONFIDENCE: 90
            SUMMARY: 所有成功信号都有可复核证据。
            FINDINGS:
            - none
            SIGNAL 1: PASS | onboarding card at MainWindow.xaml:210
            SIGNAL 2: PASS | dotnet test exited 0 and evidence ledger entry ev-2
            """);
        var partialProof = TaskOutcomeContractService.Assess(
            contract,
            result,
            verificationAttempted: true,
            verificationPassed: true,
            new EngineeringCodeReviewResult(
                95,
                [],
                "clean",
                DateTimeOffset.Now),
            council);
        Expect(
            partialProof.Status == "PARTIAL"
            && partialProof.Criteria.Single(item =>
                item.Id == "goal-signal-03").Status == "UNVERIFIED",
            "A missing success signal was incorrectly treated as proven.");

        var outcomeLedger = new GoalOutcomeLedgerService(
            Path.Combine(temporaryDirectory, "goal-outcomes"));
        var initialized = await outcomeLedger.InitializeAsync(mission);
        Expect(
            initialized.Signals.Count == 3
            && initialized.Signals.All(item =>
                item.Status == GoalSignalStatus.Pending),
            "Goal outcome ledger did not initialize stable pending signals.");
        var partialLedger = await outcomeLedger.ReconcileAsync(
            mission,
            partialProof,
            council);
        Expect(
            partialLedger.Phase == GoalRunPhase.Partial
            && !partialLedger.IsProven
            && partialLedger.Signals[2].Status == GoalSignalStatus.Unverified,
            "Goal outcome ledger overclaimed a partial evidence matrix.");
        var repairEvidence = new WorkspaceEvidenceFingerprint(
            temporaryDirectory,
            new string('a', 64),
            1,
            128,
            true,
            DateTimeOffset.Now,
            "smoke evidence");
        var repairLoop = new GoalRepairLoopService(
            Path.Combine(temporaryDirectory, "goal-repairs"));
        var firstRepair = await repairLoop.PlanNextAsync(
            mission,
            partialLedger,
            repairEvidence);
        Expect(
            firstRepair is
            {
                Round: 1,
                MaximumRounds: GoalRepairLoopService.MaximumRounds,
                PreservedPassCount: 2
            }
            && firstRepair.Targets.Count == 1
            && firstRepair.Targets[0].SignalIndex == 3,
            "Goal repair did not target only the unmet success signal.");
        var repairPrompt = GoalRepairLoopService.BuildPrompt(
            mission,
            partialLedger,
            firstRepair!,
            TaskOutcomeContractService.FormatForPrompt(contract));
        Expect(
            repairPrompt.Contains("Repair only these unmet success signals", StringComparison.Ordinal)
            && repairPrompt.Contains("SIGNAL 3:", StringComparison.Ordinal)
            && repairPrompt.Contains("Frozen evidence", StringComparison.Ordinal),
            "Goal repair prompt did not freeze passing evidence or identify its target.");
        await repairLoop.UpdateAsync(
            mission.TaskId,
            firstRepair!.AttemptId,
            GoalRepairAttemptStatus.Partial,
            "signal 3 still missing");
        var secondRepair = await repairLoop.PlanNextAsync(
            mission,
            partialLedger,
            repairEvidence);
        await repairLoop.UpdateAsync(
            mission.TaskId,
            secondRepair!.AttemptId,
            GoalRepairAttemptStatus.Partial,
            "signal 3 still missing");
        var thirdRepair = await repairLoop.PlanNextAsync(
            mission,
            partialLedger,
            repairEvidence);
        await repairLoop.UpdateAsync(
            mission.TaskId,
            thirdRepair!.AttemptId,
            GoalRepairAttemptStatus.Partial,
            "signal 3 still missing");
        Expect(
            secondRepair.Round == 2
            && thirdRepair.Round == 3
            && await repairLoop.PlanNextAsync(
                mission,
                partialLedger,
                repairEvidence) is null
            && new GoalRepairLoopService(
                    Path.Combine(temporaryDirectory, "goal-repairs"))
                .Load(mission.TaskId)?.UsedRounds == 3,
            "Goal repair round cap was not durable across service reloads.");

        var provenCouncil = IndependentVerificationCouncilService.Parse(
            "deepseek",
            "goal-judge",
            """
            VERDICT: PASS
            CONFIDENCE: 94
            SUMMARY: 三个信号均有独立证据。
            FINDINGS:
            - none
            SIGNAL 1: PASS | replacement evidence should remain frozen
            SIGNAL 2: PASS | replacement evidence should remain frozen
            SIGNAL 3: PASS | paused task reload exposes the original mission hash
            """);
        var provenProof = TaskOutcomeContractService.Assess(
            contract,
            result,
            verificationAttempted: true,
            verificationPassed: true,
            new EngineeringCodeReviewResult(
                95,
                [],
                "clean",
                DateTimeOffset.Now),
            provenCouncil);
        var provenLedger = await outcomeLedger.ReconcileTargetedAsync(
            mission,
            provenProof,
            provenCouncil,
            repairEvidence,
            [3]);
        Expect(
            provenProof.Status == "PROVEN"
            && provenLedger.IsProven
            && outcomeLedger.Load(mission.TaskId)?.IsProven == true
            && provenLedger.Signals.Select(item => item.Id)
                .SequenceEqual(initialized.Signals.Select(item => item.Id))
            && provenLedger.Signals[0].Evidence == partialLedger.Signals[0].Evidence
            && provenLedger.Signals[1].Evidence == partialLedger.Signals[1].Evidence,
            "Targeted Goal reconciliation did not preserve passing evidence or prove the repaired signal.");

        var evidenceWorkspace = Path.Combine(
            temporaryDirectory,
            "evidence-workspace");
        Directory.CreateDirectory(evidenceWorkspace);
        var evidenceFile = Path.Combine(evidenceWorkspace, "app.txt");
        await File.WriteAllTextAsync(evidenceFile, "version-one");
        var evidenceFingerprintService =
            new WorkspaceEvidenceFingerprintService();
        var originalFingerprint =
            await evidenceFingerprintService.CaptureAsync(evidenceWorkspace);
        var trackedLedger = await outcomeLedger.ReconcileAsync(
            mission,
            provenProof,
            provenCouncil,
            originalFingerprint);
        Expect(
            trackedLedger.IsProven
            && trackedLedger.Freshness == EvidenceFreshness.Fresh
            && trackedLedger.EvidenceFingerprint == originalFingerprint.Sha256
            && trackedLedger.EvidenceFileCount == 1,
            "PROVEN Goal outcome did not retain a fresh workspace fingerprint.");

        await File.WriteAllTextAsync(evidenceFile, "version-two");
        var changedFingerprint =
            await evidenceFingerprintService.CaptureAsync(evidenceWorkspace);
        var staleLedger = await outcomeLedger.ValidateFreshnessAsync(
            mission.TaskId,
            changedFingerprint);
        Expect(
            staleLedger is
            {
                Phase: GoalRunPhase.Stale,
                Freshness: EvidenceFreshness.Stale,
                IsProven: false
            }
            && staleLedger.Signals.All(item =>
                item.Status == GoalSignalStatus.Stale)
            && staleLedger.AssessmentStatus == "STALE"
            && outcomeLedger.Load(mission.TaskId)?.Phase == GoalRunPhase.Stale,
            "A workspace mutation did not durably invalidate old PROVEN evidence.");
        Expect(
            AgentExecutionPolicy.CanMutateWorkspace(AgentExecutionMode.Goal)
            && AgentExecutionPolicy.GetSystemContract(AgentExecutionMode.Goal)
                .Contains("observable Mission Charter", StringComparison.Ordinal),
            "Goal Mode policy does not expose its autonomous result boundary.");
        Expect(
            AutomaticAgentPlanner.Create(
                "让产品达到可以正式交付的状态",
                AgentExecutionMode.Goal,
                allowParallelDelegation: true) is not null,
            "Goal Mode could not create an automatic evidence team.");
    }
    finally
    {
        DeleteGeneratedTestDirectory(temporaryDirectory);
    }
});

await CheckAsync("adaptive context compiler relevance and budget", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-context-compiler-" + Guid.NewGuid().ToString("N"));
    var storageDirectory = Path.Combine(temporaryDirectory, "packs");
    Directory.CreateDirectory(Path.Combine(temporaryDirectory, "src"));
    Directory.CreateDirectory(Path.Combine(temporaryDirectory, "tests"));
    Directory.CreateDirectory(Path.Combine(temporaryDirectory, "docs"));
    try
    {
        await File.WriteAllTextAsync(
            Path.Combine(temporaryDirectory, "App.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");
        await File.WriteAllTextAsync(
            Path.Combine(temporaryDirectory, "src", "AuthenticationService.cs"),
            """
            public sealed class AuthenticationService
            {
                private const string apiKey = "sk-context-secret-must-redact-123456";
                public async Task<bool> LoginAsync(string token)
                {
                    await Task.Delay(1);
                    return token.Length > 8;
                }
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(temporaryDirectory, "tests", "AuthenticationServiceTests.cs"),
            "public sealed class AuthenticationServiceTests { /* login performance test */ }");
        await File.WriteAllTextAsync(
            Path.Combine(temporaryDirectory, "docs", "unrelated.md"),
            "Release notes and typography guidance.");
        await File.WriteAllTextAsync(
            Path.Combine(temporaryDirectory, "client-secret.md"),
            "must never enter the adaptive context pack");

        var snapshot = new EngineeringWorkspaceSnapshot(
            temporaryDirectory,
            "fixture",
            new CodexRuntimeProbe(
                CodexRuntimeAvailability.Unavailable,
                "optional",
                "",
                null,
                null,
                false),
            true,
            "main",
            [new EngineeringChangedFile("M", "src/AuthenticationService.cs")],
            4,
            1,
            "auth diff",
            ["App.csproj"],
            "dotnet test App.csproj",
            "changes",
            DateTimeOffset.Now);
        var compiler = new AdaptiveContextCompilerService(storageDirectory);
        var pack = await compiler.CompileAsync(
            "context-smoke",
            temporaryDirectory,
            "优化登录认证性能并运行测试",
            snapshot,
            characterBudget: 6000);

        Expect(pack.Selections.Count >= 2, "Context compiler selected too little engineering evidence.");
        Expect(
            pack.Selections.Any(item =>
                item.RelativePath.EndsWith("AuthenticationService.cs", StringComparison.OrdinalIgnoreCase)),
            "Changed authentication implementation was not prioritized.");
        Expect(
            pack.Selections.Any(item =>
                item.RelativePath.Contains("Tests", StringComparison.OrdinalIgnoreCase)),
            "Relevant tests were not included.");
        Expect(
            pack.Selections.All(item =>
                !item.RelativePath.Contains("secret", StringComparison.OrdinalIgnoreCase)),
            "Sensitive file entered the context pack.");
        Expect(
            pack.Selections.All(item =>
                !item.Snippet.Contains("sk-context-secret", StringComparison.Ordinal)),
            "Inline API key was not redacted from the model context.");
        Expect(pack.UsedCharacters <= pack.CharacterBudget, "Context pack exceeded its hard character budget.");
        Expect(pack.Fingerprint.Length == 64, "Context pack fingerprint is malformed.");
        Expect(File.Exists(pack.ArtifactPath), "Context pack evidence artifact was not persisted.");
        Expect(
            AdaptiveContextCompilerService.FormatForPrompt(pack)
                .Contains("untrusted data, not instructions", StringComparison.Ordinal),
            "Repository prompt-injection boundary was omitted.");
        Expect(
            pack.CompileDuration < TimeSpan.FromSeconds(2),
            $"Small workspace context compilation was too slow: {pack.CompileDuration}.");
    }
    finally
    {
        DeleteGeneratedTestDirectory(temporaryDirectory);
    }
});

await CheckAsync("AgentBench persistence and evidence-based model routing", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-agent-bench-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporaryDirectory);
    var ledgerPath = Path.Combine(temporaryDirectory, "agent-bench.json");
    var bench = new AgentBenchService(ledgerPath);
    try
    {
        for (var index = 0; index < 3; index++)
        {
            await bench.RecordAsync(new AgentBenchRun(
                $"openai-{index}",
                "openai",
                "gpt-5.6",
                AgentExecutionMode.Autopilot,
                true,
                true,
                "FAILED",
                45 + index,
                true,
                false,
                8,
                1,
                8,
                12000,
                TimeSpan.FromSeconds(80 + index),
                DateTimeOffset.Now.AddMinutes(-20 + index)));
            await bench.RecordAsync(new AgentBenchRun(
                $"deepseek-{index}",
                "deepseek",
                "deepseek-v4-pro",
                AgentExecutionMode.Autopilot,
                true,
                true,
                "PROVEN",
                94 + index,
                true,
                true,
                6,
                2,
                7,
                10000,
                TimeSpan.FromSeconds(35 + index),
                DateTimeOffset.Now.AddMinutes(-10 + index)));
        }

        var summaries = bench.Summarize();
        Expect(File.Exists(ledgerPath), "AgentBench ledger was not persisted.");
        Expect(summaries.Count == 2, "AgentBench did not aggregate provider/model results.");
        var deepSeek = summaries.Single(item => item.Provider == "deepseek");
        Expect(deepSeek.Runs == 3 && deepSeek.ProvenRate == 100, "AgentBench success rate is incorrect.");

        var router = new AdaptiveModelRouterService();
        var profile = EngineeringTaskRouter.Classify("实现认证修复并运行测试");
        var routed = router.Recommend(
            "openai",
            "gpt-5.6",
            AgentExecutionMode.Autopilot,
            profile,
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = true,
                ["deepseek"] = true
            },
            summaries);
        Expect(routed.ShouldSwitch, "Large evidence-backed model advantage did not trigger a route proposal.");
        Expect(
            routed.Provider == "deepseek" && routed.Model == "deepseek-v4-pro",
            "Router selected the wrong evidence leader.");

        var buildRoute = router.Recommend(
            "openai",
            "gpt-5.6",
            AgentExecutionMode.Build,
            profile,
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = true,
                ["deepseek"] = true
            },
            summaries);
        Expect(!buildRoute.ShouldSwitch, "Build mode switched providers without explicit Autopilot intent.");
        var goalRoute = router.Recommend(
            "openai",
            "gpt-5.6",
            AgentExecutionMode.Goal,
            profile,
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = true,
                ["deepseek"] = true
            },
            summaries);
        Expect(
            goalRoute.ShouldSwitch && goalRoute.Provider == "deepseek",
            "Goal Mode did not use evidence-backed autonomous model routing.");

        var unavailableRoute = router.Recommend(
            "openai",
            "gpt-5.6",
            AgentExecutionMode.Autopilot,
            profile,
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = true,
                ["deepseek"] = false
            },
            summaries);
        Expect(!unavailableRoute.ShouldSwitch, "Router selected a provider without a configured credential.");
    }
    finally
    {
        DeleteGeneratedTestDirectory(temporaryDirectory);
    }
});

await CheckAsync("engineering workspace inspection and verification", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-engineering-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporaryDirectory);
    try
    {
        await File.WriteAllTextAsync(
            Path.Combine(temporaryDirectory, "Smoke.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(temporaryDirectory, "Program.cs"),
            "System.Console.WriteLine(\"NOVA engineering verification\");");
        var fakeCodexPath = Path.Combine(temporaryDirectory, "codex.exe");
        await File.WriteAllTextAsync(fakeCodexPath, "not an executable");
        var safeProbe = await new CodexRuntimeProbeService(fakeCodexPath).ProbeAsync();
        Expect(
            safeProbe.Availability == CodexRuntimeAvailability.Detected,
            "Read-only Codex discovery unexpectedly executed or rejected the configured file.");

        var service = new EngineeringWorkspaceService(
            new CodexRuntimeProbeService(Path.Combine(temporaryDirectory, "missing-codex.exe")),
            new EngineeringEvidenceLedgerService(Path.Combine(temporaryDirectory, "engineering-evidence.jsonl")));
        var snapshot = await service.InspectAsync(temporaryDirectory);
        Expect(snapshot.Projects.Contains("Smoke.csproj"), "Engineering project discovery omitted the csproj.");
        Expect(
            snapshot.VerificationCommand.Contains("dotnet build", StringComparison.OrdinalIgnoreCase),
            "Engineering verification command was not selected.");
        Expect(
            snapshot.Codex.Availability == CodexRuntimeAvailability.Unavailable,
            "Missing explicit Codex runtime was not reported as unavailable.");

        var verification = await service.VerifyAsync(temporaryDirectory);
        Expect(verification.Started, "Engineering verification process did not start.");
        Expect(verification.Passed, $"Engineering verification failed: {verification.Output}");
        Expect(verification.ExitCode == 0, "Engineering verification did not retain the successful exit code.");
        var incrementalSnapshot = await service.InspectAsync(temporaryDirectory);
        Expect(
            incrementalSnapshot.VerificationCommand.Contains("--no-restore", StringComparison.OrdinalIgnoreCase),
            "Existing .NET restore assets did not enable the incremental no-restore plan.");
    }
    finally
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
});

await CheckAsync("WeChat Mini Program files and structural verification", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-wechat-miniprogram-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporaryDirectory);
    try
    {
        var host = new WorkspaceToolHost(temporaryDirectory);
        await host.ExecuteAsync(
            "write_text_file",
            new JsonObject
            {
                ["path"] = "project.config.json",
                ["content"] = """{"appid":"touristappid","projectname":"fixture"}"""
            },
            CancellationToken.None);
        await host.ExecuteAsync(
            "write_text_file",
            new JsonObject
            {
                ["path"] = "app.json",
                ["content"] = """{"pages":["pages/index/index"]}"""
            },
            CancellationToken.None);
        await host.ExecuteAsync(
            "write_text_file",
            new JsonObject { ["path"] = "app.js", ["content"] = "App({})" },
            CancellationToken.None);
        await host.ExecuteAsync(
            "write_text_file",
            new JsonObject { ["path"] = "app.wxss", ["content"] = "page { background: #fff; }" },
            CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(temporaryDirectory, "pages", "index"));
        await host.ExecuteAsync(
            "write_text_file",
            new JsonObject { ["path"] = "pages/index/index.js", ["content"] = "Page({})" },
            CancellationToken.None);
        await host.ExecuteAsync(
            "write_text_file",
            new JsonObject { ["path"] = "pages/index/index.wxml", ["content"] = "<view>ready</view>" },
            CancellationToken.None);
        await host.ExecuteAsync(
            "write_text_file",
            new JsonObject { ["path"] = "pages/index/index.wxss", ["content"] = ".ready { color: #111; }" },
            CancellationToken.None);

        var engineering = new EngineeringWorkspaceService(
            new CodexRuntimeProbeService(Path.Combine(temporaryDirectory, "missing-codex.exe")),
            new EngineeringEvidenceLedgerService(Path.Combine(temporaryDirectory, "evidence.jsonl")));
        var snapshot = await engineering.InspectAsync(temporaryDirectory);
        Expect(
            snapshot.Projects.Any(project =>
                project.EndsWith("project.config.json", StringComparison.OrdinalIgnoreCase))
            && snapshot.VerificationCommand.Contains(
                "WeChat Mini Program",
                StringComparison.Ordinal),
            "WeChat project manifest did not select the native structural validator.");
        var verification = await engineering.VerifyAsync(temporaryDirectory);
        Expect(
            verification.Started
            && verification.Passed
            && verification.Output.Contains("1 个页面", StringComparison.Ordinal),
            $"Valid WeChat Mini Program fixture failed structural verification: {verification.Output}");
    }
    finally
    {
        DeleteGeneratedTestDirectory(temporaryDirectory);
    }
});

await CheckAsync("engineering completeness rejects demo-shaped delivery", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-completeness-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporaryDirectory);
    try
    {
        var probe = new CodexRuntimeProbe(
            CodexRuntimeAvailability.Unavailable,
            "Unavailable",
            "Fixture",
            null,
            null,
            false);
        var before = new EngineeringWorkspaceSnapshot(
            temporaryDirectory,
            "fixture",
            probe,
            true,
            "main",
            [],
            0,
            0,
            "工作区干净，没有未提交变更。",
            ["app.csproj"],
            "dotnet test app.csproj --nologo",
            "工程已识别 · 工作区干净",
            DateTimeOffset.Now);
        var weak = before with
        {
            ChangedFiles = [new EngineeringChangedFile("??", "Program.cs")],
            Additions = 1,
            Diff =
                """
                +++ b/Program.cs
                @@ -0,0 +1 @@
                +throw new NotImplementedException(); // TODO: implement
                """
        };
        var service = new EngineeringCompletenessService();
        var cleanReview = new EngineeringCodeReviewResult(
            100,
            [],
            "fixture",
            DateTimeOffset.Now);
        var rejected = await service.AssessAndPersistAsync(
            "weak-project",
            "帮我开发一个完整应用程序",
            before,
            weak,
            true,
            true,
            cleanReview);
        Expect(
            !rejected.ReadyForDelivery
            && rejected.Findings.Any(item => item.Code == "placeholder-implementation")
            && rejected.Findings.Any(item => item.Code == "implausibly-small-project"),
            "A one-file placeholder project crossed the engineering delivery gate.");

        var complete = before with
        {
            ChangedFiles =
            [
                new EngineeringChangedFile("??", "app.csproj"),
                new EngineeringChangedFile("??", "Program.cs"),
                new EngineeringChangedFile("??", "AppTests.cs")
            ],
            Additions = 80,
            Diff =
                """
                +++ b/app.csproj
                +<Project Sdk="Microsoft.NET.Sdk"/>
                +++ b/Program.cs
                +public static class Program { public static int Main() => 0; }
                +++ b/AppTests.cs
                +public sealed class AppTests { }
                """
        };
        var accepted = await service.AssessAndPersistAsync(
            "complete-project",
            "帮我开发一个完整应用程序",
            before,
            complete,
            true,
            true,
            cleanReview);
        Expect(
            accepted.ReadyForDelivery && accepted.Score == 100,
            "A verified multi-file project fixture did not pass the completeness gate.");
        Expect(File.Exists(accepted.ArtifactPath), "Completeness report was not persisted.");

        await File.WriteAllTextAsync(
            Path.Combine(temporaryDirectory, "app.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"/>""");
        await File.WriteAllTextAsync(
            Path.Combine(temporaryDirectory, "Program.cs"),
            """throw new NotImplementedException(); // TODO: implement""");
        await File.WriteAllTextAsync(
            Path.Combine(temporaryDirectory, "AppTests.cs"),
            """public sealed class AppTests { }""");
        var nonGitBefore = before with
        {
            IsGitRepository = false,
            ChangedFiles = [],
            Diff = "该目录不是 Git 仓库；无法生成未提交差异。",
            WorkspaceFileCount = 0,
            WorkspaceFingerprint = "before"
        };
        var nonGitAfter = nonGitBefore with
        {
            WorkspaceFileCount = 3,
            WorkspaceFingerprint = "after"
        };
        var nonGitRejected = await service.AssessAndPersistAsync(
            "non-git-placeholder",
            "创建一个完整项目",
            nonGitBefore,
            nonGitAfter,
            true,
            true,
            cleanReview);
        Expect(
            !nonGitRejected.ReadyForDelivery
            && nonGitRejected.ChangedFileCount == 3
            && nonGitRejected.Findings.Any(item =>
                item.Code == "placeholder-implementation"),
            "A non-Git placeholder project bypassed fingerprint and workspace scanning.");

        await File.WriteAllTextAsync(
            Path.Combine(temporaryDirectory, "Program.cs"),
            """public static class Program { public static int Main() => 0; }""");
        var nonGitAccepted = await service.AssessAndPersistAsync(
            "non-git-complete",
            "创建一个完整项目",
            nonGitBefore,
            nonGitAfter with { WorkspaceFingerprint = "repaired" },
            true,
            true,
            cleanReview);
        Expect(
            nonGitAccepted.ReadyForDelivery,
            "A complete non-Git project did not pass the fingerprint-based gate.");
    }
    finally
    {
        DeleteGeneratedTestDirectory(temporaryDirectory);
    }
});

await CheckAsync("engineering evidence ledger redaction and decisions", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-evidence-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporaryDirectory);
    try
    {
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "README.md"), "NOVA evidence");
        var ledgerPath = Path.Combine(temporaryDirectory, "evidence.jsonl");
        var ledger = new EngineeringEvidenceLedgerService(ledgerPath);
        var host = new WorkspaceToolHost(
            temporaryDirectory,
            evidenceLedger: ledger,
            taskId: "evidence-smoke");

        await host.ExecuteAsync(
            "list_workspace_files",
            new JsonObject { ["directory"] = "", ["max_depth"] = 1 },
            CancellationToken.None);
        await host.RecordApprovalDecisionAsync(
            "write_text_file",
            new JsonObject
            {
                ["path"] = "result.md",
                ["content"] = "super-secret-content-must-not-enter-ledger"
            },
            approved: false);
        await ledger.AppendAsync(
            "evidence-smoke",
            temporaryDirectory,
            "tool",
            "remote-error",
            "https://example.test/callback?access_token=sensitive-token-value",
            "error",
            false,
            null,
            TimeSpan.Zero,
            null,
            "Bearer token-value-123456 and sk-super-secret-evidence-key",
            CancellationToken.None);

        var entries = ledger.ReadRecent(temporaryDirectory, "evidence-smoke");
        Expect(entries.Count == 3, "Evidence ledger did not retain tool and approval records.");
        Expect(entries.Any(item => item.Outcome == "completed"), "Completed tool evidence was omitted.");
        Expect(entries.Any(item => item.Outcome == "denied"), "Denied approval evidence was omitted.");
        Expect(entries.All(item => item.OutputSha256 is null or { Length: 64 }), "Evidence output hash is malformed.");
        var rawLedger = await File.ReadAllTextAsync(ledgerPath);
        Expect(
            !rawLedger.Contains("super-secret-content", StringComparison.Ordinal),
            "Evidence ledger stored raw write content.");
        Expect(
            !rawLedger.Contains("sensitive-token-value", StringComparison.Ordinal)
            && !rawLedger.Contains("token-value-123456", StringComparison.Ordinal)
            && !rawLedger.Contains("sk-super-secret-evidence-key", StringComparison.Ordinal),
            "Evidence ledger leaked credential-like values.");

        var unavailableLedgerHost = new WorkspaceToolHost(
            temporaryDirectory,
            evidenceLedger: new EngineeringEvidenceLedgerService(temporaryDirectory),
            taskId: "unavailable-ledger");
        var fallbackResult = await unavailableLedgerHost.ExecuteAsync(
            "list_workspace_files",
            new JsonObject { ["directory"] = "", ["max_depth"] = 1 },
            CancellationToken.None);
        Expect(
            fallbackResult.Contains("README.md", StringComparison.OrdinalIgnoreCase),
            "Evidence persistence failure replaced a valid tool result.");
    }
    finally
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
});

await CheckAsync("engineering before and after checkpoints", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-checkpoint-" + Guid.NewGuid().ToString("N"));
    var checkpointDirectory = Path.Combine(temporaryDirectory, "checkpoints");
    var workspace = Path.Combine(temporaryDirectory, "workspace");
    Directory.CreateDirectory(workspace);
    try
    {
        await File.WriteAllTextAsync(Path.Combine(workspace, "sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var workspaceService = new EngineeringWorkspaceService(
            new CodexRuntimeProbeService(Path.Combine(temporaryDirectory, "missing-codex.exe")),
            new EngineeringEvidenceLedgerService(Path.Combine(temporaryDirectory, "evidence.jsonl")));
        var checkpoints = new EngineeringCheckpointService(workspaceService, checkpointDirectory);

        var before = await checkpoints.CaptureAsync("checkpoint-smoke", "before", workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "Program.cs"), "System.Console.WriteLine(1);");
        var after = await checkpoints.CaptureAsync("checkpoint-smoke", "after", workspace);
        var loaded = checkpoints.LoadForTask("checkpoint-smoke");

        Expect(before is not null && after is not null, "Engineering checkpoints were not captured.");
        Expect(loaded.Count == 2, "Before/after checkpoints were not persisted.");
        Expect(
            loaded.All(item => item.DiffSha256.Length == 64),
            "Checkpoint diff fingerprints are malformed.");
    }
    finally
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
});

await CheckAsync("native text patch preview and write approval", async () =>
{
    var previewService = new TextPatchPreviewService();
    var preview = previewService.Create(
        "src/sample.txt",
        "alpha\nbeta\ngamma\n",
        "alpha\nBETA\ngamma\ndelta\n");
    Expect(preview.Additions == 2, "Patch preview addition count is incorrect.");
    Expect(preview.Deletions == 1, "Patch preview deletion count is incorrect.");
    Expect(preview.UnifiedDiff.Contains("-beta", StringComparison.Ordinal), "Removed line is missing from Patch preview.");
    Expect(preview.UnifiedDiff.Contains("+BETA", StringComparison.Ordinal), "Added replacement is missing from Patch preview.");
    Expect(preview.UnifiedDiff.Contains("+delta", StringComparison.Ordinal), "Added tail line is missing from Patch preview.");
    var newFilePreview = previewService.Create("new.txt", string.Empty, "created\n", originalExists: false);
    Expect(newFilePreview.IsNewFile, "New empty-origin file was not identified.");
    Expect(
        newFilePreview.UnifiedDiff.Contains("--- /dev/null", StringComparison.Ordinal),
        "New-file Patch did not use /dev/null origin.");
    var newlinePreview = previewService.Create("newline.txt", "same", "same\n");
    Expect(
        newlinePreview.UnifiedDiff.Contains("newline metadata", StringComparison.Ordinal),
        "Trailing newline-only change was invisible in Patch preview.");

    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-patch-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporaryDirectory);
    try
    {
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "sample.txt"), "before\n");
        var host = new WorkspaceToolHost(temporaryDirectory);
        var writeArguments = new JsonObject
        {
            ["path"] = "sample.txt",
            ["content"] = "after\n"
        };
        var approval = host.CreateApprovalRequest(
            "write_text_file",
            writeArguments);
        Expect(approval.PreviewKind == "unified-diff", "Write approval did not use native Patch preview.");
        Expect(approval.ChangePreview?.Contains("-before", StringComparison.Ordinal) == true, "Write approval omitted old content.");
        Expect(approval.ChangePreview?.Contains("+after", StringComparison.Ordinal) == true, "Write approval omitted proposed content.");
        Expect(
            !approval.ArgumentsPreview.Contains("\"after", StringComparison.Ordinal),
            "Write approval duplicated full proposed content in arguments.");

        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "sample.txt"), "changed elsewhere\n");
        try
        {
            await host.ExecuteAsync("write_text_file", writeArguments, CancellationToken.None);
            throw new Exception("Stale approved Patch overwrote a concurrently changed file.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("changed after Patch approval", StringComparison.Ordinal))
        {
        }

        var freshArguments = new JsonObject
        {
            ["path"] = "sample.txt",
            ["content"] = "approved result\n"
        };
        host.CreateApprovalRequest("write_text_file", freshArguments);
        await host.ExecuteAsync("write_text_file", freshArguments, CancellationToken.None);
        Expect(
            await File.ReadAllTextAsync(Path.Combine(temporaryDirectory, "sample.txt")) == "approved result\n",
            "Fresh approved Patch was not applied.");

        var editArguments = new JsonObject
        {
            ["path"] = "sample.txt",
            ["old_text"] = "approved",
            ["new_text"] = "verified",
            ["replace_all"] = false
        };
        var editApproval = host.CreateApprovalRequest("replace_text_in_file", editArguments);
        Expect(
            editApproval.PreviewKind == "unified-diff"
            && editApproval.ChangePreview?.Contains("+verified result", StringComparison.Ordinal) == true,
            "Exact text edit did not produce a reviewable Patch.");
        await host.ExecuteAsync("replace_text_in_file", editArguments, CancellationToken.None);
        Expect(
            await File.ReadAllTextAsync(Path.Combine(temporaryDirectory, "sample.txt")) == "verified result\n",
            "Approved exact text edit was not applied.");
        Expect(
            Directory.EnumerateFiles(
                Path.Combine(temporaryDirectory, ".nova", "recovery"),
                "sample.txt",
                SearchOption.AllDirectories).Any(),
            "Exact text edit did not preserve a recovery copy.");
    }
    finally
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
});

await CheckAsync("explicit isolated Git worktree creation", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-worktree-" + Guid.NewGuid().ToString("N"));
    var repository = Path.Combine(temporaryDirectory, "repository");
    var worktreeRoot = Path.Combine(temporaryDirectory, "isolated");
    Directory.CreateDirectory(repository);
    try
    {
        Expect((await RunCommandAsync(repository, "git", ["init"])).ExitCode == 0, "git init failed.");
        Expect((await RunCommandAsync(repository, "git", ["config", "user.email", "nova-smoke@example.test"])).ExitCode == 0, "git email config failed.");
        Expect((await RunCommandAsync(repository, "git", ["config", "user.name", "NOVA Smoke"])).ExitCode == 0, "git name config failed.");
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "NOVA worktree smoke");
        Expect((await RunCommandAsync(repository, "git", ["add", "README.md"])).ExitCode == 0, "git add failed.");
        Expect((await RunCommandAsync(repository, "git", ["commit", "-m", "initial"])).ExitCode == 0, "git commit failed.");

        var ledger = new EngineeringEvidenceLedgerService(
            Path.Combine(temporaryDirectory, "evidence.jsonl"));
        var recoveryRoot = Path.Combine(temporaryDirectory, "recovery");
        var worktrees = new GitWorktreeService(worktreeRoot, ledger, recoveryRoot);
        var session = await worktrees.CreateAsync(repository, "smoke");
        Expect(session.Created, $"Isolated worktree was not created: {session.Detail}");
        Expect(Directory.Exists(session.WorkspaceRoot), "Isolated worktree directory is missing.");
        Expect(File.Exists(Path.Combine(session.WorkspaceRoot, "README.md")), "Committed workspace content was not checked out.");
        Expect(
            Path.GetFullPath(session.WorkspaceRoot).StartsWith(
                Path.GetFullPath(worktreeRoot) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase),
            "Worktree escaped its managed root.");
        var list = await RunCommandAsync(repository, "git", ["worktree", "list", "--porcelain"]);
        Expect(
            list.Output.Contains(session.WorkspaceRoot.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
            || list.Output.Contains(session.WorkspaceRoot, StringComparison.OrdinalIgnoreCase),
            "Git did not register the isolated worktree.");
        Expect(
            ledger.ReadRecent(repository).Any(item => item.Action == "create-isolated-worktree"),
            "Worktree creation was not written to the evidence ledger.");

        try
        {
            await new GitWorktreeService(
                    Path.Combine(repository, ".nova", "worktrees"),
                    ledger)
                .CreateAsync(repository, "unsafe-nested");
            throw new Exception("Nested managed worktree root was accepted.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("目标无效", StringComparison.Ordinal))
        {
        }

        await File.WriteAllTextAsync(
            Path.Combine(session.WorkspaceRoot, "README.md"),
            "NOVA modified tracked content");
        await File.WriteAllTextAsync(
            Path.Combine(session.WorkspaceRoot, "notes.txt"),
            "untracked recovery content");
        var recycled = await worktrees.RecycleAsync(session.WorkspaceRoot);
        Expect(recycled.Succeeded, $"Managed Worktree recycle failed: {recycled.Detail}");
        Expect(!Directory.Exists(session.WorkspaceRoot), "Recycled Worktree directory still exists.");
        Expect(
            recycled.RecoveryPath is not null
            && File.Exists(Path.Combine(recycled.RecoveryPath, "changes.patch")),
            "Tracked Worktree changes were not preserved in a recovery Patch.");
        Expect(
            recycled.RecoveryPath is not null
            && File.Exists(Path.Combine(recycled.RecoveryPath, "untracked", "notes.txt")),
            "Untracked Worktree content was not copied to recovery.");
        Expect(
            ledger.ReadRecent(repository).Any(item => item.Action == "recycle-isolated-worktree"),
            "Worktree recycle was not written to the evidence ledger.");
    }
    finally
    {
        DeleteGeneratedTestDirectory(temporaryDirectory);
    }
});

await CheckAsync("isolated Worktree Tournament and guarded winner merge", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-tournament-" + Guid.NewGuid().ToString("N"));
    var repository = Path.Combine(temporaryDirectory, "repository");
    Directory.CreateDirectory(repository);
    try
    {
        Expect((await RunCommandAsync(repository, "git", ["init"])).ExitCode == 0, "git init failed.");
        Expect((await RunCommandAsync(repository, "git", ["config", "user.email", "nova-smoke@example.test"])).ExitCode == 0, "git email config failed.");
        Expect((await RunCommandAsync(repository, "git", ["config", "user.name", "NOVA Smoke"])).ExitCode == 0, "git name config failed.");
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "Tournament base");
        Expect((await RunCommandAsync(repository, "git", ["add", "README.md"])).ExitCode == 0, "git add failed.");
        Expect((await RunCommandAsync(repository, "git", ["commit", "-m", "initial"])).ExitCode == 0, "git commit failed.");

        var ledger = new EngineeringEvidenceLedgerService(
            Path.Combine(temporaryDirectory, "evidence.jsonl"));
        var worktrees = new GitWorktreeService(
            Path.Combine(temporaryDirectory, "worktrees"),
            ledger,
            Path.Combine(temporaryDirectory, "recovery"));
        var engineering = new EngineeringWorkspaceService(
            evidenceLedger: ledger,
            worktrees: worktrees);
        var tournamentService = new WorktreeTournamentService(
            worktrees,
            engineering,
            Path.Combine(temporaryDirectory, "artifacts"));
        var specs = new[]
        {
            new TournamentCandidateSpec("candidate-a", "openai", "model-a", "minimal"),
            new TournamentCandidateSpec("candidate-b", "deepseek", "model-b", "adversarial")
        };
        var tournament = await tournamentService.RunAsync(
            repository,
            "smoke-task",
            specs,
            async (spec, candidateRoot, token) =>
            {
                await File.WriteAllTextAsync(
                    Path.Combine(candidateRoot, $"{spec.Id}.txt"),
                    spec.Id == "candidate-a"
                        ? "winner content sk-tournament-secret-123456"
                        : "alternative content",
                    token);
                return new AgentRunResult(
                    spec.Id,
                    $"{spec.Id} implemented",
                    1,
                    spec.Provider,
                    spec.Model)
                {
                    MutatingToolCalls = 1
                };
            },
            runVerification: false);
        Expect(tournament.Candidates.Count == 2, "Tournament did not preserve both candidates.");
        Expect(
            tournament.Candidates.All(candidate =>
                candidate.IsEligible
                && File.Exists(candidate.PatchPath)
                && candidate.Patch.Contains("new file mode", StringComparison.Ordinal)),
            "Tournament did not export eligible untracked-file patches.");
        Expect(
            File.Exists(Path.Combine(tournament.ArtifactDirectory, "tournament.json")),
            "Tournament manifest was not persisted.");

        var snapshot = await engineering.InspectAsync(repository);
        var contract = await new TaskOutcomeContractService(
                engineering,
                Path.Combine(temporaryDirectory, "contracts"))
            .CreateAsync(
                "smoke-task",
                "Create a winner file",
                AgentExecutionMode.Autopilot,
                snapshot);
        var judgePrompt = TournamentCouncilService.BuildPrompt(
            "Create a winner file with sk-user-secret-123456",
            contract,
            tournament);
        Expect(
            !judgePrompt.Contains("sk-user-secret-123456", StringComparison.Ordinal)
            && !judgePrompt.Contains("sk-tournament-secret-123456", StringComparison.Ordinal),
            "Tournament Council prompt leaked a secret.");
        var decision = TournamentCouncilService.Parse(
            "openai",
            "judge",
            """
            WINNER: candidate-a
            VERDICT: SELECT
            CONFIDENCE: 91
            SUMMARY: Candidate A is smaller and satisfies the frozen contract.
            REASONS:
            - It has an exportable patch.
            """,
            specs.Select(item => item.Id).ToArray());
        Expect(
            decision.Selected && decision.WinnerId == "candidate-a",
            "Tournament Council did not parse a valid winner.");
        var invalidDecision = TournamentCouncilService.Parse(
            "openai",
            "judge",
            "WINNER: unknown\nVERDICT: SELECT\nCONFIDENCE: 100\nSUMMARY: invalid",
            specs.Select(item => item.Id).ToArray());
        Expect(
            invalidDecision.Verdict == "UNAVAILABLE",
            "Tournament Council accepted an unknown candidate.");

        var apply = await tournamentService.ApplyWinnerAsync(
            tournament,
            decision.WinnerId);
        Expect(apply.Applied, $"Winner Patch was not applied: {apply.Detail}");
        Expect(
            File.Exists(Path.Combine(repository, "candidate-a.txt"))
            && !File.Exists(Path.Combine(repository, "candidate-b.txt")),
            "Winner merge did not preserve candidate isolation.");
        var decisionPath = await tournamentService.PersistDecisionAsync(
            tournament,
            decision,
            applied: true);
        Expect(
            File.Exists(decisionPath)
            && (await File.ReadAllTextAsync(decisionPath))
                .Contains("\"applied\": true", StringComparison.Ordinal),
            "Tournament decision ledger did not persist the merge outcome.");
        await tournamentService.CleanupAsync(tournament);
        Expect(
            tournament.Candidates.All(candidate =>
                !Directory.Exists(candidate.Session.WorkspaceRoot)),
            "Tournament candidate Worktrees were not cleaned up.");
        Expect(
            !Directory.Exists(Path.Combine(temporaryDirectory, "recovery"))
            || !Directory.EnumerateFileSystemEntries(
                Path.Combine(temporaryDirectory, "recovery"),
                "*",
                SearchOption.AllDirectories).Any(),
            "Successful Tournament cleanup created redundant recovery copies.");
        Expect(
            ledger.ReadRecent(repository).Any(item =>
                item.Action == "discard-tournament-candidate"),
            "Tournament candidate cleanup was not written to the evidence ledger.");
    }
    finally
    {
        DeleteGeneratedTestDirectory(temporaryDirectory);
    }
});

await CheckAsync("Agent Mesh planning and hard write ownership", async () =>
{
    var plan = AgentMeshPlannerService.Parse(
        """
        {
          "strategy": "Parallelize independent API and UI work, then add integration tests.",
          "packages": [
            {
              "id": "api",
              "title": "API worker",
              "instruction": "Implement the API contract and leave an observable endpoint result.",
              "owned_paths": ["src/api/"],
              "depends_on": []
            },
            {
              "id": "ui",
              "title": "UI worker",
              "instruction": "Implement the UI surface and expose an observable rendered state.",
              "owned_paths": ["src/ui/"],
              "depends_on": []
            },
            {
              "id": "tests",
              "title": "Integration test worker",
              "instruction": "Add integration coverage that proves the API and UI contract together.",
              "owned_paths": ["tests/"],
              "depends_on": ["api", "ui"]
            }
          ]
        }
        """);
    var waves = plan.BuildWaves();
    Expect(
        waves.Count == 2
        && waves[0].Count == 2
        && waves[1].Single().Id == "tests",
        "Agent Mesh planner did not preserve dependency waves.");
    try
    {
        AgentMeshPlannerService.Parse(
            """
            {
              "strategy": "Invalid overlapping ownership",
              "packages": [
                {
                  "id": "one",
                  "title": "First worker",
                  "instruction": "Implement the first independently observable change.",
                  "owned_paths": ["src/"],
                  "depends_on": []
                },
                {
                  "id": "two",
                  "title": "Second worker",
                  "instruction": "Implement the second independently observable change.",
                  "owned_paths": ["src/file.cs"],
                  "depends_on": []
                }
              ]
            }
            """);
        throw new Exception("Agent Mesh accepted overlapping path ownership.");
    }
    catch (InvalidOperationException exception) when (
        exception.Message.Contains("overlap", StringComparison.OrdinalIgnoreCase))
    {
    }

    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-mesh-ownership-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporaryDirectory);
    try
    {
        var tools = new WorkspaceToolHost(
            temporaryDirectory,
            allowedWriteScopes: ["owned/", "exact.txt"]);
        var allowed = await tools.ExecuteAsync(
            "write_text_file",
            new JsonObject
            {
                ["path"] = "owned/new.txt",
                ["content"] = "owned"
            },
            CancellationToken.None);
        Expect(
            allowed.Contains("\"status\":\"written\"", StringComparison.Ordinal),
            "Agent Mesh blocked an owned write.");
        await tools.ExecuteAsync(
            "write_text_file",
            new JsonObject
            {
                ["path"] = "exact.txt",
                ["content"] = "exact"
            },
            CancellationToken.None);
        try
        {
            await tools.ExecuteAsync(
                "write_text_file",
                new JsonObject
                {
                    ["path"] = "outside.txt",
                    ["content"] = "denied"
                },
                CancellationToken.None);
            throw new Exception("Agent Mesh allowed an ownership escape.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("ownership violation", StringComparison.OrdinalIgnoreCase))
        {
        }
        Expect(
            !File.Exists(Path.Combine(temporaryDirectory, "outside.txt")),
            "Ownership escape wrote a file before rejection.");
    }
    finally
    {
        DeleteGeneratedTestDirectory(temporaryDirectory);
    }
});

await CheckAsync("Agent Mesh dependency waves and guarded integration", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-agent-mesh-" + Guid.NewGuid().ToString("N"));
    var repository = Path.Combine(temporaryDirectory, "repository");
    Directory.CreateDirectory(repository);
    try
    {
        Expect((await RunCommandAsync(repository, "git", ["init"])).ExitCode == 0, "git init failed.");
        Expect((await RunCommandAsync(repository, "git", ["config", "user.email", "nova-smoke@example.test"])).ExitCode == 0, "git email config failed.");
        Expect((await RunCommandAsync(repository, "git", ["config", "user.name", "NOVA Smoke"])).ExitCode == 0, "git name config failed.");
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "Agent Mesh base");
        Expect((await RunCommandAsync(repository, "git", ["add", "README.md"])).ExitCode == 0, "git add failed.");
        Expect((await RunCommandAsync(repository, "git", ["commit", "-m", "initial"])).ExitCode == 0, "git commit failed.");

        var ledger = new EngineeringEvidenceLedgerService(
            Path.Combine(temporaryDirectory, "evidence.jsonl"));
        var worktrees = new GitWorktreeService(
            Path.Combine(temporaryDirectory, "worktrees"),
            ledger,
            Path.Combine(temporaryDirectory, "recovery"));
        var engineering = new EngineeringWorkspaceService(
            evidenceLedger: ledger,
            worktrees: worktrees);
        var meshService = new AgentMeshService(
            worktrees,
            engineering,
            Path.Combine(temporaryDirectory, "artifacts"));
        var meshPlan = AgentMeshPlannerService.Parse(
            """
            {
              "strategy": "Build two independent components before the integration contract.",
              "packages": [
                {
                  "id": "component-a",
                  "title": "Component A",
                  "instruction": "Create component A with a stable observable text contract.",
                  "owned_paths": ["component-a/"],
                  "depends_on": []
                },
                {
                  "id": "component-b",
                  "title": "Component B",
                  "instruction": "Create component B with a stable observable text contract.",
                  "owned_paths": ["component-b/"],
                  "depends_on": []
                },
                {
                  "id": "integration",
                  "title": "Integration worker",
                  "instruction": "Read both components and create an integration proof file.",
                  "owned_paths": ["integration/"],
                  "depends_on": ["component-a", "component-b"]
                }
              ]
            }
            """);
        var mesh = await meshService.RunAsync(
            repository,
            "mesh-smoke",
            meshPlan,
            async (package, packageRoot, waveIndex, _, token) =>
            {
                var directory = Path.Combine(packageRoot, package.Id);
                Directory.CreateDirectory(directory);
                if (package.Id == "integration")
                {
                    Expect(
                        waveIndex == 1
                        && File.Exists(Path.Combine(packageRoot, "component-a", "result.txt"))
                        && File.Exists(Path.Combine(packageRoot, "component-b", "result.txt")),
                        "Dependent Mesh worker did not receive prior-wave commits.");
                }
                await File.WriteAllTextAsync(
                    Path.Combine(directory, "result.txt"),
                    package.Id == "component-a"
                        ? "component A sk-mesh-secret-123456"
                        : package.Id,
                    token);
                return new AgentRunResult(
                    package.Id,
                    $"{package.Id} completed",
                    1,
                    package.Id == "component-b" ? "deepseek" : "openai",
                    "mesh-model")
                {
                    MutatingToolCalls = 1
                };
            },
            runVerification: false);
        Expect(
            mesh.IsEligible
            && mesh.Packages.Count == 3
            && mesh.Waves.Count == 2,
            "Agent Mesh did not produce an eligible two-wave integration.");
        Expect(
            mesh.CombinedPatch.Contains("component-a/result.txt", StringComparison.Ordinal)
            && mesh.CombinedPatch.Contains("component-b/result.txt", StringComparison.Ordinal)
            && mesh.CombinedPatch.Contains("integration/result.txt", StringComparison.Ordinal),
            "Agent Mesh combined Patch omitted a work package.");
        var contract = await new TaskOutcomeContractService(
                engineering,
                Path.Combine(temporaryDirectory, "contracts"))
            .CreateAsync(
                "mesh-smoke",
                "Build integrated components",
                AgentExecutionMode.Autopilot,
                await engineering.InspectAsync(repository));
        var councilPrompt = AgentMeshCouncilService.BuildPrompt(
            "Build components without exposing sk-user-mesh-secret-123456",
            contract,
            mesh);
        Expect(
            !councilPrompt.Contains("sk-user-mesh-secret-123456", StringComparison.Ordinal)
            && !councilPrompt.Contains("sk-mesh-secret-123456", StringComparison.Ordinal),
            "Agent Mesh Council prompt leaked a secret.");
        var decision = AgentMeshCouncilService.Parse(
            "openai",
            "judge",
            """
            VERDICT: ACCEPT
            CONFIDENCE: 93
            SUMMARY: Ownership is disjoint and the dependency wave is integrated.
            FINDINGS:
            - All package patches are present.
            """);
        Expect(decision.Accepted, "Agent Mesh Council did not parse ACCEPT.");
        var apply = await meshService.ApplyAsync(mesh);
        Expect(apply.Applied, $"Agent Mesh Combined Patch was not applied: {apply.Detail}");
        Expect(
            File.Exists(Path.Combine(repository, "component-a", "result.txt"))
            && File.Exists(Path.Combine(repository, "component-b", "result.txt"))
            && File.Exists(Path.Combine(repository, "integration", "result.txt")),
            "Agent Mesh integration did not reach the source workspace.");
        var decisionPath = await meshService.PersistDecisionAsync(
            mesh,
            decision,
            applied: true);
        Expect(File.Exists(decisionPath), "Agent Mesh decision ledger was not persisted.");
        await meshService.CleanupAsync(mesh);
        Expect(
            !Directory.Exists(mesh.IntegrationSession.WorkspaceRoot),
            "Agent Mesh integration Worktree was not cleaned up.");
    }
    finally
    {
        DeleteGeneratedTestDirectory(temporaryDirectory);
    }
});

await CheckAsync("selective Git Hunk stage and revert", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-hunks-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporaryDirectory);
    try
    {
        Expect((await RunCommandAsync(temporaryDirectory, "git", ["init"])).ExitCode == 0, "git init failed.");
        Expect((await RunCommandAsync(temporaryDirectory, "git", ["config", "user.email", "nova-smoke@example.test"])).ExitCode == 0, "git email config failed.");
        Expect((await RunCommandAsync(temporaryDirectory, "git", ["config", "user.name", "NOVA Smoke"])).ExitCode == 0, "git name config failed.");
        var originalLines = Enumerable.Range(1, 24).Select(index => $"line {index}").ToArray();
        await File.WriteAllLinesAsync(Path.Combine(temporaryDirectory, "sample.txt"), originalLines);
        Expect((await RunCommandAsync(temporaryDirectory, "git", ["add", "sample.txt"])).ExitCode == 0, "git add failed.");
        Expect((await RunCommandAsync(temporaryDirectory, "git", ["commit", "-m", "initial"])).ExitCode == 0, "git commit failed.");

        var changedLines = originalLines.ToArray();
        changedLines[1] = "line 2 changed";
        changedLines[21] = "line 22 changed";
        await File.WriteAllLinesAsync(Path.Combine(temporaryDirectory, "sample.txt"), changedLines);
        var ledger = new EngineeringEvidenceLedgerService(Path.Combine(temporaryDirectory, "evidence.jsonl"));
        var service = new GitHunkReviewService(ledger);
        var hunks = await service.GetUnstagedHunksAsync(temporaryDirectory);
        Expect(hunks.Count == 2, $"Expected two separate hunks, got {hunks.Count}.");

        var staged = await service.StageAsync(temporaryDirectory, [hunks[0].Id]);
        Expect(staged.Succeeded, $"Selective Hunk staging failed: {staged.Detail}");
        var remaining = await service.GetUnstagedHunksAsync(temporaryDirectory);
        Expect(remaining.Count == 1, "Staging one Hunk did not leave exactly one unstaged Hunk.");
        var cachedDiff = await RunCommandAsync(temporaryDirectory, "git", ["diff", "--cached"]);
        Expect(cachedDiff.Output.Contains("line 2 changed", StringComparison.Ordinal), "Selected Hunk was not staged.");
        Expect(!cachedDiff.Output.Contains("line 22 changed", StringComparison.Ordinal), "Unselected Hunk was staged.");

        var reverted = await service.RevertAsync(temporaryDirectory, [remaining[0].Id]);
        Expect(reverted.Succeeded, $"Selective Hunk revert failed: {reverted.Detail}");
        Expect((await service.GetUnstagedHunksAsync(temporaryDirectory)).Count == 0, "Reverted Hunk remains unstaged.");
        var finalText = await File.ReadAllTextAsync(Path.Combine(temporaryDirectory, "sample.txt"));
        Expect(finalText.Contains("line 2 changed", StringComparison.Ordinal), "Staged Hunk disappeared from working tree.");
        Expect(!finalText.Contains("line 22 changed", StringComparison.Ordinal), "Reverted Hunk remains in working tree.");
    }
    finally
    {
        DeleteGeneratedTestDirectory(temporaryDirectory);
    }
});

await CheckAsync("local and Codex read-only code review", async () =>
{
    var snapshot = new EngineeringWorkspaceSnapshot(
        @"D:\Agent",
        "Agent",
        new CodexRuntimeProbe(CodexRuntimeAvailability.Unavailable, "optional", "", null, null, false),
        true,
        "main",
        [new EngineeringChangedFile("M", "src/app.cs")],
        3,
        0,
        """
        diff --git a/src/app.cs b/src/app.cs
        --- a/src/app.cs
        +++ b/src/app.cs
        @@ -1,1 +1,4 @@
         class App {}
        +var apiKey = "sk-super-secret-review-key";
        +Thread.Sleep(1000);
        +// TODO remove blocking path
        """,
        ["src/app.csproj"],
        "dotnet build src/app.csproj",
        "changes",
        DateTimeOffset.Now);
    var review = new EngineeringCodeReviewService().Review(snapshot);
    Expect(review.Findings.Any(item => item.Severity == "HIGH"), "Local review missed credential exposure.");
    Expect(review.Findings.Any(item => item.Rule == "blocking-call"), "Local review missed blocking call.");
    Expect(review.Findings.Any(item => item.Rule == "test-coverage"), "Local review missed absent test changes.");

    var executable = Environment.ProcessPath
                     ?? throw new Exception("Current smoke-test executable path is unavailable.");
    var codex = new CodexRuntimeProbeService(executable);
    var probe = await codex.ProbeExecutableAsync();
    Expect(probe.Availability == CodexRuntimeAvailability.Ready, "Codex fixture did not pass capability probing.");
    var codexReview = await codex.RunReadOnlyReviewAsync(@"D:\Agent");
    Expect(codexReview.Succeeded, $"Codex read-only review fixture failed: {codexReview.Detail}");
    Expect(codexReview.Review.Contains("只读审查完成", StringComparison.Ordinal), "Codex review text was not parsed.");
});

await CheckAsync("AgentOS execution-mode policy boundary", async () =>
{
    var host = new WorkspaceToolHost(@"D:\Agent");
    var askTools = AgentExecutionPolicy.FilterTools(
        host.Definitions,
        AgentExecutionMode.Ask);
    var planTools = AgentExecutionPolicy.FilterTools(
        host.Definitions,
        AgentExecutionMode.Plan);
    var buildTools = AgentExecutionPolicy.FilterTools(
        host.Definitions,
        AgentExecutionMode.Build);
    var askNames = askTools
        .Select(tool => tool["name"]?.GetValue<string>() ?? string.Empty)
        .ToHashSet(StringComparer.Ordinal);
    var buildNames = buildTools
        .Select(tool => tool["name"]?.GetValue<string>() ?? string.Empty)
        .ToHashSet(StringComparer.Ordinal);

    Expect(!askNames.Contains("write_text_file"), "Ask mode exposed workspace mutation.");
    Expect(!askNames.Contains("run_workspace_command"), "Ask mode exposed process execution.");
    Expect(!askNames.Contains("inspect_mcp_server_tools"), "Ask mode exposed MCP process startup.");
    Expect(!askNames.Contains("index_workspace_knowledge"), "Ask mode exposed index mutation.");
    Expect(
        planTools.Count == askTools.Count,
        "Plan and Ask should share the same read-only tool boundary.");
    Expect(buildNames.Contains("write_text_file"), "Build mode removed workspace mutation.");
    Expect(
        AgentExecutionPolicy.GetSystemContract(AgentExecutionMode.Autopilot)
            .Contains("verification", StringComparison.OrdinalIgnoreCase),
        "Autopilot system contract omitted verification.");
    await Task.CompletedTask;
});

await CheckAsync("Autopilot automatic child-agent planning", async () =>
{
    var buildPlan = AutomaticAgentPlanner.Create(
        "优化这个界面的性能和交互",
        AgentExecutionMode.Build,
        allowParallelDelegation: true);
    Expect(buildPlan is null, "Build mode unexpectedly auto-created extra model requests.");

    var plan = AutomaticAgentPlanner.Create(
        "深度优化整个界面的 UI、交互动画和性能，并设计完整回归测试",
        AgentExecutionMode.Autopilot,
        allowParallelDelegation: true)
        ?? throw new Exception("Autopilot did not create an automatic child-agent plan.");
    Expect(plan.Tasks.Count == 3, "Autopilot plan did not create three bounded workers.");
    Expect(
        plan.Tasks.Select(task => task.Title).Distinct(StringComparer.Ordinal).Count() == 3,
        "Autopilot worker roles are not distinct.");
    Expect(
        plan.Tasks.All(task =>
            task.Instruction.Contains("只读", StringComparison.Ordinal)
            || task.Instruction.Contains("不要修改", StringComparison.Ordinal)),
        "Automatic workers are not explicitly read-only.");
    Expect(
        plan.ToApprovalPreview().Contains("体验审查员", StringComparison.Ordinal)
        && plan.ToArguments()["tasks"]?.AsArray().Count == 3,
        "Automatic worker approval preview does not expose exact tasks.");
    var executionPlan = JsonNode.Parse(plan.ToExecutionPlanPayload())?.AsObject();
    Expect(
        executionPlan?["steps"]?.AsArray().Count == 3
        && executionPlan["steps"]?[0]?["agent"]?.GetValue<string>() == "子 Agent 1",
        "Automatic worker plan is not projectable into the live task-plan UI.");
    var workshopPlan = AutomaticAgentPlanner.Create(
        "[NOVA_AGENT_WORKSHOP]\n为跨境电商内容生产设计专业 Agent",
        AgentExecutionMode.Goal,
        allowParallelDelegation: true)
        ?? throw new Exception("Agent Workshop did not create a council plan.");
    Expect(
        workshopPlan.Tasks.Select(task => task.Title).SequenceEqual(
            new[] { "行业架构师", "工作流架构师", "信任审查官" }),
        "Agent Workshop did not reuse the expected three-role council.");
    Expect(
        workshopPlan.Tasks.All(task =>
            task.Instruction.Contains("不要修改", StringComparison.Ordinal)
            || task.Instruction.Contains("只读", StringComparison.Ordinal)),
        "Agent Workshop council roles are not constrained to read-only analysis.");
    var approvalPreview = plan.ToApprovalPreview();
    Expect(
        approvalPreview.Length < 1200
        && !approvalPreview.Contains("完整回归测试", StringComparison.Ordinal)
        && approvalPreview.Contains("不能写文件或执行命令", StringComparison.Ordinal),
        "Automatic worker approval preview leaked the full task context instead of a bounded summary.");

    var openAiRuntime = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\Services\OpenAIResponsesAgentRuntime.cs");
    var deepSeekRuntime = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\Services\DeepSeekChatAgentRuntime.cs");
    Expect(
        openAiRuntime.Contains("TryRunAutomaticParallelAsync", StringComparison.Ordinal)
        && deepSeekRuntime.Contains("TryRunAutomaticParallelAsync", StringComparison.Ordinal),
        "Provider runtimes do not start automatic parallel groups.");
    Expect(
        openAiRuntime.Contains("AllowParallelDelegation = false", StringComparison.Ordinal)
        && deepSeekRuntime.Contains("AllowParallelDelegation = false", StringComparison.Ordinal),
        "Child agents can recursively create unbounded workers.");

    var activeWorkers = 0;
    var peakWorkers = 0;
    var events = new List<AgentRuntimeEvent>();
    var orchestrator = new ParallelAgentOrchestrator(
        new HttpClient(),
        async (task, index, cancellationToken) =>
        {
            var active = Interlocked.Increment(ref activeWorkers);
            while (true)
            {
                var observed = Volatile.Read(ref peakWorkers);
                if (active <= observed
                    || Interlocked.CompareExchange(ref peakWorkers, active, observed) == observed)
                {
                    break;
                }
            }
            try
            {
                await Task.Delay(80, cancellationToken);
                return $"{index}:{task.Title}";
            }
            finally
            {
                Interlocked.Decrement(ref activeWorkers);
            }
        });
    var result = await orchestrator.ExecuteAsync(
        new AgentRunRequest(
            "parallel-smoke",
            "并行验证",
            @"D:\Agent",
            "memory-only",
            "openai",
            "gpt-test",
            AgentExecutionMode.Autopilot),
        plan.ToArguments(),
        runtimeEvent =>
        {
            lock (events)
            {
                events.Add(runtimeEvent);
            }
            return Task.CompletedTask;
        },
        CancellationToken.None);
    Expect(peakWorkers == 3, "Automatic child agents did not execute concurrently.");
    Expect(
        events.Any(item =>
            item.Kind == AgentRuntimeEventKind.BatchCompleted
            && item.Detail.Contains("3/3", StringComparison.Ordinal)),
        "Parallel worker completion was not surfaced to the execution stream.");
    Expect(
        result.Contains("\"worker_count\":3", StringComparison.Ordinal),
        "Parallel worker results were not returned to the commander.");
});

await CheckAsync("AgentOS kernel persistence and event ledger", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-agentos-kernel-" + Guid.NewGuid().ToString("N"));
    try
    {
        var kernel = new AgentOsKernel(temporaryDirectory);
        var firstBoot = await kernel.BootAsync();
        await kernel.SetExecutionModeAsync(AgentExecutionMode.Autopilot, "smoke");
        await kernel.ReportServiceAsync(
            "runtime",
            "Model Runtime",
            AgentOsServiceHealth.Ready,
            "Smoke provider ready",
            "smoke");
        await kernel.PublishEventAsync(
            "task",
            "Smoke Task",
            "Execution accepted.",
            "smoke-task");

        var restoredKernel = new AgentOsKernel(temporaryDirectory);
        var restored = await restoredKernel.BootAsync();
        Expect(firstBoot.KernelVersion == "1.0.0", "Kernel exposed an incorrect version.");
        Expect(
            restored.ExecutionMode == AgentExecutionMode.Autopilot,
            "Kernel did not restore the execution policy.");
        Expect(
            restored.Services.Any(service =>
                service.Id == "runtime"
                && service.Health == AgentOsServiceHealth.Ready),
            "Kernel did not restore service health.");
        Expect(
            restored.RecentEvents.Any(item =>
                item.CorrelationId == "smoke-task"),
            "Kernel event ledger did not survive reboot.");
    }
    finally
    {
        DeleteGeneratedTestDirectory(temporaryDirectory);
    }
});

await CheckAsync("unified monotonic execution ledger and projection replay", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-execution-ledger-" + Guid.NewGuid().ToString("N"));
    try
    {
        var kernelRoot = Path.Combine(temporaryDirectory, "kernel");
        var kernel = new AgentOsKernel(kernelRoot);
        var boot = await kernel.BootAsync();
        var task = new TaskItem
        {
            Id = "projection-smoke",
            Title = "Project one task truth",
            Description = "Verify the monotonic execution ledger",
            WorkspaceRoot = temporaryDirectory,
            State = TaskState.Running,
            Stage = "Starting",
            Progress = 4
        };
        var supervisor = new AgentSupervisorService(
            Path.Combine(temporaryDirectory, "supervisor"));
        await supervisor.BootAsync(boot.BootId);
        await supervisor.AcquireAsync(task);

        var started = await kernel.PublishTaskEventAsync(
            "task",
            "Mission Control",
            "Task started.",
            task);
        var graphService = new AgentTaskGraphService(
            Path.Combine(temporaryDirectory, "graphs"));
        await graphService.CreateAsync(
            task.Id,
            task.Title,
            AgentExecutionMode.Build,
            executionSequence: started.Sequence);
        await supervisor.HeartbeatAsync(
            task.Id,
            task.Stage,
            forcePersist: true,
            executionSequence: started.Sequence);

        var concurrent = await Task.WhenAll(
            Enumerable.Range(0, 32).Select(index =>
                kernel.PublishEventAsync(
                    "fault-injection",
                    "Ledger Smoke",
                    $"Concurrent event {index:D2}",
                    task.Id)));
        var orderedSequences = concurrent
            .Select(entry => entry.Sequence)
            .OrderBy(sequence => sequence)
            .ToArray();
        Expect(
            orderedSequences.Distinct().Count() == 32
            && orderedSequences.Zip(
                orderedSequences.Skip(1),
                (left, right) => right > left).All(result => result),
            "Concurrent commits reused or reordered a monotonic execution sequence.");

        task.State = TaskState.Completed;
        task.Stage = "Proof accepted";
        task.Progress = 100;
        var completed = await kernel.PublishTaskEventAsync(
            "task",
            "Mission Control",
            "Task completed with fresh evidence.",
            task);
        await graphService.CompleteAsync(
            task.Id,
            true,
            task.Stage,
            executionSequence: completed.Sequence);
        await supervisor.ReleaseAsync(
            task,
            executionSequence: completed.Sequence);
        var snapshots = new TaskSnapshotService(
            Path.Combine(temporaryDirectory, "tasks"));
        await snapshots.SaveAsync(task);

        var graph = graphService.GetSnapshot(task.Id);
        var lease = supervisor.GetSnapshot().Leases.Single(item =>
            item.TaskId == task.Id);
        var snapshot = snapshots.LoadAll().Single(item =>
            item.TaskId == task.Id);
        Expect(
            task.ExecutionSequence == completed.Sequence
            && graph?.ExecutionSequence == completed.Sequence
            && lease.ExecutionSequence == completed.Sequence
            && snapshot.ExecutionSequence == completed.Sequence,
            "Task, graph, supervisor and snapshot projections did not share one committed sequence.");

        File.Delete(Path.Combine(kernelRoot, "kernel-state.json"));
        await File.AppendAllTextAsync(
            Path.Combine(kernelRoot, "execution-events.jsonl"),
            "{\"Sequence\":");
        var replayedKernel = new AgentOsKernel(kernelRoot);
        await replayedKernel.BootAsync();
        var replayed = replayedKernel.GetTaskProjection(task.Id);
        Expect(
            replayed?.Sequence == completed.Sequence
            && replayed.TaskState == TaskState.Completed
            && replayed.Stage == task.Stage,
            "A missing state snapshot or torn ledger tail changed the replayed task truth.");
    }
    finally
    {
        DeleteGeneratedTestDirectory(temporaryDirectory);
    }
});

await CheckAsync("GA fault injection matrix across five execution boundaries", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-ga-fault-matrix-" + Guid.NewGuid().ToString("N"));
    try
    {
        var receiptService = new SideEffectReceiptService(
            Path.Combine(temporaryDirectory, "receipts"));

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var modelFailure = TaskFailureClassifier.Classify(
                $"model-{attempt:D2}",
                new HttpRequestException(
                    "provider interrupted sk-secret-should-be-redacted"),
                "model request");
            Expect(
                modelFailure.Kind == TaskFailureKind.Network
                && modelFailure.RecoveryAction == FailureRecoveryAction.Retry
                && !modelFailure.UserMessage.Contains(
                    "sk-secret",
                    StringComparison.Ordinal),
                "Model-boundary injection was misclassified or leaked a credential.");

            var toolFailure = TaskFailureClassifier.Classify(
                $"tool-{attempt:D2}",
                new IOException("tool pipe stopped before completion"),
                "tool execution");
            Expect(
                toolFailure.Kind == TaskFailureKind.Tool
                && toolFailure.Retryable
                && !toolFailure.BlocksAutomaticReplay,
                "Tool-boundary injection did not preserve a recoverable terminal.");

            var operationId = $"write-{attempt:D2}";
            var intent = await receiptService.BeginAsync(
                "write-boundary",
                operationId,
                "write_text_file",
                $"file-{attempt:D2}.txt",
                $"{{\"attempt\":{attempt}}}",
                $"approval-{attempt:D2}",
                "before");
            try
            {
                await receiptService.BeginAsync(
                    "write-boundary",
                    operationId,
                    "write_text_file",
                    $"file-{attempt:D2}.txt",
                    $"{{\"attempt\":{attempt}}}",
                    $"approval-{attempt:D2}",
                    "before");
                throw new Exception(
                    "An interrupted write intent was replayed automatically.");
            }
            catch (UncertainSideEffectException)
            {
            }
            await receiptService.CommitAsync(
                intent.Receipt,
                "after",
                "committed once");
            var replay = await receiptService.BeginAsync(
                "write-boundary",
                operationId,
                "write_text_file",
                $"file-{attempt:D2}.txt",
                $"{{\"attempt\":{attempt}}}",
                $"approval-{attempt:D2}",
                "before");
            Expect(
                replay.IsCommittedReplay,
                "A committed write was not deduplicated by its idempotency receipt.");

            var verificationFailure = TaskFailureClassifier.Classify(
                $"verification-{attempt:D2}",
                new InvalidOperationException("proof signal did not pass"),
                "independent verification");
            Expect(
                verificationFailure.Kind == TaskFailureKind.Verification
                && verificationFailure.RecoveryAction == FailureRecoveryAction.Reverify,
                "Verification-boundary injection did not request targeted re-verification.");

            var deliveryRoot = Path.Combine(
                temporaryDirectory,
                "delivery",
                attempt.ToString("D2"));
            var deliveryKernel = new AgentOsKernel(deliveryRoot);
            await deliveryKernel.BootAsync();
            var deliveryTask = new TaskItem
            {
                Id = $"delivery-{attempt:D2}",
                Title = "Delivery boundary",
                Description = "Persist terminal truth",
                WorkspaceRoot = temporaryDirectory,
                State = TaskState.Completed,
                Stage = "Evidence committed",
                Progress = 100
            };
            var terminal = await deliveryKernel.PublishTaskEventAsync(
                "delivery",
                "Proof Gate",
                "Delivery committed.",
                deliveryTask);
            File.Delete(Path.Combine(deliveryRoot, "kernel-state.json"));
            await File.AppendAllTextAsync(
                Path.Combine(deliveryRoot, "execution-events.jsonl"),
                "{\"Sequence\":");
            var replayed = new AgentOsKernel(deliveryRoot);
            await replayed.BootAsync();
            var projection = replayed.GetTaskProjection(deliveryTask.Id);
            Expect(
                projection?.Sequence == terminal.Sequence
                && projection.TaskState == TaskState.Completed,
                "Delivery-boundary injection lost or duplicated the committed terminal.");
        }

        Expect(
            receiptService.LoadForTask("write-boundary").Count == 20,
            "Write fault injection created duplicate side-effect receipts.");
    }
    finally
    {
        DeleteGeneratedTestDirectory(temporaryDirectory);
    }
});

await CheckAsync("AgentOS task graph and resource governor", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-agentos-graph-" + Guid.NewGuid().ToString("N"));
    try
    {
        var graphService = new AgentTaskGraphService(temporaryDirectory);
        var graph = await graphService.CreateAsync(
            "agentos-smoke",
            "Implement control plane",
            AgentExecutionMode.Autopilot);
        Expect(graph.Nodes.Count == 9, "Autopilot did not create the nine-stage orchestration DAG.");
        Expect(
            graph.Nodes[3].Dependencies.Count == 2,
            "Autopilot implementation node did not preserve parallel dependencies.");

        await graphService.ApplyRuntimeEventAsync(
            graph.TaskId,
            new AgentRuntimeEvent(
                AgentRuntimeEventKind.Thinking,
                "NOVA",
                "Establish mission",
                "Planning",
                12,
                1));
        var running = graphService.GetSnapshot(graph.TaskId);
        Expect(
            running?.Nodes.Any(node => node.State == AgentGraphNodeState.Running) == true,
            "Runtime event did not activate a graph node.");
        await graphService.ApplyRuntimeEventAsync(
            graph.TaskId,
            new AgentRuntimeEvent(
                AgentRuntimeEventKind.ToolRunning,
                "候选 candidate-a · Builder",
                "Implement isolated candidate",
                "Writing only inside the candidate Worktree",
                45,
                2));
        var candidateRunning = graphService.GetSnapshot(graph.TaskId);
        Expect(
            candidateRunning?.Nodes.Any(node =>
                node.Role == "implementer"
                && node.State == AgentGraphNodeState.Running) == true,
            "Tournament candidate event did not activate the candidate implementation node.");
        await graphService.ApplyRuntimeEventAsync(
            graph.TaskId,
            new AgentRuntimeEvent(
                AgentRuntimeEventKind.ToolCompleted,
                "候选验证竞技场",
                "Verify isolated candidates",
                "One candidate is eligible",
                71,
                1));
        var candidateVerifier = graphService.GetSnapshot(graph.TaskId);
        Expect(
            candidateVerifier?.Nodes.Any(node =>
                node.Role == "reviewer"
                && node.State == AgentGraphNodeState.Running) == true,
            "Candidate verification event did not activate the verification arena.");
        await graphService.ApplyRuntimeEventAsync(
            graph.TaskId,
            new AgentRuntimeEvent(
                AgentRuntimeEventKind.Thinking,
                "Tournament Council · reviewer",
                "Compare candidates",
                "Selecting only from eligible evidence",
                82,
                1));
        var tournamentJudge = graphService.GetSnapshot(graph.TaskId);
        Expect(
            tournamentJudge?.Nodes.Any(node =>
                node.Role == "adjudicator"
                && node.State == AgentGraphNodeState.Running) == true,
            "Tournament Council event did not activate the judge node.");
        await graphService.ApplyRuntimeEventAsync(
            graph.TaskId,
            new AgentRuntimeEvent(
                AgentRuntimeEventKind.Thinking,
                "验证 Council · DeepSeek",
                "Adversarial verification",
                "Reviewing the implementation evidence",
                90,
                1));
        var councilRunning = graphService.GetSnapshot(graph.TaskId);
        Expect(
            councilRunning?.Nodes.Any(node =>
                node.Role == "adversarial-reviewer"
                && node.State == AgentGraphNodeState.Running) == true,
            "Independent Council event did not activate the adversarial review node.");
        await graphService.CompleteAsync(
            graph.TaskId,
            true,
            "Verified");
        var completed = graphService.GetSnapshot(graph.TaskId);
        Expect(
            completed?.Nodes.All(node =>
                node.State == AgentGraphNodeState.Completed
                && node.Progress == 100) == true,
            "Successful task did not close the complete DAG.");
        var goalGraph = await graphService.CreateAsync(
            "goal-mode-smoke",
            "Make the product launch-ready",
            AgentExecutionMode.Goal);
        Expect(
            goalGraph.Nodes.Count == 11
            && goalGraph.Nodes[0].Role == "goal-explorer",
            "Goal Mode did not create its outcome-discovery DAG.");
        await graphService.ApplyRuntimeEventAsync(
            goalGraph.TaskId,
            new AgentRuntimeEvent(
                AgentRuntimeEventKind.Thinking,
                "Goal Explorer · NOVA",
                "Freeze outcome",
                "Mapping evidence to observable success signals",
                12,
                1));
        Expect(
            graphService.GetSnapshot(goalGraph.TaskId)?.Nodes.Any(node =>
                node.Role == "goal-explorer"
                && node.State == AgentGraphNodeState.Running) == true,
            "Goal Explorer event did not activate the mission discovery node.");
        await graphService.ApplyRuntimeEventAsync(
            goalGraph.TaskId,
            new AgentRuntimeEvent(
                AgentRuntimeEventKind.Completed,
                "Goal Explorer · NOVA",
                "Mission response complete",
                "Runtime phase ended; proof has not run yet",
                18,
                1));
        Expect(
            graphService.GetSnapshot(goalGraph.TaskId)?.Nodes.All(node =>
                node.State == AgentGraphNodeState.Completed) == false,
            "A model response incorrectly painted the entire Goal DAG as completed.");

        var governor = new AgentResourceGovernor();
        governor.BeginTask(graph.TaskId, AgentExecutionMode.Autopilot);
        await governor.ObserveRuntimeEventAsync(
            graph.TaskId,
            new AgentRuntimeEvent(
            AgentRuntimeEventKind.BatchStarted,
            "Scheduler",
            "Parallel scan",
            "Three workers",
            15,
            3));
        await governor.ObserveRuntimeEventAsync(
            graph.TaskId,
            new AgentRuntimeEvent(
            AgentRuntimeEventKind.ToolRequested,
            "Builder",
            "Read files",
            "Tool",
            30,
            3));
        var resources = governor.GetSnapshot();
        Expect(resources.Policy.MaxConcurrentAgents == 6, "Autopilot budget was not selected.");
        Expect(resources.ActiveAgents == 3, "Active agent count was not tracked.");
        Expect(resources.ToolCalls == 1, "Tool-call budget usage was not tracked.");
        governor.EndTask(graph.TaskId);
        Expect(governor.GetSnapshot().ActiveTasks == 0, "Resource lease did not close.");
        governor.BeginTask(goalGraph.TaskId, AgentExecutionMode.Goal);
        Expect(
            governor.GetSnapshot().Policy.MaxConcurrentAgents == 8
            && governor.GetSnapshot().Policy.MaxToolCallsPerTask == 400,
            "Goal Mode did not receive its long-horizon resource policy.");
        governor.EndTask(goalGraph.TaskId);

        var pausedGovernor = new AgentResourceGovernor();
        pausedGovernor.BeginTask("pause-smoke", AgentExecutionMode.Ask);
        pausedGovernor.SetPaused(true);
        var gatedEvent = pausedGovernor.ObserveRuntimeEventAsync(
            "pause-smoke",
            new AgentRuntimeEvent(
                AgentRuntimeEventKind.ToolRequested,
                "Builder",
                "Read after resume",
                "Must wait at the safe point"));
        await Task.Delay(50);
        Expect(!gatedEvent.IsCompleted, "A paused task crossed the execution safe point.");
        Expect(
            pausedGovernor.GetSnapshot().ToolCalls == 0,
            "A paused tool call consumed budget before it was released.");
        pausedGovernor.SetPaused(false);
        await gatedEvent;
        Expect(
            pausedGovernor.GetSnapshot().ToolCalls == 1
            && !pausedGovernor.GetSnapshot().IsPaused,
            "Resume did not release exactly one gated tool call.");
        pausedGovernor.EndTask("pause-smoke");

        var limitedGovernor = new AgentResourceGovernor();
        limitedGovernor.BeginTask("budget-smoke", AgentExecutionMode.Ask);
        await limitedGovernor.ObserveRuntimeEventAsync(
            "budget-smoke",
            new AgentRuntimeEvent(
                AgentRuntimeEventKind.Thinking,
                "NOVA",
                "Connect model",
                "Status-only thinking event"));
        Expect(
            limitedGovernor.GetSnapshot().ModelRounds == 0,
            "A status-only thinking event incorrectly consumed model budget.");
        for (var round = 0; round < 24; round++)
        {
            await limitedGovernor.ObserveRuntimeEventAsync(
                "budget-smoke",
                new AgentRuntimeEvent(
                    AgentRuntimeEventKind.Thinking,
                    "Planner",
                    $"Round {round + 1}",
                    "Within budget")
                {
                    ModelRoundCost = 1
                });
        }
        try
        {
            await limitedGovernor.ObserveRuntimeEventAsync(
                "budget-smoke",
                new AgentRuntimeEvent(
                    AgentRuntimeEventKind.Thinking,
                    "Planner",
                    "Round 25",
                    "Must be rejected")
                {
                    ModelRoundCost = 1
                });
            throw new Exception("The model-round budget did not reject the next action.");
        }
        catch (AgentBudgetExceededException exception)
            when (exception.Resource == "模型轮次" && exception.Limit == 24)
        {
        }
        Expect(
            limitedGovernor.GetSnapshot().ModelRounds == 24
            && !string.IsNullOrWhiteSpace(limitedGovernor.GetSnapshot().LimitReason),
            "Budget rejection changed usage or failed to expose its terminal reason.");
        limitedGovernor.EndTask("budget-smoke");
    }
    finally
    {
        DeleteGeneratedTestDirectory(temporaryDirectory);
    }
});

await CheckAsync("AgentOS native control-plane shell", async () =>
{
    var mainWindow = await File.ReadAllTextAsync(@"D:\Agent\NovaDesktop\MainWindow.xaml");
    var center = await File.ReadAllTextAsync(@"D:\Agent\NovaDesktop\AgentOsCenterWindow.xaml");
    var centerCode = await File.ReadAllTextAsync(@"D:\Agent\NovaDesktop\AgentOsCenterWindow.xaml.cs");
    var mainWindowCode = await File.ReadAllTextAsync(@"D:\Agent\NovaDesktop\MainWindow.xaml.cs");
    var mainViewModelCode = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\ViewModels\MainViewModel.cs");
    var threadspace = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\Controls\ConversationStage.xaml");
    Expect(
        mainWindow.Contains("Click=\"AgentOs_Click\"", StringComparison.Ordinal),
        "The native title bar does not expose AgentOS.");
    Expect(
        center.Contains("实时任务图", StringComparison.Ordinal)
        && center.Contains("资源治理", StringComparison.Ordinal)
        && center.Contains("内核事件账本", StringComparison.Ordinal),
        "AgentOS control plane is missing a required operational surface.");
    Expect(
        center.Contains("WindowChrome.IsHitTestVisibleInChrome=\"True\"", StringComparison.Ordinal)
        && center.Contains("ToolTip=\"关闭（Esc）\"", StringComparison.Ordinal)
        && centerCode.Contains("Key.Escape", StringComparison.Ordinal),
        "AgentOS title-bar controls are not reliably interactive or keyboard dismissible.");
    Expect(
        mainWindowCode.Contains("MainWindow_Closing", StringComparison.Ordinal)
        && mainWindowCode.Contains("PrepareForShutdownAsync", StringComparison.Ordinal)
        && mainViewModelCode.Contains(
            "_agentResourceGovernor.ObserveRuntimeEventAsync",
            StringComparison.Ordinal)
        && mainViewModelCode.Contains(
            "_agentResourceGovernor.SetPaused(IsPaused)",
            StringComparison.Ordinal),
        "Execution safe points or the asynchronous shutdown persistence barrier are not wired into the live shell.");
    Expect(
        threadspace.Contains("ItemsSource=\"{Binding ExecutionModes}\"", StringComparison.Ordinal)
        && threadspace.Contains("SelectedExecutionMode", StringComparison.Ordinal)
        && threadspace.Contains("Text=\"{Binding EmptyStateTitle}\"", StringComparison.Ordinal)
        && threadspace.Contains("Content=\"{Binding SuggestionLabel}\"", StringComparison.Ordinal)
        && threadspace.Contains("HasGoalMission", StringComparison.Ordinal)
        && threadspace.Contains(
            "ItemsSource=\"{Binding GoalSignals}\"",
            StringComparison.Ordinal)
        && threadspace.Contains(
            "x:Name=\"MissionDetailsToggle\"",
            StringComparison.Ordinal)
        && threadspace.Contains(
            "ElementName=MissionDetailsToggle",
            StringComparison.Ordinal),
        "Threadspace does not expose execution-mode selection.");
});

await CheckAsync("trustworthy adaptive UI and task isolation", async () =>
{
    var viewModel = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\ViewModels\MainViewModel.cs");
    var mainWindow = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\MainWindow.xaml");
    var threadspace = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\Controls\ConversationStage.xaml");
    var conversationCode = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\Controls\ConversationStage.xaml.cs");
    var mainWindowCode = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\MainWindow.xaml.cs");

    Expect(
        viewModel.Contains("private string _promptText = string.Empty;", StringComparison.Ordinal),
        "Composer still starts with a fake prompt.");
    Expect(
        !viewModel.Contains("aether-01", StringComparison.Ordinal)
        && !viewModel.Contains("orbit-02", StringComparison.Ordinal)
        && !viewModel.Contains("lumen-03", StringComparison.Ordinal),
        "Demo tasks are still mixed into real task history.");
    Expect(
        mainWindow.Contains(
            "IsHitTestVisible=\"{Binding IsRunning, Converter={StaticResource InverseBoolean}}\"",
            StringComparison.Ordinal)
        && mainWindow.Contains(
            "Visibility=\"{Binding IsTraceVisible, Converter={StaticResource BooleanToVisibility}}\"",
            StringComparison.Ordinal),
        "Running-task navigation lock or adaptive trace rail is missing.");
    Expect(
        threadspace.Contains("<ControlTemplate TargetType=\"ComboBox\">", StringComparison.Ordinal)
        && threadspace.Contains("<Style TargetType=\"ScrollBar\">", StringComparison.Ordinal),
        "Dark mode selector or native scroll surface is missing.");
    Expect(
        conversationCode.Contains("_scrollPending", StringComparison.Ordinal)
        && conversationCode.Contains("_followTail", StringComparison.Ordinal)
        && conversationCode.Contains("ScrollToVerticalOffset", StringComparison.Ordinal)
        && conversationCode.Contains("ScrollLatest_Click", StringComparison.Ordinal),
        "Conversation scrolling is not coalesced or reader-aware.");
    Expect(
        mainWindowCode.Contains(
            "_viewModel.SelectedExecutionMode = AgentExecutionMode.Goal;",
            StringComparison.Ordinal)
        && viewModel.Contains(
            "任务保持可恢复状态，不宣称完成",
            StringComparison.Ordinal),
        "QuickStart does not enter Goal Mode or PARTIAL still overclaims completion.");
});

await CheckAsync("0.9 durable Agent Supervisor lease recovery", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-supervisor-" + Guid.NewGuid().ToString("N"));
    try
    {
        var task = new TaskItem
        {
            Id = "supervisor-smoke",
            Title = "Durable supervised task",
            Description = "Verify lease recovery",
            WorkspaceRoot = @"D:\Agent",
            ExecutionMode = AgentExecutionMode.Autopilot,
            State = TaskState.Running,
            Stage = "Repository scan"
        };
        var firstHost = new AgentSupervisorService(temporaryDirectory);
        await firstHost.BootAsync("boot-a");
        var firstLease = await firstHost.AcquireAsync(task);
        await firstHost.HeartbeatAsync(
            task.Id,
            "Implementation checkpoint",
            forcePersist: true);
        Expect(
            firstLease.Attempt == 1 && firstLease.Epoch == 1,
            "First supervisor lease attempt or epoch is incorrect.");

        var contender = new AgentSupervisorService(temporaryDirectory);
        var contended = await contender.BootAsync("boot-b");
        Expect(
            contended.RecoverableCount == 0
            && contended.Leases.Single().State == AgentSupervisorLeaseState.Active,
            "A live task lock was incorrectly declared recoverable.");
        try
        {
            await contender.AcquireAsync(task);
            throw new Exception("A second host acquired the same active task lease.");
        }
        catch (AgentLeaseConflictException exception)
            when (exception.Owner?.Epoch == 1)
        {
        }
        contender.Dispose();

        firstHost.Dispose();
        var recoveredHost = new AgentSupervisorService(temporaryDirectory);
        var recovered = await recoveredHost.BootAsync("boot-c");
        Expect(recovered.RecoverableCount == 1, "Orphaned active lease was not made recoverable.");
        Expect(
            recovered.Leases.Single().Checkpoint == "Implementation checkpoint",
            "Supervisor lost the latest durable checkpoint.");

        var resumedLease = await recoveredHost.AcquireAsync(task);
        Expect(
            resumedLease.Attempt == 2 && resumedLease.Epoch == 2,
            "Resumed lease did not increment its attempt and epoch.");
        Expect(resumedLease.OwnerBootId == "boot-c", "Lease owner did not move to the new host.");
        task.State = TaskState.Completed;
        task.Stage = "Verified delivery";
        await recoveredHost.ReleaseAsync(task);
        recoveredHost.Dispose();

        var finalHost = new AgentSupervisorService(temporaryDirectory);
        var final = await finalHost.BootAsync("boot-d");
        Expect(final.RecoverableCount == 0, "Released completed lease became recoverable.");
        Expect(
            final.Leases.Single().State == AgentSupervisorLeaseState.Completed,
            "Supervisor did not persist terminal task state.");
        finalHost.Dispose();
    }
    finally
    {
        DeleteGeneratedTestDirectory(temporaryDirectory);
    }
});

await CheckAsync("terminal checkpoint lease is recoverable without stealing live work", async () =>
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "nova-terminal-lease-" + Guid.NewGuid().ToString("N"));
    try
    {
        var task = new TaskItem
        {
            Id = "terminal-checkpoint-smoke",
            Title = "Terminal checkpoint recovery",
            Description = "Recover a model-completed task from completion commit failure",
            WorkspaceRoot = @"D:\Agent",
            ExecutionMode = AgentExecutionMode.Goal,
            State = TaskState.Running,
            Stage = "DeepSeek 任务完成"
        };
        var oldHost = new AgentSupervisorService(temporaryDirectory);
        await oldHost.BootAsync("old-host");
        await oldHost.AcquireAsync(task);
        await oldHost.HeartbeatAsync(
            task.Id,
            "DeepSeek 任务完成",
            forcePersist: true);

        var recoveryHost = new AgentSupervisorService(temporaryDirectory);
        await recoveryHost.BootAsync("recovery-host");
        var recovered = await recoveryHost.AcquireAsync(task);
        Expect(
            recovered.OwnerBootId == "recovery-host" && recovered.Epoch == 2,
            "An explicitly terminal model checkpoint could not be safely adopted.");
        task.State = TaskState.Completed;
        task.Stage = "Agent Pack contract checks completed";
        await recoveryHost.ReleaseAsync(task);
        recoveryHost.Dispose();
        oldHost.Dispose();

        var verifier = new AgentSupervisorService(temporaryDirectory);
        var snapshot = await verifier.BootAsync("verifier");
        Expect(
            snapshot.Leases.Single().State == AgentSupervisorLeaseState.Completed,
            "Recovered terminal lease did not persist the final task state.");
        verifier.Dispose();
    }
    finally
    {
        DeleteGeneratedTestDirectory(temporaryDirectory);
    }
});

await CheckAsync("Electron top-level workspace approval contract", async () =>
{
    var bridgeSource = await File.ReadAllTextAsync(
        @"D:\Agent\Nova.AgentOS.Bridge\Program.cs");
    var electronSource = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop.Electron\src\App.tsx");
    var electronStyles = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop.Electron\src\styles.css");
    Expect(
        bridgeSource.Contains("AllowedWriteScopes: null", StringComparison.Ordinal)
        && !bridgeSource.Contains(
            "AllowedWriteScopes: [task.WorkspaceRoot]",
            StringComparison.Ordinal),
        "The Electron bridge passed an absolute workspace root into Agent Mesh ownership scopes.");
    Expect(
        bridgeSource.Contains("\"fetch_public_web_page\"", StringComparison.Ordinal)
        && bridgeSource.Contains("\"delegate_parallel_tasks\"", StringComparison.Ordinal)
        && bridgeSource.Contains("自动审核通过", StringComparison.Ordinal),
        "Automatic review does not cover bounded research and Agent collaboration.");
    Expect(
        electronSource.Contains("智能审核后执行", StringComparison.Ordinal)
        && electronSource.Contains("越界操作不会自动执行", StringComparison.Ordinal),
        "The execution confirmation does not explain the automatic review boundary.");
    Expect(
        bridgeSource.Contains(
            "runtimeEvent.Kind == AgentRuntimeEventKind.TextDelta",
            StringComparison.Ordinal)
        && bridgeSource.Contains(
            "Streaming text is transient UI data",
            StringComparison.Ordinal)
        && electronSource.Contains(
            "if (event.kind === \"textdelta\")",
            StringComparison.Ordinal)
        && electronSource.Contains("setStreamingText", StringComparison.Ordinal),
        "Streaming tokens can still flood the durable ledger or activity timeline.");
    Expect(
        electronSource.Contains("ReactMarkdown", StringComparison.Ordinal)
        && electronSource.Contains("remarkGfm", StringComparison.Ordinal)
        && electronSource.Contains("prepareMarkdown", StringComparison.Ordinal)
        && electronStyles.Contains(".markdown-body h1", StringComparison.Ordinal)
        && electronStyles.Contains(".markdown-body table", StringComparison.Ordinal)
        && electronStyles.Contains(".markdown-body pre", StringComparison.Ordinal),
        "Assistant Markdown is not rendered as a structured, readable document.");
});

await CheckAsync("Electron 1.0 trustworthy cross-model delivery contract", async () =>
{
    var bridgeSource = await File.ReadAllTextAsync(
        @"D:\Agent\Nova.AgentOS.Bridge\Program.cs");
    var conversationSource = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\Services\ConversationHistoryService.cs");
    var mainSource = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop.Electron\electron\main.cjs");
    var rendererSource = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop.Electron\src\App.tsx");
    Expect(
        bridgeSource.Contains("\"verify_result\"", StringComparison.Ordinal)
        && bridgeSource.Contains(
            "NOVA HETEROGENEOUS RESULT REVIEW",
            StringComparison.Ordinal)
        && bridgeSource.Contains(
            "AllowParallelDelegation: false",
            StringComparison.Ordinal),
        "The Electron bridge no longer exposes a bounded read-only independent reviewer.");
    Expect(
        bridgeSource.Contains(
            "BuildConversationContext",
            StringComparison.Ordinal)
        && conversationSource.Contains(
            "[NOVA THREAD MEMORY v2]",
            StringComparison.Ordinal)
        && bridgeSource.Contains(
            "includeCurrentPrompt: false",
            StringComparison.Ordinal)
        && mainSource.Contains("conversation: messages", StringComparison.Ordinal),
        "Multi-turn UI history is still silently discarded before the runtime.");
    Expect(
        bridgeSource.Contains(
            "\"auto_delegate_parallel_tasks\"",
            StringComparison.Ordinal),
        "Electron Autopilot still rejects the runtime's automatic Agent delegation approval name.");
    Expect(
        mainSource.Contains("chooseIndependentReviewer", StringComparison.Ordinal)
        && mainSource.Contains("requiresWorkspaceMutation", StringComparison.Ordinal)
        && mainSource.Contains("deliveryStatus = \"PARTIAL\"", StringComparison.Ordinal)
        && rendererSource.Contains("双模型复核", StringComparison.Ordinal)
        && rendererSource.Contains("delivery-result", StringComparison.Ordinal),
        "The 1.0 renderer can no longer opt into cross-model review or expose truthful partial delivery.");
});

await CheckAsync("Electron Ollama native endpoint contract", async () =>
{
    var mainSource = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop.Electron\electron\main.cjs");
    var rendererSource = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop.Electron\src\App.tsx");
    Expect(
        mainSource.Contains(
            "endpoint: \"http://localhost:11434/api/chat\"",
            StringComparison.Ordinal)
        && mainSource.Contains(
            "provider === \"ollama\" && /\\/api\\/chat$/i.test(pathname)",
            StringComparison.Ordinal)
        && mainSource.Contains(
            "endpoint.pathname.replace(/\\/api\\/chat$/i, \"/api/tags\")",
            StringComparison.Ordinal),
        "Electron no longer preserves Ollama's native /api/chat endpoint and matching model probe path.");
    Expect(
        mainSource.Contains("Ollama 服务已连接，但没有发现已安装模型", StringComparison.Ordinal)
        && mainSource.Contains("Ollama 中未找到模型", StringComparison.Ordinal)
        && rendererSource.Contains("http://localhost:11434", StringComparison.Ordinal),
        "Electron no longer gives an actionable missing-model error or localhost guidance.");
});

await CheckAsync("Electron bridge non-blocking start and lease retry contract", async () =>
{
    var bridgeSource = await File.ReadAllTextAsync(
        @"D:\Agent\Nova.AgentOS.Bridge\Program.cs");
    var mainSource = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop.Electron\electron\main.cjs");
    Expect(
        bridgeSource.Contains(
            "inFlightRequests.Add(ProcessRequestAsync(line))",
            StringComparison.Ordinal)
        && bridgeSource.Contains(
            "await Task.WhenAll(inFlightRequests)",
            StringComparison.Ordinal),
        "Long model calls can still serialize and block every AgentOS control request.");
    Expect(
        bridgeSource.Contains(
            "_active.TryGetValue(requestedTaskId, out var activeTask)",
            StringComparison.Ordinal)
        && bridgeSource.Contains(
            "return ProjectTask(activeTask)",
            StringComparison.Ordinal),
        "A completed start_task response cannot be recovered idempotently after a shell timeout.");
    Expect(
        mainSource.Contains(
            "method === \"start_task\"",
            StringComparison.Ordinal)
        && mainSource.Contains(
            "2 * 60 * 1000",
            StringComparison.Ordinal),
        "Electron still applies the legacy twenty-second timeout to AgentOS task startup.");
    Expect(
        bridgeSource.Contains("using var runtimeEventGate", StringComparison.Ordinal)
        && bridgeSource.Contains("await runtimeEventGate.WaitAsync", StringComparison.Ordinal),
        "Parallel tool progress can still race while persisting one task snapshot and abort a successful round.");
});

await CheckAsync("Agent Pack generation enters the durable task workspace", async () =>
{
    var mainSource = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop.Electron\electron\main.cjs");
    var rendererSource = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop.Electron\src\App.tsx");
    var workshopSource = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\Services\AgentPackWorkshopService.cs");
    var createHandlerStart = mainSource.IndexOf(
        "ipcMain.handle(\"nova:create-agent-pack\"",
        StringComparison.Ordinal);
    var createHandlerEnd = mainSource.IndexOf(
        "ipcMain.handle(\"nova:list-agent-calibrations\"",
        createHandlerStart,
        StringComparison.Ordinal);
    var createHandler = createHandlerStart >= 0 && createHandlerEnd > createHandlerStart
        ? mainSource[createHandlerStart..createHandlerEnd]
        : string.Empty;
    var buildStarterStart = mainSource.IndexOf(
        "async function startAgentPackBuild",
        StringComparison.Ordinal);
    var buildStarterEnd = mainSource.IndexOf(
        "function modelSourceId",
        buildStarterStart,
        StringComparison.Ordinal);
    var buildStarter = buildStarterStart >= 0 && buildStarterEnd > buildStarterStart
        ? mainSource[buildStarterStart..buildStarterEnd]
        : string.Empty;
    Expect(
        mainSource.Contains("startAgentPackBuild", StringComparison.Ordinal)
        && mainSource.Contains("executeAgentPackBuild", StringComparison.Ordinal)
        && mainSource.Contains("bridge.call(\"start_task\"", StringComparison.Ordinal)
        && mainSource.Contains("bridge.call(\"task_event\"", StringComparison.Ordinal)
        && mainSource.Contains("bridge.call(\"complete_task\"", StringComparison.Ordinal),
        "Agent Pack generation is not represented by a durable AgentOS task and real task events.");
    Expect(
        mainSource.Contains("validateGeneratedAgentPack", StringComparison.Ordinal)
        && mainSource.Contains("标准体检没有达到 100/100", StringComparison.Ordinal)
        && workshopSource.Contains("certification.Checks.Any(check => !check.Passed)", StringComparison.Ordinal),
        "An incomplete Agent Pack can still be registered as a usable generated Agent.");
    Expect(
        rendererSource.Contains("setSettingsOpen(false)", StringComparison.Ordinal)
        && rendererSource.Contains("Agent Pack 构建任务已进入任务空间", StringComparison.Ordinal)
        && rendererSource.Contains("Agent Pack 生成与可用性验证", StringComparison.Ordinal),
        "The workshop does not navigate into the task workspace after build confirmation.");
    Expect(
        createHandler.Contains("startAgentPackBuild", StringComparison.Ordinal)
        && !createHandler.Contains("showMessageBox", StringComparison.Ordinal)
        && rendererSource.Contains("构建任务未创建", StringComparison.Ordinal),
        "An already-reviewed Agent draft is blocked by a redundant native confirmation or hides task-start errors.");
    Expect(
        buildStarter.Contains("agentPackId: null", StringComparison.Ordinal)
        && !buildStarter.Contains("agentPackId: request?.id", StringComparison.Ordinal),
        "Agent Pack build task incorrectly requires the not-yet-created target Pack to already exist and be enabled.");
    Expect(
        rendererSource.Contains("generateAgentId", StringComparison.Ordinal)
        && rendererSource.Contains("Agent ID · 系统自动生成", StringComparison.Ordinal)
        && !rendererSource.Contains("nova.user.new-agent", StringComparison.Ordinal),
        "Agent Workshop still reuses a hard-coded editable Agent ID.");
    Expect(
        rendererSource.Contains("window.nova.agentPacks.remove", StringComparison.Ordinal)
        && mainSource.Contains("nova:remove-agent-pack", StringComparison.Ordinal)
        && mainSource.Contains("请先停用此 Agent Pack", StringComparison.Ordinal),
        "Installed Agent Packs cannot be safely removed from the Agent Center.");
});

await CheckAsync("Agent Workshop uses a persistent design session before task creation", async () =>
{
    var mainSource = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop.Electron\electron\main.cjs");
    var preloadSource = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop.Electron\electron\preload.cjs");
    var rendererSource = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop.Electron\src\App.tsx");
    var plannerSource = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\Services\AutomaticAgentPlanner.cs");
    var deepSeekRuntimeSource = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\Services\DeepSeekChatAgentRuntime.cs");
    var openAiRuntimeSource = await File.ReadAllTextAsync(
        @"D:\Agent\NovaDesktop\Services\OpenAIResponsesAgentRuntime.cs");
    var bridgeSource = await File.ReadAllTextAsync(
        @"D:\Agent\Nova.AgentOS.Bridge\Program.cs");
    Expect(
        mainSource.Contains("startAgentWorkshopSession", StringComparison.Ordinal)
        && mainSource.Contains("executeAgentWorkshopSession", StringComparison.Ordinal)
        && mainSource.Contains("bridge.call(\"run_design_session\"", StringComparison.Ordinal)
        && mainSource.Contains("design-sessions.json", StringComparison.Ordinal)
        && mainSource.Contains("nova:agent-workshop-ready", StringComparison.Ordinal),
        "Agent Workshop does not use a recoverable design session backed by the AgentOS runtime.");
    Expect(
        plannerSource.Contains("[NOVA_AGENT_WORKSHOP]", StringComparison.Ordinal)
        && plannerSource.Contains("行业架构师", StringComparison.Ordinal)
        && plannerSource.Contains("工作流架构师", StringComparison.Ordinal)
        && plannerSource.Contains("信任审查官", StringComparison.Ordinal)
        && bridgeSource.Contains("RunDesignSessionAsync", StringComparison.Ordinal)
        && bridgeSource.Contains("Design session", StringComparison.Ordinal)
        && bridgeSource.Contains("仅允许本轮真实子 Agent 委派", StringComparison.Ordinal),
        "AgentOS does not provide a read-only Agent Workshop council plan and approval boundary.");
    Expect(
        mainSource.Contains("nova:cancel-agent-pack-orchestration", StringComparison.Ordinal)
        && mainSource.Contains("cancel_design_session", StringComparison.Ordinal)
        && preloadSource.Contains("cancelOrchestration", StringComparison.Ordinal)
        && mainSource.Contains("本次智能体编排已停止", StringComparison.Ordinal),
        "Agent Workshop does not expose an end-to-end cancellation path.");
    Expect(
        rendererSource.Contains("这里完成审阅前不会创建任务空间", StringComparison.Ordinal)
        && rendererSource.Contains("getDesignSession", StringComparison.Ordinal)
        && preloadSource.Contains("onOrchestrationReady", StringComparison.Ordinal),
        "Agent Workshop does not remain in Agent Center or restore the reviewed draft.");
    Expect(
        !mainSource.Contains("信任审查官没有批准当前编排", StringComparison.Ordinal)
        && rendererSource.Contains("用户已在 Agent 中心审阅并确认本版编排草案", StringComparison.Ordinal)
        && rendererSource.Contains("确认方案并构建 Agent Pack", StringComparison.Ordinal),
        "A structurally complete council draft cannot be human-approved before the formal Pack build.");
    Expect(
        deepSeekRuntimeSource.Contains("TaskId.StartsWith(\"design:\"", StringComparison.Ordinal)
        && openAiRuntimeSource.Contains("TaskId.StartsWith(\"design:\"", StringComparison.Ordinal)
        && mainSource.Contains("agent = \"编排委员会\"", StringComparison.Ordinal),
        "Agent Workshop still exposes unrelated workspace tools or noisy internal role cards during design.");
    Expect(
        bridgeSource.Contains("stageOutputs", StringComparison.Ordinal)
        && bridgeSource.Contains("allowParallelDelegation", StringComparison.Ordinal)
        && bridgeSource.Contains("stageOutputs.Count < 24", StringComparison.Ordinal)
        && !bridgeSource.Contains("runtimeEvent.Agent.StartsWith(\"子 Agent \"", StringComparison.Ordinal)
        && mainSource.Contains("buildAgentWorkshopRepairPrompt", StringComparison.Ordinal)
        && mainSource.Contains("recoverWorkshopDraftFromStageOutputs", StringComparison.Ordinal)
        && mainSource.Contains("coerceWorkshopDraft", StringComparison.Ordinal)
        && mainSource.Contains("已恢复可审阅草案", StringComparison.Ordinal)
        && mainSource.Contains("只进行一次轻量结构修复", StringComparison.Ordinal)
        && mainSource.Contains("编排草案已生成", StringComparison.Ordinal),
        "Malformed council JSON discards completed child-Agent analysis instead of repairing and delivering a reviewable draft.");
});

if (File.Exists(runtimeEvidencePath))
{
    File.Delete(runtimeEvidencePath);
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"NOVA smoke tests failed ({failures.Count}):");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"- {failure}");
    }
    return 1;
}

Console.WriteLine($"NOVA smoke tests passed: {passed}/{checks}");
return 0;

async Task CheckAsync(string name, Func<Task> test)
{
    checks++;
    try
    {
        await test();
        passed++;
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
    }
}

static void Expect(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}

static async Task<(int ExitCode, string Output, string Error)> RunCommandAsync(
    string workingDirectory,
    string executable,
    IReadOnlyList<string> arguments)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = executable,
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = new Process { StartInfo = startInfo };
    process.Start();
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return (process.ExitCode, await outputTask, await errorTask);
}

static void DeleteGeneratedTestDirectory(string directory)
{
    var fullPath = Path.GetFullPath(directory);
    var tempRoot = Path.GetFullPath(Path.GetTempPath())
        .TrimEnd(Path.DirectorySeparatorChar)
        + Path.DirectorySeparatorChar;
    if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(fullPath).StartsWith("nova-", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Refusing to delete a directory outside the generated NOVA test scope.");
    }

    if (!Directory.Exists(fullPath))
    {
        return;
    }

    foreach (var file in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
    {
        File.SetAttributes(file, FileAttributes.Normal);
    }
    Directory.Delete(fullPath, recursive: true);
}

static async Task<int> RunMcpFixtureAsync()
{
    Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    while (await Console.In.ReadLineAsync() is { } line)
    {
        var request = JsonNode.Parse(line)?.AsObject();
        if (request is null)
        {
            continue;
        }

        var method = request["method"]?.GetValue<string>();
        if (method == "notifications/initialized")
        {
            continue;
        }

        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = request["id"]?.DeepClone()
        };
        response["result"] = method switch
        {
            "initialize" => new JsonObject
            {
                ["protocolVersion"] = "2025-11-25",
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject
                {
                    ["name"] = "nova-smoke-fixture",
                    ["version"] = "1.0.0"
                }
            },
            "tools/list" => new JsonObject
            {
                ["tools"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "echo",
                        ["description"] = "Echo test text.",
                        ["inputSchema"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["text"] = new JsonObject { ["type"] = "string" }
                            },
                            ["required"] = new JsonArray("text")
                        }
                    }
                }
            },
            "tools/call" => new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = request["params"]?["arguments"]?["text"]?.GetValue<string>() ?? string.Empty
                    }
                },
                ["isError"] = false
            },
            _ => new JsonObject()
        };
        await Console.Out.WriteLineAsync(response.ToJsonString());
        await Console.Out.FlushAsync();
    }
    return 0;
}

file sealed class FakeResponsesHandler : HttpMessageHandler
{
    public int RequestCount { get; private set; }
    public bool SecondRequestContainedToolOutput { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);

        string response;
        if (RequestCount == 1)
        {
            response =
                """
                {
                  "id": "resp_smoke_1",
                  "output": [
                    {
                      "type": "function_call",
                      "call_id": "call_smoke_1",
                      "name": "list_workspace_files",
                      "arguments": "{\"directory\":\"\",\"max_depth\":2}"
                    }
                  ]
                }
                """;
        }
        else
        {
            SecondRequestContainedToolOutput =
                body.Contains("\"type\":\"function_call_output\"", StringComparison.Ordinal)
                && body.Contains("\"call_id\":\"call_smoke_1\"", StringComparison.Ordinal);
            response =
                """
                {
                  "id": "resp_smoke_2",
                  "output": [
                    {
                      "type": "message",
                      "role": "assistant",
                      "content": [
                        { "type": "output_text", "text": "真实工具循环已完成。" }
                      ]
                    }
                  ]
                }
                """;
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "application/json")
        };
    }
}

file sealed class FakeParallelResponsesHandler : HttpMessageHandler
{
    private int _requestCount;
    public bool SecondRequestContainedBothOutputs { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _requestCount++;
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var response = _requestCount == 1
            ? """
              {
                "id": "resp_parallel_1",
                "output": [
                  {
                    "type": "function_call",
                    "call_id": "parallel_call_1",
                    "name": "list_workspace_files",
                    "arguments": "{\"directory\":\"\",\"max_depth\":1}"
                  },
                  {
                    "type": "function_call",
                    "call_id": "parallel_call_2",
                    "name": "read_text_file",
                    "arguments": "{\"path\":\"README.md\",\"max_chars\":4000}"
                  }
                ]
              }
              """
            : """
              {
                "id": "resp_parallel_2",
                "output": [
                  {
                    "type": "message",
                    "role": "assistant",
                    "content": [
                      { "type": "output_text", "text": "并行工具已汇总。" }
                    ]
                  }
                ]
              }
              """;

        if (_requestCount == 2)
        {
            SecondRequestContainedBothOutputs =
                body.Contains("\"call_id\":\"parallel_call_1\"", StringComparison.Ordinal)
                && body.Contains("\"call_id\":\"parallel_call_2\"", StringComparison.Ordinal)
                && body.Contains("README.md", StringComparison.OrdinalIgnoreCase)
                && body.Contains("NOVA", StringComparison.Ordinal);
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "application/json")
        };
    }
}

file sealed class FakeWriteResponsesHandler : HttpMessageHandler
{
    private int _requestCount;
    public bool DenialWasContinued { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _requestCount++;
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var response = _requestCount == 1
            ? """
              {
                "id": "resp_write_1",
                "output": [
                  {
                    "type": "function_call",
                    "call_id": "call_write_1",
                    "name": "write_text_file",
                    "arguments": "{\"path\":\"never-written.md\",\"content\":\"blocked\"}"
                  }
                ]
              }
              """
            : """
              {
                "id": "resp_write_2",
                "output": [
                  {
                    "type": "message",
                    "role": "assistant",
                    "content": [
                      { "type": "output_text", "text": "已尊重用户拒绝。" }
                    ]
                  }
                ]
              }
              """;

        if (_requestCount == 2)
        {
            DenialWasContinued =
                body.Contains("function_call_output", StringComparison.Ordinal)
                && body.Contains("call_write_1", StringComparison.Ordinal)
                && body.Contains("denied", StringComparison.Ordinal);
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "application/json")
        };
    }
}

file sealed class FakeOllamaCompatibleHandler : HttpMessageHandler
{
    public bool UsedConfiguredEndpoint { get; private set; }
    public bool AuthorizationWasOmitted { get; private set; }
    public bool ThinkingExtensionWasOmitted { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        UsedConfiguredEndpoint = request.RequestUri?.ToString()
            .Equals(
                "http://127.0.0.1:11434/v1/chat/completions",
                StringComparison.OrdinalIgnoreCase) == true;
        AuthorizationWasOmitted = request.Headers.Authorization is null;
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        ThinkingExtensionWasOmitted = !body.Contains("\"thinking\"", StringComparison.Ordinal);
        const string response = """
            data: {"id":"ollama-smoke-1","choices":[{"delta":{"content":"Ollama Agent 通道已连接。"},"finish_reason":"stop"}]}

            data: [DONE]

            """;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "text/event-stream")
        };
    }
}

file sealed class FakeOllamaNativeHandler : HttpMessageHandler
{
    public bool UsedNativeEndpoint { get; private set; }
    public bool UsedNativeRequestShape { get; private set; }
    public bool UsedExpandedContextWindow { get; private set; }
    public bool AcceptedNdjson { get; private set; }
    public bool AuthorizationWasOmitted { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        UsedNativeEndpoint = request.RequestUri?.ToString()
            .Equals("http://127.0.0.1:11434/api/chat", StringComparison.OrdinalIgnoreCase) == true;
        AuthorizationWasOmitted = request.Headers.Authorization is null;
        AcceptedNdjson = request.Headers.Accept.Any(item =>
            item.MediaType?.Equals("application/x-ndjson", StringComparison.OrdinalIgnoreCase) == true);
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        UsedNativeRequestShape = body.Contains("\"options\"", StringComparison.Ordinal)
                                 && body.Contains("\"num_predict\"", StringComparison.Ordinal)
                                 && !body.Contains("\"max_tokens\"", StringComparison.Ordinal)
                                 && !body.Contains("\"stream_options\"", StringComparison.Ordinal)
                                 && !body.Contains("\"tool_choice\"", StringComparison.Ordinal)
                                 && !body.Contains("\"thinking\"", StringComparison.Ordinal);
        using var payload = JsonDocument.Parse(body);
        UsedExpandedContextWindow = payload.RootElement
            .GetProperty("options")
            .GetProperty("num_ctx")
            .GetInt32() >= 8192;
        const string response = """
            {"model":"openbmb/minicpm5:latest","created_at":"2026-07-31T10:00:00Z","message":{"role":"assistant","content":"Ollama native API connected."},"done":false}
            {"model":"openbmb/minicpm5:latest","created_at":"2026-07-31T10:00:01Z","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop"}
            """;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "application/x-ndjson")
        };
    }
}

file sealed class FakeDeepSeekHandler : HttpMessageHandler
{
    public int RequestCount { get; private set; }
    public bool SecondRequestContainedToolOutput { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);

        string response;
        if (RequestCount == 1)
        {
            response =
                """
                data: {"id":"chat_deepseek_1","choices":[{"delta":{"reasoning_content":"先检查工作区。"}}]}

                data: {"id":"chat_deepseek_1","choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_deepseek_1","type":"function","function":{"name":"list_workspace_files","arguments":"{\"directory\":\"\","}}]}}]}

                data: {"id":"chat_deepseek_1","choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"max_depth\":2}"}}]},"finish_reason":"tool_calls"}]}

                data: [DONE]

                """;
        }
        else
        {
            SecondRequestContainedToolOutput =
                body.Contains("\"role\":\"tool\"", StringComparison.Ordinal)
                && body.Contains("\"tool_call_id\":\"call_deepseek_1\"", StringComparison.Ordinal)
                && body.Contains("README.md", StringComparison.OrdinalIgnoreCase);
            response =
                """
                data: {"id":"chat_deepseek_2","choices":[{"delta":{"content":"DeepSeek 流式"}}]}

                data: {"id":"chat_deepseek_2","choices":[{"delta":{"content":"工具循环已完成。"},"finish_reason":"stop"}]}

                data: [DONE]

                """;
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "text/event-stream")
        };
    }
}

file sealed class FakeEvolutionToolFilterHandler : HttpMessageHandler
{
    public HashSet<string> ToolNames { get; } = new(StringComparer.Ordinal);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var root = JsonNode.Parse(body)?.AsObject();
        if (root?["tools"] is JsonArray tools)
        {
            foreach (var item in tools.OfType<JsonObject>())
            {
                var name = item["function"]?["name"]?.GetValue<string>()
                           ?? item["name"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    ToolNames.Add(name);
                }
            }
        }

        const string response = """
            data: {"id":"chat_evolution_filter","choices":[{"delta":{"content":"Evolution tool boundary ready."},"finish_reason":"stop"}]}

            data: [DONE]

            """;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "text/event-stream")
        };
    }
}

file sealed class FakeLongDeepSeekHandler(int toolRounds) : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        var response = RequestCount <= toolRounds
            ? """
                data: {"id":"chat_long_$N","choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_long_$N","type":"function","function":{"name":"list_workspace_files","arguments":"{\"directory\":\"\",\"max_depth\":1}"}}]},"finish_reason":"tool_calls"}]}

                data: [DONE]

                """.Replace("$N", RequestCount.ToString(), StringComparison.Ordinal)
            : """
                data: {"id":"chat_long_final","choices":[{"delta":{"content":"超过二十轮后正常完成。"},"finish_reason":"stop"}]}

                data: [DONE]

                """;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "text/event-stream")
        });
    }
}

file sealed class FakeTransientKimiHandler : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        if (RequestCount == 1)
        {
            throw new HttpRequestException(
                "Unable to read data from the transport connection.");
        }

        const string response =
            """
            data: {"id":"chat_kimi_recovered","choices":[{"delta":{"content":"连接恢复后完成。"},"finish_reason":"stop"}]}

            data: [DONE]

            """;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "text/event-stream")
        });
    }
}

file sealed class FakeKimiMultimodalHandler : HttpMessageHandler
{
    public bool UsedMoonshotEndpoint { get; private set; }
    public bool ContainedImageDataUrl { get; private set; }
    public bool ContainedTextAttachment { get; private set; }
    public bool UsedKimiTokenField { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        UsedMoonshotEndpoint =
            request.RequestUri?.AbsoluteUri
                .Equals(
                    "https://api.moonshot.cn/v1/chat/completions",
                    StringComparison.OrdinalIgnoreCase) == true;
        ContainedImageDataUrl =
            body.Contains("\"type\":\"image_url\"", StringComparison.Ordinal)
            && body.Contains("data:image/png;base64,", StringComparison.Ordinal);
        ContainedTextAttachment =
            body.Contains("context.md", StringComparison.Ordinal)
            && body.Contains("\\u4FDD\\u7559", StringComparison.OrdinalIgnoreCase);
        UsedKimiTokenField =
            body.Contains("\"max_completion_tokens\":32768", StringComparison.Ordinal)
            && !body.Contains("\"user_id\"", StringComparison.Ordinal);
        const string response =
            """
            data: {"id":"chat_kimi_1","choices":[{"delta":{"content":"Kimi 已理解图片和文件。"},"finish_reason":"stop"}]}

            data: [DONE]

            """;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "text/event-stream")
        };
    }
}

file sealed class FakeWorkerResponsesHandler : HttpMessageHandler
{
    private int _requestCount;
    public int RequestCount => _requestCount;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var worker = Interlocked.Increment(ref _requestCount);
        var response =
            $$"""
              {
                "id": "worker_{{worker}}",
                "output": [
                  {
                    "type": "message",
                    "role": "assistant",
                    "content": [
                      { "type": "output_text", "text": "工作者结果 {{worker}}" }
                    ]
                  }
                ]
              }
              """;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "application/json")
        });
    }
}

file sealed class FakeMacOpenAiHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"output_text":"Mac Core real response."}""",
                Encoding.UTF8,
                "application/json")
        };
        return Task.FromResult(response);
    }
}

file sealed class FakeMacKimiHandler : HttpMessageHandler
{
    public bool UsedMoonshotEndpoint { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        UsedMoonshotEndpoint = request.RequestUri?.AbsoluteUri.Equals(
            "https://api.moonshot.cn/v1/chat/completions",
            StringComparison.OrdinalIgnoreCase) == true;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"choices":[{"message":{"content":"Mac Kimi real response."}}]}""",
                Encoding.UTF8,
                "application/json")
        });
    }
}

file sealed class FakeParallelMacHandler : HttpMessageHandler
{
    private int _active;
    private int _peakConcurrency;
    private int _requestCount;

    public int PeakConcurrency => Volatile.Read(ref _peakConcurrency);
    public int RequestCount => Volatile.Read(ref _requestCount);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        var active = Interlocked.Increment(ref _active);
        while (true)
        {
            var observed = Volatile.Read(ref _peakConcurrency);
            if (active <= observed
                || Interlocked.CompareExchange(ref _peakConcurrency, active, observed) == observed)
            {
                break;
            }
        }
        try
        {
            await Task.Delay(80, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"output_text":"parallel result"}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }
}

file sealed class FakeMcpHttpHandler : HttpMessageHandler
{
    public int InitializedNotifications { get; private set; }
    public bool SessionHeaderSeen { get; private set; }
    public bool ProtocolHeaderSeen { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var message = JsonNode.Parse(body)?.AsObject() ?? new JsonObject();
        var method = message["method"]?.GetValue<string>() ?? string.Empty;
        var id = message["id"]?.DeepClone();
        if (method != "initialize")
        {
            SessionHeaderSeen |= request.Headers.Contains("Mcp-Session-Id");
            ProtocolHeaderSeen |= request.Headers.Contains("MCP-Protocol-Version");
        }
        if (method == "notifications/initialized")
        {
            InitializedNotifications++;
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }

        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id
        };
        if (method == "initialize")
        {
            response["result"] = new JsonObject
            {
                ["protocolVersion"] = "2025-11-25",
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "http-fixture", ["version"] = "1.0" }
            };
            var initialized = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json")
            };
            initialized.Headers.TryAddWithoutValidation("Mcp-Session-Id", "session-smoke");
            return initialized;
        }
        if (method == "tools/list")
        {
            response["result"] = new JsonObject
            {
                ["tools"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "remote_echo",
                        ["description"] = "Remote echo.",
                        ["inputSchema"] = new JsonObject { ["type"] = "object" }
                    }
                }
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"data: {response.ToJsonString()}\n\n",
                    Encoding.UTF8,
                    "text/event-stream")
            };
        }

        response["result"] = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = message["params"]?["arguments"]?["text"]?.GetValue<string>() ?? string.Empty
                }
            },
            ["isError"] = false
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json")
        };
    }
}
