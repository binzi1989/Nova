# NOVA 1.0 AgentOS · Codex / Claude Code 差距与差异化基线

更新日期：2026-07-29  
比较对象：OpenAI Codex、Anthropic Claude Code（CC）、NOVA 0.9 Preview 26

本文件只使用公开官方资料与 NOVA 当前代码路径作为依据。它不是营销评分，也不把尚未实现的规划计为已有能力。

## 一句话结论

Codex 当前是“覆盖本地、云端、IDE、移动端和团队工作流的成熟软件工程 Agent 平台”；Claude Code 是“终端原生、可编程、可组合、拥有成熟 Hooks/Checkpoint/Agent Team 的工程执行器”；NOVA 已经形成“原生 Windows、模型中立、以目标契约和持续有效证据为核心的本地 AgentOS”，但在模型编码上限、生态覆盖、Hooks/SDK、会话级回退和正式发布链方面仍明显落后。

NOVA 1.0 不应假装全面超过两者。可成立的竞争位置是：

> 不只让 Agent 把代码写出来，还让用户知道目标是否真的达成、证据是否仍然有效、失败后应该从哪里继续。

## 能力对比

| 维度 | Codex | Claude Code | NOVA 当前 | 1.0 判断 |
|---|---|---|---|---|
| 核心编码能力 | 前沿 Codex 模型，长任务、代码审查和真实工程优化成熟 | Claude Opus/Sonnet，终端工程执行成熟 | OpenAI/DeepSeek/Kimi 通用 API 路由，结果高度依赖用户选择的模型 | **明显落后**；产品层无法完全补偿模型差距 |
| 原生桌面体验 | Windows/macOS Codex App，多任务 command center | 以终端为核心，提供 agent view 等管理界面 | 原生 Windows WPF Threadspace、完成态净化与结果优先交付台 | **有竞争力**，但可访问性与状态收口仍需 RC1 |
| 长任务与恢复 | 长时间/后台任务、跨设备接管、线程持续 | session resume、background task、checkpoint/rewind | 快照、Supervisor lease、崩溃恢复、弹性预算、安全暂停 | **接近本地场景**；缺云端和跨设备 |
| 多 Agent | 并行线程、worktree 隔离 | subagent、agent teams、共享任务列表和 mailbox | 自动子 Agent、Parallel Orchestrator、Agent Mesh、Worktree Tournament | **机制丰富但成熟度落后**；需要更多真实工程压测 |
| Worktree / Diff | 内建 worktree、diff review、编辑器交接 | worktree session、checkpoint rewind | Worktree Tournament、Git hunk stage/revert、代码审查 | **部分追平**；缺通用会话级 rewind |
| Skills / Plugins | Skills、Plugins、应用连接器、自动匹配 | Skills、Plugins、agents、hooks、MCP | Skills 注册/审计/超市、MCP 发现/导入/超市、Capability Compass | **本地能力接近**；生态规模和团队共享落后 |
| MCP | 支持 MCP/插件体系并与应用整合 | stdio/HTTP、用户/项目/本地 scope、OAuth | stdio/Streamable HTTP、只读扫描、分步授权导入、后台搜索 | **安全体验有特色**；OAuth 与团队 scope 不足 |
| 自动化 | Automations、后台计划、review queue、云端触发方向 | Hooks、headless/SDK、CI/脚本组合 | 本地计划任务，仅应用打开期间运行 | **明显落后**；1.0 明确限制，不伪装后台服务 |
| Hooks / SDK / CLI | Hooks、SDK、CLI、IDE、GitHub/Slack 等 | 生命周期 Hooks、CLI JSON/stream、Agent SDK | 无公开 CLI/SDK、无通用生命周期 Hooks | **明显落后**；延后到 1.x |
| 权限与安全 | 系统级 sandbox、项目/团队规则、按风险提升 | permission modes、allow/deny、sandbox、managed policy | 工作区边界、逐风险授权、整轮信任、Intent/Commit 收据、防重放 | **接近且更可视**；需要故障注入和安全审计收口 |
| 完成证明 | 依赖 Agent 验证、测试和审查 | 依赖 Agent 验证、hooks、测试和用户检查 | Outcome Contract、Engineering Completeness、Independent Council、Goal Evidence Matrix、三轮成功信号级定向修复 | **NOVA 差异化优势** |
| 证据新鲜度 | 官方产品介绍未把持续证据有效性作为主要交互对象 | Checkpoint 关注回退，不等同于结果证据持续有效 | `PROVEN → STALE → 重新验证` 工作区指纹生命周期 | **NOVA 明确优势** |
| 目标模式 | 可用长任务提示驱动结果 | 可用 plan/agent teams 驱动目标 | Goal Explorer 自动补齐未知项，冻结 Mission Charter 与成功信号 | **NOVA 差异化优势** |
| 状态治理 | 成熟线程/云任务状态 | 成熟 session/task/agent 状态 | Kernel、Task Graph、Supervisor、Evidence、Artifact 多存储并存 | **尚未收口**；统一事件日志是 1.0 P0 |
| 故障恢复语义 | 产品级错误与恢复体系成熟 | 权限、checkpoint、doctor、resume 体系成熟 | Preview 18 统一模型/网络/工具/权限/预算/构建/验证/宿主故障码 | **本轮补齐基础**，仍需故障注入验证 |
| 正式发布 | 官方签名、更新、账户与企业分发 | 自动更新、官方安装方式、企业平台 | ZIP/安装器定义；尚无发布证书和真实更新源 | **1.0 GA 外部阻断项** |

## NOVA 应当追平的部分

1. **工程完整性**：复杂任务不能用单文件 Demo、说明性文本或未经验证的结果交差。
2. **恢复确定性**：崩溃、预算耗尽、权限拒绝、网络失败后，只恢复未完成阶段，不重复副作用。
3. **并行工作的可理解性**：用户能看见为何拆分、谁拥有哪些文件、哪些结果被采纳。
4. **Diff、审查与回退**：每个真实变更可检查、可定位、可撤回。
5. **扩展生态体验**：MCP/Skills 应可发现、可审计、可授权、可禁用，而不是手写配置。
6. **性能与长任务能力**：至少用固定工程任务集证明成功率、恢复率和终态分类，而不是依靠主观体验。

## NOVA 不应在 1.0 强行追赶的部分

- 云任务、手机接管、跨设备同步。
- IDE 插件、公开 CLI/SDK 和完整 Hooks 生态。
- 企业 RBAC、团队账单、远程多宿主管理。
- Mac 与 Windows 完全对等。
- 无限层级子 Agent 和无人值守外部副作用。

这些能力会扩大权限面、平台面和状态源，违反当前 1.0 范围冻结。

## NOVA 可以建立的差异化

### 1. 结果契约，而不是“Agent 说完成”

用户给目标后，NOVA 生成 Mission Charter，明确成功信号、约束、未知项和停止条件。工程任务必须同时通过真实文件变化、构建/测试、工程完整性和结果证明。

### 2. 持续有效的证据

完成不是永久标签。工作区或交付内容变化后，旧证据自动降为 `STALE`。这是 NOVA 从聊天工具走向 AgentOS 的关键区别。

### 3. 模型中立的治理层

OpenAI、DeepSeek 与 Kimi 使用同一权限、预算、工具、证据和恢复协议。NOVA 的核心资产不应是“又一个模型壳”，而是模型之上的可控执行内核。

### 4. 面向非专家的能力装配

能力罗盘、MCP 超市和 Skills 超市把“安装配置文件”转成“理解影响—确认权限—加载能力”。这比纯终端配置更适合普通 Windows 用户。

### 5. 温和但不含糊的恢复

统一故障码不要求用户理解异常栈，而是给出一个明确下一步；同时保留事件 ID、原始诊断和防重放边界，兼顾温度与工程严谨。

## 距离 1.0 GA 的剩余阻断项

| 优先级 | 阻断项 | 当前状态 | 退出条件 |
|---|---|---|---|
| P0 | Goal 未满足信号的定向修复回路 | Preview 19 已实现 | 最多三轮，只重做未通过信号；通过证据冻结并跨重启保留轮次 |
| P0 | 统一单调执行事件日志 | Preview 26 已实现 | Task Snapshot、Task Graph、Supervisor 与终态由同一序列投影并通过撕裂重放 |
| P0 | 统一故障分类与恢复协议 | Preview 18 已实现 | 九类故障稳定分类、持久化、脱敏、恢复动作回归通过 |
| P1 | 单一状态真值与 Truthful UX | 部分完成 | 六个主要界面无终态冲突 |
| P1 | 30 个固定端到端任务集 | 目录与硬门禁已完成，真实运行未执行 | 30 项各三轮、90 份可检查证据；可行任务 PROVEN ≥ 80%，终态分类正确率 ≥ 90% |
| P1 | 故障注入与安全审计 | Preview 26 自动回归已完成 | 模型、工具、写入、验证、交付五类各 20 次，无重复副作用；RC2 再做签名环境实测 |
| 外部 | Windows Authenticode 证书 | 未提供 | EXE 与安装器签名均为 Valid |
| 外部 | 真实 HTTPS 更新源 | 未提供 | 清单地址可用、签名与 SHA-256 验证通过 |

## 本轮收口决策

- Preview 18 新增统一故障分类和持久化恢复账本。
- Preview 18 发布脚本不再写入虚假的 `example.invalid` 更新地址。
- 未签名 Preview 明确标记为 `PREVIEW_UNSIGNED` 且自动更新关闭。
- 稳定版 `1.0.0` 在缺少真实 HTTPS 地址、有效 EXE 签名或有效安装器签名时直接拒绝构建。
- Preview 19 新增持久化的 Goal 定向修复账本，只重做未满足信号并冻结已有通过证据。
- Preview 20 新增真实文件级交付清单，并把首页与交付台收紧为结果优先、证据按需展开。
- Preview 21 将顶部与运行中的多组内部状态合并为一个面向人的当前状态，并把专业工程、MCP / Skills 与计划任务收进按需工具入口。
- Preview 22 新增 Kimi K3/K2.6 真实运行时和有界多模态附件输入；图片不会在不支持视觉的模型或并行路径中静默丢失。
- Preview 24 已让 Kernel、Supervisor、模型 User-Agent 与 MCP 从程序集读取同一版本，并同步 Windows manifest 与安装器默认版本；正式 GA 仍需把安装器构建、签名和全新环境安装验证纳入自动发布门禁。
- Preview 24 已补齐计划中心直接创建和任务归档，并修复计划误续接当前任务、附件误带入与执行模式漂移；durable claim/lease、停用竞态和 misfire 策略仍是 1.0 调度 P0。
- Preview 26 新增可重放的单调执行日志；任务快照、任务图与 Supervisor 租约共享提交序号，状态文件丢失或日志尾部撕裂后仍能恢复最后真值。
- Preview 26 固化 30 项三轮 GA 基准及发布硬门禁，并完成五类边界各 20 次故障注入；没有真实 90 次结果、代码签名证书和 HTTPS 包地址时，`1.0.0` 仍拒绝发布。
- 下一主线只执行真实端到端基准、六界面 Truthful UX 验收与外部签名发布，不再扩展新平台或新面板。

## 官方资料

- OpenAI, Introducing the Codex app: https://openai.com/index/introducing-the-codex-app/
- OpenAI, Work with Codex from anywhere: https://openai.com/index/work-with-codex-from-anywhere/
- OpenAI, Codex is now generally available: https://openai.com/index/codex-now-generally-available/
- OpenAI, Codex for every role, tool, and workflow: https://openai.com/index/codex-for-every-role-tool-workflow/
- Anthropic, Run agents in parallel: https://code.claude.com/docs/en/agents
- Anthropic, Create custom subagents: https://code.claude.com/docs/en/sub-agents
- Anthropic, Agent teams: https://code.claude.com/docs/en/agent-teams
- Anthropic, Checkpointing: https://code.claude.com/docs/en/checkpointing
- Anthropic, Hooks: https://code.claude.com/docs/en/hooks-guide
- Anthropic, CLI reference: https://docs.anthropic.com/en/docs/claude-code/cli-usage
