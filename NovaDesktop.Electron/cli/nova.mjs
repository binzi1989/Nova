#!/usr/bin/env node

import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import http from "node:http";
import https from "node:https";
import readline from "node:readline/promises";
import { spawnSync } from "node:child_process";
import process from "node:process";

const VERSION = "0.1.0-p0";
const CONFIG_ROOT = process.env.NOVA_CLI_CONFIG_DIR
  ? path.resolve(process.env.NOVA_CLI_CONFIG_DIR)
  : path.join(os.homedir(), ".nova-agentos");
const CONFIG_FILE = path.join(CONFIG_ROOT, "config.json");
const CREDENTIAL_FILE = path.join(CONFIG_ROOT, process.platform === "win32" ? "credential.dpapi" : "credential");
const JSON_MODE = process.argv.includes("--json");

class CliError extends Error {
  constructor(message, code = "cli_error", details = null) {
    super(message);
    this.code = code;
    this.details = details;
  }
}

function sessionCandidates() {
  if (process.env.NOVA_GATEWAY_SESSION) return [path.resolve(process.env.NOVA_GATEWAY_SESSION)];
  if (process.platform === "win32") {
    const appData = process.env.APPDATA || path.join(os.homedir(), "AppData", "Roaming");
    return [
      path.join(appData, "NOVA AgentOS", "extension-gateway", "session.json"),
      path.join(appData, "nova-agentos-electron", "extension-gateway", "session.json")
    ];
  }
  if (process.platform === "darwin") {
    return [path.join(os.homedir(), "Library", "Application Support", "NOVA AgentOS", "extension-gateway", "session.json")];
  }
  const config = process.env.XDG_CONFIG_HOME || path.join(os.homedir(), ".config");
  return [path.join(config, "NOVA AgentOS", "extension-gateway", "session.json")];
}

function readJson(file, fallback = null) {
  try {
    return JSON.parse(fs.readFileSync(file, "utf8"));
  } catch {
    return fallback;
  }
}

function readConfig() {
  const value = readJson(CONFIG_FILE, {});
  return value && typeof value === "object" ? value : {};
}

function writeConfig(value) {
  fs.mkdirSync(CONFIG_ROOT, { recursive: true, mode: 0o700 });
  fs.writeFileSync(CONFIG_FILE, `${JSON.stringify(value, null, 2)}\n`, { encoding: "utf8", mode: 0o600 });
}

function validateBaseUrl(value) {
  let parsed;
  try {
    parsed = new URL(String(value || ""));
  } catch {
    throw new CliError("Gateway 地址无效。", "invalid_gateway_url");
  }
  const host = parsed.hostname.replace(/^\[|\]$/g, "");
  if (parsed.protocol !== "http:" || !["127.0.0.1", "localhost", "::1"].includes(host)) {
    throw new CliError("P0 CLI 只允许连接本机 HTTP Gateway。", "gateway_not_local");
  }
  parsed.pathname = "";
  parsed.search = "";
  parsed.hash = "";
  return parsed.toString().replace(/\/$/, "");
}

function discoverGateway() {
  if (process.env.NOVA_GATEWAY_URL) {
    return { baseUrl: validateBaseUrl(process.env.NOVA_GATEWAY_URL), source: "environment", sessionPath: null };
  }
  for (const candidate of sessionCandidates()) {
    const session = readJson(candidate);
    if (session?.baseUrl) {
      return { baseUrl: validateBaseUrl(session.baseUrl), source: "desktop-session", sessionPath: candidate, session };
    }
  }
  const configured = readConfig().baseUrl;
  if (configured) return { baseUrl: validateBaseUrl(configured), source: "configuration", sessionPath: null };
  throw new CliError(
    "没有发现正在运行的 NOVA Gateway。请先在桌面端扩展坞开启 Gateway，或执行 nova configure <URL>。",
    "gateway_not_discovered"
  );
}

function runPowerShell(script, input = "") {
  const result = spawnSync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script, CREDENTIAL_FILE], {
    input,
    encoding: "utf8",
    windowsHide: true
  });
  if (result.status !== 0) throw new CliError(String(result.stderr || "Windows 凭据保护失败。").trim(), "credential_error");
  return String(result.stdout || "").trim();
}

function saveToken(token) {
  fs.mkdirSync(CONFIG_ROOT, { recursive: true, mode: 0o700 });
  if (process.platform === "win32") {
    runPowerShell(
      "$plain=[Console]::In.ReadToEnd(); $secure=ConvertTo-SecureString $plain -AsPlainText -Force; ConvertFrom-SecureString $secure | Set-Content -LiteralPath $args[0] -NoNewline",
      token
    );
    return;
  }
  fs.writeFileSync(CREDENTIAL_FILE, token, { encoding: "utf8", mode: 0o600 });
  fs.chmodSync(CREDENTIAL_FILE, 0o600);
}

function readToken() {
  if (process.env.NOVA_GATEWAY_TOKEN) return String(process.env.NOVA_GATEWAY_TOKEN).trim();
  if (!fs.existsSync(CREDENTIAL_FILE)) return "";
  if (process.platform === "win32") {
    return runPowerShell("$secure=Get-Content -Raw -LiteralPath $args[0] | ConvertTo-SecureString; ([System.Net.NetworkCredential]::new('', $secure)).Password");
  }
  return fs.readFileSync(CREDENTIAL_FILE, "utf8").trim();
}

function removeToken() {
  try {
    fs.unlinkSync(CREDENTIAL_FILE);
  } catch (error) {
    if (error?.code !== "ENOENT") throw error;
  }
}

async function readSecret(prompt) {
  if (!process.stdin.isTTY) {
    let value = "";
    for await (const chunk of process.stdin) value += chunk;
    return value.trim();
  }
  process.stdout.write(prompt);
  process.stdin.setRawMode(true);
  process.stdin.resume();
  let value = "";
  return new Promise((resolve, reject) => {
    const restore = () => {
      process.stdin.off("data", onData);
      process.stdin.setRawMode(false);
      process.stdin.pause();
      process.stdout.write("\n");
    };
    const onData = (buffer) => {
      const text = buffer.toString("utf8");
      if (text === "\u0003") {
        restore();
        reject(new CliError("已取消。", "cancelled"));
      } else if (text === "\r" || text === "\n") {
        restore();
        resolve(value.trim());
      } else if (text === "\u007f" || text === "\b") {
        if (value) {
          value = value.slice(0, -1);
          process.stdout.write("\b \b");
        }
      } else if (!/[\u0000-\u001f]/.test(text)) {
        value += text;
        process.stdout.write("*");
      }
    };
    process.stdin.on("data", onData);
  });
}

function requestJson(baseUrl, token, route, { method = "GET", body = null, timeout = 10000 } = {}) {
  const target = new URL(route, `${baseUrl}/`);
  const payload = body == null ? "" : JSON.stringify(body);
  const transport = target.protocol === "https:" ? https : http;
  return new Promise((resolve, reject) => {
    const request = transport.request(target, {
      method,
      headers: {
        Authorization: `Bearer ${token}`,
        Accept: "application/json",
        ...(payload ? { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(payload) } : {})
      }
    }, (response) => {
      let raw = "";
      response.setEncoding("utf8");
      response.on("data", (chunk) => { raw = `${raw}${chunk}`.slice(0, 4_000_000); });
      response.on("end", () => {
        let value = {};
        try { value = raw ? JSON.parse(raw) : {}; } catch { value = { raw }; }
        if ((response.statusCode || 500) >= 400) {
          const status = response.statusCode || 500;
          const message = value.message || value.error || `HTTP ${status}`;
          reject(new CliError(String(message), status === 401 ? "unauthorized" : "gateway_error", { status, value }));
          return;
        }
        resolve(value);
      });
    });
    request.setTimeout(timeout, () => request.destroy(new CliError("Gateway 响应超时。", "gateway_timeout")));
    request.once("error", (error) => reject(error instanceof CliError ? error : new CliError(error.message, "gateway_unreachable")));
    if (payload) request.write(payload);
    request.end();
  });
}

function output(value, render) {
  if (JSON_MODE) {
    process.stdout.write(`${JSON.stringify(value, null, 2)}\n`);
    return;
  }
  render(value);
}

function table(rows, columns) {
  if (!rows.length) {
    console.log("暂无记录。");
    return;
  }
  const widths = columns.map(({ key, label, max = 40 }) => Math.min(max, Math.max(label.length, ...rows.map((row) => String(row[key] ?? "").length))));
  console.log(columns.map((column, index) => column.label.padEnd(widths[index])).join("  "));
  console.log(widths.map((width) => "─".repeat(width)).join("  "));
  for (const row of rows) {
    console.log(columns.map((column, index) => {
      const raw = String(row[column.key] ?? "");
      const value = raw.length > widths[index] ? `${raw.slice(0, Math.max(1, widths[index] - 1))}…` : raw;
      return value.padEnd(widths[index]);
    }).join("  "));
  }
}

function option(args, name, fallback = "") {
  const index = args.indexOf(name);
  if (index < 0) return fallback;
  if (!args[index + 1] || args[index + 1].startsWith("--")) throw new CliError(`${name} 缺少参数。`, "invalid_arguments");
  const value = args[index + 1];
  args.splice(index, 2);
  return value;
}

function context() {
  const gateway = discoverGateway();
  const token = readToken();
  if (!token) throw new CliError("尚未保存 Gateway 访问令牌。请执行 nova auth login。", "credential_missing");
  return { ...gateway, token };
}

function help() {
  console.log(`NOVA AgentOS CLI ${VERSION}

用法:
  nova status                         查看 AgentOS 与 Gateway 状态
  nova doctor                         诊断会话、鉴权与接口契约
  nova configure <URL>                保存本机 Gateway 地址
  nova auth login|logout|status       管理本机访问令牌
  nova task list                      查看任务
  nova task show <TASK_ID>            查看任务与交付摘要
  nova task request <GOAL> [选项]      向桌面端提交待审阅任务
  nova task watch                     持续观察 AgentOS 事件
  nova delivery list <TASK_ID>        查看交付物清单
  nova context inspect <TASK_ID>      查看 Task Capsule 预算与分层
  nova context explain <TASK_ID>      解释文件为何被纳入上下文
  nova budget show [TASK_ID]          查看本轮或指定任务预算
  nova budget estimate <GOAL>         估算目标的基础输入成本
  nova hooks manifest                 查看 Hook 和权限契约

task request 选项:
  --title <标题>  --source <来源>  --mode Ask|Plan|Build|Goal  --agent <PACK_ID>

全局选项:
  --json                              输出机器可读 JSON
  --version                           输出版本

安全边界: CLI 只能连接本机 Gateway；任务请求必须回到桌面端由用户审阅。`);
}

async function statusCommand() {
  const ctx = context();
  const health = await requestJson(ctx.baseUrl, ctx.token, "/v1/health");
  output({ ...health, discovery: ctx.source, sessionPath: ctx.sessionPath }, (value) => {
    console.log(`NOVA AgentOS: ${value.status === "ready" ? "就绪" : value.status}`);
    console.log(`Gateway: ${ctx.baseUrl}`);
    console.log(`发现方式: ${ctx.source}`);
    console.log(`权限: ${(value.permissions || []).join(", ")}`);
    console.log(`待审阅请求: ${value.pendingActionRequests ?? 0}`);
  });
}

async function doctorCommand() {
  const checks = [];
  let gateway;
  try {
    gateway = discoverGateway();
    checks.push({ name: "Gateway 会话发现", status: "pass", detail: gateway.baseUrl });
  } catch (error) {
    checks.push({ name: "Gateway 会话发现", status: "fail", detail: error.message });
    output({ ok: false, checks }, (value) => table(value.checks, [
      { key: "status", label: "状态", max: 8 }, { key: "name", label: "检查", max: 24 }, { key: "detail", label: "说明", max: 70 }
    ]));
    process.exitCode = 2;
    return;
  }
  const token = readToken();
  checks.push({ name: "访问令牌", status: token ? "pass" : "fail", detail: token ? "已加载（不会显示）" : "执行 nova auth login" });
  if (token) {
    try {
      const health = await requestJson(gateway.baseUrl, token, "/v1/health");
      checks.push({ name: "Gateway 鉴权", status: "pass", detail: health.status || "ready" });
      const manifest = await requestJson(gateway.baseUrl, token, "/v1/manifest");
      checks.push({ name: "接口契约", status: "pass", detail: `${manifest.routes?.length || 0} routes · ${manifest.hooks?.length || 0} hooks` });
    } catch (error) {
      checks.push({ name: "Gateway 鉴权", status: "fail", detail: error.code === "unauthorized" ? "令牌已轮换，请重新执行 nova auth login" : error.message });
    }
  }
  const ok = checks.every((item) => item.status === "pass");
  output({ ok, checks }, (value) => table(value.checks, [
    { key: "status", label: "状态", max: 8 }, { key: "name", label: "检查", max: 24 }, { key: "detail", label: "说明", max: 70 }
  ]));
  if (!ok) process.exitCode = 2;
}

async function authCommand(action) {
  if (action === "logout") {
    removeToken();
    output({ authenticated: false }, () => console.log("本机 CLI 凭据已移除。"));
    return;
  }
  if (action === "status") {
    const stored = Boolean(readToken());
    output({ authenticated: stored, source: process.env.NOVA_GATEWAY_TOKEN ? "environment" : stored ? "credential-store" : "none" },
      (value) => console.log(value.authenticated ? `已保存访问令牌（${value.source}）。` : "尚未保存访问令牌。"));
    return;
  }
  if (action !== "login") throw new CliError("auth 仅支持 login、logout 或 status。", "invalid_arguments");
  const gateway = discoverGateway();
  const token = await readSecret("粘贴桌面端扩展坞显示的 Gateway Token: ");
  if (!token) throw new CliError("访问令牌不能为空。", "credential_missing");
  await requestJson(gateway.baseUrl, token, "/v1/health");
  saveToken(token);
  output({ authenticated: true, baseUrl: gateway.baseUrl }, (value) => console.log(`已连接并安全保存凭据：${value.baseUrl}`));
}

async function taskCommand(action, args) {
  const ctx = context();
  if (action === "list") {
    const result = await requestJson(ctx.baseUrl, ctx.token, "/v1/tasks");
    output(result, (value) => table(value.tasks || [], [
      { key: "id", label: "任务 ID", max: 32 }, { key: "title", label: "标题", max: 38 },
      { key: "state", label: "状态", max: 14 }, { key: "progress", label: "进度", max: 8 }
    ]));
    return;
  }
  if (action === "show") {
    const taskId = args.shift();
    if (!taskId) throw new CliError("缺少 TASK_ID。", "invalid_arguments");
    const task = await requestJson(ctx.baseUrl, ctx.token, `/v1/tasks/${encodeURIComponent(taskId)}`);
    output(task, (value) => {
      console.log(`${value.title} (${value.id})`);
      console.log(`状态: ${value.state} · 进度: ${value.progress}% · 模式: ${value.executionMode || "-"}`);
      if (value.delivery) {
        console.log(`交付: ${value.delivery.status} · ${value.delivery.artifacts?.length || 0} 个文件`);
        if (value.delivery.outcome) console.log(value.delivery.outcome);
      }
    });
    return;
  }
  if (action === "request") {
    const title = option(args, "--title");
    const source = option(args, "--source", "NOVA CLI");
    const executionMode = option(args, "--mode", "Plan");
    const agentPackId = option(args, "--agent");
    const prompt = args.filter((value) => value !== "--json").join(" ").trim();
    if (!prompt) throw new CliError("请提供任务目标。", "invalid_arguments");
    const result = await requestJson(ctx.baseUrl, ctx.token, "/v1/action-requests", {
      method: "POST",
      body: { title, prompt, source, executionMode, agentPackId }
    });
    output(result, (value) => {
      console.log(`任务请求已进入桌面端审阅箱：${value.request.id}`);
      console.log(`标题: ${value.request.title}`);
      console.log("下一步: 回到 NOVA 桌面端确认或拒绝；CLI 不会绕过权限直接执行。");
    });
    return;
  }
  if (action === "watch") {
    await watchCommand(ctx);
    return;
  }
  throw new CliError("task 仅支持 list、show、request 或 watch。", "invalid_arguments");
}

async function watchCommand(ctx) {
  const target = new URL("/v1/events", `${ctx.baseUrl}/`);
  if (!JSON_MODE) console.log(`正在观察 ${ctx.baseUrl}，按 Ctrl+C 停止。`);
  await new Promise((resolve, reject) => {
    const request = http.get(target, {
      headers: { Accept: "text/event-stream", Authorization: `Bearer ${ctx.token}` }
    }, (response) => {
      if ((response.statusCode || 500) >= 400) {
        response.resume();
        reject(new CliError(`事件流连接失败：HTTP ${response.statusCode}`, response.statusCode === 401 ? "unauthorized" : "gateway_error"));
        return;
      }
      response.setEncoding("utf8");
      let buffer = "";
      response.on("data", (chunk) => {
        buffer += chunk;
        let boundary;
        while ((boundary = buffer.indexOf("\n\n")) >= 0) {
          const frame = buffer.slice(0, boundary);
          buffer = buffer.slice(boundary + 2);
          const data = frame.split("\n").filter((line) => line.startsWith("data:")).map((line) => line.slice(5).trim()).join("\n");
          if (!data) continue;
          try {
            const event = JSON.parse(data);
            if (JSON_MODE) console.log(JSON.stringify(event));
            else console.log(`${event.occurredAt || ""}  ${event.type || "event"}${event.taskId ? `  ${event.taskId}` : ""}`);
          } catch {
            if (!JSON_MODE) console.log(data);
          }
        }
      });
      response.on("end", resolve);
    });
    request.once("error", (error) => reject(new CliError(error.message, "gateway_unreachable")));
    process.once("SIGINT", () => {
      request.destroy();
      resolve();
    });
  });
}

async function deliveryCommand(action, args) {
  if (action !== "list") throw new CliError("delivery 仅支持 list。", "invalid_arguments");
  const taskId = args.shift();
  if (!taskId) throw new CliError("缺少 TASK_ID。", "invalid_arguments");
  const ctx = context();
  const result = await requestJson(ctx.baseUrl, ctx.token, `/v1/tasks/${encodeURIComponent(taskId)}/artifacts`);
  output(result, (value) => table(value.artifacts || [], [
    { key: "title", label: "交付物", max: 38 }, { key: "relativePath", label: "相对路径", max: 52 },
    { key: "kind", label: "类型", max: 12 }, { key: "size", label: "字节", max: 12 }
  ]));
}

async function manifestCommand() {
  const ctx = context();
  const manifest = await requestJson(ctx.baseUrl, ctx.token, "/v1/manifest");
  output(manifest, (value) => {
    console.log(`${value.schema} · ${value.transport.join(" + ")}`);
    console.log(`权限: ${value.permissions.join(", ")}`);
    console.log("路由:");
    for (const route of value.routes || []) console.log(`  ${route}`);
    console.log("Hooks:");
    for (const hook of value.hooks || []) console.log(`  ${hook}`);
  });
}

async function contextCommand(action, args) {
  if (!["inspect", "explain"].includes(action)) {
    throw new CliError("context 仅支持 inspect 或 explain。", "invalid_arguments");
  }
  const taskId = args.shift();
  if (!taskId) throw new CliError("缺少 TASK_ID。", "invalid_arguments");
  const ctx = context();
  const capsule = await requestJson(
    ctx.baseUrl,
    ctx.token,
    `/v1/tasks/${encodeURIComponent(taskId)}/context`
  );
  output(capsule, (value) => {
    if (value.status === "not-compiled") {
      console.log(value.detail || "任务尚未生成 Task Capsule。");
      return;
    }
    console.log(`${value.taskId} · ${value.executionMode} · Task Capsule`);
    console.log(`上下文: ${value.usedCharacters}/${value.characterBudget} 字符 · 约 ${value.estimatedPromptTokens} Token`);
    console.log(`预计避免重复输入: ${value.estimatedCharactersAvoided} 字符 · 约 ${value.estimatedTokensAvoided} Token`);
    console.log(`工作区证据: ${value.contextCacheHit ? "已复用缓存" : "本轮更新"}`);
    if (action === "inspect") {
      table(value.layers || [], [
        { key: "id", label: "层", max: 18 }, { key: "label", label: "内容", max: 26 },
        { key: "sourceCharacters", label: "原始字符", max: 12 },
        { key: "includedCharacters", label: "纳入字符", max: 12 }, { key: "reason", label: "原因", max: 60 }
      ]);
      return;
    }
    table((value.selections || []).map((item) => ({
      ...item,
      lines: `${item.startLine}-${item.endLine}`,
      reason: (item.reasons || []).join("；")
    })), [
      { key: "relativePath", label: "文件", max: 52 }, { key: "score", label: "信号", max: 8 },
      { key: "lines", label: "行", max: 12 }, { key: "reason", label: "纳入原因", max: 64 }
    ]);
    for (const excluded of value.exclusions || []) console.log(`未纳入：${excluded}`);
  });
}

async function budgetCommand(action, args) {
  if (!["show", "estimate"].includes(action)) {
    throw new CliError("budget 仅支持 show 或 estimate。", "invalid_arguments");
  }
  const mode = option(args, "--mode", "Build");
  let taskId = "";
  let characters = 0;
  let goal = "";
  if (action === "show") taskId = args.shift() || "";
  else {
    goal = args.join(" ").trim();
    if (!goal) throw new CliError("请提供需要估算的目标。", "invalid_arguments");
    characters = goal.length;
  }
  const ctx = context();
  const query = new URLSearchParams({ mode, characters: String(characters) });
  if (taskId) query.set("taskId", taskId);
  const result = await requestJson(ctx.baseUrl, ctx.token, `/v1/budget?${query}`);
  output({ ...result, goal: goal || undefined }, (value) => {
    const policy = value.policy || {};
    console.log(`${policy.mode} · ${policy.strategy}`);
    console.log(`Task Capsule 上限: ${policy.characterBudget} 字符 · 约 ${policy.estimatedTokenBudget} Token`);
    console.log(`工作区证据上限: ${policy.workspaceCharacterBudget} 字符`);
    if (action === "estimate") {
      console.log(`当前目标: ${policy.inputCharacters} 字符 · 约 ${policy.estimatedInputTokens} Token（不含历史和文件）`);
    }
    if (value.capsule) {
      console.log(`实际最近一次: ${value.capsule.usedCharacters}/${value.capsule.characterBudget} 字符`);
      console.log(`预计避免重复输入: 约 ${value.capsule.estimatedTokensAvoided} Token`);
    }
  });
}

async function main() {
  const args = process.argv.slice(2).filter((value) => value !== "--json");
  if (args.includes("--version") || args[0] === "version") {
    console.log(VERSION);
    return;
  }
  const command = args.shift();
  if (!command || command === "help" || command === "--help" || command === "-h") {
    help();
    return;
  }
  if (command === "configure") {
    const baseUrl = validateBaseUrl(args.shift());
    writeConfig({ ...readConfig(), baseUrl });
    output({ baseUrl }, (value) => console.log(`已保存 Gateway 地址：${value.baseUrl}`));
    return;
  }
  if (command === "status") return statusCommand();
  if (command === "doctor") return doctorCommand();
  if (command === "auth") return authCommand(args.shift());
  if (command === "task") return taskCommand(args.shift(), args);
  if (command === "delivery") return deliveryCommand(args.shift(), args);
  if (command === "context") return contextCommand(args.shift(), args);
  if (command === "budget") return budgetCommand(args.shift(), args);
  if (command === "hooks" && args.shift() === "manifest") return manifestCommand();
  throw new CliError(`未知命令：${command}`, "unknown_command");
}

main().catch((error) => {
  const normalized = error instanceof CliError ? error : new CliError(error?.message || String(error));
  if (JSON_MODE) {
    console.error(JSON.stringify({ ok: false, error: normalized.code, message: normalized.message, details: normalized.details }));
  } else {
    console.error(`错误：${normalized.message}`);
    if (normalized.code === "unauthorized") console.error("访问令牌可能已轮换，请执行 nova auth login 后重试。");
  }
  process.exitCode = 1;
});
