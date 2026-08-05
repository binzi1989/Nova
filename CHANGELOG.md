# Changelog

## 1.1.0-preview.15

### Added

- Agent 工坊现在为每一个新 Agent 自动生成独立的只读 ID；同一份未完成草案恢复时保持原 ID，成功进入构建后自动准备下一枚新 ID。
- Agent 中心支持移除用户生成或导入的 Agent Pack；内置 Pack 受保护，已启用 Pack 必须先停用，并在原生确认后才会删除本机扩展文件。
- 移除 Agent Pack 不会删除历史任务、聊天记录或用户工作区交付物。

### Fixed

- 修复编排委员会因协调模型返回截断、代码围栏或轻微结构错误而丢弃三名设计 Agent 真实产出的不稳定问题。
- Bridge 现在持久化全部阶段产出；客户端优先进行确定性结构修复，仅在 JSON 无法解析时进行一次轻量模型修复，并可从阶段成果恢复为待人工审阅草案。
- Word、PDF 与常见文本附件进入统一提取链路，避免把常见办公文件直接判为“不可安全读取的文本文件”。
- 取消或关闭工坊时会终止仍在进行的轻量修复请求，不再遗留悬挂模型调用。

### Verification

- 新增随机 ID、禁止硬编码 ID、安全移除入口以及 Pack 注册表移除回归检查。
- 新增 Agent 工坊阶段结果恢复冒烟测试；Electron Renderer、主进程、AgentOS Bridge 与 Windows 生产构建通过。

### English summary

- Agent Workshop now preserves every design agent's stage output and no longer discards real work because the coordinator returned truncated or lightly malformed JSON.
- The client performs deterministic structural recovery first, issues at most one bounded model-repair request only when JSON cannot be parsed, and can return a recoverable draft for human review.
- Word, PDF, and common text attachments use a unified extraction path instead of being rejected as non-text input by default.
- Generated Agent Packs receive unique IDs and can be removed safely without deleting historical tasks, conversations, or workspace deliverables.
- Workshop recovery smoke tests, the Electron production build, and the AgentOS Bridge build pass for this Windows preview.

## 1.1.0-preview.14

### Fixed

- 修复 Agent Pack 构建任务在 `start_task` 阶段错误绑定目标 Pack ID，导致新 Pack 报“不存在”、旧 Pack 报“尚未启用”的循环依赖。
- Pack 构建任务现在由系统 Builder 启动；目标 Pack ID只进入后续编译与注册阶段，体检完成前不会作为运行时 Agent 加载。
- 新生成 Pack 仍在完整体检后注册，并默认保持停用等待用户检查。

### Verification

- 新增“构建任务不得依赖待创建 Pack 已存在或已启用”回归检查。

## 1.1.0-preview.13

### Fixed

- 移除 Agent 中心已审阅草案之后的冗余 Windows 二次确认框；“确认方案并构建 Agent Pack”现在会立即创建正式 AgentOS 构建任务。
- 修复原生确认框等待期间页面提前显示“正在生成与体检”，但任务账本中尚无 `start_task` 的误导状态。
- 如果正式任务创建失败，错误会直接显示在 Agent 工坊内，编排草案继续保留，可原地重试而无需重新设计。
- Pack 仍保持默认停用，只有契约、工作流、引导、交付模板和基础评测全部体检通过后才注册。

### Verification

- 新增“已审阅草案单击即创建正式任务”与工坊内错误可见性回归检查。
- Electron Renderer、主进程、AgentOS Bridge 与包内客户端启动检查通过。

## 1.1.0-preview.12

### Fixed

- 修复真实子 Agent 已完成设计、但主协调 Agent 返回截断或轻微错误 JSON 时，Agent 工坊仍退回“开始多 Agent 设计”的问题。
- AgentOS Bridge 现在把三名设计 Agent 的完整阶段产出交回工坊；主草案解析失败时，由同一模型进行一次只修复结构的综合请求，不重新运行三名 Agent。
- JSON 接收器支持代码围栏、多对象输出、尾随逗号和字符串控制字符等常见模型格式偏差。
- 如果主协调修订仍存在格式问题，但真实子 Agent 已返回符合标准的结构草案，系统会保留该草案等待人工确认，不再丢弃模型成果。
- 编排完成后统一写入“编排草案已生成”事件，界面进入“确认方案并构建 Agent Pack”阶段。

### Verification

- 增加子 Agent 完整产出捕获、单协调 Agent 修订、结构草案恢复和完成事件回归检查。
- Electron 主进程语法、Renderer 与 AgentOS Bridge 编译通过。

## 1.1.0-preview.11

### Fixed

- 修复 Agent 工坊完成真实多 Agent 设计后又退回“开始多 Agent 设计”的状态机错误；应用重载后也会恢复正在运行的 Design Session。
- 结构完整但模型审查标记为 `revise` 的草案不再被当作失败丢弃，而是留在 Agent 中心等待用户审阅；用户确认将作为明确的人工批准。
- 用户确认方案后，才创建正式任务空间并开始构建 Agent Pack，不再混淆“设计”和“代码/契约实现”两个阶段。
- 设计阶段不再暴露文件、知识库、工作区搜索等无关工具，界面只展示行业架构师、工作流架构师、信任审查官与编排委员会。
- 操作文案调整为“确认方案并构建 Agent Pack”，并明确提示后续会生成角色、工作流、契约、引导与评测。

### Verification

- 新增结构草案人工批准、设计工具隔离、角色卡去噪和运行状态恢复回归检查。
- Electron Renderer、主进程、AgentOS Bridge 与打包客户端完成构建和冒烟验证。

## 1.1.0-preview.10

### Changed

- Agent 工坊的“设计”和“执行”正式分层：多 Agent 编排继续使用真实 AgentOS Runtime，但设计阶段停留在 Agent 中心，不再提前创建或污染任务空间。
- 新增可恢复的 Design Session。设计输入、真实角色状态、阶段产出、草案和失败原因独立持久化；应用中断后可以在 Agent 中心恢复审阅或重新编排。
- 用户确认编排草案后才创建唯一一条正式 Agent Pack 构建任务，随后进入任务空间完成契约落盘、引导装配、标准体检和注册。
- 设计会话只允许 Runtime 创建本轮只读子 Agent，文件写入、命令、网页、MCP 和桌面操作均不会被设计阶段自动授权。
- 停止设计只取消当前 Design Session，不影响任何正式任务或用户工程。

### Verification

- 新增“设计会话先于任务创建”回归检查，覆盖 Runtime 复用、会话持久化、取消、草案恢复和确认后建任务边界。
- Electron Renderer、主进程、AgentOS Bridge 与打包客户端自检通过。

## 1.1.0-preview.9

### Fixed

- Agent 工坊不再绕过 AgentOS 直接拼装模型请求；编排统一进入现有持久化任务、模型运行时、流式解析、预算和恢复链路，修复模型实际返回但工坊误判为“未返回”的问题。
- 行业架构师、工作流架构师和信任审查官由 AgentOS Supervisor 作为真实只读子 Agent 工作组创建，角色状态、阶段产出和综合过程进入任务空间，不再停留在设置窗口假等待。
- 新增只读 `orchestration` 权限模式：只自动放行本轮多 Agent 委派，不自动授权文件写入、命令、网页、MCP 或桌面操作。
- 编排任务创建后立即返回并进入任务空间；结构化草案通过 Agent Creation Standard 校验后才回到工坊等待用户确认，失败任务保留真实原因和诊断编号。
- 停止编排现在取消对应的 AgentOS 任务，而不只是中断一个临时网络请求。

### Verification

- 新增 Agent 工坊复用持久化 AgentOS 多 Agent 运行时、只读编排权限、任务空间回流和取消通道回归检查。
- Electron Renderer、主进程语法和 AgentOS Bridge 构建通过。

## 1.1.0-preview.8

### Fixed

- Agent 工坊编排不再无限等待：行业与工作流分析拥有独立的 70 秒单次超时，信任审查阶段拥有 80 秒单次超时，并且每个真实模型角色最多尝试两次。
- 两位架构师改为顺序协作：工作流架构师会读取行业架构师的真实产出，避免同一 API 连接上的并行限流争抢；任一必要角色最终失败都不会生成草案。
- 编排期间每 6 秒更新一次真实等待状态，避免模型仍在处理时界面表现为冻结。
- 新增“停止编排”操作，并贯通 Renderer、Preload 与主进程 AbortController；关闭窗口或重复发起编排也不会遗留悬挂请求。
- 取消本地模板降级方案；行业定位、角色和工作流必须经过真实模型分析。三名角色改为分阶段协作，避免同一模型连接上的并行限流争抢，每个阶段支持携带失败原因重试。
- 审查模型返回不完整 JSON 或不符合 Agent Creation Standard 时，会把真实校验错误交回模型进行修订；只有模型草案最终通过结构审查后才能生成 Pack。
- Agent 工坊失败会生成脱敏诊断编号并写入本机 `logs/agent-workshop.jsonl`，HTTP、超时、空响应、非 JSON 与结构错误不再被统一隐藏为“生成失败”。

### Verification

- 新增 Agent 工坊有界等待、降级结算、状态呈现和端到端取消通道回归检查。
- Electron Renderer 与主进程语法检查通过。

## 1.1.0-preview.6

### Changed

- 用户确认编排草案后，不再停留在设置窗口同步等待；系统会立即创建一条持久化 AgentOS 构建任务并自动进入任务空间。
- Agent Pack 生成过程按真实后端事件展示“锁定草案、编译契约、装配引导与能力、标准体检与注册”，没有延时动画或虚构进度。
- 构建完成后，角色、工作流、引导、能力需求和交付模板必须真实存在，并达到 Runnable、100/100，才允许进入能力仓。
- 构建失败会停留在对应任务中，保留失败原因和任务上下文；不会把不完整 Pack 冒充成可用 Agent。
- 生成任务完成后会自动刷新 Agent Pack 注册表，Pack 默认保持停用，等待用户检查后启用。

### Verification

- 新增“Agent Pack 生成进入持久化任务空间”回归测试。
- Electron Renderer 与 AgentOS Bridge 构建通过；现有 Agent 工坊编排持久化测试继续通过。

## 1.1.0-preview.5

### Changed

- Agent 工坊不再点击后直接套模板生成 Pack；现在必须先完成真实模型驱动的多角色编排，再由用户确认草案后落盘。
- 行业架构师与工作流架构师并行分析前三步设计，信任审查官独立复核角色边界、工作流、验收条件和风险。
- 编排过程会显示每个角色的实时状态与实际输出，最终草案明确列出角色、步骤、交付物、验收条件、所需资料与风险。
- 首轮编排固定为三次受限模型请求；未连接模型、请求失败、输出不完整或审查未通过时均不会生成 Agent Pack。
- 经确认的角色分工、工作流、风险、审查结论及模型来源会写入 Pack，避免被旧的固定角色模板替换。

### Verification

- 新增“审查后的智能体编排持久化”回归测试，验证真实角色、步骤、审查来源和风险均进入最终 Pack。
- Electron Renderer、AgentOS Bridge 构建及 Electron Bridge 端到端冒烟测试通过。

## 1.1.0-preview.4

### Changed

- Agent 工坊第 04 步从资料清单输入框改为 NOVA 自动反馈，不再要求创建者代替 Agent 设计用户资料要求。
- 启动建议会综合前三步中的行业、最终目标、自主程度、工作周期、协作方式、交付形式与判断风格，动态生成核心资料、补充资料和首次任务入口。
- 页面预览与最终 Agent Pack 使用同一个 AgentOS 建议器；即使客户端不提交资料字段，生成的首次使用引导仍然完整。

### Verification

- 新增“空资料配置自动生成引导”和“前三步动态总结”回归测试。
- Electron Renderer、AgentOS Bridge 与 Electron Bridge 端到端验证通过。

## 1.1.0-preview.3

### Added

- Agent Creation Standard 1.0：从场景模板生成包含 Agent Card、工作流、引导、交付契约、基础评测和认证结果的可运行 Agent Pack。
- 交付审查台新增 Agent 校准入口，可把一次用户纠正保存为本轮、当前项目、该 Agent 或本机组织版本的长期规则。
- Agent 详情新增校准版本账本，显示规则来源、范围、类别和回归状态，并支持停用与重新启用。

### Changed

- 校准规则以独立覆盖层保存，不修改原始 Agent Pack；运行时按组织、Agent、项目、本轮逐级叠加，越具体的规则优先。
- Agent 校准不能覆盖权限、预算、工作区、外部动作审批及 Proof-of-Done 安全边界。

### Verification

- 新增校准跨任务、跨项目隔离和可逆回滚测试。
- Electron Renderer、AgentOS Bridge 构建和 Electron Bridge 端到端测试通过。

## 1.1.0-preview.2

### Added

- Agent Pack onboarding v1：行业 Agent 可声明需要用户准备的资料、信息价值、填写示例和期望结果，桌面端按统一协议生成首次使用引导。
- Agent Pack 能力需求 v1：专业 Agent 可声明必需或可选 MCP/Skill，启动前显示已就绪、已登记停用、可加载或待接入状态。
- 通用 MCP 接入器：支持粘贴实际 HTTPS MCP 端点、`mcpServers` JSON、Codex TOML，以及经用户确认的本机 Claude、Cursor、Windsurf、Codex 配置只读扫描。
- 跨境电商 Agent 增加 Mercado Libre 官方 MCP 快速配置与 TikTok for Business MCP 能力提示。
- 跨境电商 Agent 新增中立商品识别、12 维市场需求适配评估、竞争缺口、最小市场验证和明确的 Go / Validate / No-Go 决策路径。
- 新增确定性 `commerce_assess_market_demand` 工具，区分事实、指示性信号、假设和未知项，并单独报告证据质量与不确定性。

### Changed

- 跨境电商默认入口由单一搅蒜器案例改为通用“图片或线索 → 商品识别 → 市场需求推理 → 验证方案 → 交付”工作流；历史案例仅保留作离线回归样本。
- 财务测算调整为市场判断的一层证据，不再替代用户问题、需求频率、竞争空间、内容表现、本地适配、合规与退货风险判断。
- 选择专业 Agent 后先展示低学习成本的资料收集向导，生成提示词后仍由用户确认再执行。
- MCP 导入只复制经过净化的环境变量引用，不复制明文密钥；新连接默认停用，首次启用由桌面原生确认框单独授权。
- GitHub 仓库或产品文档链接不会被误判为可调用的 MCP 服务端点，界面会提示用户粘贴 README 中的连接配置或真实端点。

## Unreleased · Cross-Border Commerce Agent v0.2

- 跨境 Agent 新增按 Pack 隔离的商品档案、落地利润和市场证据三个确定性工具；通用 NOVA 不加载行业工具；
- 上市工作流升级为 Product Passport → Evidence Ledger → Landed Profit → Launch Gate；成本或证据不足时只能条件式裁决；
- 成果展示改为“裁决、关键指标、交付文件、完整说明、下一步”五层结构，机器展示标记不会暴露给用户；
- 移除独立伙伴验收模块、验证向导和报告导出入口；底层自动测试、权限与 Proof-of-Done 保持不变。

## 1.1.0-preview.1

### Added

- 新增 Agent Pack 运行时契约，任务可持久绑定行业包，并在恢复、多轮追问和交付阶段保持同一专业上下文；
- 新增 Agent 中心，可查看角色、工作流、起始任务、交付模板和权限声明，并在输入框旁切换通用 NOVA 或专业 Agent；
- 首个内置样板为 Cross-Border Commerce，覆盖选品、市场证据、本地化内容和交付审计；
- 新增经用户确认的本地 Agent Pack 导入；只接受声明和知识文件，不执行包内代码，也不自动授权；
- 新增跨行业操作方案、Agent Pack SDK、JSON Schema 和可复制模板。

### Safety

- 一个任务只绑定一个主 Agent Pack，Pack 内角色由 Agent Mesh 编排，MCP 与 Skills 只作为受控能力补充；
- Agent Pack 不得绕过工作区、权限、预算、恢复和 Proof-of-Done；外部发布、账号、投放与购买仍为独立审批边界。

## 1.0.4

### Added

- 新增合作伙伴验证中心，以内核、模型、工作区和真实交付四项状态显示当前就绪度；
- 新增 2 分钟只读检查和 5 分钟最小落盘检查，首次体验无需自行设计测试任务；
- 新增不含 API Key、完整工作区路径或对话内容的伙伴验证报告导出；
- 新增独立合作伙伴验证指南，统一体验、恢复、纠正和反馈口径。

### Changed

- 首页首次使用状态增加克制的验证入口，不改变既有任务空间和 Threadspace 主流程；
- README 版本状态与 Electron 客户端保持一致。

## 1.0.3

### Fixed

- Ollama native `/api/chat` endpoints are preserved instead of being rewritten to an invalid nested URL.
- Native Ollama NDJSON streaming and tool-call payloads are supported alongside OpenAI-compatible `/v1/chat/completions`.
- Missing local models and invalid Ollama endpoints now produce actionable diagnostics.
- The default Ollama address uses `localhost`, avoiding an empty IPv4-only Ollama instance when the active model service is bound to IPv6.
- Native Ollama runs now size `num_ctx` adaptively from 8K to 64K and use a bounded local output budget, preventing NOVA's system context from overflowing Ollama's 4096-token default.

本项目遵循“先记录真实能力，再发布版本”的原则。未完成签名、公证和真实端到端基准的构建均标记为 Preview。

## 1.0.2

### Fixed

- Evolution Lab 的“定时提出候选”不再只是保存开关；AgentOS 现会在应用空闲 10 分钟后执行首次本地扫描，之后每 6 小时最多扫描一次；
- 自动发现从近 30 天任务快照中识别失败恢复或重复工作流信号，并对同一批信号去重；
- 候选生成不调用模型、不预留 Token、不创建插件沙箱，也不会自动安装；准备、模型运行、验证和采纳仍需用户明确操作；
- Evolution Lab 会显示最近扫描、下一次扫描窗口与本轮状态，并在后台发现候选时实时刷新。

## 0.9.0-preview.29

### Added

- Electron / React 主交互壳与 .NET 8 AgentOS Bridge；
- Windows 与 macOS 共用 Electron UI、跨平台 AgentOS Bridge/Core，并提供 macOS arm64/x64 原生构建流水线；
- OpenAI、DeepSeek、Kimi、Ollama 与兼容模型端点；
- 输入附件、图片理解、Markdown 渲染与多轮 Threadspace；
- Ask、Plan、Build、Autopilot、Goal 模式；
- MCP、Skills、SSH、云开发与组件扩展坞；
- Agent Mesh、Worktree 隔离、Tournament 与 Council；
- Mission Charter、Proof-of-Done、证据账本与交付工作台；
- 插件式自进化实验室与硬预算控制。

### Changed

- 任务预算由静态硬截断调整为模式化、可观察的治理；
- 流式输出合并为单一事件，不再为每个字符生成执行流项目；
- 多轮任务、任务归档、权限确认、工作区路径和 Markdown 展示得到修复；
- Electron 桌面壳采用克制的实色界面，减少玻璃和装饰性视觉。

### Known limitations

- Preview 未签名，自动更新关闭；
- Mac Preview 未完成 Developer ID 签名、公证和 macOS 原生桌面控制；
- 复杂编码质量仍依赖底层模型、上下文编译和工具链成熟度；
- 1.0 GA 所需的 30×3 真实端到端基准尚未完成。
