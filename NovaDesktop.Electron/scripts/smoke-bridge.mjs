import { spawn } from "node:child_process";
import path from "node:path";
import readline from "node:readline";
import { fileURLToPath } from "node:url";

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const executable = path.resolve(
  scriptDirectory,
  "..",
  "resources",
  "bridge",
  "Nova.AgentOS.Bridge.exe"
);
const child = spawn(executable, [], {
  windowsHide: true,
  stdio: ["pipe", "pipe", "inherit"]
});
const lines = readline.createInterface({ input: child.stdout });
const pending = new Map();
let sequence = 0;

lines.on("line", (line) => {
  const message = JSON.parse(line);
  console.log(
    `SMOKE_RX id=${message.id} bytes=${Buffer.byteLength(line)} status=${message.error ? "error" : "ok"}`
  );
  const callback = pending.get(message.id);
  if (!callback) return;
  pending.delete(message.id);
  message.error
    ? callback.reject(new Error(message.error.message))
    : callback.resolve(message.result);
});

function call(method, params = {}, timeoutMs = 10000) {
  const id = `bridge-smoke-${++sequence}`;
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      pending.delete(id);
      reject(new Error(`${method} timed out after ${timeoutMs} ms`));
    }, timeoutMs);
    pending.set(id, {
      resolve: (value) => {
        clearTimeout(timeout);
        resolve(value);
      },
      reject: (error) => {
        clearTimeout(timeout);
        reject(error);
      }
    });
    const payload = `${JSON.stringify({ id, method, params })}\n`;
    console.log(`SMOKE_TX ${method} bytes=${Buffer.byteLength(payload)}`);
    child.stdin.write(payload);
  });
}

try {
  console.log("SMOKE_STEP boot");
  await call("boot");
  console.log("SMOKE_STEP cancel_idle_design_session");
  const idleDesignCancellation = await call("cancel_design_session", {
    sessionId: "smoke-idle-design"
  });
  if (idleDesignCancellation?.cancelled !== false) {
    throw new Error("An idle Agent design session reported an unexpected active cancellation.");
  }
  console.log("SMOKE_STEP list_agent_packs");
  const agentPacks = await call("list_agent_packs");
  const commercePack = Array.isArray(agentPacks)
    ? agentPacks.find((pack) => pack.id === "nova.cross-border-commerce")
    : null;
  if (!commercePack || !commercePack.enabled || commercePack.workflowCount < 1) {
    throw new Error("Built-in cross-border Agent Pack was not discovered and enabled.");
  }
  console.log("SMOKE_STEP list_agent_creation_templates");
  const creationTemplates = await call("list_agent_creation_templates");
  if (
    !Array.isArray(creationTemplates) ||
    creationTemplates.length < 10 ||
    !creationTemplates.some((item) => item.id === "orchestration")
  ) {
    throw new Error("Agent Creation Standard scenario templates are incomplete.");
  }
  console.log("SMOKE_STEP recommend_agent_pack");
  const recommendation = await call("recommend_agent_pack", {
    id: "nova.user.bridge-preview",
    name: "市场判断 Agent",
    category: "跨境电商",
    description: "根据商品、目标市场和真实证据形成可复核的市场判断。",
    objective: "判断一个新品是否值得进入墨西哥市场",
    scenarioProfile: "decision",
    autonomyLevel: "approval-execute",
    lifecycle: "project",
    collaborationMode: "specialist-team",
    deliveryMode: "document",
    decisionStyle: "balanced",
    primaryArtifact: "新品市场判断.md",
    requiredInputs: [],
    recommendedInputs: [],
    starterPrompts: []
  });
  if (
    !recommendation.summary?.includes("跨境电商") ||
    !recommendation.summary?.includes("专业工作组") ||
    !Array.isArray(recommendation.requiredInputs) ||
    recommendation.requiredInputs.length < 3 ||
    !recommendation.starterPrompts?.some((item) => item.includes("新品市场判断.md"))
  ) {
    throw new Error("Agent workshop recommendation did not synthesize the first three design stages.");
  }
  const packDetails = await call("get_agent_pack", { id: commercePack.id });
  if (!Array.isArray(packDetails.workflows) || packDetails.workflows[0]?.stepCount < 3) {
    throw new Error("Agent Pack workflow contract is incomplete.");
  }
  if (
    packDetails.workflows[0]?.id !== "cross-border-product-demand-validation" ||
    !packDetails.onboarding ||
    !Array.isArray(packDetails.onboarding.steps) ||
    packDetails.onboarding.steps.length < 3 ||
    !Array.isArray(packDetails.onboarding.outcomes) ||
    packDetails.onboarding.outcomes.length < 2
  ) {
    throw new Error("Agent Pack generic onboarding or product-neutral entry workflow is incomplete.");
  }
  if (
    !packDetails.capabilityRequirements ||
    !Array.isArray(packDetails.capabilityRequirements.items) ||
    packDetails.capabilityRequirements.items.length < 4
  ) {
    throw new Error("Agent Pack capability requirement contract is incomplete.");
  }
  console.log("SMOKE_STEP list_agent_calibrations");
  const calibration = await call("list_agent_calibrations", { packId: commercePack.id });
  if (
    calibration.packId !== commercePack.id ||
    !Array.isArray(calibration.patches) ||
    typeof calibration.activeCount !== "number"
  ) {
    throw new Error("Agent calibration snapshot contract is incomplete.");
  }
  console.log("SMOKE_STEP get_agent_pack_capabilities");
  const packCapabilities = await call("get_agent_pack_capabilities", {
    id: commercePack.id,
    workspaceRoot: path.resolve(scriptDirectory, "..")
  }, 30000);
  if (
    !Array.isArray(packCapabilities.items) ||
    packCapabilities.items.length < 4 ||
    !packCapabilities.items.some((item) => item.id === "mercadolibre-account-data") ||
    !packCapabilities.items.some((item) => item.id === "tiktok-business-data")
  ) {
    throw new Error("Agent Pack capability readiness projection is incomplete.");
  }
  console.log("SMOKE_STEP preview_mcp_config");
  const preview = await call("preview_mcp_config", {
    workspaceRoot: path.resolve(scriptDirectory, ".."),
    configuration: JSON.stringify({
      mcpServers: {
        "smoke-remote": {
          url: "https://mcp.example.com/mcp",
          headers: { Authorization: "${NOVA_SMOKE_TOKEN}" }
        }
      }
    })
  });
  if (
    !Array.isArray(preview.candidates) ||
    preview.candidates.length !== 1 ||
    preview.candidates[0].name !== "smoke-remote" ||
    preview.candidates[0].canImport !== true
  ) {
    throw new Error("Generic MCP configuration preview is incomplete.");
  }
  console.log("SMOKE_STEP start_task");
  const started = await call("start_task", {
    prompt: "Electron Bridge 端到端验证",
    title: "Electron Bridge Smoke",
    workspaceRoot: path.resolve(scriptDirectory, ".."),
    provider: "local-smoke",
    model: "protocol-only",
    mode: "Build",
    agentPackId: commercePack.id
  });
  console.log(`SMOKE_STEP task_event ${started.id}`);
  await call("task_event", {
    taskId: started.id,
    kind: "Thinking",
    action: "验证真实任务事件",
    detail: "JSON-RPC → AgentOS → snapshot",
    progress: 60
  });
  console.log("SMOKE_STEP complete_task");
  const completed = await call("complete_task", {
    taskId: started.id,
    succeeded: true,
    detail: "Electron Bridge 端到端验证通过",
    outputCharacters: 1800,
    draft: "NOVA_ELECTRON_BRIDGE_E2E_OK"
  });
  if (String(completed.state).toLowerCase() !== "completed") {
    throw new Error(`Unexpected final state: ${completed.state}`);
  }
  console.log("SMOKE_STEP get_task");
  const recovered = await call("get_task", { taskId: completed.id });
  if (!Array.isArray(recovered.messages) || recovered.messages.length < 2) {
    throw new Error("Recovered task did not preserve the conversation.");
  }
  if (recovered.task?.agentPackId !== commercePack.id) {
    throw new Error("Recovered task lost its selected Agent Pack.");
  }
  console.log("SMOKE_STEP partial_delivery");
  const partialTask = await call("start_task", {
    prompt: "创建一个必须落盘并验证的示例",
    title: "Electron Partial Delivery Smoke",
    workspaceRoot: path.resolve(scriptDirectory, ".."),
    provider: "local-smoke",
    model: "protocol-only",
    mode: "Build"
  });
  const partial = await call("complete_task", {
    taskId: partialTask.id,
    succeeded: true,
    outcome: "partial",
    detail: "PARTIAL · 没有真实文件写入，禁止冒充完成",
    draft: "仅返回了说明，没有交付文件。"
  });
  if (String(partial.state).toLowerCase() !== "paused") {
    throw new Error(`Partial delivery escaped the completion gate: ${partial.state}`);
  }
  await call("archive_task", { taskId: partial.id });
  console.log("SMOKE_STEP list_capabilities");
  const capabilities = await call("list_capabilities", {
    workspaceRoot: path.resolve(scriptDirectory, "..")
  });
  if (
    !Array.isArray(capabilities.mcp) ||
    !Array.isArray(capabilities.skills) ||
    !Array.isArray(capabilities.marketplace)
  ) {
    throw new Error("Capability dock projection is incomplete.");
  }
  console.log("SMOKE_STEP workspace_knowledge");
  const knowledgeWorkspace = path.resolve(scriptDirectory, "..");
  const knowledgeIndex = await call("index_workspace_knowledge", {
    workspaceRoot: knowledgeWorkspace
  }, 30000);
  if (
    typeof knowledgeIndex.summary?.scannedFiles !== "number" ||
    typeof knowledgeIndex.graph?.nodeCount !== "number"
  ) {
    throw new Error("Knowledge index projection is incomplete.");
  }
  const knowledgeState = await call("get_knowledge_state", {
    workspaceRoot: knowledgeWorkspace
  });
  if (
    !Array.isArray(knowledgeState.documents) ||
    typeof knowledgeState.chunks !== "number" ||
    !Array.isArray(knowledgeState.graph?.nodes)
  ) {
    throw new Error("Knowledge dock state is not renderer-compatible.");
  }
  const knowledgeSearch = await call("search_workspace_knowledge", {
    workspaceRoot: knowledgeWorkspace,
    query: "NOVA AgentOS",
    maximumResults: 5
  });
  if (!Array.isArray(knowledgeSearch.results)) {
    throw new Error("Knowledge search projection is incomplete.");
  }
  console.log("SMOKE_STEP living_memory");
  const livingMemory = await call("get_living_memory");
  if (!Array.isArray(livingMemory.habits) || !Array.isArray(livingMemory.skillCandidates)) {
    throw new Error("Living Memory projection is incomplete.");
  }
  console.log("SMOKE_STEP evolution_lab");
  const evolution = await call("get_evolution_lab");
  if (
    !evolution.policy ||
    typeof evolution.policy.enabled !== "boolean" ||
    !Array.isArray(evolution.experiments) ||
    typeof evolution.remainingTokensThisMonth !== "number"
  ) {
    throw new Error("Plugin Evolution Lab projection is incomplete.");
  }
  console.log("SMOKE_STEP desktop_snapshot");
  const desktop = await call("desktop_snapshot");
  if (!Array.isArray(desktop.windows) || typeof desktop.count !== "number") {
    throw new Error("Desktop Pilot observation projection is incomplete.");
  }
  if (
    desktop.windows.length > 0 &&
    (typeof desktop.windows[0].windowId !== "string" ||
      typeof desktop.windows[0].inputProtected !== "boolean")
  ) {
    throw new Error("Desktop Pilot window projection is not renderer-compatible.");
  }
  console.log("SMOKE_STEP list_store_sources");
  const sources = await call("list_store_sources");
  if (!Array.isArray(sources) || !sources.some((source) => source.id === "mcp-official")) {
    throw new Error("Capability store sources are not available.");
  }
  if (process.env.NOVA_STORE_LIVE_SMOKE === "1") {
    console.log("SMOKE_STEP search_capability_store_live");
    const store = await call("search_capability_store", {
      kind: "mcp",
      query: "filesystem"
    });
    if (!Array.isArray(store.items) || !store.items.length) {
      throw new Error("Official MCP registry returned no searchable items.");
    }
  }
  console.log("SMOKE_STEP archive_task");
  await call("archive_task", { taskId: completed.id });
  const archived = await call("list_archived_tasks");
  if (!Array.isArray(archived) || !archived.some((task) => task.id === completed.id)) {
    throw new Error("Archived task was not moved into the archive library.");
  }
  console.log("SMOKE_STEP restore_task");
  await call("restore_task", { taskId: completed.id });
  const restored = await call("list_tasks");
  if (!Array.isArray(restored) || !restored.some((task) => task.id === completed.id)) {
    throw new Error("Archived task was not restored into the active workspace.");
  }
  await call("archive_task", { taskId: completed.id });
  console.log("SMOKE_STEP delete_archived_task");
  const deleted = await call("delete_archived_task", { taskId: completed.id });
  if (!deleted.deleted || deleted.retainedWorkspaceFiles !== true) {
    throw new Error("Archived task deletion did not preserve the workspace boundary.");
  }
  await call("delete_archived_task", { taskId: partial.id });
  const afterDelete = await call("list_archived_tasks");
  if (afterDelete.some((task) => task.id === completed.id || task.id === partial.id)) {
    throw new Error("Deleted archive records are still visible.");
  }
  console.log(`NOVA_ELECTRON_BRIDGE_E2E_OK task=${completed.id}`);
  child.stdin.end();
  child.kill();
} catch (error) {
  console.error(`NOVA_ELECTRON_BRIDGE_E2E_FAILED: ${error.message}`);
  child.kill();
  process.exitCode = 1;
}
