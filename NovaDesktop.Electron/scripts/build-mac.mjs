import { existsSync, mkdirSync, rmSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";

if (process.platform !== "darwin") {
  throw new Error(
    "macOS application bundles must be built on macOS. Run the Release macOS GitHub Actions workflow."
  );
}

const scriptRoot = dirname(fileURLToPath(import.meta.url));
const projectRoot = resolve(scriptRoot, "..");
const repositoryRoot = resolve(projectRoot, "..");
const bridgeProject = join(
  repositoryRoot,
  "Nova.AgentOS.Bridge",
  "Nova.AgentOS.Bridge.csproj"
);
const bridgeOutput = join(projectRoot, "resources", "bridge");
const packageOutput = join(projectRoot, "dist-electron-mac");
const builder = join(
  projectRoot,
  "node_modules",
  ".bin",
  "electron-builder"
);

function run(command, args, cwd = projectRoot) {
  const result = spawnSync(command, args, {
    cwd,
    stdio: "inherit",
    env: {
      ...process.env,
      CSC_IDENTITY_AUTO_DISCOVERY: "false"
    }
  });
  if (result.error) throw result.error;
  if (result.status !== 0) {
    throw new Error(`${command} failed with exit code ${result.status}.`);
  }
}

if (!existsSync(builder)) {
  throw new Error("electron-builder is not installed. Run npm install first.");
}

run("npm", ["run", "build:renderer"]);

for (const architecture of ["arm64", "x64"]) {
  rmSync(bridgeOutput, { recursive: true, force: true });
  mkdirSync(bridgeOutput, { recursive: true });

  run("dotnet", [
    "publish",
    bridgeProject,
    "--configuration",
    "Release",
    "--runtime",
    `osx-${architecture}`,
    "--self-contained",
    "true",
    "-p:PublishSingleFile=false",
    "--output",
    bridgeOutput
  ]);

  const bridgeExecutable = join(bridgeOutput, "Nova.AgentOS.Bridge");
  if (!existsSync(bridgeExecutable)) {
    throw new Error(`AgentOS bridge publish did not produce ${bridgeExecutable}.`);
  }

  run(builder, [
    "--mac",
    "zip",
    `--${architecture}`,
    `--config.directories.output=${packageOutput}`
  ]);
}

console.log(`macOS packages ready: ${packageOutput}`);
