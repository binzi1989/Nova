const { contextBridge, ipcRenderer } = require("electron");

const invoke = (channel, payload) => ipcRenderer.invoke(channel, payload);

contextBridge.exposeInMainWorld("nova", {
  system: {
    boot: () => invoke("nova:boot"),
    listTasks: () => invoke("nova:list-tasks"),
    listArchivedTasks: () => invoke("nova:list-archived-tasks"),
    getTask: (request) => invoke("nova:get-task", request),
    archiveTask: (request) => invoke("nova:archive-task", request),
    restoreTask: (request) => invoke("nova:restore-task", request),
    deleteArchivedTask: (request) => invoke("nova:delete-archived-task", request),
    readDeliveryArtifact: (request) => invoke("nova:read-delivery-artifact", request),
    selectWorkspace: () => invoke("nova:select-workspace"),
    selectAttachments: () => invoke("nova:select-attachments"),
    desktopSnapshot: () => invoke("nova:desktop-snapshot")
  },
  model: {
    configure: (configuration) => invoke("nova:configure-model", configuration),
    run: (request) => invoke("nova:run-model", request),
    cancel: (request) => invoke("nova:cancel-model", request),
    onEvent: (listener) => {
      const handler = (_event, payload) => listener(payload);
      ipcRenderer.on("nova:agent-event", handler);
      return () => ipcRenderer.removeListener("nova:agent-event", handler);
    }
  },
  capabilities: {
    list: (request) => invoke("nova:list-capabilities", request),
    setMcpEnabled: (request) => invoke("nova:set-mcp-enabled", request),
    setSkillEnabled: (request) => invoke("nova:set-skill-enabled", request),
    install: (request) => invoke("nova:install-capability", request),
    searchStore: (request) => invoke("nova:search-capability-store", request),
    installStore: (request) => invoke("nova:install-store-capability", request),
    discoverMcp: (request) => invoke("nova:discover-mcp", request),
    previewMcpConfig: (request) => invoke("nova:preview-mcp-config", request),
    importDiscoveredMcp: (request) => invoke("nova:import-discovered-mcp", request)
  },
  agentPacks: {
    list: () => invoke("nova:list-agent-packs"),
    get: (request) => invoke("nova:get-agent-pack", request),
    listCreationTemplates: () => invoke("nova:list-agent-creation-templates"),
    recommend: (request) => invoke("nova:recommend-agent-pack", request),
    getDesignSession: () => invoke("nova:get-agent-workshop-session"),
    orchestrate: (request) => invoke("nova:orchestrate-agent-pack", request),
    cancelOrchestration: () => invoke("nova:cancel-agent-pack-orchestration"),
    create: (request) => invoke("nova:create-agent-pack", request),
    onOrchestrationEvent: (listener) => {
      const handler = (_event, payload) => listener(payload);
      ipcRenderer.on("nova:agent-workshop-event", handler);
      return () => ipcRenderer.removeListener("nova:agent-workshop-event", handler);
    },
    onOrchestrationReady: (listener) => {
      const handler = (_event, payload) => listener(payload);
      ipcRenderer.on("nova:agent-workshop-ready", handler);
      return () => ipcRenderer.removeListener("nova:agent-workshop-ready", handler);
    },
    listCalibrations: (request) => invoke("nova:list-agent-calibrations", request),
    createCalibration: (request) => invoke("nova:create-agent-calibration", request),
    rollbackCalibration: (request) => invoke("nova:rollback-agent-calibration", request),
    getCapabilities: (request) => invoke("nova:get-agent-pack-capabilities", request),
    install: () => invoke("nova:install-agent-pack"),
    setEnabled: (request) => invoke("nova:set-agent-pack-enabled", request),
    remove: (request) => invoke("nova:remove-agent-pack", request)
  },
  extensions: {
    listProfiles: () => invoke("nova:list-extension-profiles"),
    saveSshProfile: (request) => invoke("nova:save-ssh-profile", request),
    testSshProfile: (request) => invoke("nova:test-ssh-profile", request),
    saveCloudAdapter: (request) => invoke("nova:save-cloud-adapter", request)
  },
  growth: {
    getState: () => invoke("nova:get-living-memory"),
    analyze: () => invoke("nova:analyze-living-memory"),
    setHabitState: (request) => invoke("nova:set-habit-state", request),
    distillSkill: () => invoke("nova:distill-personal-skill"),
    installSkill: (request) => invoke("nova:install-distilled-skill", request),
    getEvolutionLab: () => invoke("nova:get-evolution-lab"),
    configureEvolutionLab: (request) => invoke("nova:configure-evolution-lab", request),
    proposeEvolution: (request) => invoke("nova:propose-evolution", request),
    prepareEvolution: (request) => invoke("nova:prepare-evolution", request),
    evaluateEvolution: (request) => invoke("nova:evaluate-evolution", request),
    adoptEvolution: (request) => invoke("nova:adopt-evolution", request),
    rejectEvolution: (request) => invoke("nova:reject-evolution", request),
    onEvolutionEvent: (listener) => {
      const handler = (_event, payload) => listener(payload);
      ipcRenderer.on("nova:evolution-event", handler);
      return () => ipcRenderer.removeListener("nova:evolution-event", handler);
    }
  },
  knowledge: {
    getState: (request) => invoke("nova:get-knowledge-state", request),
    indexWorkspace: (request) => invoke("nova:index-workspace-knowledge", request),
    search: (request) => invoke("nova:search-workspace-knowledge", request)
  },
  window: {
    minimize: () => invoke("nova:window-minimize"),
    toggleMaximize: () => invoke("nova:window-toggle-maximize"),
    close: () => invoke("nova:window-close")
  }
});
