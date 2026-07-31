const {
  app,
  BrowserWindow,
  dialog,
  ipcMain,
  session
} = require("electron");
const { spawn } = require("node:child_process");
const fs = require("node:fs");
const path = require("node:path");
const crypto = require("node:crypto");
const readline = require("node:readline");

const isDev = !app.isPackaged;
const isSmoke =
  process.argv.includes("--smoke") || app.commandLine.hasSwitch("smoke");
const modelConnections = new Map();
const approvedAttachments = new Set();
const cancelledRuns = new Set();
let mainWindow;
let bridge;
let activeRunId = null;
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
        method === "run_agent" || method === "verify_result"
          ? 30 * 60 * 1000
          : 20000;
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
      endpoint: "http://127.0.0.1:11434/v1/chat/completions"
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
    endpoint.pathname = "/api/tags";
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
  activeRunId = runId;

  try {
    const task = await bridge.call("start_task", {
      taskId: request?.taskId || null,
      title: taskTitle || "NOVA 新任务",
      prompt,
      provider,
      model,
      workspaceRoot: request?.workspace || process.cwd(),
      mode: request?.executionMode || "Build"
    });
    taskId = task.id || task.taskId;
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
    if (activeRunId === runId) activeRunId = null;
  }
}

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1560,
    height: 960,
    minWidth: 1080,
    minHeight: 680,
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
  mainWindow.once("ready-to-show", () => {
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
  ipcMain.handle("nova:get-task", (event, request) => {
    senderWindow(event);
    return bridge.call("get_task", { taskId: request?.taskId });
  });
  ipcMain.handle("nova:archive-task", (event, request) => {
    senderWindow(event);
    return bridge.call("archive_task", { taskId: request?.taskId });
  });
  ipcMain.handle("nova:restore-task", (event, request) => {
    senderWindow(event);
    return bridge.call("restore_task", { taskId: request?.taskId });
  });
  ipcMain.handle("nova:select-workspace", async (event) => {
    const result = await dialog.showOpenDialog(senderWindow(event), {
      title: "选择 NOVA 工作区",
      properties: ["openDirectory", "createDirectory"]
    });
    return result.canceled ? null : result.filePaths[0];
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
        kind: contentTypeFor(filePath) ? "image" : "text"
      };
    });
  });
  ipcMain.handle("nova:configure-model", async (event, configuration) => {
    senderWindow(event);
    const normalized = normalizeModelConfiguration(configuration);
    const discoveredModels = await probeModelConnection(normalized);
    if (
      normalized.provider === "ollama" &&
      discoveredModels.length &&
      !discoveredModels.includes(normalized.model)
    ) {
      normalized.model = discoveredModels[0];
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
    if (!runId || activeRunId !== runId) return { cancelled: false };
    cancelledRuns.add(runId);
    bridge.stop();
    bridge = createBridgeClient();
    return { cancelled: true };
  });
  ipcMain.handle("nova:list-capabilities", (event, request) => {
    senderWindow(event);
    return bridge.call("list_capabilities", {
      workspaceRoot: request?.workspace || process.cwd()
    });
  });
  ipcMain.handle("nova:set-mcp-enabled", (event, request) => {
    senderWindow(event);
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
  ipcMain.handle("nova:desktop-snapshot", (event) => {
    senderWindow(event);
    return bridge.call("desktop_snapshot");
  });
  ipcMain.handle("nova:get-living-memory", (event) => {
    senderWindow(event);
    return bridge.call("get_living_memory");
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
