# NOVA Agent Pack SDK v1

Agent Pack 是声明式专业能力包，不包含或暴露 NOVA 核心源码。它向 AgentOS 提供行业任务章程、角色、工作流、知识边界、交付模板和评测，不得绕过工作区、权限、预算、恢复和 Proof-of-Done。

## 目录约定

```text
my-agent-pack/
├─ nova.industry.json
├─ agent-card.json
├─ certification.json
├─ INDUSTRY_CHARTER.md
├─ agents/AGENT_ROSTER.md
├─ workflows/entry-workflow.json
├─ delivery-templates/result.md
├─ knowledge/
└─ evaluations/
```

复制 `industry-packs/_template` 后，把 `nova.industry.example.json` 重命名为 `nova.industry.json`。Manifest 可使用 `industry-packs/nova.industry.schema.json` 校验。

也可以在 Electron 客户端进入 **扩展坞 → Agents → 创建 Agent**。Agent 工坊会按照 [NOVA Agent Creation Standard 1.0](NOVA-AGENT-CREATION-STANDARD.md) 生成完整 Pack、五类基础评测和持久化体检报告；新 Agent 默认停用，必须检查后手动启用。

## 运行契约

- `id` 必须稳定、全小写，并采用反向域名风格，例如 `nova.manufacturing-rfq`。
- 一个 Pack 必须有清晰的客户、任务边界和可检查交付物。
- Workflow 的每一步必须声明 `agent`、`outputs` 和 `acceptance`。
- 建议声明 `onboarding` v1。NOVA 会把 `steps` 统一渲染为文本、选择或附件输入，把用户选择代入 `outcomes[].promptTemplate`，形成第一轮任务。
- 启动引导不只列“需要什么”，还必须用 `whyItMatters` 说明资料如何影响判断，并用 `example` 告诉新用户怎样收集。
- `promptTemplate` 使用 `{{step-id}}` 引用输入；附件步骤会替换为已附文件名。必填项未完成时 AgentOS 不允许启动该目标。
- Pack 知识只作为任务指导；价格、政策、库存、平台规则等易变化事实仍需实时验证。
- Pack 可以请求 MCP、Skill 或 A2A 能力，但不能假定它们已经安装或获得授权。
- 建议使用 `capabilityRequirements` v1 声明运行前需要或可选的 MCP/Skill。AgentOS 会检查已启用、已注册但停用、内置可加载和缺失四种状态；缺失 MCP 可进入统一接入器。
- 统一 MCP 接入器接受 HTTPS 地址、`mcpServers` JSON，以及 Codex 风格 TOML。它只预览并净化配置，明文密钥不会被复制，连接导入后默认停用，必须由用户再次启用。
- `matchIds` 应包含官方名称与常见配置名；内置市场能力可额外填写 `catalogId`。第三方网上配置不需要 NOVA 预先收录，用户可粘贴或在授权后扫描本机已有配置。
- 外部发布、投放、账号访问、购买、付款、删除和桌面控制必须在 Manifest 中声明，并由 AgentOS 独立审批。
- v1 只允许声明文件，不执行 Pack 内脚本、二进制、依赖安装或任意代码。

## 能力需求示例

```json
{
  "capabilityRequirements": {
    "version": "1.0",
    "items": [
      {
        "id": "marketplace-account-data",
        "kind": "mcp",
        "name": "平台官方 MCP",
        "reason": "获得用户授权后读取真实账号数据。",
        "required": false,
        "matchIds": ["platform-official", "platform-mcp"],
        "catalogId": "platform-official"
      }
    ]
  }
}
```

不要在 Pack、URL、JSON、TOML、说明文档或示例中提交真实 Token。配置只引用环境变量名；OAuth MCP 由其官方授权流程完成。

## 版本与兼容

- 修改描述或非契约知识：Patch。
- 增加兼容角色、步骤或模板：Minor。
- 更改交付字段、权限或主工作流：Major。
- `novaCompatibility` 声明可运行的 AgentOS 版本范围。
- 已发布 Pack 不在原目录静默覆盖；升级必须保留版本记录并重新验收。

## 发布闸门

发布前必须满足：Manifest 可解析；路径不越界；没有密钥、个人数据或可执行文件；至少一个入口工作流；角色与步骤一一对应；交付模板可直接使用；外部动作已声明；五个真实评测中没有伪造来源、伪造完成或越权动作。
