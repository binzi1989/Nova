import {
  Activity,
  Archive,
  Bot,
  Boxes,
  BrainCircuit,
  BookOpen,
  Check,
  ChevronDown,
  ChevronRight,
  Circle,
  Clock3,
  Cloud,
  Copy,
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
  RotateCw,
  Search,
  Send,
  Settings2,
  ShieldCheck,
  Server,
  Sparkles,
  Square,
  Terminal,
  Trash2,
  Webhook,
  X,
  Zap
} from "lucide-react";
import { FormEvent, useEffect, useMemo, useRef, useState } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import type {
  AgentPackDetails,
  AgentCreationTemplate,
  AgentPackCreationRequest,
  AgentPackCreationResult,
  AgentWorkshopRecommendation,
  AgentWorkshopDesignSession,
  AgentWorkshopOrchestrationDraft,
  AgentWorkshopOrchestrationEvent,
  AgentWorkshopReadyEvent,
  AgentCalibrationPatch,
  AgentCalibrationSnapshot,
  AgentPackCapabilityReport,
  AgentFoundryBrief,
  AgentPackSummary,
  AgentEvent,
  AgentTask,
  Attachment,
  BootInfo,
  CapabilityState,
  DesktopSnapshot,
  DeliveryArtifact,
  DeliveryArtifactPreview,
  EvolutionDiscoveryEvent,
  EvolutionLabState,
  ExtensionGatewayState,
  GatewayActionRequest,
  KnowledgeGraphEdge,
  KnowledgeGraphNode,
  KnowledgeSearchResult,
  KnowledgeState,
  LivingMemoryState,
  ExecutionMode,
  Message,
  McpDiscoveryCandidate,
  McpDiscoveryResult,
  Provider,
  StoreCapabilityItem,
  ContextEvent,
  TaskCapsuleView,
  ToolApprovalRequestEvent,
  WorkspacePermissionGrant
} from "./types";

type SettingsSection =
  | "agents"
  | "model"
  | "mcp"
  | "skills"
  | "knowledge"
  | "gateway"
  | "ssh"
  | "cloud"
  | "plugins"
  | "growth"
  | "permissions";
type PendingSubmission = {
  content: string;
  attachments: Attachment[];
};
type RuntimeInteractionKind = "progress" | "supplement" | "correction" | "stop";
type RuntimeChange = PendingSubmission & {
  id: string;
  taskId: string;
  kind: Exclude<RuntimeInteractionKind, "progress" | "stop">;
  queuedAt: string;
};
type ApprovalMode = "workspace" | "workspaceDesktop" | "readOnly";
type PlanStep = {
  id: string;
  title: string;
  detail: string;
  agent: string;
  status: "pending" | "running" | "done" | "failed";
  output?: string;
};

function mergeAttachments(...groups: Array<Attachment[] | undefined>): Attachment[] {
  const unique = new Map<string, Attachment>();
  for (const group of groups) {
    for (const item of group || []) {
      const key = item.path.trim().toLocaleLowerCase();
      if (!key) continue;
      unique.set(key, item);
    }
  }
  // Retain a useful task-level material set without allowing a long-running chat
  // to grow an unbounded multimodal request.
  return [...unique.values()].slice(-12);
}

function classifyRuntimeInteraction(
  content: string,
  submittedAttachments: Attachment[]
): RuntimeInteractionKind {
  const normalized = content.trim().toLocaleLowerCase("zh-CN");
  if (/^(停止|停下|终止|结束任务|取消任务|先停一下|stop|cancel)[。.!！?？\s]*$/i.test(normalized)) {
    return "stop";
  }
  if (
    /^(进度|进展|到哪了|怎么样了|现在在做什么|正在做什么|还在吗|卡住了吗|有结果了吗|status|progress)/i.test(normalized)
    || /^(怎么这么慢|为什么这么慢|还要多久|是不是卡住了)[。.!！?？\s]*$/i.test(normalized)
  ) {
    return "progress";
  }
  if (/(改成|改为|不要再|不要做|先别|优先做|方向改|纠正|需求变更|换成|撤销|重做|调整为|以.+为准)/i.test(normalized)) {
    return "correction";
  }
  if (submittedAttachments.length > 0) return "supplement";
  // During an active run, a substantive user message is more likely to be a
  // steering instruction than passive reference material. Treat imperative
  // language as a correction even when the user does not say the word “纠正”.
  if (/(请|需要|应该|必须|先|重点|别|不要|改|换|只要|以此|按这个|我要|我希望|记得|务必)/i.test(normalized)) {
    return "correction";
  }
  return "supplement";
}

function compileRuntimeChanges(changes: RuntimeChange[]): PendingSubmission {
  const correctionCount = changes.filter((item) => item.kind === "correction").length;
  const lines = changes.map((item, index) =>
    `${index + 1}. [${item.kind === "correction" ? "方向纠正" : "补充资料"}] ${item.content}`
  );
  return {
    content: [
      "请沿用当前任务、工作区、已验证结果和已保存上下文继续推进，不要另起一个无关任务。",
      correctionCount
        ? "用户在运行过程中修正了方向。以最新纠正为准；保留仍然有效的成果，只重做受影响部分。"
        : "用户在运行过程中补充了资料。请在下一安全步骤合入，不重复已经完成且仍有效的工作。",
      "",
      ...lines,
      "",
      "先说明这些变化影响哪些后续步骤，再继续执行、验证并交付。"
    ].join("\n"),
    attachments: mergeAttachments(...changes.map((item) => item.attachments))
  };
}

function AttachmentThumbnail({
  file,
  onOpen
}: {
  file: Attachment;
  onOpen: (preview: { name: string; dataUrl: string }) => void;
}) {
  const [preview, setPreview] = useState<{ name: string; dataUrl: string } | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let active = true;
    setPreview(null);
    setFailed(false);
    void window.nova.system.previewAttachment({ path: file.path })
      .then((result) => {
        if (active) setPreview({ name: result.name, dataUrl: result.dataUrl });
      })
      .catch(() => {
        if (active) setFailed(true);
      });
    return () => {
      active = false;
    };
  }, [file.path]);

  if (!preview) {
    return (
      <span className="message-attachment-file">
        <Image size={15} />
        <span>{file.name}</span>
        {failed && <small>预览不可用</small>}
      </span>
    );
  }

  return (
    <button
      type="button"
      className="message-image-attachment"
      onClick={() => onOpen(preview)}
      aria-label={`查看图片 ${file.name}`}
    >
      <img src={preview.dataUrl} alt={file.name} />
      <span><Image size={14} />{file.name}</span>
    </button>
  );
}

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

type AgentWorkshopForm = Omit<
  AgentPackCreationRequest,
  "requiredInputs" | "recommendedInputs" | "starterPrompts"
>;

function generateAgentId() {
  const token = typeof globalThis.crypto?.randomUUID === "function"
    ? globalThis.crypto.randomUUID().replaceAll("-", "").slice(0, 16)
    : `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 10)}`;
  return `nova.user.agent-${token.toLowerCase()}`;
}

function createInitialAgentWorkshopForm(): AgentWorkshopForm {
  return {
    id: generateAgentId(),
    name: "",
    category: "",
    description: "",
    objective: "",
    scenarioProfile: "research",
    autonomyLevel: "assist",
    lifecycle: "single-run",
    collaborationMode: "independent",
    deliveryMode: "document",
    decisionStyle: "balanced",
    primaryArtifact: "研究分析报告.md"
  };
}

const calibrationCategoryLabels: Record<AgentCalibrationPatch["category"], string> = {
  fact: "事实不对",
  judgment: "判断偏差",
  workflow: "流程缺步",
  format: "格式不适合",
  evidence: "证据不足",
  permission: "权限方式",
  tone: "表达语气",
  other: "其他纠正"
};

const calibrationScopeLabels: Record<AgentCalibrationPatch["scope"], string> = {
  turn: "仅当前任务",
  project: "当前项目",
  agent: "该 Agent",
  organization: "组织版本（本机）"
};

function now() {
  return new Date().toLocaleTimeString("zh-CN", {
    hour: "2-digit",
    minute: "2-digit"
  });
}

function formatLocalDateTime(value?: string | null) {
  if (!value) return "尚未安排";
  return new Date(value).toLocaleString("zh-CN", {
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit"
  });
}

function formatDiscoveryWindow(value?: string | null) {
  if (!value) return "等待首次扫描";
  return new Date(value).getTime() <= Date.now()
    ? "已到扫描窗口，等待应用空闲"
    : `下次窗口 ${formatLocalDateTime(value)}`;
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

function approvalModeLabel(mode: ApprovalMode | null) {
  if (mode === "workspaceDesktop") return "桌面与工作区";
  if (mode === "workspace") return "工作区智能审核";
  if (mode === "readOnly") return "仅分析";
  return "执行前确认";
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

function parseDeliveryPresentation(content: string) {
  const metrics: Array<{ label: string; value: string }> = [];
  const artifacts: Array<{ label: string; path: string }> = [];
  let outcome: { verdict: string; reason: string } | null = null;
  let nextAction = "";

  const display = content
    .replace(
      /^\s*\[\[NOVA_OUTCOME\|([^|\]\r\n]{1,40})\|([^\]\r\n]{1,600})\]\]\s*$/gm,
      (_line, verdict: string, reason: string) => {
        outcome = { verdict: verdict.trim(), reason: reason.trim() };
        return "";
      }
    )
    .replace(
      /^\s*\[\[NOVA_METRIC\|([^|\]\r\n]{1,80})\|([^\]\r\n]{1,120})\]\]\s*$/gm,
      (_line, label: string, value: string) => {
        metrics.push({ label: label.trim(), value: value.trim() });
        return "";
      }
    )
    .replace(
      /^\s*\[\[NOVA_ARTIFACT\|([^|\]\r\n]{1,80})\|([^\]\r\n]{1,500})\]\]\s*$/gm,
      (_line, label: string, path: string) => {
        artifacts.push({ label: label.trim(), path: path.trim() });
        return "";
      }
    )
    .replace(
      /^\s*\[\[NOVA_NEXT\|([^\]\r\n]{1,600})\]\]\s*$/gm,
      (_line, value: string) => {
        nextAction = value.trim();
        return "";
      }
    )
    .replace(/\n{3,}/g, "\n\n")
    .trim();

  if (!outcome) {
    const verdict = content.match(/\b(CONDITIONAL\s+GO|NO[- ]GO|GO)\b/i)?.[1];
    if (verdict) outcome = { verdict: verdict.toUpperCase(), reason: "查看完整交付说明了解裁决依据" };
  }

  return {
    display,
    outcome,
    metrics: metrics.slice(0, 6),
    artifacts: artifacts.slice(0, 12),
    nextAction
  };
}

type PresentedArtifact = DeliveryArtifact | { label: string; path: string };

function presentedArtifactPath(artifact: PresentedArtifact) {
  return ("relativePath" in artifact ? artifact.relativePath : artifact.path)
    .replaceAll("\\", "/");
}

function isInternalPresentedArtifact(artifact: PresentedArtifact) {
  const relativePath = presentedArtifactPath(artifact).toLowerCase();
  return relativePath === ".nova" || relativePath.startsWith(".nova/");
}

function presentedArtifactPriority(artifact: PresentedArtifact) {
  const relativePath = presentedArtifactPath(artifact).toLowerCase();
  const fileName = relativePath.split("/").at(-1) || relativePath;
  let score = "role" in artifact && artifact.role === "primary" ? 80 : 0;
  if (/^(交付|成果|报告|deliverables?|reports?)\//i.test(relativePath)) score += 100;
  if (/(入口|总览|摘要|简报|报告|结论|建议|方案|清单|readme|summary|report|result)/i.test(fileName)) score += 55;
  if (/^(输入|解析结果|画像|审查|工具|input|raw|analysis|audit|tools?)\//i.test(relativePath)) score -= 35;
  if (/(proof-of-done|test_|\.test\.|\.spec\.|sha256|raw_texts)/i.test(relativePath)) score -= 60;
  return score;
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

function KnowledgeNetwork({
  nodes,
  edges,
  selectedId,
  onSelect
}: {
  nodes: KnowledgeGraphNode[];
  edges: KnowledgeGraphEdge[];
  selectedId: string | null;
  onSelect: (nodeId: string) => void;
}) {
  const layout = useMemo(() => {
    const visible = nodes.slice(0, 48);
    const center = visible.find((node) => node.kind === "KnowledgeSpace" || node.kind === "Project")
      || visible.find((node) => node.kind === "Goal")
      || visible[0];
    const inner = visible.filter((node) => node.id !== center?.id
      && ["Goal", "Input", "Document", "Artifact", "Knowledge"].includes(node.kind));
    const outer = visible.filter((node) => node.id !== center?.id && !inner.includes(node));
    const positions = new Map<string, { x: number; y: number; ring: "center" | "inner" | "outer" }>();
    if (center) positions.set(center.id, { x: 400, y: 225, ring: "center" });
    inner.forEach((node, index) => {
      const angle = -Math.PI / 2 + (Math.PI * 2 * index) / Math.max(1, inner.length);
      positions.set(node.id, {
        x: 400 + Math.cos(angle) * 142,
        y: 225 + Math.sin(angle) * 126,
        ring: "inner"
      });
    });
    outer.forEach((node, index) => {
      const angle = -Math.PI / 2 + (Math.PI * 2 * index) / Math.max(1, outer.length);
      positions.set(node.id, {
        x: 400 + Math.cos(angle) * 292,
        y: 225 + Math.sin(angle) * 190,
        ring: "outer"
      });
    });
    return {
      nodes: visible,
      positions,
      edges: edges.filter((edge) => positions.has(edge.sourceId) && positions.has(edge.targetId))
    };
  }, [edges, nodes]);

  if (!layout.nodes.length) return null;
  return (
    <svg className="knowledge-network" viewBox="0 0 800 450" role="img" aria-label="知识关系网络图">
      <g className="knowledge-network-edges">
        {layout.edges.map((edge, index) => {
          const source = layout.positions.get(edge.sourceId)!;
          const target = layout.positions.get(edge.targetId)!;
          return (
            <line
              key={`${edge.sourceId}-${edge.targetId}-${edge.relation}-${index}`}
              x1={source.x}
              y1={source.y}
              x2={target.x}
              y2={target.y}
              className={edge.isInferred ? "inferred" : "confirmed"}
            >
              <title>{edge.isInferred ? `可能映射 · ${Math.round((edge.confidence || 0) * 100)}% · ${edge.evidence || "共享概念"}` : edge.relation}</title>
            </line>
          );
        })}
      </g>
      <g className="knowledge-network-nodes">
        {layout.nodes.map((node) => {
          const position = layout.positions.get(node.id)!;
          const radius = position.ring === "center" ? 20 : position.ring === "inner" ? 14 : 10;
          const label = node.label.length > 11 ? `${node.label.slice(0, 11)}…` : node.label;
          return (
            <g
              key={node.id}
              className={`network-node ${node.kind.toLowerCase()} ${selectedId === node.id ? "selected" : ""}`}
              transform={`translate(${position.x} ${position.y})`}
              role="button"
              tabIndex={0}
              onClick={() => onSelect(node.id)}
              onKeyDown={(event) => {
                if (event.key === "Enter" || event.key === " ") onSelect(node.id);
              }}
            >
              <circle r={radius} />
              <text y={radius + 15} textAnchor="middle">{label}</text>
              <title>{node.kind} · {node.label}\n{node.detail}</title>
            </g>
          );
        })}
      </g>
    </svg>
  );
}

const knowledgeKindLabels: Record<string, string> = {
  KnowledgeSpace: "知识空间",
  Project: "项目",
  Goal: "目标",
  Input: "用户输入",
  Document: "资料",
  Artifact: "交付物",
  Knowledge: "确认知识",
  Concept: "关键概念",
  Skill: "技能",
  MCP: "外部能力",
  Provider: "模型服务",
  Model: "模型"
};

function knowledgeRelationLabel(edge: KnowledgeGraphEdge) {
  if (edge.isInferred) return "可能有关";
  const labels: Record<string, string> = {
    "belongs to": "属于",
    "contributes to": "支持目标",
    "contains": "包含",
    "produced": "产生",
    "mentions": "提到",
    "related to": "相关",
    "confirmed mapping": "已确认关联",
    "uses": "使用"
  };
  return labels[edge.relation] || edge.relation;
}

function KnowledgeExplorerWindow() {
  const params = new URLSearchParams(window.location.search);
  const workspace = params.get("workspace") || null;
  const [scope, setScope] = useState<"workspace" | "all">(workspace ? "workspace" : "all");
  const [mode, setMode] = useState<"wiki" | "overview" | "review" | "decisions" | "advanced">("wiki");
  const [state, setState] = useState<KnowledgeState | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [query, setQuery] = useState("");
  const [loading, setLoading] = useState(true);
  const [notice, setNotice] = useState("");

  async function reload(nextScope: "workspace" | "all" = scope) {
    setLoading(true);
    try {
      const result = await window.nova.knowledge.getState({
        workspace: nextScope === "workspace" ? workspace : null
      });
      setState(result);
      setSelectedId((current) => current && result.graph.nodes.some((node) => node.id === current)
        ? current
        : result.graph.nodes.find((node) => ["Goal", "Input", "Knowledge", "Artifact"].includes(node.kind))?.id
          || result.graph.nodes[0]?.id
          || null);
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "知识地图读取失败");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    void reload(scope);
  }, [scope]);

  const visibleGraph = useMemo(() => {
    if (!state) return { nodes: [] as KnowledgeGraphNode[], edges: [] as KnowledgeGraphEdge[] };
    const allNodes = state.graph.nodes;
    const allEdges = state.graph.edges;
    if (mode === "advanced") return { nodes: allNodes.slice(0, 48), edges: allEdges };
    const priority = (node: KnowledgeGraphNode) => {
      const rank: Record<string, number> = {
        Goal: 9, Input: 8, Knowledge: 8, Artifact: 7, Document: 6,
        Concept: 5, KnowledgeSpace: 4, Project: 4
      };
      return (rank[node.kind] || 0) * 100 + node.weight;
    };
    let candidates = mode === "review"
      ? allEdges.filter((edge) => edge.isInferred).flatMap((edge) => [edge.sourceId, edge.targetId]).slice(0, 18)
      : [...allNodes].sort((left, right) => priority(right) - priority(left)).slice(0, 12).map((node) => node.id);
    const retained = new Set(candidates);
    allEdges.forEach((edge) => {
      if (retained.has(edge.sourceId) && retained.size < 20) retained.add(edge.targetId);
      if (retained.has(edge.targetId) && retained.size < 20) retained.add(edge.sourceId);
    });
    const normalized = query.trim().toLocaleLowerCase("zh-CN");
    if (normalized) {
      const matches = allNodes.filter((node) => `${node.label} ${node.detail}`.toLocaleLowerCase("zh-CN").includes(normalized));
      retained.clear();
      matches.forEach((node) => retained.add(node.id));
      allEdges.forEach((edge) => {
        if (retained.has(edge.sourceId)) retained.add(edge.targetId);
        if (retained.has(edge.targetId)) retained.add(edge.sourceId);
      });
    }
    const nodes = allNodes.filter((node) => retained.has(node.id)).slice(0, 22);
    const ids = new Set(nodes.map((node) => node.id));
    return {
      nodes,
      edges: allEdges.filter((edge) => ids.has(edge.sourceId) && ids.has(edge.targetId))
    };
  }, [mode, query, state]);

  const selected = state?.graph.nodes.find((node) => node.id === selectedId) || null;
  const related = selected && state
    ? state.graph.edges
      .filter((edge) => edge.sourceId === selected.id || edge.targetId === selected.id)
      .map((edge) => ({
        edge,
        node: state.graph.nodes.find((node) => node.id === (edge.sourceId === selected.id ? edge.targetId : edge.sourceId))
      }))
      .filter((item) => item.node)
      .slice(0, 12)
    : [];
  const topics = visibleGraph.nodes
    .filter((node) => !["KnowledgeSpace", "Project", "Provider", "Model"].includes(node.kind))
    .slice(0, 12);
  const wikiPages = useMemo(() => {
    const pages = state?.knowledgeOs.wikiPages || [];
    const normalized = query.trim().toLocaleLowerCase("zh-CN");
    if (!normalized) return pages;
    return pages.filter((page) => `${page.title} ${page.entityType} ${page.summary} ${page.confirmedFacts.join(" ")} ${page.sourceLabels.join(" ")}`
      .toLocaleLowerCase("zh-CN")
      .includes(normalized));
  }, [query, state]);
  const decisions = state?.knowledgeOs.decisions || [];
  const triggeredDecisions = decisions.filter((item) => item.status === "triggered");

  async function review(edge: KnowledgeGraphEdge, accepted: boolean) {
    await window.nova.knowledge.reviewMapping({
      sourceId: edge.sourceId,
      targetId: edge.targetId,
      accepted
    });
    setNotice(accepted ? "已确认，这条联系会作为可靠知识保留" : "已忽略，NOVA 不会再次推荐这条联系");
    await reload();
  }

  return (
    <main className="knowledge-window-shell">
      <header className="knowledge-window-header">
        <div>
          <span>NOVA KNOWLEDGE MAP</span>
          <h1>知识地图</h1>
          <p>把散落在任务、文件和交付物里的知识连接起来，帮你找线索、查依据、发现可能被忽略的关系。</p>
        </div>
        <div className="knowledge-window-scope">
          <button className={scope === "workspace" ? "active" : ""} disabled={!workspace} onClick={() => setScope("workspace")}>当前项目</button>
          <button className={scope === "all" ? "active" : ""} onClick={() => setScope("all")}>全部知识</button>
          <button title="刷新" onClick={() => void reload()}><RefreshCw size={15} className={loading ? "spinning" : ""} /></button>
        </div>
      </header>

      <section className="knowledge-window-guide">
        <article><Search size={17} /><div><strong>找线索</strong><span>搜索一个产品、客户或问题，查看相关任务和资料。</span></div></article>
        <article><BookOpen size={17} /><div><strong>查依据</strong><span>点击节点查看来源，区分事实和模型推测。</span></div></article>
        <article><ShieldCheck size={17} /><div><strong>校准关系</strong><span>确认或忽略虚线关系，让知识网络越来越准确。</span></div></article>
      </section>

      <nav className="knowledge-window-tabs">
        <button className={mode === "wiki" ? "active" : ""} onClick={() => setMode("wiki")}>知识首页</button>
        <button className={mode === "overview" ? "active" : ""} onClick={() => setMode("overview")}>关系地图</button>
        <button className={mode === "review" ? "active" : ""} onClick={() => setMode("review")}>待确认关系 <i>{state?.graph.inferredEdgeCount || 0}</i></button>
        <button className={mode === "decisions" ? "active" : ""} onClick={() => setMode("decisions")}>规则判断 <i>{state?.knowledgeOs.decisions.filter((item) => item.status === "triggered").length || 0}</i></button>
        <button className={mode === "advanced" ? "active" : ""} onClick={() => setMode("advanced")}>完整网络</button>
        <label><Search size={14} /><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="搜索产品、客户、结论或资料" /></label>
      </nav>

      {notice && <div className="knowledge-window-notice">{notice}</div>}
      {mode === "wiki" ? (
        <section className="knowledge-wiki-home">
          <header className="knowledge-wiki-summary">
            <div>
              <span>KNOWLEDGE WIKI</span>
              <h2>先读懂，再看关系</h2>
              <p>NOVA 已把任务、资料与交付物整理成可阅读的知识页。这里展示已确认事实、待核实联系和来源，不需要先学会看图谱。</p>
            </div>
            <dl>
              <div><dt>{wikiPages.length}</dt><dd>知识页</dd></div>
              <div><dt>{state?.knowledgeOs.entityTypes.length || 0}</dt><dd>对象类型</dd></div>
              <div><dt>{state?.graph.edges.filter((edge) => !edge.isInferred || edge.reviewState === "accepted").length || 0}</dt><dd>确认关系</dd></div>
              <div className={triggeredDecisions.length ? "attention" : ""}><dt>{triggeredDecisions.length}</dt><dd>需要处理</dd></div>
            </dl>
          </header>
          {loading ? (
            <div className="knowledge-wiki-empty"><RefreshCw className="spinning" /><strong>正在生成本地知识页…</strong></div>
          ) : wikiPages.length ? (
            <div className="knowledge-wiki-grid">
              {wikiPages.map((page) => (
                <article key={page.id} className="knowledge-wiki-card">
                  <header><span>{page.entityType}</span><time>{new Date(page.updatedAt).toLocaleDateString("zh-CN")}</time></header>
                  <h3>{page.title}</h3>
                  <p>{page.summary}</p>
                  <div className="knowledge-wiki-facts">
                    <strong>已确认</strong>
                    {page.confirmedFacts.length
                      ? page.confirmedFacts.slice(0, 3).map((fact) => <span key={fact}><Check size={12} />{fact}</span>)
                      : <span className="muted">暂时没有已确认关系</span>}
                  </div>
                  <footer>
                    <small>{page.sourceLabels.length ? `来源：${page.sourceLabels.join(" · ")}` : "来源待补充"}</small>
                    <button onClick={() => { setSelectedId(page.id); setMode("overview"); }}>查看关系</button>
                  </footer>
                </article>
              ))}
            </div>
          ) : (
            <div className="knowledge-wiki-empty"><BookOpen /><strong>还没有知识页</strong><span>完成任务或索引工作区后，NOVA 会在本地自动整理。</span></div>
          )}
        </section>
      ) : mode === "decisions" ? (
        <section className="knowledge-rules-view">
          <header>
            <div><span>RULE ENGINE</span><h2>可解释的知识判断</h2><p>这些判断来自固定规则，不调用模型、不消耗 Token，也不会自动修改文件。</p></div>
            <strong className={triggeredDecisions.length ? "attention" : "clear"}>{triggeredDecisions.length ? `${triggeredDecisions.length} 项需要处理` : "当前没有阻断项"}</strong>
          </header>
          <div className="knowledge-rule-grid">
            {decisions.map((decision) => (
              <article key={decision.ruleId} className={decision.status === "triggered" ? `triggered ${decision.severity}` : "clear"}>
                <header><span>{decision.status === "triggered" ? "需要处理" : "当前正常"}</span><i>{decision.ruleId}</i></header>
                <h3>{decision.title}</h3>
                <p>{decision.explanation}</p>
                {!!decision.evidence.length && <div><strong>依据</strong>{decision.evidence.map((item) => <span key={item}>· {item}</span>)}</div>}
                <footer><ShieldCheck size={15} /><span>{decision.recommendation}</span></footer>
              </article>
            ))}
          </div>
        </section>
      ) : (
      <section className="knowledge-window-body">
        <aside className="knowledge-topic-panel">
          <header><strong>{mode === "review" ? "需要你判断" : "重点主题"}</strong><span>{topics.length} 项</span></header>
          {topics.length ? topics.map((node) => (
            <button key={node.id} className={selectedId === node.id ? "active" : ""} onClick={() => setSelectedId(node.id)}>
              <span>{knowledgeKindLabels[node.kind] || node.kind}</span>
              <strong>{node.label}</strong>
            </button>
          )) : <p>暂时没有需要展示的知识。完成任务或建立工作区索引后，这里会自动出现。</p>}
        </aside>

        <div className="knowledge-map-stage">
          {loading ? <div className="knowledge-map-empty"><RefreshCw className="spinning" /><strong>正在整理知识关系…</strong></div>
            : visibleGraph.nodes.length ? <KnowledgeNetwork nodes={visibleGraph.nodes} edges={visibleGraph.edges} selectedId={selectedId} onSelect={setSelectedId} />
              : <div className="knowledge-map-empty"><BookOpen /><strong>还没有可展示的知识</strong><span>先完成一个任务，或在主窗口为工作区建立知识索引。</span></div>}
        </div>

        <aside className="knowledge-detail-panel">
          {selected ? (
            <>
              <header><span>{knowledgeKindLabels[selected.kind] || selected.kind}</span><strong>{selected.label}</strong></header>
              <p>{selected.detail || "暂无补充说明。"}</p>
              <small>来源：{selected.sourceLabel || "NOVA 本地知识"}</small>
              <div className="knowledge-detail-relations">
                <strong>它与什么有关</strong>
                {related.length ? related.map(({ edge, node }) => (
                  <article key={`${edge.sourceId}-${edge.targetId}-${edge.relation}`}>
                    <button onClick={() => setSelectedId(node!.id)}>
                      <span>{knowledgeRelationLabel(edge)}</span>
                      <strong>{node!.label}</strong>
                      {edge.isInferred && <small>{Math.round((edge.confidence || 0) * 100)}% · {edge.evidence || "等待判断"}</small>}
                    </button>
                    {edge.isInferred && (
                      <div><button onClick={() => void review(edge, true)}><Check size={12} />确认</button><button onClick={() => void review(edge, false)}><X size={12} />忽略</button></div>
                    )}
                  </article>
                )) : <p>暂时没有直接关系。</p>}
              </div>
            </>
          ) : <div className="knowledge-detail-empty"><BrainCircuit /><strong>点击一个知识节点</strong><span>这里会用自然语言解释它的来源和联系。</span></div>}
        </aside>
      </section>
      )}
    </main>
  );
}

function MainApp() {
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
  const [crossModelReview, setCrossModelReview] = useState(false);
  const [connected, setConnected] = useState<Partial<Record<Provider, boolean>>>({});
  const [workspace, setWorkspace] = useState<string | null>(null);
  const [attachments, setAttachments] = useState<Attachment[]>([]);
  const [imagePreview, setImagePreview] = useState<{ name: string; dataUrl: string } | null>(null);
  const retainedTaskAttachments = useMemo(
    () => mergeAttachments(...messages.map((message) => message.attachments)),
    [messages]
  );
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [settingsSection, setSettingsSection] = useState<SettingsSection>("model");
  const [capabilities, setCapabilities] = useState<CapabilityState | null>(null);
  const [capabilitiesLoading, setCapabilitiesLoading] = useState(false);
  const [knowledgeState, setKnowledgeState] = useState<KnowledgeState | null>(null);
  const [knowledgeLoading, setKnowledgeLoading] = useState(false);
  const [knowledgeScope, setKnowledgeScope] = useState<"workspace" | "all">("workspace");
  const [knowledgeQuery, setKnowledgeQuery] = useState("");
  const [knowledgeResults, setKnowledgeResults] = useState<KnowledgeSearchResult[]>([]);
  const [knowledgeGraphQuery, setKnowledgeGraphQuery] = useState("");
  const [selectedKnowledgeNodeId, setSelectedKnowledgeNodeId] = useState<string | null>(null);
  const [agentPacks, setAgentPacks] = useState<AgentPackSummary[]>([]);
  const [agentPacksLoading, setAgentPacksLoading] = useState(false);
  const [agentCreationTemplates, setAgentCreationTemplates] =
    useState<AgentCreationTemplate[]>([]);
  const [agentWorkshopOpen, setAgentWorkshopOpen] = useState(false);
  const [agentFoundryOpen, setAgentFoundryOpen] = useState(false);
  const [agentFoundryGoal, setAgentFoundryGoal] = useState("");
  const [agentFoundryPreparing, setAgentFoundryPreparing] = useState(false);
  const [agentFoundryError, setAgentFoundryError] = useState("");
  const [agentWorkshopSimpleMode, setAgentWorkshopSimpleMode] = useState(false);
  const [agentCreating, setAgentCreating] = useState(false);
  const [agentBuildError, setAgentBuildError] = useState("");
  const [agentOrchestrating, setAgentOrchestrating] = useState(false);
  const [agentCreationResult, setAgentCreationResult] =
    useState<AgentPackCreationResult | null>(null);
  const [agentWorkshopForm, setAgentWorkshopForm] =
    useState<AgentWorkshopForm>(createInitialAgentWorkshopForm);
  const [agentWorkshopRecommendation, setAgentWorkshopRecommendation] =
    useState<AgentWorkshopRecommendation | null>(null);
  const [agentRecommendationLoading, setAgentRecommendationLoading] = useState(false);
  const [agentOrchestrationDraft, setAgentOrchestrationDraft] =
    useState<AgentWorkshopOrchestrationDraft | null>(null);
  const [agentOrchestrationEvents, setAgentOrchestrationEvents] =
    useState<AgentWorkshopOrchestrationEvent[]>([]);
  const [agentDesignSession, setAgentDesignSession] =
    useState<AgentWorkshopDesignSession | null>(null);
  const [agentCalibration, setAgentCalibration] =
    useState<AgentCalibrationSnapshot | null>(null);
  const [selectedAgentPackId, setSelectedAgentPackId] = useState<string | null>(null);
  const [inspectedAgentPack, setInspectedAgentPack] = useState<AgentPackDetails | null>(null);
  const [agentLaunchGuide, setAgentLaunchGuide] = useState<AgentPackDetails | null>(null);
  const [agentLaunchOpen, setAgentLaunchOpen] = useState(false);
  const [agentLaunchValues, setAgentLaunchValues] = useState<Record<string, string>>({});
  const [agentLaunchError, setAgentLaunchError] = useState("");
  const [agentCapabilityReport, setAgentCapabilityReport] =
    useState<AgentPackCapabilityReport | null>(null);
  const [mcpDiscovery, setMcpDiscovery] = useState<McpDiscoveryResult | null>(null);
  const [selectedMcpCandidates, setSelectedMcpCandidates] = useState<Set<string>>(
    () => new Set()
  );
  const [capabilityPreparing, setCapabilityPreparing] = useState(false);
  const [mcpConfigText, setMcpConfigText] = useState("");
  const [mcpAuthorizationEnvironment, setMcpAuthorizationEnvironment] = useState("");
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
  const [extensionGateway, setExtensionGateway] =
    useState<ExtensionGatewayState | null>(null);
  const [gatewayActionRequests, setGatewayActionRequests] =
    useState<GatewayActionRequest[]>([]);
  const [gatewayBusy, setGatewayBusy] = useState(false);
  const [apiKey, setApiKey] = useState("");
  const [runningTaskIds, setRunningTaskIds] = useState<Set<string>>(
    () => new Set()
  );
  const [streamingText, setStreamingText] = useState("");
  const [runtimePulse, setRuntimePulse] = useState("正在建立模型连接");
  const [selectedTaskId, setSelectedTaskId] = useState<string | null>(null);
  const running = selectedTaskId ? runningTaskIds.has(selectedTaskId) : false;
  const [pendingArchiveTask, setPendingArchiveTask] = useState<AgentTask | null>(null);
  const [pendingDeleteTask, setPendingDeleteTask] = useState<AgentTask | null>(null);
  const [archiveLibraryOpen, setArchiveLibraryOpen] = useState(false);
  const [deliveryReview, setDeliveryReview] = useState<{
    artifactId?: string;
    title: string;
    path?: string;
    content: string;
    kind: "markdown" | "text" | "image" | "document" | "external";
    truncated?: boolean;
  } | null>(null);
  const [deliveryReviewLoading, setDeliveryReviewLoading] = useState(false);
  const [deliveryReviewNote, setDeliveryReviewNote] = useState("");
  const [deliveryReviewMode, setDeliveryReviewMode] =
    useState<"rework" | "calibrate">("rework");
  const [calibrationCategory, setCalibrationCategory] =
    useState<AgentCalibrationPatch["category"]>("judgment");
  const [calibrationScope, setCalibrationScope] =
    useState<AgentCalibrationPatch["scope"]>("turn");
  const [pendingSubmission, setPendingSubmission] =
    useState<PendingSubmission | null>(null);
  const [approvalOpen, setApprovalOpen] = useState(false);
  const [rememberTaskPermission, setRememberTaskPermission] = useState(true);
  const [taskApprovalModes, setTaskApprovalModes] = useState<Record<string, ApprovalMode>>({});
  const [pendingToolApproval, setPendingToolApproval] =
    useState<ToolApprovalRequestEvent | null>(null);
  const [rememberToolPermission, setRememberToolPermission] = useState(true);
  const [rememberWorkspacePermission, setRememberWorkspacePermission] = useState(false);
  const [workspacePermissions, setWorkspacePermissions] = useState<WorkspacePermissionGrant[]>([]);
  const [currentRuntimeEvent, setCurrentRuntimeEvent] = useState<{
    agent: string;
    action: string;
    detail: string;
    progress: number;
  } | null>(null);
  const [runtimeChangeQueue, setRuntimeChangeQueue] = useState<RuntimeChange[]>([]);
  const [newTaskGuideOpen, setNewTaskGuideOpen] = useState(false);
  const [leftOpen, setLeftOpen] = useState(() => window.innerWidth > 1180);
  const [rightOpen, setRightOpen] = useState(false);
  const [taskQuery, setTaskQuery] = useState("");
  const [taskListExpanded, setTaskListExpanded] = useState(false);
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
  const [taskCapsule, setTaskCapsule] = useState<TaskCapsuleView | null>(null);
  const threadEnd = useRef<HTMLDivElement>(null);
  const selectedTaskIdRef = useRef<string | null>(null);
  const agentWorkshopSessionIdRef = useRef<string | null>(null);
  const taskRunIds = useRef<Map<string, string>>(new Map());
  const streamBuffer = useRef("");
  const streamFlushTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const lastRuntimeEventAt = useRef(Date.now());
  const runtimeChangeQueueRef = useRef<RuntimeChange[]>([]);
  const suppressQueuedResumeTasksRef = useRef<Set<string>>(new Set());

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
  const recentTaskWorkspaces = useMemo(() => Array.from(new Set(
    tasks
      .map((task) => task.workspaceRoot?.trim())
      .filter((value): value is string => Boolean(value))
  )).slice(0, 3), [tasks]);
  const filteredTasks = useMemo(() => {
    const query = taskQuery.trim().toLocaleLowerCase("zh-CN");
    return query
      ? tasks.filter((task) => `${task.title || ""} ${task.summary || ""}`.toLocaleLowerCase("zh-CN").includes(query))
      : tasks;
  }, [taskQuery, tasks]);
  const visibleTasks = useMemo(() => {
    if (taskQuery.trim() || taskListExpanded) return filteredTasks;
    const recent = filteredTasks.slice(0, 8);
    const selected = selectedTaskId
      ? filteredTasks.find((task) => task.id === selectedTaskId)
      : null;
    return selected && !recent.some((task) => task.id === selected.id)
      ? [selected, ...recent.slice(0, 7)]
      : recent;
  }, [filteredTasks, selectedTaskId, taskListExpanded, taskQuery]);
  const reviewCandidates = useMemo(
    () =>
      (Object.keys(providerLabels) as Provider[]).filter(
        (candidate) => candidate !== provider && connected[candidate]
      ),
    [connected, provider]
  );
  const selectedAgentPack = useMemo(
    () => agentPacks.find((pack) => pack.id === selectedAgentPackId) || null,
    [agentPacks, selectedAgentPackId]
  );
  const currentPlanStep = useMemo(
    () => taskPlan.find((step) => step.status === "running")
      || taskPlan.find((step) => step.status === "pending")
      || taskPlan.at(-1)
      || null,
    [taskPlan]
  );
  const completedPlanSteps = taskPlan.filter((step) => step.status === "done").length;
  const currentTaskApprovalMode = selectedTaskId
    ? taskApprovalModes[selectedTaskId] || null
    : null;
  const runtimeDraftKind = running && (draft.trim() || attachments.length)
    ? classifyRuntimeInteraction(draft, attachments)
    : null;
  const runtimeSubmitLabel = runtimeDraftKind === "progress"
    ? "查看进度"
    : runtimeDraftKind === "correction"
      ? "纠正方向"
      : runtimeDraftKind === "stop"
        ? "安全停止"
        : "补充资料";
  const visibleKnowledgeGraph = useMemo(() => {
    const nodes = knowledgeState?.graph.nodes || [];
    const edges = knowledgeState?.graph.edges || [];
    const query = knowledgeGraphQuery.trim().toLocaleLowerCase("zh-CN");
    if (!query) return { nodes, edges };
    const retained = new Set(nodes
      .filter((node) => `${node.label} ${node.detail} ${node.kind}`.toLocaleLowerCase("zh-CN").includes(query))
      .map((node) => node.id));
    edges.forEach((edge) => {
      if (retained.has(edge.sourceId)) retained.add(edge.targetId);
      if (retained.has(edge.targetId)) retained.add(edge.sourceId);
    });
    return {
      nodes: nodes.filter((node) => retained.has(node.id)),
      edges: edges.filter((edge) => retained.has(edge.sourceId) && retained.has(edge.targetId))
    };
  }, [knowledgeGraphQuery, knowledgeState]);
  const selectedKnowledgeNode = useMemo(
    () => knowledgeState?.graph.nodes.find((node) => node.id === selectedKnowledgeNodeId) || null,
    [knowledgeState, selectedKnowledgeNodeId]
  );
  const selectedKnowledgeRelations = useMemo(() => {
    if (!selectedKnowledgeNode || !knowledgeState) return [];
    const nodeById = new Map(knowledgeState.graph.nodes.map((node) => [node.id, node]));
    return knowledgeState.graph.edges
      .filter((edge) => edge.sourceId === selectedKnowledgeNode.id || edge.targetId === selectedKnowledgeNode.id)
      .map((edge) => ({
        edge,
        node: nodeById.get(edge.sourceId === selectedKnowledgeNode.id ? edge.targetId : edge.sourceId)
      }))
      .filter((item) => item.node)
      .slice(0, 16);
  }, [knowledgeState, selectedKnowledgeNode]);

  useEffect(() => {
    if (!settingsOpen || settingsSection !== "agents" || !agentWorkshopOpen) return;
    let cancelled = false;
    setAgentRecommendationLoading(true);
    const timer = setTimeout(() => {
      void window.nova.agentPacks.recommend({
        ...agentWorkshopForm,
        requiredInputs: [],
        recommendedInputs: [],
        starterPrompts: []
      }).then((recommendation) => {
        if (!cancelled) setAgentWorkshopRecommendation(recommendation);
      }).catch(() => {
        if (!cancelled) setAgentWorkshopRecommendation(null);
      }).finally(() => {
        if (!cancelled) setAgentRecommendationLoading(false);
      });
    }, 320);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [agentWorkshopForm, agentWorkshopOpen, settingsOpen, settingsSection]);

  async function refreshTasks() {
    const result = await window.nova.system.listTasks();
    setTasks(normalizeTasks(result));
  }

  async function refreshArchivedTasks() {
    const result = await window.nova.system.listArchivedTasks();
    setArchivedTasks(normalizeTasks(result));
  }

  async function loadTaskCapsule(taskId: string) {
    try {
      const capsule = await window.nova.system.getTaskCapsule({ taskId });
      if (selectedTaskIdRef.current === taskId) setTaskCapsule(capsule);
    } catch {
      if (selectedTaskIdRef.current === taskId) setTaskCapsule(null);
    }
  }

  async function archiveTask(task: AgentTask) {
    await window.nova.system.archiveTask({ taskId: task.id });
    if (selectedTaskId === task.id) resetTaskState();
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

  async function deleteArchivedTask(task: AgentTask) {
    await window.nova.system.deleteArchivedTask({ taskId: task.id });
    setPendingDeleteTask(null);
    await refreshArchivedTasks();
    setNotice(`“${task.title || "未命名任务"}”的对话与任务索引已永久删除，工作区文件和交付物仍保留。`);
    addActivity("归档记录已删除", task.title || task.id, "done");
  }

  async function openDeliveryArtifact(
    artifact: { id?: string; label?: string; title?: string; path: string }
  ) {
    setDeliveryReviewLoading(true);
    setDeliveryReviewNote("");
    setDeliveryReviewMode("rework");
    try {
      const preview: DeliveryArtifactPreview =
        await window.nova.system.readDeliveryArtifact({
          path: artifact.path,
          workspace
        });
      setDeliveryReview({
        artifactId: artifact.id,
        title: artifact.label || artifact.title || preview.name,
        path: preview.path,
        content: preview.content,
        kind: preview.kind,
        truncated: preview.truncated
      });
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "交付文件预览失败");
    } finally {
      setDeliveryReviewLoading(false);
    }
  }

  function openDeliverySummary(title: string, content: string) {
    setDeliveryReviewNote("");
    setDeliveryReviewMode("rework");
    setDeliveryReview({ title, content, kind: "markdown" });
  }

  async function submitDeliveryRework() {
    if (!deliveryReview || !selectedTaskId) return;
    const subject = deliveryReview.path
      ? `交付文件 ${deliveryReview.path}`
      : `本轮交付“${deliveryReview.title}”`;
    const note = deliveryReviewNote.trim()
      || "请进一步检查完整性、清晰度与可直接使用程度，并修正发现的问题。";
    try {
      await window.nova.system.submitDeliveryFeedback({
        taskId: selectedTaskId,
        scope: deliveryReview.artifactId ? "artifact" : "delivery",
        category: calibrationCategory,
        note,
        artifactId: deliveryReview.artifactId || null,
        calibrateAgent: false
      });
      setMessages((current) => current.map((message) =>
        message.delivery
          ? { ...message, delivery: { ...message.delivery, reviewState: "changes-requested" } }
          : message));
      const repairPrompt = `请继续修复${subject}。\n\n审查意见：${note}\n\n保留已经验证通过的内容，只修改不满足项；完成后重新生成真实交付文件、证据与未完成边界。`;
      setPendingSubmission({ content: repairPrompt, attachments: [] });
      setApprovalOpen(true);
      setDeliveryReview(null);
      setDeliveryReviewNote("");
      setNotice("修改意见已记录。确认本轮权限后，NOVA 会在当前任务中直接修复并生成新版本交付。");
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "修改意见没有保存");
    }
  }

  async function acceptCurrentDelivery() {
    if (!selectedTaskId) return;
    try {
      await window.nova.system.acceptDelivery({ taskId: selectedTaskId });
      setDeliveryReview(null);
      setDeliveryReviewNote("");
      setMessages((current) => current.map((message) =>
        message.delivery
          ? { ...message, delivery: { ...message.delivery, reviewState: "accepted" } }
          : message));
      setNotice("本轮交付已验收，文件与证据仍保留在当前任务中。");
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "交付验收状态没有保存");
    }
  }

  async function saveAgentCalibration() {
    if (!deliveryReview || !selectedAgentPackId) return;
    const instruction = deliveryReviewNote.trim();
    if (instruction.length < 4) {
      setNotice("请先描述 Agent 哪个地方不合适，以及以后应该怎样处理。");
      return;
    }
    try {
      const snapshot = await window.nova.agentPacks.createCalibration({
        packId: selectedAgentPackId,
        scope: calibrationScope,
        category: calibrationCategory,
        instruction,
        taskId: selectedTaskId,
        workspaceRoot: workspace,
        sourceTitle: deliveryReview.title,
        sourcePath: deliveryReview.path || null
      });
      setAgentCalibration(snapshot);
      const subject = deliveryReview.path
        ? `交付文件 ${deliveryReview.path}`
        : `本轮交付“${deliveryReview.title}”`;
      if (selectedTaskId) {
        await window.nova.system.submitDeliveryFeedback({
          taskId: selectedTaskId,
          scope: deliveryReview.artifactId ? "artifact" : "delivery",
          category: calibrationCategory,
          note: instruction,
          artifactId: deliveryReview.artifactId || null,
          calibrateAgent: true
        });
        setMessages((current) => current.map((message) =>
          message.delivery
            ? { ...message, delivery: { ...message.delivery, reviewState: "changes-requested" } }
            : message));
      }
      const repairPrompt = `请根据刚刚保存的 Agent 校准规则继续加工${subject}。\n\n本次纠正：${instruction}\n\n保留已经验证通过的内容，只修改不满足项，并重新生成真实交付文件、证据与未完成边界。`;
      setPendingSubmission({ content: repairPrompt, attachments: [] });
      setApprovalOpen(true);
      setDeliveryReview(null);
      setDeliveryReviewNote("");
      setDeliveryReviewMode("rework");
      setNotice(
        `校准 v${snapshot.version} 已保存到“${calibrationScopeLabels[calibrationScope]}”。确认权限后会立即按新规则修复当前交付。`
      );
    } catch (error) {
      setNotice(`Agent 校准没有保存：${error instanceof Error ? error.message : String(error)}`);
    }
  }

  async function rollbackAgentCalibration(patch: AgentCalibrationPatch) {
    try {
      const snapshot = await window.nova.agentPacks.rollbackCalibration({
        packId: patch.packId,
        patchId: patch.id
      });
      setAgentCalibration(snapshot);
      setNotice(
        patch.state === "active"
          ? `校准 v${patch.version} 已回滚，原始 Agent Pack 没有被修改。`
          : `校准 v${patch.version} 已重新启用。`
      );
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "校准状态更新失败");
    }
  }

  async function openTask(task: AgentTask) {
    try {
      const recovered = await window.nova.system.getTask({ taskId: task.id });
      selectedTaskIdRef.current = task.id;
      setSelectedTaskId(task.id);
      setTaskCapsule(null);
      void loadTaskCapsule(task.id);
      setWorkspace(recovered.task.workspaceRoot || workspace);
      if (recovered.task.provider && recovered.task.provider in providerLabels) {
        const nextProvider = recovered.task.provider as Provider;
        setProvider(nextProvider);
        setModel(recovered.task.model || providerModels[nextProvider][0]);
      }
      const recoveredMode = parseExecutionMode(recovered.task.executionMode);
      if (recoveredMode) setExecutionMode(recoveredMode);
      setSelectedAgentPackId(recovered.task.agentPackId || null);
      // Attachments are task inputs, not disposable message decorations. Keep
      // the durable snapshot inputs visible after reopening a task so the user
      // can switch to a vision/document-capable model without uploading again.
      setAttachments(mergeAttachments(recovered.task.attachments));
      const restoredMessages = recovered.messages.map((message) => ({
          ...message,
          createdAt: new Date(message.createdAt).toLocaleTimeString("zh-CN", {
            hour: "2-digit",
            minute: "2-digit"
          })
        }));
      if (recovered.delivery) {
        let lastAssistant = -1;
        for (let index = restoredMessages.length - 1; index >= 0; index -= 1) {
          if (restoredMessages[index].role === "assistant") {
            lastAssistant = index;
            break;
          }
        }
        if (lastAssistant >= 0) restoredMessages[lastAssistant].delivery = recovered.delivery;
      }
      setMessages(restoredMessages);
      setNotice("已恢复任务上下文，可以继续追问或修改方向");
      addActivity("任务已恢复", task.title, "done");
      if (window.innerWidth <= 1180) setLeftOpen(false);
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "任务恢复失败");
    }
  }

  async function loadCapabilities() {
    setCapabilitiesLoading(true);
    try {
      const state = await window.nova.capabilities.list({ workspace });
      setCapabilities(state);
      return state;
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "能力状态读取失败");
      return null;
    } finally {
      setCapabilitiesLoading(false);
    }
  }

  async function loadKnowledge(scope: "workspace" | "all" = knowledgeScope) {
    setKnowledgeLoading(true);
    try {
      const state = await window.nova.knowledge.getState({
        workspace: scope === "workspace" ? workspace : null
      });
      setKnowledgeState(state);
      return state;
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "知识库状态读取失败");
      return null;
    } finally {
      setKnowledgeLoading(false);
    }
  }

  async function deleteKnowledgeNode(node: KnowledgeGraphNode) {
    try {
      const result = await window.nova.knowledge.deleteNode({
        nodeId: node.id,
        label: node.label
      });
      if (!result.deleted) return;
      setSelectedKnowledgeNodeId(null);
      await loadKnowledge();
      setNotice("知识节点已从思维网络移除；原始任务和文件仍然保留");
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "知识节点移除失败");
    }
  }

  async function reviewKnowledgeMapping(edge: KnowledgeGraphEdge, accepted: boolean) {
    try {
      await window.nova.knowledge.reviewMapping({
        sourceId: edge.sourceId,
        targetId: edge.targetId,
        accepted
      });
      await loadKnowledge();
      setNotice(accepted
        ? "这条知识映射已确认为可靠关系"
        : "这条候选映射已忽略，NOVA 不会再次自动推荐");
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "知识映射审核失败");
    }
  }

  async function indexWorkspaceKnowledge() {
    if (!workspace) {
      setNotice("请先选择工作区，再建立知识索引");
      return;
    }
    setKnowledgeLoading(true);
    try {
      const result = await window.nova.knowledge.indexWorkspace({ workspace });
      await loadKnowledge();
      setNotice(
        `知识库已更新：${result.summary.indexedFiles} 个文件重建，${result.summary.reusedFiles} 个文件复用`
      );
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "知识库索引失败");
    } finally {
      setKnowledgeLoading(false);
    }
  }

  async function searchKnowledge(event?: FormEvent) {
    event?.preventDefault();
    const query = knowledgeQuery.trim();
    if (!query) return;
    setKnowledgeLoading(true);
    try {
      const result = await window.nova.knowledge.search({
        workspace: knowledgeScope === "workspace" ? workspace : null,
        query,
        maximumResults: 20
      });
      setKnowledgeResults(result.results);
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "知识库检索失败");
    } finally {
      setKnowledgeLoading(false);
    }
  }

  async function loadAgentPacks(preferredId?: string | null) {
    setAgentPacksLoading(true);
    try {
      const [packs, templates] = await Promise.all([
        window.nova.agentPacks.list(),
        window.nova.agentPacks.listCreationTemplates()
      ]);
      setAgentPacks(packs);
      setAgentCreationTemplates(templates);
      const requestedId = preferredId ?? selectedAgentPackId;
      if (requestedId && !packs.some((pack) => pack.id === requestedId && pack.enabled)) {
        setSelectedAgentPackId(null);
      }
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "Agent Pack 读取失败");
    } finally {
      setAgentPacksLoading(false);
    }
  }

  async function inspectAgentPack(id: string) {
    try {
      const [details, calibration] = await Promise.all([
        window.nova.agentPacks.get({ id }),
        window.nova.agentPacks.listCalibrations({ packId: id })
      ]);
      setInspectedAgentPack(details);
      setAgentCalibration(calibration);
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "Agent Pack 详情读取失败");
    }
  }

  async function orchestrateAgentPack(event: FormEvent) {
    event.preventDefault();
    if (!connected[provider]) {
      setNotice(`请先连接 ${providerLabels[provider]}，Agent 工坊需要模型完成真实编排。`);
      openSettings("model");
      return;
    }
    setAgentOrchestrating(true);
    setAgentOrchestrationDraft(null);
    setAgentOrchestrationEvents([]);
    setAgentCreationResult(null);
    try {
      const response = await window.nova.agentPacks.orchestrate({
        ...agentWorkshopForm,
        provider,
        model
      });
      agentWorkshopSessionIdRef.current = response.session.id;
      setAgentDesignSession(response.session);
      setAgentOrchestrationEvents(response.session.events || []);
      setNotice("真实多 Agent 已在 Agent 中心开始编排；这里完成审阅前不会创建任务空间。");
    } catch (error) {
      setAgentOrchestrating(false);
      setNotice(`智能体编排失败：${error instanceof Error ? error.message : String(error)}`);
    }
  }

  function openAgentFoundry() {
    setAgentFoundryError("");
    setAgentFoundryOpen(true);
  }

  async function prepareAgentFoundry(event: FormEvent) {
    event.preventDefault();
    const goal = agentFoundryGoal.trim();
    if (goal.length < 8) {
      setAgentFoundryError("再多说一点：这个 Agent 要帮助谁，解决什么问题？");
      return;
    }
    if (!connected[provider]) {
      setAgentFoundryOpen(false);
      setNotice(`目标已经保留。先连接 ${providerLabels[provider]}，然后再点“创建专属 Agent”。`);
      openSettings("model");
      return;
    }
    setAgentFoundryPreparing(true);
    setAgentFoundryError("");
    try {
      const brief: AgentFoundryBrief = await window.nova.agentPacks.prepare({
        goal,
        provider,
        model
      });
      setAgentWorkshopForm({
        id: generateAgentId(),
        name: brief.name,
        category: brief.category,
        description: brief.description,
        objective: brief.objective,
        scenarioProfile: brief.scenarioProfile,
        autonomyLevel: brief.autonomyLevel,
        lifecycle: brief.lifecycle,
        collaborationMode: brief.collaborationMode,
        deliveryMode: brief.deliveryMode,
        decisionStyle: brief.decisionStyle,
        primaryArtifact: brief.primaryArtifact
      });
      agentWorkshopSessionIdRef.current = null;
      setAgentDesignSession(null);
      setAgentOrchestrationDraft(null);
      setAgentOrchestrationEvents([]);
      setAgentCreationResult(null);
      setAgentBuildError("");
      setAgentWorkshopSimpleMode(true);
      setAgentWorkshopOpen(true);
      setAgentFoundryOpen(false);
      openSettings("agents");
      setNotice(brief.understanding);
    } catch (error) {
      setAgentFoundryError(error instanceof Error ? error.message : "NOVA 暂时没有完成业务理解，请重试。" );
    } finally {
      setAgentFoundryPreparing(false);
    }
  }

  async function createAgentPack() {
    if (!agentOrchestrationDraft) {
      setNotice("请先完成智能体编排并审阅草案，再生成 Agent Pack。");
      return;
    }
    const approvedOrchestration: AgentWorkshopOrchestrationDraft = {
      ...agentOrchestrationDraft,
      reviewVerdict: "approved",
      designRationale: [
        ...agentOrchestrationDraft.designRationale,
        "用户已在 Agent 中心审阅并确认本版编排草案。"
      ]
    };
    setAgentBuildError("");
    setAgentCreating(true);
    try {
      const response = await window.nova.agentPacks.create({
        ...agentWorkshopForm,
        requiredInputs: approvedOrchestration.requiredInputs,
        recommendedInputs: approvedOrchestration.recommendedInputs,
        starterPrompts: approvedOrchestration.starterPrompts,
        orchestration: approvedOrchestration
      });
      if (response.canceled || !response.task) return;
      const buildTask = normalizeTasks([response.task])[0];
      setAgentCreationResult(null);
      setAgentWorkshopOpen(false);
      setSettingsOpen(false);
      selectedTaskIdRef.current = buildTask.id;
      setSelectedTaskId(buildTask.id);
      setRunningTaskIds((current) => new Set(current).add(buildTask.id));
      setTasks((current) => [buildTask, ...current.filter((task) => task.id !== buildTask.id)]);
      setAgentWorkshopForm(createInitialAgentWorkshopForm());
      agentWorkshopSessionIdRef.current = null;
      setAgentDesignSession(null);
      setAgentOrchestrationDraft(null);
      setAgentOrchestrationEvents([]);
      setPlanTitle("Agent Pack 生成与可用性验证");
      setTaskPlan([
        { id: "lock", title: "锁定编排草案", detail: "保存角色、工作流和审查结论", agent: "Agent 工坊", status: "running" },
        { id: "compile", title: "编译 Pack 契约", detail: "生成身份、角色、工作流和交付模板", agent: "Pack 编译器", status: "pending" },
        { id: "assemble", title: "装配引导与能力", detail: "检查首次使用引导与能力需求", agent: "能力装配器", status: "pending" },
        { id: "verify", title: "标准体检与注册", detail: "完整性全部通过后才进入能力仓", agent: "标准体检官", status: "pending" }
      ]);
      setAgentUnits({});
      await openTask(buildTask);
      setNotice("Agent Pack 构建任务已进入任务空间；你可以看到每个真实生成与体检阶段。");
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      setAgentBuildError(message);
      setNotice(`Agent 创建失败：${message}`);
    } finally {
      setAgentCreating(false);
    }
  }

  function updateAgentWorkshopField<K extends keyof AgentWorkshopForm>(
    key: K,
    value: AgentWorkshopForm[K]
  ) {
    setAgentWorkshopForm((current) => ({ ...current, [key]: value }));
    agentWorkshopSessionIdRef.current = null;
    setAgentDesignSession(null);
    setAgentOrchestrationDraft(null);
    setAgentOrchestrationEvents([]);
    setAgentCreationResult(null);
    setAgentBuildError("");
  }

  async function activateAgentPack(id: string | null, showGuide = true) {
    if (!id) {
      setSelectedAgentPackId(null);
      setAgentLaunchGuide(null);
      setAgentLaunchOpen(false);
      setAgentLaunchValues({});
      setAgentLaunchError("");
      setAgentCapabilityReport(null);
      setAgentCalibration(null);
      setMcpDiscovery(null);
      setSelectedMcpCandidates(new Set());
      setNotice("已切回通用 NOVA，不装载行业 Agent Pack");
      return;
    }
    try {
      const [details, capabilityReport, calibration] = await Promise.all([
        window.nova.agentPacks.get({ id }),
        window.nova.agentPacks.getCapabilities({ id, workspace }),
        window.nova.agentPacks.listCalibrations({ packId: id })
      ]);
      if (!details.summary.enabled) {
        setNotice(`请先在 Agent 中心启用 ${details.summary.name}`);
        return;
      }
      let activeCapabilityReport = capabilityReport;
      const missingRequired = capabilityReport.items.filter((item) =>
        item.required && item.state !== "ready"
      );
      const canAuthorize = missingRequired.some((item) =>
        (item.state === "available" && item.catalogId)
        || (item.state === "registered-disabled" && item.matchedId)
      );
      let capabilityNotice = "";
      let unresolvedRequired = missingRequired;
      if (missingRequired.length && canAuthorize) {
        const capabilityResult = await window.nova.capabilities.authorizeAgentCapabilities({
          packId: id,
          workspace
        });
        activeCapabilityReport = capabilityResult.report;
        unresolvedRequired = capabilityResult.unresolved || [];
        if (capabilityResult.changed.length) {
          await loadCapabilities();
          capabilityNotice = `已按你的确认补全 ${capabilityResult.changed.length} 项必要能力。`;
        } else if (capabilityResult.canceled) {
          capabilityNotice = "未安装 MCP；Agent 仍可启动，并会明确标出能力缺口。";
        }
      }
      setSelectedAgentPackId(id);
      setAgentLaunchGuide(details);
      setAgentCapabilityReport(activeCapabilityReport);
      setAgentCalibration(calibration);
      setMcpDiscovery(null);
      setSelectedMcpCandidates(new Set());
      setAgentLaunchValues({});
      setAgentLaunchError("");
      setExecutionMode("Goal");
      setSettingsOpen(false);
      const unresolvedMcp = unresolvedRequired.find((item) =>
        item.required && item.kind === "mcp" && item.state === "missing"
      );
      if (unresolvedMcp) {
        try {
          const result = await window.nova.capabilities.searchStore({
            kind: "mcp",
            query: unresolvedMcp.name.replace(/\bMCP\b/ig, "").trim()
          });
          setStoreKind("mcp");
          setStoreQuery(unresolvedMcp.name);
          setStoreSources(result.sources);
          setStoreItems(result.items);
          const candidate = result.items.find((item) => item.installable);
          if (candidate) {
            setPendingStoreItem(candidate);
            capabilityNotice += ` NOVA 在官方目录找到了“${candidate.name}”，请审阅来源与权限后决定是否自动接入。`;
          } else {
            capabilityNotice += ` 必要能力“${unresolvedMcp.name}”暂未找到可自动安装项。`;
          }
        } catch {
          capabilityNotice += ` 必要能力“${unresolvedMcp.name}”暂未完成在线匹配，可稍后从扩展坞搜索。`;
        }
      }
      if (showGuide && details.onboarding) {
        setAgentLaunchOpen(true);
        setNotice(`${capabilityNotice}已装载 ${details.summary.name}；按引导补充现有线索即可开始`);
      } else {
        setNotice(`${capabilityNotice}已装载 ${details.summary.name}；下一轮将使用它的角色、工作流与交付契约`);
      }
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "专业 Agent 装载失败");
    }
  }

  async function refreshAgentCapabilities(packId = selectedAgentPackId) {
    if (!packId) return null;
    const report = await window.nova.agentPacks.getCapabilities({
      id: packId,
      workspace
    });
    setAgentCapabilityReport(report);
    return report;
  }

  async function scanLocalMcpConfigurations() {
    setCapabilityPreparing(true);
    try {
      const result = await window.nova.capabilities.discoverMcp({ workspace });
      if (result.canceled) {
        setNotice("已取消本机 MCP 扫描；没有读取配置内容。");
        return;
      }
      setMcpDiscovery(result);
      setSelectedMcpCandidates(new Set(
        result.candidates
          .filter((candidate) =>
            candidate.canImport
            && !candidate.mayAcquireSoftware
            && candidate.omittedSecretCount === 0)
          .map((candidate) => candidate.id)
      ));
      setNotice(result.candidates.length
        ? `已发现 ${result.candidates.length} 个 MCP 候选，请审阅后登记。`
        : "扫描完成，没有发现可导入的 MCP 配置。");
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "本机 MCP 扫描失败");
    } finally {
      setCapabilityPreparing(false);
    }
  }

  async function prepareAgentCapability(item: AgentPackCapabilityReport["items"][number]) {
    if (item.state === "ready") return;
    if (item.state === "registered-disabled") {
      setAgentLaunchOpen(false);
      openSettings(item.kind === "mcp" ? "mcp" : "skills");
      setNotice(`“${item.name}”已经登记但尚未启用，请审阅后决定是否启用。`);
      return;
    }
    if (item.state === "available" && item.catalogId) {
      const current = capabilities || await loadCapabilities();
      const catalogItem = current?.marketplace.find((candidate) =>
        candidate.id === item.catalogId
      );
      if (catalogItem) {
        setPendingBundledItem(catalogItem);
        return;
      }
    }
    if (item.kind === "mcp") {
      setAgentLaunchOpen(false);
      setStoreKind("mcp");
      setStoreQuery(item.name);
      openSettings("plugins");
      setStoreLoading(true);
      try {
        const result = await window.nova.capabilities.searchStore({
          kind: "mcp",
          query: item.name.replace(/\bMCP\b/ig, "").trim()
        });
        setStoreSources(result.sources);
        setStoreItems(result.items);
        setNotice(result.items.length
          ? `已为“${item.name}”找到 ${result.items.length} 个 MCP 候选；安装前请核对来源和权限。`
          : `官方目录没有找到“${item.name}”；你也可以扫描本机已有配置或粘贴服务地址。`);
      } catch (error) {
        setNotice(error instanceof Error ? error.message : "MCP 目录查询失败，可改用本机扫描或手动接入");
      } finally {
        setStoreLoading(false);
      }
      return;
    }
    setAgentLaunchOpen(false);
    setStoreKind("skill");
    setStoreQuery(item.name);
    openSettings("plugins");
    setNotice(`能力“${item.name}”尚未找到，可以从 Skills 超市选择。`);
  }

  async function importSelectedMcpCandidates() {
    if (!mcpDiscovery) return;
    const selected = mcpDiscovery.candidates.filter((candidate) =>
      selectedMcpCandidates.has(candidate.id) && candidate.canImport
    );
    if (!selected.length) {
      setAgentLaunchError("请至少选择一个可导入的 MCP 连接。");
      return;
    }
    setCapabilityPreparing(true);
    try {
      const result = await window.nova.capabilities.importDiscoveredMcp({
        candidates: selected
      });
      if (result.canceled) return;
      setMcpDiscovery(null);
      setSelectedMcpCandidates(new Set());
      await Promise.all([loadCapabilities(), refreshAgentCapabilities()]);
      setNotice(`已登记 ${result.imported.length} 个 MCP，并保持停用；启用前仍可逐项审阅。`);
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "MCP 登记失败");
    } finally {
      setCapabilityPreparing(false);
    }
  }

  async function previewPastedMcpConfiguration() {
    if (!mcpConfigText.trim()) {
      setNotice("请粘贴 MCP URL、JSON 或 Codex TOML 配置。");
      return;
    }
    setCapabilityPreparing(true);
    try {
      const result = await window.nova.capabilities.previewMcpConfig({
        workspace,
        configuration: mcpConfigText.trim(),
        authorizationEnvironment: mcpAuthorizationEnvironment.trim() || undefined
      });
      setMcpDiscovery({ canceled: false, ...result });
      setSelectedMcpCandidates(new Set(
        result.candidates.filter((candidate) => candidate.canImport).map((candidate) => candidate.id)
      ));
      setNotice(result.candidates.length
        ? `已解析 ${result.candidates.length} 个连接，请确认后登记。`
        : "没有从这段内容中解析出 MCP 连接。");
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "MCP 配置解析失败");
    } finally {
      setCapabilityPreparing(false);
    }
  }

  async function useAgentLaunchOutcome(outcomeId?: string) {
    const onboarding = agentLaunchGuide?.onboarding;
    if (!onboarding || !agentLaunchGuide) return;
    const outcome = onboarding.outcomes.find((item) => item.id === outcomeId)
      || onboarding.outcomes[0]
      || {
        id: "start",
        title: "开始执行",
        description: "按这个 Agent 的工作流推进，并生成可检查的结果。",
        promptTemplate: agentLaunchGuide.summary.starterPrompts[0]
          || `请使用“${agentLaunchGuide.summary.name}”完成当前目标。先检查已有资料，再按工作流生成真实交付物；缺失信息保留为未知项。`
      };
    const missing = onboarding.steps.filter((step) => {
      if (!step.required) return false;
      if (step.kind === "attachment") return attachments.length === 0;
      return !agentLaunchValues[step.id]?.trim();
    });
    // Missing material lowers confidence; it must never turn onboarding into a
    // dead end. The Agent will report unknowns and ask for the next best input.
    setAgentLaunchError("");
    let prompt = outcome.promptTemplate;
    for (const step of onboarding.steps) {
      const writtenValue = agentLaunchValues[step.id]?.trim();
      const value = step.kind === "attachment"
        ? writtenValue || (attachments.length
          ? `已上传：${attachments.map((item) => item.name).join("、")}`
          : "未提供附件")
        : writtenValue || "未提供";
      prompt = prompt.split(`{{${step.id}}}`).join(value);
    }
    if (attachments.length) {
      prompt += `\n\n本轮共同资料：${attachments.map((item) => item.name).join("、")}。请读取这些附件，不要要求用户在多个输入项中重复上传。`;
    }
    if (missing.length) {
      prompt += `\n\n当前仍缺少：${missing.map((step) => step.title).join("、")}。不要停止任务，也不要虚构；先使用现有资料完成可完成部分，并明确下一份最值得补充的资料。`;
    }
    setAgentLaunchOpen(false);
    const submission = { content: prompt, attachments };
    if (running) {
      setDraft(prompt);
      setNotice(`“${outcome.title}”已经准备好；当前任务结束后即可启动`);
      return;
    }
    if (!connected[provider]) {
      setDraft(prompt);
      openSettings("model");
      setNotice(`目标已保留。连接 ${providerLabels[provider]} 后即可直接启动`);
      return;
    }
    if (!workspace) {
      setDraft(prompt);
      setNotice("目标已保留。请先选择任务文件夹，随后即可直接启动");
      await chooseWorkspace();
      return;
    }
    setNotice(missing.length
      ? `正在用现有资料启动“${outcome.title}”；${missing.length} 项缺失信息会作为未知项保留`
      : `正在启动“${outcome.title}”`);
    if (currentTaskApprovalMode) {
      void executeSubmission(currentTaskApprovalMode, submission, true);
      return;
    }
    // Choosing an Agent outcome is an explicit start command. Use NOVA's
    // low-risk workspace review by default; the runtime still interrupts for
    // desktop control, external side effects or a new permission boundary.
    void executeSubmission("workspace", submission, true);
  }

  async function loadExtensionProfiles() {
    const [profiles, gateway, requests] = await Promise.all([
      window.nova.extensions.listProfiles(),
      window.nova.extensions.getGateway(),
      window.nova.extensions.listGatewayActionRequests()
    ]);
    setExtensionProfiles(profiles as {
      ssh: Array<Record<string, string | number>>;
      cloud: Array<Record<string, string | number>>;
    });
    setExtensionGateway(gateway);
    setGatewayActionRequests(requests);
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
    await window.nova.capabilities.installStore({
      id: item.id,
      enable: item.kind === "mcp"
    });
    setPendingStoreItem(null);
    await loadCapabilities();
    await refreshAgentCapabilities();
    setNotice(
      item.kind === "mcp"
        ? `“${item.name}”已按你的确认登记并启用；运行时仍受工作区与工具权限约束`
        : `“${item.name}”已通过格式校验并安装`
    );
    addActivity("能力已加入扩展坞", `${item.sourceLabel} · ${item.name}`, "done");
  }

  async function installBundledItem(item: CapabilityState["marketplace"][number]) {
    await window.nova.capabilities.install({ id: item.id, workspace });
    setPendingBundledItem(null);
    await Promise.all([loadCapabilities(), refreshAgentCapabilities()]);
    setNotice(`“${item.name}”已加入扩展坞；实际启用状态可在对应能力页查看`);
    addActivity("内置能力已加载", `${item.kind.toUpperCase()} · ${item.name}`, "done");
  }

  function openSettings(section: SettingsSection = "model") {
    setSettingsSection(section);
    setSettingsOpen(true);
    if (section === "mcp" || section === "skills" || section === "plugins") {
      void loadCapabilities();
    }
    if (section === "agents") void loadAgentPacks();
    if (section === "ssh" || section === "cloud" || section === "gateway") {
      void loadExtensionProfiles();
    }
    if (section === "growth") void loadGrowthState();
    if (section === "permissions") void loadWorkspacePermissions();
  }

  async function loadWorkspacePermissions() {
    try {
      setWorkspacePermissions(await window.nova.permissions.list());
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "无法读取工作区授权");
    }
  }

  async function resolveGatewayRequest(
    request: GatewayActionRequest,
    status: "accepted" | "rejected"
  ) {
    await window.nova.extensions.resolveGatewayActionRequest({ id: request.id, status });
    setGatewayActionRequests((current) => current.filter((item) => item.id !== request.id));
    setExtensionGateway((current) => current
      ? { ...current, pendingActionRequests: Math.max(0, current.pendingActionRequests - 1) }
      : current);
    if (status === "rejected") {
      setNotice(`已拒绝来自“${request.source}”的任务请求`);
      return;
    }
    const taskReady = await createTaskWithWorkspace();
    if (!taskReady) {
      setNotice("外部目标已接收；选择工作区后才能创建对应任务");
      return;
    }
    setDraft(request.prompt);
    setExecutionMode(request.executionMode);
    setAgentLaunchOpen(false);
    const requestedPack = request.agentPackId
      ? agentPacks.find((pack) => pack.id === request.agentPackId && pack.enabled)
      : null;
    setSelectedAgentPackId(requestedPack?.id || null);
    setSettingsOpen(false);
    setNotice("外部目标已带入新的任务草稿；检查内容后点击开始处理");
  }

  useEffect(() => window.nova.extensions.onGatewayActionRequest((request) => {
    setGatewayActionRequests((current) => [
      request,
      ...current.filter((item) => item.id !== request.id)
    ]);
    setExtensionGateway((current) => current
      ? { ...current, pendingActionRequests: current.pendingActionRequests + 1 }
      : current);
    setNotice(`收到来自“${request.source}”的外部任务请求，可在扩展坞审阅`);
  }), []);

  useEffect(() => {
    let active = true;
    (async () => {
      try {
        const info = await window.nova.system.boot();
        if (!active) return;
        setBoot(info);
        const restoredConnections = info.modelConnections || [];
        setConnected(Object.fromEntries(
          restoredConnections.map((connection) => [connection.provider, connection.connected])
        ));
        const preferredConnection = restoredConnections.find((connection) => connection.provider === "deepseek")
          || restoredConnections[0];
        if (preferredConnection) {
          setProvider(preferredConnection.provider);
          setModel(preferredConnection.model);
          setModelEndpoint(preferredConnection.endpoint);
        } else {
          setModel(info.defaults.deepseek.model);
        }
        await Promise.all([refreshTasks(), refreshArchivedTasks(), loadAgentPacks()]);
        const recoveredDesign = await window.nova.agentPacks.getDesignSession();
        if (recoveredDesign) {
          agentWorkshopSessionIdRef.current = recoveredDesign.id;
          setAgentDesignSession(recoveredDesign);
          setAgentOrchestrating(recoveredDesign.status === "running");
          setAgentOrchestrationEvents(recoveredDesign.events || []);
          setAgentOrchestrationDraft(recoveredDesign.draft || null);
          const {
            provider: _designProvider,
            model: _designModel,
            requiredInputs: _requiredInputs,
            recommendedInputs: _recommendedInputs,
            starterPrompts: _starterPrompts,
            orchestration: _orchestration,
            ...recoveredForm
          } = recoveredDesign.request;
          setAgentWorkshopForm((current) => ({ ...current, ...recoveredForm }));
        }
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
    selectedTaskIdRef.current = selectedTaskId;
  }, [selectedTaskId]);

  useEffect(() => window.nova.system.onContextEvent((event: ContextEvent) => {
    if (event.taskId !== selectedTaskIdRef.current) return;
    if (event.kind === "capsule-degraded") {
      setTaskCapsule({ status: "not-compiled", detail: event.detail || "本轮上下文已降级" });
      return;
    }
    void loadTaskCapsule(event.taskId);
  }), []);

  useEffect(() => window.nova.agentPacks.onOrchestrationEvent((event) => {
    if (event.sessionId !== agentWorkshopSessionIdRef.current) return;
    setAgentOrchestrationEvents((current) => {
      const index = current.findIndex((item) => item.agent === event.agent);
      if (index < 0) return [...current, event];
      const next = [...current];
      next[index] = event;
      return next;
    });
  }), []);

  useEffect(() => window.nova.agentPacks.onOrchestrationReady((event: AgentWorkshopReadyEvent) => {
    if (event.sessionId !== agentWorkshopSessionIdRef.current) return;
    setAgentOrchestrating(false);
    if (event.error || !event.draft) {
      setAgentDesignSession((current) => current && current.id === event.sessionId
        ? {
            ...current,
            status: current.status === "cancelled" ? "cancelled" : "failed",
            error: event.error || "没有生成可审阅草案"
          }
        : current);
      setNotice(`Agent 编排没有完成：${event.error || "没有生成可审阅草案"}`);
      return;
    }
    setAgentDesignSession((current) => current && current.id === event.sessionId
      ? { ...current, status: "completed", draft: event.draft || null, error: "" }
      : current);
    setAgentOrchestrationDraft(event.draft);
    setNotice(`多 Agent 编排完成：${event.draft.roles.length} 个角色 · ${event.draft.workflow.length} 个步骤，等待你确认。`);
  }), []);

  useEffect(() => {
    const unsubscribe = window.nova.model.onEvent((incomingEvent: AgentEvent) => {
      // Runtime events cross a process boundary and older/custom runtimes may
      // omit fields that the current renderer normally receives. Never let a
      // malformed progress event take down the whole React tree.
      const event: AgentEvent = {
        ...incomingEvent,
        taskId: String(incomingEvent?.taskId || ""),
        kind: String(incomingEvent?.kind || "progress"),
        agent: String(incomingEvent?.agent || "NOVA"),
        action: String(incomingEvent?.action || incomingEvent?.kind || "继续处理"),
        detail: String(incomingEvent?.detail || ""),
        progress: Number.isFinite(Number(incomingEvent?.progress))
          ? Math.max(0, Math.min(100, Number(incomingEvent.progress)))
          : 0,
        activeUnits: Number.isFinite(Number(incomingEvent?.activeUnits))
          ? Math.max(0, Number(incomingEvent.activeUnits))
          : 0
      };
      if (!event.taskId) return;
      const completed = event.kind === "completed" && event.progress >= 100;
      setTasks((current) => current.map((task) =>
        task.id === event.taskId
          ? {
              ...task,
              status: event.kind === "failed" ? "Failed" : completed ? "Completed" : "Running",
              state: event.kind === "failed" ? "Failed" : completed ? "Completed" : "Running",
              progress: Math.max(task.progress || 0, event.progress || 0),
              summary: event.action || task.summary
            }
          : task
      ));
      if (event.kind === "failed" || completed) {
        setRunningTaskIds((current) => {
          // Model runs are settled by executeSubmission.finally. Keeping the
          // running flag until that promise returns prevents a queued user
          // correction from starting before the current result is persisted.
          if (taskRunIds.current.has(event.taskId)) return current;
          const next = new Set(current);
          next.delete(event.taskId);
          return next;
        });
        void refreshTasks();
        if (completed && event.packId) {
          void loadAgentPacks(event.packId);
          setNotice("Agent Pack 已完成真实生成与 100/100 体检；当前保持停用，等待你检查后启用。");
        } else if (event.kind === "failed" && event.agent === "Agent Pack Builder") {
          setNotice(`Agent Pack 构建已停止：${event.detail}`);
        }
        if (event.taskId === selectedTaskIdRef.current) {
          void window.nova.system.getTask({ taskId: event.taskId }).then((recovered) => {
            if (event.taskId !== selectedTaskIdRef.current) return;
            setMessages(recovered.messages.map((message) => ({
              ...message,
              createdAt: new Date(message.createdAt).toLocaleTimeString("zh-CN", {
                hour: "2-digit",
                minute: "2-digit"
              })
            })));
          }).catch(() => undefined);
        }
      }
      if (event.taskId !== selectedTaskIdRef.current) return;
      lastRuntimeEventAt.current = Date.now();
      if (event.kind !== "textdelta") {
        setCurrentRuntimeEvent({
          agent: event.agent,
          action: event.action || event.kind,
          detail: event.detail || "",
          progress: event.progress || 0
        });
      }
      setRuntimePulse(
        event.kind === "textdelta"
          ? "模型正在持续生成"
          : event.action || "执行链路仍在推进"
      );
      if (event.kind === "textdelta") {
        setTaskPlan((current) => current.map((step) => {
          if (step.id === "understand" || step.id === "inspect") {
            return step.status === "failed" ? step : { ...step, status: "done" };
          }
          if (step.id === "execute") return { ...step, status: "running" };
          return step;
        }));
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
            replacePlan?: boolean;
            steps?: Array<Pick<PlanStep, "id" | "title" | "detail" | "agent">>;
          };
          setPlanTitle(payload.strategy || "Agent 并行计划");
          const plannedSteps = (payload.steps || []).map((step, index) => ({
            ...step,
            status: (index === 0 ? "running" : "pending") as PlanStep["status"]
          }));
          setTaskPlan(payload.replacePlan ? plannedSteps : [
            {
              id: "understand",
              title: "理解目标与约束",
              detail: "任务目标和执行边界已经冻结",
              agent: "NOVA",
              status: "done"
            },
            ...plannedSteps,
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
            event.agent.startsWith(`${step.agent} ·`) ||
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
          if (step.id === "understand" && event.action === "理解目标") {
            return { ...step, status: "running" };
          }
          if (step.id === "understand" && (event.kind.includes("tool") || event.kind === "textdelta")) {
            return { ...step, status: "done" };
          }
          if (step.id === "inspect" && event.kind.includes("tool")) {
            return { ...step, status: isDone ? "done" : "running" };
          }
          if (step.id === "execute" && (event.agent === "NOVA" || event.kind === "textdelta")) {
            return { ...step, status: isDone ? "done" : isActive ? "running" : step.status };
          }
          if (step.id === "verify" && event.kind === "completed"
              && (event.agent === "审查官" || event.agent.includes("体检"))) {
            return { ...step, status: "done", output: event.detail || step.output };
          }
          if (event.kind === "completed" && step.status !== "failed") {
            return { ...step, status: "done" };
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

  useEffect(() => window.nova.model.onApprovalRequest((request) => {
    if (request.taskId !== selectedTaskIdRef.current) return;
    setRememberToolPermission(true);
    setRememberWorkspacePermission(false);
    setPendingToolApproval(request);
    setNotice(`NOVA 需要你确认一步新权限：${request.title}`);
  }), []);

  useEffect(() => {
    const unsubscribe = window.nova.growth.onEvolutionEvent(
      async (event: EvolutionDiscoveryEvent) => {
        try {
          setEvolutionLab(await window.nova.growth.getEvolutionLab());
        } catch {
          // The persisted state will be loaded the next time the growth hub opens.
        }
        if (event.kind === "candidate") {
          setNotice("Evolution Lab 已生成一个本地改进候选，等待你的审阅");
        } else if (event.kind === "error") {
          setNotice(event.discoveryStatus);
        }
      }
    );
    return unsubscribe;
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
        setRuntimePulse("较长推理仍在后台继续；你可以补充资料、纠正方向或安全停止");
      } else if (silenceSeconds >= 8) {
        setRuntimePulse("正在等待模型完成当前思考，不需要保持在这个页面");
      }
    }, 2000);
    return () => clearInterval(timer);
  }, [running]);

  useEffect(() => {
    runtimeChangeQueueRef.current = runtimeChangeQueue;
  }, [runtimeChangeQueue]);

  useEffect(() => {
    if (running || !selectedTaskId) return;
    const currentQueue = runtimeChangeQueue.filter((item) => item.taskId === selectedTaskId);
    if (currentQueue.length === 0) return;
    if (suppressQueuedResumeTasksRef.current.has(selectedTaskId)) {
      suppressQueuedResumeTasksRef.current.delete(selectedTaskId);
      return;
    }
    const queued = currentQueue;
    const submission = compileRuntimeChanges(queued);
    const remaining = runtimeChangeQueue.filter((item) => item.taskId !== selectedTaskId);
    runtimeChangeQueueRef.current = remaining;
    setRuntimeChangeQueue(remaining);
    const corrections = queued.filter((item) => item.kind === "correction").length;
    setNotice(corrections
      ? `当前步骤已安全落点，正在按 ${corrections} 条方向纠正继续`
      : `当前步骤已安全落点，正在合入 ${queued.length} 条补充资料`);
    const approvalMode = currentTaskApprovalMode;
    const timer = setTimeout(() => {
      if (approvalMode) {
        void executeSubmission(approvalMode, submission, true);
      } else {
        setPendingSubmission(submission);
        setRememberTaskPermission(true);
        setApprovalOpen(true);
      }
    }, 0);
    return () => clearTimeout(timer);
  }, [currentTaskApprovalMode, running, runtimeChangeQueue, selectedTaskId]);

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

  async function chooseWorkspace(): Promise<string | null> {
    const selected = await window.nova.system.selectWorkspace();
    if (!selected) return null;
    setWorkspace(selected);
    addActivity("工作区已就位", selected, "done");
    return selected;
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
      setBoot((current) => current ? {
        ...current,
        modelConnections: [
          ...(current.modelConnections || []).filter((item) => item.provider !== provider),
          {
            provider,
            model: result.model,
            endpoint: result.endpoint,
            connected: true,
            hasCredential: Boolean(apiKey.trim())
              || current.modelConnections?.some((item) => item.provider === provider && item.hasCredential)
              || false
          }
        ]
      } : current);
      setDiscoveredModels(result.discoveredModels || []);
      setApiKey("");
      setSettingsOpen(false);
      setNotice(result.persisted
        ? `${providerLabels[provider]} 已连接；凭据已由系统加密保存，重启后会自动恢复`
        : result.warning || `${providerLabels[provider]} 已连接，本次运行内有效`);
      addActivity("模型通道已连接", `${providerLabels[provider]} · ${model}`, "done");
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "模型连接失败");
    }
  }

  function changeProvider(next: Provider) {
    setProvider(next);
    const restored = boot?.modelConnections?.find((connection) => connection.provider === next);
    setModel(restored?.model || boot?.defaults?.[next]?.model || providerModels[next][0]);
    setModelEndpoint(restored?.endpoint || boot?.defaults?.[next]?.endpoint || "");
    setDiscoveredModels([]);
  }

  async function sendMessage(event?: FormEvent) {
    event?.preventDefault();
    const typedContent = draft.trim();
    if (!typedContent && !attachments.length) return;
    const content = typedContent || `补充资料：${attachments.map((item) => item.name).join("、")}`;
    if (running) {
      const interaction = classifyRuntimeInteraction(content, attachments);
      const runId = selectedTaskId
        ? taskRunIds.current.get(selectedTaskId)
        : undefined;
      const userMessage: Message = {
        id: crypto.randomUUID(),
        role: "user",
        content,
        createdAt: now(),
        attachments
      };
      if (interaction === "progress") {
        const progressMessage = [
          `现在：${currentRuntimeEvent?.action || currentPlanStep?.title || "正在推进当前步骤"}`,
          currentRuntimeEvent?.detail || currentPlanStep?.detail || runtimePulse,
          `下一步：${taskPlan.find((step) => step.status === "pending")?.title || "形成并检查本轮结果"}`
        ].join("\n\n");
        setMessages((items) => [...items, userMessage, {
          id: crypto.randomUUID(),
          role: "assistant",
          content: progressMessage,
          createdAt: now()
        }]);
        setDraft("");
        setAttachments([]);
        setNotice("任务仍在后台运行，查看进度不会打断它");
        return;
      }
      if (interaction === "stop") {
        if (selectedTaskId) suppressQueuedResumeTasksRef.current.add(selectedTaskId);
        const remaining = runtimeChangeQueueRef.current.filter((item) => item.taskId !== selectedTaskId);
        runtimeChangeQueueRef.current = remaining;
        setRuntimeChangeQueue(remaining);
        setMessages((items) => [...items, userMessage, {
          id: crypto.randomUUID(),
          role: "assistant",
          content: "正在安全停止当前步骤。已经完成的文件、上下文和交付物会保留。",
          createdAt: now()
        }]);
        setDraft("");
        setAttachments([]);
        if (runId) await window.nova.model.cancel({ runId });
        setNotice("正在安全停止当前执行");
        return;
      }
      const change: RuntimeChange = {
        id: crypto.randomUUID(),
        taskId: selectedTaskId || "",
        kind: interaction,
        content,
        attachments,
        queuedAt: now()
      };
      const nextQueue = [...runtimeChangeQueueRef.current, change].slice(-8);
      runtimeChangeQueueRef.current = nextQueue;
      setRuntimeChangeQueue(nextQueue);
      setMessages((items) => [...items, userMessage, {
        id: crypto.randomUUID(),
        role: "assistant",
        content: interaction === "correction"
          ? "方向纠正已收到，正在切换执行路径。当前可取消步骤会立即停止，已完成的文件和上下文会保留，旧方向的后续结果不会进入交付。"
          : "补充资料已收到，正在暂停当前推理并合入新资料。已完成的结果会保留，不需要重新开始。",
        createdAt: now()
      }]);
      setDraft("");
      setAttachments([]);
      setRuntimePulse(interaction === "correction"
        ? "正在停止旧方向并切换到你的最新要求"
        : "正在暂停当前推理并合入新资料");
      setNotice(interaction === "correction"
        ? "最新方向已置顶，正在抢占当前可取消步骤"
        : "新资料已置顶，正在抢占当前可取消步骤");
      // User guidance is a control-plane event, not another low-priority chat
      // message. Cancel the active model/tool step immediately. The existing
      // task snapshot and workspace files remain intact; once the cancellation
      // settles, the queue effect above resumes the same task with all guidance
      // coalesced into one request.
      if (runId) {
        try {
          await window.nova.model.cancel({ runId });
        } catch (error) {
          setNotice(`纠正已经保存；当前步骤停止较慢，将在安全落点自动切换：${readableRunError(error)}`);
        }
      }
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
    const submission = { content, attachments };
    if (currentTaskApprovalMode) {
      void executeSubmission(currentTaskApprovalMode, submission, true);
      return;
    }
    setPendingSubmission(submission);
    setRememberTaskPermission(true);
    setApprovalOpen(true);
  }

  async function executeSubmission(
    approvalMode: ApprovalMode,
    submission: PendingSubmission | null = pendingSubmission,
    rememberForTask = rememberTaskPermission
  ) {
    if (!submission) return;
    const { content, attachments: submittedAttachments } = submission;
    if (!workspace) {
      setNotice("请先选择任务文件夹，NOVA 才能在明确边界内继续。 ");
      return;
    }
    const workspaceAccess = await window.nova.system.checkWorkspaceAccess({ workspace });
    if (!workspaceAccess.exists || !workspaceAccess.readable) {
      setPendingSubmission(submission);
      setApprovalOpen(false);
      setNotice("当前任务文件夹已移动或无法读取。重新选择文件夹后可以直接继续，内容不会丢失。");
      return;
    }
    if (approvalMode !== "readOnly" && !workspaceAccess.writable) {
      setPendingSubmission(submission);
      setApprovalOpen(false);
      setNotice("这个文件夹目前是只读的。请选择可写文件夹，或把本轮权限改为只读分析。");
      return;
    }
    const activeTaskAttachments = mergeAttachments(
      ...messages.map((message) => message.attachments),
      submittedAttachments
    );
    setPendingSubmission(null);
    setApprovalOpen(false);
    const runTaskId = selectedTaskId
      ?? `electron-${crypto.randomUUID().replaceAll("-", "").slice(0, 12)}`;
    selectedTaskIdRef.current = runTaskId;
    setSelectedTaskId(runTaskId);
    if (rememberForTask) {
      setTaskApprovalModes((current) => ({ ...current, [runTaskId]: approvalMode }));
    }
    setTaskCapsule({ status: "not-compiled", detail: "正在按当前目标更新本轮理解…" });
    setRunningTaskIds((current) => new Set(current).add(runTaskId));
    setRightOpen(true);
    setTasks((current) => {
      if (current.some((task) => task.id === runTaskId)) {
        return current.map((task) => task.id === runTaskId
          ? { ...task, status: "Running", state: "Running", summary: "正在执行", progress: 3 }
          : task);
      }
      return [{
        id: runTaskId,
        title: content.split(/\r?\n/, 1)[0].slice(0, 42) || "新任务",
        status: "Running",
        state: "Running",
        summary: "正在执行",
        progress: 3,
        workspaceRoot: workspace || undefined,
        provider,
        model,
        executionMode
      }, ...current];
    });

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
    taskRunIds.current.set(runTaskId, runId);

    try {
      const result = await window.nova.model.run({
        provider,
        model,
        workspace,
        taskId: runTaskId,
        runId,
        approvalMode,
        executionMode,
        crossModelReview,
        agentPackId: selectedAgentPackId,
        messages: nextMessages.map(({ role, content: body }) => ({
          role,
          content: body
        })),
        attachments: activeTaskAttachments
      });
      if (selectedTaskIdRef.current === runTaskId) {
        selectedTaskIdRef.current = result.taskId;
        setSelectedTaskId(result.taskId);
        if (rememberForTask && result.taskId !== runTaskId) {
          setTaskApprovalModes((current) => {
            const next = { ...current, [result.taskId]: approvalMode };
            delete next[runTaskId];
            return next;
          });
        }
        void loadTaskCapsule(result.taskId);
        setMessages((items) => [
          ...items,
          {
            id: crypto.randomUUID(),
            role: "assistant",
            content: result.output,
            createdAt: now(),
            verification: result.verification,
            delivery: result.delivery
          }
        ]);
      }
      setNotice(
        result.delivery?.status === "PARTIAL"
          ? result.delivery.summary
          : result.verification?.verdict === "PASS"
            ? "本轮已经过不同模型源的独立复核，审查结论已随结果保存"
            : "本轮结果已写入任务线程，文件变更由 Agent Runtime 实际执行"
      );
      addActivity(
        result.delivery?.status === "PARTIAL" ? "本轮待继续" : "本轮已完成",
        `任务 ${result.taskId} · ${result.toolCalls || 0} 次工具调用`,
        result.delivery?.status === "PARTIAL" ? "failed" : "done"
      );
      // Refreshing the task rail is a non-critical follow-up. A locked snapshot or
      // transient Windows permission error here must never overwrite a successful
      // model result with a false "这一轮没有安全完成" message.
      await refreshTasks().catch(() => undefined);
    } catch (error) {
      const message = readableRunError(error);
      if (message.includes("NOVA_RUN_CANCELLED")) {
        setNotice("上一条执行路径已停止，工作区现场与上下文仍然保留");
        addActivity("执行已停止", "等待新的方向", "done");
        return;
      }
      const isPathAccessError = /access to the path is denied|eacces|eperm|permission denied|read-only file system|readonly filesystem/i.test(message);
      // The folder passed an actual create/write probe immediately before the run.
      // A later AccessDenied therefore normally points to a protected/locked child
      // file or an internal runtime path, not to misuse of the selected workspace.
      const isWorkspacePermissionError = isPathAccessError && !workspaceAccess.writable;
      const isFileAccessError = isPathAccessError && workspaceAccess.writable;
      const isAuthenticationError = /\b(401|403)\b|unauthorized|invalid api key|authentication|forbidden|鉴权|凭证|令牌/i.test(message);
      const isToolApprovalError = /approval|permission boundary|not approved|拒绝授权|未授权工具|权限请求/i.test(message);
      const isUnsupportedAttachment = /(image|vision|attachment|multimodal|图片|图像|附件).*(not support|unsupported|不支持)/i.test(message)
        || /(not support|unsupported|不支持).*(image|vision|attachment|multimodal|图片|图像|附件)/i.test(message);
      const userFacingMessage = isAuthenticationError
          ? "模型或外部能力的身份验证没有通过。工作区本身不一定有问题；请检查 API Key、登录状态或 MCP 授权后重试。"
          : isToolApprovalError
            ? "本轮需要的工具权限没有获批。你可以允许这项操作，或让 NOVA 改用不需要该权限的方案继续。"
          : isWorkspacePermissionError
            ? "任务文件夹没有通过真实写入测试。请检查系统保护、文件夹属性，或重新选择一个可写文件夹。"
          : isFileAccessError
            ? "任务文件夹本身可以写入，但某个具体文件或 NOVA 内部状态目录被系统拒绝访问。通常是文件占用或系统保护，不需要重新开始；关闭占用程序后可直接重试。"
        : isUnsupportedAttachment
          ? `当前模型不能读取这类附件。${activeTaskAttachments.length} 个任务资料都已保留，请切换到支持图片或文档的模型后直接继续，不需要重新上传。`
          : message;
      if (selectedTaskIdRef.current === runTaskId) setNotice(userFacingMessage);
      addActivity("本轮需要处理", message, "failed");
      if (selectedTaskIdRef.current === runTaskId) setMessages((items) => [
        ...items,
        {
          id: crypto.randomUUID(),
          role: "assistant",
          content: `本轮暂停：${userFacingMessage}\n\n上下文和已经完成的结果都已保留，不需要重新开始。`,
          createdAt: now()
        }
      ]);
      await refreshTasks().catch(() => undefined);
    } finally {
      taskRunIds.current.delete(runTaskId);
      setPendingToolApproval((current) => current?.taskId === runTaskId ? null : current);
      setRunningTaskIds((current) => {
        const next = new Set(current);
        next.delete(runTaskId);
        return next;
      });
      if (selectedTaskIdRef.current === runTaskId) {
        streamBuffer.current = "";
        setStreamingText("");
        if (streamFlushTimer.current) {
          clearTimeout(streamFlushTimer.current);
          streamFlushTimer.current = null;
        }
      }
    }
  }

  async function stopCurrentRun() {
    if (!selectedTaskId) return;
    suppressQueuedResumeTasksRef.current.add(selectedTaskId);
    const remaining = runtimeChangeQueueRef.current.filter((item) => item.taskId !== selectedTaskId);
    runtimeChangeQueueRef.current = remaining;
    setRuntimeChangeQueue(remaining);
    const runId = taskRunIds.current.get(selectedTaskId);
    if (!runId) return;
    await window.nova.model.cancel({ runId });
    setNotice("正在安全停止当前执行");
  }

  async function resolveToolApproval(approved: boolean) {
    const request = pendingToolApproval;
    if (!request) return;
    const rememberThisTask = approved
      && rememberToolPermission
      && request.canRememberForTask !== false
      && !rememberWorkspacePermission;
    const rememberThisWorkspace = approved
      && rememberWorkspacePermission
      && request.canPersistForWorkspace === true
      && request.requiresExplicitApproval !== true;
    setPendingToolApproval(null);
    try {
      const result = await window.nova.model.resolveApproval({
        approvalId: request.id,
        approved,
        rememberForTask: rememberThisTask,
        rememberForWorkspace: rememberThisWorkspace
      });
      if (!result.resolved || result.expired) {
        setNotice("这条权限请求已经过期，NOVA 会按当前任务状态继续处理。");
        return;
      }
      setNotice(
        approved
          ? rememberThisWorkspace
            ? `已允许 ${request.toolName}，以后在这个工作区内自动放行`
            : rememberThisTask
              ? `已允许 ${request.toolName}，本任务内相同能力将自动放行`
            : `已允许 ${request.toolName} 执行这一次`
          : `已拒绝 ${request.toolName}，NOVA 会尝试不使用它继续推进`
      );
    } catch (error) {
      setNotice(error instanceof Error ? error.message : "权限确认没有成功送达");
    }
  }

  function resetTaskState() {
    selectedTaskIdRef.current = null;
    setSelectedTaskId(null);
    setMessages([]);
    setTaskCapsule(null);
    setDraft("");
    setAttachments([]);
    setAgentLaunchValues({});
    setAgentLaunchOpen(Boolean(selectedAgentPackId && agentLaunchGuide?.onboarding));
    setTaskPlan([]);
    setCurrentRuntimeEvent(null);
    setPendingToolApproval(null);
    setRememberTaskPermission(true);
  }

  async function createTaskWithWorkspace(
    useCurrentWorkspace = false,
    preferredWorkspace: string | null = null
  ) {
    const selected = preferredWorkspace
      || (useCurrentWorkspace && workspace ? workspace : await chooseWorkspace());
    if (!selected) {
      setNotice("已取消创建；选择任务文件夹后才会建立新任务");
      return false;
    }
    resetTaskState();
    setWorkspace(selected);
    setNewTaskGuideOpen(false);
    setNotice(
      selectedAgentPackId && agentLaunchGuide?.onboarding
        ? `新任务已绑定到 ${selected.split(/[\\/]/).filter(Boolean).at(-1)}，按专业 Agent 引导补充线索即可开始`
        : `新任务已绑定到 ${selected.split(/[\\/]/).filter(Boolean).at(-1)}，现在告诉我想达成什么结果`
    );
    setRightOpen(true);
    if (window.innerWidth <= 1180) setLeftOpen(false);
    return true;
  }

  function newTask() {
    setNewTaskGuideOpen(true);
  }

  const headingNotice = notice
    .replace("已恢复任务上下文，可以继续追问或修改方向", "上下文已恢复，可继续追问")
    .replace("新线程已准备好，告诉我想达成什么结果", "新任务已就绪")
    .replace("内核在线，等待你的目标", "等待你的目标");
  const headingAlert = /(失败|错误|异常|阻塞|权限|恢复|连接|需要|未完成)/.test(headingNotice)
    ? headingNotice
    : "";

  return (
    <div className={`app-shell ${leftOpen ? "" : "rail-collapsed"} ${rightOpen ? "" : "trace-collapsed"}`}>
      <header className="titlebar">
        <div className="brand">
          <button
            className="rail-menu-toggle"
            type="button"
            aria-label={leftOpen ? "收起任务空间" : "展开任务空间"}
            title={leftOpen ? "收起任务空间" : "展开任务空间"}
            onClick={() => setLeftOpen((value) => !value)}
          >
            <Menu size={18} />
          </button>
          <div className="brand-core"><span /></div>
          <strong>NOVA</strong>
        </div>
        <div className="kernel-strip">
          <span className={`status-dot ${boot ? "online" : ""}`} />
          <span>
            {boot
              ? "运行正常"
              : "正在启动"}
          </span>
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
        <button className="new-agent-quick" type="button" onClick={openAgentFoundry}>
          <Sparkles size={16} />
          <span>创建专属 Agent</span>
        </button>
        <div className="rail-heading">
          <span>任务空间</span>
          <small>{tasks.length}</small>
        </div>
        {tasks.length > 8 && (
          <label className="task-search">
            <Search size={14} />
            <input
              value={taskQuery}
              onChange={(event) => setTaskQuery(event.target.value)}
              placeholder="搜索任务"
              aria-label="搜索任务"
            />
            {taskQuery && (
              <button type="button" aria-label="清空搜索" onClick={() => setTaskQuery("")}>
                <X size={13} />
              </button>
            )}
          </label>
        )}
        <div className="task-list">
          {visibleTasks.length ? (
            visibleTasks.map((task) => (
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
                    <small>{statusTone(task.status) === "done" ? "已完成" : statusTone(task.status) === "failed" ? "需处理" : statusTone(task.status) === "running" ? "进行中" : "已保存"}</small>
                  </span>
                </button>
                <button
                  className="task-archive-action"
                  type="button"
                  aria-label={`归档 ${task.title || "任务"}`}
                  title="移入归档库"
                  disabled={runningTaskIds.has(task.id)}
                  onClick={() => setPendingArchiveTask(task)}
                >
                  <Archive size={15} />
                </button>
              </div>
            ))
          ) : taskQuery ? (
            <div className="task-search-empty">
              <Search size={18} />
              <span>没有匹配的任务</span>
            </div>
          ) : (
            <div className="rail-empty">
              <Sparkles size={22} />
              <strong>从一个真实目标开始</strong>
              <span>任务会自动沉淀在这里</span>
            </div>
          )}
          {!taskQuery && filteredTasks.length > 8 && (
            <button
              type="button"
              className="task-list-more"
              onClick={() => setTaskListExpanded((value) => !value)}
            >
              {taskListExpanded ? "收起任务" : `查看其余 ${filteredTasks.length - 8} 个任务`}
              <ChevronDown size={14} />
            </button>
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
            <h1>{messages.length ? "持续推进当前目标" : "把想做成的事交给我"}</h1>
            {headingAlert && <p title={notice}>{headingAlert}</p>}
          </div>
          <div className="heading-actions">
            <button
              type="button"
              className={`active-agent-chip ${selectedAgentPack ? "active" : ""}`}
              disabled={running}
              onClick={() => openSettings("agents")}
              title="切换 Agent"
            >
              <Bot size={15} />
              <span>{selectedAgentPack ? selectedAgentPack.name : "通用 NOVA"}</span>
            </button>
            <button className="workspace-chip" onClick={chooseWorkspace}>
              <FolderOpen size={15} />
              {workspaceName}
            </button>
            {!rightOpen && (
              <button
                type="button"
                className="trace-toggle trace-reopen"
                onClick={() => setRightOpen(true)}
                title="展开执行过程"
                aria-label="展开执行过程"
              >
                <Activity size={17} />
                <span>执行过程</span>
              </button>
            )}
          </div>
        </section>

        <section className="threadspace">
          <div className="conversation">
            {messages.length === 0 ? (
              <div className="empty-state">
                <div className="empty-mark">
                  <Zap size={25} />
                </div>
                <h2>{workspace ? "准备好了，想先做成什么？" : "先选一个保存结果的文件夹"}</h2>
                <p>{workspace
                  ? `直接在下方说结果。NOVA 会从“${workspaceName}”读取资料，并把成果留在这里。`
                  : "资料、修改和交付物都会留在这个文件夹，不会和其他任务混在一起。"}</p>
                {!workspace && (
                  <div className="empty-state-actions">
                    <button type="button" className="empty-workspace-action primary" onClick={newTask}>
                      <FolderOpen size={16} />
                      选择任务文件夹
                    </button>
                  </div>
                )}
              </div>
            ) : (
              <div className="message-list">
                {messages.map((message) => {
                  const parsed = parseChoices(message.content);
                  const presentation = parseDeliveryPresentation(parsed.display);
                  const deliveryArtifacts: PresentedArtifact[] = (
                    message.delivery?.artifacts?.length
                      ? message.delivery.artifacts
                      : presentation.artifacts
                  )
                    .filter((artifact) => !isInternalPresentedArtifact(artifact))
                    .sort((left, right) => presentedArtifactPriority(right) - presentedArtifactPriority(left));
                  const preferredArtifacts = deliveryArtifacts.filter(
                    (artifact) => presentedArtifactPriority(artifact) >= 40
                  );
                  const featuredArtifacts = (preferredArtifacts.length
                    ? preferredArtifacts
                    : deliveryArtifacts
                  ).slice(0, 4);
                  const supportingArtifacts = deliveryArtifacts.filter(
                    (artifact) => !featuredArtifacts.includes(artifact)
                  );
                  const plainDeliverySummary = message.delivery
                    ? message.delivery.status === "PARTIAL"
                      ? "仍有未完成项，可查看详情或让 NOVA 继续。"
                      : deliveryArtifacts.length
                        ? `已生成 ${deliveryArtifacts.length} 个文件，可直接打开或修改。`
                        : "结果已保存在对话中，可继续修改或补充资料。"
                    : "";
                  return (
                  <article className={`message ${message.role}`} key={message.id}>
                    <div className="message-meta">
                      {message.role === "assistant" ? <Bot size={16} /> : <Circle size={11} fill="currentColor" />}
                      <strong>{message.role === "assistant" ? "NOVA" : "你"}</strong>
                      <time>{message.createdAt}</time>
                    </div>
                    {(!message.delivery || message.role !== "assistant") && (
                      <div className={`message-body ${message.role === "assistant" ? "markdown-body" : ""}`}>
                        {message.role === "assistant"
                          ? <MarkdownContent content={presentation.display} />
                          : parsed.display}
                      </div>
                    )}
                    {message.role === "assistant" && message.delivery && (
                      <section
                        className={`delivery-result ${message.delivery.status.toLowerCase()}`}
                      >
                        <header className="delivery-result-hero">
                          <div className="delivery-result-mark"><ShieldCheck size={19} /></div>
                          <div>
                            <span>本轮成果 · v{message.delivery.revision || 1}</span>
                            <strong>
                              {presentation.outcome?.verdict ||
                                (message.delivery.status === "PARTIAL" ? "还需要一步" : "已经可以接手")}
                            </strong>
                            <small>{plainDeliverySummary}</small>
                          </div>
                          <button
                            type="button"
                            className="delivery-review-open"
                            onClick={() => openDeliverySummary("本轮交付说明", presentation.display)}
                          >
                            查看与修改
                          </button>
                          <b>
                            {message.delivery.reviewState === "accepted"
                              ? "已验收"
                              : message.delivery.reviewState === "changes-requested"
                                ? "待修复"
                                : message.delivery.status === "PARTIAL"
                                  ? "待继续"
                                  : "已完成"}
                          </b>
                        </header>

                        {!!featuredArtifacts.length && (
                          <section className="delivery-artifacts">
                            <header><strong>本轮交付物</strong><span>{deliveryArtifacts.length} 项</span></header>
                            <div>
                              {featuredArtifacts.map((artifact) => (
                                <button
                                  type="button"
                                  className="delivery-artifact-item"
                                  key={`${"id" in artifact ? artifact.id : artifact.label}-${artifact.path}`}
                                  disabled={deliveryReviewLoading}
                                  onClick={() => void openDeliveryArtifact(artifact)}
                                >
                                  <FileCode2 size={16} />
                                  <span>
                                    <strong>{"title" in artifact ? artifact.title : artifact.label}</strong>
                                    <small>{"relativePath" in artifact ? artifact.relativePath : artifact.path}</small>
                                  </span>
                                </button>
                              ))}
                            </div>
                          </section>
                        )}

                        {!deliveryArtifacts.length && (
                          <div className="delivery-empty-artifacts">
                            本轮没有产生文件型交付物；当前结果按对话交付保存，可直接反馈继续完善。
                          </div>
                        )}

                        {presentation.nextAction && (
                          <button
                            type="button"
                            className="delivery-next-action"
                            onClick={() => setDraft(presentation.nextAction)}
                          >
                            <span><small>建议下一步</small><strong>{presentation.nextAction}</strong></span>
                            <Send size={16} />
                          </button>
                        )}

                        <details className="delivery-technical-details" open={message.delivery.status === "PARTIAL"}>
                          <summary>检查记录（可选） <ChevronDown size={15} /></summary>

                          {!!supportingArtifacts.length && (
                            <section className="delivery-artifacts supporting">
                              <header><strong>辅助文件</strong><span>{supportingArtifacts.length} 项</span></header>
                              <div>
                                {supportingArtifacts.map((artifact) => (
                                  <button
                                    type="button"
                                    className="delivery-artifact-item"
                                    key={`${"id" in artifact ? artifact.id : artifact.label}-${artifact.path}`}
                                    disabled={deliveryReviewLoading}
                                    onClick={() => void openDeliveryArtifact(artifact)}
                                  >
                                    <FileCode2 size={16} />
                                    <span>
                                      <strong>{"title" in artifact ? artifact.title : artifact.label}</strong>
                                      <small>{"relativePath" in artifact ? artifact.relativePath : artifact.path}</small>
                                    </span>
                                  </button>
                                ))}
                              </div>
                            </section>
                          )}

                          {!!presentation.metrics.length && (
                            <div className="delivery-metrics">
                              {presentation.metrics.map((metric) => (
                                <span key={`${metric.label}-${metric.value}`}>
                                  <small>{metric.label}</small>
                                  <strong>{metric.value}</strong>
                                </span>
                              ))}
                            </div>
                          )}

                          <div className="delivery-proof-row">
                            <span>文件落盘 <b>{message.delivery.hasWorkspaceChanges ? "已完成" : "无变更"}</b></span>
                            <span>本机检查 <b>{message.delivery.validationRuns} 项</b></span>
                            {message.verification && <span>独立复核 <b>{message.verification.verdict} · {message.verification.confidence}%</b></span>}
                          </div>

                          <div className="delivery-report-body message-body markdown-body">
                            <MarkdownContent content={presentation.display} />
                          </div>

                          {message.verification && (
                            <details className="verification-detail">
                              <summary>
                                {message.verification.provider
                                  ? `${providerLabels[
                                      message.verification.provider as Provider
                                    ] || message.verification.provider} · ${
                                      message.verification.model
                                    }`
                                  : "异构复核未启用"}
                                <span>{message.verification.summary}</span>
                              </summary>
                              {message.verification.details && (
                                <MarkdownContent content={message.verification.details} />
                              )}
                            </details>
                          )}
                        </details>
                      </section>
                    )}
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
                          file.kind === "image" ? (
                            <AttachmentThumbnail key={file.id} file={file} onOpen={setImagePreview} />
                          ) : (
                            <span className="message-attachment-file" key={file.id}>
                              <FileCode2 size={14} />
                              <span>{file.name}</span>
                            </span>
                          )
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
            placeholder={running
              ? "任务仍在后台执行。可以问“到哪了”，补充文件，或说“改成……”"
              : "描述想达成的结果，NOVA 会自己理解工程并持续推进…"}
            rows={3}
          />
          <div className="composer-bar">
            <div className="composer-tools">
              <div className="composer-context-actions">
                <button type="button" onClick={chooseAttachments}>
                  <Paperclip size={17} />
                  <span>附件</span>
                </button>
              </div>
              <div className="composer-execution-actions">
              <label className={`agent-pack-control ${selectedAgentPack ? "active" : ""}`}>
                <Bot size={16} />
                <select
                  aria-label="选择专业 Agent"
                  value={selectedAgentPackId || ""}
                  disabled={running || agentPacksLoading}
                  onChange={(event) => {
                    const nextId = event.target.value || null;
                    void activateAgentPack(nextId);
                  }}
                >
                  <option value="">通用 NOVA</option>
                  {agentPacks.filter((pack) => pack.enabled).map((pack) => (
                    <option value={pack.id} key={pack.id}>{pack.category} · {pack.name}</option>
                  ))}
                </select>
                <ChevronDown size={14} />
              </label>
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
              <details className="composer-more-options">
                <summary title="更多任务设置">
                  <Settings2 size={16} />
                  <span>更多</span>
                  <ChevronDown size={13} />
                </summary>
                <div className="composer-more-menu">
                  <button type="button" onClick={chooseWorkspace} disabled={running}>
                    <FolderOpen size={15} />
                    <span>切换工作区</span>
                  </button>
                  {selectedAgentPack && agentLaunchGuide?.onboarding && (
                    <button type="button" disabled={running} onClick={() => setAgentLaunchOpen(true)}>
                      <Sparkles size={15} />
                      <span>查看 Agent 使用引导</span>
                    </button>
                  )}
                  {currentTaskApprovalMode && (
                    <button
                      type="button"
                      disabled={running}
                      onClick={() => {
                        if (!selectedTaskId) return;
                        setTaskApprovalModes((current) => {
                          const next = { ...current };
                          delete next[selectedTaskId];
                          return next;
                        });
                        setNotice("已取消沿用权限；下一轮执行前会重新让你选择");
                      }}
                    >
                      <ShieldCheck size={15} />
                      <span>权限：{approvalModeLabel(currentTaskApprovalMode)}</span>
                    </button>
                  )}
                  <button
                    type="button"
                    className={crossModelReview ? "active" : ""}
                    disabled={running}
                    onClick={() => {
                      if (!reviewCandidates.length) {
                        openSettings("model");
                        setNotice("双模型复核需要再连接一个不同来源的模型");
                        return;
                      }
                      setCrossModelReview((value) => !value);
                    }}
                  >
                    <ShieldCheck size={15} />
                    <span>{crossModelReview ? "双模型复核已开启" : "开启双模型复核"}</span>
                  </button>
                  <div className="composer-context-summary">
                    <MessageSquareText size={14} />
                    <span>{messages.length ? `${messages.length} 条上下文已保存` : "新会话"}</span>
                    {!!retainedTaskAttachments.length && <small>· {retainedTaskAttachments.length} 个资料已保留</small>}
                  </div>
                </div>
              </details>
              </div>
            </div>
            <div className="run-actions">
              {running && (
                <button type="button" className="stop-button" onClick={stopCurrentRun}>
                  <Square size={14} fill="currentColor" />
                  停止
                </button>
              )}
              <button className="send-button" disabled={!draft.trim() && !attachments.length}>
                {running ? <RefreshCw size={18} /> : <Send size={18} />}
                <span>
                  {running
                    ? runtimeSubmitLabel
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
            <span>执行过程</span>
            <small>当前步骤 · 下一步 · 阶段产出</small>
          </div>
          <button
            type="button"
            className="trace-collapse"
            onClick={() => setRightOpen(false)}
            aria-label="收起执行过程"
            title="收起执行过程"
          >
            <span>收起</span>
            <ChevronRight size={15} />
          </button>
        </div>
        <div className={`progress-card execution-now ${running ? "running" : ""}`}>
          <span>
            <strong>{running ? "当前正在做" : "当前状态"}</strong>
            <b>{taskPlan.length ? `${completedPlanSteps}/${taskPlan.length}` : running ? "执行中" : "待命"}</b>
          </span>
          <h3>
            {running
              ? currentRuntimeEvent?.action || currentPlanStep?.title || "正在理解目标与工作区"
              : taskPlan.length && currentPlanStep
                ? currentPlanStep.title
                : "等待你的目标"}
          </h3>
          <p>
            {running
              ? currentRuntimeEvent?.detail || currentPlanStep?.detail || runtimePulse
              : currentPlanStep?.detail || "任务开始后，这里显示当前步骤和结果。"}
          </p>
          <small>
            {running
              ? `${currentRuntimeEvent?.agent || currentPlanStep?.agent || "NOVA"} · ${runtimePulse}`
              : "NOVA AgentOS"}
          </small>
          <div>
            <i
              className={running ? "active" : ""}
              style={{
                width: `${Math.max(
                  running ? 6 : 0,
                  currentRuntimeEvent?.progress
                    ?? (taskPlan.length ? Math.round((completedPlanSteps / taskPlan.length) * 100) : 0)
                )}%`
              }}
            />
          </div>
        </div>
        <div className="trace-list">
        {selectedTaskId && taskCapsule && (
          <section className="context-capsule-panel">
            <details>
              <summary>
                <span className="context-capsule-icon"><BrainCircuit size={15} /></span>
                <div>
                  <strong>本轮理解</strong>
                  <small>
                    {taskCapsule.status === "not-compiled"
                      ? "等待生成上下文"
                      : `${taskCapsule.selections?.length || 0} 个文件 · ${taskCapsule.contextCacheHit ? "已复用" : "已更新"}`}
                  </small>
                </div>
                <b>{taskCapsule.status === "not-compiled" ? "待生成" : `${taskCapsule.usedCharacters || 0}/${taskCapsule.characterBudget || 0}`}</b>
                <ChevronDown size={14} />
              </summary>
              {taskCapsule.status === "not-compiled" ? (
                <p className="context-capsule-empty">
                  {taskCapsule.detail || "开始执行后，NOVA 会在这里说明本轮纳入了哪些上下文。"}
                </p>
              ) : (
                <div className="context-capsule-body">
                  <div className="context-budget-meter">
                    <span>
                      <strong>上下文预算</strong>
                      <small>约 {(taskCapsule.estimatedPromptTokens || 0).toLocaleString("zh-CN")} Token</small>
                    </span>
                    <i>
                      <b style={{ width: `${Math.min(100, Math.round(((taskCapsule.usedCharacters || 0) / Math.max(1, taskCapsule.characterBudget || 1)) * 100))}%` }} />
                    </i>
                  </div>
                  {!!taskCapsule.layers?.length && (
                    <div className="context-layer-list">
                      {taskCapsule.layers.filter((layer) => layer.includedCharacters > 0).slice(0, 5).map((layer) => (
                        <div key={layer.id}>
                          <span>{layer.label}</span>
                          <small>{layer.includedCharacters.toLocaleString("zh-CN")} 字符</small>
                        </div>
                      ))}
                    </div>
                  )}
                  {!!taskCapsule.selections?.length && (
                    <div className="context-file-list">
                      <strong>纳入的工作区证据</strong>
                      {taskCapsule.selections.slice(0, 5).map((selection) => (
                        <div key={`${selection.relativePath}-${selection.startLine}`}>
                          <span title={selection.relativePath}>{selection.relativePath}</span>
                          <small>{selection.reasons[0] || `第 ${selection.startLine}-${selection.endLine} 行`}</small>
                        </div>
                      ))}
                      {(taskCapsule.selections.length || 0) > 5 && (
                        <small className="context-more">另有 {(taskCapsule.selections.length || 0) - 5} 个高信号文件</small>
                      )}
                    </div>
                  )}
                </div>
              )}
            </details>
          </section>
        )}
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
          <details className="agent-roster">
            <summary className="agent-roster-heading">
              <span>参与的 Agent</span>
              <small>{Object.keys(agentUnits).length}</small>
              <ChevronDown size={14} />
            </summary>
            <div className="agent-roster-list">
              {Object.values(agentUnits).map((unit) => (
                <details key={unit.agent}>
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
            </div>
          </details>
        )}
        <details className="trace-history">
          <summary>
            <span>运行记录</span>
            <small>{activity.length || 0}</small>
            <ChevronDown size={14} />
          </summary>
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
                <strong>还没有运行记录</strong>
              </div>
            )}
          </div>
        </details>
        </div>
      </aside>

      {agentFoundryOpen && (
        <div
          className="modal-layer agent-foundry-layer"
          onMouseDown={(event) => {
            if (event.currentTarget === event.target && !agentFoundryPreparing) setAgentFoundryOpen(false);
          }}
        >
          <form className="agent-foundry-modal" onSubmit={prepareAgentFoundry}>
            <header>
              <div className="agent-foundry-symbol"><Sparkles size={20} /></div>
              <div>
                <span>创建专属 Agent</span>
                <h2>你想让它帮你做什么？</h2>
                <p>用一句话描述业务目标。行业分析、角色设计和工作流由 NOVA 完成。</p>
              </div>
              <button type="button" aria-label="关闭" disabled={agentFoundryPreparing} onClick={() => setAgentFoundryOpen(false)}>
                <X size={18} />
              </button>
            </header>

            <label>
              <span>我的需求</span>
              <textarea
                autoFocus
                rows={5}
                value={agentFoundryGoal}
                disabled={agentFoundryPreparing}
                onChange={(event) => {
                  setAgentFoundryGoal(event.target.value);
                  setAgentFoundryError("");
                }}
                placeholder="例如：帮销售团队整理客户线索，判断跟进优先级，并生成每天的行动清单。"
              />
            </label>

            <div className="agent-foundry-examples">
              <span>也可以从这里开始</span>
              <div>
                {["分析新品有没有市场机会", "整理客户线索并给出跟进建议", "根据现有资料生成合规报告"].map((example) => (
                  <button type="button" key={example} disabled={agentFoundryPreparing} onClick={() => setAgentFoundryGoal(example)}>
                    {example}
                  </button>
                ))}
              </div>
            </div>

            {agentFoundryError && <div className="agent-foundry-error">{agentFoundryError}</div>}

            <footer>
              <button
                type="button"
                disabled={agentFoundryPreparing}
                onClick={() => {
                  setAgentFoundryOpen(false);
                  setAgentWorkshopSimpleMode(false);
                  setAgentWorkshopOpen(true);
                  openSettings("agents");
                }}
              >
                自己设置细节
              </button>
              <button type="submit" className="primary" disabled={agentFoundryPreparing || agentFoundryGoal.trim().length < 8}>
                {agentFoundryPreparing ? <RefreshCw size={17} /> : <Sparkles size={17} />}
                {agentFoundryPreparing ? "正在理解你的业务…" : "让 NOVA 开始设计"}
              </button>
            </footer>
          </form>
        </div>
      )}

      {agentLaunchOpen && agentLaunchGuide?.onboarding && (
        <div
          className="modal-layer agent-launch-layer"
          onMouseDown={(event) => {
            if (event.currentTarget === event.target) setAgentLaunchOpen(false);
          }}
        >
          <div className="agent-launch-modal">
            <header>
              <div className="agent-launch-symbol"><Bot size={20} /></div>
              <div>
                <span>{agentLaunchGuide.summary.category} · 启动引导</span>
                <h2>{agentLaunchGuide.onboarding.headline}</h2>
                <p>{agentLaunchGuide.onboarding.description}</p>
              </div>
              <button type="button" onClick={() => setAgentLaunchOpen(false)}><X size={18} /></button>
            </header>

            {agentCapabilityReport && agentCapabilityReport.items.length > 0 && (
              <section className="agent-capability-preflight">
                <header>
                  <div>
                    <span>能力准备</span>
                    <strong>
                      {agentCapabilityReport.requiredReadyCount}/{agentCapabilityReport.requiredCount} 项必需能力已就绪
                    </strong>
                  </div>
                  <small>只检查状态；扫描、登记、启用和真实调用都需要你明确操作。</small>
                </header>
                <div>
                  {agentCapabilityReport.items.map((item) => (
                    <article className={item.state} key={item.id}>
                      <span className="agent-capability-kind">{item.kind.toUpperCase()}</span>
                      <div>
                        <strong>{item.name}</strong>
                        <p>{item.reason}</p>
                        <small>{item.required ? "本 Agent 建议就绪" : "可选增强"}</small>
                      </div>
                      <button
                        type="button"
                        disabled={capabilityPreparing || item.state === "ready"}
                        onClick={() => void prepareAgentCapability(item)}
                      >
                        {item.state === "ready"
                          ? "已就绪"
                          : item.state === "registered-disabled"
                            ? "审阅并启用"
                            : item.state === "available"
                              ? "审阅并加载"
                              : item.kind === "mcp"
                                ? "扫描或接入"
                                : "去能力超市"}
                      </button>
                    </article>
                  ))}
                </div>
              </section>
            )}

            <section className="agent-launch-materials">
              <div>
                <span>任务资料</span>
                <strong>文件只需上传一次，所有步骤共同使用</strong>
                <small>图片、Word、PDF、表格和其他资料可以一起添加；也可以只填写文字后直接开始。</small>
              </div>
              <button type="button" onClick={() => void chooseAttachments()}>
                <Paperclip size={16} />
                {attachments.length ? `继续添加（已有 ${attachments.length} 个）` : "添加文件"}
              </button>
              {!!attachments.length && (
                <p>{attachments.map((item) => item.name).join(" · ")}</p>
              )}
            </section>

            <section className="agent-launch-steps">
              {agentLaunchGuide.onboarding.steps.map((step, index) => (
                <article className="agent-launch-step" key={step.id}>
                  <div className="agent-launch-step-number">{index + 1}</div>
                  <div className="agent-launch-step-content">
                    <header>
                      <strong>{step.title}</strong>
                      <span>{step.required ? "建议提供" : "可选"}</span>
                    </header>
                    <p>{step.description}</p>
                    {step.kind === "select" ? (
                      <select
                        value={agentLaunchValues[step.id] || ""}
                        onChange={(event) => setAgentLaunchValues((values) => ({
                          ...values,
                          [step.id]: event.target.value
                        }))}
                      >
                        <option value="">{step.placeholder || "请选择"}</option>
                        {step.options.map((option) => <option value={option} key={option}>{option}</option>)}
                      </select>
                    ) : (
                      <textarea
                        rows={2}
                        value={agentLaunchValues[step.id] || ""}
                        placeholder={step.kind === "attachment"
                          ? "可以用一句话说明；文件请使用上方统一入口添加"
                          : step.placeholder}
                        onChange={(event) => setAgentLaunchValues((values) => ({
                          ...values,
                          [step.id]: event.target.value
                        }))}
                      />
                    )}
                    <details>
                      <summary>为什么需要这项资料？</summary>
                      <p>{step.whyItMatters}</p>
                      {step.example && <small>{step.example}</small>}
                    </details>
                  </div>
                </article>
              ))}
            </section>

            {agentLaunchError && <div className="agent-launch-error">{agentLaunchError}</div>}

            {!!agentLaunchGuide.onboarding.outcomes.length && <section className="agent-launch-outcomes">
              <header>
                <div><span>最后一步</span><strong>你想先得到什么？</strong></div>
                <small>选中后立即进入任务；涉及桌面、发布或其他新权限时，NOVA 会再单独询问。</small>
              </header>
              <div>
                {agentLaunchGuide.onboarding.outcomes.map((outcome, index) => (
                  <button
                    type="button"
                    className={index === 0 ? "recommended" : ""}
                    key={outcome.id}
                    onClick={() => void useAgentLaunchOutcome(outcome.id)}
                  >
                    <strong>{outcome.title}</strong>
                    <span>{outcome.description}</span>
                    <b>{index === 0 ? "推荐并启动" : "立即启动"}</b>
                  </button>
                ))}
              </div>
            </section>}

            <footer>
              <span>资料不完整不会阻止开始；NOVA 会降低结论级别，并告诉你下一份最值得收集的证据。</span>
              <div className="agent-launch-footer-actions">
                <button type="button" onClick={() => setAgentLaunchOpen(false)}>返回对话</button>
                <button
                  type="button"
                  className="primary"
                  disabled={running}
                  onClick={() => void useAgentLaunchOutcome(agentLaunchGuide.onboarding?.outcomes[0]?.id)}
                >
                  <Send size={15} />
                  {running ? "当前任务执行中" : "开始执行"}
                </button>
              </div>
            </footer>
          </div>
        </div>
      )}

      {settingsOpen && (
        <div className="modal-layer" onMouseDown={(event) => {
          if (event.currentTarget === event.target) setSettingsOpen(false);
        }}>
          <div className={`dock-modal dock-section-${settingsSection}`}>
            <aside className="dock-nav">
              <div className="dock-brand">
                <Boxes size={19} />
                <span><strong>扩展坞</strong><small>模型、能力与远程环境</small></span>
              </div>
              {([
                ["agents", "Agent 工坊", Bot],
                ["model", "模型", KeyRound],
                ["mcp", "MCP", Server],
                ["skills", "Skills", BrainCircuit],
                ["knowledge", "知识库", BookOpen],
                ["growth", "成长", Sparkles],
                ["permissions", "权限", ShieldCheck],
                ["gateway", "服务接口", Webhook],
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
                    if (id === "agents") void loadAgentPacks();
                    if (id === "knowledge") void loadKnowledge();
                    if (id === "growth") void loadGrowthState();
                    if (id === "permissions") void loadWorkspacePermissions();
                    if (id === "ssh" || id === "cloud" || id === "gateway") {
                      void loadExtensionProfiles();
                    }
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
                    {settingsSection === "agents" && "Agent 中心"}
                    {settingsSection === "model" && "模型连接"}
                    {settingsSection === "mcp" && "MCP 连接"}
                    {settingsSection === "skills" && "Skills"}
                    {settingsSection === "knowledge" && "知识库"}
                    {settingsSection === "growth" && "成长与桌面"}
                    {settingsSection === "permissions" && "工作区授权"}
                    {settingsSection === "gateway" && "服务接口"}
                    {settingsSection === "ssh" && "SSH 工作区"}
                    {settingsSection === "cloud" && "云开发适配器"}
                    {settingsSection === "plugins" && "能力组件"}
                  </h2>
                  <p>
                    {settingsSection === "agents"
                      ? "专业 Agent 只扩展任务知识与工作流，不改变 NOVA 的权限和证据边界。"
                      : settingsSection === "knowledge"
                        ? "把当前工作区的资料变成可检索、可追溯的本地知识，不会自动上传到外部服务。"
                        : settingsSection === "gateway"
                          ? "让你授权的本机网页或微服务只读订阅 NOVA 任务、交付物与实时事件。"
                        : settingsSection === "permissions"
                          ? "查看和撤销长期授权。终端权限只对指定工作区生效，高风险命令始终需要再次确认。"
                        : "只显示真实状态；安装、启用和外部访问都需要明确确认。"}
                  </p>
                </div>
                <button type="button" onClick={() => setSettingsOpen(false)}><X size={18} /></button>
              </header>

              {settingsSection === "agents" && (
                <div className="agent-center">
                  <section className="agent-center-hero">
                    <div>
                      <span>AGENT PACKS</span>
                      <strong>一个 NOVA，装载不同专业工作方式</strong>
                      <small>行业包声明角色、流程、证据规则与交付模板；模型、权限、预算和任务历史仍由 AgentOS 统一治理。</small>
                    </div>
                    <div className="agent-center-actions">
                      <b>{agentPacks.filter((pack) => pack.enabled).length} 个已启用</b>
                      <button
                        type="button"
                        className="primary"
                        onClick={() => {
                          if (agentWorkshopOpen) {
                            setAgentWorkshopOpen(false);
                            return;
                          }
                          openAgentFoundry();
                        }}
                      >
                        {agentWorkshopOpen ? <ChevronDown size={14} /> : <Sparkles size={14} />}
                        {agentWorkshopOpen ? "收起工坊" : "创建专属 Agent"}
                      </button>
                      <button
                        type="button"
                        onClick={async () => {
                          try {
                            const result = await window.nova.agentPacks.install();
                            if (result.canceled || !result.pack) return;
                            await loadAgentPacks(result.pack.id);
                            setNotice(`${result.pack.name} 已安全导入；检查结构后再启用即可。`);
                            await inspectAgentPack(result.pack.id);
                          } catch (error) {
                            setNotice(`导入失败：${error instanceof Error ? error.message : String(error)}`);
                          }
                        }}
                      >
                        导入 Agent Pack
                      </button>
                    </div>
                  </section>

                  {agentWorkshopOpen && (
                    <form className={`agent-workshop ${agentWorkshopSimpleMode ? "simple" : ""}`} onSubmit={orchestrateAgentPack}>
                      <header>
                        <div>
                          <span>NOVA AGENT CREATION STANDARD 1.0</span>
                          <strong>Agent 工坊</strong>
                          <small>统一可靠性与协作契约，同时保留行业、场景、自主程度和交付方式的差异。</small>
                        </div>
                        <b>生成后默认停用</b>
                      </header>

                      {agentWorkshopSimpleMode && (
                        <section className="agent-foundry-review">
                          <header>
                            <span><Check size={16} /></span>
                            <div>
                              <small>NOVA 已理解</small>
                              <strong>{agentWorkshopForm.name}</strong>
                              <p>{agentWorkshopForm.description}</p>
                            </div>
                            <button type="button" onClick={() => setAgentWorkshopSimpleMode(false)}>调整详细设置</button>
                          </header>
                          <div>
                            <article><small>要解决的问题</small><strong>{agentWorkshopForm.objective}</strong></article>
                            <article><small>主要交付</small><strong>{agentWorkshopForm.primaryArtifact}</strong></article>
                            <article><small>工作方式</small><strong>{agentWorkshopForm.collaborationMode === "specialist-team" ? "专业工作组" : agentWorkshopForm.collaborationMode === "coordinator" ? "主 Agent 协调" : "独立完成"}</strong></article>
                          </div>
                          <p>确认后，当前模型会完成真实的角色设计、工作流拆解和交叉审查；草案确认前不会创建任务或写入文件。</p>
                        </section>
                      )}

                      <section className="agent-template-picker">
                        <div className="agent-workshop-section-title">
                          <span>01</span>
                          <div><strong>选择工作场景</strong><small>模板只提供可靠的起点，不限制行业做法。</small></div>
                        </div>
                        <div>
                          {agentCreationTemplates.map((template) => (
                            <button
                              type="button"
                              key={template.id}
                              className={agentWorkshopForm.scenarioProfile === template.id ? "active" : ""}
                              onClick={() => {
                                updateAgentWorkshopField("scenarioProfile", template.id);
                                updateAgentWorkshopField("primaryArtifact", template.defaultArtifact);
                              }}
                            >
                              <strong>{template.name}</strong>
                              <small>{template.description}</small>
                            </button>
                          ))}
                        </div>
                      </section>

                      <section className="agent-workshop-fields">
                        <div className="agent-workshop-section-title">
                          <span>02</span>
                          <div><strong>定义行业与结果</strong><small>先定义最终要交付什么，再决定 Agent 如何工作。</small></div>
                        </div>
                        <div className="agent-workshop-grid">
                          <label><span>Agent 名称</span><input required value={agentWorkshopForm.name} onChange={(event) => updateAgentWorkshopField("name", event.target.value)} placeholder="例如：墨西哥新品机会侦察" /></label>
                          <label><span>Agent ID · 系统自动生成</span><input required readOnly value={agentWorkshopForm.id} title="每个新 Agent 都会获得独立且不可重复的系统 ID" /></label>
                          <label><span>行业分类</span><input required value={agentWorkshopForm.category} onChange={(event) => updateAgentWorkshopField("category", event.target.value)} placeholder="例如：跨境电商 / 医疗器械 / 制造业" /></label>
                          <label><span>主交付物</span><input required value={agentWorkshopForm.primaryArtifact} onChange={(event) => updateAgentWorkshopField("primaryArtifact", event.target.value)} placeholder="可检查的中文文件名.md" /></label>
                          <label className="wide"><span>最终目标</span><input required value={agentWorkshopForm.objective} onChange={(event) => updateAgentWorkshopField("objective", event.target.value)} placeholder="描述最终能被检查和验收的结果" /></label>
                          <label className="wide"><span>服务说明</span><textarea required minLength={10} value={agentWorkshopForm.description} onChange={(event) => updateAgentWorkshopField("description", event.target.value)} placeholder="说明服务对象、典型任务和不负责的边界" /></label>
                        </div>
                      </section>

                      <section className="agent-workshop-fields">
                        <div className="agent-workshop-section-title">
                          <span>03</span>
                          <div><strong>保留 Agent 的多样性</strong><small>这些参数决定它是助手、执行者、长期监控者还是协调者。</small></div>
                        </div>
                        <div className="agent-workshop-axis">
                          <label><span>自主程度</span><select value={agentWorkshopForm.autonomyLevel} onChange={(event) => updateAgentWorkshopField("autonomyLevel", event.target.value)}><option value="assist">辅助建议</option><option value="approval-execute">审批后执行</option><option value="goal-autonomous">目标自治</option></select></label>
                          <label><span>工作周期</span><select value={agentWorkshopForm.lifecycle} onChange={(event) => updateAgentWorkshopField("lifecycle", event.target.value)}><option value="single-run">单次任务</option><option value="project">项目持续</option><option value="continuous">长期监控</option><option value="scheduled">定时运行</option></select></label>
                          <label><span>协作方式</span><select value={agentWorkshopForm.collaborationMode} onChange={(event) => updateAgentWorkshopField("collaborationMode", event.target.value)}><option value="independent">独立完成</option><option value="specialist-team">专业工作组</option><option value="coordinator">主协调 Agent</option></select></label>
                          <label><span>交付形式</span><select value={agentWorkshopForm.deliveryMode} onChange={(event) => updateAgentWorkshopField("deliveryMode", event.target.value)}><option value="conversation">对话</option><option value="document">文档</option><option value="data">数据</option><option value="code">代码</option><option value="operation">软件操作</option><option value="mixed">混合交付</option></select></label>
                          <label><span>判断风格</span><select value={agentWorkshopForm.decisionStyle} onChange={(event) => updateAgentWorkshopField("decisionStyle", event.target.value)}><option value="conservative">保守</option><option value="balanced">平衡</option><option value="exploratory">探索</option><option value="creative">创新</option><option value="compliance-first">合规优先</option></select></label>
                        </div>
                      </section>

                      <section className="agent-workshop-fields">
                        <div className="agent-workshop-section-title">
                          <span>04</span>
                          <div><strong>NOVA 给出的启动建议</strong><small>根据前面三步自动总结，不需要你再填写；资料不足时 Agent 会保留未知项并降低结论级别。</small></div>
                        </div>
                        <div className="agent-workshop-recommendation">
                          <header>
                            <span><Sparkles size={14} /></span>
                            <div>
                              <strong>{agentRecommendationLoading ? "正在理解前面三步…" : "已形成 Agent 启动策略"}</strong>
                              <p>{agentWorkshopRecommendation?.summary || "完善上方的行业、目标和工作方式后，NOVA 会在这里给出用户资料建议。"}</p>
                            </div>
                            <b>自动生成</b>
                          </header>
                          {!!agentWorkshopRecommendation?.designSignals.length && (
                            <div className="agent-recommendation-signals">
                              {agentWorkshopRecommendation.designSignals.map((signal) => <span key={signal}>{signal}</span>)}
                            </div>
                          )}
                          <div className="agent-recommendation-columns">
                            <section>
                              <span>建议用户先准备</span>
                              <strong>形成可靠结果的核心资料</strong>
                              <ul>
                                {(agentWorkshopRecommendation?.requiredInputs || []).map((item) => <li key={item}>{item}</li>)}
                              </ul>
                            </section>
                            <section>
                              <span>有则更好</span>
                              <strong>提高证据质量的补充资料</strong>
                              <ul>
                                {(agentWorkshopRecommendation?.recommendedInputs || []).map((item) => <li key={item}>{item}</li>)}
                              </ul>
                            </section>
                          </div>
                          {!!agentWorkshopRecommendation?.starterPrompts.length && (
                            <section className="agent-recommendation-starters">
                              <span>Agent 创建后会主动提供这些起点</span>
                              <div>
                                {agentWorkshopRecommendation.starterPrompts.map((prompt) => <p key={prompt}>{prompt}</p>)}
                              </div>
                            </section>
                          )}
                        </div>
                      </section>

                      {(agentOrchestrating || agentDesignSession || agentOrchestrationEvents.length > 0 || agentOrchestrationDraft) && (
                        <section className="agent-workshop-orchestration">
                          <div className="agent-workshop-section-title">
                            <span>05</span>
                            <div>
                              <strong>智能体编排与交叉审查</strong>
                              <small>草案来自 Runtime 中的真实子 Agent 工作组；确认前不创建任务，也不会写入 Agent Pack。</small>
                            </div>
                            {agentDesignSession && (
                              <b>{agentDesignSession.status === "running"
                                ? "设计中"
                                : agentDesignSession.status === "completed"
                                  ? "等待确认"
                                  : agentDesignSession.status === "cancelled"
                                    ? "已停止"
                                    : agentDesignSession.status === "interrupted"
                                      ? "可重新编排"
                                      : "未完成"}</b>
                            )}
                          </div>
                          <div className="agent-orchestration-agents">
                            {agentOrchestrationEvents.map((event) => (
                              <article className={event.status} key={event.agent}>
                                <span>{event.status === "done" ? <Check size={13} /> : event.status === "failed" ? <X size={13} /> : <Sparkles size={13} />}</span>
                                <div>
                                  <strong>{event.agent}</strong>
                                  <small>{event.detail}</small>
                                  {event.output && <details><summary>查看本角色产出</summary><p>{event.output}</p></details>}
                                </div>
                                <b>{event.status === "done" ? "完成" : event.status === "failed" ? "未返回" : "编排中"}</b>
                              </article>
                            ))}
                          </div>
                          {agentOrchestrationDraft && (
                            <div className="agent-orchestration-draft">
                              <header>
                                <div><span>COUNCIL DRAFT</span><strong>{agentOrchestrationDraft.summary}</strong></div>
                                <b>
                                  {agentOrchestrationDraft.reviewVerdict === "approved" ? "模型审查通过" : "等待人工确认"}
                                  {" · "}{agentOrchestrationDraft.modelProvider} · {agentOrchestrationDraft.model}
                                </b>
                              </header>
                              <div className="agent-orchestration-layout">
                                <section>
                                  <span>角色编排</span>
                                  {agentOrchestrationDraft.roles.map((role) => (
                                    <article key={role.id}>
                                      <strong>{role.name}</strong>
                                      <small>{role.id}</small>
                                      <p>{role.responsibility}</p>
                                      <em>{role.deliverables.join(" · ")}</em>
                                    </article>
                                  ))}
                                </section>
                                <section>
                                  <span>主工作流 · {agentOrchestrationDraft.workflow.length} 个执行步骤</span>
                                  {agentOrchestrationDraft.workflow.map((step) => (
                                    <article key={`${step.order}-${step.title}`}>
                                      <b>{step.order}</b>
                                      <div>
                                        <strong>{step.title}</strong>
                                        <small>{step.owner} → {step.output}</small>
                                        <p>{step.acceptance.join("；")}</p>
                                      </div>
                                    </article>
                                  ))}
                                </section>
                              </div>
                              {!!agentOrchestrationDraft.risks.length && (
                                <footer><span>审查保留项</span><p>{agentOrchestrationDraft.risks.join(" · ")}</p></footer>
                              )}
                              <section className="agent-output-contract-confirm">
                                <span>统一输出格式 · nova.delivery/1.0</span>
                                <strong>结果、真实交付物、验证证据、未完成边界、下一步</strong>
                                <p>
                                  主交付物：{agentWorkshopForm.primaryArtifact}。运行时由 AgentOS 记录真实文件，
                                  用户可逐项审查、要求同任务修复，或把纠正沉淀为 Agent 校准规则。
                                </p>
                              </section>
                            </div>
                          )}
                        </section>
                      )}

                      {agentCreationResult && (
                        <section className="agent-certification">
                          <header>
                            <span><ShieldCheck size={17} /></span>
                            <div><strong>{agentCreationResult.certification.level}</strong><small>标准体检 {agentCreationResult.certification.score}/100 · Contract {agentCreationResult.certification.standardVersion}</small></div>
                          </header>
                          <div>
                            {agentCreationResult.certification.checks.map((check) => (
                              <span className={check.passed ? "passed" : "failed"} key={check.id}>
                                {check.passed ? <Check size={13} /> : <X size={13} />}{check.name}
                              </span>
                            ))}
                          </div>
                          <p>{agentCreationResult.certification.nextActions.join(" ")}</p>
                        </section>
                      )}

                      {agentBuildError && (
                        <div className="agent-launch-error">
                          <strong>构建任务未创建</strong>
                          <span>{agentBuildError}</span>
                          <small>编排草案仍然保留，可以修复连接后直接重试，不需要重新设计。</small>
                        </div>
                      )}

                      <footer>
                        <span>{agentOrchestrating
                          ? "真实子 Agent 正在 Agent 中心内分析和交叉审查；现在不会创建任务空间。"
                          : agentOrchestrationDraft
                            ? agentOrchestrationDraft.reviewVerdict === "approved"
                              ? "草案已通过模型审查。确认后将创建正式任务，构建 Agent Card、角色、工作流、契约、引导与评测。"
                              : "模型已形成完整草案并保留了风险项。请审阅；你的确认会作为人工批准，然后创建正式构建任务。"
                            : "将启动一条可恢复的设计会话，由现有 Runtime 组织三名只读子 Agent；草案确认后才创建正式构建任务。"}</span>
                        <div>
                          {agentOrchestrating && (
                            <button
                              type="button"
                              onClick={async () => {
                                const result = await window.nova.agentPacks.cancelOrchestration();
                                if (result.canceled) {
                                  setAgentOrchestrating(false);
                                  setAgentDesignSession((current) => current
                                    ? { ...current, status: "cancelled" }
                                    : current);
                                  setNotice("本次设计会话已停止；输入仍然保留，可以调整后重新编排。");
                                }
                              }}
                            >
                              停止编排
                            </button>
                          )}
                          {agentOrchestrationDraft && (
                            <button type="submit" disabled={agentOrchestrating || agentCreating}>重新编排</button>
                          )}
                          <button
                            type={agentOrchestrationDraft ? "button" : "submit"}
                            className="primary"
                            disabled={agentOrchestrating || agentCreating}
                            onClick={agentOrchestrationDraft ? () => void createAgentPack() : undefined}
                          >
                            <Sparkles size={15} />
                            {agentOrchestrating
                              ? "正在多 Agent 设计…"
                              : agentCreating
                                ? "正在生成与体检…"
                                : agentOrchestrationDraft
                                  ? "确认方案并构建 Agent Pack"
                                  : agentWorkshopSimpleMode
                                    ? "确认理解，开始设计"
                                    : "开始多 Agent 设计"}
                          </button>
                        </div>
                      </footer>
                    </form>
                  )}

                  {agentPacksLoading && <p className="loading-row">正在读取 Agent Pack 注册表…</p>}
                  {!agentPacksLoading && !agentPacks.length && (
                    <p className="empty-row">尚未发现 Agent Pack。可以按照 Agent Pack SDK 创建并放入扩展目录。</p>
                  )}

                  <div className="agent-pack-grid">
                    {agentPacks.map((pack) => (
                      <article
                        className={`${pack.enabled ? "enabled" : ""} ${selectedAgentPackId === pack.id ? "selected" : ""}`}
                        key={pack.id}
                      >
                        <header>
                          <span><Bot size={17} /></span>
                          <div>
                            <small>{pack.category} · v{pack.version}</small>
                            <strong>{pack.name}</strong>
                          </div>
                          <b>{pack.status}</b>
                        </header>
                        <p>{pack.description}</p>
                        <div className="agent-pack-facts">
                          <span title={pack.roleNames.join("、")}>
                            <strong>{pack.agentCount} 个角色</strong>
                            <small>{pack.roleNames.join("、") || "尚未声明角色"}</small>
                          </span>
                          <span title={pack.workflowNames.join("、")}>
                            <strong>{pack.workflowStepCount} 步工作流</strong>
                            <small>{pack.workflowNames.join("、") || "尚未声明工作流"}</small>
                          </span>
                          <span title={pack.capabilityNames.join("、")}>
                            <strong>{pack.capabilityNames.length} 项核心能力</strong>
                            <small>{pack.capabilityNames.join("、") || "使用 NOVA 基础能力"}</small>
                          </span>
                        </div>
                        <footer>
                          <button type="button" onClick={() => void inspectAgentPack(pack.id)}>查看结构</button>
                          <button
                            type="button"
                            className={pack.enabled ? "enabled" : ""}
                            onClick={async () => {
                              await window.nova.agentPacks.setEnabled({
                                id: pack.id,
                                enabled: !pack.enabled
                              });
                              if (pack.enabled && selectedAgentPackId === pack.id) {
                                setSelectedAgentPackId(null);
                              }
                              await loadAgentPacks();
                            }}
                          >
                            {pack.enabled ? "已启用" : "启用"}
                          </button>
                          {pack.enabled && (
                            <button
                              type="button"
                              className="primary"
                              onClick={() => {
                                void activateAgentPack(pack.id);
                              }}
                            >
                              {selectedAgentPackId === pack.id ? "使用中" : "使用此 Agent"}
                            </button>
                          )}
                          {!pack.builtIn && (
                            <button
                              type="button"
                              className="danger"
                              disabled={pack.enabled}
                              title={pack.enabled ? "请先停用此 Agent，再将其移除" : "从本机移除此 Agent Pack"}
                              onClick={async () => {
                                try {
                                  const result = await window.nova.agentPacks.remove({ id: pack.id });
                                  if (result.canceled || !result.removed) return;
                                  if (selectedAgentPackId === pack.id) setSelectedAgentPackId(null);
                                  if (inspectedAgentPack?.summary.id === pack.id) setInspectedAgentPack(null);
                                  await loadAgentPacks();
                                  setNotice(`“${pack.name}”已从本机 Agent 扩展坞移除。`);
                                } catch (error) {
                                  setNotice(`Agent 移除失败：${error instanceof Error ? error.message : String(error)}`);
                                }
                              }}
                            >
                              <Trash2 size={14} />
                              移除
                            </button>
                          )}
                        </footer>
                      </article>
                    ))}
                  </div>

                  {inspectedAgentPack && (
                    <section className="agent-pack-inspector">
                      <header>
                        <div>
                          <span>{inspectedAgentPack.summary.category}</span>
                          <strong>{inspectedAgentPack.summary.name}</strong>
                          <small>{inspectedAgentPack.summary.id} · {inspectedAgentPack.summary.version}</small>
                        </div>
                        <button type="button" onClick={() => setInspectedAgentPack(null)}><X size={15} /></button>
                      </header>
                      <div className="agent-pack-inspector-grid">
                        <section>
                          <strong>可直接开始</strong>
                          <div className="agent-starter-list">
                            {inspectedAgentPack.summary.starterPrompts.map((prompt) => (
                              <button
                                type="button"
                                key={prompt}
                                disabled={!inspectedAgentPack.summary.enabled}
                                onClick={() => {
                                  void activateAgentPack(inspectedAgentPack.summary.id, false);
                                  setDraft(prompt);
                                  setNotice("专业 Agent 已装载；这是一条快捷任务，也可以打开启动引导补充更明确的资料");
                                }}
                              >
                                {prompt}
                              </button>
                            ))}
                          </div>
                        </section>
                        <section>
                          <strong>工作流与角色</strong>
                          {inspectedAgentPack.workflows.map((workflow) => (
                            <details key={workflow.id} open>
                              <summary>{workflow.name}<span>{workflow.stepCount} 步</span></summary>
                              {workflow.steps.map((step) => (
                                <div className="agent-workflow-step" key={step.id}>
                                  <span>{step.agent}</span>
                                  <strong>{step.title}</strong>
                                  <small>{step.outputs.join(" · ") || "由工作流决定交付物"}</small>
                                </div>
                              ))}
                            </details>
                          ))}
                        </section>
                      </div>
                      {inspectedAgentPack.certification && (
                        <div className="agent-pack-certification-strip">
                          <span><ShieldCheck size={14} /></span>
                          <div>
                            <strong>{inspectedAgentPack.certification.level}</strong>
                            <small>NOVA Creation Standard {inspectedAgentPack.certification.standardVersion}</small>
                          </div>
                          <b>{inspectedAgentPack.certification.score}/100</b>
                          <em>{inspectedAgentPack.certification.checks.filter((check) => check.passed).length}/{inspectedAgentPack.certification.checks.length} 项通过</em>
                        </div>
                      )}
                      {agentCalibration && (
                        <section className="agent-calibration-ledger">
                          <header>
                            <div>
                              <span>校准版本账本</span>
                              <strong>{agentCalibration.activeCount} 条规则正在生效</strong>
                            </div>
                            <small>当前 v{agentCalibration.version}</small>
                          </header>
                          {agentCalibration.patches.length === 0 ? (
                            <p>还没有校准记录。审查交付物时，可以把一次纠正保存为这个 Agent 的长期工作规则。</p>
                          ) : (
                            <div className="agent-calibration-list">
                              {agentCalibration.patches.slice(0, 8).map((patch) => (
                                <article className={patch.state === "active" ? "active" : "rolled-back"} key={patch.id}>
                                  <div>
                                    <span>v{patch.version} · {calibrationScopeLabels[patch.scope]} · {calibrationCategoryLabels[patch.category]}</span>
                                    <strong>{patch.instruction}</strong>
                                    <small>{patch.scopeLabel} · 回归检查 {patch.regressionStatus === "pending" ? "待运行" : patch.regressionStatus}</small>
                                  </div>
                                  <button type="button" onClick={() => void rollbackAgentCalibration(patch)}>
                                    {patch.state === "active" ? "停用" : "重新启用"}
                                  </button>
                                </article>
                              ))}
                            </div>
                          )}
                        </section>
                      )}
                      <footer>
                        <span>权限声明：{inspectedAgentPack.permissions.length ? inspectedAgentPack.permissions.join("、") : "无额外权限"}</span>
                        <span>外部发布、投放、账号与购买仍需独立授权</span>
                      </footer>
                    </section>
                  )}
                </div>
              )}

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
                            ? "http://localhost:11434"
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
                          : connected[provider]
                            ? "已安全保存；留空可继续使用原凭据"
                          : `输入 ${providerLabels[provider]} API Key`
                      }
                    />
                  </label>
                  <div className="security-note">
                    <ShieldCheck size={17} />
                    <span>
                      API Key 由操作系统加密后保存在本机，不会回传到界面或写入日志；远程接口强制 HTTPS。
                    </span>
                  </div>
                  <div className="modal-actions">
                    <button
                      className="primary"
                      disabled={
                        !model.trim() ||
                        ((provider === "ollama" || provider === "custom")
                          ? !modelEndpoint.trim()
                          : !connected[provider] && apiKey.trim().length < 12)
                      }
                    >
                      验证并连接
                    </button>
                  </div>
                </form>
              )}

              {settingsSection === "mcp" && (
                <div className="mcp-connect-center">
                  <section className="mcp-quick-connect">
                    <header>
                      <div>
                        <span>开放接入</span>
                        <strong>把互联网上找到的 MCP 接进 NOVA</strong>
                        <small>粘贴实际 MCP 服务地址、`mcpServers` JSON 或 Codex TOML；仓库/文档链接本身不会被误当成服务。</small>
                      </div>
                      <button
                        type="button"
                        disabled={capabilityPreparing}
                        onClick={() => void scanLocalMcpConfigurations()}
                      >
                        扫描本机配置
                      </button>
                    </header>
                    <textarea
                      rows={5}
                      spellCheck={false}
                      value={mcpConfigText}
                      onChange={(event) => setMcpConfigText(event.target.value)}
                      placeholder={'例如：https://mcp.example.com/mcp\n或从项目 README 粘贴 { "mcpServers": { ... } }'}
                    />
                    <label>
                      <span>Authorization 环境变量名（可选）</span>
                      <input
                        value={mcpAuthorizationEnvironment}
                        onChange={(event) => setMcpAuthorizationEnvironment(event.target.value)}
                        placeholder="例如 MERCADOLIBRE_AUTHORIZATION"
                        spellCheck={false}
                      />
                      <small>环境变量的值可设为 `Bearer 你的令牌`；NOVA 只保存变量名，不保存令牌。</small>
                    </label>
                    <div className="mcp-quick-actions">
                      <button
                        type="button"
                        className="primary"
                        disabled={capabilityPreparing || !mcpConfigText.trim()}
                        onClick={() => void previewPastedMcpConfiguration()}
                      >
                        解析并审阅
                      </button>
                      <button
                        type="button"
                        onClick={() => {
                          setMcpConfigText(JSON.stringify({
                            mcpServers: {
                              "mercadolibre-official": {
                                url: "https://mcp.mercadolibre.com/mcp",
                                headers: { Authorization: "${MERCADOLIBRE_AUTHORIZATION}" }
                              }
                            }
                          }, null, 2));
                          setMcpAuthorizationEnvironment("MERCADOLIBRE_AUTHORIZATION");
                        }}
                      >
                        Mercado Libre 示例
                      </button>
                    </div>
                  </section>

                  {mcpDiscovery && (
                    <section className="mcp-discovery-review">
                      <header>
                        <div>
                          <strong>发现结果</strong>
                          <small>{mcpDiscovery.candidates.length} 个候选 · 默认不会启用</small>
                        </div>
                        <button type="button" onClick={() => setMcpDiscovery(null)}>清除</button>
                      </header>
                      {mcpDiscovery.candidates.map((candidate) => (
                        <label className={!candidate.canImport ? "blocked" : ""} key={candidate.id}>
                          <input
                            type="checkbox"
                            disabled={!candidate.canImport}
                            checked={selectedMcpCandidates.has(candidate.id)}
                            onChange={(event) => setSelectedMcpCandidates((current) => {
                              const next = new Set(current);
                              if (event.target.checked) next.add(candidate.id);
                              else next.delete(candidate.id);
                              return next;
                            })}
                          />
                          <span>
                            <strong>{candidate.name}</strong>
                            <small>{candidate.sourceProduct} · {candidate.summary}</small>
                            <em>{candidate.riskLabel} · {candidate.notes}</em>
                          </span>
                        </label>
                      ))}
                      {!!mcpDiscovery.warnings.length && (
                        <p>{mcpDiscovery.warnings.slice(0, 2).join("；")}</p>
                      )}
                      <button
                        type="button"
                        className="primary"
                        disabled={capabilityPreparing || selectedMcpCandidates.size === 0}
                        onClick={() => void importSelectedMcpCandidates()}
                      >
                        登记所选并保持停用
                      </button>
                    </section>
                  )}

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
                          await Promise.all([
                            loadCapabilities(),
                            refreshAgentCapabilities()
                          ]);
                        }}
                      >
                        {server.enabled ? "已启用" : "启用"}
                      </button>
                    </article>
                  ))}
                  </div>
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
                          await Promise.all([
                            loadCapabilities(),
                            refreshAgentCapabilities()
                          ]);
                        }}
                      >
                        {skill.enabled ? "已启用" : "启用"}
                      </button>
                    </article>
                  ))}
                </div>
              )}

              {settingsSection === "knowledge" && (
                <div className="knowledge-center">
                  <section className="knowledge-hero">
                    <div>
                      <span>LOCAL KNOWLEDGE</span>
                      <strong>让输入、资料与成果连接成思维网络</strong>
                      <small>
                        一个工作区就是一个个人或企业知识空间；实线来自真实证据，虚线映射等待你判断。
                      </small>
                    </div>
                    <div className="knowledge-hero-actions">
                      <div className="knowledge-scope-switch">
                        <button
                          type="button"
                          className={knowledgeScope === "workspace" ? "active" : ""}
                          disabled={!workspace}
                          onClick={() => {
                            setKnowledgeScope("workspace");
                            setSelectedKnowledgeNodeId(null);
                            void loadKnowledge("workspace");
                          }}
                        >当前空间</button>
                        <button
                          type="button"
                          className={knowledgeScope === "all" ? "active" : ""}
                          onClick={() => {
                            setKnowledgeScope("all");
                            setSelectedKnowledgeNodeId(null);
                            void loadKnowledge("all");
                          }}
                        >全部知识</button>
                      </div>
                      <button
                        type="button"
                        onClick={() => void window.nova.knowledge.openWindow({ workspace })}
                      >
                        <Maximize2 size={15} /> 打开知识地图
                      </button>
                      <button
                        type="button"
                        className="primary"
                        disabled={!workspace || knowledgeLoading}
                        onClick={() => void indexWorkspaceKnowledge()}
                      >
                        <RefreshCw size={15} className={knowledgeLoading ? "spinning" : ""} />
                        {knowledgeLoading ? "正在更新" : knowledgeState?.count ? "更新索引" : "建立索引"}
                      </button>
                    </div>
                  </section>

                  {!workspace && knowledgeScope === "workspace" ? (
                    <section className="knowledge-empty">
                      <BookOpen size={24} />
                      <strong>先选择一个工作区</strong>
                      <span>知识库会读取当前工程内的文档与代码，并建立可追溯的本地索引。</span>
                      <button type="button" onClick={chooseWorkspace}>选择工作区</button>
                    </section>
                  ) : (
                    <>
                      <div className="knowledge-metrics">
                        <article><span>已索引文件</span><strong>{knowledgeState?.count || 0}</strong></article>
                        <article><span>知识片段</span><strong>{knowledgeState?.chunks || 0}</strong></article>
                        <article><span>图谱节点</span><strong>{knowledgeState?.graph.nodeCount || 0}</strong></article>
                        <article><span>关联关系</span><strong>{knowledgeState?.graph.edgeCount || 0}</strong></article>
                      </div>

                      <form className="knowledge-search" onSubmit={searchKnowledge}>
                        <Search size={17} />
                        <input
                          value={knowledgeQuery}
                          onChange={(event) => setKnowledgeQuery(event.target.value)}
                          placeholder="搜索文件、事实、约束或历史结论"
                        />
                        <button type="submit" disabled={!knowledgeQuery.trim() || knowledgeLoading}>
                          检索
                        </button>
                      </form>

                      {knowledgeResults.length > 0 ? (
                        <section className="knowledge-section">
                          <header>
                            <div><strong>检索结果</strong><small>{knowledgeResults.length} 条可追溯片段</small></div>
                            <button type="button" onClick={() => setKnowledgeResults([])}>返回概览</button>
                          </header>
                          <div className="knowledge-results">
                            {knowledgeResults.map((result, index) => (
                              <article key={`${result.documentId}-${result.startLine}-${index}`}>
                                <div>
                                  <strong>{result.title}</strong>
                                  <span>{result.relativePath} · 第 {result.startLine} 行</span>
                                </div>
                                <em>{Math.round(result.score * 100)}%</em>
                                <p>{result.snippet}</p>
                              </article>
                            ))}
                          </div>
                        </section>
                      ) : (
                        <div className="knowledge-overview">
                          <section className="knowledge-section">
                            <header>
                              <div><strong>最近索引</strong><small>{knowledgeState?.documents.length || 0} 个文件可检索</small></div>
                              <span>
                                {knowledgeState?.count
                                  ? `更新于 ${new Date(knowledgeState.updatedAt).toLocaleString("zh-CN")}`
                                  : "尚未建立索引"}
                              </span>
                            </header>
                            {!knowledgeState?.documents.length ? (
                              <p className="empty-row">点击“建立索引”，NOVA 会扫描当前工作区的可读资料。</p>
                            ) : (
                              <div className="knowledge-documents">
                                {knowledgeState.documents.slice(0, 12).map((document) => (
                                  <article key={document.id}>
                                    <FileCode2 size={15} />
                                    <div><strong>{document.title}</strong><small>{document.relativePath}</small></div>
                                    <span>{document.chunkCount} 片段</span>
                                  </article>
                                ))}
                              </div>
                            )}
                          </section>

                          <section className="knowledge-section knowledge-graph-summary">
                            <header>
                              <div>
                                <strong>企业与个人知识网络</strong>
                                <small>
                                  {knowledgeState?.graph.inputCount || 0} 条用户输入 · {knowledgeState?.graph.inferredEdgeCount || 0} 条待确认映射
                                </small>
                              </div>
                            </header>
                            {!knowledgeState?.graph.nodes.length ? (
                              <p className="empty-row">开始一个任务或建立工作区索引后，这里会形成可追溯的思维网络。</p>
                            ) : (
                              <div className={`knowledge-network-browser ${selectedKnowledgeNode ? "has-selection" : ""}`}>
                                <div className="knowledge-network-toolbar">
                                  <Search size={14} />
                                  <input
                                    value={knowledgeGraphQuery}
                                    onChange={(event) => setKnowledgeGraphQuery(event.target.value)}
                                    placeholder="筛选概念、输入、任务或交付物"
                                  />
                                  <span><i />事实关系</span>
                                  <span className="inferred"><i />可能映射</span>
                                </div>
                                <KnowledgeNetwork
                                  nodes={visibleKnowledgeGraph.nodes}
                                  edges={visibleKnowledgeGraph.edges}
                                  selectedId={selectedKnowledgeNodeId}
                                  onSelect={setSelectedKnowledgeNodeId}
                                />
                                {selectedKnowledgeNode && (
                                  <aside className="knowledge-node-inspector">
                                    <header>
                                      <div>
                                        <span>{selectedKnowledgeNode.kind}</span>
                                        <strong>{selectedKnowledgeNode.label}</strong>
                                      </div>
                                      {selectedKnowledgeNode.isDeletable && (
                                        <button type="button" onClick={() => void deleteKnowledgeNode(selectedKnowledgeNode)}>
                                          <Trash2 size={13} /> 从图谱移除
                                        </button>
                                      )}
                                    </header>
                                    <p>{selectedKnowledgeNode.detail}</p>
                                    <small>
                                      来源：{selectedKnowledgeNode.sourceLabel || selectedKnowledgeNode.sourceType || "NOVA 本地知识"}
                                      · 更新于 {new Date(selectedKnowledgeNode.updatedAt).toLocaleString("zh-CN")}
                                    </small>
                                    <div className="knowledge-relation-list">
                                      {selectedKnowledgeRelations.length ? selectedKnowledgeRelations.map(({ edge, node }) => (
                                        <article key={`${edge.sourceId}-${edge.targetId}-${edge.relation}`}>
                                          <button className="knowledge-relation-target" type="button" onClick={() => setSelectedKnowledgeNodeId(node!.id)}>
                                            <span>{edge.isInferred ? "可能映射" : edge.relation}</span>
                                            <strong>{node!.label}</strong>
                                            <small>
                                              {edge.isInferred
                                                ? `${Math.round((edge.confidence || 0) * 100)}% · ${edge.evidence || "等待确认"}`
                                                : node!.kind}
                                            </small>
                                          </button>
                                          {edge.isInferred && edge.reviewState !== "rejected" && (
                                            <div className="knowledge-mapping-review">
                                              <button type="button" onClick={() => void reviewKnowledgeMapping(edge, true)}>
                                                <Check size={12} /> 确认关系
                                              </button>
                                              <button type="button" onClick={() => void reviewKnowledgeMapping(edge, false)}>
                                                <X size={12} /> 忽略
                                              </button>
                                            </div>
                                          )}
                                        </article>
                                      )) : <p className="empty-row">这个节点暂时没有可显示的关联。</p>}
                                    </div>
                                  </aside>
                                )}
                              </div>
                            )}
                          </section>
                        </div>
                      )}
                    </>
                  )}
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

                    {evolutionLab && (
                      <div className="evolution-discovery-status">
                        <div>
                          <strong>{evolutionLab.discoveryStatus}</strong>
                          <small>
                            最近扫描 {formatLocalDateTime(evolutionLab.lastDiscoveryAt)}
                          </small>
                        </div>
                        <span>
                          {evolutionLab.policy.scheduledDiscoveryEnabled
                            ? formatDiscoveryWindow(evolutionLab.nextDiscoveryAt)
                            : "自动发现未启用"}
                        </span>
                      </div>
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
                            {["ready", "failed"].includes(experiment.state) && experiment.isolatedWorkspace && (
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

              {settingsSection === "permissions" && (
                <div className="workspace-permissions-panel">
                  <section className="permission-boundary-card">
                    <ShieldCheck size={22} />
                    <div>
                      <strong>授权跟着工作区，不跟着整台电脑</strong>
                      <small>普通终端命令可以长期信任；提权、系统修改、批量删除和下载后执行仍会逐次询问。</small>
                    </div>
                    {!!workspacePermissions.length && (
                      <button
                        type="button"
                        onClick={async () => {
                          await window.nova.permissions.clear({});
                          await loadWorkspacePermissions();
                          setNotice("已撤销全部长期工作区授权");
                        }}
                      >
                        撤销全部
                      </button>
                    )}
                  </section>
                  <div className="workspace-permission-list">
                    {!workspacePermissions.length && (
                      <div className="empty-dock-state">
                        <ShieldCheck size={24} />
                        <strong>还没有长期授权</strong>
                        <span>终端请求出现时，你可以选择“以后在这个工作区内自动允许”。</span>
                      </div>
                    )}
                    {workspacePermissions.map((grant) => (
                      <article key={grant.id}>
                        <div>
                          <strong>{grant.workspaceLabel}</strong>
                          <code>{grant.permissionKey}</code>
                          <small>{grant.workspaceRoot} · {grant.platform === "macos" ? "macOS" : grant.platform === "windows" ? "Windows" : "Linux"}</small>
                        </div>
                        <button
                          type="button"
                          onClick={async () => {
                            await window.nova.permissions.revoke({ id: grant.id });
                            await loadWorkspacePermissions();
                            setNotice(`已撤销 ${grant.workspaceLabel} 的这项授权`);
                          }}
                        >
                          撤销
                        </button>
                      </article>
                    ))}
                  </div>
                </div>
              )}

              {settingsSection === "gateway" && (
                <div className="gateway-panel">
                  <section className="gateway-status-card">
                    <div className="gateway-status-heading">
                      <span className={extensionGateway?.running ? "online" : "offline"}>
                        <Webhook size={18} />
                      </span>
                      <div>
                        <strong>本机 Extension Gateway</strong>
                        <small>
                          {extensionGateway?.running
                            ? `正在 ${extensionGateway.host}:${extensionGateway.port} 提供服务`
                            : "当前未启动，不会接受任何外部连接"}
                        </small>
                      </div>
                      <b className={extensionGateway?.running ? "online" : "offline"}>
                        {extensionGateway?.running ? "运行中" : "已停止"}
                      </b>
                    </div>

                    <div className="gateway-boundary">
                      <ShieldCheck size={17} />
                      <div>
                        <strong>只监听 127.0.0.1</strong>
                        <span>当前版本只读、仅限本机；不开放局域网、远程命令或文件写入。</span>
                      </div>
                    </div>

                    <div className="gateway-address">
                      <label>
                        <span>接口地址</span>
                        <code>{extensionGateway?.baseUrl || "服务停止后无地址"}</code>
                      </label>
                      <button
                        type="button"
                        disabled={!extensionGateway?.running}
                        onClick={async () => {
                          await window.nova.extensions.copyGatewayUrl();
                          setNotice("服务接口地址已复制");
                        }}
                      >
                        <Copy size={15} /> 复制地址
                      </button>
                    </div>

                    <div className="gateway-actions">
                      <button
                        type="button"
                        className={extensionGateway?.running ? "danger-subtle" : "primary"}
                        disabled={gatewayBusy}
                        onClick={async () => {
                          setGatewayBusy(true);
                          try {
                            const next = await window.nova.extensions.setGatewayEnabled({
                              enabled: !extensionGateway?.running
                            });
                            setExtensionGateway(next);
                            setNotice(next.running ? "本机服务接口已启动" : "本机服务接口已停止");
                          } catch (error) {
                            setNotice(error instanceof Error ? error.message : "服务接口状态切换失败");
                          } finally {
                            setGatewayBusy(false);
                          }
                        }}
                      >
                        {extensionGateway?.running ? "停止服务" : "启动服务"}
                      </button>
                      <button
                        type="button"
                        disabled={!extensionGateway?.running || gatewayBusy}
                        onClick={async () => {
                          await window.nova.extensions.copyGatewayToken();
                          setNotice("访问令牌已复制；请只交给你信任的本机服务");
                        }}
                      >
                        <KeyRound size={15} /> 复制访问令牌
                      </button>
                      <button
                        type="button"
                        disabled={!extensionGateway?.running || gatewayBusy}
                        onClick={async () => {
                          setGatewayBusy(true);
                          try {
                            const next = await window.nova.extensions.rotateGatewayToken();
                            setExtensionGateway(next);
                            setNotice("访问令牌已轮换，旧连接已断开");
                          } finally {
                            setGatewayBusy(false);
                          }
                        }}
                      >
                        <RotateCw size={15} /> 轮换令牌
                      </button>
                    </div>
                  </section>

                  <section className="gateway-contract-card">
                    <header>
                      <div>
                        <strong>首批稳定契约</strong>
                        <small>网页看板、自动化工具和本机微服务可以从这里开始。</small>
                      </div>
                      <span>nova.hook/1.0</span>
                    </header>
                    <div className="gateway-hook-grid">
                      {(extensionGateway?.hooks || ["task.started", "artifact.created", "delivery.ready"]).map((hook) => (
                        <article key={hook}>
                          <code>{hook}</code>
                          <span>
                            {hook === "task.started" && "任务正式进入 AgentOS"}
                            {hook === "artifact.created" && "新的真实交付文件已产生"}
                            {hook === "delivery.ready" && "交付结果已形成并可审阅"}
                            {hook === "action.requested" && "本机扩展提交了待审阅任务目标"}
                          </span>
                        </article>
                      ))}
                    </div>
                    <details className="gateway-example">
                      <summary>查看接入示例</summary>
                      <pre>{`curl -H "Authorization: Bearer <TOKEN>" \\\n+  ${extensionGateway?.baseUrl || "http://127.0.0.1:<PORT>"}/v1/tasks\n\nEventSource:\n${extensionGateway?.eventsUrl || "http://127.0.0.1:<PORT>/v1/events"}?access_token=<TOKEN>`}</pre>
                    </details>
                  </section>

                  <section className="gateway-request-card">
                    <header>
                      <div>
                        <strong>外部任务请求箱</strong>
                        <small>这里只接收草稿，不会自动调用模型、消耗 Token 或修改文件。</small>
                      </div>
                      <span>{gatewayActionRequests.length} 条待审阅</span>
                    </header>
                    {!gatewayActionRequests.length && (
                      <div className="gateway-request-empty">
                        <MessageSquareText size={18} />
                        <span>当前没有外部任务请求</span>
                      </div>
                    )}
                    {!!gatewayActionRequests.length && (
                      <div className="gateway-request-list">
                        {gatewayActionRequests.map((request) => (
                          <article key={request.id}>
                            <div className="gateway-request-copy">
                              <span>{request.source} · {request.executionMode}</span>
                              <strong>{request.title}</strong>
                              <p>{request.prompt}</p>
                              <small>{new Date(request.createdAt).toLocaleString()}</small>
                            </div>
                            <div className="gateway-request-actions">
                              <button
                                type="button"
                                onClick={() => void resolveGatewayRequest(request, "rejected")}
                              >
                                拒绝
                              </button>
                              <button
                                type="button"
                                className="primary"
                                onClick={() => void resolveGatewayRequest(request, "accepted")}
                              >
                                带入新任务
                              </button>
                            </div>
                          </article>
                        ))}
                      </div>
                    )}
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

      {imagePreview && (
        <div
          className="image-preview-layer"
          onMouseDown={(event) => {
            if (event.currentTarget === event.target) setImagePreview(null);
          }}
        >
          <figure>
            <header>
              <strong>{imagePreview.name}</strong>
              <button type="button" onClick={() => setImagePreview(null)} aria-label="关闭图片预览">
                <X size={18} />
              </button>
            </header>
            <img src={imagePreview.dataUrl} alt={imagePreview.name} />
          </figure>
        </div>
      )}

      {newTaskGuideOpen && (
        <div
          className="modal-layer new-task-guide-layer"
          onMouseDown={(event) => {
            if (event.currentTarget === event.target) setNewTaskGuideOpen(false);
          }}
        >
          <section className="new-task-guide" role="dialog" aria-modal="true" aria-labelledby="new-task-guide-title">
            <header>
              <div className="new-task-guide-icon"><FolderOpen size={21} /></div>
              <div>
                <span>新建任务</span>
                <h2 id="new-task-guide-title">先选择成果保存的位置</h2>
                <p>这个文件夹既是 NOVA 的工作边界，也是报告、代码、图片和其他交付物的落盘位置。</p>
              </div>
              <button type="button" aria-label="关闭" onClick={() => setNewTaskGuideOpen(false)}>
                <X size={18} />
              </button>
            </header>

            <div className="new-task-guide-points">
              <div><span>1</span><p><strong>资料放在这里</strong><small>NOVA 会优先理解文件夹中的已有内容</small></p></div>
              <div><span>2</span><p><strong>操作限制在这里</strong><small>默认不会把修改写到其他位置</small></p></div>
              <div><span>3</span><p><strong>成果也保存在这里</strong><small>任务结束后可以直接打开和继续编辑</small></p></div>
            </div>

            {!!recentTaskWorkspaces.length && (
              <div className="recent-workspace-options">
                <span>最近使用</span>
                {recentTaskWorkspaces.map((path) => (
                  <button
                    type="button"
                    key={path}
                    onClick={() => void createTaskWithWorkspace(false, path)}
                  >
                    <FolderOpen size={16} />
                    <span>
                      <strong>{path.split(/[\\/]/).filter(Boolean).at(-1) || path}</strong>
                      <small>{path}</small>
                    </span>
                    <ChevronRight size={16} />
                  </button>
                ))}
              </div>
            )}

            <footer>
              <button type="button" onClick={() => setNewTaskGuideOpen(false)}>稍后再说</button>
              {workspace && (
                <button type="button" onClick={() => void createTaskWithWorkspace(true)}>
                  沿用当前目录
                </button>
              )}
              <button type="button" className="primary" onClick={() => void createTaskWithWorkspace()}>
                <FolderOpen size={17} /> 选择保存目录
              </button>
            </footer>
          </section>
        </div>
      )}

      {approvalOpen && pendingSubmission && (
        <div className="modal-layer approval-layer">
          <div className="approval-modal">
            <header>
              <div className="approval-icon"><ShieldCheck size={20} /></div>
              <div>
                <span>本任务权限</span>
                <h2>选择 NOVA 的工作边界</h2>
              </div>
            </header>
            <div className="approval-summary">
              <strong>{pendingSubmission.content}</strong>
              <span>{workspace}</span>
            </div>
            {crossModelReview && (
              <div className="cross-review-approval">
                <ShieldCheck size={18} />
                <div>
                  <strong>本轮启用双模型独立复核</strong>
                  <span>
                    主执行使用 {providerLabels[provider]}，完成后会额外调用{" "}
                    {reviewCandidates.length
                      ? providerLabels[reviewCandidates[0]]
                      : "另一个模型"}
                    进行只读审查。它会收到目标、主模型答复和必要的工作区证据，
                    不具备写入权限；最多额外请求 3 轮，每次输出预算上限 12,000 Token。
                  </span>
                </div>
              </div>
            )}
            {executionMode !== "Ask" && executionMode !== "Plan" && (
              <button
                className="approval-option recommended"
                type="button"
                onClick={() => void executeSubmission("workspace")}
              >
                <span>
                  <strong>
                    {executionMode === "Autopilot"
                      ? "工作区智能审核并启动工作组（推荐）"
                      : "工作区智能审核（推荐）"}
                  </strong>
                  <small>
                    {executionMode === "Autopilot"
                      ? "自动处理当前文件夹内的低风险修改、构建测试与子 Agent 协作；遇到新边界、桌面控制或外部副作用时再单独问你。"
                      : "自动处理当前文件夹内的低风险修改、构建测试和公开资料读取；遇到新边界或高风险动作时再单独问你。"}
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
                    在工作区能力之外，还可切换窗口、点击和输入。发送、发布、删除等有外部影响的动作仍会再次确认。
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
              <span><strong>仅分析，不修改</strong><small>只读取资料并给出结论，不写入文件，也不操作桌面。</small></span>
            </button>
            <label className="approval-remember">
              <input
                type="checkbox"
                checked={rememberTaskPermission}
                onChange={(event) => setRememberTaskPermission(event.target.checked)}
              />
              <span>
                <strong>本任务后续对话沿用此选择</strong>
                <small>不会每轮重复询问；遇到新的权限边界仍会单独确认。</small>
              </span>
            </label>
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

      {pendingToolApproval && (
        <div className="modal-layer tool-approval-layer">
          <div className="tool-approval-modal" role="dialog" aria-modal="true" aria-labelledby="tool-approval-title">
            <header>
              <div className={`approval-scope ${pendingToolApproval.scope}`}>
                <ShieldCheck size={20} />
              </div>
              <div>
                <span>
                  {pendingToolApproval.scope === "workspace"
                    ? "工作区新权限"
                    : pendingToolApproval.scope === "desktop"
                      ? "桌面操作确认"
                      : "外部操作确认"}
                </span>
                <h2 id="tool-approval-title">{pendingToolApproval.title}</h2>
              </div>
            </header>
            <p>{pendingToolApproval.description}</p>
            <div className="tool-approval-target">
              <span>请求能力</span>
              <strong>{pendingToolApproval.toolName}</strong>
            </div>
            {pendingToolApproval.preview && (
              <details className="tool-approval-details">
                <summary>查看执行详情</summary>
                <pre>{pendingToolApproval.preview}</pre>
              </details>
            )}
            {pendingToolApproval.canRememberForTask !== false && (
              <label className="approval-remember">
                <input
                  type="checkbox"
                  checked={rememberToolPermission}
                  onChange={(event) => {
                    setRememberToolPermission(event.target.checked);
                    if (event.target.checked) setRememberWorkspacePermission(false);
                  }}
                />
                <span>
                  <strong>本任务内相同能力不再询问</strong>
                  <small>只记住这一项能力，不会扩大到其他工具、任务或工作区。</small>
                </span>
              </label>
            )}
            {pendingToolApproval.canPersistForWorkspace && !pendingToolApproval.requiresExplicitApproval && (
              <label className="approval-remember workspace-trust">
                <input
                  type="checkbox"
                  checked={rememberWorkspacePermission}
                  onChange={(event) => {
                    setRememberWorkspacePermission(event.target.checked);
                    if (event.target.checked) setRememberToolPermission(false);
                  }}
                />
                <span>
                  <strong>以后在这个工作区内自动允许</strong>
                  <small>只记住当前文件夹和这类能力；高风险命令仍会再次询问。</small>
                </span>
              </label>
            )}
            {pendingToolApproval.requiresExplicitApproval && (
              <div className="approval-risk-warning">
                <strong>这一步必须单独确认</strong>
                <span>涉及提权、系统位置、批量删除或下载后执行，不能加入长期授权。</span>
              </div>
            )}
            <div className="tool-approval-actions">
              <button type="button" onClick={() => void resolveToolApproval(false)}>拒绝这一步</button>
              <button type="button" className="primary" onClick={() => void resolveToolApproval(true)}>
                允许并继续
              </button>
            </div>
          </div>
        </div>
      )}

      {deliveryReview && (
        <div
          className="modal-layer delivery-review-layer"
          onMouseDown={(event) => {
            if (event.currentTarget === event.target) setDeliveryReview(null);
          }}
        >
          <div className="delivery-review-modal">
            <header>
              <div>
                <span>查看交付物</span>
                <h2>{deliveryReview.title}</h2>
                {deliveryReview.path && <small>{deliveryReview.path}</small>}
              </div>
              <button type="button" onClick={() => setDeliveryReview(null)}>
                <X size={18} />
              </button>
            </header>
            {deliveryReview.truncated && (
              <div className="review-truncated">文件较大，当前显示前 600 KB 内容。</div>
            )}
            <div className={`delivery-review-content ${deliveryReview.kind}`}>
              {deliveryReview.kind === "markdown"
                ? <MarkdownContent content={deliveryReview.content} />
                : deliveryReview.kind === "image"
                  ? <img src={deliveryReview.content} alt={deliveryReview.title} />
                  : <pre>{deliveryReview.content}</pre>}
            </div>
            <aside className="delivery-review-panel">
              {deliveryReview.path && (
                <div className="delivery-file-actions">
                  <button type="button" onClick={() => void window.nova.system.openDeliveryArtifact({ path: deliveryReview.path!, workspace })}>
                    打开原文件
                  </button>
                  <button type="button" onClick={() => void window.nova.system.revealDeliveryArtifact({ path: deliveryReview.path!, workspace })}>
                    在文件夹中定位
                  </button>
                </div>
              )}
              {selectedAgentPack && (
                <div className="delivery-review-mode">
                  <button
                    type="button"
                    className={deliveryReviewMode === "rework" ? "active" : ""}
                    onClick={() => setDeliveryReviewMode("rework")}
                  >
                    修改这次结果
                  </button>
                  <button
                    type="button"
                    className={deliveryReviewMode === "calibrate" ? "active" : ""}
                    onClick={() => setDeliveryReviewMode("calibrate")}
                  >
                    改进这个 Agent
                  </button>
                </div>
              )}
              {deliveryReviewMode === "calibrate" && selectedAgentPack && (
                <div className="delivery-calibration-controls">
                  <label>
                    <span>哪里不合适</span>
                    <select
                      value={calibrationCategory}
                      onChange={(event) => setCalibrationCategory(event.target.value as AgentCalibrationPatch["category"])}
                    >
                      {Object.entries(calibrationCategoryLabels).map(([value, label]) => (
                        <option value={value} key={value}>{label}</option>
                      ))}
                    </select>
                  </label>
                  <label>
                    <span>生效范围</span>
                    <select
                      value={calibrationScope}
                      onChange={(event) => setCalibrationScope(event.target.value as AgentCalibrationPatch["scope"])}
                    >
                      {Object.entries(calibrationScopeLabels).map(([value, label]) => (
                        <option
                          value={value}
                          key={value}
                          disabled={(value === "turn" && !selectedTaskId) || (value === "project" && !workspace)}
                        >
                          {label}
                        </option>
                      ))}
                    </select>
                  </label>
                </div>
              )}
              <label>
                <span>{deliveryReviewMode === "calibrate" ? "以后希望它怎样做" : "你希望怎么改"}</span>
                <textarea
                  value={deliveryReviewNote}
                  onChange={(event) => setDeliveryReviewNote(event.target.value)}
                  placeholder={deliveryReviewMode === "calibrate"
                    ? "例如：选品判断不能只看利润，必须同时评价真实需求、竞争密度、内容传播性和合规风险。"
                    : "例如：结论不够明确、表格缺少价格列、请补充数据来源……"}
                />
              </label>
              <div>
                <button
                  type="button"
                  onClick={() => void acceptCurrentDelivery()}
                >
                  通过并保留
                </button>
                <button
                  type="button"
                  className="primary"
                  onClick={() => deliveryReviewMode === "calibrate"
                    ? void saveAgentCalibration()
                    : void submitDeliveryRework()}
                >
                  {deliveryReviewMode === "calibrate" ? "保存校准并继续" : "提交修改意见"}
                </button>
              </div>
            </aside>
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

      {pendingDeleteTask && (
        <div className="modal-layer archive-confirm-layer">
          <div className="archive-confirm-modal danger-confirm-modal">
            <div className="archive-confirm-icon"><X size={21} /></div>
            <div>
              <span>永久删除归档记录</span>
              <h2>删除“{pendingDeleteTask.title || "未命名任务"}”？</h2>
              <p>任务对话、任务索引和行动日志将永久删除且无法恢复；用户工作区文件与已经生成的交付物不会被删除。</p>
            </div>
            <div className="archive-confirm-actions">
              <button type="button" onClick={() => setPendingDeleteTask(null)}>取消</button>
              <button
                type="button"
                className="danger"
                onClick={() => void deleteArchivedTask(pendingDeleteTask)}
              >
                确认永久删除
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
                  <button
                    type="button"
                    className="archive-delete-action"
                    onClick={() => setPendingDeleteTask(task)}
                  >
                    <X size={15} />
                    删除记录
                  </button>
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
                ? "确认后会登记并启用此 MCP。涉及账号登录、外部写入或高风险动作时，NOVA 仍会再次请求授权。"
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

function App() {
  const view = new URLSearchParams(window.location.search).get("view");
  return view === "knowledge" ? <KnowledgeExplorerWindow /> : <MainApp />;
}

export default App;
