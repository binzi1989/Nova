import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";

const executable = path.resolve(process.argv[2] || path.join(
  import.meta.dirname,
  "..", "connector", "bin", "Release", "net8.0-windows", "Nova.Maimai.Connector.exe"
));
assert.ok(fs.existsSync(executable), `connector executable missing: ${executable}`);
const dataRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nova-maimai-e2e-"));

function frame(value) {
  const payload = Buffer.from(JSON.stringify(value), "utf8");
  const header = Buffer.alloc(4);
  header.writeUInt32LE(payload.length, 0);
  return Buffer.concat([header, payload]);
}

function readFrames(buffer) {
  const items = [];
  let offset = 0;
  while (offset < buffer.length) {
    assert.ok(offset + 4 <= buffer.length, "truncated native header");
    const length = buffer.readUInt32LE(offset);
    offset += 4;
    assert.ok(offset + length <= buffer.length, "truncated native payload");
    items.push(JSON.parse(buffer.subarray(offset, offset + length).toString("utf8")));
    offset += length;
  }
  return items;
}

try {
  const nativeInput = Buffer.concat([
    frame({ action: "ping" }),
    frame({
      action: "capture",
      payload: {
        sourceType: "community-post-author",
        sourceUrl: "https://maimai.cn/community/feed-detail/e2e?from=test",
        pageTitle: "王五 - 脉脉",
        displayName: "王五",
        currentTitle: "企业智能体架构师",
        currentCompany: "端到端科技",
        location: "深圳",
        publicSummary: "AgentOS 与企业架构",
        visibleSkills: ["AgentOS", "MCP"],
        evidenceText: "当前页面可见内容",
        purpose: "端到端连接验证",
        retentionDays: 30
      }
    })
  ]);
  // Chrome launches a Windows Native Messaging Host with the extension origin
  // and a parent-window argument, not with our internal `native-host` token.
  const native = spawnSync(executable, [
    "chrome-extension://nplhnfcoijedjoihpghfhnjkkgomhhpg/",
    "--parent-window=4242",
    "--data-dir",
    dataRoot
  ], {
    input: nativeInput,
    encoding: null,
    maxBuffer: 4 * 1024 * 1024
  });
  assert.equal(native.status, 0, native.stderr?.toString("utf8"));
  const nativeResponses = readFrames(native.stdout);
  assert.equal(nativeResponses.length, 2);
  assert.equal(nativeResponses[0].ok, true);
  assert.equal(nativeResponses[1].profile.displayName, "王五");

  const requests = [
    { jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2025-11-25" } },
    { jsonrpc: "2.0", method: "notifications/initialized" },
    { jsonrpc: "2.0", id: 2, method: "tools/list", params: {} },
    { jsonrpc: "2.0", id: 3, method: "tools/call", params: { name: "maimai_search_profiles", arguments: { query: "AgentOS 深圳" } } },
    { jsonrpc: "2.0", id: 4, method: "tools/call", params: { name: "maimai_stats", arguments: {} } }
  ];
  const mcp = spawnSync(executable, ["mcp", "--data-dir", dataRoot], {
    input: requests.map((item) => JSON.stringify(item)).join("\n") + "\n",
    encoding: "utf8",
    maxBuffer: 4 * 1024 * 1024
  });
  assert.equal(mcp.status, 0, mcp.stderr);
  const responses = mcp.stdout.trim().split(/\r?\n/).map((line) => JSON.parse(line));
  assert.equal(responses.length, 4, "MCP notification should not produce a response");
  assert.equal(responses[0].result.protocolVersion, "2025-11-25");
  assert.equal(responses[1].result.tools.length, 7);
  const searchResult = JSON.parse(responses[2].result.content[0].text);
  const statsResult = JSON.parse(responses[3].result.content[0].text);
  assert.equal(searchResult.profiles[0].displayName, "王五");
  assert.equal(statsResult.total, 1);

  const vault = fs.readFileSync(path.join(dataRoot, "profiles.vault"));
  assert.equal(vault.includes(Buffer.from("王五", "utf8")), false);
  assert.equal(vault.includes(Buffer.from("maimai.cn", "utf8")), false);
  console.log(JSON.stringify({
    ok: true,
    nativeProcessResponses: nativeResponses.length,
    mcpProcessResponses: responses.length,
    mcpTools: responses[1].result.tools.length,
    crossProcessPersistence: true,
    plaintextLeak: false
  }));
} finally {
  if (path.basename(dataRoot).startsWith("nova-maimai-e2e-")) {
    fs.rmSync(dataRoot, { recursive: true, force: true });
  }
}
