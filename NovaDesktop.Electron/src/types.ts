export type Provider = "deepseek" | "openai" | "kimi" | "ollama" | "custom";
export type ExecutionMode = "Ask" | "Plan" | "Build" | "Autopilot" | "Goal";

export interface Attachment {
  id: string;
  name: string;
  path: string;
  size: number;
  kind: "image" | "text";
}

export interface Message {
  id: string;
  role: "user" | "assistant";
  content: string;
  createdAt: string;
  attachments?: Attachment[];
  verification?: CrossModelVerification | null;
  delivery?: DeliveryProof | null;
}

export interface CrossModelVerification {
  provider: string;
  model: string;
  verdict: "PASS" | "CONCERNS" | "FAIL" | "SKIPPED" | "UNAVAILABLE";
  confidence: number;
  summary: string;
  details?: string;
  completedAt?: string;
}

export interface DeliveryProof {
  status: "PROVEN" | "EVIDENCED" | "READY" | "PARTIAL";
  summary: string;
  requiresWorkspaceMutation: boolean;
  hasWorkspaceChanges: boolean;
  validationRuns: number;
}

export interface AgentTask {
  id: string;
  title: string;
  status?: string;
  state?: string;
  updatedAt?: string;
  createdAt?: string;
  summary?: string;
  progress?: number;
  workspaceRoot?: string;
  provider?: string;
  model?: string;
  executionMode?: string;
  hasResult?: boolean;
}

export interface CapabilityState {
  mcp: Array<{
    name: string;
    transport: string;
    enabled: boolean;
    command?: string;
    url?: string;
  }>;
  skills: Array<{
    id: string;
    name: string;
    description: string;
    enabled: boolean;
    fileCount: number;
    sizeBytes: number;
  }>;
  marketplace: Array<{
    id: string;
    kind: "mcp" | "skill";
    category: string;
    name: string;
    publisher: string;
    description: string;
    trustLabel: string;
    riskLabel: string;
    permissionSummary: string;
    requirements: string;
    isInstalled: boolean;
    isEnabled: boolean;
    stateLabel: string;
    actionLabel: string;
  }>;
  enabledSchedules: number;
}

export interface AgentEvent {
  taskId: string;
  kind: string;
  agent: string;
  action: string;
  detail: string;
  progress: number;
  activeUnits: number;
}

export interface StoreCapabilityItem {
  id: string;
  kind: "mcp" | "skill";
  source: string;
  sourceLabel: string;
  name: string;
  publisher: string;
  description: string;
  trustLabel: string;
  permissionSummary: string;
  requirements: string;
  sourceUrl: string;
  installable: boolean;
  actionLabel: string;
}

export interface CapabilityStoreResult {
  sources: Array<{
    id: string;
    kind: "mcp" | "skill";
    name: string;
    publisher: string;
    description: string;
    trust: string;
    endpoint: string;
  }>;
  items: StoreCapabilityItem[];
}

export interface WorkingHabitCandidate {
  id: string;
  category: string;
  statement: string;
  evidenceCount: number;
  confidence: number;
  state: "proposed" | "accepted" | "rejected";
  updatedAt: string;
}

export interface DistilledSkillCandidate {
  id: string;
  name: string;
  description: string;
  instructions: string;
  habitIds: string[];
  installed: boolean;
  createdAt: string;
}

export interface LivingMemoryState {
  habits: WorkingHabitCandidate[];
  skillCandidates: DistilledSkillCandidate[];
  lastAnalyzedAt?: string | null;
  tasksAnalyzed: number;
}

export interface EvolutionExperiment {
  id: string;
  objective: string;
  hypothesis: string;
  sourceWorkspace: string;
  isolatedWorkspace?: string | null;
  state:
    | "proposed"
    | "ready"
    | "running"
    | "evaluating"
    | "passed"
    | "failed"
    | "adopted"
    | "rejected";
  isolationMode: string;
  agentPrompt: string;
  changedFiles: Array<{
    path: string;
    kind: "added" | "modified" | "deleted";
    sizeBytes: number;
  }>;
  verificationCommand: string;
  verificationPassed?: boolean | null;
  evidence: string;
  blockers: string[];
  tokenBudget: number;
  reservedTokens: number;
  createdAt: string;
  updatedAt: string;
  adoptedAt?: string | null;
}

export interface EvolutionLabState {
  policy: {
    enabled: boolean;
    scheduledDiscoveryEnabled: boolean;
    maxTokensPerExperiment: number;
    monthlyTokenBudget: number;
    maxExperimentsPerWeek: number;
    maxModelRounds: number;
    updatedAt: string;
  };
  experiments: EvolutionExperiment[];
  activeExperiments: number;
  passedExperiments: number;
  adoptedExperiments: number;
  usedTokensThisMonth: number;
  remainingTokensThisMonth: number;
  usageMonth: string;
  lastDiscoveryAt?: string | null;
  nextDiscoveryAt?: string | null;
  discoveryStatus: string;
  lastDiscoveryCandidateId?: string | null;
}

export interface EvolutionDiscoveryEvent {
  kind: "scan" | "candidate" | "error";
  candidateId?: string | null;
  objective?: string | null;
  discoveryStatus: string;
  lastDiscoveryAt?: string | null;
  nextDiscoveryAt?: string | null;
}

export interface DesktopSnapshot {
  count: number;
  windows: Array<{
    windowId: string;
    title: string;
    processName: string;
    processId: number;
    inputProtected: boolean;
    bounds: { left: number; top: number; width: number; height: number };
  }>;
}

export interface BootInfo {
  appVersion: string;
  platform: string;
  kernel: {
    version?: string;
    kernelVersion?: string;
    bootId?: string;
    services?: Array<{ name: string; status: string }>;
    servicesReady?: number;
    servicesTotal?: number;
  };
  defaults: Record<Provider, { model: string; endpoint: string }>;
}

export interface NovaApi {
  system: {
    boot(): Promise<BootInfo>;
    listTasks(): Promise<AgentTask[] | { tasks: AgentTask[] }>;
    listArchivedTasks(): Promise<AgentTask[] | { tasks: AgentTask[] }>;
    getTask(request: {
      taskId: string;
    }): Promise<{ task: AgentTask; messages: Message[] }>;
    archiveTask(request: { taskId: string }): Promise<{ archived: boolean }>;
    restoreTask(request: { taskId: string }): Promise<{ archived: boolean }>;
    selectWorkspace(): Promise<string | null>;
    selectAttachments(): Promise<Attachment[]>;
    desktopSnapshot(): Promise<DesktopSnapshot>;
  };
  model: {
    configure(configuration: {
      provider: Provider;
      model: string;
      apiKey: string;
      endpoint?: string;
    }): Promise<{
      provider: Provider;
      connected: boolean;
      model: string;
      endpoint: string;
      discoveredModels: string[];
    }>;
    run(request: {
      provider: Provider;
      model: string;
      workspace: string | null;
      messages: Pick<Message, "role" | "content">[];
      attachments: Attachment[];
      taskId?: string | null;
      runId: string;
      approvalMode: "workspace" | "workspaceDesktop" | "readOnly";
      executionMode: ExecutionMode;
      crossModelReview?: boolean;
    }): Promise<{
      taskId: string;
      output: string;
      toolCalls: number;
      mutatingToolCalls: number;
      verification?: CrossModelVerification | null;
      delivery?: DeliveryProof | null;
    }>;
    cancel(request: { runId: string }): Promise<{ cancelled: boolean }>;
    onEvent(listener: (event: AgentEvent) => void): () => void;
  };
  capabilities: {
    list(request: { workspace: string | null }): Promise<CapabilityState>;
    setMcpEnabled(request: { name: string; enabled: boolean }): Promise<unknown>;
    setSkillEnabled(request: { id: string; enabled: boolean }): Promise<unknown>;
    install(request: { id: string; workspace: string | null }): Promise<unknown>;
    searchStore(request: {
      kind: "all" | "mcp" | "skill";
      query: string;
    }): Promise<CapabilityStoreResult>;
    installStore(request: { id: string }): Promise<unknown>;
  };
  extensions: {
    listProfiles(): Promise<{ ssh: unknown[]; cloud: unknown[] }>;
    saveSshProfile(request: Record<string, FormDataEntryValue>): Promise<unknown>;
    testSshProfile(request: Record<string, FormDataEntryValue>): Promise<{ reachable: boolean }>;
    saveCloudAdapter(request: Record<string, FormDataEntryValue>): Promise<unknown>;
  };
  growth: {
    getState(): Promise<LivingMemoryState>;
    analyze(): Promise<LivingMemoryState>;
    setHabitState(request: {
      id: string;
      state: WorkingHabitCandidate["state"];
    }): Promise<LivingMemoryState>;
    distillSkill(): Promise<LivingMemoryState>;
    installSkill(request: { id: string }): Promise<LivingMemoryState>;
    getEvolutionLab(): Promise<EvolutionLabState>;
    configureEvolutionLab(request: {
      enabled: boolean;
      scheduledDiscoveryEnabled: boolean;
      maxTokensPerExperiment: number;
      monthlyTokenBudget: number;
      maxExperimentsPerWeek: number;
      maxModelRounds: number;
    }): Promise<EvolutionLabState>;
    proposeEvolution(request: {
      workspaceRoot: string;
      objective: string;
    }): Promise<EvolutionLabState>;
    prepareEvolution(request: { id: string }): Promise<EvolutionLabState>;
    evaluateEvolution(request: { id: string }): Promise<EvolutionLabState>;
    adoptEvolution(request: { id: string }): Promise<EvolutionLabState>;
    rejectEvolution(request: { id: string }): Promise<EvolutionLabState>;
    onEvolutionEvent(listener: (event: EvolutionDiscoveryEvent) => void): () => void;
  };
  window: {
    minimize(): Promise<void>;
    toggleMaximize(): Promise<boolean>;
    close(): Promise<void>;
  };
}

declare global {
  interface Window {
    nova: NovaApi;
  }
}
