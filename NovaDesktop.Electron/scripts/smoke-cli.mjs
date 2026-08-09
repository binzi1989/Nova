import assert from "node:assert/strict";
import fs from "node:fs";
import http from "node:http";
import os from "node:os";
import path from "node:path";
import { spawn } from "node:child_process";

const token = "nova-cli-smoke-token";
const cli = path.resolve("cli/nova.mjs");
const configDir = fs.mkdtempSync(path.join(os.tmpdir(), "nova-cli-smoke-"));

function reply(response, status, value) {
  response.writeHead(status, { "Content-Type": "application/json; charset=utf-8" });
  response.end(JSON.stringify(value));
}

const server = http.createServer((request, response) => {
  if (request.headers.authorization !== `Bearer ${token}`) {
    reply(response, 401, { error: "invalid_access_token" });
    return;
  }
  if (request.method === "GET" && request.url === "/v1/health") {
    reply(response, 200, {
      product: "NOVA AgentOS Extension Gateway",
      version: "1.0",
      status: "ready",
      permissions: ["tasks.read", "artifacts.read", "events.read", "actions.request"],
      pendingActionRequests: 0
    });
    return;
  }
  if (request.method === "GET" && request.url === "/v1/manifest") {
    reply(response, 200, {
      schema: "nova.extension-gateway/1.0",
      transport: ["http", "sse"],
      permissions: ["tasks.read", "artifacts.read", "events.read", "actions.request"],
      hooks: ["task.started", "task.completed"],
      routes: ["GET /v1/health", "GET /v1/tasks", "POST /v1/action-requests"]
    });
    return;
  }
  if (request.method === "GET" && request.url === "/v1/tasks") {
    reply(response, 200, { tasks: [{ id: "task-1", title: "CLI 冒烟任务", state: "completed", progress: 100 }] });
    return;
  }
  if (request.method === "GET" && request.url === "/v1/tasks/task-1") {
    reply(response, 200, {
      id: "task-1",
      title: "CLI 冒烟任务",
      state: "completed",
      progress: 100,
      delivery: { status: "READY", artifacts: [{ id: "artifact-1", title: "冒烟报告", relativePath: "冒烟报告.md" }] }
    });
    return;
  }
  if (request.method === "GET" && request.url === "/v1/tasks/task-1/artifacts") {
    reply(response, 200, {
      taskId: "task-1",
      artifacts: [{ id: "artifact-1", title: "冒烟报告", relativePath: "冒烟报告.md", kind: "file", size: 128 }]
    });
    return;
  }
  if (request.method === "GET" && request.url === "/v1/tasks/task-1/context") {
    reply(response, 200, {
      schema: "nova.task-capsule/1.0",
      taskId: "task-1",
      goal: "验证 CLI 上下文诊断",
      executionMode: "Build",
      characterBudget: 44000,
      usedCharacters: 12000,
      estimatedPromptTokens: 4000,
      estimatedCharactersAvoided: 24000,
      estimatedTokensAvoided: 8000,
      layers: [{ id: "L0.goal", label: "当前目标", sourceCharacters: 20, includedCharacters: 20, reason: "当前目标" }],
      selections: [{ relativePath: "src/App.tsx", score: 42, reasons: ["路径命中"], startLine: 1, endLine: 20, includedCharacters: 500 }],
      exclusions: []
    });
    return;
  }
  if (request.method === "GET" && request.url?.startsWith("/v1/budget?")) {
    const target = new URL(request.url, "http://127.0.0.1");
    const characters = Number(target.searchParams.get("characters") || 0);
    reply(response, 200, {
      schema: "nova.context-budget/1.0",
      policy: {
        mode: target.searchParams.get("mode") || "Build",
        characterBudget: 44000,
        workspaceCharacterBudget: 16000,
        estimatedTokenBudget: 14667,
        inputCharacters: characters,
        estimatedInputTokens: Math.ceil(characters / 3),
        strategy: "构建"
      },
      capsule: null
    });
    return;
  }
  if (request.method === "POST" && request.url === "/v1/action-requests") {
    let body = "";
    request.setEncoding("utf8");
    request.on("data", (chunk) => { body += chunk; });
    request.on("end", () => {
      const value = JSON.parse(body);
      reply(response, 202, {
        request: { id: "request-1", title: value.title || value.prompt, prompt: value.prompt, status: "pending" }
      });
    });
    return;
  }
  reply(response, 404, { error: "route_not_found" });
});

function run(args, extraEnv = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [cli, ...args, "--json"], {
      cwd: path.resolve("."),
      windowsHide: true,
      env: {
        ...process.env,
        NOVA_CLI_CONFIG_DIR: configDir,
        NOVA_GATEWAY_URL: `http://127.0.0.1:${server.address().port}`,
        NOVA_GATEWAY_TOKEN: token,
        ...extraEnv
      }
    });
    let stdout = "";
    let stderr = "";
    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");
    child.stdout.on("data", (chunk) => { stdout += chunk; });
    child.stderr.on("data", (chunk) => { stderr += chunk; });
    child.once("error", reject);
    child.once("exit", (code) => {
      if (code !== 0) {
        reject(new Error(`CLI exited ${code}: ${stderr || stdout}`));
        return;
      }
      resolve(JSON.parse(stdout));
    });
  });
}

try {
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  const status = await run(["status"]);
  assert.equal(status.status, "ready");
  const doctor = await run(["doctor"]);
  assert.equal(doctor.ok, true);
  const tasks = await run(["task", "list"]);
  assert.equal(tasks.tasks[0].id, "task-1");
  const task = await run(["task", "show", "task-1"]);
  assert.equal(task.delivery.artifacts.length, 1);
  const request = await run(["task", "request", "生成一份可验证报告", "--title", "CLI 请求", "--mode", "Plan"]);
  assert.equal(request.request.status, "pending");
  const delivery = await run(["delivery", "list", "task-1"]);
  assert.equal(delivery.artifacts[0].relativePath, "冒烟报告.md");
  const manifest = await run(["hooks", "manifest"]);
  assert.equal(manifest.schema, "nova.extension-gateway/1.0");
  const capsule = await run(["context", "inspect", "task-1"]);
  assert.equal(capsule.schema, "nova.task-capsule/1.0");
  const budget = await run(["budget", "estimate", "检查工作区", "--mode", "Build"]);
  assert.equal(budget.policy.mode, "Build");
  console.log("NOVA_CLI_SMOKE_OK");
} finally {
  await new Promise((resolve) => server.close(resolve));
  fs.rmSync(configDir, { recursive: true, force: true });
}
