const {
  app,
  BrowserWindow,
  clipboard,
  dialog,
  ipcMain,
  safeStorage,
  screen,
  session,
  shell
} = require("electron");
const { spawn } = require("node:child_process");
const fs = require("node:fs");
const path = require("node:path");
const crypto = require("node:crypto");
const http = require("node:http");
const os = require("node:os");
const readline = require("node:readline");

const isDev = !app.isPackaged;
const isWorkshopRecoverySmoke =
  process.argv.includes("--smoke-workshop-recovery")
  || app.commandLine.hasSwitch("smoke-workshop-recovery");
const isGatewaySmoke =
  process.argv.includes("--smoke-gateway")
  || app.commandLine.hasSwitch("smoke-gateway");
const isKnowledgeWindowSmoke =
  process.argv.includes("--smoke-knowledge-window")
  || app.commandLine.hasSwitch("smoke-knowledge-window");
const isCredentialSmoke =
  process.argv.includes("--smoke-credentials")
  || app.commandLine.hasSwitch("smoke-credentials");
const isSmoke =
  isWorkshopRecoverySmoke
  || isGatewaySmoke
  || isKnowledgeWindowSmoke
  || isCredentialSmoke
  || process.argv.includes("--smoke")
  || app.commandLine.hasSwitch("smoke");
// A subset of Windows Intel/virtual display drivers can lose Electron's GPU
// process during a large task-view repaint. Chromium then leaves a completely
// white BrowserWindow even though the task continues in the AgentOS host.
// NOVA favours task reliability over GPU compositing on Windows; macOS keeps
// native acceleration and animations.
if (isSmoke || process.platform === "win32") {
  app.disableHardwareAcceleration();
  app.commandLine.appendSwitch("disable-gpu");
  app.commandLine.appendSwitch("disable-gpu-compositing");
}
const credentialSmokeUserData = isCredentialSmoke
  ? path.join(os.tmpdir(), `nova-credential-smoke-${process.pid}-${crypto.randomUUID()}`)
  : "";
if (credentialSmokeUserData) {
  fs.mkdirSync(credentialSmokeUserData, { recursive: true });
  app.setPath("userData", credentialSmokeUserData);
}
const modelConnections = new Map();
const MODEL_CONNECTION_STORE_VERSION = 1;
const approvedAttachments = new Set();
const approvedWorkspaceRoots = new Set();
const cancelledRuns = new Set();
const activeRuns = new Map();
const activeAgentPackBuilds = new Map();
const activeWorkshopRuns = new Map();
let mainWindow;
let knowledgeWindow;
let bridge;
let manualZoomFactor = null;
let adaptiveZoomTimer = null;
const ownsInstance = isSmoke || app.requestSingleInstanceLock();
const extensionGateway = {
  server: null,
  port: 0,
  token: "",
  startedAt: null,
  clients: new Set(),
  events: [],
  sequence: 0,
  keepAlive: null
};
const extensionGatewayHooks = [
  "task.started",
  "context.compiled",
  "artifact.created",
  "delivery.ready",
  "action.requested"
];

if (!ownsInstance) {
  app.quit();
}

class BridgeClient {
  constructor() {
    this.pending = new Map();
    this.sequence = 0;
    this.process = null;
  }

  start() {
    if (this.process) return;

    const bridgeExecutable =
      process.platform === "win32"
        ? "Nova.AgentOS.Bridge.exe"
        : "Nova.AgentOS.Bridge";
    const executable = app.isPackaged
      ? path.join(process.resourcesPath, "bridge", bridgeExecutable)
      : "dotnet";
    const args = app.isPackaged
      ? []
      : [
          path.resolve(
            __dirname,
            "..",
            "..",
            "Nova.AgentOS.Bridge",
            "bin",
            "Release",
            "net8.0",
            "Nova.AgentOS.Bridge.dll"
          )
        ];

    if (app.isPackaged && !fs.existsSync(executable)) {
      throw new Error("AgentOS Bridge 不存在，安装包可能不完整。");
    }
    if (!app.isPackaged && !fs.existsSync(args[0])) {
      throw new Error("AgentOS Bridge 尚未编译，请先运行 dotnet build Nova.AgentOS.Bridge。");
    }

    this.process = spawn(executable, args, {
      windowsHide: process.platform === "win32",
      stdio: ["pipe", "pipe", "pipe"]
    });

    readline
      .createInterface({ input: this.process.stdout })
      .on("line", (line) => this.onLine(line));

    this.process.stderr.on("data", (buffer) => {
      const text = buffer.toString().trim();
      if (text) console.error(`[AgentOS Bridge] ${text}`);
    });

    this.process.once("exit", (code) => {
      const error = new Error(`AgentOS Bridge 已停止（${code ?? "unknown"}）。`);
      for (const { reject } of this.pending.values()) reject(error);
      this.pending.clear();
      this.process = null;
    });
  }

  onLine(line) {
    let message;
    try {
      message = JSON.parse(line);
    } catch {
      console.error("[AgentOS Bridge] 非法协议输出", line);
      return;
    }

    if (message.event) {
      this.onEvent?.(message.event, message.payload);
      return;
    }

    const pending = this.pending.get(message.id);
    if (!pending) return;
    this.pending.delete(message.id);
    clearTimeout(pending.timeout);
    if (message.error) {
      pending.reject(new Error(message.error.message || message.error.code));
    } else {
      pending.resolve(message.result);
    }
  }

  call(method, params = {}) {
    this.start();
    const id = `electron-${Date.now()}-${++this.sequence}`;
    return new Promise((resolve, reject) => {
      const timeoutMs =
        method === "run_agent" || method === "run_design_session" || method === "verify_result"
          ? 30 * 60 * 1000
          : method === "start_task"
            ? 2 * 60 * 1000
            : method === "boot"
              ? 60 * 1000
              : 30000;
      const timeout = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`AgentOS ${method} 响应超时。`));
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timeout });
      this.process.stdin.write(`${JSON.stringify({ id, method, params })}\n`);
    });
  }

  stop() {
    this.process?.kill();
    this.process = null;
  }
}

function safeError(error) {
  const message = error instanceof Error ? error.message : String(error);
  return message
    .replace(/sk-[A-Za-z0-9_-]{12,}/g, "[credential]")
    .replace(/[A-Za-z]:\\[^\r\n"]+/g, "[local path]");
}

function recordWorkshopFailure(request, error) {
  const id = `AW-${Date.now().toString(36).toUpperCase()}-${crypto.randomBytes(2).toString("hex").toUpperCase()}`;
  try {
    const logDirectory = path.join(app.getPath("userData"), "logs");
    fs.mkdirSync(logDirectory, { recursive: true });
    fs.appendFileSync(
      path.join(logDirectory, "agent-workshop.jsonl"),
      `${JSON.stringify({
        id,
        at: new Date().toISOString(),
        provider: String(request?.provider || ""),
        model: String(request?.model || ""),
        name: String(request?.name || "").slice(0, 120),
        error: safeError(error).slice(0, 1200)
      })}\n`,
      "utf8"
    );
  } catch {
    // Diagnostics must never replace the original model failure.
  }
  return id;
}

function recordRendererFailure(source, error, extra = {}) {
  try {
    const logDirectory = path.join(app.getPath("userData"), "logs");
    fs.mkdirSync(logDirectory, { recursive: true });
    const logPath = path.join(logDirectory, "renderer-errors.jsonl");
    fs.appendFileSync(
      logPath,
      `${JSON.stringify({
        at: new Date().toISOString(),
        source: String(source || "renderer"),
        message: safeError(error).slice(0, 2400),
        ...extra
      })}\n`,
      "utf8"
    );
    return logPath;
  } catch {
    return "";
  }
}

function workshopSessionStorePath() {
  return path.join(app.getPath("userData"), "agent-workshop", "design-sessions.json");
}

function readWorkshopSessions() {
  try {
    const storePath = workshopSessionStorePath();
    if (!fs.existsSync(storePath)) return [];
    const value = JSON.parse(fs.readFileSync(storePath, "utf8"));
    return Array.isArray(value?.sessions) ? value.sessions : [];
  } catch {
    return [];
  }
}

function writeWorkshopSessions(sessions) {
  const storePath = workshopSessionStorePath();
  fs.mkdirSync(path.dirname(storePath), { recursive: true });
  fs.writeFileSync(
    storePath,
    JSON.stringify({ version: 1, sessions: sessions.slice(0, 20) }, null, 2),
    "utf8"
  );
}

function saveWorkshopSession(session) {
  const sessions = readWorkshopSessions().filter((item) => item.id !== session.id);
  const next = { ...session, updatedAt: new Date().toISOString() };
  writeWorkshopSessions([next, ...sessions]);
  return next;
}

function updateWorkshopSession(sessionId, changes) {
  const current = readWorkshopSessions().find((item) => item.id === sessionId);
  if (!current) return null;
  return saveWorkshopSession({ ...current, ...changes, id: sessionId });
}

function latestWorkshopSession() {
  const session = readWorkshopSessions()[0] || null;
  if (session?.status === "building") return null;
  if (!session || session.status !== "running" || activeWorkshopRunForSession(session.id)) {
    return session;
  }
  return updateWorkshopSession(session.id, {
    status: "interrupted",
    error: "上次编排因应用退出而中断；设计输入和已产生的记录仍然保留，可以重新编排。"
  });
}

function activeWorkshopRunForSession(sessionId) {
  return [...activeWorkshopRuns.values()].find((item) => item.sessionId === sessionId) || null;
}

function normalizeWorkshopRuntimeEvent(payload) {
  const kind = String(payload?.kind || "message").toLowerCase();
  const action = String(payload?.action || "正在编排");
  const run = activeWorkshopRunForSession(payload?.sessionId);
  const rawAgent = String(payload?.agent || "Agent Creation Council");
  const declaredCouncilRoles = ["行业架构师", "工作流架构师", "信任审查官"];
  const directWorker = /^子 Agent \d+$/.test(rawAgent);
  const workerMatch = rawAgent.match(/^(子 Agent \d+)/);
  let agent = rawAgent;
  if (workerMatch) {
    const worker = workerMatch[1];
    if (directWorker
        && kind === "thinking"
        && ["行业架构师", "工作流架构师", "信任审查官"].includes(action)) {
      run?.roles?.set(worker, action);
    }
    agent = run?.roles?.get(worker) || worker;
  }
  if (directWorker && kind === "toolcompleted" && action.endsWith(" 完成")) {
    agent = action.slice(0, -3);
  } else if (directWorker && action.endsWith(" 失败")) {
    agent = action.slice(0, -3);
  } else if (!workerMatch && !declaredCouncilRoles.includes(rawAgent)) {
    agent = "编排委员会";
  }
  const failed = kind === "failed" || action.includes("失败");
  const done = kind === "completed" || (kind === "toolcompleted" && !failed);
  return {
    sessionId: String(payload?.sessionId || ""),
    agent,
    status: failed ? "failed" : done ? "done" : "running",
    detail: String(payload?.detail || action),
    output: done ? String(payload?.detail || "").slice(0, 4000) : "",
    at: new Date().toISOString()
  };
}

function acceptWorkshopRuntimeEvent(payload) {
  if (!payload?.sessionId) return;
  const event = normalizeWorkshopRuntimeEvent(payload);
  const session = readWorkshopSessions().find((item) => item.id === event.sessionId);
  if (session) {
    const events = Array.isArray(session.events) ? [...session.events] : [];
    const index = events.findIndex((item) => item.agent === event.agent);
    if (index >= 0) events[index] = event;
    else events.push(event);
    saveWorkshopSession({ ...session, events: events.slice(-16) });
  }
  if (mainWindow && !mainWindow.isDestroyed()) {
    mainWindow.webContents.send("nova:agent-workshop-event", event);
  }
}

function extensionProfilePath() {
  return path.join(app.getPath("userData"), "extension-profiles.json");
}

function gatewayActionRequestPath() {
  return path.join(app.getPath("userData"), "extension-gateway", "action-requests.json");
}

function gatewaySessionDescriptorPath() {
  return path.join(app.getPath("userData"), "extension-gateway", "session.json");
}

async function writeGatewaySessionDescriptor() {
  const target = gatewaySessionDescriptorPath();
  await fs.promises.mkdir(path.dirname(target), { recursive: true });
  await fs.promises.writeFile(target, JSON.stringify({
    schema: "nova.gateway-session/1.0",
    product: "NOVA AgentOS",
    pid: process.pid,
    baseUrl: `http://127.0.0.1:${extensionGateway.port}`,
    apiVersion: "v1",
    startedAt: extensionGateway.startedAt
  }, null, 2), { encoding: "utf8", mode: 0o600 });
}

async function removeGatewaySessionDescriptor() {
  const target = gatewaySessionDescriptorPath();
  try {
    const current = JSON.parse(await fs.promises.readFile(target, "utf8"));
    if (Number(current?.pid) !== process.pid) return;
    await fs.promises.unlink(target);
  } catch (error) {
    if (error?.code !== "ENOENT") console.error(`[Extension Gateway] ${safeError(error)}`);
  }
}

function readGatewayActionRequests() {
  try {
    const value = JSON.parse(fs.readFileSync(gatewayActionRequestPath(), "utf8"));
    return Array.isArray(value?.requests) ? value.requests.slice(0, 100) : [];
  } catch {
    return [];
  }
}

async function writeGatewayActionRequests(requests) {
  const target = gatewayActionRequestPath();
  const temporary = `${target}.tmp`;
  await fs.promises.mkdir(path.dirname(target), { recursive: true });
  await fs.promises.writeFile(
    temporary,
    JSON.stringify({ version: 1, requests: requests.slice(0, 100) }, null, 2),
    "utf8"
  );
  await fs.promises.rename(temporary, target);
}

function normalizeGatewayActionRequest(value, request) {
  const prompt = String(value?.prompt || value?.goal || "").trim();
  if (!prompt || prompt.length > 8000) {
    throw new Error("任务目标必须为 1 到 8000 个字符。");
  }
  const title = String(value?.title || prompt.split(/\r?\n/, 1)[0] || "外部任务请求")
    .trim()
    .slice(0, 120);
  const allowedModes = new Set(["Ask", "Plan", "Build", "Goal"]);
  const executionMode = allowedModes.has(value?.executionMode) ? value.executionMode : "Plan";
  const agentPackId = String(value?.agentPackId || "").trim();
  if (agentPackId && !/^[A-Za-z0-9._:-]{1,160}$/.test(agentPackId)) {
    throw new Error("Agent Pack ID 格式无效。");
  }
  return {
    id: `request-${crypto.randomUUID().replaceAll("-", "")}`,
    title: title || "外部任务请求",
    prompt,
    executionMode,
    agentPackId: agentPackId || null,
    source: String(value?.source || "本机扩展").trim().slice(0, 80) || "本机扩展",
    origin: String(request.headers.origin || "local-process").slice(0, 200),
    status: "pending",
    createdAt: new Date().toISOString(),
    resolvedAt: null
  };
}

function readGatewayJsonBody(request, maximumBytes = 65536) {
  return new Promise((resolve, reject) => {
    let body = "";
    let bytes = 0;
    request.setEncoding("utf8");
    request.on("data", (chunk) => {
      bytes += Buffer.byteLength(chunk);
      if (bytes > maximumBytes) {
        reject(new Error("请求体超过 64 KB 上限。"));
        request.destroy();
        return;
      }
      body += chunk;
    });
    request.on("end", () => {
      try {
        resolve(body ? JSON.parse(body) : {});
      } catch {
        reject(new Error("请求体不是有效 JSON。"));
      }
    });
    request.once("error", reject);
  });
}

async function createGatewayActionRequest(value, request) {
  const current = readGatewayActionRequests();
  const pending = current.filter((item) => item.status === "pending");
  if (pending.length >= 20) throw new Error("待审阅任务请求已达到 20 条上限。");
  const actionRequest = normalizeGatewayActionRequest(value, request);
  await writeGatewayActionRequests([actionRequest, ...current]);
  publishGatewayHook("action.requested", {
    requestId: actionRequest.id,
    title: actionRequest.title,
    source: actionRequest.source,
    executionMode: actionRequest.executionMode,
    agentPackId: actionRequest.agentPackId
  });
  if (mainWindow && !mainWindow.isDestroyed()) {
    mainWindow.webContents.send("nova:gateway-action-request", actionRequest);
  }
  return actionRequest;
}

async function resolveGatewayActionRequest(id, status) {
  if (!/^[A-Za-z0-9-]{1,160}$/.test(String(id || ""))) {
    throw new Error("任务请求 ID 无效。");
  }
  if (!["accepted", "rejected"].includes(status)) {
    throw new Error("任务请求处理状态无效。");
  }
  const requests = readGatewayActionRequests();
  const index = requests.findIndex((item) => item.id === id);
  if (index < 0) throw new Error("任务请求不存在或已被清理。");
  const resolved = {
    ...requests[index],
    status,
    resolvedAt: new Date().toISOString()
  };
  requests[index] = resolved;
  await writeGatewayActionRequests(requests);
  publishGatewayHook(`action.${status}`, {
    requestId: resolved.id,
    title: resolved.title,
    source: resolved.source
  });
  return resolved;
}

function readExtensionProfiles() {
  try {
    const value = JSON.parse(fs.readFileSync(extensionProfilePath(), "utf8"));
    return {
      ...value,
      ssh: Array.isArray(value?.ssh) ? value.ssh : [],
      cloud: Array.isArray(value?.cloud) ? value.cloud : [],
      gateway: { enabled: value?.gateway?.enabled === true }
    };
  } catch {
    return { ssh: [], cloud: [], gateway: { enabled: false } };
  }
}

async function writeExtensionProfiles(value) {
  const target = extensionProfilePath();
  const temporary = `${target}.tmp`;
  await fs.promises.mkdir(path.dirname(target), { recursive: true });
  await fs.promises.writeFile(temporary, JSON.stringify(value, null, 2), "utf8");
  await fs.promises.rename(temporary, target);
}

function gatewayStatus(includeToken = false) {
  const running = Boolean(extensionGateway.server?.listening);
  const baseUrl = running ? `http://127.0.0.1:${extensionGateway.port}` : "";
  return {
    enabled: readExtensionProfiles().gateway.enabled,
    running,
    host: "127.0.0.1",
    port: extensionGateway.port,
    baseUrl,
    eventsUrl: running ? `${baseUrl}/v1/events` : "",
    startedAt: extensionGateway.startedAt,
    hooks: extensionGatewayHooks,
    permissions: ["tasks.read", "artifacts.read", "events.read", "actions.request"],
    pendingActionRequests: readGatewayActionRequests().filter((item) => item.status === "pending").length,
    localOnly: true,
    token: includeToken ? extensionGateway.token : undefined
  };
}

function gatewayOriginAllowed(origin) {
  if (!origin) return true;
  try {
    const parsed = new URL(origin);
    return ["http:", "https:"].includes(parsed.protocol)
      && ["127.0.0.1", "localhost", "[::1]"].includes(parsed.hostname);
  } catch {
    return false;
  }
}

function gatewayWriteJson(response, statusCode, value, origin = "") {
  const headers = {
    "Content-Type": "application/json; charset=utf-8",
    "Cache-Control": "no-store",
    "X-Content-Type-Options": "nosniff",
    "Referrer-Policy": "no-referrer"
  };
  if (origin && gatewayOriginAllowed(origin)) {
    headers["Access-Control-Allow-Origin"] = origin;
    headers.Vary = "Origin";
  }
  response.writeHead(statusCode, headers);
  response.end(JSON.stringify(value));
}

function gatewayAuthorized(request, parsedUrl) {
  const authorization = String(request.headers.authorization || "");
  const bearer = authorization.startsWith("Bearer ") ? authorization.slice(7).trim() : "";
  const headerToken = String(request.headers["x-nova-token"] || "").trim();
  const queryToken = parsedUrl.searchParams.get("access_token") || "";
  const candidate = bearer || headerToken || queryToken;
  if (!candidate || !extensionGateway.token) return false;
  const expected = Buffer.from(extensionGateway.token);
  const actual = Buffer.from(candidate);
  return expected.length === actual.length && crypto.timingSafeEqual(expected, actual);
}

function sanitizeGatewayArtifact(artifact) {
  return {
    id: String(artifact?.id || ""),
    title: String(artifact?.title || artifact?.relativePath || "交付物"),
    relativePath: String(artifact?.relativePath || ""),
    kind: String(artifact?.kind || "file"),
    mediaType: String(artifact?.mediaType || "application/octet-stream"),
    size: Number(artifact?.size || 0),
    modifiedAt: artifact?.modifiedAt || null,
    role: String(artifact?.role || "supporting"),
    previewable: Boolean(artifact?.previewable)
  };
}

function sanitizeGatewayTask(task, includeDelivery = false) {
  const source = task?.task || task || {};
  const delivery = task?.delivery || source.delivery || null;
  const result = {
    id: String(source.id || source.taskId || ""),
    title: String(source.title || "NOVA 任务"),
    state: String(source.state || source.status || "unknown"),
    status: String(source.status || source.state || "unknown"),
    progress: Number(source.progress || 0),
    provider: String(source.provider || ""),
    model: String(source.model || ""),
    agentPackId: source.agentPackId || null,
    executionMode: String(source.executionMode || source.mode || ""),
    createdAt: source.createdAt || null,
    updatedAt: source.updatedAt || null,
    hasResult: Boolean(source.hasResult || delivery)
  };
  if (includeDelivery && delivery) {
    result.delivery = {
      deliveryId: String(delivery.deliveryId || ""),
      revision: Number(delivery.revision || 1),
      status: String(delivery.status || "READY"),
      title: String(delivery.title || result.title),
      outcome: String(delivery.outcome || delivery.summary || ""),
      reviewState: String(delivery.reviewState || "unreviewed"),
      artifacts: Array.isArray(delivery.artifacts)
        ? delivery.artifacts.map(sanitizeGatewayArtifact)
        : [],
      evidence: Array.isArray(delivery.evidence) ? delivery.evidence.map(String) : [],
      incomplete: Array.isArray(delivery.incomplete) ? delivery.incomplete.map(String) : [],
      nextActions: Array.isArray(delivery.nextActions) ? delivery.nextActions.map(String) : []
    };
  }
  return result;
}

function sanitizeGatewayTaskCapsule(capsule) {
  if (!capsule || capsule.status === "not-compiled") return capsule;
  return {
    schema: String(capsule.schema || "nova.task-capsule/1.0"),
    taskId: String(capsule.taskId || ""),
    goal: String(capsule.goal || ""),
    executionMode: String(capsule.executionMode || ""),
    characterBudget: Number(capsule.characterBudget || 0),
    usedCharacters: Number(capsule.usedCharacters || 0),
    estimatedPromptTokens: Number(capsule.estimatedPromptTokens || 0),
    estimatedRawCharacters: Number(capsule.estimatedRawCharacters || 0),
    estimatedCharactersAvoided: Number(capsule.estimatedCharactersAvoided || 0),
    estimatedTokensAvoided: Number(capsule.estimatedTokensAvoided || 0),
    fingerprint: String(capsule.fingerprint || ""),
    contextCacheHit: capsule.contextCacheHit === true,
    contextSourceFingerprint: String(capsule.contextSourceFingerprint || ""),
    layers: Array.isArray(capsule.layers) ? capsule.layers : [],
    selections: Array.isArray(capsule.selections)
      ? capsule.selections.map((item) => ({
        relativePath: String(item.relativePath || ""),
        score: Number(item.score || 0),
        reasons: Array.isArray(item.reasons) ? item.reasons.map(String) : [],
        startLine: Number(item.startLine || 0),
        endLine: Number(item.endLine || 0),
        includedCharacters: Number(item.includedCharacters || 0)
      }))
      : [],
    exclusions: Array.isArray(capsule.exclusions) ? capsule.exclusions.map(String) : [],
    compiledAt: capsule.compiledAt || null
  };
}

function publishGatewayHook(type, payload = {}) {
  if (!extensionGateway.server?.listening) return null;
  const event = {
    schema: "nova.hook/1.0",
    id: `hook-${Date.now().toString(36)}-${++extensionGateway.sequence}`,
    sequence: extensionGateway.sequence,
    type,
    occurredAt: new Date().toISOString(),
    taskId: String(payload.taskId || ""),
    payload
  };
  extensionGateway.events.push(event);
  if (extensionGateway.events.length > 200) extensionGateway.events.shift();
  const frame = `event: ${type}\nid: ${event.id}\ndata: ${JSON.stringify(event)}\n\n`;
  for (const client of [...extensionGateway.clients]) {
    try {
      client.write(frame);
    } catch {
      extensionGateway.clients.delete(client);
    }
  }
  return event;
}

async function handleGatewayRequest(request, response) {
  const origin = String(request.headers.origin || "");
  if (!gatewayOriginAllowed(origin)) {
    gatewayWriteJson(response, 403, { error: "origin_not_allowed" });
    return;
  }
  const parsedUrl = new URL(request.url || "/", "http://127.0.0.1");
  if (request.method === "OPTIONS") {
    response.writeHead(204, {
      "Access-Control-Allow-Origin": origin,
      "Access-Control-Allow-Headers": "Authorization, X-Nova-Token, Content-Type",
      "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
      "Access-Control-Max-Age": "600",
      Vary: "Origin"
    });
    response.end();
    return;
  }
  if (!gatewayAuthorized(request, parsedUrl)) {
    gatewayWriteJson(response, 401, { error: "invalid_access_token" }, origin);
    return;
  }

  try {
    if (request.method === "POST" && parsedUrl.pathname === "/v1/action-requests") {
      const body = await readGatewayJsonBody(request);
      const actionRequest = await createGatewayActionRequest(body, request);
      gatewayWriteJson(response, 202, { request: actionRequest }, origin);
      return;
    }
    if (request.method !== "GET") {
      gatewayWriteJson(response, 405, { error: "write_api_not_enabled" }, origin);
      return;
    }
    if (parsedUrl.pathname === "/" || parsedUrl.pathname === "/v1/health") {
      gatewayWriteJson(response, 200, {
        product: "NOVA AgentOS Extension Gateway",
        version: "1.0",
        status: "ready",
        ...gatewayStatus(false)
      }, origin);
      return;
    }
    if (parsedUrl.pathname === "/v1/manifest") {
      gatewayWriteJson(response, 200, {
        schema: "nova.extension-gateway/1.0",
        transport: ["http", "sse"],
        permissions: gatewayStatus(false).permissions,
        hooks: extensionGatewayHooks,
        routes: [
          "GET /v1/health",
          "GET /v1/tasks",
          "GET /v1/tasks/{taskId}",
          "GET /v1/tasks/{taskId}/artifacts",
          "GET /v1/tasks/{taskId}/context",
          "GET /v1/budget",
          "GET /v1/events",
          "GET /v1/action-requests",
          "POST /v1/action-requests"
        ]
      }, origin);
      return;
    }
    if (parsedUrl.pathname === "/v1/tasks") {
      const result = await bridge.call("list_tasks");
      const tasks = Array.isArray(result) ? result : Array.isArray(result?.tasks) ? result.tasks : [];
      gatewayWriteJson(response, 200, { tasks: tasks.map((task) => sanitizeGatewayTask(task)) }, origin);
      return;
    }
    if (parsedUrl.pathname === "/v1/action-requests") {
      gatewayWriteJson(response, 200, {
        requests: readGatewayActionRequests().filter((item) => item.status === "pending")
      }, origin);
      return;
    }
    if (parsedUrl.pathname === "/v1/budget") {
      const mode = parsedUrl.searchParams.get("mode") || "Build";
      const taskId = parsedUrl.searchParams.get("taskId") || "";
      const characters = Number(parsedUrl.searchParams.get("characters") || 0);
      if (!/^(Ask|Plan|Build|Autopilot|Goal)$/i.test(mode)
          || !Number.isInteger(characters)
          || characters < 0
          || characters > 20000000
          || (taskId && !/^[A-Za-z0-9._:-]{1,160}$/.test(taskId))) {
        gatewayWriteJson(response, 400, { error: "invalid_budget_query" }, origin);
        return;
      }
      const budget = await bridge.call("get_context_budget", { mode, characters, taskId: taskId || null });
      if (budget?.capsule) budget.capsule = sanitizeGatewayTaskCapsule(budget.capsule);
      gatewayWriteJson(response, 200, budget, origin);
      return;
    }
    const taskMatch = parsedUrl.pathname.match(/^\/v1\/tasks\/([^/]+)$/);
    const artifactMatch = parsedUrl.pathname.match(/^\/v1\/tasks\/([^/]+)\/artifacts$/);
    const contextMatch = parsedUrl.pathname.match(/^\/v1\/tasks\/([^/]+)\/context$/);
    if (taskMatch || artifactMatch || contextMatch) {
      const taskId = decodeURIComponent((artifactMatch || contextMatch || taskMatch)[1]);
      if (!/^[A-Za-z0-9._:-]{1,160}$/.test(taskId)) {
        gatewayWriteJson(response, 400, { error: "invalid_task_id" }, origin);
        return;
      }
      if (contextMatch) {
        const capsule = await bridge.call("get_task_capsule", { taskId });
        gatewayWriteJson(response, 200, sanitizeGatewayTaskCapsule(capsule), origin);
        return;
      }
      const task = await bridge.call("get_task", { taskId });
      if (artifactMatch) {
        const delivery = task?.delivery || task?.task?.delivery || null;
        const artifacts = Array.isArray(delivery?.artifacts)
          ? delivery.artifacts.map(sanitizeGatewayArtifact)
          : [];
        gatewayWriteJson(response, 200, { taskId, artifacts }, origin);
      } else {
        gatewayWriteJson(response, 200, sanitizeGatewayTask(task, true), origin);
      }
      return;
    }
    if (parsedUrl.pathname === "/v1/events") {
      const headers = {
        "Content-Type": "text/event-stream; charset=utf-8",
        "Cache-Control": "no-cache, no-transform",
        Connection: "keep-alive",
        "X-Accel-Buffering": "no"
      };
      if (origin) {
        headers["Access-Control-Allow-Origin"] = origin;
        headers.Vary = "Origin";
      }
      response.writeHead(200, headers);
      response.write(": NOVA Extension Gateway\n\n");
      extensionGateway.clients.add(response);
      const lastEventId = String(request.headers["last-event-id"] || "");
      const replay = lastEventId
        ? extensionGateway.events.slice(Math.max(0, extensionGateway.events.findIndex((item) => item.id === lastEventId) + 1))
        : extensionGateway.events.slice(-20);
      for (const event of replay) {
        response.write(`event: ${event.type}\nid: ${event.id}\ndata: ${JSON.stringify(event)}\n\n`);
      }
      request.on("close", () => extensionGateway.clients.delete(response));
      return;
    }
    gatewayWriteJson(response, 404, { error: "route_not_found" }, origin);
  } catch (error) {
    gatewayWriteJson(response, 502, { error: "agentos_bridge_error", message: safeError(error) }, origin);
  }
}

async function startExtensionGateway(force = false) {
  if (extensionGateway.server?.listening || (!force && !readExtensionProfiles().gateway.enabled)) {
    return gatewayStatus(true);
  }
  extensionGateway.token = crypto.randomBytes(32).toString("base64url");
  const server = http.createServer((request, response) => {
    void handleGatewayRequest(request, response);
  });
  extensionGateway.server = server;
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  extensionGateway.port = Number(server.address()?.port || 0);
  extensionGateway.startedAt = new Date().toISOString();
  await writeGatewaySessionDescriptor();
  extensionGateway.keepAlive = setInterval(() => {
    for (const client of [...extensionGateway.clients]) {
      try {
        client.write(`: keepalive ${Date.now()}\n\n`);
      } catch {
        extensionGateway.clients.delete(client);
      }
    }
  }, 15000);
  extensionGateway.keepAlive.unref?.();
  return gatewayStatus(true);
}

function requestGatewayForSmoke(pathname, method = "GET", payload = null) {
  return new Promise((resolve, reject) => {
    const body = payload ? JSON.stringify(payload) : "";
    const request = http.request({
      host: "127.0.0.1",
      port: extensionGateway.port,
      path: pathname,
      method,
      headers: {
        Authorization: `Bearer ${extensionGateway.token}`,
        ...(body ? {
          "Content-Type": "application/json",
          "Content-Length": Buffer.byteLength(body)
        } : {})
      }
    }, (response) => {
      let body = "";
      response.setEncoding("utf8");
      response.on("data", (chunk) => {
        body = `${body}${chunk}`.slice(0, 1000000);
      });
      response.on("end", () => resolve({ status: response.statusCode, body }));
    });
    request.setTimeout(5000, () => request.destroy(new Error("Gateway smoke timeout.")));
    request.once("error", reject);
    if (body) request.write(body);
    request.end();
  });
}

async function smokeExtensionGateway(includeBridge = true) {
  if (!extensionGateway.server?.listening) throw new Error("Extension Gateway did not start.");
  const sessionDescriptor = JSON.parse(await fs.promises.readFile(gatewaySessionDescriptorPath(), "utf8"));
  const health = await requestGatewayForSmoke("/v1/health");
  const manifest = await requestGatewayForSmoke("/v1/manifest");
  const deniedWrite = await requestGatewayForSmoke("/v1/tasks", "POST");
  const unauthorized = await new Promise((resolve, reject) => {
    const request = http.request({
      host: "127.0.0.1",
      port: extensionGateway.port,
      path: "/v1/tasks",
      method: "GET"
    }, (response) => {
      response.resume();
      response.on("end", () => resolve(response.statusCode));
    });
    request.once("error", reject);
    request.end();
  });
  let tasks = { status: 200, body: "" };
  if (includeBridge) tasks = await requestGatewayForSmoke("/v1/tasks");
  let actionRequest = { status: 202, body: "{}" };
  let actionRequests = { status: 200, body: "{\"requests\":[]}" };
  if (!includeBridge) {
    actionRequest = await requestGatewayForSmoke("/v1/action-requests", "POST", {
      source: "Gateway Smoke",
      title: "验证外部任务请求",
      prompt: "生成一份只读接入检查清单",
      executionMode: "Plan"
    });
    actionRequests = await requestGatewayForSmoke("/v1/action-requests");
    const created = JSON.parse(actionRequest.body)?.request;
    if (created?.id) await resolveGatewayActionRequest(created.id, "accepted");
  }
  if (health.status !== 200
      || manifest.status !== 200
      || !manifest.body.includes("GET /v1/tasks/{taskId}/context")
      || !manifest.body.includes("GET /v1/budget")
      || !manifest.body.includes("context.compiled")
      || tasks.status !== 200
      || actionRequest.status !== 202
      || actionRequests.status !== 200
      || (!includeBridge && !actionRequests.body.includes("验证外部任务请求"))
      || deniedWrite.status !== 405
      || unauthorized !== 401
      || sessionDescriptor.baseUrl !== `http://127.0.0.1:${extensionGateway.port}`
      || Object.prototype.hasOwnProperty.call(sessionDescriptor, "token")
      || /workspaceRoot|[A-Za-z]:\\\\/.test(tasks.body)) {
    throw new Error("Extension Gateway security contract failed.");
  }
}

async function stopExtensionGateway() {
  if (extensionGateway.keepAlive) clearInterval(extensionGateway.keepAlive);
  extensionGateway.keepAlive = null;
  for (const client of extensionGateway.clients) client.end();
  extensionGateway.clients.clear();
  const server = extensionGateway.server;
  extensionGateway.server = null;
  extensionGateway.port = 0;
  extensionGateway.startedAt = null;
  extensionGateway.token = "";
  await removeGatewaySessionDescriptor();
  if (server) {
    await new Promise((resolve) => server.close(() => resolve()));
  }
}

function normalizeSshProfile(value) {
  const host = String(value?.host || "").trim();
  const username = String(value?.username || "").trim();
  const port = Number(value?.port || 22);
  if (!/^[A-Za-z0-9.-]{1,253}$/.test(host)) throw new Error("SSH 主机格式无效。");
  if (!/^[A-Za-z0-9._-]{1,64}$/.test(username)) throw new Error("SSH 用户名格式无效。");
  if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error("SSH 端口无效。");
  const authentication = value?.authentication === "key" ? "key" : "agent";
  const keyPath = authentication === "key" ? String(value?.keyPath || "").trim() : "";
  if (keyPath && !fs.existsSync(keyPath)) throw new Error("SSH 私钥文件不存在。");
  return {
    id: String(value?.id || crypto.randomUUID()),
    name: String(value?.name || `${username}@${host}`).trim().slice(0, 80),
    host,
    port,
    username,
    authentication,
    keyPath,
    remoteRoot: String(value?.remoteRoot || "").trim().slice(0, 500),
    updatedAt: new Date().toISOString()
  };
}

function normalizeCloudAdapter(value) {
  const allowed = new Set([
    "generic",
    "github-codespaces",
    "aliyun-devstudio",
    "tencent-cloud"
  ]);
  const provider = String(value?.provider || "generic");
  if (!allowed.has(provider)) throw new Error("不支持的云开发适配器。");
  const project = String(value?.project || "").trim();
  if (!project || project.length > 200) throw new Error("项目或工作区标识无效。");
  return {
    id: String(value?.id || crypto.randomUUID()),
    provider,
    project,
    region: String(value?.region || "").trim().slice(0, 100),
    updatedAt: new Date().toISOString()
  };
}

function testSsh(profile) {
  const normalized = normalizeSshProfile(profile);
  return new Promise((resolve, reject) => {
    const args = [
      "-o",
      "BatchMode=yes",
      "-o",
      "ConnectTimeout=8",
      "-p",
      String(normalized.port)
    ];
    if (normalized.keyPath) args.push("-i", normalized.keyPath);
    args.push(`${normalized.username}@${normalized.host}`, "exit");
    const process = spawn("ssh", args, { windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
    let errorText = "";
    process.stderr.on("data", (buffer) => {
      errorText = `${errorText}${buffer.toString()}`.slice(-2000);
    });
    const timeout = setTimeout(() => process.kill(), 12000);
    process.once("error", (error) => {
      clearTimeout(timeout);
      reject(new Error(`无法启动系统 SSH：${error.message}`));
    });
    process.once("exit", (code) => {
      clearTimeout(timeout);
      if (code === 0) resolve({ reachable: true });
      else reject(new Error(errorText.trim() || `SSH 连接测试失败（${code}）。`));
    });
  });
}

function senderWindow(event) {
  const window = BrowserWindow.fromWebContents(event.sender);
  if (!window || window !== mainWindow) throw new Error("无效窗口调用。");
  return window;
}

function knowledgeSenderWindow(event) {
  const window = BrowserWindow.fromWebContents(event.sender);
  const isMain = window && window === mainWindow;
  const isKnowledge = window
    && knowledgeWindow
    && !knowledgeWindow.isDestroyed()
    && window === knowledgeWindow;
  if (!isMain && !isKnowledge) throw new Error("无效知识窗口调用。");
  return window;
}

function registerWindowChannel(channel, action) {
  ipcMain.handle(channel, (event) => action(senderWindow(event)));
}

function modelDefaults(provider) {
  if (provider === "openai") {
    return { model: "gpt-5.6", endpoint: "https://api.openai.com/v1/responses" };
  }
  if (provider === "kimi") {
    return {
      model: "kimi-k3",
      endpoint: "https://api.moonshot.cn/v1/chat/completions"
    };
  }
  if (provider === "ollama") {
    return {
      model: "gpt-oss:20b",
      endpoint: "http://localhost:11434/api/chat"
    };
  }
  if (provider === "custom") {
    return {
      model: "custom-model",
      endpoint: ""
    };
  }
  return {
    model: "deepseek-v4-flash",
    endpoint: "https://api.deepseek.com/chat/completions"
  };
}

function modelConnectionStorePath() {
  return path.join(app.getPath("userData"), "secure-model-connections.json");
}

function publicModelConnections() {
  return Array.from(modelConnections.values()).map((connection) => ({
    provider: connection.provider,
    model: connection.model,
    endpoint: connection.endpoint,
    connected: true,
    hasCredential: Boolean(connection.apiKey)
  }));
}

function persistModelConnections() {
  const records = [];
  for (const connection of modelConnections.values()) {
    let protectedApiKey = "";
    if (connection.apiKey) {
      if (!safeStorage.isEncryptionAvailable()) {
        return {
          persisted: false,
          warning: "系统安全存储暂不可用；本次连接有效，但重启后需要重新输入 API Key。"
        };
      }
      protectedApiKey = safeStorage.encryptString(connection.apiKey).toString("base64");
    }
    records.push({
      provider: connection.provider,
      model: connection.model,
      endpoint: connection.endpoint,
      protectedApiKey
    });
  }

  const storePath = modelConnectionStorePath();
  const temporaryPath = `${storePath}.${process.pid}.tmp`;
  fs.mkdirSync(path.dirname(storePath), { recursive: true });
  try {
    fs.writeFileSync(temporaryPath, JSON.stringify({
      version: MODEL_CONNECTION_STORE_VERSION,
      connections: records
    }), { encoding: "utf8", mode: 0o600 });
    fs.copyFileSync(temporaryPath, storePath);
    try {
      fs.chmodSync(storePath, 0o600);
    } catch {
      // Windows protects the encrypted value with DPAPI; POSIX mode hardening is best effort.
    }
    return { persisted: true, warning: "" };
  } finally {
    if (fs.existsSync(temporaryPath)) {
      try { fs.unlinkSync(temporaryPath); } catch { /* best-effort cleanup */ }
    }
  }
}

function restoreModelConnections() {
  const storePath = modelConnectionStorePath();
  if (!fs.existsSync(storePath)) return [];
  let payload;
  try {
    payload = JSON.parse(fs.readFileSync(storePath, "utf8"));
  } catch (error) {
    console.error(`[Model Credentials] 配置读取失败：${safeError(error)}`);
    return [];
  }
  if (Number(payload?.version) !== MODEL_CONNECTION_STORE_VERSION
      || !Array.isArray(payload?.connections)) return [];

  for (const record of payload.connections) {
    try {
      const provider = String(record?.provider || "");
      validateProvider(provider);
      let apiKey = "";
      if (record?.protectedApiKey) {
        if (!safeStorage.isEncryptionAvailable()) continue;
        apiKey = safeStorage.decryptString(Buffer.from(String(record.protectedApiKey), "base64"));
      }
      const normalized = normalizeModelConfiguration({
        provider,
        model: record?.model,
        endpoint: record?.endpoint,
        apiKey
      });
      modelConnections.set(provider, normalized);
    } catch (error) {
      console.error(`[Model Credentials] 跳过无法恢复的连接：${safeError(error)}`);
    }
  }
  return publicModelConnections();
}

function validateProvider(provider) {
  if (!["openai", "deepseek", "kimi", "ollama", "custom"].includes(provider)) {
    throw new Error("不支持的模型提供方。");
  }
}

function isPrivateModelHost(hostname) {
  const host = hostname.toLowerCase().replace(/^\[|\]$/g, "");
  return (
    host === "localhost" ||
    host === "::1" ||
    host === "127.0.0.1" ||
    host.startsWith("127.") ||
    host.startsWith("10.") ||
    host.startsWith("192.168.") ||
    /^172\.(1[6-9]|2\d|3[01])\./.test(host)
  );
}

function normalizeCompatibleEndpoint(provider, rawValue) {
  let raw = String(rawValue || modelDefaults(provider).endpoint || "").trim();
  if (!raw) throw new Error("请填写模型 API 地址。");
  if (!/^[a-z][a-z0-9+.-]*:\/\//i.test(raw)) {
    raw = /^(localhost|127\.|10\.|192\.168\.|172\.)/i.test(raw)
      ? `http://${raw}`
      : `https://${raw}`;
  }
  const endpoint = new URL(raw);
  if (!["http:", "https:"].includes(endpoint.protocol)) {
    throw new Error("模型 API 只支持 HTTP 或 HTTPS。");
  }
  if (endpoint.username || endpoint.password || endpoint.search || endpoint.hash) {
    throw new Error("模型 API 地址不能包含账号、密码、查询参数或锚点。");
  }
  if (endpoint.protocol === "http:" && !isPrivateModelHost(endpoint.hostname)) {
    throw new Error("远程自定义模型必须使用 HTTPS；HTTP 仅允许本机或局域网地址。");
  }

  let pathname = endpoint.pathname.replace(/\/+$/, "");
  if (provider === "ollama" && /\/api\/chat$/i.test(pathname)) {
    endpoint.pathname = pathname;
    return endpoint.toString();
  }
  if (provider === "ollama" && (!pathname || pathname === "/")) {
    endpoint.pathname = "/api/chat";
    return endpoint.toString();
  }
  if (provider === "ollama" && /\/api$/i.test(pathname)) {
    endpoint.pathname = `${pathname}/chat`;
    return endpoint.toString();
  }
  if (!/\/chat\/completions$/i.test(pathname)) {
    pathname = pathname && pathname !== "/"
      ? /\/v1$/i.test(pathname)
        ? `${pathname}/chat/completions`
        : `${pathname}/v1/chat/completions`
      : "/v1/chat/completions";
  }
  endpoint.pathname = pathname;
  return endpoint.toString();
}

function normalizeModelConfiguration(value) {
  const provider = String(value?.provider || "");
  validateProvider(provider);
  const model = String(value?.model || modelDefaults(provider).model).trim().slice(0, 160);
  if (!model) throw new Error("模型 ID 不能为空。");
  const apiKey = String(value?.apiKey || "").trim();
  const isCompatible = provider === "ollama" || provider === "custom";
  if (!isCompatible && apiKey.length < 12) throw new Error("API Key 格式无效。");
  if (apiKey && apiKey.length < 4) throw new Error("API Key 格式无效。");
  return {
    provider,
    model,
    apiKey,
    endpoint: isCompatible
      ? normalizeCompatibleEndpoint(provider, value?.endpoint)
      : modelDefaults(provider).endpoint
  };
}

function modelsEndpoint(configuration) {
  if (configuration.provider === "ollama") {
    const endpoint = new URL(configuration.endpoint);
    endpoint.pathname = /\/api\/chat$/i.test(endpoint.pathname)
      ? endpoint.pathname.replace(/\/api\/chat$/i, "/api/tags")
      : "/api/tags";
    return endpoint;
  }
  if (configuration.provider === "openai") return new URL("https://api.openai.com/v1/models");
  if (configuration.provider === "deepseek") return new URL("https://api.deepseek.com/models");
  if (configuration.provider === "kimi") return new URL("https://api.moonshot.cn/v1/models");
  const endpoint = new URL(configuration.endpoint);
  endpoint.pathname = endpoint.pathname.replace(/\/chat\/completions$/i, "/models");
  return endpoint;
}

async function probeModelConnection(configuration) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 10000);
  try {
    const headers = { Accept: "application/json" };
    if (configuration.apiKey) headers.Authorization = `Bearer ${configuration.apiKey}`;
    const response = await fetch(modelsEndpoint(configuration), {
      method: "GET",
      headers,
      signal: controller.signal
    });
    const text = await response.text();
    let data = {};
    try {
      data = text ? JSON.parse(text) : {};
    } catch {
      data = {};
    }
    if (!response.ok && configuration.provider === "custom" && [404, 405].includes(response.status)) {
      return [];
    }
    if (!response.ok) {
      throw new Error(
        data?.error?.message ||
        data?.message ||
        `模型接口探测失败（HTTP ${response.status}）`
      );
    }
    const candidates = configuration.provider === "ollama"
      ? data?.models?.map((item) => item?.name || item?.model)
      : data?.data?.map((item) => item?.id);
    return [...new Set((candidates || []).filter(Boolean).map(String))].slice(0, 80);
  } catch (error) {
    if (error?.name === "AbortError") throw new Error("模型接口连接超时。");
    throw error;
  } finally {
    clearTimeout(timeout);
  }
}

function contentTypeFor(filePath) {
  const extension = path.extname(filePath).toLowerCase();
  return (
    {
      ".png": "image/png",
      ".jpg": "image/jpeg",
      ".jpeg": "image/jpeg",
      ".webp": "image/webp"
    }[extension] || null
  );
}

function documentTypeFor(filePath) {
  const extension = path.extname(filePath).toLowerCase();
  return (
    {
      ".pdf": "application/pdf",
      ".doc": "application/msword",
      ".docx": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
      ".docm": "application/vnd.ms-word.document.macroEnabled.12",
      ".dotx": "application/vnd.openxmlformats-officedocument.wordprocessingml.template",
      ".dotm": "application/vnd.ms-word.template.macroEnabled.12"
    }[extension] || null
  );
}

function readApprovedAttachments(attachments = []) {
  let totalBytes = 0;
  return attachments.map((item) => {
    const attachmentPath = item?.path ? path.resolve(item.path) : "";
    if (!attachmentPath || !approvedAttachments.has(attachmentPath)) {
      throw new Error("附件未通过本次系统选择授权。");
    }
    const stat = fs.statSync(attachmentPath);
    if (!stat.isFile()) throw new Error("附件不是有效文件。");
    totalBytes += stat.size;
    if (totalBytes > 20 * 1024 * 1024) throw new Error("附件总大小不能超过 20 MB。");

    const documentMime = documentTypeFor(attachmentPath);
    if (documentMime) {
      if (stat.size > 12 * 1024 * 1024) {
        throw new Error("单个 PDF 或 Word 文档不能超过 12 MB。");
      }
      return {
        id: item.id,
        name: path.basename(attachmentPath),
        path: attachmentPath,
        kind: "document",
        mime: documentMime
      };
    }

    const mime = contentTypeFor(attachmentPath);
    if (mime) {
      if (stat.size > 10 * 1024 * 1024) throw new Error("单张图片不能超过 10 MB。");
      return {
        id: item.id,
        name: path.basename(attachmentPath),
        path: attachmentPath,
        kind: "image",
        mime,
        data: fs.readFileSync(attachmentPath).toString("base64")
      };
    }
    if (stat.size > 1024 * 1024) throw new Error("单个文本附件不能超过 1 MB。");
    return {
      id: item.id,
      name: path.basename(attachmentPath),
      path: attachmentPath,
      kind: "text",
      text: fs.readFileSync(attachmentPath, "utf8")
    };
  });
}

function normalizeMessages(messages) {
  if (!Array.isArray(messages) || messages.length === 0) {
    throw new Error("任务内容为空。");
  }
  return messages
    .slice(-30)
    .map((message) => ({
      role: message.role === "assistant" ? "assistant" : "user",
      content: String(message.content || "").slice(0, 60000)
    }))
    .filter((message) => message.content.trim());
}

function workspaceContract(workspace) {
  return [
    "你是 NOVA AgentOS 的工程执行智能体。",
    "以用户给出的结果为目标，先理解上下文，再给出清晰、具体、可验证的结果。",
    "不得声称已经修改、运行或验证本地文件，除非工具证据明确证明。",
    workspace ? `当前工作区：${workspace}` : "当前未选择工作区。",
    "回答使用用户使用的语言，优先给出结果和下一步。"
  ].join("\n");
}

async function callOpenAI({ apiKey, model, messages, attachments, workspace }) {
  const latest = messages[messages.length - 1];
  const input = messages.slice(0, -1).map((message) => ({
    role: message.role,
    content: [{ type: "input_text", text: message.content }]
  }));
  const latestContent = [{ type: "input_text", text: latest.content }];
  for (const attachment of attachments) {
    if (attachment.kind === "image") {
      latestContent.push({
        type: "input_image",
        image_url: `data:${attachment.mime};base64,${attachment.data}`
      });
    } else {
      latestContent.push({
        type: "input_text",
        text: `\n附件 ${attachment.name}：\n${attachment.text}`
      });
    }
  }
  input.push({ role: latest.role, content: latestContent });

  const response = await fetch("https://api.openai.com/v1/responses", {
    method: "POST",
    headers: {
      Authorization: `Bearer ${apiKey}`,
      "Content-Type": "application/json"
    },
    body: JSON.stringify({
      model,
      instructions: workspaceContract(workspace),
      input
    })
  });
  const data = await response.json();
  if (!response.ok) throw new Error(data?.error?.message || `OpenAI HTTP ${response.status}`);
  const output =
    data.output_text ||
    data.output
      ?.flatMap((item) => item.content || [])
      .filter((item) => item.type === "output_text")
      .map((item) => item.text)
      .join("\n");
  if (!output) throw new Error("OpenAI 未返回可显示文本。");
  return output;
}

async function callChatCompletions({
  provider,
  apiKey,
  model,
  messages,
  attachments,
  workspace
}) {
  if (provider === "deepseek" && attachments.some((item) => item.kind === "image")) {
    throw new Error("当前 DeepSeek 对话入口不接收图片，请切换 Kimi 或 OpenAI。");
  }

  const defaults = modelDefaults(provider);
  const payloadMessages = [
    { role: "system", content: workspaceContract(workspace) },
    ...messages.slice(0, -1)
  ];
  const latest = messages[messages.length - 1];
  const textAttachments = attachments
    .filter((item) => item.kind === "text")
    .map((item) => `\n附件 ${item.name}：\n${item.text}`)
    .join("\n");
  const images = attachments.filter((item) => item.kind === "image");

  if (images.length) {
    payloadMessages.push({
      role: latest.role,
      content: [
        { type: "text", text: `${latest.content}${textAttachments}` },
        ...images.map((item) => ({
          type: "image_url",
          image_url: { url: `data:${item.mime};base64,${item.data}` }
        }))
      ]
    });
  } else {
    payloadMessages.push({
      role: latest.role,
      content: `${latest.content}${textAttachments}`
    });
  }

  const response = await fetch(defaults.endpoint, {
    method: "POST",
    headers: {
      Authorization: `Bearer ${apiKey}`,
      "Content-Type": "application/json"
    },
    body: JSON.stringify({ model, messages: payloadMessages, stream: false })
  });
  const data = await response.json();
  if (!response.ok) {
    throw new Error(data?.error?.message || `${provider} HTTP ${response.status}`);
  }
  const output = data?.choices?.[0]?.message?.content;
  if (!output) throw new Error(`${provider} 未返回可显示文本。`);
  return output;
}

function balancedJsonObjects(source) {
  const values = [];
  let start = -1;
  let depth = 0;
  let inString = false;
  let escaped = false;
  for (let index = 0; index < source.length; index += 1) {
    const character = source[index];
    if (inString) {
      if (escaped) escaped = false;
      else if (character === "\\") escaped = true;
      else if (character === '"') inString = false;
      continue;
    }
    if (character === '"') {
      inString = true;
      continue;
    }
    if (character === "{") {
      if (depth === 0) start = index;
      depth += 1;
    } else if (character === "}" && depth > 0) {
      depth -= 1;
      if (depth === 0 && start >= 0) {
        values.push(source.slice(start, index + 1));
        start = -1;
      }
    }
  }
  return values;
}

function escapeJsonStringControls(source) {
  let output = "";
  let inString = false;
  let escaped = false;
  for (const character of source) {
    if (!inString) {
      output += character;
      if (character === '"') inString = true;
      continue;
    }
    if (escaped) {
      output += character;
      escaped = false;
    } else if (character === "\\") {
      output += character;
      escaped = true;
    } else if (character === '"') {
      output += character;
      inString = false;
    } else if (character === "\n") output += "\\n";
    else if (character === "\r") output += "\\r";
    else if (character === "\t") output += "\\t";
    else output += character;
  }
  return output;
}

function extractJsonObject(text) {
  const source = String(text || "").replace(/^\uFEFF/, "").trim();
  const candidates = [];
  for (const match of source.matchAll(/```(?:json)?\s*([\s\S]*?)```/gi)) {
    if (match[1]?.trim()) candidates.push(match[1].trim());
  }
  candidates.push(...balancedJsonObjects(source));
  if (source.startsWith("{") && source.endsWith("}")) candidates.push(source);
  if (!candidates.length) throw new Error("编排审查官没有返回完整的结构化草案。");

  const unique = [...new Set(candidates)].reverse();
  for (const candidate of unique) {
    const variants = [
      candidate,
      candidate.replace(/,\s*([}\]])/g, "$1"),
      escapeJsonStringControls(candidate).replace(/,\s*([}\]])/g, "$1")
    ];
    for (const variant of [...new Set(variants)]) {
      try {
        return JSON.parse(variant);
      } catch {
        // Try the next bounded syntax normalization before asking the model to repair.
      }
    }
  }
  throw new Error("编排审查官返回的草案存在 JSON 语法错误。");
}

function boundedStrings(value, limit, length = 240) {
  return Array.isArray(value)
    ? [...new Set(value.map((item) => String(item || "").trim().slice(0, length)).filter(Boolean))]
        .slice(0, limit)
    : [];
}

function normalizeWorkshopDraft(value, request, connection) {
  const roles = Array.isArray(value?.roles)
    ? value.roles.slice(0, 8).map((role, index) => ({
        id: String(role?.id || `specialist-${index + 1}`)
          .toLowerCase()
          .replace(/[^a-z0-9-]/g, "-")
          .replace(/^-+|-+$/g, "")
          .slice(0, 48) || `specialist-${index + 1}`,
        name: String(role?.name || `专业角色 ${index + 1}`).trim().slice(0, 80),
        responsibility: String(role?.responsibility || "承担声明的专业职责。").trim().slice(0, 300),
        deliverables: boundedStrings(role?.deliverables, 8, 180)
      }))
    : [];
  const roleIds = new Set(roles.map((role) => role.id));
  const workflow = Array.isArray(value?.workflow)
    ? value.workflow.slice(0, 12).map((step, index) => ({
        order: index + 1,
        title: String(step?.title || `执行步骤 ${index + 1}`).trim().slice(0, 120),
        owner: roleIds.has(String(step?.owner || "")) ? String(step.owner) : roles[0]?.id || "primary-agent",
        output: String(step?.output || `intermediates/step-${index + 1}.md`).trim().slice(0, 180),
        acceptance: boundedStrings(step?.acceptance, 6, 180)
      }))
    : [];
  if (roles.length < 2) throw new Error("编排草案缺少主执行与独立审查角色，请重新编排。");
  if (workflow.length < 3 || workflow.some((step) => step.acceptance.length < 2)) {
    throw new Error("编排草案缺少完整的工作流输出或验收条件，请重新编排。");
  }
  const reviewVerdict = String(value?.reviewVerdict || "").toLowerCase();
  const draft = {
    summary: String(value?.summary || "智能体编排草案").trim().slice(0, 500),
    designRationale: boundedStrings(value?.designRationale, 10, 300),
    roles,
    workflow,
    requiredInputs: boundedStrings(value?.requiredInputs, 6, 180),
    recommendedInputs: boundedStrings(value?.recommendedInputs, 12, 180),
    starterPrompts: boundedStrings(value?.starterPrompts, 8, 240),
    risks: boundedStrings(value?.risks, 10, 240),
    reviewVerdict: reviewVerdict === "approved" ? "approved" : "revise",
    modelProvider: connection.provider,
    model: connection.model,
    objective: String(request?.objective || "").slice(0, 500)
  };
  validateWorkshopDraftSemantics(draft, request);
  return draft;
}

function validateWorkshopDraftSemantics(draft, request) {
  const genericRole = draft.roles.find((role) =>
    !role.deliverables.length
    || role.responsibility === "承担声明的专业职责。"
    || /^专业角色\s*\d+$/.test(role.name));
  if (genericRole) {
    throw new Error(`角色“${genericRole.name}”仍是占位描述，必须说明行业职责和真实交付物。`);
  }

  const reviewSignals = /review|audit|verify|quality|审查|审核|验证|质检|风控/i;
  const reviewer = draft.roles.find((role, index) => index > 0 && reviewSignals.test(
    `${role.id} ${role.name} ${role.responsibility}`
  ));
  if (!reviewer) {
    throw new Error("缺少职责明确、独立于主执行角色的审查角色。");
  }
  const finalStep = draft.workflow[draft.workflow.length - 1];
  if (finalStep.owner !== reviewer.id) {
    throw new Error(`最终验收步骤必须由独立审查角色 ${reviewer.id} 负责。`);
  }

  const outputs = draft.workflow.map((step) => step.output.trim().toLowerCase());
  if (outputs.some((output) => !output || /真实文件或结构化成果|待定|todo|tbd/i.test(output))) {
    throw new Error("工作流仍包含占位输出；每一步必须写明可落盘的文件或结构化成果名称。");
  }
  if (new Set(outputs).size !== outputs.length) {
    throw new Error("多个步骤复用了同一个输出名称，无法形成可追溯的产物链。");
  }
  const primaryArtifact = String(request?.primaryArtifact || "").trim().toLowerCase();
  if (primaryArtifact && !outputs.some((output) => output.includes(primaryArtifact))) {
    throw new Error(`工作流没有生成用户定义的主交付物 ${request.primaryArtifact}。`);
  }
  if (draft.designRationale.length < 2) {
    throw new Error("设计依据不足；至少说明两个与当前行业和目标直接相关的角色或流程选择理由。");
  }
  if (!draft.requiredInputs.length) {
    throw new Error("没有从目标推导出任何必要资料，Agent 首次使用时将无法判断输入是否充分。");
  }
  if (draft.starterPrompts.length < 2) {
    throw new Error("快捷任务不足；至少生成两个针对当前 Agent 目标、可直接开始的真实任务。");
  }
  if (!draft.risks.length) {
    throw new Error("没有声明任何行业风险或不负责边界，不能通过信任审查。");
  }
}

function workshopRoleId(value, fallback) {
  return String(value || fallback)
    .toLowerCase()
    .replace(/[^a-z0-9-]/g, "-")
    .replace(/-+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 48) || fallback;
}

function uniqueWorkshopArtifact(value, fallback, used) {
  const source = String(value || fallback).trim().slice(0, 180) || fallback;
  let candidate = source;
  let suffix = 2;
  while (used.has(candidate.toLowerCase())) {
    const extension = path.extname(source);
    const stem = extension ? source.slice(0, -extension.length) : source;
    candidate = `${stem}-${suffix}${extension}`.slice(0, 180);
    suffix += 1;
  }
  used.add(candidate.toLowerCase());
  return candidate;
}

function coerceWorkshopDraft(value, request, connection, recoveryNote = "") {
  const category = String(request?.category || "当前行业").trim().slice(0, 80) || "当前行业";
  const objective = String(request?.objective || request?.description || "完成用户声明的目标")
    .trim().slice(0, 300) || "完成用户声明的目标";
  const primaryArtifact = String(request?.primaryArtifact || "最终交付物.md").trim().slice(0, 180)
    || "最终交付物.md";
  const rawRoles = Array.isArray(value?.roles) ? value.roles.slice(0, 6) : [];
  const roles = rawRoles.map((role, index) => {
    const fallbackId = index === 0 ? "domain-lead" : `specialist-${index + 1}`;
    const id = workshopRoleId(role?.id, fallbackId);
    const genericName = !role?.name || /^专业角色\s*\d+$/.test(String(role.name));
    const genericResponsibility = !role?.responsibility
      || String(role.responsibility).trim() === "承担声明的专业职责。";
    return {
      id,
      name: String(genericName ? `${category}${index === 0 ? "主执行 Agent" : "专业 Agent"}` : role.name)
        .trim().slice(0, 80),
      responsibility: String(genericResponsibility
        ? `围绕“${objective}”承担可追溯的分析与交付职责，并明确事实、推断和未知项。`
        : role.responsibility).trim().slice(0, 300),
      deliverables: boundedStrings(role?.deliverables, 8, 180)
    };
  });

  if (!roles.length) {
    roles.push({
      id: "domain-lead",
      name: `${category}主执行 Agent`.slice(0, 80),
      responsibility: `围绕“${objective}”整合资料、形成方案并生成主交付物。`.slice(0, 300),
      deliverables: ["evidence-map.md", primaryArtifact]
    });
  }
  if (!roles[0].deliverables.length) roles[0].deliverables = ["evidence-map.md", primaryArtifact];

  const reviewSignals = /review|audit|verify|quality|审查|审核|验证|质检|风控/i;
  let reviewer = roles.find((role, index) => index > 0 && reviewSignals.test(
    `${role.id} ${role.name} ${role.responsibility}`
  ));
  if (!reviewer) {
    reviewer = {
      id: "independent-reviewer",
      name: "独立审查 Agent",
      responsibility: "独立核验交付物是否覆盖目标、证据是否充分，并记录未完成项与风险边界。",
      deliverables: ["proof-of-done.json"]
    };
    roles.push(reviewer);
  } else if (!reviewer.deliverables.length) {
    reviewer.deliverables = ["proof-of-done.json"];
  }

  const roleIds = new Set(roles.map((role) => role.id));
  const usedOutputs = new Set();
  const workflow = (Array.isArray(value?.workflow) ? value.workflow.slice(0, 10) : [])
    .map((step, index) => ({
      order: index + 1,
      title: String(step?.title || `执行步骤 ${index + 1}`).trim().slice(0, 120),
      owner: roleIds.has(String(step?.owner || "")) ? String(step.owner) : roles[0].id,
      output: uniqueWorkshopArtifact(step?.output, `intermediates/step-${index + 1}.md`, usedOutputs),
      acceptance: boundedStrings(step?.acceptance, 6, 180)
    }));

  const defaults = [
    {
      title: "核对目标、资料与未知项",
      owner: roles[0].id,
      output: "evidence-map.md",
      acceptance: ["已区分事实、推断与未知项", "已列出缺失资料及其对结论的影响"]
    },
    {
      title: "形成行业判断与执行方案",
      owner: roles[0].id,
      output: "execution-plan.md",
      acceptance: ["方案直接对应用户目标", "关键判断均能追溯到输入资料或明确假设"]
    },
    {
      title: "生成主交付物",
      owner: roles[0].id,
      output: primaryArtifact,
      acceptance: ["主交付物已真实落盘且可打开", "内容覆盖目标、约束和下一步行动"]
    },
    {
      title: "独立验证并登记完成证据",
      owner: reviewer.id,
      output: "proof-of-done.json",
      acceptance: ["独立核对主交付物与用户目标", "未完成项、风险与证据位置已明确记录"]
    }
  ];
  while (workflow.length < 3) {
    const source = defaults[workflow.length];
    workflow.push({
      ...source,
      order: workflow.length + 1,
      output: uniqueWorkshopArtifact(source.output, `intermediates/step-${workflow.length + 1}.md`, usedOutputs)
    });
  }
  for (const step of workflow) {
    while (step.acceptance.length < 2) {
      step.acceptance.push(step.acceptance.length
        ? "输出已标明证据位置、限制和待确认项"
        : "输出已真实生成并可由用户直接检查");
    }
  }
  if (!workflow.some((step) => step.output.toLowerCase().includes(primaryArtifact.toLowerCase()))) {
    const insertAt = Math.max(1, workflow.length - 1);
    const source = defaults[2];
    workflow.splice(insertAt, 0, {
      ...source,
      order: insertAt + 1,
      output: uniqueWorkshopArtifact(primaryArtifact, primaryArtifact, usedOutputs)
    });
  }
  if (workflow[workflow.length - 1].owner !== reviewer.id) {
    const source = defaults[3];
    workflow.push({
      ...source,
      order: workflow.length + 1,
      output: uniqueWorkshopArtifact(source.output, "proof-of-done.json", usedOutputs)
    });
  }
  workflow.forEach((step, index) => { step.order = index + 1; });

  const rationale = boundedStrings(value?.designRationale, 8, 300);
  if (rationale.length < 1) {
    rationale.push(`${category}场景需要先验证输入与未知项，再生成“${primaryArtifact}”，避免用无依据内容代替真实交付。`);
  }
  if (rationale.length < 2) {
    rationale.push("主执行与独立审查分离，确保交付结果、证据和未完成边界可以分别检查。");
  }
  if (recoveryNote) rationale.push(String(recoveryNote).slice(0, 300));

  const requiredInputs = boundedStrings(value?.requiredInputs, 6, 180);
  if (!requiredInputs.length) {
    requiredInputs.push("任务对象、当前状态与希望达成的明确结果", "目标用户、市场或实际使用环境");
  }
  const recommendedInputs = boundedStrings(value?.recommendedInputs, 12, 180);
  if (!recommendedInputs.length) {
    recommendedInputs.push("已有图片、文档、数据或历史案例", "可用预算、时间边界与禁止事项");
  }
  const starterPrompts = boundedStrings(value?.starterPrompts, 8, 240);
  if (starterPrompts.length < 1) {
    starterPrompts.push(`先检查现有资料是否足以完成“${objective}”，列出最值得优先补充的内容。`);
  }
  if (starterPrompts.length < 2) {
    starterPrompts.push(`基于现有证据推进“${objective}”，生成 ${primaryArtifact}，并明确标注所有推断与未知项。`);
  }
  const risks = boundedStrings(value?.risks, 10, 240);
  if (!risks.length) risks.push("资料不足时不得编造事实；低置信度判断必须标注并给出验证方法。");
  if (recoveryNote) risks.push("编排委员会原始结构化输出未完全通过校验，本草案需要用户确认后才能构建 Agent Pack。");

  return normalizeWorkshopDraft({
    summary: String(value?.summary || `${request?.name || category} · 可审阅编排草案`).trim().slice(0, 500),
    designRationale: rationale,
    roles,
    workflow,
    requiredInputs,
    recommendedInputs,
    starterPrompts,
    risks,
    reviewVerdict: recoveryNote ? "revise" : value?.reviewVerdict
  }, request, connection);
}

async function callWorkshopModel(connection, systemPrompt, userPrompt, options = {}) {
  const controller = new AbortController();
  const timeoutMs = Math.max(15000, Math.min(Number(options.timeoutMs || 55000), 90000));
  const outputTokens = Math.max(600, Math.min(Number(options.outputTokens || 1400), 3200));
  const externalSignal = options.signal;
  const cancelFromParent = () => controller.abort("parent-cancelled");
  if (externalSignal?.aborted) cancelFromParent();
  else externalSignal?.addEventListener("abort", cancelFromParent, { once: true });
  const timeout = setTimeout(() => controller.abort("role-timeout"), timeoutMs);
  try {
    const headers = { "Content-Type": "application/json" };
    if (connection.apiKey) headers.Authorization = `Bearer ${connection.apiKey}`;
    let body;
    if (connection.provider === "openai") {
      body = {
        model: connection.model,
        instructions: systemPrompt,
        input: userPrompt,
        max_output_tokens: outputTokens,
        ...(options.jsonMode ? { text: { format: { type: "json_object" } } } : {})
      };
    } else if (connection.provider === "ollama" && /\/api\/chat\/?$/i.test(connection.endpoint)) {
      body = {
        model: connection.model,
        messages: [
          { role: "system", content: systemPrompt },
          { role: "user", content: userPrompt }
        ],
        stream: false,
        ...(options.jsonMode ? { format: "json" } : {}),
        options: { num_ctx: 12288, num_predict: outputTokens }
      };
    } else {
      body = {
        model: connection.model,
        messages: [
          { role: "system", content: systemPrompt },
          { role: "user", content: userPrompt }
        ],
        stream: false,
        max_tokens: outputTokens,
        ...(options.jsonMode ? { response_format: { type: "json_object" } } : {})
      };
    }
    const response = await fetch(connection.endpoint, {
      method: "POST",
      headers,
      body: JSON.stringify(body),
      signal: controller.signal
    });
    const responseText = await response.text();
    let data;
    try {
      data = JSON.parse(responseText);
    } catch {
      throw new Error(
        response.ok
          ? "模型接口返回了无法解析的非 JSON 响应。"
          : `模型编排请求失败：HTTP ${response.status} · ${responseText.slice(0, 240)}`
      );
    }
    if (!response.ok) {
      throw new Error(data?.error?.message || `模型编排请求失败：HTTP ${response.status}`);
    }
    const output = connection.provider === "openai"
      ? data.output_text || data.output?.flatMap((item) => item.content || [])
          .filter((item) => item.type === "output_text").map((item) => item.text).join("\n")
      : connection.provider === "ollama" && data?.message?.content
        ? data.message.content
        : data?.choices?.[0]?.message?.content;
    if (!output) throw new Error("模型没有返回智能体编排内容。");
    return String(output);
  } catch (error) {
    if (error?.name === "AbortError" && externalSignal?.aborted) {
      throw new Error("智能体编排已由用户停止。");
    }
    if (error?.name === "AbortError") {
      throw new Error(`智能体角色在 ${Math.round(timeoutMs / 1000)} 秒内没有返回，已停止等待。`);
    }
    throw error;
  } finally {
    clearTimeout(timeout);
    externalSignal?.removeEventListener("abort", cancelFromParent);
  }
}

function normalizeAgentFoundryBrief(value) {
  const text = (input, limit = 600) => String(input || "").trim().slice(0, limit);
  const choose = (input, allowed, fallback) => allowed.includes(String(input || ""))
    ? String(input)
    : fallback;
  const brief = {
    name: text(value?.name, 80),
    category: text(value?.category, 60),
    description: text(value?.description, 520),
    objective: text(value?.objective, 420),
    scenarioProfile: choose(value?.scenarioProfile, ["research", "operations", "content", "engineering", "compliance", "service"], "research"),
    autonomyLevel: choose(value?.autonomyLevel, ["assist", "approval-execute", "goal-autonomous"], "approval-execute"),
    lifecycle: choose(value?.lifecycle, ["single-run", "project", "continuous", "scheduled"], "project"),
    collaborationMode: choose(value?.collaborationMode, ["independent", "specialist-team", "coordinator"], "specialist-team"),
    deliveryMode: choose(value?.deliveryMode, ["conversation", "document", "data", "code", "operation", "mixed"], "mixed"),
    decisionStyle: choose(value?.decisionStyle, ["conservative", "balanced", "exploratory", "creative", "compliance-first"], "balanced"),
    primaryArtifact: text(value?.primaryArtifact, 100),
    understanding: text(value?.understanding, 360)
  };
  const missing = ["name", "category", "description", "objective", "primaryArtifact", "understanding"]
    .filter((key) => !brief[key]);
  if (missing.length) {
    throw new Error(`模型返回的业务 Agent 摘要不完整：缺少 ${missing.join("、")}`);
  }
  return brief;
}

async function prepareAgentFoundryBrief(request) {
  const provider = String(request?.provider || "");
  validateProvider(provider);
  const connection = modelConnections.get(provider);
  if (!connection) throw new Error(`请先连接 ${provider.toUpperCase()} 模型，再让 NOVA 理解业务需求。`);
  const goal = String(request?.goal || "").trim();
  if (goal.length < 8) throw new Error("请用一句完整的话说明这个 Agent 要帮助谁、解决什么问题。");
  const schema = `{"name":"简洁中文名称","category":"行业或业务分类","description":"服务对象、典型任务与明确边界","objective":"可检查的最终结果","scenarioProfile":"research|operations|content|engineering|compliance|service","autonomyLevel":"assist|approval-execute|goal-autonomous","lifecycle":"single-run|project|continuous|scheduled","collaborationMode":"independent|specialist-team|coordinator","deliveryMode":"conversation|document|data|code|operation|mixed","decisionStyle":"conservative|balanced|exploratory|creative|compliance-first","primaryArtifact":"中文文件名.扩展名","understanding":"用普通人能懂的一句话复述 NOVA 的理解"}`;
  const systemPrompt = "你是 NOVA Agent Foundry 的业务分析师。把用户的一句话需求提炼成可审阅、可编排的专业 Agent 业务摘要。不要虚构用户没有提供的数据；信息不足写入边界，不要反问。只输出一个 JSON 对象，不要 Markdown。";
  const userPrompt = `用户需求：\n${goal}\n\n请严格按这个结构输出：${schema}`;
  let firstOutput = "";
  try {
    firstOutput = await callWorkshopModel(connection, systemPrompt, userPrompt, {
      timeoutMs: 65000,
      outputTokens: 1200,
      jsonMode: true
    });
    return normalizeAgentFoundryBrief(extractJsonObject(firstOutput));
  } catch (firstError) {
    const repairPrompt = `用户需求：\n${goal}\n\n上一份输出：\n${firstOutput.slice(0, 7000)}\n\n校验错误：${safeError(firstError)}\n\n请重新输出完整且合法的 JSON，结构必须是：${schema}`;
    const repairedOutput = await callWorkshopModel(
      connection,
      `${systemPrompt} 这是结构修订请求，必须补齐所有字段。`,
      repairPrompt,
      { timeoutMs: 65000, outputTokens: 1200, jsonMode: true }
    );
    return normalizeAgentFoundryBrief(extractJsonObject(repairedOutput));
  }
}

async function waitForWorkshopRetry(milliseconds, signal) {
  if (signal?.aborted) throw new Error("智能体编排已由用户停止。");
  await new Promise((resolve, reject) => {
    const finish = () => {
      signal?.removeEventListener("abort", cancel);
      resolve();
    };
    const timeout = setTimeout(finish, milliseconds);
    const cancel = () => {
      clearTimeout(timeout);
      signal?.removeEventListener("abort", cancel);
      reject(new Error("智能体编排已由用户停止。"));
    };
    signal?.addEventListener("abort", cancel, { once: true });
  });
}

async function callWorkshopRole(connection, systemPrompt, userPrompt, options = {}) {
  const attempts = Math.max(1, Math.min(Number(options.attempts || 2), 3));
  let lastError;
  for (let attempt = 1; attempt <= attempts; attempt += 1) {
    options.onAttempt?.(attempt, attempts);
    try {
      return await callWorkshopModel(connection, systemPrompt, userPrompt, options);
    } catch (error) {
      lastError = error;
      if (options.signal?.aborted || attempt === attempts) throw error;
      options.onRetry?.(attempt, attempts, safeError(error));
      await waitForWorkshopRetry(900 * attempt, options.signal);
    }
  }
  throw lastError || new Error("模型角色没有完成分析。");
}

async function orchestrateAgentPack(owner, request, signal) {
  const provider = String(request?.provider || "");
  validateProvider(provider);
  const connection = modelConnections.get(provider);
  if (!connection) throw new Error(`请先连接 ${provider.toUpperCase()} 模型，再开始智能体编排。`);
  const design = {
    name: request?.name,
    category: request?.category,
    description: request?.description,
    objective: request?.objective,
    scenarioProfile: request?.scenarioProfile,
    autonomyLevel: request?.autonomyLevel,
    lifecycle: request?.lifecycle,
    collaborationMode: request?.collaborationMode,
    deliveryMode: request?.deliveryMode,
    decisionStyle: request?.decisionStyle,
    primaryArtifact: request?.primaryArtifact
  };
  const publish = (agent, status, detail, output = "") => {
    if (owner.isDestroyed() || owner.webContents.isDestroyed()) return;
    owner.webContents.send(
      "nova:agent-workshop-event",
      { agent, status, detail, output: String(output || "").slice(0, 1200), at: new Date().toISOString() }
    );
  };
  const designText = JSON.stringify(design, null, 2);
  const runRole = async (agent, initialDetail, systemPrompt, userPrompt, options = {}) => {
    const started = Date.now();
    let currentAttempt = 1;
    publish(agent, "running", initialDetail);
    const heartbeat = setInterval(() => {
      const seconds = Math.max(1, Math.round((Date.now() - started) / 1000));
      publish(agent, "running", `第 ${currentAttempt} 次模型分析仍在进行，已等待 ${seconds} 秒；可随时停止。`);
    }, 6000);
    try {
      const output = await callWorkshopRole(connection, systemPrompt, userPrompt, {
        signal,
        attempts: 2,
        ...options,
        onAttempt: (attempt, attempts) => {
          currentAttempt = attempt;
          publish(agent, "running", `正在进行第 ${attempt}/${attempts} 次真实模型分析`);
        },
        onRetry: (attempt, attempts, reason) => publish(
          agent,
          "running",
          `第 ${attempt}/${attempts} 次未完成：${reason}；正在重新请求模型`
        )
      });
      publish(agent, "done", "模型分析完成", output);
      return output;
    } catch (error) {
      publish(agent, "failed", safeError(error));
      throw new Error(`${agent}未完成：${safeError(error)}`);
    } finally {
      clearInterval(heartbeat);
    }
  };

  const domainAnalysis = await runRole(
    "行业架构师",
    "正在分析行业目标、服务对象、必要输入与未知项边界",
    "你是 NOVA 行业 Agent 架构师。只做声明式设计，不编写代码，不假装拥有资料。区分事实、假设和未知项，并从用户给出的设计推导输入要求。",
    `分析下面的 Agent 设计。输出紧凑中文架构建议，覆盖服务对象、核心判断、必要资料、可选资料、行业风险和不负责边界。\n${designText}`,
    { timeoutMs: 70000, outputTokens: 1100 }
  );

  const workflowAnalysis = await runRole(
    "工作流架构师",
    "正在基于行业分析设计角色分工、依赖关系、交付物与验收条件",
    "你是 NOVA 多 Agent 工作流架构师。设计真实可执行的角色与交付契约。每个角色职责独立，每一步都有负责人、真实输出和可检查验收条件，并保留独立审查角色。",
    `原始设计：\n${designText}\n\n行业架构师分析：\n${domainAnalysis}\n\n据此设计 2-6 个角色和 3-8 个顺序步骤，说明每步交付物与验收条件。`,
    { timeoutMs: 70000, outputTokens: 1300 }
  );

  publish("信任审查官", "running", "正在交叉审查两份方案并形成可落盘的最终编排草案");
  const reviewStarted = Date.now();
  const reviewHeartbeat = setInterval(() => {
    const seconds = Math.max(1, Math.round((Date.now() - reviewStarted) / 1000));
    publish("信任审查官", "running", `正在校验角色、工作流与验收闭环，已等待 ${seconds} 秒。`);
  }, 6000);
  let finalOutput;
  try {
    finalOutput = await callWorkshopRole(
      connection,
      "你是 NOVA Agent Creation Council 的信任审查官。综合两位架构师的结果，删除套话和重复角色，确保用户目标、角色、工作流、输入建议和交付物闭环。只能输出一个 JSON 对象，不要 Markdown。只有结构完整时 reviewVerdict 才能是 approved。",
      `原始设计：\n${designText}\n\n行业架构师：\n${domainAnalysis}\n\n工作流架构师：\n${workflowAnalysis}\n\n` +
      `请严格输出：{"summary":"...","designRationale":["..."],"roles":[{"id":"lowercase-role-id","name":"...","responsibility":"...","deliverables":["..."]}],"workflow":[{"order":1,"title":"...","owner":"角色id","output":"真实文件或结构化成果","acceptance":["可检查条件"]}],"requiredInputs":["..."],"recommendedInputs":["..."],"starterPrompts":["..."],"risks":["..."],"reviewVerdict":"approved或revise"}`,
      {
        signal,
        attempts: 2,
        timeoutMs: 80000,
        outputTokens: 2000,
        jsonMode: true,
        onAttempt: (attempt, attempts) => publish("信任审查官", "running", `正在进行第 ${attempt}/${attempts} 次模型交叉审查`),
        onRetry: (attempt, attempts, reason) => publish("信任审查官", "running", `第 ${attempt}/${attempts} 次审查未完成：${reason}；正在重新请求模型`)
      }
    );
    let validationError;
    try {
      const draft = normalizeWorkshopDraft(extractJsonObject(finalOutput), design, connection);
      publish("信任审查官", "done", "编排草案已通过结构与安全边界审查", draft.summary);
      return draft;
    } catch (error) {
      validationError = error;
    }

    publish("信任审查官", "running", `首份草案结构未通过：${safeError(validationError)}；正在由模型修订`);
    const revisedOutput = await callWorkshopRole(
      connection,
      "你是 NOVA Agent Creation Council 的修订审查官。必须根据校验错误修正草案，不能删除必要角色、工作流、验收条件或风险。只输出一个合法 JSON 对象，不要 Markdown。",
      `原始设计：\n${designText}\n\n行业分析：\n${domainAnalysis}\n\n工作流分析：\n${workflowAnalysis}\n\n待修订草案：\n${finalOutput.slice(0, 12000)}\n\n校验错误：${safeError(validationError)}\n\n重新输出完整草案。`,
      {
        signal,
        attempts: 2,
        timeoutMs: 80000,
        outputTokens: 2200,
        jsonMode: true,
        onAttempt: (attempt, attempts) => publish("信任审查官", "running", `正在进行第 ${attempt}/${attempts} 次模型草案修订`),
        onRetry: (attempt, attempts, reason) => publish("信任审查官", "running", `第 ${attempt}/${attempts} 次修订未完成：${reason}；正在重试`)
      }
    );
    const draft = normalizeWorkshopDraft(extractJsonObject(revisedOutput), design, connection);
    publish("信任审查官", "done", "模型已修订草案，并通过结构与安全边界审查", draft.summary);
    return draft;
  } catch (error) {
    publish("信任审查官", "failed", safeError(error));
    throw error;
  } finally {
    clearInterval(reviewHeartbeat);
  }
}

function buildAgentWorkshopRuntimePrompt(request) {
  const design = {
    name: request?.name,
    category: request?.category,
    description: request?.description,
    objective: request?.objective,
    scenarioProfile: request?.scenarioProfile,
    autonomyLevel: request?.autonomyLevel,
    lifecycle: request?.lifecycle,
    collaborationMode: request?.collaborationMode,
    deliveryMode: request?.deliveryMode,
    decisionStyle: request?.decisionStyle,
    primaryArtifact: request?.primaryArtifact
  };
  return [
    "[NOVA_AGENT_WORKSHOP]",
    "你是 NOVA Agent Creation Council 的主协调 Agent。",
    "AgentOS Supervisor 会先附上行业架构师、工作流架构师和信任审查官的真实子 Agent 产出；必须交叉综合这些产出，不要重复创建第二组 Agent。",
    "三名子 Agent 在并行阶段彼此不可见；忽略任何关于‘没有看到其他子 Agent 产出’的抱怨，你现在收到的工作组上下文才是完整汇总。",
    "子 Agent 只能进行只读分析；不要修改用户工程，不要执行命令，不要假装拥有用户未提供的事实。",
    "你只负责输出可供用户审阅的智能体设计草案，不负责生成、写入、安装或注册 Agent Pack 文件。",
    "禁止自行发明 manifest.json、契约.json、工作流.json、注册记录.json 或‘已注册’状态；这些都不是 NOVA 的可加载格式。",
    "用户确认草案后，NOVA Pack 编译器会固定生成 nova.industry.json、agent-card.json、delivery-contract.json、标准目录与 certification.json，并通过真实注册服务安装。",
    "reviewVerdict 只表示设计草案是否可进入编译阶段，绝不表示 Agent Pack 已落盘、已安装或已启用。",
    "最终只输出一个 JSON 对象，不要 Markdown、解释文字或代码围栏。",
    "JSON 契约：",
    '{"summary":"...","designRationale":["..."],"roles":[{"id":"lowercase-role-id","name":"...","responsibility":"...","deliverables":["..."]}],"workflow":[{"order":1,"title":"...","owner":"角色id","output":"真实文件或结构化成果","acceptance":["可检查条件"]}],"requiredInputs":["..."],"recommendedInputs":["..."],"starterPrompts":["..."],"risks":["..."],"reviewVerdict":"approved或revise"}',
    "硬性要求：角色 2–6 个；必须包含职责明确且独立于主执行角色的审查角色；步骤 3–8 个；每步负责人必须引用角色 id；每步必须有唯一的真实文件/结构化输出和至少两条可检查验收条件；最终步骤由独立审查角色负责并生成用户定义的主交付物。",
    "禁止输出‘专业角色’‘承担专业职责’‘真实文件或结构化成果’等占位词。至少给出两条行业化设计依据、一项必要资料、两个可直接开始的快捷任务和一项风险边界。",
    "只要能够形成结构完整且可供用户审阅的方案，就必须输出完整草案。未知事实应放入 requiredInputs 或 risks，而不是拒绝返回草案。",
    "结构完整、结果可验证且风险已被工作流吸收时 reviewVerdict 为 approved；仍需用户取舍但可以继续审阅时为 revise。",
    "Agent 设计输入：",
    JSON.stringify(design, null, 2)
  ].join("\n");
}

function buildAgentWorkshopRepairPrompt(request, output, stageOutputs, parseError) {
  const evidence = (Array.isArray(stageOutputs) ? stageOutputs : [])
    .slice(0, 4)
    .map((item, index) => [
      `## 子 Agent 产出 ${index + 1} · ${String(item?.action || item?.agent || "未命名角色")}`,
      String(item?.detail || "").slice(0, 2400)
    ].join("\n"))
    .join("\n\n");
  return [
    "[NOVA_AGENT_DRAFT_REPAIR]",
    "你是 NOVA Agent Creation Council 的最终编排委员。前三名真实子 Agent 已经完成分析；本轮不要创建任何新 Agent。",
    "主协调输出存在 JSON 截断或语法错误。请综合下面的真实产出并修复结构，不得改成模板、不得删除行业信息、不得声称资料未返回。",
    "本轮仍然只修复设计 JSON；禁止生成文件清单、伪造注册记录或声称 Agent Pack 已安装。真正的 Pack 只能由用户确认后的 NOVA Pack 编译器生成。",
    "只输出一个完整合法的 JSON 对象，不要 Markdown、代码围栏或解释。",
    "JSON 契约：",
    '{"summary":"...","designRationale":["..."],"roles":[{"id":"lowercase-role-id","name":"...","responsibility":"...","deliverables":["..."]}],"workflow":[{"order":1,"title":"...","owner":"角色id","output":"真实文件或结构化成果","acceptance":["可检查条件"]}],"requiredInputs":["..."],"recommendedInputs":["..."],"starterPrompts":["..."],"risks":["..."],"reviewVerdict":"approved或revise"}',
    `解析错误：${safeError(parseError)}`,
    "Agent 设计输入：",
    JSON.stringify(request || {}, null, 2).slice(0, 5000),
    "主协调 Agent 的原始输出：",
    String(output || "").slice(0, 8000),
    "真实子 Agent 阶段产出：",
    evidence || "（阶段产出未被宿主捕获；仅修复主协调输出）"
  ].join("\n");
}

function recoverWorkshopDraftFromStageOutputs(stageOutputs, request, connection, additionalOutputs = []) {
  const recovered = [];
  const stageEvidence = (Array.isArray(stageOutputs) ? stageOutputs : [])
    .map((item) => ({
      label: String(item?.action || item?.agent || "阶段产出"),
      detail: String(item?.detail || item?.output || "").trim()
    }))
    .filter((item) => item.detail);
  const evidence = [
    ...stageEvidence,
    ...(Array.isArray(additionalOutputs) ? additionalOutputs : [])
      .map((detail, index) => ({ label: `委员会输出 ${index + 1}`, detail: String(detail || "").trim() }))
      .filter((item) => item.detail)
  ];
  for (const item of evidence) {
    try {
      const draft = coerceWorkshopDraft(
        extractJsonObject(String(item?.detail || "")),
        request,
        connection,
        `本草案从已完成的模型阶段产出“${item.label}”恢复；原始产出已保留，等待用户最终确认。`
      );
      recovered.push({
        ...draft,
        reviewVerdict: "revise"
      });
    } catch {
      // Prose-only stage results are retained below and can still seed a safe review draft.
    }
  }
  const best = recovered.sort((left, right) =>
    (right.roles.length + right.workflow.length)
    - (left.roles.length + left.workflow.length))[0] || null;
  if (best) return best;
  if (!evidence.length) return null;
  return coerceWorkshopDraft(
    {},
    request,
    connection,
    `编排委员会已产生 ${evidence.length} 份模型阶段结果，但最终结构化输出未通过；本草案保留任务目标和安全边界，等待用户审阅。`
  );
}

async function executeAgentWorkshopSession(owner, sessionId, request, connection, ownerKey) {
  const prompt = buildAgentWorkshopRuntimePrompt(request);
  const localAbortSignal = activeWorkshopRuns.get(ownerKey)?.abortController?.signal;
  try {
    const result = await bridge.call("run_design_session", {
      sessionId,
      prompt,
      workspaceRoot: path.join(app.getPath("userData"), "agent-workshop", "runtime", sessionId),
      provider: String(request?.provider || connection.provider || "deepseek"),
      model: connection.model,
      apiKey: connection.apiKey || "",
      endpoint: connection.endpoint,
    });
    const output = String(result?.output || "");
    const stageOutputs = Array.isArray(result?.stageOutputs) ? result.stageOutputs : [];
    if (localAbortSignal?.aborted) throw new Error("智能体编排已由用户停止。");
    updateWorkshopSession(sessionId, {
      stageOutputs: stageOutputs.slice(0, 12).map((item) => ({
        agent: String(item?.agent || "").slice(0, 100),
        action: String(item?.action || "").slice(0, 160),
        detail: String(item?.detail || "").slice(0, 8000)
      })),
      councilOutput: output.slice(0, 20000)
    });
    let draft;
    let recoveryDetail = "";
    let parseError;
    try {
      const parsed = extractJsonObject(output);
      try {
        draft = normalizeWorkshopDraft(parsed, request, connection);
      } catch (validationError) {
        draft = coerceWorkshopDraft(
          parsed,
          request,
          connection,
          `委员会草案已完成模型分析，但结构校验未通过：${safeError(validationError)}`
        );
        recoveryDetail = "模型草案已完成；NOVA 在本地补齐了缺失的结构、验收条件和独立审查闭环，未再次消耗 Token。";
      }
    } catch (error) {
      parseError = error;
    }
    if (!draft) {
      acceptWorkshopRuntimeEvent({
        sessionId,
        kind: "thinking",
        agent: "NOVA",
        action: "编排委员会正在修复草案结构",
        detail: "设计 Agent 的阶段结果已经保存；只进行一次轻量结构修复，不会重新启动另一组 Agent。"
      });
      let repairError;
      let repairOutput = "";
      try {
        repairOutput = await callWorkshopModel(
          connection,
          "你是 NOVA Agent Creation Council 的结构修复委员。只修复已有委员会结果，不创建新角色组、不调用工具、不扩写无依据事实。只输出一个完整 JSON 对象。",
          buildAgentWorkshopRepairPrompt(request, output, stageOutputs, parseError),
          { jsonMode: true, outputTokens: 2800, timeoutMs: 80000, signal: localAbortSignal }
        );
        const repairedValue = extractJsonObject(repairOutput);
        try {
          draft = normalizeWorkshopDraft(repairedValue, request, connection);
        } catch (validationError) {
          draft = coerceWorkshopDraft(
            repairedValue,
            request,
            connection,
            `模型修复稿已返回，但仍有结构缺口：${safeError(validationError)}`
          );
          recoveryDetail = "委员会修复稿已返回；NOVA 仅在本地补齐结构缺口，并将草案标记为需要审阅。";
        }
      } catch (error) {
        repairError = error;
      }
      if (!draft) {
        draft = recoverWorkshopDraftFromStageOutputs(
          stageOutputs,
          request,
          connection,
          [output, repairOutput]
        );
        if (draft) {
          recoveryDetail = `最终 JSON 修复未通过，但已从实际完成的模型阶段结果恢复可审阅草案：${safeError(repairError || parseError)}`;
        }
      }
      if (!draft) {
        throw new Error(
          `最终草案结构修复失败：${safeError(repairError || parseError)}`
        );
      }
    }
    if (recoveryDetail) {
      acceptWorkshopRuntimeEvent({
        sessionId,
        kind: "message",
        agent: "NOVA",
        action: "已恢复可审阅草案",
        detail: recoveryDetail
      });
    }
    acceptWorkshopRuntimeEvent({
      sessionId,
      kind: "completed",
      agent: "NOVA",
      action: "编排草案已生成",
      detail: draft.summary
    });
    saveWorkshopSession({
      ...(readWorkshopSessions().find((item) => item.id === sessionId) || { id: sessionId }),
      status: "completed",
      draft,
      error: "",
      warning: recoveryDetail
    });
    if (!owner.isDestroyed() && !owner.webContents.isDestroyed()) {
      owner.webContents.send("nova:agent-workshop-ready", { sessionId, draft });
    }
  } catch (error) {
    const diagnosticId = recordWorkshopFailure(request, error);
    const existing = readWorkshopSessions().find((item) => item.id === sessionId);
    const cancelled = existing?.status === "cancelled";
    const eventEvidence = (Array.isArray(existing?.events) ? existing.events : [])
      .filter((item) => String(item?.output || "").trim())
      .map((item) => ({ agent: item.agent, action: item.detail, detail: item.output }));
    const recoveredDraft = !cancelled
      ? recoverWorkshopDraftFromStageOutputs(
          [...(Array.isArray(existing?.stageOutputs) ? existing.stageOutputs : []), ...eventEvidence],
          request,
          connection,
          [existing?.councilOutput]
        )
      : null;
    if (recoveredDraft) {
      const warning = `委员会运行中断，但已从完成的模型阶段结果恢复草案（诊断编号 ${diagnosticId}）。请审阅后再构建。`;
      updateWorkshopSession(sessionId, {
        status: "completed",
        draft: recoveredDraft,
        error: "",
        warning
      });
      acceptWorkshopRuntimeEvent({
        sessionId,
        kind: "completed",
        agent: "NOVA",
        action: "已从阶段结果恢复草案",
        detail: warning
      });
      if (!owner.isDestroyed() && !owner.webContents.isDestroyed()) {
        owner.webContents.send("nova:agent-workshop-ready", { sessionId, draft: recoveredDraft, warning });
      }
      return;
    }
    const detail = cancelled
      ? "本次智能体编排已停止，设计输入仍然保留。"
      : `${safeError(error)}（诊断编号 ${diagnosticId}）`;
    updateWorkshopSession(sessionId, {
      status: cancelled ? "cancelled" : "failed",
      error: detail
    });
    if (!owner.isDestroyed() && !owner.webContents.isDestroyed()) {
      owner.webContents.send("nova:agent-workshop-ready", { sessionId, error: detail });
    }
  } finally {
    const active = activeWorkshopRuns.get(ownerKey);
    if (active?.sessionId === sessionId) activeWorkshopRuns.delete(ownerKey);
  }
}

async function startAgentWorkshopSession(owner, request) {
  const provider = String(request?.provider || "");
  validateProvider(provider);
  const connection = modelConnections.get(provider);
  if (!connection) throw new Error(`请先连接 ${provider.toUpperCase()} 模型，再开始智能体编排。`);
  const sessionId = `design-${crypto.randomUUID().replaceAll("-", "").slice(0, 12)}`;
  const now = new Date().toISOString();
  const session = saveWorkshopSession({
    id: sessionId,
    status: "running",
    name: String(request?.name || "未命名 Agent").slice(0, 120),
    provider,
    model: connection.model,
    request: { ...request, provider, model: connection.model },
    events: [],
    draft: null,
    error: "",
    createdAt: now,
    updatedAt: now
  });
  const ownerKey = owner.webContents.id;
  activeWorkshopRuns.set(ownerKey, {
    sessionId,
    roles: new Map(),
    abortController: new AbortController()
  });
  const execution = new Promise((resolve) => setImmediate(resolve))
    .then(() => executeAgentWorkshopSession(owner, sessionId, request, connection, ownerKey));
  activeWorkshopRuns.get(ownerKey).execution = execution;
  void execution;
  return { session };
}

function sendAgentPackBuildEvent(owner, payload) {
  if (!owner?.isDestroyed()) {
    owner.webContents.send("nova:agent-event", payload);
  }
}

async function publishAgentPackBuildEvent(owner, taskId, event) {
  const payload = {
    taskId,
    kind: event.kind,
    agent: event.agent,
    action: event.action,
    detail: event.detail || "",
    progress: event.progress,
    activeUnits: event.activeUnits ?? 1
  };
  await bridge.call("task_event", payload);
  sendAgentPackBuildEvent(owner, payload);
}

function validateGeneratedAgentPack(result, details, request) {
  const failures = [];
  if (!result?.pack?.id) failures.push("Agent Pack 没有可注册的身份标识");
  if (result?.certification?.score !== 100) failures.push("标准体检没有达到 100/100");
  if (result?.certification?.level !== "Runnable") failures.push("Agent Pack 尚未达到 Runnable");
  if (!details?.agentRoster?.trim()) failures.push("角色契约为空");
  if (!details?.workflows?.length || !details.workflows[0]?.steps?.length) failures.push("没有可执行工作流");
  const designedStepCount = request?.orchestration?.workflow?.length || 0;
  const compiledStepCount = (details?.workflows || [])
    .reduce((total, workflow) => total + (workflow?.steps?.length || 0), 0);
  if (designedStepCount > 0 && compiledStepCount !== designedStepCount) {
    failures.push(`编排草案包含 ${designedStepCount} 个步骤，但 Pack 只生成了 ${compiledStepCount} 个步骤`);
  }
  if (!details?.onboarding?.steps?.length) failures.push("首次使用引导为空");
  if (!details?.deliveryTemplate?.trim()) failures.push("交付模板为空");
  const passedChecks = new Set((result?.certification?.checks || [])
    .filter((check) => check?.passed)
    .map((check) => check.id));
  for (const [id, label] of [
    ["workflow-owner-integrity", "角色与工作流没有闭环"],
    ["independent-review", "缺少独立交付审查"],
    ["artifact-chain", "主交付物与证据链不完整"],
    ["delivery-envelope", "统一输出与反馈契约缺失"],
    ["eval-contracts", "五类行为契约不完整"],
    ["sandbox-dry-run", "沙箱契约演练未通过"]
  ]) {
    if (!passedChecks.has(id)) failures.push(label);
  }
  if (failures.length) {
    throw new Error(`Agent Pack 完整性检查未通过：${failures.join("；")}`);
  }
}

function buildAgentPackDelivery(request, result, details) {
  const roles = request?.orchestration?.roles || [];
  const workflow = details?.workflows?.[0]?.steps || [];
  return [
    `# ${result.pack.name} 已完成构建`,
    "",
    "本次构建由 AgentOS 任务队列真实执行，Agent Pack 已完成编译、跨文件契约检查、五类行为契约和无副作用沙箱演练。",
    "",
    "## 构建结果",
    `- Agent ID：${result.pack.id}`,
    `- 状态：${result.pack.status}（默认保持停用，等待用户检查）`,
    `- 标准体检：${result.certification.score}/100 · ${result.certification.level}`,
    `- 角色数量：${roles.length}`,
    `- 工作流步骤：${workflow.length}`,
    "",
    "## 角色编排",
    ...roles.map((role) => `- **${role.name}**：${role.responsibility}`),
    "",
    "## 可执行工作流",
    ...workflow.map((step, index) =>
      `${index + 1}. **${step.title}** · ${step.agent} · 输出：${(step.outputs || []).join("、")}`),
    "",
    "## 下一步",
    "该 Agent 默认保持停用。请先查看角色、工作流和资料引导，再用一个真实案例试运行；真实案例通过后再启用到正式任务。"
  ].join("\n");
}

async function executeAgentPackBuild(owner, taskId, request) {
  try {
    await publishAgentPackBuildEvent(owner, taskId, {
      kind: "message",
      agent: "Agent 工坊",
      action: "任务规划",
      detail: JSON.stringify({
        strategy: "Agent Pack 生成与可用性验证",
        replacePlan: true,
        steps: [
          { id: "lock", title: "锁定编排草案", detail: "保存角色、工作流和审查结论", agent: "Agent 工坊" },
          { id: "compile", title: "编译 Pack 契约", detail: "生成 Agent Card、角色、工作流和交付模板", agent: "Pack 编译器" },
          { id: "assemble", title: "装配引导与能力", detail: "检查首次使用引导和能力需求", agent: "能力装配器" }
        ]
      }),
      progress: 8,
      activeUnits: 1
    });
    await publishAgentPackBuildEvent(owner, taskId, {
      kind: "completed",
      agent: "Agent 工坊",
      action: "编排草案已锁定",
      detail: `${request.orchestration?.roles?.length || 0} 个角色 · ${request.orchestration?.workflow?.length || 0} 个步骤 · 审查已通过`,
      progress: 18,
      activeUnits: 1
    });
    await publishAgentPackBuildEvent(owner, taskId, {
      kind: "toolrunning",
      agent: "Pack 编译器",
      action: "正在编译 Agent Pack",
      detail: "生成身份、角色、工作流、交付契约和基础评测文件",
      progress: 34,
      activeUnits: 1
    });

    const result = await bridge.call("create_agent_pack", request || {});
    await publishAgentPackBuildEvent(owner, taskId, {
      kind: "toolcompleted",
      agent: "Pack 编译器",
      action: "Pack 文件已真实生成",
      detail: `${result.pack.agentCount} 个角色 · ${result.pack.workflowCount} 条主工作流 · ${request?.orchestration?.workflow?.length || 0} 个执行步骤`,
      progress: 62,
      activeUnits: 1
    });

    const details = await bridge.call("get_agent_pack", { id: result.pack.id });
    await publishAgentPackBuildEvent(owner, taskId, {
      kind: "toolcompleted",
      agent: "能力装配器",
      action: "引导与能力契约已装配",
      detail: `${details.onboarding?.steps?.length || 0} 项资料引导 · ${details.capabilityRequirements?.items?.length || 0} 项能力需求`,
      progress: 78,
      activeUnits: 1
    });

    validateGeneratedAgentPack(result, details, request);
    await publishAgentPackBuildEvent(owner, taskId, {
      kind: "completed",
      agent: "标准体检官",
      action: "契约体检与沙箱演练通过",
      detail: `${result.certification.checks.filter((check) => check.passed).length}/${result.certification.checks.length} 项通过 · ${result.certification.level} · 尚待真实案例试运行`,
      progress: 92,
      activeUnits: 1
    });

    const delivery = buildAgentPackDelivery(request, result, details);
    await bridge.call("complete_task", {
      taskId,
      succeeded: true,
      outcome: "completed",
      outputCharacters: delivery.length,
      detail: `Agent Pack ${result.pack.id} 已完成编译、体检并注册`,
      draft: delivery,
      agentPackId: result.pack.id
    });
    sendAgentPackBuildEvent(owner, {
      taskId,
      kind: "completed",
      agent: "Agent Pack Builder",
      action: "Agent 已生成，等待真实案例试运行",
      detail: `${result.pack.name} · 契约体检 ${result.certification.score}/100 · 默认停用`,
      progress: 100,
      activeUnits: 0,
      packId: result.pack.id
    });
  } catch (error) {
    const message = safeError(error);
    try {
      await bridge.call("complete_task", {
        taskId,
        succeeded: false,
        detail: `Agent Pack 构建失败：${message}`
      });
    } catch {
      // Preserve the original build failure.
    }
    sendAgentPackBuildEvent(owner, {
      taskId,
      kind: "failed",
      agent: "Agent Pack Builder",
      action: "Agent 生成已停止",
      detail: message,
      progress: 1,
      activeUnits: 0,
      packId: request?.id || null
    });
  } finally {
    activeAgentPackBuilds.delete(taskId);
  }
}

async function startAgentPackBuild(owner, request) {
  const taskId = `agent-pack-${crypto.randomUUID().replaceAll("-", "").slice(0, 12)}`;
  const title = `构建 Agent · ${String(request?.name || "未命名 Agent").slice(0, 52)}`;
  const prompt = [
    `根据已经通过信任审查的编排草案，生成并验证 Agent Pack：${request?.name || request?.id}`,
    `目标：${request?.objective || "完成已确认的 Agent 目标"}`,
    `主交付物：${request?.primaryArtifact || "未声明"}`,
    "只有完整契约、引导、工作流和标准体检全部通过后才允许注册。"
  ].join("\n");
  const task = await bridge.call("start_task", {
    taskId,
    title,
    prompt,
    provider: request?.orchestration?.modelProvider || "nova",
    model: request?.orchestration?.model || "agent-pack-compiler",
    // This task creates the target Pack; binding that not-yet-created Pack as
    // the task runtime would make start_task reject it as missing or disabled.
    agentPackId: null,
    workspaceRoot: request?.workspaceRoot || app.getPath("userData"),
    mode: "Build"
  });
  const buildPromise = new Promise((resolve) => setImmediate(resolve))
    .then(() => executeAgentPackBuild(owner, taskId, request));
  activeAgentPackBuilds.set(taskId, buildPromise);
  void buildPromise;
  return task;
}

function modelSourceId(provider, endpoint) {
  if (provider === "openai") return "openai";
  if (provider === "deepseek") return "deepseek";
  if (provider === "kimi") return "moonshot";
  try {
    const authority = new URL(endpoint).host.toLowerCase();
    return provider === "ollama" ? `local:${authority}` : `host:${authority}`;
  } catch {
    return `${provider}:unknown`;
  }
}

function chooseIndependentReviewer(primaryProvider, primaryConnection) {
  const primarySource = modelSourceId(
    primaryProvider,
    primaryConnection?.endpoint || ""
  );
  const preference = ["openai", "deepseek", "kimi", "ollama", "custom"];
  return preference
    .filter((candidate) => candidate !== primaryProvider)
    .map((candidate) => [candidate, modelConnections.get(candidate)])
    .find(
      ([candidate, connection]) =>
        connection &&
        modelSourceId(candidate, connection.endpoint || "") !== primarySource
    );
}

async function runModel(request) {
  const provider = String(request?.provider || "deepseek");
  validateProvider(provider);
  const connection = modelConnections.get(provider);
  if (!connection) throw new Error(`请先连接 ${provider.toUpperCase()} 模型。`);
  const apiKey = connection.apiKey || "";

  const defaults = modelDefaults(provider);
  const model = String(request?.model || defaults.model).slice(0, 120);
  const messages = normalizeMessages(request?.messages);
  const attachments = readApprovedAttachments(request?.attachments);
  const taskTitle = messages.findLast((item) => item.role === "user")?.content.slice(0, 80);
  const prompt = messages[messages.length - 1].content;
  const workspaceRoot = path.resolve(String(request?.workspace || process.cwd()));
  const workspaceBefore = snapshotWorkspace(workspaceRoot);
  const runId = String(request?.runId || crypto.randomUUID());
  let taskId;
  let taskSettled = false;
  activeRuns.set(runId, null);

  const settleCancelledTask = async () => {
    if (!taskId || taskSettled) return;
    try {
      await bridge.call("cancel_task", { taskId });
    } catch {
      // The runtime may already have observed cancellation. The partial
      // completion below is the authoritative task-lease cleanup.
    }
    try {
      await bridge.call("complete_task", {
        taskId,
        succeeded: true,
        outcome: "partial",
        detail: "用户已安全停止；上下文、已完成文件和交付结果均已保留，可继续任务。"
      });
      taskSettled = true;
    } catch {
      // Recovery boot can still reclaim the lease after an abnormal bridge exit.
    }
  };

  try {
    const task = await bridge.call("start_task", {
      taskId: request?.taskId || null,
      title: taskTitle || "NOVA 新任务",
      prompt,
      provider,
      model,
      agentPackId: request?.agentPackId || null,
      workspaceRoot,
      mode: request?.executionMode || "Build"
    });
    taskId = task.id || task.taskId;
    activeRuns.set(runId, taskId);
    publishGatewayHook("task.started", {
      taskId,
      title: taskTitle || "NOVA 任务",
      provider,
      model,
      agentPackId: request?.agentPackId || null,
      executionMode: request?.executionMode || "Build"
    });
    if (cancelledRuns.has(runId)) {
      await settleCancelledTask();
      throw new Error("NOVA_RUN_CANCELLED");
    }
    const result = await bridge.call("run_agent", {
      taskId,
      prompt,
      apiKey,
      endpoint: connection.endpoint,
      approvalMode: request?.approvalMode || "readOnly",
      conversation: messages,
      attachments: attachments.map((item) => ({
        id: item.id,
        path: item.path,
        kind: item.kind,
        mime: item.mime || null
      }))
    });
    // Cancellation can arrive just as the provider finishes a response. Do not
    // let that stale response win the race and become a delivery after the user
    // has already redirected the task.
    if (cancelledRuns.has(runId)) {
      await settleCancelledTask();
      throw new Error("NOVA_RUN_CANCELLED");
    }
    const output = String(result.output || "");
    const artifacts = collectDeliveryArtifacts(
      workspaceRoot,
      workspaceBefore,
      snapshotWorkspace(workspaceRoot),
      output
    );
    const requiresWorkspaceMutation = Boolean(result.requiresWorkspaceMutation);
    // A tool claiming it wrote something is not proof of delivery. Completion
    // requires at least one file that AgentOS can observe and present to the user.
    const hasWorkspaceChanges = artifacts.length > 0;
    const hasValidationRun = Number(result.validationRuns || 0) > 0;
    let deliveryStatus =
      requiresWorkspaceMutation && (!hasWorkspaceChanges || !hasValidationRun)
        ? "PARTIAL"
        : requiresWorkspaceMutation
          ? "EVIDENCED"
          : "READY";
    let deliverySummary =
      deliveryStatus === "PARTIAL"
        ? !hasWorkspaceChanges
          ? "任务需要修改工程，但本轮没有产生真实文件写入。结果已保留，未标记为完成。"
          : "文件已经修改，但缺少可识别的构建或测试证据。结果已保留，等待继续验证。"
        : deliveryStatus === "EVIDENCED"
          ? "已检测到真实文件写入和本机验证步骤。"
          : "本轮不要求工作区变更，结果已生成。";
    let verification = null;

    if (request?.crossModelReview === true) {
      const reviewer = chooseIndependentReviewer(provider, connection);
      if (!reviewer) {
        verification = {
          provider: "",
          model: "",
          verdict: "SKIPPED",
          confidence: 0,
          summary: "没有找到来自不同模型源的第二个已连接模型，本轮未进行异构复核。",
          details: ""
        };
        if (requiresWorkspaceMutation) {
          deliveryStatus = "PARTIAL";
          deliverySummary =
            "本轮要求双模型复核，但没有可用的独立模型源；工程任务已保留为待继续状态。";
        }
      } else {
        const [reviewProvider, reviewConnection] = reviewer;
        try {
          verification = await bridge.call("verify_result", {
            taskId,
            originalGoal: prompt,
            primaryOutput: output,
            provider: reviewProvider,
            model: reviewConnection.model,
            apiKey: reviewConnection.apiKey || "",
            endpoint: reviewConnection.endpoint
          });
          if (verification?.verdict === "PASS" && deliveryStatus !== "PARTIAL") {
            deliveryStatus = "PROVEN";
            deliverySummary =
              "主模型结果已通过不同模型源的独立只读复核，并保留可查看的审查结论。";
          } else if (
            ["CONCERNS", "FAIL", "UNAVAILABLE"].includes(
              String(verification?.verdict || "")
            )
          ) {
            deliveryStatus = "PARTIAL";
            deliverySummary =
              "独立复核未能确认结果可靠，本轮保留为待继续状态，不冒充已经完成。";
          }
        } catch (reviewError) {
          verification = {
            provider: reviewProvider,
            model: reviewConnection.model,
            verdict: "UNAVAILABLE",
            confidence: 0,
            summary: `独立复核暂时不可用：${safeError(reviewError)}`,
            details: ""
          };
          if (requiresWorkspaceMutation) {
            deliveryStatus = "PARTIAL";
            deliverySummary =
              "主执行结果已保留，但异构复核未完成；工程任务不会因此冒充已验证。";
          }
        }
      }
    }
    const partial = deliveryStatus === "PARTIAL";
    const verificationLine = verification
      ? `\n- 独立审查：${verification.provider || "未启用"}${
          verification.model ? ` · ${verification.model}` : ""
        } · ${verification.verdict} · 置信度 ${verification.confidence || 0}%`
      : "";
    const persistedDraft =
      `${output}\n\n---\n### NOVA 交付护照\n` +
      `- 状态：${deliveryStatus}\n` +
      `- 结论：${deliverySummary}\n` +
      `- 文件写入：${hasWorkspaceChanges ? "有" : "无"}\n` +
      `- 本机验证步骤：${Number(result.validationRuns || 0)}` +
      verificationLine;
    const delivery = {
      schemaVersion: "1.0",
      deliveryId: `delivery-${crypto.randomUUID().replaceAll("-", "")}`,
      revision: 1,
      status: deliveryStatus,
      title: taskTitle || "本轮任务成果",
      outcome: deliverySummary,
      summary: deliverySummary,
      requiresWorkspaceMutation,
      hasWorkspaceChanges,
      validationRuns: Number(result.validationRuns || 0),
      artifacts,
      evidence: [
        ...(hasWorkspaceChanges ? [`检测到 ${artifacts.length} 个真实交付文件`] : []),
        ...(hasValidationRun ? [`完成 ${Number(result.validationRuns || 0)} 项本机验证`] : []),
        ...(verification?.verdict === "PASS" ? ["独立模型复核通过"] : [])
      ],
      incomplete: deliveryStatus === "PARTIAL" ? [deliverySummary] : [],
      nextActions: deliveryStatus === "PARTIAL"
        ? ["提交反馈并在当前任务中继续修复"]
        : ["审查交付物并确认，或提交修改意见"],
      outputFormat: {
        contract: "nova.delivery/1.0",
        sections: ["outcome", "artifacts", "evidence", "incomplete", "nextActions"],
        artifactManifestRequired: true
      },
      agentPackId: request?.agentPackId || null,
      reviewState: "unreviewed"
    };
    await bridge.call("complete_task", {
      taskId,
      succeeded: true,
      outcome: partial ? "partial" : "completed",
      outputCharacters: persistedDraft.length,
      detail: `${deliveryStatus} · ${deliverySummary} · ${result.toolCalls || 0} 次工具调用 · ${result.mutatingToolCalls || 0} 次写操作`,
      draft: persistedDraft,
      delivery
    });
    taskSettled = true;
    for (const artifact of artifacts) {
      publishGatewayHook("artifact.created", {
        taskId,
        artifact: sanitizeGatewayArtifact(artifact)
      });
    }
    publishGatewayHook("delivery.ready", {
      taskId,
      delivery: {
        deliveryId: delivery.deliveryId,
        revision: delivery.revision,
        status: delivery.status,
        title: delivery.title,
        outcome: delivery.outcome,
        reviewState: delivery.reviewState,
        artifactCount: delivery.artifacts.length,
        artifacts: delivery.artifacts.map(sanitizeGatewayArtifact)
      }
    });
    return {
      taskId,
      output,
      toolCalls: result.toolCalls || 0,
      mutatingToolCalls: result.mutatingToolCalls || 0,
      verification,
      delivery
    };
  } catch (error) {
    if (cancelledRuns.delete(runId)) {
      await settleCancelledTask();
      throw new Error("NOVA_RUN_CANCELLED");
    }
    if (taskId && !taskSettled) {
      try {
        await bridge.call("complete_task", {
          taskId,
          succeeded: false,
          detail: safeError(error)
        });
        taskSettled = true;
      } catch {
        // The original model error remains the useful user-facing failure.
      }
    }
    throw new Error(safeError(error));
  } finally {
    activeRuns.delete(runId);
  }
}

const deliveryIgnoredDirectories = new Set([
  ".git", ".svn", ".hg", ".nova", "node_modules", "bin", "obj", "dist", "build",
  ".next", ".cache", ".pytest_cache", "coverage", "packages"
]);

function snapshotWorkspace(root) {
  const files = new Map();
  if (!root || !fs.existsSync(root)) return files;
  const pending = [root];
  let visited = 0;
  while (pending.length && visited < 12000) {
    const directory = pending.pop();
    let entries;
    try {
      entries = fs.readdirSync(directory, { withFileTypes: true });
    } catch {
      continue;
    }
    for (const entry of entries) {
      if (visited++ >= 12000) break;
      if (entry.isSymbolicLink()) continue;
      const fullPath = path.join(directory, entry.name);
      if (entry.isDirectory()) {
        if (!deliveryIgnoredDirectories.has(entry.name.toLowerCase())) pending.push(fullPath);
        continue;
      }
      if (!entry.isFile()) continue;
      try {
        const stat = fs.statSync(fullPath);
        files.set(fullPath.toLowerCase(), {
          path: fullPath,
          size: stat.size,
          mtimeMs: stat.mtimeMs
        });
      } catch {
        // Files may be atomically replaced while a tool is running.
      }
    }
  }
  return files;
}

function artifactKind(extension) {
  if ([".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg"].includes(extension)) return "image";
  if (extension === ".pdf") return "pdf";
  if ([".doc", ".docx", ".docm", ".dotx", ".dotm"].includes(extension)) return "word";
  if ([".xlsx", ".xls", ".ods"].includes(extension)) return "spreadsheet";
  if ([".pptx", ".ppt", ".odp"].includes(extension)) return "presentation";
  if ([".md", ".txt", ".json", ".csv", ".html", ".xml", ".yaml", ".yml", ".log"].includes(extension)) return "document";
  return "file";
}

function deliveryArtifactPriority(root, filePath) {
  const relative = path.relative(root, filePath).replaceAll("\\", "/").toLowerCase();
  const base = path.basename(filePath).toLowerCase();
  let score = 0;

  if (/^(交付|成果|报告|deliverables?|reports?)\//i.test(relative)) score += 100;
  if (/(入口|总览|摘要|简报|报告|结论|建议|方案|清单|readme|summary|report|result)/i.test(base)) score += 55;
  if (/^(输入|解析结果|画像|审查|工具|input|raw|analysis|audit|tools?)\//i.test(relative)) score -= 35;
  if (/(proof-of-done|test_|\.test\.|\.spec\.|sha256|raw_texts)/i.test(relative)) score -= 60;

  return score;
}

function collectDeliveryArtifacts(root, before, after, output) {
  const candidates = new Map();
  for (const [key, file] of after) {
    const previous = before.get(key);
    if (!previous || previous.size !== file.size || previous.mtimeMs !== file.mtimeMs) {
      candidates.set(key, file);
    }
  }
  const marker = /\[\[NOVA_ARTIFACT\|([^|\]]+)\|([^\]]+)\]\]/g;
  for (const match of String(output || "").matchAll(marker)) {
    const declared = path.resolve(root, match[2].trim());
    if (!isWithinRoot(declared, root) || !fs.existsSync(declared)) continue;
    let stat;
    try {
      stat = fs.statSync(declared);
    } catch {
      // A declared artifact can disappear or become temporarily locked after the
      // model returns. That must not turn an otherwise completed task into a failure.
      continue;
    }
    if (!stat.isFile()) continue;
    candidates.set(declared.toLowerCase(), {
      path: declared,
      size: stat.size,
      mtimeMs: stat.mtimeMs,
      label: match[1].trim()
    });
  }
  return [...candidates.values()]
    .map((file) => ({
      ...file,
      deliveryPriority: deliveryArtifactPriority(root, file.path)
    }))
    .sort((a, b) => b.deliveryPriority - a.deliveryPriority || b.mtimeMs - a.mtimeMs)
    .slice(0, 80)
    .map((file, index) => {
      const extension = path.extname(file.path).toLowerCase();
      return {
        id: `artifact-${index + 1}-${crypto.createHash("sha1").update(file.path).digest("hex").slice(0, 10)}`,
        title: file.label || path.basename(file.path),
        path: file.path,
        relativePath: path.relative(root, file.path),
        kind: artifactKind(extension),
        mediaType: extension.slice(1) || "file",
        size: file.size,
        modifiedAt: new Date(file.mtimeMs).toISOString(),
        role: file.deliveryPriority >= 40
          ? "primary"
          : file.deliveryPriority <= -45
            ? "evidence"
            : "supporting",
        previewable: true
      };
    });
}

function normalizedRoot(value) {
  if (!value) return null;
  return path.resolve(String(value)).replace(/[\\/]+$/, "").toLowerCase();
}

function rememberWorkspace(value) {
  const normalized = normalizedRoot(value);
  if (normalized) approvedWorkspaceRoots.add(normalized);
}

function isWithinRoot(candidate, root) {
  const relative = path.relative(root, candidate);
  return relative === "" || (!relative.startsWith("..") && !path.isAbsolute(relative));
}

async function readDeliveryArtifact(request) {
  const requestedPath = path.resolve(String(request?.path || ""));
  if (!requestedPath || !fs.existsSync(requestedPath)) {
    throw new Error("交付文件不存在，可能已被移动或删除。");
  }

  const outputRoot = path.resolve(
    process.env.LOCALAPPDATA || app.getPath("userData"),
    "NOVA",
    "outputs"
  );
  const allowedRoots = [outputRoot];
  const workspace = request?.workspace ? path.resolve(String(request.workspace)) : null;
  if (workspace && approvedWorkspaceRoots.has(normalizedRoot(workspace))) {
    allowedRoots.push(workspace);
  }
  if (!allowedRoots.some((root) => isWithinRoot(requestedPath, root))) {
    throw new Error("只能在已授权工作区或 NOVA 交付目录内审查文件。");
  }

  const extension = path.extname(requestedPath).toLowerCase();
  const supported = new Set([
    ".md", ".txt", ".json", ".csv", ".html", ".xml", ".yaml", ".yml",
    ".js", ".jsx", ".ts", ".tsx", ".css", ".py", ".cs", ".sql", ".log"
  ]);
  const imageExtensions = new Set([".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg"]);
  const documentExtensions = new Set([".pdf", ".doc", ".docx", ".docm", ".dotx", ".dotm"]);
  if (imageExtensions.has(extension)) {
    const stat = fs.statSync(requestedPath);
    const mime = extension === ".svg"
      ? "image/svg+xml"
      : `image/${extension === ".jpg" ? "jpeg" : extension.slice(1)}`;
    return {
      path: requestedPath,
      name: path.basename(requestedPath),
      size: stat.size,
      truncated: false,
      kind: "image",
      language: "image",
      content: `data:${mime};base64,${fs.readFileSync(requestedPath).toString("base64")}`
    };
  }
  if (documentExtensions.has(extension)) {
    const stat = fs.statSync(requestedPath);
    const extracted = await bridge.call("extract_delivery_document", { path: requestedPath });
    return {
      path: requestedPath,
      name: path.basename(requestedPath),
      size: stat.size,
      truncated: false,
      kind: "document",
      language: extracted.format || extension.slice(1),
      content: String(extracted.text || "（文档中没有可提取的文字，可使用系统应用查看原始版式。）")
    };
  }
  const externalExtensions = new Set([".xlsx", ".xls", ".ods", ".pptx", ".ppt", ".odp", ".zip"]);
  if (externalExtensions.has(extension)) {
    const stat = fs.statSync(requestedPath);
    return {
      path: requestedPath,
      name: path.basename(requestedPath),
      size: stat.size,
      truncated: false,
      kind: "external",
      language: extension.slice(1),
      content: "该交付物需要使用系统应用查看。你仍可在本窗口提交文件级反馈。"
    };
  }
  if (!supported.has(extension)) {
    throw new Error("该文件不是可在窗体内安全预览的文本格式。");
  }

  const maximumBytes = 600_000;
  const stat = fs.statSync(requestedPath);
  const buffer = fs.readFileSync(requestedPath);
  const truncated = buffer.length > maximumBytes;
  const content = buffer.subarray(0, maximumBytes).toString("utf8");
  return {
    path: requestedPath,
    name: path.basename(requestedPath),
    size: stat.size,
    truncated,
    kind: extension === ".md" ? "markdown" : "text",
    language: extension.slice(1) || "text",
    content
  };
}

function recommendedZoomFactor(window) {
  const display = screen.getDisplayMatching(window.getBounds());
  const { width, height } = display.workAreaSize;
  if (width >= 3000 || height >= 1800) return 1.2;
  if (width >= 2300 || height >= 1300) return 1.1;
  return 1;
}

function applyAdaptiveZoom(window) {
  if (!window || window.isDestroyed()) return;
  const next = manualZoomFactor ?? recommendedZoomFactor(window);
  window.webContents.setZoomFactor(next);
}

function scheduleAdaptiveZoom(window) {
  if (adaptiveZoomTimer) clearTimeout(adaptiveZoomTimer);
  adaptiveZoomTimer = setTimeout(() => applyAdaptiveZoom(window), 120);
}

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1560,
    height: 960,
    minWidth: 960,
    minHeight: 640,
    show: false,
    frame: false,
    backgroundColor: "#11120f",
    title: "NOVA AgentOS",
    webPreferences: {
      preload: path.join(__dirname, "preload.cjs"),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      webSecurity: true
    }
  });

  mainWindow.webContents.setWindowOpenHandler(() => ({ action: "deny" }));
  mainWindow.webContents.on("will-navigate", (event) => event.preventDefault());
  mainWindow.webContents.on("render-process-gone", (_event, details) => {
    recordRendererFailure("render-process-gone", details?.reason || "renderer exited", {
      exitCode: details?.exitCode,
      reason: details?.reason
    });
  });
  mainWindow.webContents.on("unresponsive", () => {
    recordRendererFailure("renderer-unresponsive", "The main renderer stopped responding.");
  });
  mainWindow.webContents.on("did-fail-load", (_event, errorCode, errorDescription, validatedURL) => {
    recordRendererFailure("did-fail-load", `${errorCode}: ${errorDescription}`, {
      url: String(validatedURL || "").slice(0, 500)
    });
  });
  mainWindow.webContents.on("before-input-event", (event, input) => {
    if (!(input.control || input.meta) || input.type !== "keyDown") return;
    const key = input.key.toLowerCase();
    if (!["+", "=", "-", "0"].includes(key)) return;
    event.preventDefault();
    const current = mainWindow.webContents.getZoomFactor();
    manualZoomFactor = key === "0"
      ? null
      : Math.min(1.5, Math.max(0.9, current + (key === "-" ? -0.1 : 0.1)));
    applyAdaptiveZoom(mainWindow);
  });
  mainWindow.on("resize", () => scheduleAdaptiveZoom(mainWindow));
  mainWindow.on("move", () => scheduleAdaptiveZoom(mainWindow));
  mainWindow.once("ready-to-show", () => {
    applyAdaptiveZoom(mainWindow);
    if (!isSmoke) mainWindow.show();
  });

  if (isDev) {
    mainWindow.loadURL("http://127.0.0.1:5173");
  } else {
    mainWindow.loadFile(path.join(__dirname, "..", "dist", "index.html"));
  }
}

function openKnowledgeWindow(workspace) {
  if (knowledgeWindow && !knowledgeWindow.isDestroyed()) {
    knowledgeWindow.show();
    knowledgeWindow.focus();
    return knowledgeWindow;
  }
  knowledgeWindow = new BrowserWindow({
    width: 1320,
    height: 840,
    minWidth: 920,
    minHeight: 640,
    show: false,
    frame: true,
    autoHideMenuBar: true,
    backgroundColor: "#11120f",
    title: "NOVA 知识地图",
    webPreferences: {
      preload: path.join(__dirname, "preload.cjs"),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      webSecurity: true
    }
  });
  knowledgeWindow.webContents.setWindowOpenHandler(() => ({ action: "deny" }));
  knowledgeWindow.webContents.on("will-navigate", (event) => event.preventDefault());
  knowledgeWindow.once("ready-to-show", () => knowledgeWindow?.show());
  knowledgeWindow.on("closed", () => {
    knowledgeWindow = null;
  });
  const query = { view: "knowledge", workspace: String(workspace || "") };
  if (isDev) {
    knowledgeWindow.loadURL(`http://127.0.0.1:5173/?${new URLSearchParams(query).toString()}`);
  } else {
    knowledgeWindow.loadFile(path.join(__dirname, "..", "dist", "index.html"), { query });
  }
  return knowledgeWindow;
}

function registerIpc() {
  ipcMain.handle("nova:boot", async () => {
    const kernel = await bridge.call("boot");
    return {
      kernel,
      appVersion: app.getVersion(),
      platform: process.platform,
      defaults: {
        openai: modelDefaults("openai"),
        deepseek: modelDefaults("deepseek"),
        kimi: modelDefaults("kimi"),
        ollama: modelDefaults("ollama"),
        custom: modelDefaults("custom")
      },
      modelConnections: publicModelConnections()
    };
  });
  ipcMain.handle("nova:report-renderer-error", (event, request) => {
    requireMainWindow(event);
    const message = [request?.message, request?.stack, request?.componentStack]
      .filter(Boolean)
      .join("\n")
      .slice(0, 12000);
    const logPath = recordRendererFailure(request?.source || "renderer", message);
    return { recorded: Boolean(logPath), logPath };
  });
  ipcMain.handle("nova:list-tasks", () => bridge.call("list_tasks"));
  ipcMain.handle("nova:list-archived-tasks", () => bridge.call("list_archived_tasks"));
  ipcMain.handle("nova:get-task", async (event, request) => {
    senderWindow(event);
    const detail = await bridge.call("get_task", { taskId: request?.taskId });
    rememberWorkspace(detail?.task?.workspaceRoot);
    for (const attachment of detail?.task?.attachments || []) {
      const attachmentPath = String(attachment?.path || "");
      if (!attachmentPath) continue;
      try {
        if (fs.statSync(attachmentPath).isFile()) approvedAttachments.add(path.resolve(attachmentPath));
      } catch {
        // A moved task attachment remains visible by name, but is not authorized
        // for preview or reuse until the user selects it again.
      }
    }
    return detail;
  });
  ipcMain.handle("nova:get-task-capsule", async (event, request) => {
    senderWindow(event);
    const capsule = await bridge.call("get_task_capsule", { taskId: request?.taskId });
    return sanitizeGatewayTaskCapsule(capsule);
  });
  ipcMain.handle("nova:get-context-budget", async (event, request) => {
    senderWindow(event);
    const result = await bridge.call("get_context_budget", request || {});
    if (result?.capsule) result.capsule = sanitizeGatewayTaskCapsule(result.capsule);
    return result;
  });
  ipcMain.handle("nova:archive-task", (event, request) => {
    senderWindow(event);
    return bridge.call("archive_task", { taskId: request?.taskId });
  });
  ipcMain.handle("nova:restore-task", (event, request) => {
    senderWindow(event);
    return bridge.call("restore_task", { taskId: request?.taskId });
  });
  ipcMain.handle("nova:delete-archived-task", (event, request) => {
    senderWindow(event);
    return bridge.call("delete_archived_task", { taskId: request?.taskId });
  });
  ipcMain.handle("nova:read-delivery-artifact", (event, request) => {
    senderWindow(event);
    return readDeliveryArtifact(request);
  });
  ipcMain.handle("nova:open-delivery-artifact", async (event, request) => {
    senderWindow(event);
    const requestedPath = path.resolve(String(request?.path || ""));
    const workspace = request?.workspace ? path.resolve(String(request.workspace)) : null;
    const outputRoot = path.resolve(process.env.LOCALAPPDATA || app.getPath("userData"), "NOVA", "outputs");
    const allowed = isWithinRoot(requestedPath, outputRoot)
      || Boolean(workspace && approvedWorkspaceRoots.has(normalizedRoot(workspace)) && isWithinRoot(requestedPath, workspace));
    if (!allowed) {
      throw new Error("只能打开当前已授权工作区内的交付文件。");
    }
    const result = await shell.openPath(requestedPath);
    if (result) throw new Error(result);
    return { opened: true };
  });
  ipcMain.handle("nova:reveal-delivery-artifact", (event, request) => {
    senderWindow(event);
    const requestedPath = path.resolve(String(request?.path || ""));
    const workspace = request?.workspace ? path.resolve(String(request.workspace)) : null;
    const outputRoot = path.resolve(process.env.LOCALAPPDATA || app.getPath("userData"), "NOVA", "outputs");
    const allowed = isWithinRoot(requestedPath, outputRoot)
      || Boolean(workspace && approvedWorkspaceRoots.has(normalizedRoot(workspace)) && isWithinRoot(requestedPath, workspace));
    if (!allowed) {
      throw new Error("只能定位当前已授权工作区内的交付文件。");
    }
    shell.showItemInFolder(requestedPath);
    return { revealed: true };
  });
  ipcMain.handle("nova:submit-delivery-feedback", (event, request) => {
    senderWindow(event);
    return bridge.call("submit_delivery_feedback", request || {});
  });
  ipcMain.handle("nova:accept-delivery", (event, request) => {
    senderWindow(event);
    return bridge.call("accept_delivery", request || {});
  });
  ipcMain.handle("nova:select-workspace", async (event) => {
    const result = await dialog.showOpenDialog(senderWindow(event), {
      title: "选择 NOVA 工作区",
      properties: ["openDirectory", "createDirectory"]
    });
    if (result.canceled) return null;
    rememberWorkspace(result.filePaths[0]);
    return result.filePaths[0];
  });
  ipcMain.handle("nova:check-workspace-access", (event, request) => {
    senderWindow(event);
    const rawPath = String(request?.workspace || "").trim();
    if (!rawPath) {
      return { exists: false, readable: false, writable: false, reason: "尚未选择任务文件夹" };
    }
    const requestedPath = path.resolve(rawPath);
    if (!fs.existsSync(requestedPath) || !fs.statSync(requestedPath).isDirectory()) {
      return { exists: false, readable: false, writable: false, reason: "工作区不存在或已经移动" };
    }
    let readable = false;
    let writable = false;
    let reason = "";
    try {
      fs.accessSync(requestedPath, fs.constants.R_OK);
      readable = true;
    } catch (error) {
      reason = safeError(error);
    }
    let probePath = "";
    try {
      fs.accessSync(requestedPath, fs.constants.W_OK);
      probePath = path.join(
        requestedPath,
        `.nova-write-check-${process.pid}-${Date.now()}-${crypto.randomBytes(3).toString("hex")}.tmp`
      );
      fs.writeFileSync(probePath, "NOVA workspace write check", { flag: "wx" });
      writable = true;
    } catch (error) {
      reason = reason || safeError(error);
    } finally {
      if (probePath && fs.existsSync(probePath)) {
        try {
          fs.unlinkSync(probePath);
        } catch {
          // A successful create/write probe is authoritative. Antivirus may hold
          // the temporary file briefly; cleanup failure must not mark the folder read-only.
        }
      }
    }
    return { exists: true, readable, writable, reason };
  });
  ipcMain.handle("nova:select-attachments", async (event) => {
    const result = await dialog.showOpenDialog(senderWindow(event), {
      title: "添加任务附件",
      properties: ["openFile", "multiSelections"],
      filters: [
        {
          name: "NOVA 支持的文件",
          extensions: [
            "png",
            "jpg",
            "jpeg",
            "webp",
            "pdf",
            "doc",
            "docx",
            "docm",
            "dotx",
            "dotm",
            "txt",
            "md",
            "json",
            "js",
            "jsx",
            "ts",
            "tsx",
            "css",
            "html",
            "py",
            "cs",
            "xml",
            "yaml",
            "yml"
          ]
        }
      ]
    });
    if (result.canceled) return [];
    return result.filePaths.slice(0, 6).map((filePath) => {
      approvedAttachments.add(path.resolve(filePath));
      const stat = fs.statSync(filePath);
      return {
        id: crypto.randomUUID(),
        name: path.basename(filePath),
        path: filePath,
        size: stat.size,
        kind: contentTypeFor(filePath)
          ? "image"
          : documentTypeFor(filePath)
            ? "document"
            : "text"
      };
    });
  });
  ipcMain.handle("nova:preview-attachment", async (event, request) => {
    senderWindow(event);
    const requestedPath = path.resolve(String(request?.path || ""));
    if (!approvedAttachments.has(requestedPath)) {
      throw new Error("该图片尚未获得本任务的预览授权。");
    }
    const mediaType = contentTypeFor(requestedPath);
    if (!mediaType) throw new Error("该附件不是支持预览的图片。");
    const stat = fs.statSync(requestedPath);
    if (!stat.isFile()) throw new Error("图片不存在或已经移动。");
    if (stat.size > 10 * 1024 * 1024) throw new Error("单张图片预览不能超过 10 MB。");
    return {
      name: path.basename(requestedPath),
      mediaType,
      dataUrl: `data:${mediaType};base64,${fs.readFileSync(requestedPath).toString("base64")}`
    };
  });
  ipcMain.handle("nova:configure-model", async (event, configuration) => {
    senderWindow(event);
    const requestedProvider = String(configuration?.provider || "");
    const existingConnection = modelConnections.get(requestedProvider);
    const normalized = normalizeModelConfiguration({
      ...configuration,
      apiKey: String(configuration?.apiKey || "").trim() || existingConnection?.apiKey || ""
    });
    const discoveredModels = await probeModelConnection(normalized);
    if (normalized.provider === "ollama") {
      if (!discoveredModels.length) {
        throw new Error(
          `Ollama 服务已连接，但没有发现已安装模型。请先运行 ollama pull ${normalized.model}`
        );
      }
      const latestAlias = normalized.model.includes(":")
        ? normalized.model
        : `${normalized.model}:latest`;
      if (!discoveredModels.includes(normalized.model) && discoveredModels.includes(latestAlias)) {
        normalized.model = latestAlias;
      } else if (!discoveredModels.includes(normalized.model)) {
        throw new Error(
          `Ollama 中未找到模型 ${normalized.model}。当前可用：${discoveredModels.join("、")}`
        );
      }
    }
    modelConnections.set(normalized.provider, normalized);
    const storage = persistModelConnections();
    return {
      provider: normalized.provider,
      connected: true,
      model: normalized.model,
      endpoint: normalized.endpoint,
      discoveredModels,
      persisted: storage.persisted,
      warning: storage.warning
    };
  });
  ipcMain.handle("nova:run-model", (event, request) => {
    senderWindow(event);
    return runModel(request);
  });
  ipcMain.handle("nova:cancel-model", async (event, request) => {
    senderWindow(event);
    const runId = String(request?.runId || "");
    if (!runId || !activeRuns.has(runId)) return { cancelled: false };
    cancelledRuns.add(runId);
    const taskId = activeRuns.get(runId);
    if (!taskId) return { cancelled: true, stopping: true };
    const result = await bridge.call("cancel_task", { taskId });
    return { ...result, cancelled: true, stopping: true };
  });
  ipcMain.handle("nova:resolve-tool-approval", (event, request) => {
    senderWindow(event);
    return bridge.call("resolve_tool_approval", {
      approvalId: request?.approvalId,
      approved: request?.approved === true,
      rememberForTask: request?.rememberForTask === true,
      rememberForWorkspace: request?.rememberForWorkspace === true
    });
  });
  ipcMain.handle("nova:list-workspace-permissions", (event) => {
    senderWindow(event);
    return bridge.call("list_workspace_permissions");
  });
  ipcMain.handle("nova:revoke-workspace-permission", (event, request) => {
    senderWindow(event);
    return bridge.call("revoke_workspace_permission", { id: request?.id });
  });
  ipcMain.handle("nova:clear-workspace-permissions", (event, request) => {
    senderWindow(event);
    return bridge.call("clear_workspace_permissions", { workspace: request?.workspace || null });
  });
  ipcMain.handle("nova:list-capabilities", (event, request) => {
    senderWindow(event);
    return bridge.call("list_capabilities", {
      workspaceRoot: request?.workspace || process.cwd()
    });
  });
  ipcMain.handle("nova:set-mcp-enabled", async (event, request) => {
    const owner = senderWindow(event);
    if (request?.enabled) {
      const confirmation = await dialog.showMessageBox(owner, {
        type: "warning",
        title: "启用这个 MCP 连接？",
        message: `允许 NOVA 使用 ${String(request?.name || "该 MCP")}？`,
        detail:
          "启用后，任务可按权限策略启动本地进程或连接远程服务，并可能访问对应账号数据。工具调用仍受任务权限审查；你可以随时在扩展坞停用。",
        buttons: ["取消", "本次确认启用"],
        defaultId: 0,
        cancelId: 0,
        noLink: true
      });
      if (confirmation.response !== 1) {
        return { canceled: true, enabled: false };
      }
    }
    return bridge.call("set_mcp_enabled", request);
  });
  ipcMain.handle("nova:set-skill-enabled", (event, request) => {
    senderWindow(event);
    return bridge.call("set_skill_enabled", request);
  });
  ipcMain.handle("nova:install-capability", (event, request) => {
    senderWindow(event);
    return bridge.call("install_capability", {
      id: request?.id,
      workspaceRoot: request?.workspace || process.cwd()
    });
  });
  ipcMain.handle("nova:authorize-agent-capabilities", async (event, request) => {
    const owner = senderWindow(event);
    const packId = String(request?.packId || "");
    const workspaceRoot = request?.workspace || process.cwd();
    if (!packId) throw new Error("没有选择需要补全能力的 Agent。");
    let report = await bridge.call("get_agent_pack_capabilities", {
      id: packId,
      workspaceRoot
    });
    const required = Array.isArray(report?.items)
      ? report.items.filter((item) => item.required === true && item.state !== "ready")
      : [];
    if (!required.length) return { canceled: false, report, changed: [] };
    const automatic = required.filter((item) =>
      (item.state === "available" && item.catalogId)
      || (item.state === "registered-disabled" && item.matchedId)
    );
    const unresolved = required.filter((item) => !automatic.includes(item));
    if (!automatic.length) return { canceled: false, report, changed: [], unresolved };
    const preview = automatic.map((item) =>
      `• ${item.name}：${item.state === "available" ? "登记本地能力" : "启用已登记能力"}\n  ${item.reason || "Agent 契约要求"}`
    ).join("\n");
    const confirmation = await dialog.showMessageBox(owner, {
      type: "warning",
      title: "允许 NOVA 补全必要能力？",
      message: `这个 Agent 缺少 ${automatic.length} 项执行所必需的能力。`,
      detail: `${preview}\n\nNOVA 只处理上面列出的能力，不会安装未列出的软件。MCP 启用后可能访问对应服务，真实工具调用仍受任务权限策略约束。`,
      buttons: ["不用 MCP 继续", "允许补全必要能力"],
      defaultId: 0,
      cancelId: 0,
      noLink: true
    });
    if (confirmation.response !== 1) return { canceled: true, report, changed: [], unresolved };
    const changed = [];
    for (const item of automatic) {
      if (item.state === "available" && item.catalogId) {
        await bridge.call("install_capability", { id: item.catalogId, workspaceRoot });
        changed.push({ id: item.id, action: "installed" });
      }
      if (item.kind === "mcp") {
        const refreshed = await bridge.call("get_agent_pack_capabilities", {
          id: packId,
          workspaceRoot
        });
        const current = refreshed?.items?.find((candidate) => candidate.id === item.id);
        if (current?.matchedId) {
          await bridge.call("set_mcp_enabled", { name: current.matchedId, enabled: true });
          changed.push({ id: item.id, action: "enabled" });
        }
      } else {
        const refreshed = await bridge.call("get_agent_pack_capabilities", {
          id: packId,
          workspaceRoot
        });
        const current = refreshed?.items?.find((candidate) => candidate.id === item.id);
        if (current?.matchedId && current.state === "registered-disabled") {
          await bridge.call("set_skill_enabled", { id: current.matchedId, enabled: true });
          changed.push({ id: item.id, action: "enabled" });
        }
      }
    }
    report = await bridge.call("get_agent_pack_capabilities", { id: packId, workspaceRoot });
    return { canceled: false, report, changed, unresolved };
  });
  ipcMain.handle("nova:search-capability-store", (event, request) => {
    senderWindow(event);
    return bridge.call("search_capability_store", {
      kind: request?.kind || "all",
      query: request?.query || ""
    });
  });
  ipcMain.handle("nova:install-store-capability", async (event, request) => {
    senderWindow(event);
    const installed = await bridge.call("install_store_capability", { id: request?.id });
    if (request?.enable === true && installed?.kind === "mcp" && installed?.name) {
      await bridge.call("set_mcp_enabled", { name: installed.name, enabled: true });
      return { ...installed, enabled: true };
    }
    return installed;
  });
  ipcMain.handle("nova:discover-mcp", async (event, request) => {
    const owner = senderWindow(event);
    const workspaceRoot = request?.workspace || process.cwd();
    const sourceResult = await bridge.call("list_mcp_discovery_sources", {
      workspaceRoot
    });
    const sources = Array.isArray(sourceResult?.sources) ? sourceResult.sources : [];
    if (!sources.length) {
      return { canceled: false, candidates: [], scannedPaths: [], warnings: [] };
    }
    const preview = sources
      .slice(0, 8)
      .map((source) => `${source.product}: ${source.path}`)
      .join("\n");
    const confirmation = await dialog.showMessageBox(owner, {
      type: "question",
      title: "允许只读扫描本机 MCP 配置？",
      message: `NOVA 找到 ${sources.length} 个可扫描的配置文件。`,
      detail:
        `${preview}\n\n扫描不会启动进程、访问网络、修改原文件或复制明文密钥。`,
      buttons: ["取消", "允许本次扫描"],
      defaultId: 0,
      cancelId: 0,
      noLink: true
    });
    if (confirmation.response !== 1) {
      return { canceled: true, candidates: [], scannedPaths: [], warnings: [] };
    }
    const result = await bridge.call("discover_mcp", { workspaceRoot });
    return { canceled: false, ...result };
  });
  ipcMain.handle("nova:preview-mcp-config", (event, request) => {
    senderWindow(event);
    return bridge.call("preview_mcp_config", {
      workspaceRoot: request?.workspace || process.cwd(),
      configuration: request?.configuration,
      authorizationEnvironment: request?.authorizationEnvironment || null
    });
  });
  ipcMain.handle("nova:import-discovered-mcp", async (event, request) => {
    const owner = senderWindow(event);
    const candidates = Array.isArray(request?.candidates) ? request.candidates : [];
    const candidateIds = candidates
      .map((candidate) => String(candidate?.id || ""))
      .filter(Boolean)
      .slice(0, 32);
    if (!candidateIds.length) {
      throw new Error("请至少选择一个 MCP 连接。");
    }
    const preview = candidates
      .slice(0, 10)
      .map((candidate) => `• ${candidate.name}（${candidate.sourceProduct} / ${candidate.riskLabel}）`)
      .join("\n");
    const confirmation = await dialog.showMessageBox(owner, {
      type: "warning",
      title: "登记所选 MCP 连接？",
      message: `准备登记 ${candidateIds.length} 个连接。`,
      detail:
        `${preview}\n\n所有连接都会保持停用；不会启动、联网、下载或访问账号。`,
      buttons: ["取消", "登记并保持停用"],
      defaultId: 0,
      cancelId: 0,
      noLink: true
    });
    if (confirmation.response !== 1) {
      return { canceled: true, imported: [], skipped: [] };
    }
    const result = await bridge.call("import_discovered_mcp", { candidateIds });
    return { canceled: false, ...result };
  });
  ipcMain.handle("nova:list-agent-packs", (event) => {
    senderWindow(event);
    return bridge.call("list_agent_packs");
  });
  ipcMain.handle("nova:get-agent-pack", (event, request) => {
    senderWindow(event);
    return bridge.call("get_agent_pack", { id: request?.id });
  });
  ipcMain.handle("nova:list-agent-creation-templates", (event) => {
    senderWindow(event);
    return bridge.call("list_agent_creation_templates");
  });
  ipcMain.handle("nova:recommend-agent-pack", (event, request) => {
    senderWindow(event);
    return bridge.call("recommend_agent_pack", request || {});
  });
  ipcMain.handle("nova:prepare-agent-pack", async (event, request) => {
    senderWindow(event);
    return prepareAgentFoundryBrief(request || {});
  });
  ipcMain.handle("nova:get-agent-workshop-session", (event) => {
    senderWindow(event);
    return latestWorkshopSession();
  });
  ipcMain.handle("nova:orchestrate-agent-pack", async (event, request) => {
    const owner = senderWindow(event);
    const key = owner.webContents.id;
    const previous = activeWorkshopRuns.get(key);
    if (previous?.sessionId) {
      previous.abortController?.abort("superseded");
      updateWorkshopSession(previous.sessionId, { status: "cancelled" });
      await bridge.call("cancel_design_session", { sessionId: previous.sessionId }).catch(() => undefined);
    }
    return startAgentWorkshopSession(owner, request || {});
  });
  ipcMain.handle("nova:cancel-agent-pack-orchestration", async (event) => {
    const owner = senderWindow(event);
    const active = activeWorkshopRuns.get(owner.webContents.id);
    if (!active?.sessionId) return { canceled: false };
    active.abortController?.abort("user-cancelled");
    const result = await bridge.call("cancel_design_session", { sessionId: active.sessionId })
      .catch(() => ({ cancelled: true }));
    updateWorkshopSession(active.sessionId, { status: "cancelled" });
    return { canceled: Boolean(result?.cancelled), sessionId: active.sessionId };
  });
  ipcMain.handle("nova:create-agent-pack", async (event, request) => {
    const owner = senderWindow(event);
    const task = await startAgentPackBuild(owner, request || {});
    const completedDesign = readWorkshopSessions().find((session) =>
      session?.request?.id === request?.id && session?.status === "completed");
    if (completedDesign) updateWorkshopSession(completedDesign.id, { status: "building" });
    return { canceled: false, task };
  });
  ipcMain.handle("nova:list-agent-calibrations", (event, request) => {
    senderWindow(event);
    return bridge.call("list_agent_calibrations", { packId: request?.packId });
  });
  ipcMain.handle("nova:create-agent-calibration", (event, request) => {
    senderWindow(event);
    return bridge.call("create_agent_calibration", request || {});
  });
  ipcMain.handle("nova:rollback-agent-calibration", (event, request) => {
    senderWindow(event);
    return bridge.call("rollback_agent_calibration", {
      packId: request?.packId,
      patchId: request?.patchId
    });
  });
  ipcMain.handle("nova:get-agent-pack-capabilities", (event, request) => {
    senderWindow(event);
    return bridge.call("get_agent_pack_capabilities", {
      id: request?.id,
      workspaceRoot: request?.workspace || process.cwd()
    });
  });
  ipcMain.handle("nova:install-agent-pack", async (event) => {
    const owner = senderWindow(event);
    const sourceChoice = await dialog.showMessageBox(owner, {
      type: "question",
      title: "导入 NOVA Agent Pack",
      message: "你的 Agent Pack 是哪种形式？",
      detail: "可以选择包含 nova.industry.json 的文件夹，也可以直接选择 NOVA 导出的 ZIP 包。",
      buttons: ["取消", "选择文件夹", "选择 ZIP 包"],
      defaultId: 1,
      cancelId: 0,
      noLink: true
    });
    if (sourceChoice.response === 0) return { canceled: true, pack: null };
    const selectingZip = sourceChoice.response === 2;
    const result = await dialog.showOpenDialog(owner, {
      title: selectingZip ? "选择 Agent Pack ZIP" : "选择 Agent Pack 文件夹",
      message: selectingZip
        ? "选择 NOVA 导出的 .zip 文件"
        : "可以选择包目录，也可以选择包目录的上一层",
      properties: selectingZip ? ["openFile"] : ["openDirectory"],
      filters: selectingZip
        ? [{ name: "NOVA Agent Pack", extensions: ["zip"] }]
        : undefined
    });
    if (result.canceled || !result.filePaths[0]) {
      return { canceled: true, pack: null };
    }
    const confirmation = await dialog.showMessageBox(owner, {
      type: "question",
      title: "导入专业 Agent",
      message: `将“${path.basename(result.filePaths[0])}”导入 NOVA 吗？`,
      detail:
        "NOVA 会先检查包结构和路径安全，再复制角色、工作流、知识与交付模板。不会执行包内代码，也不会自动授予模型、网络或桌面权限。重复导入同一 Agent 会安全更新本地副本。",
      buttons: ["取消", "检查并导入"],
      defaultId: 0,
      cancelId: 0,
      noLink: true
    });
    if (confirmation.response !== 1) {
      return { canceled: true, pack: null };
    }
    const pack = await bridge.call("install_agent_pack", {
      sourceRoot: result.filePaths[0]
    });
    return { canceled: false, pack };
  });
  ipcMain.handle("nova:set-agent-pack-enabled", (event, request) => {
    senderWindow(event);
    return bridge.call("set_agent_pack_enabled", {
      id: request?.id,
      enabled: Boolean(request?.enabled)
    });
  });
  ipcMain.handle("nova:remove-agent-pack", async (event, request) => {
    const owner = senderWindow(event);
    const pack = await bridge.call("get_agent_pack", { id: request?.id });
    if (pack?.summary?.builtIn) throw new Error("内置 Agent Pack 受系统保护，不能移除。");
    if (pack?.summary?.enabled) throw new Error("请先停用此 Agent Pack，再将其移除。");
    const confirmation = await dialog.showMessageBox(owner, {
      type: "warning",
      title: "移除 Agent",
      message: `从本机移除“${pack?.summary?.name || request?.id}”？`,
      detail: "该 Agent 的声明、角色、工作流和引导文件将从本机能力仓移除。已有任务、聊天记录和工作区交付文件不会被删除。",
      buttons: ["取消", "确认移除"],
      defaultId: 0,
      cancelId: 0,
      noLink: true
    });
    if (confirmation.response !== 1) return { canceled: true, removed: false };
    const result = await bridge.call("remove_agent_pack", { id: request?.id });
    return { canceled: false, ...result };
  });
  ipcMain.handle("nova:desktop-snapshot", (event) => {
    senderWindow(event);
    return bridge.call("desktop_snapshot");
  });
  ipcMain.handle("nova:get-living-memory", (event) => {
    senderWindow(event);
    return bridge.call("get_living_memory");
  });
  ipcMain.handle("nova:get-knowledge-state", (event, request) => {
    knowledgeSenderWindow(event);
    return bridge.call("get_knowledge_state", {
      workspaceRoot: request?.workspace || null
    });
  });
  ipcMain.handle("nova:open-knowledge-window", (event, request) => {
    senderWindow(event);
    openKnowledgeWindow(request?.workspace || null);
    return { opened: true };
  });
  ipcMain.handle("nova:index-workspace-knowledge", (event, request) => {
    knowledgeSenderWindow(event);
    return bridge.call("index_workspace_knowledge", {
      workspaceRoot: request?.workspace
    });
  });
  ipcMain.handle("nova:search-workspace-knowledge", (event, request) => {
    knowledgeSenderWindow(event);
    return bridge.call("search_workspace_knowledge", {
      workspaceRoot: request?.workspace || null,
      query: request?.query,
      maximumResults: request?.maximumResults || 12
    });
  });
  ipcMain.handle("nova:delete-knowledge-node", async (event, request) => {
    const window = knowledgeSenderWindow(event);
    const confirmation = await dialog.showMessageBox(window, {
      type: "warning",
      title: "从知识图谱移除",
      message: `从知识图谱移除“${String(request?.label || "这条知识")}”？`,
      detail: "只会移除图谱节点与派生映射，不会删除原始任务、对话或工作区文件。",
      buttons: ["取消", "移除节点"],
      defaultId: 0,
      cancelId: 0,
      noLink: true
    });
    if (confirmation.response !== 1) return { deleted: false, canceled: true };
    return bridge.call("delete_knowledge_node", { nodeId: request?.nodeId });
  });
  ipcMain.handle("nova:review-knowledge-mapping", (event, request) => {
    knowledgeSenderWindow(event);
    return bridge.call("review_knowledge_mapping", {
      sourceId: request?.sourceId,
      targetId: request?.targetId,
      accepted: request?.accepted === true
    });
  });
  ipcMain.handle("nova:analyze-living-memory", (event) => {
    senderWindow(event);
    return bridge.call("analyze_living_memory");
  });
  ipcMain.handle("nova:set-habit-state", (event, request) => {
    senderWindow(event);
    return bridge.call("set_habit_state", {
      id: request?.id,
      state: request?.state
    });
  });
  ipcMain.handle("nova:distill-personal-skill", (event) => {
    senderWindow(event);
    return bridge.call("distill_personal_skill");
  });
  ipcMain.handle("nova:install-distilled-skill", (event, request) => {
    senderWindow(event);
    return bridge.call("install_distilled_skill", { id: request?.id });
  });
  ipcMain.handle("nova:get-evolution-lab", (event) => {
    senderWindow(event);
    return bridge.call("get_evolution_lab");
  });
  ipcMain.handle("nova:configure-evolution-lab", (event, request) => {
    senderWindow(event);
    return bridge.call("configure_evolution_lab", {
      enabled: Boolean(request?.enabled),
      scheduledDiscoveryEnabled: Boolean(request?.scheduledDiscoveryEnabled),
      maxTokensPerExperiment: Number(request?.maxTokensPerExperiment),
      monthlyTokenBudget: Number(request?.monthlyTokenBudget),
      maxExperimentsPerWeek: Number(request?.maxExperimentsPerWeek),
      maxModelRounds: Number(request?.maxModelRounds)
    });
  });
  ipcMain.handle("nova:propose-evolution", (event, request) => {
    senderWindow(event);
    return bridge.call("propose_evolution", {
      workspaceRoot: request?.workspaceRoot,
      objective: request?.objective
    });
  });
  ipcMain.handle("nova:prepare-evolution", async (event, request) => {
    const window = senderWindow(event);
    const confirmation = await dialog.showMessageBox(window, {
      type: "question",
      title: "准备插件实验",
      message: "允许 NOVA 建立一个不含核心源码的声明式插件沙箱吗？",
      detail:
        "只生成公开 Plugin SDK、manifest、SKILL.md 和审阅说明；不会读取、复制或修改 NOVA 核心源码，也不会调用模型。",
      buttons: ["取消", "建立插件沙箱"],
      defaultId: 0,
      cancelId: 0,
      noLink: true
    });
    if (confirmation.response !== 1) {
      return bridge.call("get_evolution_lab");
    }
    return bridge.call("prepare_evolution", { id: request?.id });
  });
  ipcMain.handle("nova:evaluate-evolution", async (event, request) => {
    const window = senderWindow(event);
    const confirmation = await dialog.showMessageBox(window, {
      type: "question",
      title: "验证插件实验",
      message: "允许 NOVA 检查插件差异与安全声明吗？",
      detail:
        "这是本地静态验证，不调用模型：禁止执行代码、依赖、网络、凭据和任何权限声明。",
      buttons: ["取消", "开始验证"],
      defaultId: 0,
      cancelId: 0,
      noLink: true
    });
    if (confirmation.response !== 1) {
      return bridge.call("get_evolution_lab");
    }
    return bridge.call("evaluate_evolution", { id: request?.id });
  });
  ipcMain.handle("nova:adopt-evolution", async (event, request) => {
    const window = senderWindow(event);
    const confirmation = await dialog.showMessageBox(window, {
      type: "warning",
      title: "安装进化插件",
      message: "把已验证的声明式插件安装到 NOVA 能力仓吗？",
      detail:
        "插件会作为可随时停用的 Skill 安装；不会修改 NOVA 核心程序、审批内核、凭据或更新器。",
      buttons: ["保留实验，不安装", "安装插件"],
      defaultId: 0,
      cancelId: 0,
      noLink: true
    });
    if (confirmation.response !== 1) {
      return bridge.call("get_evolution_lab");
    }
    return bridge.call("adopt_evolution", { id: request?.id });
  });
  ipcMain.handle("nova:reject-evolution", (event, request) => {
    senderWindow(event);
    return bridge.call("reject_evolution", { id: request?.id });
  });
  ipcMain.handle("nova:list-extension-profiles", (event) => {
    senderWindow(event);
    return readExtensionProfiles();
  });
  ipcMain.handle("nova:get-extension-gateway", (event) => {
    senderWindow(event);
    return gatewayStatus(true);
  });
  ipcMain.handle("nova:list-gateway-action-requests", (event) => {
    senderWindow(event);
    return readGatewayActionRequests().filter((item) => item.status === "pending");
  });
  ipcMain.handle("nova:resolve-gateway-action-request", (event, request) => {
    senderWindow(event);
    return resolveGatewayActionRequest(String(request?.id || ""), String(request?.status || ""));
  });
  ipcMain.handle("nova:set-extension-gateway-enabled", async (event, request) => {
    senderWindow(event);
    const enabled = request?.enabled === true;
    const profiles = readExtensionProfiles();
    await writeExtensionProfiles({ ...profiles, gateway: { enabled } });
    if (enabled) await startExtensionGateway();
    else await stopExtensionGateway();
    return gatewayStatus(true);
  });
  ipcMain.handle("nova:rotate-extension-gateway-token", (event) => {
    senderWindow(event);
    if (!extensionGateway.server?.listening) {
      throw new Error("服务接口尚未启动。");
    }
    extensionGateway.token = crypto.randomBytes(32).toString("base64url");
    for (const client of extensionGateway.clients) client.end();
    extensionGateway.clients.clear();
    return gatewayStatus(true);
  });
  ipcMain.handle("nova:copy-extension-gateway-token", (event) => {
    senderWindow(event);
    if (!extensionGateway.token) throw new Error("服务接口尚未启动。");
    clipboard.writeText(extensionGateway.token);
    return { copied: true };
  });
  ipcMain.handle("nova:copy-extension-gateway-url", (event) => {
    senderWindow(event);
    const status = gatewayStatus(false);
    if (!status.baseUrl) throw new Error("服务接口尚未启动。");
    clipboard.writeText(status.baseUrl);
    return { copied: true };
  });
  ipcMain.handle("nova:save-ssh-profile", async (event, request) => {
    senderWindow(event);
    const profile = normalizeSshProfile(request);
    const profiles = readExtensionProfiles();
    const ssh = Array.isArray(profiles.ssh) ? profiles.ssh : [];
    const index = ssh.findIndex((item) => item.id === profile.id);
    if (index >= 0) ssh[index] = profile;
    else ssh.push(profile);
    await writeExtensionProfiles({ ...profiles, ssh });
    return profile;
  });
  ipcMain.handle("nova:test-ssh-profile", (event, request) => {
    senderWindow(event);
    return testSsh(request);
  });
  ipcMain.handle("nova:save-cloud-adapter", async (event, request) => {
    senderWindow(event);
    const adapter = normalizeCloudAdapter(request);
    const profiles = readExtensionProfiles();
    const cloud = Array.isArray(profiles.cloud) ? profiles.cloud : [];
    const index = cloud.findIndex((item) => item.id === adapter.id);
    if (index >= 0) cloud[index] = adapter;
    else cloud.push(adapter);
    await writeExtensionProfiles({ ...profiles, cloud });
    return adapter;
  });

  registerWindowChannel("nova:window-minimize", (window) => window.minimize());
  registerWindowChannel("nova:window-toggle-maximize", (window) => {
    window.isMaximized() ? window.unmaximize() : window.maximize();
    return window.isMaximized();
  });
  registerWindowChannel("nova:window-close", (window) => window.close());
}

function createBridgeClient() {
  const client = new BridgeClient();
  client.onEvent = (eventName, payload) => {
    if (eventName === "agent_event" && mainWindow && !mainWindow.isDestroyed()) {
      mainWindow.webContents.send("nova:agent-event", payload);
    }
    if (eventName === "approval_request" && mainWindow && !mainWindow.isDestroyed()) {
      mainWindow.webContents.send("nova:tool-approval-request", payload);
    }
    if (eventName === "design_event") {
      acceptWorkshopRuntimeEvent(payload);
    }
    if (eventName === "context_event") {
      publishGatewayHook("context.compiled", payload);
      if (mainWindow && !mainWindow.isDestroyed()) {
        mainWindow.webContents.send("nova:context-event", payload);
      }
    }
    if (eventName === "evolution_event" && mainWindow && !mainWindow.isDestroyed()) {
      mainWindow.webContents.send("nova:evolution-event", payload);
    }
  };
  return client;
}

app.whenReady().then(async () => {
  if (!ownsInstance) return;
  bridge = createBridgeClient();
  restoreModelConnections();
  try {
    await startExtensionGateway(isSmoke);
  } catch (error) {
    console.error(`[Extension Gateway] ${safeError(error)}`);
  }
  session.defaultSession.setPermissionCheckHandler(() => false);
  session.defaultSession.setPermissionRequestHandler((_webContents, _permission, callback) =>
    callback(false)
  );
  registerIpc();

  if (isKnowledgeWindowSmoke) {
    try {
      await bridge.call("boot");
      const window = openKnowledgeWindow(null);
      await new Promise((resolve, reject) => {
        const timeout = setTimeout(() => reject(new Error("知识地图窗体加载超时。")), 20000);
        window.webContents.once("did-finish-load", () => {
          clearTimeout(timeout);
          resolve();
        });
      });
      await new Promise((resolve) => setTimeout(resolve, 1200));
      const bodyText = await window.webContents.executeJavaScript("document.body.innerText", true);
      if (!String(bodyText).includes("知识地图") || String(bodyText).includes("无效窗口调用")) {
        throw new Error("知识地图窗体未能通过独立 IPC 身份校验。");
      }
      console.log("NOVA_KNOWLEDGE_WINDOW_SMOKE_OK");
      window.destroy();
      bridge.stop();
      process.exit(0);
    } catch (error) {
      console.error(`NOVA_KNOWLEDGE_WINDOW_SMOKE_FAILED: ${safeError(error)}`);
      bridge.stop();
      process.exit(1);
    }
    return;
  }

  if (isCredentialSmoke) {
    try {
      if (!safeStorage.isEncryptionAvailable()) {
        throw new Error("系统安全存储不可用。");
      }
      modelConnections.set("deepseek", normalizeModelConfiguration({
        provider: "deepseek",
        model: modelDefaults("deepseek").model,
        endpoint: modelDefaults("deepseek").endpoint,
        apiKey: "sk-nova-credential-smoke-secret"
      }));
      const result = persistModelConnections();
      if (!result.persisted) throw new Error(result.warning);
      modelConnections.clear();
      restoreModelConnections();
      const restored = modelConnections.get("deepseek");
      const rawStore = fs.readFileSync(modelConnectionStorePath(), "utf8");
      if (restored?.apiKey !== "sk-nova-credential-smoke-secret"
          || rawStore.includes("sk-nova-credential-smoke-secret")) {
        throw new Error("模型凭据加密持久化契约失败。");
      }
      console.log("NOVA_MODEL_CREDENTIAL_SMOKE_OK");
      bridge.stop();
      try { fs.rmSync(credentialSmokeUserData, { recursive: true, force: true }); } catch { }
      process.exit(0);
    } catch (error) {
      console.error(`NOVA_MODEL_CREDENTIAL_SMOKE_FAILED: ${safeError(error)}`);
      bridge.stop();
      try { fs.rmSync(credentialSmokeUserData, { recursive: true, force: true }); } catch { }
      process.exit(1);
    }
    return;
  }

  if (isGatewaySmoke) {
    try {
      await smokeExtensionGateway(false);
      console.log("NOVA_EXTENSION_GATEWAY_SMOKE_OK");
      await stopExtensionGateway();
      process.exit(0);
    } catch (error) {
      console.error(`NOVA_EXTENSION_GATEWAY_SMOKE_FAILED: ${safeError(error)}`);
      await stopExtensionGateway();
      process.exit(1);
    }
    return;
  }

  if (isWorkshopRecoverySmoke) {
    try {
      const smokeRequest = {
        name: "跨境内容 Agent",
        category: "跨境电商",
        objective: "基于真实资料生成市场进入建议",
        primaryArtifact: "市场进入建议.md"
      };
      const smokeConnection = { provider: "local-smoke", model: "deterministic-recovery" };
      const repaired = coerceWorkshopDraft({
        summary: "模型已完成分析但漏掉审查闭环",
        roles: [{ id: "analyst", name: "市场分析师", responsibility: "分析市场证据", deliverables: [] }],
        workflow: [{ title: "分析资料", owner: "analyst", output: "analysis.md", acceptance: ["文件可打开"] }]
      }, smokeRequest, smokeConnection, "结构修复冒烟测试");
      const recovered = recoverWorkshopDraftFromStageOutputs(
        [{ agent: "行业架构师", action: "分析完成", detail: "已识别用户目标、资料边界与市场风险。" }],
        smokeRequest,
        smokeConnection
      );
      const primaryOutput = repaired.workflow.some((step) =>
        step.output.includes(smokeRequest.primaryArtifact));
      if (repaired.roles.length < 2
          || repaired.workflow.length < 3
          || repaired.workflow.at(-1)?.owner !== "independent-reviewer"
          || !primaryOutput
          || repaired.reviewVerdict !== "revise"
          || !recovered
          || recovered.reviewVerdict !== "revise") {
        throw new Error("Agent Workshop recovery contract failed.");
      }
      console.log("NOVA_WORKSHOP_RECOVERY_SMOKE_OK");
      bridge.stop();
      process.exit(0);
    } catch (error) {
      console.error(`NOVA_WORKSHOP_RECOVERY_SMOKE_FAILED: ${safeError(error)}`);
      bridge.stop();
      process.exit(1);
    }
    return;
  }

  if (isSmoke) {
    try {
      await bridge.call("boot");
      await bridge.call("health");
      await bridge.call("list_tasks");
      await smokeExtensionGateway();
      console.log("NOVA_ELECTRON_SMOKE_OK");
      bridge.stop();
      setTimeout(() => process.exit(0), 100);
    } catch (error) {
      console.error(`NOVA_ELECTRON_SMOKE_FAILED: ${safeError(error)}`);
      bridge.stop();
      setTimeout(() => process.exit(1), 100);
    }
    return;
  }

  createWindow();
});

app.on("second-instance", () => {
  if (!mainWindow) return;
  if (mainWindow.isMinimized()) mainWindow.restore();
  mainWindow.show();
  mainWindow.focus();
});

app.on("window-all-closed", () => {
  void stopExtensionGateway();
  bridge?.stop();
  if (process.platform !== "darwin") app.quit();
});

app.on("before-quit", () => {
  void stopExtensionGateway();
  bridge?.stop();
});
