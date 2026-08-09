const Module = require("node:module");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const originalLoad = Module._load;
const noOperation = () => {};
const smokeUserData = path.join(os.tmpdir(), "nova-extension-gateway-smoke");
fs.rmSync(smokeUserData, { recursive: true, force: true });
const app = {
  isPackaged: false,
  commandLine: { hasSwitch: (name) => name === "smoke-gateway" },
  disableHardwareAcceleration: noOperation,
  requestSingleInstanceLock: () => true,
  quit: noOperation,
  whenReady: () => Promise.resolve(),
  on: noOperation,
  getPath: () => smokeUserData
};

Module._load = function load(request, parent, isMain) {
  if (request === "electron") {
    return {
      app,
      BrowserWindow: { fromWebContents: () => null },
      clipboard: { writeText: noOperation },
      dialog: {},
      ipcMain: { handle: noOperation },
      screen: {},
      session: {
        defaultSession: {
          setPermissionCheckHandler: noOperation,
          setPermissionRequestHandler: noOperation
        }
      },
      shell: {}
    };
  }
  return originalLoad.call(this, request, parent, isMain);
};

if (!process.argv.includes("--smoke-gateway")) process.argv.push("--smoke-gateway");
require("../electron/main.cjs");
