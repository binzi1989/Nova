import {
  Activity,
  Archive,
  Bot,
  Boxes,
  BrainCircuit,
  Check,
  ChevronDown,
  Circle,
  Clock3,
  Cloud,
  FileCode2,
  FolderOpen,
  Image,
  KeyRound,
  Maximize2,
  Menu,
  MessageSquareText,
  Minimize2,
  Paperclip,
  Plus,
  RefreshCw,
  Send,
  Settings2,
  ShieldCheck,
  Server,
  Sparkles,
  Square,
  Terminal,
  X,
  Zap
} from "lucide-react";
import { FormEvent, useEffect, useMemo, useRef, useState } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import type {
  AgentEvent,
  AgentTask,
  Attachment,
  BootInfo,
  CapabilityState,
  DesktopSnapshot,
  EvolutionLabState,
  LivingMemoryState,
  ExecutionMode,
  Message,
  Provider,
  StoreCapabilityItem
} from "./types";

type SettingsSection =
  | "model"
  | "mcp"
  | "skills"
  | "ssh"
  | "cloud"
  | "plugins"
  | "growth";
type PendingSubmission = {
  content: string;
  attachments: Attachment[];
};
type PlanStep = {
  id: string;
  title: string;
  detail: string;
  agent: string;
  status: "pending" | "running" | "done" | "failed";
  output?: string;
};

const providerLabels: Record<Provider, string> = {
  deepseek: "DeepSeek",
  openai: "OpenAI",
  kimi: "Kimi",
  ollama: "Ollama",
  custom: "兼容接口"
};

const providerModels: Record<Provider, string[]> = {
  deepseek: ["deepseek-v4-flash", "deepseek-v4-pro"],
  openai: ["gpt-5.6", "gpt-5.6-terra", "gpt-5.6-luna"],
  kimi: ["kimi-k3", "kimi-k2.6"],
  ollama: ["gpt-oss:20b", "qwen3:8b", "gemma3:4b"],
  custom: ["custom-model"]
};

const executionModeLabels: Record<ExecutionMode, { label: string; detail: string }> = {
  Ask: { label: "咨询", detail: "只读回答" },
  Plan: { label: "规划", detail: "只读方案" },
  Build: { label: "构建", detail: "修改并验证" },
  Autopilot: { label: "Agent", detail: "自主拆解并行" },
  Goal: { label: "目标", detail: "自主探索结果" }
};

function now() {
  return new Date().toLocaleTimeString("zh-CN", {
    hour: "2-digit",
    minute: "2-digit"
  });
}

function readableRunError(error: unknown) {
  const raw = error instanceof Error ? error.message : "任务执行失败";
  return raw
    .replace(
      /^Error invoking remote method ['"]nova:run-model['"]:\s*(?:Error:\s*)?/i,
      ""
    )
    .trim();
}

function normalizeTasks(value: AgentTask[] | { tasks: AgentTask[] }) {
  const tasks = Array.isArray(value) ? value : value?.tasks || [];
  return tasks.map((task) => ({
    ...task,
    status: task.status || task.state || "idle"
  }));
}

function parseExecutionMode(value?: string): ExecutionMode | null {
  const normalized = value?.toLowerCase();
  if (normalized === "ask") return "Ask";
  if (normalized === "plan") return "Plan";
  if (normalized === "build") return "Build";
  if (normalized === "autopilot") return "Autopilot";
  if (normalized === "goal") return "Goal";
  return null;
}

function initialPlan(mode: ExecutionMode): PlanStep[] {
  return [
    {
      id: "understand",
      title: "理解目标与约束",
      detail: "读取本轮意图、上下文和工作区边界",
      agent: "NOVA",
      status: "running"
    },
    {
      id: "inspect",
      title: "检查工作区",
      detail: "定位相关文件、已有实现与风险",
      agent: mode === "Autopilot" || mode === "Goal" ? "Agent 工作组" : "NOVA",
      status: "pending"
    },
    {
      id: "execute",
      title: mode === "Ask" || mode === "Plan" ? "形成答案" : "实施解决方案",
      detail: mode === "Ask" || mode === "Plan" ? "形成可执行、可核验的结果" : "在授权边界内修改并持续纠偏",
      agent: "NOVA",
      status: "pending"
    },
    {
      id: "verify",
      title: "验证并交付",
      detail: "检查结果、证据与未完成边界",
      agent: "审查官",
      status: "pending"
    }
  ];
}

function statusTone(status = "") {
  const normalized = status.toLowerCase();
  if (normalized.includes("complete") || normalized.includes("deliver")) return "done";
  if (normalized.includes("fail")) return "failed";
  if (normalized.includes("run") || normalized.includes("active")) return "running";
  return "idle";
}

function taskSubtitle(task: AgentTask) {
  const tone = statusTone(task.status);
  if (tone === "done") return "结果已落盘，可继续追问";
  if (tone === "failed") return "保留现场，可恢复处理";
  if (tone === "running") return "正在沿目标推进";
  return task.summary || "上下文已保存";
}

function parseChoices(content: string) {
  const pattern =
    /^\s*\[\[NOVA_CHOICE\|([^|\]\r\n]{1,56})\|([^\]\r\n]{1,600})\]\]\s*$/gm;
  const choices: Array<{ title: string; prompt: string }> = [];
  const display = content
    .replace(pattern, (_line, title: string, prompt: string) => {
      choices.push({ title: title.trim(), prompt: prompt.trim() });
      return "";
    })
    .replace(/\n{3,}/g, "\n\n")
    .trim();
  return { display: display || content, choices };
}

function prepareMarkdown(content: string) {
  return content
    .replace(/^\s*#{7,}\s+/gm, "###### ")
    .replace(/^\s*#{1,6}\s*$/gm, "")
    .replace(/\n{3,}/g, "\n\n")
    .trim();
}

function MarkdownContent({ content }: { content: string }) {
  return (
    <ReactMarkdown
      remarkPlugins={[remarkGfm]}
      skipHtml
      components={{
        a: ({ children, ...props }) => (
          <a {...props} target="_blank" rel="noreferrer">
            {children}
          </a>
        )
      }}
    >
      {prepareMarkdown(content)}
    </ReactMarkdown>
  );
}

function App() {
  const [boot, setBoot] = useState<BootInfo | null>(null);
  const [tasks, setTasks] = useState<AgentTask[]>([]);
  const [archivedTasks, setArchivedTasks] = useState<AgentTask[]>([]);
  const [messages, setMessages] = useState<Message[]>([]);
  const [draft, setDraft] = useState("");
  const [provider, setProvider] = useState<Provider>("deepseek");
  const [model, setModel] = useState(providerModels.deepseek[0]);
  const [modelEndpoint, setModelEndpoint] = useState("");
  const [discoveredModels, setDiscoveredModels] = useState<string[]>([]);
  const [executionMode, setExecutionMode] = useState<ExecutionMode>("Build");
  const [connected, setConnected] = useState<Partial<Record<Provider, boolean>>>({});
  const [workspace, setWorkspace] = useState<string | null>(null);
  const [attachments, setAttachments] = useState<Attachment[]>([]);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [settingsSection, setSettingsSection] = useState<SettingsSection>("model");
  const [capabilities, setCapabilities] = useState<CapabilityState | null>(null);
  const [capabilitiesLoading, setCapabilitiesLoading] = useState(false);
  const [livingMemory, setLivingMemory] = useState<LivingMemoryState | null>(null);
  const [evolutionLab, setEvolutionLab] = useState<EvolutionLabState | null>(null);
  const [evolutionObjective, setEvolutionObjective] = useState("");
  const [desktopSnapshot, setDesktopSnapshot] = useState<DesktopSnapshot | null>(null);
  const [growthLoading, setGrowthLoading] = useState(false);
  const [storeItems, setStoreItems] = useState<StoreCapabilityItem[]>([]);
  const [storeSources, setStoreSources] = useState<Array<{
    id: string;
    kind: "mcp" | "skill";
    name: string;
    publisher: string;
    description: string;
    trust: string;
    endpoint: string;
  }>>([]);
  const [storeQuery, setStoreQuery] = useState("");
  const [storeKind, setStoreKind] = useState<"all" | "mcp" | "skill">("all");
  const [storeLoading, setStoreLoading] = useState(false);
  const [pendingStoreItem, setPendingStoreItem] = useState<StoreCapabilityItem | null>(null);
  const [pendingBundledItem, setPendingBundledItem] =
    useState<CapabilityState["marketplace"][number] | null>(null);
  const [extensionProfiles, setExtensionProfiles] = useState<{
    ssh: Array<Record<string, string | number>>;
    cloud: Array<Record<string, string | number>>;
  }>({ ssh: [], cloud: [] });
  const [apiKey, setApiKey] = useState("");
  const [running, setRunning] = useState(false);
  const [streamingText, setStreamingText] = useState("");
  const [runtimePulse, setRuntimePulse] = useState("正在建立模型连接");
  const [selectedTaskId, setSelectedTaskId] = useState<string | null>(null);
  const [pendingArchiveTask, setPendingArchiveTask] = useState<AgentTask | null>(null);
  const [archiveLibraryOpen, setArchiveLibraryOpen] = useState(false);
  const [pendingSubmission, setPendingSubmission] =
    useState<PendingSubmission | null>(null);
  const [approvalOpen, setApprovalOpen] = useState(false);
  const [queuedCorrection, setQueuedCorrection] =
    useState<PendingSubmission | null>(null);
  const [rightOpen, setRightOpen] = useState(true);
  const [notice, setNotice] = useState("正在唤醒 AgentOS…");
  const [activity, setActivity] = useState<
    Array<{ id: string; title: string; detail: string; state: string; at: string }>
  >([]);
  const [agentUnits, setAgentUnits] = useState<
    Record<string, {
      agent: string;
      action: string;
      detail: string;
      kind: string;
      at: string;
      activeUnits: number;
      outputs: string[];
    }>
  >({});
  const [planTitle, setPlanTitle] = useState("执行计划");
  const [taskPlan, setTaskPlan] = useState<PlanStep[]>([]);
  const threadEnd = useRef<HTMLDivElement>(null);
  const activeRunId = useRef<string | null>(null);
  const streamBuffer = useRef("");
  const streamFlushTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const lastRuntimeEventAt = useRef(Date.now());

  const serviceCount =
    boot?.kernel?.servicesTotal || boot?.kernel?.services?.length || 0;
  const serviceReady =
    boot?.kernel?.servicesReady ||
    boot?.kernel?.services?.filter((service) =>
      ["ready", "online", "healthy"].includes(service.status?.toLowerCase())
    ).length ||
    serviceCount;

  const workspaceName = useMemo(() => {
    if (!workspace) return "尚未选择工作区";
    return workspace.split(/[\\/]/).filter(Boolean).at(-1) || workspace;
  }, [workspace]);

  async function refreshTasks() {
    const result = await window.nova.system.listTasks();
    setTasks(normalizeTasks(result));
  }

  async function refreshArchivedTasks() {
    const result = await window.nova.system.listArchivedTasks();
    setArchivedTasks(normalizeTasks(result));
  }

  async function archiveTask(task: AgentTask) {
    await window.nova.system.archiveTask({ taskId: task.id });
    if (selectedTaskId === task.id) newTask();
    setPendingArchiveTask(null);
    await Promise.all([refreshTasks(), refreshArchivedTasks()]);
    setNotice(`“${task.title || "未命名任务"}”已移入归档库，可随时恢复`);
    addActivity("任务已归档", task.title || task.id, "done");
  }

  async function restoreArchivedTask(task: AgentTask) {
    await window.nova.system.restoreTask({ taskId: task.id });
    await Promise.all([refreshTasks(), refreshArchivedTasks()]);
    setArchiveLibraryOpen(false);
    setNotice(`“${task.title || "未命名任务"}”已恢复到任务空间`);
    addActivity("任务已恢复", task.title || task.id, "done");
  }

  async function openTask(task: AgentTask) {
    if (running) return;
    try {
      const recovered = await window.nova.system.getTask({ taskId: task.id });
      setSelectedTaskId(task.id);
      setWorkspace(recovered.task.workspaceRoot || workspace);
      if (recovered.task.provider && recovered.task.provider in providerLabels) {
        const nextProvider = recovered.task.provider as Provider;
        setProvider(nextProvider);
        setModel(recovered.task.model || providerModels[nextProvider][0]);
      }
      const recoveredMode = parseExecutionMode(recovered.task.executionMode);
      if (recoveredMode) setExecutionMode(recoveredMode);
      setMessages(
        recovered.messages.map((message) => ({
          ...message,
          createdAt: new Date(message.createdAt).toLocaleTimeString("zh-CN", {
            hour: "2-digit",
            minute: "2-digit"
          })
        }))
      );
      setNotice("已恢复任务上下文，可以继续追问或修改方向");
      addActivity("任务已恢复", task.title, "done");
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "任务恢复失败");
    }
  }

  async function loadCapabilities() {
    setCapabilitiesLoading(true);
    try {
      setCapabilities(await window.nova.capabilities.list({ workspace }));
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "能力状态读取失败");
    } finally {
      setCapabilitiesLoading(false);
    }
  }

  async function loadExtensionProfiles() {
    const profiles = await window.nova.extensions.listProfiles();
    setExtensionProfiles(profiles as {
      ssh: Array<Record<string, string | number>>;
      cloud: Array<Record<string, string | number>>;
    });
  }

  async function loadGrowthState(refreshDesktop = true) {
    setGrowthLoading(true);
    try {
      const [memory, evolution, desktop] = await Promise.all([
        window.nova.growth.getState(),
        window.nova.growth.getEvolutionLab(),
        refreshDesktop
          ? window.nova.system.desktopSnapshot()
          : Promise.resolve(desktopSnapshot)
      ]);
      setLivingMemory(memory);
      setEvolutionLab(evolution);
      if (desktop) setDesktopSnapshot(desktop);
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "成长中心状态读取失败");
    } finally {
      setGrowthLoading(false);
    }
  }

  async function searchCapabilityStore(event?: FormEvent) {
    event?.preventDefault();
    setStoreLoading(true);
    try {
      const result = await window.nova.capabilities.searchStore({
        kind: storeKind,
        query: storeQuery.trim()
      });
      setStoreSources(result.sources);
      setStoreItems(result.items);
      setNotice(`能力目录已更新：找到 ${result.items.length} 个可选组件`);
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "能力目录连接失败");
    } finally {
      setStoreLoading(false);
    }
  }

  async function installStoreItem(item: StoreCapabilityItem) {
    await window.nova.capabilities.installStore({ id: item.id });
    setPendingStoreItem(null);
    await loadCapabilities();
    setNotice(
      item.kind === "mcp"
        ? `“${item.name}”已登记并保持停用，请在 MCP 页审阅后启用`
        : `“${item.name}”已通过格式校验并安装`
    );
    addActivity("能力已加入扩展坞", `${item.sourceLabel} · ${item.name}`, "done");
  }

  async function installBundledItem(item: CapabilityState["marketplace"][number]) {
    await window.nova.capabilities.install({ id: item.id, workspace });
    setPendingBundledItem(null);
    await loadCapabilities();
    setNotice(`“${item.name}”已加入扩展坞；实际启用状态可在对应能力页查看`);
    addActivity("内置能力已加载", `${item.kind.toUpperCase()} · ${item.name}`, "done");
  }

  function openSettings(section: SettingsSection = "model") {
    setSettingsSection(section);
    setSettingsOpen(true);
    if (section === "mcp" || section === "skills" || section === "plugins") {
      void loadCapabilities();
    }
    if (section === "ssh" || section === "cloud") void loadExtensionProfiles();
    if (section === "growth") void loadGrowthState();
  }

  useEffect(() => {
    let active = true;
    (async () => {
      try {
        const info = await window.nova.system.boot();
        if (!active) return;
        setBoot(info);
        setModel(info.defaults.deepseek.model);
        await Promise.all([refreshTasks(), refreshArchivedTasks()]);
        setNotice("内核在线，等待你的目标");
      } catch (error) {
        setNotice(error instanceof Error ? error.message : "AgentOS 启动失败");
      }
    })();
    return () => {
      active = false;
    };
  }, []);

  useEffect(() => {
    const unsubscribe = window.nova.model.onEvent((event: AgentEvent) => {
      lastRuntimeEventAt.current = Date.now();
      setRuntimePulse(
        event.kind === "textdelta"
          ? "模型正在持续生成"
          : event.action || "执行链路仍在推进"
      );
      if (event.kind === "textdelta") {
        streamBuffer.current += event.detail || "";
        if (!streamFlushTimer.current) {
          streamFlushTimer.current = setTimeout(() => {
            setStreamingText(streamBuffer.current);
            streamFlushTimer.current = null;
          }, 120);
        }
        return;
      }
      if (event.action === "流式重连") {
        streamBuffer.current = "";
        setStreamingText("");
      }
      if (event.action === "任务规划") {
        try {
          const payload = JSON.parse(event.detail) as {
            strategy?: string;
            steps?: Array<Pick<PlanStep, "id" | "title" | "detail" | "agent">>;
          };
          setPlanTitle(payload.strategy || "Agent 并行计划");
          setTaskPlan([
            {
              id: "understand",
              title: "理解目标与约束",
              detail: "任务目标和执行边界已经冻结",
              agent: "NOVA",
              status: "done"
            },
            ...(payload.steps || []).map((step) => ({ ...step, status: "pending" as const })),
            {
              id: "execute",
              title: "整合并实施",
              detail: "指挥官交叉验证子 Agent 结果并执行必要修改",
              agent: "NOVA",
              status: "pending"
            },
            {
              id: "verify",
              title: "验证并交付",
              detail: "核对文件、构建或测试证据与未完成边界",
              agent: "审查官",
              status: "pending"
            }
          ]);
        } catch {
          // The event remains visible in the activity trace if an older runtime sends prose.
        }
      } else {
        setTaskPlan((current) => current.map((step) => {
          const matchesAgent =
            step.agent === event.agent ||
            (step.agent === "Agent 工作组" && event.agent.includes("Agent"));
          const isDone =
            event.kind === "toolcompleted" ||
            event.kind === "toolbatchcompleted" ||
            event.kind === "batchcompleted" ||
            event.kind === "completed";
          const isActive =
            event.kind === "thinking" ||
            event.kind === "toolrequested" ||
            event.kind === "toolrunning" ||
            event.kind === "toolbatchstarted" ||
            event.kind === "batchstarted";

          if (matchesAgent) {
            return {
              ...step,
              status: event.kind === "failed" ? "failed" : isDone ? "done" : isActive ? "running" : step.status,
              output: isDone && event.detail ? event.detail : step.output
            };
          }
          if (step.id === "inspect" && event.kind.includes("tool")) {
            return { ...step, status: isDone ? "done" : "running" };
          }
          if (step.id === "execute" && event.agent === "NOVA") {
            return { ...step, status: isDone ? "done" : isActive ? "running" : step.status };
          }
          if (step.id === "verify" && event.kind === "completed") {
            return { ...step, status: "done", output: event.detail || step.output };
          }
          return step;
        }));
      }
      setAgentUnits((current) => {
        const previous = current[event.agent];
        const shouldKeepOutput =
          event.kind === "toolcompleted" ||
          event.kind === "toolbatchcompleted" ||
          event.kind === "batchcompleted" ||
          event.kind === "completed" ||
          event.detail.includes("阶段产出：");
        return {
          ...current,
          [event.agent]: {
            agent: event.agent,
            action: event.action || event.kind,
            detail: event.detail || "",
            kind: event.kind,
            at: now(),
            activeUnits: event.activeUnits,
            outputs: shouldKeepOutput
              ? [event.detail, ...(previous?.outputs || [])].slice(0, 4)
              : previous?.outputs || []
          }
        };
      });
      addActivity(
        event.action || event.agent,
        event.detail || event.kind,
        event.kind === "failed" ? "failed" : event.kind.includes("tool") ? "running" : "done"
      );
    });
    return () => {
      unsubscribe();
      if (streamFlushTimer.current) {
        clearTimeout(streamFlushTimer.current);
        streamFlushTimer.current = null;
      }
    };
  }, []);

  useEffect(() => {
    if (!running) {
      setRuntimePulse("等待下一项任务");
      return;
    }
    const timer = setInterval(() => {
      const silenceSeconds = Math.floor(
        (Date.now() - lastRuntimeEventAt.current) / 1000
      );
      if (silenceSeconds >= 25) {
        setRuntimePulse(
          `模型已 ${silenceSeconds} 秒没有返回新数据，可继续等待或停止后重试`
        );
      } else if (silenceSeconds >= 8) {
        setRuntimePulse(
          `正在等待模型下一段响应 · ${silenceSeconds} 秒`
        );
      }
    }, 2000);
    return () => clearInterval(timer);
  }, [running]);

  useEffect(() => {
    if (!running && queuedCorrection) {
      setPendingSubmission(queuedCorrection);
      setQueuedCorrection(null);
      setApprovalOpen(true);
      setNotice("上一条路径已停止，请确认纠正后的执行权限");
    }
  }, [running, queuedCorrection]);

  useEffect(() => {
    threadEnd.current?.scrollIntoView({ behavior: "smooth", block: "end" });
  }, [messages, running]);

  function addActivity(title: string, detail: string, state = "done") {
    setActivity((items) =>
      [
        {
          id: crypto.randomUUID(),
          title,
          detail,
          state,
          at: now()
        },
        ...items
      ].slice(0, 12)
    );
  }

  async function chooseWorkspace() {
    const selected = await window.nova.system.selectWorkspace();
    if (!selected) return;
    setWorkspace(selected);
    addActivity("工作区已就位", selected, "done");
  }

  async function chooseAttachments() {
    try {
      const selected = await window.nova.system.selectAttachments();
      setAttachments((items) => {
        const paths = new Set(items.map((item) => item.path));
        return [...items, ...selected.filter((item) => !paths.has(item.path))].slice(0, 6);
      });
      if (selected.length) addActivity("附件已装载", `${selected.length} 个文件`, "done");
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "附件加载失败");
    }
  }

  async function connectModel(event: FormEvent) {
    event.preventDefault();
    try {
      const result = await window.nova.model.configure({
        provider,
        model,
        apiKey,
        endpoint: modelEndpoint
      });
      setConnected((value) => ({ ...value, [provider]: true }));
      setModel(result.model);
      setModelEndpoint(result.endpoint || modelEndpoint);
      setDiscoveredModels(result.discoveredModels || []);
      setApiKey("");
      setSettingsOpen(false);
      setNotice(`${providerLabels[provider]} 已连接，密钥仅保留在本次运行内存`);
      addActivity("模型通道已连接", `${providerLabels[provider]} · ${model}`, "done");
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "模型连接失败");
    }
  }

  function changeProvider(next: Provider) {
    setProvider(next);
    setModel(boot?.defaults?.[next]?.model || providerModels[next][0]);
    setModelEndpoint(boot?.defaults?.[next]?.endpoint || "");
    setDiscoveredModels([]);
  }

  async function sendMessage(event?: FormEvent) {
    event?.preventDefault();
    const content = draft.trim();
    if (!content) return;
    if (running) {
      const correction = { content, attachments };
      setDraft("");
      setAttachments([]);
      setQueuedCorrection(correction);
      if (activeRunId.current) {
        await window.nova.model.cancel({ runId: activeRunId.current });
      }
      setNotice("正在停止上一条路径，随后按你的纠正继续");
      return;
    }
    if (!connected[provider]) {
      openSettings("model");
      setNotice(`先连接 ${providerLabels[provider]}，然后继续当前内容`);
      return;
    }
    if (!workspace) {
      setNotice("先选择一个工作区，NOVA 才能在明确边界内读取或修改文件");
      await chooseWorkspace();
      return;
    }
    setPendingSubmission({ content, attachments });
    setApprovalOpen(true);
  }

  async function executeSubmission(
    approvalMode: "workspace" | "workspaceDesktop" | "readOnly"
  ) {
    if (!pendingSubmission) return;
    const { content, attachments: submittedAttachments } = pendingSubmission;
    setPendingSubmission(null);
    setApprovalOpen(false);

    const userMessage: Message = {
      id: crypto.randomUUID(),
      role: "user",
      content,
      createdAt: now(),
      attachments: submittedAttachments
    };
    const nextMessages = [...messages, userMessage];
    setMessages(nextMessages);
    setDraft("");
    setAttachments([]);
    setRunning(true);
    streamBuffer.current = "";
    setStreamingText("");
    lastRuntimeEventAt.current = Date.now();
    setRuntimePulse("正在建立模型连接");
    setAgentUnits({});
    setPlanTitle(
      executionMode === "Goal"
        ? "目标模式执行计划"
        : executionMode === "Autopilot"
          ? "Agent 工作组计划"
          : "执行计划"
    );
    setTaskPlan(initialPlan(executionMode));
    setNotice(
      approvalMode === "workspaceDesktop"
        ? "NOVA 已获得本轮桌面与工作区权限；每一步仍受安全边界约束"
        : approvalMode === "workspace"
          ? "NOVA 正在使用完整 Agent Runtime 推进任务"
          : "NOVA 正在只读分析，不会修改文件"
    );
    addActivity("任务进入内核", content.slice(0, 72), "running");
    const runId = crypto.randomUUID();
    activeRunId.current = runId;

    try {
      const result = await window.nova.model.run({
        provider,
        model,
        workspace,
        taskId: selectedTaskId,
        runId,
        approvalMode,
        executionMode,
        messages: nextMessages.map(({ role, content: body }) => ({
          role,
          content: body
        })),
        attachments: userMessage.attachments || []
      });
      setSelectedTaskId(result.taskId);
      setMessages((items) => [
        ...items,
        {
          id: crypto.randomUUID(),
          role: "assistant",
          content: result.output,
          createdAt: now()
        }
      ]);
      setNotice("本轮结果已写入任务线程，文件变更由 Agent Runtime 实际执行");
      addActivity(
        "本轮已完成",
        `任务 ${result.taskId} · ${result.toolCalls || 0} 次工具调用`,
        "done"
      );
      await refreshTasks();
    } catch (error) {
      const message = readableRunError(error);
      if (message.includes("NOVA_RUN_CANCELLED")) {
        setNotice("上一条执行路径已停止，工作区现场与上下文仍然保留");
        addActivity("执行已停止", "等待新的方向", "done");
        return;
      }
      setNotice(message);
      addActivity("本轮需要处理", message, "failed");
      setMessages((items) => [
        ...items,
        {
          id: crypto.randomUUID(),
          role: "assistant",
          content: `这一轮没有安全完成：${message}\n\n你的上下文仍然保留，可以调整模型或附件后直接重试。`,
          createdAt: now()
        }
      ]);
      await refreshTasks().catch(() => undefined);
    } finally {
      activeRunId.current = null;
      setRunning(false);
      streamBuffer.current = "";
      setStreamingText("");
      if (streamFlushTimer.current) {
        clearTimeout(streamFlushTimer.current);
        streamFlushTimer.current = null;
      }
    }
  }

  async function stopCurrentRun() {
    if (!activeRunId.current) return;
    await window.nova.model.cancel({ runId: activeRunId.current });
    setNotice("正在安全停止当前执行");
  }

  function newTask() {
    setSelectedTaskId(null);
    setMessages([]);
    setDraft("");
    setAttachments([]);
    setNotice("新线程已准备好，告诉我想达成什么结果");
  }

  return (
    <div className={`app-shell ${rightOpen ? "" : "trace-collapsed"}`}>
      <header className="titlebar">
        <div className="brand">
          <div className="brand-core"><span /></div>
          <strong>NOVA</strong>
          <span className="brand-edition">AGENTOS · ELECTRON</span>
        </div>
        <div className="kernel-strip">
          <span className={`status-dot ${boot ? "online" : ""}`} />
          <span>
            {boot
              ? `内核 ${boot.kernel.kernelVersion || boot.kernel.version || "ONLINE"}`
              : "正在启动"}
          </span>
          <i />
          <span>{serviceReady}/{serviceCount || 9} SERVICES</span>
          <i />
          <span className={connected[provider] ? "accent" : ""}>
            {connected[provider] ? `${providerLabels[provider]} 已连接` : "需要连接模型"}
          </span>
        </div>
        <div className="title-actions">
          <button aria-label="扩展坞与设置" onClick={() => openSettings("model")}>
            <Settings2 size={17} />
          </button>
          <button aria-label="最小化" onClick={() => window.nova.window.minimize()}>
            <Minimize2 size={16} />
          </button>
          <button aria-label="最大化" onClick={() => window.nova.window.toggleMaximize()}>
            <Maximize2 size={15} />
          </button>
          <button className="close-window" aria-label="关闭" onClick={() => window.nova.window.close()}>
            <X size={18} />
          </button>
        </div>
      </header>

      <aside className="task-rail">
        <button className="new-task" onClick={newTask}>
          <Plus size={18} />
          <span>新建任务</span>
          <kbd>Ctrl N</kbd>
        </button>
        <div className="rail-heading">
          <span>任务空间</span>
          <small>{tasks.length}</small>
        </div>
        <div className="task-list">
          {tasks.length ? (
            tasks.slice(0, 12).map((task) => (
              <div
                className={`task-card ${selectedTaskId === task.id ? "selected" : ""}`}
                key={task.id}
              >
                <button
                  className="task-open"
                  type="button"
                  onClick={() => void openTask(task)}
                >
                  <span className={`task-state ${statusTone(task.status)}`} />
                  <span className="task-copy">
                    <strong>{task.title || "未命名任务"}</strong>
                    <small>{taskSubtitle(task)}</small>
                    <span className="task-progress"><i /></span>
                  </span>
                </button>
                <button
                  className="task-archive-action"
                  type="button"
                  aria-label={`归档 ${task.title || "任务"}`}
                  title="移入归档库"
                  disabled={running}
                  onClick={() => setPendingArchiveTask(task)}
                >
                  <Archive size={15} />
                </button>
              </div>
            ))
          ) : (
            <div className="rail-empty">
              <Sparkles size={22} />
              <strong>从一个真实目标开始</strong>
              <span>任务会自动沉淀在这里</span>
            </div>
          )}
        </div>
        <div className="rail-footer">
          <button onClick={chooseWorkspace}>
            <FolderOpen size={18} />
            <span>
              <strong>{workspaceName}</strong>
              <small>{workspace ? "工作区已授权" : "点击选择工程目录"}</small>
            </span>
            <ChevronDown size={15} />
          </button>
          <button
            className="archive-button"
            onClick={() => setArchiveLibraryOpen(true)}
          >
            <Archive size={17} />
            <span>归档库</span>
            <small>{archivedTasks.length}</small>
          </button>
        </div>
      </aside>

      <main className="workspace">
        <section className="workspace-heading">
          <div>
            <span className="eyebrow">{running ? "EXECUTING" : "READY"}</span>
            <h1>{messages.length ? "持续推进当前目标" : "把想做成的事交给我"}</h1>
            <p>{notice}</p>
          </div>
          <div className="heading-actions">
            <button className="workspace-chip" onClick={chooseWorkspace}>
              <FolderOpen size={15} />
              {workspaceName}
            </button>
            <button
              className="trace-toggle"
              onClick={() => setRightOpen((value) => !value)}
              title="显示或收起行动脉络"
            >
              <Activity size={17} />
            </button>
          </div>
        </section>

        <section className="threadspace">
          <div className="thread-header">
            <div className={`nova-orb ${running ? "thinking" : ""}`}><span /></div>
            <div>
              <span>NOVA THREADSPACE</span>
              <strong>{running ? "正在建立解题路径" : "上下文会沿着同一条任务脉络延续"}</strong>
            </div>
          </div>

          <div className="conversation">
            {messages.length === 0 ? (
              <div className="empty-state">
                <div className="empty-mark">
                  <Zap size={25} />
                </div>
                <h2>先说结果，不必学习复杂术语</h2>
                <p>选好工作区，描述你最终想看到什么。NOVA 会保留上下文、调用模型，并把每轮执行落进 AgentOS。</p>
                <div className="starter-grid">
                  {[
                    ["检查一个工程", "理解结构、定位问题并给出改良方案"],
                    ["做出一个产品", "从目标探索到可验证交付"],
                    ["继续上次任务", "沿保存的上下文接着推进"]
                  ].map(([title, detail]) => (
                    <button
                      key={title}
                      onClick={() => setDraft(`${title}：${detail}`)}
                    >
                      <FileCode2 size={18} />
                      <span><strong>{title}</strong><small>{detail}</small></span>
                    </button>
                  ))}
                </div>
              </div>
            ) : (
              <div className="message-list">
                {messages.map((message) => {
                  const parsed = parseChoices(message.content);
                  return (
                  <article className={`message ${message.role}`} key={message.id}>
                    <div className="message-meta">
                      {message.role === "assistant" ? <Bot size={16} /> : <Circle size={11} fill="currentColor" />}
                      <strong>{message.role === "assistant" ? "NOVA" : "你"}</strong>
                      <time>{message.createdAt}</time>
                    </div>
                    <div className={`message-body ${message.role === "assistant" ? "markdown-body" : ""}`}>
                      {message.role === "assistant"
                        ? <MarkdownContent content={parsed.display} />
                        : parsed.display}
                    </div>
                    {message.role === "assistant" && parsed.choices.length >= 2 && (
                      <div className="choice-grid">
                        {parsed.choices.map((choice) => (
                          <button
                            type="button"
                            key={choice.title}
                            onClick={() => setDraft(choice.prompt)}
                          >
                            <strong>{choice.title}</strong>
                            <span>选择这个方向并继续</span>
                          </button>
                        ))}
                      </div>
                    )}
                    {!!message.attachments?.length && (
                      <div className="message-attachments">
                        {message.attachments.map((file) => (
                          <span key={file.id}>
                            {file.kind === "image" ? <Image size={14} /> : <FileCode2 size={14} />}
                            {file.name}
                          </span>
                        ))}
                      </div>
                    )}
                  </article>
                  );
                })}
                {running && (
                  <article className="message assistant thinking-message">
                    <div className="message-meta"><Bot size={16} /><strong>NOVA</strong><time>{now()}</time></div>
                    {streamingText ? (
                      <>
                        <div className="message-body markdown-body streaming-body">
                          <MarkdownContent content={streamingText} />
                          <i />
                        </div>
                        <div className="streaming-status"><span />{runtimePulse}</div>
                      </>
                    ) : (
                      <div className="thinking-line"><i /><i /><i /><span>{runtimePulse}</span></div>
                    )}
                  </article>
                )}
                <div ref={threadEnd} />
              </div>
            )}
          </div>
        </section>

        <form className="composer" onSubmit={sendMessage}>
          {!!attachments.length && (
            <div className="attachment-strip">
              {attachments.map((file) => (
                <span key={file.id}>
                  {file.kind === "image" ? <Image size={15} /> : <FileCode2 size={15} />}
                  {file.name}
                  <button
                    type="button"
                    aria-label={`移除 ${file.name}`}
                    onClick={() => setAttachments((items) => items.filter((item) => item.id !== file.id))}
                  >
                    <X size={13} />
                  </button>
                </span>
              ))}
            </div>
          )}
          <textarea
            value={draft}
            onChange={(event) => setDraft(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === "Enter" && !event.shiftKey) {
                event.preventDefault();
                sendMessage();
              }
            }}
            placeholder="描述想达成的结果，NOVA 会自己理解工程并持续推进…"
            rows={3}
          />
          <div className="composer-bar">
            <div>
              <button type="button" onClick={chooseAttachments}>
                <Paperclip size={17} />
                <span>附件</span>
              </button>
              <button type="button" onClick={chooseWorkspace}>
                <FolderOpen size={17} />
              </button>
              <span className="conversation-memory">
                <MessageSquareText size={14} />
                {messages.length ? `${messages.length} 条上下文已保存` : "新会话"}
              </span>
              <label className={`agent-mode-control ${executionMode === "Autopilot" ? "active" : ""}`}>
                <BrainCircuit size={16} />
                <select
                  aria-label="Agent 执行模式"
                  value={executionMode}
                  disabled={running}
                  onChange={(event) => setExecutionMode(event.target.value as ExecutionMode)}
                >
                  {(Object.keys(executionModeLabels) as ExecutionMode[]).map((mode) => (
                    <option value={mode} key={mode}>
                      {executionModeLabels[mode].label} · {executionModeLabels[mode].detail}
                    </option>
                  ))}
                </select>
                <ChevronDown size={14} />
              </label>
            </div>
            <div className="run-actions">
              {running && (
                <button type="button" className="stop-button" onClick={stopCurrentRun}>
                  <Square size={14} fill="currentColor" />
                  停止
                </button>
              )}
              <button className="send-button" disabled={!draft.trim()}>
                {running ? <RefreshCw size={18} /> : <Send size={18} />}
                <span>
                  {running
                    ? "纠正方向"
                    : connected[provider]
                      ? "开始处理"
                      : "连接模型"}
                </span>
              </button>
            </div>
          </div>
        </form>
      </main>

      <aside className="trace-panel">
        <div className="trace-heading">
          <div>
            <span>行动脉络</span>
            <small>可观察 · 可恢复 · 有证据</small>
          </div>
          <button onClick={() => setRightOpen(false)} aria-label="收起行动脉络"><X size={16} /></button>
        </div>
        <div className="progress-card">
          <span><strong>当前状态</strong><b>{running ? "执行中" : "待命"}</b></span>
          <div><i className={running ? "active" : ""} /></div>
        </div>
        <div className="trace-list">
        {!!taskPlan.length && (
          <section className="task-plan-panel">
            <header>
              <div>
                <strong>{planTitle}</strong>
                <small>
                  {taskPlan.filter((step) => step.status === "done").length}/{taskPlan.length} 步完成
                </small>
              </div>
              <span>{running ? "实时推进" : "本轮记录"}</span>
            </header>
            <div className="task-plan-steps">
              {taskPlan.map((step, index) => (
                <details
                  key={step.id}
                  className={`plan-step ${step.status}`}
                  open={step.status === "running" || step.status === "failed"}
                >
                  <summary>
                    <span className="plan-index">
                      {step.status === "done" ? <Check size={11} /> : index + 1}
                    </span>
                    <div>
                      <strong>{step.title}</strong>
                      <small>{step.agent}</small>
                    </div>
                    <b>{step.status === "done" ? "完成" : step.status === "running" ? "进行中" : step.status === "failed" ? "受阻" : "等待"}</b>
                  </summary>
                  <p>{step.detail}</p>
                  {step.output && <pre>{step.output}</pre>}
                </details>
              ))}
            </div>
          </section>
        )}
        {!!Object.keys(agentUnits).length && (
          <section className="agent-roster">
            <header>
              <span>Agent 工作组</span>
              <small>{Object.keys(agentUnits).length} 个执行单元</small>
            </header>
            {Object.values(agentUnits).map((unit) => (
              <details key={unit.agent} open={unit.agent.includes("子 Agent")}>
                <summary>
                  <span className={`agent-state ${unit.kind}`} />
                  <div>
                    <strong>{unit.agent}</strong>
                    <small>{unit.action}</small>
                  </div>
                  <time>{unit.at}</time>
                </summary>
                <p>{unit.detail}</p>
                {!!unit.outputs.length && (
                  <div className="agent-output">
                    <span>阶段产出</span>
                    {unit.outputs.map((output, index) => (
                      <pre key={`${unit.agent}-${index}`}>{output}</pre>
                    ))}
                  </div>
                )}
              </details>
            ))}
          </section>
        )}
        <div className="trace-events">
          {activity.length ? (
            activity.map((item) => (
              <div className={`trace-item ${item.state}`} key={item.id}>
                <span className="trace-node" />
                <div>
                  <time>{item.at}</time>
                  <strong>{item.title}</strong>
                  <p>{item.detail}</p>
                </div>
              </div>
            ))
          ) : (
            <div className="trace-empty">
              <ShieldCheck size={24} />
              <strong>每一步都会留下脉络</strong>
              <span>模型调用、任务结果和异常会在这里变得可见。</span>
            </div>
          )}
        </div>
        </div>
        <div className="trace-proof">
          <ShieldCheck size={17} />
          <span><strong>AgentOS Evidence</strong><small>结果有证据，才算完成</small></span>
        </div>
      </aside>

      {settingsOpen && (
        <div className="modal-layer" onMouseDown={(event) => {
          if (event.currentTarget === event.target) setSettingsOpen(false);
        }}>
          <div className="dock-modal">
            <aside className="dock-nav">
              <div className="dock-brand">
                <Boxes size={19} />
                <span><strong>扩展坞</strong><small>模型、能力与远程环境</small></span>
              </div>
              {([
                ["model", "模型", KeyRound],
                ["mcp", "MCP", Server],
                ["skills", "Skills", BrainCircuit],
                ["growth", "成长", Sparkles],
                ["ssh", "SSH", Terminal],
                ["cloud", "云开发", Cloud],
                ["plugins", "组件", Boxes]
              ] as Array<[SettingsSection, string, typeof KeyRound]>).map(([id, label, Icon]) => (
                <button
                  type="button"
                  key={id}
                  className={settingsSection === id ? "active" : ""}
                  onClick={() => {
                    setSettingsSection(id);
                    if (id === "mcp" || id === "skills" || id === "plugins") {
                      void loadCapabilities();
                    }
                    if (id === "growth") void loadGrowthState();
                    if (id === "ssh" || id === "cloud") void loadExtensionProfiles();
                  }}
                >
                  <Icon size={16} />
                  {label}
                </button>
              ))}
            </aside>
            <section className="dock-content">
              <header className="dock-heading">
                <div>
                  <h2>
                    {settingsSection === "model" && "模型连接"}
                    {settingsSection === "mcp" && "MCP 连接"}
                    {settingsSection === "skills" && "Skills"}
                    {settingsSection === "growth" && "成长与桌面"}
                    {settingsSection === "ssh" && "SSH 工作区"}
                    {settingsSection === "cloud" && "云开发适配器"}
                    {settingsSection === "plugins" && "能力组件"}
                  </h2>
                  <p>只显示真实状态；安装、启用和外部访问都需要明确确认。</p>
                </div>
                <button type="button" onClick={() => setSettingsOpen(false)}><X size={18} /></button>
              </header>

              {settingsSection === "model" && (
                <form className="dock-form" onSubmit={connectModel}>
                  <div className="provider-tabs">
                    {(Object.keys(providerLabels) as Provider[]).map((item) => (
                      <button
                        type="button"
                        className={provider === item ? "active" : ""}
                        onClick={() => changeProvider(item)}
                        key={item}
                      >
                        <span className={`provider-mark ${item}`} />
                        {providerLabels[item]}
                        {connected[item] && <Check size={14} />}
                      </button>
                    ))}
                  </div>
                  <label>
                    <span>模型</span>
                    {provider === "ollama" || provider === "custom" ? (
                      <>
                        <input
                          value={model}
                          onChange={(event) => setModel(event.target.value)}
                          list="nova-discovered-models"
                          placeholder={provider === "ollama" ? "例如 qwen3:8b" : "填写服务端模型 ID"}
                        />
                        <datalist id="nova-discovered-models">
                          {[...new Set([...discoveredModels, ...providerModels[provider]])].map((item) => (
                            <option value={item} key={item} />
                          ))}
                        </datalist>
                      </>
                    ) : (
                      <select value={model} onChange={(event) => setModel(event.target.value)}>
                        {providerModels[provider].map((item) => <option value={item} key={item}>{item}</option>)}
                      </select>
                    )}
                  </label>
                  {(provider === "ollama" || provider === "custom") && (
                    <label>
                      <span>{provider === "ollama" ? "Ollama 地址" : "Base URL / Chat Completions 地址"}</span>
                      <input
                        value={modelEndpoint}
                        onChange={(event) => setModelEndpoint(event.target.value)}
                        spellCheck={false}
                        placeholder={
                          provider === "ollama"
                            ? "http://127.0.0.1:11434"
                            : "https://your-provider.example/v1"
                        }
                      />
                      <small>
                        可填 Base URL 或完整 /chat/completions 地址；NOVA 会先读取模型列表验证连接。
                      </small>
                    </label>
                  )}
                  <label>
                    <span>
                      API Key
                      {(provider === "ollama" || provider === "custom") && "（可选）"}
                    </span>
                    <input
                      type="password"
                      value={apiKey}
                      onChange={(event) => setApiKey(event.target.value)}
                      autoComplete="off"
                      placeholder={
                        provider === "ollama"
                          ? "本地 Ollama 通常无需密钥"
                          : `输入 ${providerLabels[provider]} API Key`
                      }
                    />
                  </label>
                  <div className="security-note">
                    <ShieldCheck size={17} />
                    <span>
                      密钥只在本次主进程内存中使用；远程接口强制 HTTPS，本机与局域网模型可使用 HTTP。
                    </span>
                  </div>
                  <div className="modal-actions">
                    <button
                      className="primary"
                      disabled={
                        !model.trim() ||
                        ((provider === "ollama" || provider === "custom")
                          ? !modelEndpoint.trim()
                          : apiKey.trim().length < 12)
                      }
                    >
                      验证并连接
                    </button>
                  </div>
                </form>
              )}

              {settingsSection === "mcp" && (
                <div className="capability-list">
                  {capabilitiesLoading && <p className="loading-row">正在读取 MCP 注册表…</p>}
                  {!capabilitiesLoading && !capabilities?.mcp.length && (
                    <p className="empty-row">当前没有已注册 MCP，可在“组件”中选择并加载。</p>
                  )}
                  {capabilities?.mcp.map((server) => (
                    <article key={server.name}>
                      <div><strong>{server.name}</strong><small>{server.transport} · {server.url || server.command}</small></div>
                      <button
                        type="button"
                        className={server.enabled ? "enabled" : ""}
                        onClick={async () => {
                          await window.nova.capabilities.setMcpEnabled({
                            name: server.name,
                            enabled: !server.enabled
                          });
                          await loadCapabilities();
                        }}
                      >
                        {server.enabled ? "已启用" : "启用"}
                      </button>
                    </article>
                  ))}
                </div>
              )}

              {settingsSection === "skills" && (
                <div className="capability-list">
                  {capabilitiesLoading && <p className="loading-row">正在读取 Skills…</p>}
                  {!capabilitiesLoading && !capabilities?.skills.length && (
                    <p className="empty-row">当前没有已安装 Skill，可在“组件”中加载。</p>
                  )}
                  {capabilities?.skills.map((skill) => (
                    <article key={skill.id}>
                      <div><strong>{skill.name}</strong><small>{skill.description || `${skill.fileCount} 个文件`}</small></div>
                      <button
                        type="button"
                        className={skill.enabled ? "enabled" : ""}
                        onClick={async () => {
                          await window.nova.capabilities.setSkillEnabled({
                            id: skill.id,
                            enabled: !skill.enabled
                          });
                          await loadCapabilities();
                        }}
                      >
                        {skill.enabled ? "已启用" : "启用"}
                      </button>
                    </article>
                  ))}
                </div>
              )}

              {settingsSection === "growth" && (
                <div className="growth-center">
                  <section className="growth-hero">
                    <div>
                      <span>Living AgentOS</span>
                      <strong>让 NOVA 学会你的工作方式</strong>
                      <small>
                        只从本机任务记录提取候选习惯；未经你确认，不会进入长期画像或变成 Skill。
                      </small>
                    </div>
                    <button
                      type="button"
                      disabled={growthLoading}
                      onClick={async () => {
                        setGrowthLoading(true);
                        try {
                          setLivingMemory(await window.nova.growth.analyze());
                          setDesktopSnapshot(await window.nova.system.desktopSnapshot());
                          setNotice("本机工作记录已分析，候选习惯等待你确认");
                        } catch (error) {
                          setNotice(error instanceof Error ? error.message : "习惯分析失败");
                        } finally {
                          setGrowthLoading(false);
                        }
                      }}
                    >
                      <RefreshCw size={15} />
                      {growthLoading ? "正在分析" : "分析我的工作方式"}
                    </button>
                  </section>

                  <div className="growth-metrics">
                    <article>
                      <span>已分析任务</span>
                      <strong>{livingMemory?.tasksAnalyzed || 0}</strong>
                    </article>
                    <article>
                      <span>已确认习惯</span>
                      <strong>
                        {livingMemory?.habits.filter((habit) => habit.state === "accepted").length || 0}
                      </strong>
                    </article>
                    <article>
                      <span>可观察窗口</span>
                      <strong>{desktopSnapshot?.count || 0}</strong>
                    </article>
                  </div>

                  <section className="growth-section">
                    <header>
                      <div>
                        <strong>工作习惯候选</strong>
                        <small>只有“采用”的内容会进入后续模型上下文</small>
                      </div>
                    </header>
                    {!livingMemory?.habits.length && (
                      <p className="empty-row">尚未形成候选习惯。点击上方按钮分析本机任务记录。</p>
                    )}
                    <div className="habit-list">
                      {livingMemory?.habits.map((habit) => (
                        <article className={habit.state} key={habit.id}>
                          <div>
                            <span>{habit.category}</span>
                            <strong>{habit.statement}</strong>
                            <small>
                              {habit.evidenceCount} 个任务信号 · 置信度{" "}
                              {Math.round(habit.confidence * 100)}%
                            </small>
                          </div>
                          <div className="habit-actions">
                            <button
                              type="button"
                              className={habit.state === "accepted" ? "active" : ""}
                              onClick={async () => {
                                setLivingMemory(await window.nova.growth.setHabitState({
                                  id: habit.id,
                                  state: "accepted"
                                }));
                              }}
                            >
                              采用
                            </button>
                            <button
                              type="button"
                              className={habit.state === "rejected" ? "active" : ""}
                              onClick={async () => {
                                setLivingMemory(await window.nova.growth.setHabitState({
                                  id: habit.id,
                                  state: "rejected"
                                }));
                              }}
                            >
                              忽略
                            </button>
                          </div>
                        </article>
                      ))}
                    </div>
                  </section>

                  <section className="growth-section skill-distiller">
                    <header>
                      <div>
                        <strong>记忆炼成 Skill</strong>
                        <small>把已确认习惯编译成可查看、可停用、不可越权的个人 Skill</small>
                      </div>
                      <button
                        type="button"
                        disabled={!livingMemory?.habits.some((habit) => habit.state === "accepted")}
                        onClick={async () => {
                          try {
                            setLivingMemory(await window.nova.growth.distillSkill());
                            setNotice("个人工作流 Skill 候选已生成，安装前仍可审阅");
                          } catch (error) {
                            setNotice(error instanceof Error ? error.message : "Skill 蒸馏失败");
                          }
                        }}
                      >
                        生成候选 Skill
                      </button>
                    </header>
                    {livingMemory?.skillCandidates.map((skill) => (
                      <article className="skill-candidate" key={skill.id}>
                        <div>
                          <strong>{skill.name}</strong>
                          <small>{skill.description}</small>
                          <em>{skill.habitIds.length} 条已确认习惯 · {skill.id}</em>
                        </div>
                        <button
                          type="button"
                          disabled={skill.installed}
                          onClick={async () => {
                            setLivingMemory(await window.nova.growth.installSkill({ id: skill.id }));
                            await loadCapabilities();
                            setNotice("个人工作流 Skill 已安装并启用，可随时在 Skills 页停用");
                          }}
                        >
                          {skill.installed ? "已装载" : "审阅后装载"}
                        </button>
                      </article>
                    ))}
                  </section>

                  <section className="growth-section evolution-lab">
                    <header>
                      <div>
                        <strong>Evolution Lab</strong>
                        <small>
                          核心代码不进入实验区；模型只能生成声明式插件，通过审阅后作为可停用能力安装
                        </small>
                      </div>
                      <span className="lab-safety">
                        {evolutionLab?.policy.enabled ? "插件进化已开启" : "默认关闭"}
                      </span>
                    </header>

                    {evolutionLab && (
                      <form
                        key={evolutionLab.policy.updatedAt}
                        className="evolution-policy"
                        onSubmit={async (event) => {
                          event.preventDefault();
                          const values = new FormData(event.currentTarget);
                          setGrowthLoading(true);
                          try {
                            setEvolutionLab(
                              await window.nova.growth.configureEvolutionLab({
                                enabled: values.get("enabled") === "on",
                                scheduledDiscoveryEnabled:
                                  values.get("scheduledDiscoveryEnabled") === "on",
                                maxTokensPerExperiment: Number(values.get("maxTokensPerExperiment")),
                                monthlyTokenBudget: Number(values.get("monthlyTokenBudget")),
                                maxExperimentsPerWeek: Number(values.get("maxExperimentsPerWeek")),
                                maxModelRounds: Number(values.get("maxModelRounds"))
                              })
                            );
                            setNotice("插件进化开关与硬预算已经保存");
                          } catch (error) {
                            setNotice(error instanceof Error ? error.message : "进化预算保存失败");
                          } finally {
                            setGrowthLoading(false);
                          }
                        }}
                      >
                        <label className="policy-switch">
                          <input
                            type="checkbox"
                            name="enabled"
                            defaultChecked={evolutionLab.policy.enabled}
                          />
                          <span>
                            <strong>允许插件式自进化</strong>
                            <small>关闭时不能创建实验，也不能调用模型继续已有实验</small>
                          </span>
                        </label>
                        <label className="policy-switch">
                          <input
                            type="checkbox"
                            name="scheduledDiscoveryEnabled"
                            defaultChecked={evolutionLab.policy.scheduledDiscoveryEnabled}
                          />
                          <span>
                            <strong>允许定时提出候选</strong>
                            <small>只提出本地候选，不自动调用模型、不自动安装</small>
                          </span>
                        </label>
                        <div className="policy-grid">
                          <label>
                            <span>单次 Token 上限</span>
                            <input
                              name="maxTokensPerExperiment"
                              type="number"
                              min="2000"
                              max="64000"
                              step="1000"
                              defaultValue={evolutionLab.policy.maxTokensPerExperiment}
                            />
                          </label>
                          <label>
                            <span>每月 Token 上限</span>
                            <input
                              name="monthlyTokenBudget"
                              type="number"
                              min="5000"
                              max="2000000"
                              step="5000"
                              defaultValue={evolutionLab.policy.monthlyTokenBudget}
                            />
                          </label>
                          <label>
                            <span>每周实验上限</span>
                            <input
                              name="maxExperimentsPerWeek"
                              type="number"
                              min="1"
                              max="20"
                              defaultValue={evolutionLab.policy.maxExperimentsPerWeek}
                            />
                          </label>
                          <label>
                            <span>单次模型轮数</span>
                            <input
                              name="maxModelRounds"
                              type="number"
                              min="1"
                              max="12"
                              defaultValue={evolutionLab.policy.maxModelRounds}
                            />
                          </label>
                        </div>
                        <div className="policy-footer">
                          <span>
                            本月已预留 {evolutionLab.usedTokensThisMonth.toLocaleString()} /{" "}
                            {evolutionLab.policy.monthlyTokenBudget.toLocaleString()} Token
                          </span>
                          <button type="submit" disabled={growthLoading}>保存开关与预算</button>
                        </div>
                      </form>
                    )}

                    <form
                      className="evolution-proposal"
                      onSubmit={async (event) => {
                        event.preventDefault();
                        if (!workspace) {
                          setNotice("请先选择需要改进的源码工作区");
                          return;
                        }
                        setGrowthLoading(true);
                        try {
                          const state = await window.nova.growth.proposeEvolution({
                            workspaceRoot: workspace,
                            objective: evolutionObjective.trim()
                          });
                          setEvolutionLab(state);
                          setEvolutionObjective("");
                          setNotice("改进假设已进入实验室；尚未复制源码或运行任何命令");
                        } catch (error) {
                          setNotice(error instanceof Error ? error.message : "创建进化实验失败");
                        } finally {
                          setGrowthLoading(false);
                        }
                      }}
                    >
                      <input
                        value={evolutionObjective}
                        onChange={(event) => setEvolutionObjective(event.target.value)}
                        placeholder="例如：减少任务卡住时的等待感，并保留真实执行证据"
                        minLength={8}
                        maxLength={1200}
                        disabled={!workspace || !evolutionLab?.policy.enabled || growthLoading}
                      />
                      <button
                        type="submit"
                        disabled={
                          !workspace
                          || !evolutionLab?.policy.enabled
                          || evolutionObjective.trim().length < 8
                          || growthLoading
                        }
                      >
                        提出安全实验
                      </button>
                    </form>

                    <div className="evolution-metrics">
                      <span>进行中 {evolutionLab?.activeExperiments || 0}</span>
                      <span>验证通过 {evolutionLab?.passedExperiments || 0}</span>
                      <span>已采纳 {evolutionLab?.adoptedExperiments || 0}</span>
                      <span>剩余 {evolutionLab?.remainingTokensThisMonth.toLocaleString() || 0} Token</span>
                    </div>

                    {!evolutionLab?.experiments.length && (
                      <p className="empty-row">
                        还没有插件实验。NOVA 不会接触核心源码，也不会在后台偷偷调用模型或安装能力。
                      </p>
                    )}

                    <div className="evolution-list">
                      {evolutionLab?.experiments.slice(0, 6).map((experiment) => (
                        <article className={`evolution-card ${experiment.state}`} key={experiment.id}>
                          <div className="evolution-card-head">
                            <div>
                              <span>{experiment.state}</span>
                              <strong>{experiment.objective}</strong>
                              <small>{experiment.hypothesis}</small>
                            </div>
                            <em>{experiment.id}</em>
                          </div>

                          {experiment.isolatedWorkspace && (
                            <div className="evolution-path">
                              隔离副本 · {experiment.isolatedWorkspace}
                            </div>
                          )}

                          {experiment.changedFiles.length > 0 && (
                            <div className="evolution-changes">
                              {experiment.changedFiles.slice(0, 5).map((file) => (
                                <span key={`${file.kind}-${file.path}`}>
                                  {file.kind} · {file.path}
                                </span>
                              ))}
                              {experiment.changedFiles.length > 5 && (
                                <span>另有 {experiment.changedFiles.length - 5} 个文件</span>
                              )}
                            </div>
                          )}

                          {experiment.blockers.length > 0 && (
                            <div className="evolution-blockers">
                              {experiment.blockers.map((blocker) => (
                                <span key={blocker}>{blocker}</span>
                              ))}
                            </div>
                          )}

                          <div className="evolution-evidence">
                            <span>
                              {experiment.verificationPassed === true
                                ? "验证通过"
                                : experiment.verificationPassed === false
                                  ? "验证失败"
                                  : "等待验证"}
                            </span>
                            <small>{experiment.verificationCommand}</small>
                          </div>

                          <div className="evolution-actions">
                            {experiment.state === "proposed" && (
                              <button
                                type="button"
                                onClick={async () => {
                                  setGrowthLoading(true);
                                  try {
                                    setEvolutionLab(
                                      await window.nova.growth.prepareEvolution({ id: experiment.id })
                                    );
                                    setNotice("声明式插件沙箱已经准备完成；没有复制核心源码，也没有消耗 Token");
                                  } catch (error) {
                                    setNotice(
                                      error instanceof Error ? error.message : "隔离实验准备失败"
                                    );
                                  } finally {
                                    setGrowthLoading(false);
                                  }
                                }}
                              >
                                准备插件沙箱
                              </button>
                            )}
                            {experiment.state === "ready" && experiment.isolatedWorkspace && (
                              <button
                                type="button"
                                className="primary"
                                onClick={() => {
                                  setWorkspace(experiment.isolatedWorkspace || null);
                                  setDraft(experiment.agentPrompt);
                                  setExecutionMode("Build");
                                  setSettingsOpen(false);
                                  setNotice(
                                    `已进入插件沙箱；本次最多 ${experiment.tokenBudget.toLocaleString()} Token，核心源码不可见`
                                  );
                                }}
                              >
                                交给 Agent 生成插件
                              </button>
                            )}
                            {["ready", "running", "failed"].includes(experiment.state) && (
                              <button
                                type="button"
                                onClick={async () => {
                                  setGrowthLoading(true);
                                  try {
                                    setEvolutionLab(
                                      await window.nova.growth.evaluateEvolution({ id: experiment.id })
                                    );
                                    setNotice("隔离差异与项目验证已更新");
                                  } catch (error) {
                                    setNotice(
                                      error instanceof Error ? error.message : "实验验证失败"
                                    );
                                  } finally {
                                    setGrowthLoading(false);
                                  }
                                }}
                              >
                                静态检查插件
                              </button>
                            )}
                            {experiment.state === "passed" && (
                              <button
                                type="button"
                                className="adopt"
                                onClick={async () => {
                                  setGrowthLoading(true);
                                  try {
                                    setEvolutionLab(
                                      await window.nova.growth.adoptEvolution({ id: experiment.id })
                                    );
                                    await loadCapabilities();
                                    setNotice("插件已作为可停用 Skill 安装；NOVA 核心没有被修改");
                                  } catch (error) {
                                    setNotice(
                                      error instanceof Error ? error.message : "插件安装失败，核心没有被修改"
                                    );
                                  } finally {
                                    setGrowthLoading(false);
                                  }
                                }}
                              >
                                审阅后安装
                              </button>
                            )}
                            {!["adopted", "rejected"].includes(experiment.state) && (
                              <button
                                type="button"
                                className="quiet"
                                onClick={async () => {
                                  setEvolutionLab(
                                    await window.nova.growth.rejectEvolution({ id: experiment.id })
                                  );
                                  setNotice("插件实验已放弃，核心与能力仓没有被修改");
                                }}
                              >
                                放弃
                              </button>
                            )}
                          </div>
                        </article>
                      ))}
                    </div>
                  </section>

                  <section className="growth-section desktop-pilot">
                    <header>
                      <div>
                        <strong>Desktop Pilot</strong>
                        <small>观察默认开启；点击、输入和按键只在明确授权的本轮生效</small>
                      </div>
                      <button type="button" onClick={() => void loadGrowthState(true)}>
                        刷新窗口
                      </button>
                    </header>
                    <div className="desktop-window-list">
                      {desktopSnapshot?.windows.slice(0, 8).map((windowItem) => (
                        <article key={windowItem.windowId}>
                          <Activity size={15} />
                          <div>
                            <strong>{windowItem.title}</strong>
                            <small>
                              {windowItem.processName} · {windowItem.bounds.width}×
                              {windowItem.bounds.height}
                            </small>
                          </div>
                          <span className={windowItem.inputProtected ? "protected" : ""}>
                            {windowItem.inputProtected ? "仅观察" : "可申请操作"}
                          </span>
                        </article>
                      ))}
                    </div>
                  </section>
                </div>
              )}

              {settingsSection === "ssh" && (
                <form
                  key={String(extensionProfiles.ssh[0]?.id || "new-ssh")}
                  className="dock-form"
                  onSubmit={async (event) => {
                    event.preventDefault();
                    const values = Object.fromEntries(new FormData(event.currentTarget));
                    await window.nova.extensions.saveSshProfile(values);
                    setNotice("SSH 配置已保存；密码和私钥内容未进入 NOVA 配置");
                    setSettingsOpen(false);
                  }}
                >
                  <div className="field-grid">
                    <label><span>主机</span><input name="host" defaultValue={extensionProfiles.ssh[0]?.host || ""} required placeholder="server.example.com" /></label>
                    <label><span>端口</span><input name="port" defaultValue={extensionProfiles.ssh[0]?.port || "22"} required /></label>
                  </div>
                  <input type="hidden" name="id" value={extensionProfiles.ssh[0]?.id || ""} />
                  <label><span>用户名</span><input name="username" defaultValue={extensionProfiles.ssh[0]?.username || ""} required placeholder="developer" /></label>
                  <label>
                    <span>认证方式</span>
                    <select name="authentication" defaultValue={extensionProfiles.ssh[0]?.authentication || "agent"}>
                      <option value="agent">系统 ssh-agent</option>
                      <option value="key">私钥文件</option>
                    </select>
                  </label>
                  <label><span>私钥路径（使用 ssh-agent 时留空）</span><input name="keyPath" defaultValue={extensionProfiles.ssh[0]?.keyPath || ""} placeholder="C:\Users\you\.ssh\id_ed25519" /></label>
                  <label><span>远程工作目录</span><input name="remoteRoot" defaultValue={extensionProfiles.ssh[0]?.remoteRoot || ""} placeholder="/workspace/project" /></label>
                  <div className="security-note"><ShieldCheck size={17} /><span>密码和私钥内容不保存在 Electron 设置中。</span></div>
                  <div className="modal-actions">
                    <button
                      type="button"
                      onClick={async (event) => {
                        const form = event.currentTarget.closest("form");
                        if (!form) return;
                        try {
                          await window.nova.extensions.testSshProfile(
                            Object.fromEntries(new FormData(form))
                          );
                          setNotice("SSH 只读连通性测试通过");
                        } catch (error) {
                          setNotice(error instanceof Error ? error.message : "SSH 测试失败");
                        }
                      }}
                    >
                      测试连接
                    </button>
                    <button className="primary">保存 SSH 配置</button>
                  </div>
                </form>
              )}

              {settingsSection === "cloud" && (
                <form
                  key={String(extensionProfiles.cloud[0]?.id || "new-cloud")}
                  className="dock-form"
                  onSubmit={async (event) => {
                    event.preventDefault();
                    const values = Object.fromEntries(new FormData(event.currentTarget));
                    await window.nova.extensions.saveCloudAdapter(values);
                    setNotice("云开发适配器配置已保存，凭证仍由对应 CLI 或系统凭据管理");
                    setSettingsOpen(false);
                  }}
                >
                  <label>
                    <span>适配器</span>
                    <select name="provider" defaultValue={extensionProfiles.cloud[0]?.provider || "generic"}>
                      <option value="generic">通用远程工作区</option>
                      <option value="github-codespaces">GitHub Codespaces</option>
                      <option value="aliyun-devstudio">阿里云 DevStudio</option>
                      <option value="tencent-cloud">腾讯云开发</option>
                    </select>
                  </label>
                  <input type="hidden" name="id" value={extensionProfiles.cloud[0]?.id || ""} />
                  <label><span>项目或工作区标识</span><input name="project" defaultValue={extensionProfiles.cloud[0]?.project || ""} required placeholder="组织/项目或实例 ID" /></label>
                  <label><span>区域（可选）</span><input name="region" defaultValue={extensionProfiles.cloud[0]?.region || ""} placeholder="cn-hangzhou" /></label>
                  <div className="security-note"><Cloud size={17} /><span>连接凭证由提供方 CLI 管理，NOVA 只保存项目映射。</span></div>
                  <div className="modal-actions"><button className="primary">保存适配器</button></div>
                </form>
              )}

              {settingsSection === "plugins" && (
                <div className="store-shell">
                  <section className="store-hero">
                    <div>
                      <strong>能力商店</strong>
                      <span>连接开放目录，搜索后再决定是否登记或安装。</span>
                    </div>
                    <div className="store-source-pills">
                      {(storeSources.length ? storeSources : [
                        { id: "mcp-official", name: "MCP 官方 Registry", kind: "mcp" as const },
                        { id: "skillmd", name: "SkillMD", kind: "skill" as const }
                      ]).map((source) => (
                        <span key={source.id}>{source.kind.toUpperCase()} · {source.name}</span>
                      ))}
                    </div>
                  </section>
                  <form className="store-search" onSubmit={searchCapabilityStore}>
                    <select
                      value={storeKind}
                      onChange={(event) => setStoreKind(event.target.value as typeof storeKind)}
                    >
                      <option value="all">全部能力</option>
                      <option value="mcp">MCP 超市</option>
                      <option value="skill">Skills 超市</option>
                    </select>
                    <input
                      value={storeQuery}
                      onChange={(event) => setStoreQuery(event.target.value)}
                      placeholder="搜索 GitHub、数据库、浏览器、设计、测试…"
                    />
                    <button type="submit" disabled={storeLoading}>
                      {storeLoading ? <RefreshCw size={15} /> : <Sparkles size={15} />}
                      {storeLoading ? "读取目录" : "搜索目录"}
                    </button>
                  </form>
                  {!storeItems.length && !storeLoading && (
                    <div className="store-empty">
                      <Boxes size={25} />
                      <strong>目录不会在后台偷偷联网</strong>
                      <span>点击“搜索目录”后，NOVA 才会读取 MCP 官方 Registry 与 SkillMD。</span>
                    </div>
                  )}
                  {!!storeItems.length && (
                    <div className="marketplace-list store-results">
                      {storeItems.map((item) => (
                        <article key={item.id}>
                          <span className="kind-badge">{item.kind.toUpperCase()}</span>
                          <div>
                            <strong>{item.name}</strong>
                            <small>{item.description}</small>
                            <em>{item.sourceLabel} · {item.publisher} · {item.trustLabel}</em>
                          </div>
                          <button
                            type="button"
                            disabled={!item.installable}
                            onClick={() => setPendingStoreItem(item)}
                          >
                            {item.actionLabel}
                          </button>
                        </article>
                      ))}
                    </div>
                  )}
                  <div className="store-section-title">
                    <span>内置精选</span>
                    <small>经过 NOVA 审阅，可离线查看</small>
                  </div>
                  <div className="marketplace-list">
                    {capabilitiesLoading && <p className="loading-row">正在读取内置能力…</p>}
                    {capabilities?.marketplace.map((item) => (
                      <article key={item.id}>
                        <span className="kind-badge">{item.kind.toUpperCase()}</span>
                        <div>
                          <strong>{item.name}</strong>
                          <small>{item.description}</small>
                          <em>{item.publisher} · {item.riskLabel} · {item.stateLabel}</em>
                        </div>
                        <button
                          type="button"
                          disabled={item.isEnabled}
                          onClick={() => setPendingBundledItem(item)}
                        >
                          {item.isEnabled ? "已加载" : item.isInstalled ? "启用" : "加载"}
                        </button>
                      </article>
                    ))}
                  </div>
                </div>
              )}
            </section>
          </div>
        </div>
      )}

      {approvalOpen && pendingSubmission && (
        <div className="modal-layer approval-layer">
          <div className="approval-modal">
            <header>
              <div className="approval-icon"><ShieldCheck size={20} /></div>
              <div>
                <span>执行前确认</span>
                <h2>NOVA 可以对当前工作区做什么？</h2>
              </div>
            </header>
            <div className="approval-summary">
              <strong>{pendingSubmission.content}</strong>
              <span>{workspace}</span>
            </div>
            {executionMode !== "Ask" && executionMode !== "Plan" && (
              <button
                className="approval-option recommended"
                type="button"
                onClick={() => void executeSubmission("workspace")}
              >
                <span>
                  <strong>
                    {executionMode === "Autopilot"
                      ? "智能审核并启动 Agent 工作组"
                      : "智能审核后执行"}
                  </strong>
                  <small>
                    {executionMode === "Autopilot"
                      ? "自动放行当前工作区内的低风险修改、受限构建和子 Agent 协作；越界或外部副作用不会自动执行。"
                      : "自动审核并放行当前工作区内的低风险修改、受限构建测试和后台公开资料读取；越界操作不会自动执行。"}
                  </small>
                </span>
                <Check size={18} />
              </button>
            )}
            {executionMode !== "Ask" && executionMode !== "Plan" && (
              <button
                className="approval-option desktop-access"
                type="button"
                onClick={() => void executeSubmission("workspaceDesktop")}
              >
                <span>
                  <strong>允许本轮操作桌面与工作区</strong>
                  <small>
                    除低风险工程操作外，可切换窗口、定点点击、输入文字和发送有限按键。仅本轮有效；终端、安全软件、密码管理器与 NOVA 自身始终禁止注入。
                  </small>
                </span>
                <Activity size={18} />
              </button>
            )}
            <button
              className="approval-option"
              type="button"
              onClick={() => void executeSubmission("readOnly")}
            >
              <span><strong>仅分析，不修改</strong><small>允许读取和推理；任何写入类工具请求都会被拒绝。</small></span>
            </button>
            <div className="approval-actions">
              <button
                type="button"
                onClick={() => {
                  setApprovalOpen(false);
                  setDraft(pendingSubmission.content);
                  setAttachments(pendingSubmission.attachments);
                  setPendingSubmission(null);
                }}
              >
                返回修改
              </button>
            </div>
          </div>
        </div>
      )}

      {pendingArchiveTask && (
        <div className="modal-layer archive-confirm-layer">
          <div className="archive-confirm-modal">
            <div className="archive-confirm-icon"><Archive size={21} /></div>
            <div>
              <span>整理任务空间</span>
              <h2>归档“{pendingArchiveTask.title || "未命名任务"}”？</h2>
              <p>任务上下文、交付结果和证据都会保留，只是不再占用左侧任务空间。之后可从归档库一键恢复。</p>
            </div>
            <div className="archive-confirm-actions">
              <button type="button" onClick={() => setPendingArchiveTask(null)}>取消</button>
              <button
                type="button"
                className="primary"
                onClick={() => void archiveTask(pendingArchiveTask)}
              >
                归档并保留记录
              </button>
            </div>
          </div>
        </div>
      )}

      {archiveLibraryOpen && (
        <div
          className="modal-layer archive-library-layer"
          onMouseDown={(event) => {
            if (event.currentTarget === event.target) setArchiveLibraryOpen(false);
          }}
        >
          <div className="archive-library-modal">
            <header>
              <div>
                <span>任务档案</span>
                <h2>归档库</h2>
                <p>归档不是删除。上下文、文件结果和执行证据仍然完整保留。</p>
              </div>
              <button type="button" onClick={() => setArchiveLibraryOpen(false)}><X size={18} /></button>
            </header>
            <div className="archive-library-list">
              {archivedTasks.length ? archivedTasks.map((task) => (
                <article key={task.id}>
                  <span className={`task-state ${statusTone(task.status)}`} />
                  <div>
                    <strong>{task.title || "未命名任务"}</strong>
                    <small>{taskSubtitle(task)}</small>
                  </div>
                  <button type="button" onClick={() => void restoreArchivedTask(task)}>
                    <RefreshCw size={15} />
                    恢复到任务空间
                  </button>
                </article>
              )) : (
                <div className="archive-library-empty">
                  <Archive size={25} />
                  <strong>归档库还是空的</strong>
                  <span>在任务卡右侧点击归档按钮，即可整理任务空间。</span>
                </div>
              )}
            </div>
          </div>
        </div>
      )}

      {pendingStoreItem && (
        <div className="modal-layer store-install-layer">
          <div className="store-install-modal">
            <header>
              <span className="kind-badge">{pendingStoreItem.kind.toUpperCase()}</span>
              <div>
                <span>{pendingStoreItem.sourceLabel}</span>
                <h2>加载“{pendingStoreItem.name}”？</h2>
              </div>
            </header>
            <dl>
              <div><dt>来源</dt><dd>{pendingStoreItem.publisher} · {pendingStoreItem.trustLabel}</dd></div>
              <div><dt>权限边界</dt><dd>{pendingStoreItem.permissionSummary}</dd></div>
              <div><dt>运行要求</dt><dd>{pendingStoreItem.requirements}</dd></div>
            </dl>
            <p>
              {pendingStoreItem.kind === "mcp"
                ? "MCP 只会先登记并保持停用，不会立即启动进程或访问账号。"
                : "NOVA 只下载并校验 SKILL.md 文本；不从商店安装二进制和可执行脚本。"}
            </p>
            <div className="archive-confirm-actions">
              <button type="button" onClick={() => setPendingStoreItem(null)}>取消</button>
              <button
                type="button"
                className="primary"
                onClick={() => void installStoreItem(pendingStoreItem)}
              >
                确认加载
              </button>
            </div>
          </div>
        </div>
      )}

      {pendingBundledItem && (
        <div className="modal-layer store-install-layer">
          <div className="store-install-modal">
            <header>
              <span className="kind-badge">{pendingBundledItem.kind.toUpperCase()}</span>
              <div>
                <span>NOVA 内置精选 · {pendingBundledItem.publisher}</span>
                <h2>加载“{pendingBundledItem.name}”？</h2>
              </div>
            </header>
            <dl>
              <div><dt>风险</dt><dd>{pendingBundledItem.riskLabel}</dd></div>
              <div><dt>权限边界</dt><dd>{pendingBundledItem.permissionSummary}</dd></div>
              <div><dt>运行要求</dt><dd>{pendingBundledItem.requirements}</dd></div>
            </dl>
            <p>加载只会登记此能力；需要外部账号、进程启动或更高权限时，NOVA 仍会在实际使用前单独说明。</p>
            <div className="archive-confirm-actions">
              <button type="button" onClick={() => setPendingBundledItem(null)}>取消</button>
              <button
                type="button"
                className="primary"
                onClick={() => void installBundledItem(pendingBundledItem)}
              >
                确认加载
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

export default App;
