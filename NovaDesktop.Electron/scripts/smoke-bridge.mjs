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
  console.log(`SMOKE_RX ${line}`);
  const message = JSON.parse(line);
  const callback = pending.get(message.id);
  if (!callback) return;
  pending.delete(message.id);
  message.error
    ? callback.reject(new Error(message.error.message))
    : callback.resolve(message.result);
});

function call(method, params = {}) {
  const id = `bridge-smoke-${++sequence}`;
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      pending.delete(id);
      reject(new Error(`${method} timed out after 10 seconds`));
    }, 10000);
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
  console.log("SMOKE_STEP start_task");
  const started = await call("start_task", {
    prompt: "Electron Bridge 端到端验证",
    title: "Electron Bridge Smoke",
    workspaceRoot: path.resolve(scriptDirectory, ".."),
    provider: "local-smoke",
    model: "protocol-only",
    mode: "Build"
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
  console.log(`NOVA_ELECTRON_BRIDGE_E2E_OK task=${completed.id}`);
  child.stdin.end();
  child.kill();
} catch (error) {
  console.error(`NOVA_ELECTRON_BRIDGE_E2E_FAILED: ${error.message}`);
  child.kill();
  process.exitCode = 1;
}
