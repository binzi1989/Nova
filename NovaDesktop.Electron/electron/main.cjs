const {
  app,
  BrowserWindow,
  dialog,
  ipcMain,
  screen,
  session
} = require("electron");
const { spawn } = require("node:child_process");
const fs = require("node:fs");
const path = require("node:path");
const crypto = require("node:crypto");
const readline = require("node:readline");

const isDev = !app.isPackaged;
const isWorkshopRecoverySmoke =
  process.argv.includes("--smoke-workshop-recovery")
  || app.commandLine.hasSwitch("smoke-workshop-recovery");
const isSmoke =
  isWorkshopRecoverySmoke
  || process.argv.includes("--smoke")
  || app.commandLine.hasSwitch("smoke");
if (isSmoke) app.disableHardwareAcceleration();
const modelConnections = new Map();
const approvedAttachments = new Set();
const approvedWorkspaceRoots = new Set();
const cancelledRuns = new Set();
const activeRuns = new Map();
const activeAgentPackBuilds = new Map();
const activeWorkshopRuns = new Map();
let mainWindow;
let bridge;
let manualZoomFactor = null;
let adaptiveZoomTimer = null;
const ownsInstance = isSmoke || app.requestSingleInstanceLock();

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

function readExtensionProfiles() {
  try {
    return JSON.parse(fs.readFileSync(extensionProfilePath(), "utf8"));
  } catch {
    return { ssh: [], cloud: [] };
  }
}

async function writeExtensionProfiles(value) {
  const target = extensionProfilePath();
  const temporary = `${target}.tmp`;
  await fs.promises.mkdir(path.dirname(target), { recursive: true });
  await fs.promises.writeFile(temporary, JSON.stringify(value, null, 2), "utf8");
  await fs.promises.rename(temporary, target);
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
    if (!item?.path || !approvedAttachments.has(item.path)) {
      throw new Error("附件未通过本次系统选择授权。");
    }
    const stat = fs.statSync(item.path);
    if (!stat.isFile()) throw new Error("附件不是有效文件。");
    totalBytes += stat.size;
    if (totalBytes > 20 * 1024 * 1024) throw new Error("附件总大小不能超过 20 MB。");

    const documentMime = documentTypeFor(item.path);
    if (documentMime) {
      if (stat.size > 12 * 1024 * 1024) {
        throw new Error("单个 PDF 或 Word 文档不能超过 12 MB。");
      }
      return {
        id: item.id,
        name: path.basename(item.path),
        path: item.path,
        kind: "document",
        mime: documentMime
      };
    }

    const mime = contentTypeFor(item.path);
    if (mime) {
      if (stat.size > 10 * 1024 * 1024) throw new Error("单张图片不能超过 10 MB。");
      return {
        id: item.id,
        name: path.basename(item.path),
        path: item.path,
        kind: "image",
        mime,
        data: fs.readFileSync(item.path).toString("base64")
      };
    }
    if (stat.size > 1024 * 1024) throw new Error("单个文本附件不能超过 1 MB。");
    return {
      id: item.id,
      name: path.basename(item.path),
      path: item.path,
      kind: "text",
      text: fs.readFileSync(item.path, "utf8")
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
  const runId = String(request?.runId || crypto.randomUUID());
  let taskId;
  activeRuns.set(runId, null);

  try {
    const task = await bridge.call("start_task", {
      taskId: request?.taskId || null,
      title: taskTitle || "NOVA 新任务",
      prompt,
      provider,
      model,
      agentPackId: request?.agentPackId || null,
      workspaceRoot: request?.workspace || process.cwd(),
      mode: request?.executionMode || "Build"
    });
    taskId = task.id || task.taskId;
    activeRuns.set(runId, taskId);
    if (cancelledRuns.has(runId)) {
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
    const output = String(result.output || "");
    const requiresWorkspaceMutation = Boolean(result.requiresWorkspaceMutation);
    const hasWorkspaceChanges = Number(result.mutatingToolCalls || 0) > 0;
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
    await bridge.call("complete_task", {
      taskId,
      succeeded: true,
      outcome: partial ? "partial" : "completed",
      outputCharacters: persistedDraft.length,
      detail: `${deliveryStatus} · ${deliverySummary} · ${result.toolCalls || 0} 次工具调用 · ${result.mutatingToolCalls || 0} 次写操作`,
      draft: persistedDraft
    });
    return {
      taskId,
      output,
      toolCalls: result.toolCalls || 0,
      mutatingToolCalls: result.mutatingToolCalls || 0,
      verification,
      delivery: {
        status: deliveryStatus,
        summary: deliverySummary,
        requiresWorkspaceMutation,
        hasWorkspaceChanges,
        validationRuns: Number(result.validationRuns || 0)
      }
    };
  } catch (error) {
    if (cancelledRuns.delete(runId)) {
      throw new Error("NOVA_RUN_CANCELLED");
    }
    if (taskId) {
      try {
        await bridge.call("complete_task", {
          taskId,
          succeeded: false,
          detail: safeError(error)
        });
      } catch {
        // The original model error remains the useful user-facing failure.
      }
    }
    throw new Error(safeError(error));
  } finally {
    activeRuns.delete(runId);
  }
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

function readDeliveryArtifact(request) {
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
      }
    };
  });
  ipcMain.handle("nova:list-tasks", () => bridge.call("list_tasks"));
  ipcMain.handle("nova:list-archived-tasks", () => bridge.call("list_archived_tasks"));
  ipcMain.handle("nova:get-task", async (event, request) => {
    senderWindow(event);
    const detail = await bridge.call("get_task", { taskId: request?.taskId });
    rememberWorkspace(detail?.task?.workspaceRoot);
    return detail;
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
  ipcMain.handle("nova:select-workspace", async (event) => {
    const result = await dialog.showOpenDialog(senderWindow(event), {
      title: "选择 NOVA 工作区",
      properties: ["openDirectory", "createDirectory"]
    });
    if (result.canceled) return null;
    rememberWorkspace(result.filePaths[0]);
    return result.filePaths[0];
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
      approvedAttachments.add(filePath);
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
  ipcMain.handle("nova:configure-model", async (event, configuration) => {
    senderWindow(event);
    const normalized = normalizeModelConfiguration(configuration);
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
    return {
      provider: normalized.provider,
      connected: true,
      model: normalized.model,
      endpoint: normalized.endpoint,
      discoveredModels
    };
  });
  ipcMain.handle("nova:run-model", (event, request) => {
    senderWindow(event);
    return runModel(request);
  });
  ipcMain.handle("nova:cancel-model", (event, request) => {
    senderWindow(event);
    const runId = String(request?.runId || "");
    if (!runId || !activeRuns.has(runId)) return { cancelled: false };
    cancelledRuns.add(runId);
    const taskId = activeRuns.get(runId);
    if (!taskId) return { cancelled: true };
    return bridge.call("cancel_task", { taskId });
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
  ipcMain.handle("nova:search-capability-store", (event, request) => {
    senderWindow(event);
    return bridge.call("search_capability_store", {
      kind: request?.kind || "all",
      query: request?.query || ""
    });
  });
  ipcMain.handle("nova:install-store-capability", (event, request) => {
    senderWindow(event);
    return bridge.call("install_store_capability", { id: request?.id });
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
    const result = await dialog.showOpenDialog(senderWindow(event), {
      title: "导入 NOVA Agent Pack",
      message: "选择包含 nova.industry.json 的文件夹",
      properties: ["openDirectory"]
    });
    if (result.canceled || !result.filePaths[0]) {
      return { canceled: true, pack: null };
    }
    const confirmation = await dialog.showMessageBox(senderWindow(event), {
      type: "question",
      title: "导入专业 Agent",
      message: `将 ${path.basename(result.filePaths[0])} 导入 NOVA 的 Agent 扩展坞吗？`,
      detail:
        "NOVA 只复制声明、角色、工作流、知识和交付模板；不会执行包内代码，也不会自动授予模型、网络或桌面权限。导入后仍需手动启用。",
      buttons: ["取消", "安全导入"],
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
    senderWindow(event);
    return bridge.call("get_knowledge_state", {
      workspaceRoot: request?.workspace || null
    });
  });
  ipcMain.handle("nova:index-workspace-knowledge", (event, request) => {
    senderWindow(event);
    return bridge.call("index_workspace_knowledge", {
      workspaceRoot: request?.workspace
    });
  });
  ipcMain.handle("nova:search-workspace-knowledge", (event, request) => {
    senderWindow(event);
    return bridge.call("search_workspace_knowledge", {
      workspaceRoot: request?.workspace || null,
      query: request?.query,
      maximumResults: request?.maximumResults || 12
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
    if (eventName === "design_event") {
      acceptWorkshopRuntimeEvent(payload);
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
  session.defaultSession.setPermissionCheckHandler(() => false);
  session.defaultSession.setPermissionRequestHandler((_webContents, _permission, callback) =>
    callback(false)
  );
  registerIpc();

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
  bridge?.stop();
  if (process.platform !== "darwin") app.quit();
});

app.on("before-quit", () => bridge?.stop());
