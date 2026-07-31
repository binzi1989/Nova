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
    installStore: (request) => invoke("nova:install-store-capability", request)
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
  window: {
    minimize: () => invoke("nova:window-minimize"),
    toggleMaximize: () => invoke("nova:window-toggle-maximize"),
    close: () => invoke("nova:window-close")
  }
});
