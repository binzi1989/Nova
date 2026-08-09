import assert from "node:assert/strict";
import crypto from "node:crypto";
import fs from "node:fs";
import vm from "node:vm";

const source = fs.readFileSync(new URL("./capture-core.js", import.meta.url), "utf8");
const manifest = JSON.parse(fs.readFileSync(new URL("./manifest.json", import.meta.url), "utf8"));
const digest = crypto.createHash("sha256").update(Buffer.from(manifest.key, "base64")).digest().subarray(0, 16);
const alphabet = "abcdefghijklmnop";
const extensionId = [...digest].map((byte) => alphabet[byte >> 4] + alphabet[byte & 15]).join("");
assert.equal(extensionId, "nplhnfcoijedjoihpghfhnjkkgomhhpg");
assert.deepEqual([...manifest.permissions].sort(), ["activeTab", "nativeMessaging", "scripting", "storage"].sort());
assert.equal(Object.hasOwn(manifest, "host_permissions"), false);
const context = vm.createContext({ globalThis: {} });
vm.runInContext(source, context);
const parser = context.globalThis.NovaMaimaiCapture;
assert.ok(parser, "capture API missing");

const result = parser.parseVisibleText(`
李雷
当前职位：研发总监
当前公司：未来智能
所在地：北京
个人简介：专注企业智能体落地
技能：AgentOS、企业架构、产品管理
`, "李雷 - 脉脉");

assert.equal(result.displayName, "李雷");
assert.equal(result.currentTitle, "研发总监");
assert.equal(result.currentCompany, "未来智能");
assert.equal(result.location, "北京");
assert.equal(result.visibleSkills.length, 3);

const feed = parser.parseCommunityFeed(`
社区
招聘
返回
钢铁侠
06-02 ·
九坤投资
🔥顶级私募·九坤投资 | 能源科技板块大规模扩招！
交易算法研究员：要求 C++/Python
发布于 北京
2
评论
赞
`);
assert.equal(feed.sourceType, "community-post-author");
assert.equal(feed.displayName, "钢铁侠");
assert.equal(feed.currentCompany, "九坤投资");
assert.equal(feed.location, "北京");
assert.match(feed.publicSummary, /交易算法研究员/);
assert.equal(parser.detectPageType("/community/feed-detail/1915753117"), "community-post-author");
assert.equal(parser.detectPageType("/web/search_center"), "unsupported");
assert.equal(parser.detectPageType("/contact/detail/abc"), "person-profile");
console.log(JSON.stringify({ ok: true, assertions: 17, extensionId, persistentHostPermissions: false }));
