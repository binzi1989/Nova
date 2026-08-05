export type Provider = "deepseek" | "openai" | "kimi" | "ollama" | "custom";
export type ExecutionMode = "Ask" | "Plan" | "Build" | "Autopilot" | "Goal";

export interface Attachment {
  id: string;
  name: string;
  path: string;
  size: number;
  kind: "image" | "text" | "document";
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

export interface DeliveryArtifactPreview {
  path: string;
  name: string;
  size: number;
  truncated: boolean;
  kind: "markdown" | "text";
  language: string;
  content: string;
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
  agentPackId?: string | null;
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
  packId?: string | null;
}

export interface AgentPackSummary {
  id: string;
  name: string;
  version: string;
  status: string;
  category: string;
  description: string;
  enabled: boolean;
  builtIn: boolean;
  declaredCapabilities: string[];
  starterPrompts: string[];
  agentCount: number;
  workflowCount: number;
}

export interface AgentCreationTemplate {
  id: string;
  name: string;
  description: string;
  defaultArtifact: string;
  defaultSteps: string[];
  evidenceRules: string[];
}

export interface AgentPackCreationRequest {
  id: string;
  name: string;
  category: string;
  description: string;
  objective: string;
  scenarioProfile: string;
  autonomyLevel: string;
  lifecycle: string;
  collaborationMode: string;
  deliveryMode: string;
  decisionStyle: string;
  primaryArtifact: string;
  requiredInputs: string[];
  recommendedInputs: string[];
  starterPrompts: string[];
  orchestration?: AgentWorkshopOrchestrationDraft | null;
}

export interface AgentPackCertificationReport {
  standardVersion: string;
  level: "Draft" | "Runnable" | "Verified" | "Production";
  score: number;
  checks: Array<{ id: string; name: string; passed: boolean; detail: string }>;
  nextActions: string[];
}

export interface AgentPackCreationResult {
  pack: AgentPackSummary;
  certification: AgentPackCertificationReport;
}

export interface AgentWorkshopRecommendation {
  summary: string;
  requiredInputs: string[];
  recommendedInputs: string[];
  starterPrompts: string[];
  designSignals: string[];
}

export interface AgentWorkshopRoleDraft {
  id: string;
  name: string;
  responsibility: string;
  deliverables: string[];
}

export interface AgentWorkshopStepDraft {
  order: number;
  title: string;
  owner: string;
  output: string;
  acceptance: string[];
}

export interface AgentWorkshopOrchestrationDraft {
  summary: string;
  designRationale: string[];
  roles: AgentWorkshopRoleDraft[];
  workflow: AgentWorkshopStepDraft[];
  requiredInputs: string[];
  recommendedInputs: string[];
  starterPrompts: string[];
  risks: string[];
  reviewVerdict: "approved" | "revise";
  modelProvider: string;
  model: string;
}

export interface AgentWorkshopOrchestrationEvent {
  sessionId: string;
  agent: string;
  status: "running" | "done" | "failed";
  detail: string;
  output: string;
  at: string;
}

export interface AgentWorkshopReadyEvent {
  sessionId: string;
  draft?: AgentWorkshopOrchestrationDraft;
  error?: string;
}

export interface AgentWorkshopDesignSession {
  id: string;
  status: "running" | "completed" | "failed" | "cancelled" | "interrupted";
  name: string;
  provider: Provider;
  model: string;
  request: AgentPackCreationRequest & { provider?: Provider; model?: string };
  events: AgentWorkshopOrchestrationEvent[];
  draft: AgentWorkshopOrchestrationDraft | null;
  error: string;
  createdAt: string;
  updatedAt: string;
}

export interface AgentCalibrationPatch {
  id: string;
  packId: string;
  version: number;
  scope: "turn" | "project" | "agent" | "organization";
  scopeKey: string;
  scopeLabel: string;
  category: "fact" | "judgment" | "workflow" | "format" | "evidence" | "permission" | "tone" | "other";
  instruction: string;
  sourceTaskId: string | null;
  sourceTitle: string | null;
  sourcePath: string | null;
  state: "active" | "rolled-back";
  regressionStatus: "pending" | "passed" | "failed";
  createdAt: string;
  updatedAt: string;
}

export interface AgentCalibrationSnapshot {
  packId: string;
  version: number;
  activeCount: number;
  patches: AgentCalibrationPatch[];
}

export interface AgentPackDetails {
  summary: AgentPackSummary;
  charter: string;
  agentRoster: string;
  workflows: Array<{
    id: string;
    name: string;
    executionMode: string;
    stepCount: number;
    steps: Array<{
      id: string;
      agent: string;
      title: string;
      outputs: string[];
      acceptance: string[];
    }>;
  }>;
  deliveryTemplate: string;
  permissions: string[];
  externalActions: Record<string, string>;
  onboarding: AgentPackOnboarding | null;
  capabilityRequirements: AgentPackCapabilityRequirements | null;
  certification: AgentPackCertificationReport | null;
}

export interface AgentPackCapabilityRequirements {
  version: string;
  items: Array<{
    id: string;
    kind: "mcp" | "skill";
    name: string;
    reason: string;
    required: boolean;
    matchIds: string[];
    catalogId: string | null;
  }>;
}

export interface AgentPackCapabilityReport {
  packId: string;
  version: string;
  ready: boolean;
  readyCount: number;
  requiredCount: number;
  requiredReadyCount: number;
  items: Array<{
    id: string;
    kind: "mcp" | "skill";
    name: string;
    reason: string;
    required: boolean;
    matchIds: string[];
    catalogId: string | null;
    state: "ready" | "registered-disabled" | "available" | "missing";
    matchedId: string | null;
    matchedName: string | null;
    catalogName: string | null;
    action: "none" | "enable" | "load" | "scan" | "store";
  }>;
}

export interface McpDiscoveryCandidate {
  id: string;
  name: string;
  sourceProduct: string;
  sourcePath: string;
  isCompatible: boolean;
  isAlreadyRegistered: boolean;
  canImport: boolean;
  mayAcquireSoftware: boolean;
  omittedSecretCount: number;
  riskLabel: string;
  summary: string;
  notes: string;
}

export interface McpDiscoveryResult {
  canceled: boolean;
  candidates: McpDiscoveryCandidate[];
  scannedPaths: string[];
  warnings: string[];
}

export interface AgentPackOnboarding {
  version: string;
  headline: string;
  description: string;
  steps: Array<{
    id: string;
    title: string;
    description: string;
    kind: "text" | "select" | "attachment";
    required: boolean;
    placeholder: string;
    options: string[];
    whyItMatters: string;
    example: string;
  }>;
  outcomes: Array<{
    id: string;
    title: string;
    description: string;
    promptTemplate: string;
  }>;
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

export interface KnowledgeDocument {
  id: string;
  workspaceRoot: string;
  relativePath: string;
  title: string;
  extension: string;
  sizeBytes: number;
  chunkCount: number;
  indexedAt: string;
}

export interface KnowledgeState {
  workspaceRoot: string | null;
  indexPath: string;
  updatedAt: string;
  count: number;
  chunks: number;
  bytes: number;
  documents: KnowledgeDocument[];
  graph: {
    graphPath: string;
    updatedAt: string;
    nodeCount: number;
    edgeCount: number;
    nodes: Array<{
      id: string;
      label: string;
      kind: string;
      detail: string;
      weight: number;
      updatedAt: string;
    }>;
  };
}

export interface KnowledgeSearchResult {
  documentId: string;
  relativePath: string;
  title: string;
  startLine: number;
  score: number;
  snippet: string;
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
    deleteArchivedTask(request: {
      taskId: string;
    }): Promise<{ deleted: boolean; retainedWorkspaceFiles: boolean }>;
    readDeliveryArtifact(request: {
      path: string;
      workspace: string | null;
    }): Promise<DeliveryArtifactPreview>;
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
      agentPackId?: string | null;
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
    discoverMcp(request: { workspace: string | null }): Promise<McpDiscoveryResult>;
    previewMcpConfig(request: {
      workspace: string | null;
      configuration: string;
      authorizationEnvironment?: string;
    }): Promise<Omit<McpDiscoveryResult, "canceled">>;
    importDiscoveredMcp(request: {
      candidates: McpDiscoveryCandidate[];
    }): Promise<{ canceled: boolean; imported: string[]; skipped: string[]; enabled?: boolean }>;
  };
  agentPacks: {
    list(): Promise<AgentPackSummary[]>;
    get(request: { id: string }): Promise<AgentPackDetails>;
    listCreationTemplates(): Promise<AgentCreationTemplate[]>;
    recommend(request: AgentPackCreationRequest): Promise<AgentWorkshopRecommendation>;
    getDesignSession(): Promise<AgentWorkshopDesignSession | null>;
    orchestrate(request: Omit<AgentPackCreationRequest, "requiredInputs" | "recommendedInputs" | "starterPrompts" | "orchestration"> & {
      provider: Provider;
      model: string;
    }): Promise<{ session: AgentWorkshopDesignSession }>;
    cancelOrchestration(): Promise<{ canceled: boolean; sessionId?: string }>;
    create(request: AgentPackCreationRequest): Promise<{
      canceled: boolean;
      task: AgentTask | null;
    }>;
    onOrchestrationEvent(listener: (event: AgentWorkshopOrchestrationEvent) => void): () => void;
    onOrchestrationReady(listener: (event: AgentWorkshopReadyEvent) => void): () => void;
    listCalibrations(request: { packId: string }): Promise<AgentCalibrationSnapshot>;
    createCalibration(request: {
      packId: string;
      scope: AgentCalibrationPatch["scope"];
      category: AgentCalibrationPatch["category"];
      instruction: string;
      taskId: string | null;
      workspaceRoot: string | null;
      sourceTitle?: string | null;
      sourcePath?: string | null;
    }): Promise<AgentCalibrationSnapshot>;
    rollbackCalibration(request: {
      packId: string;
      patchId: string;
    }): Promise<AgentCalibrationSnapshot>;
    getCapabilities(request: {
      id: string;
      workspace: string | null;
    }): Promise<AgentPackCapabilityReport>;
    install(): Promise<{ canceled: boolean; pack: AgentPackSummary | null }>;
    setEnabled(request: { id: string; enabled: boolean }): Promise<AgentPackSummary>;
    remove(request: { id: string }): Promise<{ canceled: boolean; removed: boolean; id?: string }>;
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
  knowledge: {
    getState(request: { workspace: string | null }): Promise<KnowledgeState>;
    indexWorkspace(request: { workspace: string }): Promise<{
      summary: {
        scannedFiles: number;
        indexedFiles: number;
        reusedFiles: number;
        removedFiles: number;
        skippedFiles: number;
        chunkCount: number;
        indexedBytes: number;
        completedAt: string;
      };
      graph: { updatedAt: string; nodeCount: number; edgeCount: number };
    }>;
    search(request: {
      workspace: string | null;
      query: string;
      maximumResults?: number;
    }): Promise<{ query: string; workspaceRoot: string | null; results: KnowledgeSearchResult[] }>;
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
