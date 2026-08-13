# NOVA AgentOS A2A 与自进化插件技术白皮书

**版本：** 1.1.0-preview.16
**文档状态：** 基于当前代码实现的技术基线
**更新日期：** 2026-08-12
**适用对象：** 架构师、研发人员、安全审查人员、Agent Pack 作者、合作伙伴与技术决策者

---

## 0. 文档目的与结论先行

本文完整说明 NOVA AgentOS 当前版本中的两组核心能力：

1. **A2A（Agent-to-Agent）协作系统**：如何把一个目标拆成多个角色、多个工作包和依赖波次，怎样隔离执行、合并结果、交叉审查并形成可验证交付。
2. **插件式自进化系统**：如何从任务历史发现改进机会，在受限沙箱中调用模型生成声明式 Skill，经静态验证和人工审阅后安装，而不向模型暴露或修改 NOVA 核心代码。

当前实现的核心原则可以概括为：

> A2A 负责“多人协作把事做完”，Evolution Lab 负责“在安全边界内把工作方法沉淀成可停用插件”；两者都不能绕过工作区、审批、预算、证据和恢复机制。

### 0.1 当前已经实现

- 两类并行协作：只读分析型并行委派、带 Git 隔离与写入所有权的 Agent Mesh。
- 2～4 个工作包的 DAG 规划、依赖波次并行、独立 worktree、Patch 审核、确定性合并。
- Agent Mesh Council、Tournament Council、Independent Verification Council 等交叉审查机制。
- Evolution Lab 的候选发现、预算预留、声明式插件沙箱、模型生成、静态验证、人工采纳与 Skill 安装。
- Living Memory 的习惯候选、人工接受和 Skill 蒸馏。
- 模型、工具、写入路径、并行请求、桌面操作、网络访问等审批边界。
- 状态、决策、Patch 哈希、任务图、候选习惯和实验记录的本地持久化。

### 0.2 当前明确不做

- 不允许模型直接修改 NOVA 核心源代码或更新器。
- 不允许进化插件携带可执行代码、原生二进制、第三方依赖、密钥或网络客户端。
- 不允许后台无限调用模型、无限递归创建子 Agent 或绕过 Token 预算。
- 不允许未审阅的实验自动安装。
- 当前 A2A 是 **NOVA 内部协作契约与运行时**，不是对外宣称已经完整实现某一外部 A2A 标准协议。
- 当前 Agent Mesh 不是跨机器分布式集群；其隔离单位是本机 Git worktree 与受控工作区。

---

## 1. 总体架构

### 1.1 分层模型

| 层级 | 主要职责 | 关键组件 |
|---|---|---|
| 交互与任务层 | 接收用户目标、展示计划、请求授权、展示 Agent 进度与交付 | Electron UI、MainViewModel、AgentTaskGraphService |
| 目标与规划层 | 识别模式，形成角色图、工作包 DAG、依赖波次和验收条件 | AgentTaskGraphService、AgentMeshPlannerService |
| A2A 执行层 | 并行调用模型、隔离写入、导出 Patch、合并与复核 | ParallelAgentOrchestrator、AgentMeshService、WorkspaceToolHost |
| 审查与裁决层 | 对计划、候选实现、最终集成结果进行结构化裁决 | AgentMeshCouncilService、TournamentCouncilService、IndependentVerificationCouncilService |
| 学习与进化层 | 从历史任务提炼偏好和能力改进候选，生成声明式插件 | LivingMemoryService、EvolutionLabService、SkillRegistryService |
| 治理与证据层 | 审批、预算、哈希、状态、恢复、决策账本 | Approval Broker、Evidence Ledger、Recovery、Atomic Persistence |

### 1.2 两条主链路

**A2A 交付链路**

`用户目标 → 任务模式 → 角色/工作包规划 → 用户审批 → 并行执行 → Patch/结果汇总 → 委员会审查 → 主工作区应用 → 验证与交付`

**插件式自进化链路**

`本地任务历史 → 候选发现 → 用户准备实验 → 沙箱生成 → 静态验证 → 人工审阅 → 安装为可停用 Skill → 后续任务调用`

两条链路共享同一组治理原则：最小权限、显式边界、预算硬限制、真实证据、可回滚、不可悄悄扩大能力。

---

## 2. A2A 的定义与边界

NOVA 中的 A2A 指多个模型角色或 Agent 执行单元围绕同一个任务进行分工、依赖传递、结果合并和交叉审查。它分为两个强度等级。

### 2.1 分析型并行委派

适用于市场研究、方案比较、风险识别、代码审查意见等不需要直接修改文件的任务。

- 工具名：`delegate_parallel_tasks`。
- 每次接受 2～4 个独立子任务。
- 每个 Worker 只返回结构化分析，不拥有工具，不得声称已经查看本地文件或执行外部动作。
- 支持 OpenAI Responses 与 Chat-compatible 提供方，包括 DeepSeek、Kimi、自定义接口和 Ollama。
- 主 Agent 负责整合结果；Worker 之间没有共享写入。
- 创建额外模型请求，必须获得用户授权，并明确提示额外 Token 成本。
- 标题最大 80 字符，任务指令最大 8,000 字符。

这一层强调低风险并行思考，不适合需要多角色共同修改工程文件的任务。

### 2.2 Agent Mesh 工程型协作

适用于编码、文档体系改造、跨模块实现、测试补全等需要多个 Agent 写文件的任务。

- 规划器生成 2～4 个工作包。
- 每个工作包必须声明独占 `owned_paths`。
- 工作包通过依赖构成有向无环图（DAG）。
- 至少有一个依赖波次包含两个以上可并行工作包，否则不视为真实 Mesh。
- 每个 Worker 在独立 Git worktree 中执行。
- 每个 Worker 只允许写入自己的路径范围。
- 每波结果以 Patch 形式进入集成 worktree；按照确定顺序应用并提交。
- 所有波次结束后运行验证、工程复核与委员会裁决。
- 只有满足完整性、验证和哈希条件，最终 Patch 才能由用户批准应用到主工作区。

### 2.3 与外部 A2A 协议的关系

当前版本的 A2A 是内部运行时能力，包括角色契约、DAG、工具边界、隔离写入、结果协议和委员会裁决。NOVA 目前通过 MCP、Extension Gateway、Hook 与 Agent Pack 扩展外部能力，但尚未把自身声明为某一外部 A2A 标准的完整 Server/Client 实现。

未来若接入外部 A2A 协议，应把外部 Agent 视为不可信远端执行者，继续经过身份、能力清单、预算、审批、输入脱敏和交付验证，不应直接继承本地工作区写权限。

---

### 2.4 A2A 运行时对象模型

NOVA 的 A2A 不是一个模糊的“多开几个模型”功能，而是一组有固定责任、可持久化、可审计的运行时对象。当前核心对象如下：

| 对象 | 关键字段 | 责任 |
|---|---|---|
| `ParallelAgentTask` | `Title`、`Instruction` | 描述一个只读、相互独立的分析任务 |
| `AgentMeshWorkPackage` | `Id`、`Title`、`Instruction`、`OwnedPaths`、`DependsOn` | 描述一个可写工程工作包及其独占路径和依赖 |
| `AgentMeshPlan` | `Strategy`、`Packages` | 保存编排策略，并把依赖图拓扑排序为执行波次 |
| `AgentMeshPackageResult` | Worker 结果、状态、Patch、哈希、增删行数 | 保存单个工作包的真实产出与边界审计结果 |
| `AgentMeshRunResult` | 基线 HEAD、集成 HEAD、波次、验证、复核、组合 Patch、产物目录 | 表示一次完整 Mesh 的可验证结果 |
| `AgentMeshCouncilDecision` | Provider、Model、Verdict、Confidence、Summary、RawResponse | 表示独立委员会对集成结果的裁决 |
| `AgentMeshApplyResult` | Applied、ExitCode、Detail | 表示最终是否把组合 Patch 应用到主工作区 |

这些对象把“思考”“实施”“验证”“裁决”“应用”分开。Worker 的文字回答不能直接替代 Patch；Council 的 ACCEPT 也不能跳过路径、哈希和 Git 基线检查。

### 2.5 内部消息与事件契约

当前 A2A 采用进程内强类型调用与 `AgentRuntimeEvent` 事件流，而不是外部消息队列。可以把它理解为一套本地可靠消息语义：

| 消息类别 | 典型载荷 | 消费者 | 可见结果 |
|---|---|---|---|
| 规划请求 | 目标、Proof-of-Done、上下文包、工程快照 | Mesh Planner | `AgentMeshPlan` |
| Worker 指令 | 工作包、允许写入范围、集成基线、轮次与预算 | 模型 Runtime / Tool Host | Worker 回答、文件变更、Patch |
| 执行事件 | BatchStarted、Thinking、ToolCompleted、Message、BatchCompleted | 主界面与任务图 | 执行过程、角色状态、阶段产出 |
| 波次提交 | 已完成工作包、Patch、当前 Integration HEAD | Mesh Service | 波次提交、更新后的集成基线 |
| 验证请求 | 集成工作区、验证命令、组合 Patch | Engineering Service / Council | 测试结果、工程复核、裁决 |
| 应用请求 | Mesh ID、Base HEAD、Patch SHA-256、用户批准 | Mesh Service | `AgentMeshApplyResult`、`decision.json` |

事件流只展示阶段级信号。纯文本流式增量会在同一角色节点内合并，避免产生大量“流式输出”噪声。失败事件必须带错误原因和可恢复线索，不能只把角色状态留在“思考中”。

### 2.6 一次完整 A2A 的端到端时序

1. 用户给出目标，AgentOS 固化目标、工作区、Proof-of-Done 与权限边界。
2. 上下文编译器只选择高信号文件、知识与历史证据，形成有大小上限的上下文包。
3. 系统判断任务是否真正可并行。只读调研走分析型委派；跨文件工程变更走 Agent Mesh。
4. Mesh Planner 只返回 JSON 计划，不调用工具、不写文件。
5. 计划解析器验证工作包数量、ID、依赖图、路径所有权和至少一个可并行波次。
6. UI 展示策略、Worker 数、独占路径、模型成本和审批影响；用户批准后才启动。
7. 系统记录源仓库 `BaseHead`，创建集成 worktree，再按波次为 Worker 创建隔离 worktree。
8. Worker 在 `OwnedPaths` 内实施。工具宿主在每次写入时再次检查允许范围。
9. Worker 完成后导出 Patch，计算 SHA-256、增删行数并校验每个变更路径。
10. 同一波次全部结束后，系统按稳定顺序把 READY Patch 应用到集成 worktree 并提交波次。
11. 下游 Worker 从新的 Integration HEAD 创建工作区，因此读取到的是上游真实文件，而非转述摘要。
12. 所有波次完成后，系统形成 `combined.patch`，执行最窄相关构建或测试和本地工程复核。
13. Agent Mesh Council 对目标、Proof-of-Done、计划、验证证据和组合 Patch 做只读对抗审查。
14. 只有资格条件、委员会裁决和用户应用审批同时满足时，系统才检查主工作区 HEAD 与 Patch 哈希。
15. `git apply --check` 通过后应用到主工作区；NOVA 不自动替用户提交。
16. 最终把计划、结果、验证、裁决、哈希和应用状态写入本地证据目录，供恢复和审计。

### 2.7 A2A 的硬不变量

以下条件不是提示词建议，而是运行时必须维护的不变量：

- 主工作区是事实源，Mesh 启动和应用时都必须验证 Git 基线。
- Worker 只能写入声明的工作区相对路径；空路径、绝对路径、越界路径和重叠路径均无效。
- 同一共享文件只能由一个工作包拥有。
- 工作包依赖必须是有向无环图；没有可执行节点时立即判定环依赖。
- 后续波次必须建立在前序波次已经集成的真实 HEAD 上。
- 单个 Worker 成功不等于 Mesh 成功；所有工作包必须 READY。
- 组合 Patch 为空时不能应用，验证失败时不能声称完成。
- 模型输出、Council 输出和用户批准是不同证据，不能互相替代。
- Apply 前必须复核 `BaseHead`、Patch SHA-256 与 `git apply --check`。
- A2A 不能递归无限派生 Worker；数量、模型轮次、Token 和工具调用受宿主预算治理。

### 2.8 与标准化外部 A2A 的能力差距

NOVA 已具备内部编排内核，但尚未完成跨厂商、跨机器的通用 A2A 网络协议。下表严格区分现状与下一步：

| 能力 | 当前状态 | 推荐实现 |
|---|---|---|
| Agent 身份与能力卡 | Agent Pack 有本地身份、角色与能力声明；未暴露标准远程 Agent Card | 由 Extension Gateway 生成只读能力卡，并做版本与签名校验 |
| Agent 发现 | 本地 Registry 可发现已安装 Agent；无跨组织发现 | 增加受信目录、显式导入和租户隔离 |
| 远程任务提交 | 未提供通用远程 `task/send` 兼容层 | 用 A2A Adapter 把远程任务映射为内部 Task Graph，不绕过审批 |
| 流式状态与订阅 | 本地 `AgentRuntimeEvent` 已支持阶段事件 | 映射为可恢复的 SSE/WebSocket 事件，增加游标、重放与背压 |
| Artifact 交换 | 本地交付物、Patch、哈希和清单已存在 | 只传签名清单和按需下载地址；禁止默认上传完整工作区 |
| 身份、鉴权与租户 | 本地用户权限模型 | 增加远程主体身份、最小能力令牌、租户边界和撤销机制 |
| 取消与幂等 | 本地 CancellationToken 和任务持有者机制已部分具备 | 为远程请求增加 Idempotency Key、租约、心跳、取消确认和过期处理 |
| 跨机器调度 | 未实现 | 在本地 Mesh 之上增加独立调度层，不把远程 Worker 直接接入文件系统 |

推荐架构是“外部协议适配器 → 身份与能力协商 → AgentOS Task Graph → 现有 Mesh / Runtime → 证据与交付层”。外部 Agent 不应直接获得 `WorkspaceToolHost`；它只能提交受约束结果或 Artifact，由本地宿主验证后进入集成链。

---

## 3. Agent 角色图与任务状态

### 3.1 角色图

`AgentTaskGraphService` 根据运行模式生成角色图：

| 模式 | 典型角色链 |
|---|---|
| Ask | Analyst |
| Plan | Analyst → Planner → Reviewer |
| Build | Analyst → Planner → Implementer → Reviewer |
| Autopilot | Planner → Researcher/Analyst → Implementer → Reviewer → Adjudicator → Merge Guardian → Adversarial Reviewer → Integrator |
| Goal | Goal Explorer → Mission Charter → 执行角色 → Goal Auditor / Reviewer |

角色图不是装饰性 UI。模型调用、工具请求、阶段产出和错误事件会更新节点状态，并持久化到任务图存储中。

### 3.2 状态模型

常见节点状态包括：

- `ready`：已创建、尚未进入执行。
- `pending`：等待前置依赖或审批。
- `running`：正在调用模型或工具。
- `waiting`：等待用户、外部服务或下一阶段。
- `completed`：已形成阶段产出。
- `failed`：阶段失败，保留错误和恢复线索。

纯文本流式增量不会反复创建时间线事件，避免“每个字都是一条流式输出”的噪音。任务图重点展示可理解的阶段变化。

### 3.3 本地持久化

任务图默认保存在：

`%LOCALAPPDATA%\NOVA\agent-os\task-graphs`

持久化的价值是：程序重启后仍可恢复阶段、角色状态和执行脉络，而不是把多 Agent 过程只存在于内存或聊天窗口中。

---

## 4. Agent Mesh 规划契约

### 4.1 规划器输出

规划器必须返回结构化 JSON。示意：

```json
{
  "strategy": "接口与界面并行，验证工作包在两者集成后执行",
  "packages": [
    {
      "id": "runtime-api",
      "title": "实现运行时接口",
      "instruction": "在限定模块内实现接口并补充最小验证。",
      "owned_paths": ["NovaDesktop/Services/Runtime/"],
      "depends_on": []
    },
    {
      "id": "desktop-ui",
      "title": "完成桌面交互",
      "instruction": "消费新接口并补充清晰状态。",
      "owned_paths": ["NovaDesktop.Electron/src/"],
      "depends_on": []
    },
    {
      "id": "integration-tests",
      "title": "补充集成测试",
      "instruction": "验证运行时与界面契约。",
      "owned_paths": ["NovaDesktop.SmokeTests/"],
      "depends_on": ["runtime-api", "desktop-ui"]
    }
  ]
}
```

### 4.2 字段约束

| 字段 | 约束 |
|---|---|
| `id` | 符合 `^[a-z][a-z0-9-]{1,31}$` |
| `title` | 2～100 字符 |
| `instruction` | 20～6,000 字符 |
| `owned_paths` | 1～16 条，必须是工作区相对路径 |
| `depends_on` | 只能引用已声明包；禁止自依赖与环依赖 |
| 工作包数量 | 2～4 个 |

### 4.3 写入范围安全检查

以下路径会被拒绝：

- 绝对路径。
- 包含 `..` 的越界路径。
- `.git` 及其子路径。
- 含 `*`、`?` 的模糊通配路径。
- `bin/`、`obj/`、`node_modules/` 等构建或依赖目录。
- 与其他工作包存在父子覆盖或同路径重叠的路径。

路径所有权不仅在规划阶段检查，还由 `WorkspaceToolHost` 在实际写入时再次执行。即使 Worker 提示词被模型忽略，工具宿主仍会拒绝越权写入。

### 4.4 依赖波次

规划器对依赖图进行拓扑排序，形成波次：

- 同一波次的工作包可以并行。
- 后续波次必须等待前置波次进入集成分支。
- 发现环依赖、未知依赖或无法解析的节点时，计划无效。
- 后续 Worker 从包含前一波提交的 HEAD 创建 worktree，因此能看到上游真实产出，而不是只收到一段文字摘要。

---

## 5. Agent Mesh 执行与集成

### 5.1 执行前置条件

Mesh 运行前必须满足：

1. 当前工作区是 Git 仓库。
2. 主工作区无未提交改动。
3. 用户确认工作包、路径范围与额外模型消耗。
4. 规划 DAG 通过结构与所有权检查。

拒绝在脏工作区启动，是为了避免把用户未提交修改混入 Agent Patch，或在集成时覆盖用户工作。

### 5.2 隔离结构

运行时先从主工作区当前 HEAD 创建集成 worktree，再为每个工作包创建独立 worktree：

`主工作区（只作为最终落点）`

`└─ 集成 worktree`

`   ├─ Wave 1 / Worker A worktree`

`   ├─ Wave 1 / Worker B worktree`

`   └─ Wave 2 / Worker C worktree`

Worker 不直接写主工作区。隔离可减少并行冲突，并为审查与失败恢复保留明确边界。

### 5.3 Worker 产出

每个 Worker 完成后：

- 检查工作树变更。
- 导出统一 Patch。
- 审计每个变更路径是否属于 `owned_paths`。
- 记录摘要、状态、Patch 大小和失败原因。
- 单工作包 Patch 上限为 1.5 MB。

不满足路径所有权、Patch 为空、执行失败或交付声明不完整时，该工作包不得标记为 READY。

### 5.4 波次集成

同一波次的 Patch 在并行执行结束后，按确定顺序应用到集成 worktree，并形成波次提交。这样可以：

- 尽早发现 Patch 冲突。
- 保持后续波次的基线一致。
- 让依赖 Worker 读取上游真实文件。
- 为最后的组合 Patch 提供可审计提交链。

组合 Patch 上限为 3 MB。超过上限通常意味着任务拆分不合理、包含生成物或修改范围过大，应重新规划。

### 5.5 验证与工程复核

集成完成后可运行最窄相关构建或测试，并进行工程复核。最终“可应用”条件至少包括：

- 所有工作包状态为 READY。
- 组合 Patch 非空。
- 已配置的验证步骤通过。
- 工程复核没有阻断项。
- Council 返回可解析的结构化裁决。

过程活跃度、模型说“完成了”或 Worker 数量很多，都不能代替验证证据。

### 5.6 应用到主工作区

应用前再次检查：

1. 主工作区仍然干净。
2. HEAD 与 Mesh 启动时相同。
3. 组合 Patch 的 SHA-256 与记录一致。
4. `git apply --check` 通过。
5. 用户批准应用。

通过后只把 Patch 应用到主工作区，不自动替用户提交。决定记录会写入 `decision.json`，包括 Council 结论、置信度、摘要、Patch 哈希和是否已应用。

---

### 5.7 产物目录与证据链

每次 Mesh 的默认产物根目录为：

`%LOCALAPPDATA%\NOVA\agent-mesh\<mesh-id>`

`mesh-id` 由时间、任务安全名称和随机后缀组成，避免不同运行互相覆盖。目录中的核心证据包括：

| 证据 | 含义 |
|---|---|
| 规划快照 | 当次使用的策略、工作包、依赖和路径所有权 |
| 单包结果 | Worker 状态、说明、Patch 路径、Patch SHA-256、增删行数 |
| `combined.patch` | 从 `BaseHead` 到 `IntegrationHead` 的最终组合变更 |
| 验证结果 | 验证命令、退出码、输出摘要与是否通过 |
| 工程复核 | 本地工程完整性评分、阻断项和风险 |
| Council 原始响应 | 结构化裁决及未经美化的模型原文 |
| `decision.json` | 最终 Verdict、Confidence、摘要、Patch 哈希、应用状态与时间 |

证据链应能回答四个问题：谁负责了什么、实际改了什么、如何证明可用、谁批准落到主工作区。交付界面可以简化展示，但底层证据不能被“简洁 UI”删除。

### 5.8 资格判定、取消与清理

`AgentMeshRunResult.IsEligible` 的当前代码条件是：

```text
全部工作包 Status == READY
AND combined.patch 非空
AND（没有配置验证 OR 验证通过）
```

这是进入最终裁决与应用流程的最低资格，不代表系统已经自动批准。Council、工具审批和用户确认仍是后续闸门。

取消或失败时，系统应遵循以下清理顺序：

1. 停止向 Worker 发起新的模型轮次和工具调用。
2. 等待已在安全边界内执行的操作返回，或由 CancellationToken 终止。
3. 保留已经生成的 Patch、哈希、错误和工作包状态。
4. 不应用尚未完成的波次，不修改主工作区。
5. 回收 Worker worktree；需要诊断时保留 Mesh 产物目录。
6. 将任务图节点改为 failed、waiting 或 cancelled，并写明下一步可恢复动作。

### 5.9 A2A 失败域

| 失败域 | 典型症状 | 是否可安全重试 | 正确恢复点 |
|---|---|---:|---|
| Planner | JSON 为空、字段缺失、依赖环、路径重叠 | 是 | 仅重跑规划，不重复已批准实施 |
| Approval | 用户未批准额外 Worker 或写入范围 | 是 | 保留计划，等待确认或降级为单 Agent |
| Worker Model | 超时、上下文过大、模型拒绝或输出不完整 | 是 | 重试失败工作包，必要时压缩上下文 |
| Worker Tool | 越权写入、命令失败、附件不可读 | 有条件 | 修正范围或能力后重跑该包 |
| Wave Integration | Patch 冲突、Patch 过大、上游包未 READY | 有条件 | 回到该波次，不继续下游波次 |
| Verification | 构建或测试失败 | 是 | 保留 Integration worktree，生成修复包 |
| Council | 返回格式不可解析、REJECT | 是 | 重试只读审查或进入人工审阅；不得默认 ACCEPT |
| Apply | 主 HEAD 变化、Patch 哈希变化、`git apply --check` 失败 | 否，需重新校准 | 重新读取主工作区并决定重建或人工合并 |
| Host Lease | 任务由另一宿主持有、epoch 不一致 | 有条件 | 由任务租约与检查点决定恢复主体，禁止双写 |

失败恢复的核心原则是“只重跑最小失败单元”。如果三个 Worker 中只有一个超时，不应重新消耗另外两个已经形成证据的 Worker；如果 Apply 阶段失败，也不应重新调用所有模型。

### 5.10 建议的服务边界

从实现责任看，A2A 应维持以下服务边界：

| 服务 | 只负责 | 不负责 |
|---|---|---|
| `AutomaticAgentPlanner` | 生成分析型并行角色 | 文件写入与工程合并 |
| `ParallelAgentOrchestrator` | 并行调用只读 Worker、汇总答案与事件 | 决定工程完成或直接落盘 |
| `AgentMeshPlannerService` | 生成并校验工程 DAG | 实施代码 |
| `AgentMeshService` | worktree、波次、Patch、验证、应用与证据 | 自行改变用户目标 |
| `WorkspaceToolHost` | 在每次真实工具调用时执行资源边界 | 信任模型自行守规 |
| `AgentMeshCouncilService` | 对组合结果做只读对抗审查 | 写文件、执行命令或替用户批准 |
| `AgentTaskGraphService` | 保存角色、阶段与恢复状态 | 伪造 Worker 产出 |

边界清楚后，UI 可以把不同服务的事件汇聚成一个自然过程，但不能把规划、实施、审查和应用重新揉成一次不可解释的模型调用。

---

## 6. A2A 委员会与竞争式执行

### 6.1 Agent Mesh Council

Council 读取经过脱敏和边界化的集成结果，输出结构化 ACCEPT/REJECT、置信度和摘要。解析失败或返回非结构化自然语言时，不应把它当作权威通过。

### 6.2 Worktree Tournament

对于高风险或存在多种实现路线的任务，NOVA 可让多个候选实现进入隔离 worktree。每个候选独立产生 Patch 与证据，再由 Tournament Council 比较：

- 是否真正解决目标。
- 修改范围是否受控。
- 验证是否可复现。
- 是否引入回归和安全风险。
- 哪个候选更适合作为最终集成基线。

### 6.3 Independent Verification Council

独立验证层不参与最初实现，重点进行对抗性复核，降低“实现者自己证明自己”的偏差。它可检查：

- 交付物是否存在。
- 验证是否与目标直接相关。
- 是否隐瞒失败或未知项。
- 是否存在表面完成、实际未落盘的情况。

### 6.4 递归与并行上限

子 Agent 不能无限递归创建新的并行 Worker。并行数量、工具权限和模型请求均受宿主控制。分析型委派限制为 2～4 个 Worker；Mesh 工作包同样限制为 2～4 个，以控制复杂度和 Token 扩张。

---

## 7. A2A 审批、预算与故障恢复

### 7.1 必须审批的动作

与 A2A 直接相关的典型审批包括：

- 创建 2～4 个额外模型 Worker。
- 规划与启动 Agent Mesh。
- 文件写入、替换和命令执行。
- 网络请求、后台网页研究、MCP 调用。
- 桌面窗口激活、键盘输入和点击。
- 把组合 Patch 应用到主工作区。

审批说明应告诉用户“将发生什么、影响哪些路径、会增加多少模型请求、是否可撤销”，而不是只显示抽象的“允许/拒绝”。

### 7.2 Token 与模型成本

A2A 会放大请求数量。NOVA 的原则是：

- 只在子任务可以真正独立时并行。
- 只读分析 Worker 不重复携带不必要的完整上下文。
- 工程 Mesh 用路径所有权减少重复扫描。
- 把上游产出通过 Git 文件传递，而不是在每轮 Prompt 中重复全文。
- 用户可以拒绝并行，退回单 Agent 路径。

### 7.3 常见失败与恢复

| 失败 | 处理 |
|---|---|
| 工作区不是 Git 或有未提交修改 | 不启动 Mesh；提示用户提交、另存或改用单 Agent |
| 路径所有权重叠 | 计划无效，要求重新拆分 |
| Worker 越权写入 | 工具宿主拒绝，工作包失败 |
| Patch 冲突 | 集成波次失败，不应用到主工作区 |
| 主 HEAD 已变化 | 最终应用拒绝，防止基线错配 |
| Patch 哈希不一致 | 视为完整性失败 |
| Council 输出不可解析 | 不自动通过，保留结果供重试或人工审查 |
| 模型超时或部分 Worker 失败 | 保留工作包状态和证据，可缩小任务或重试失败包 |

### 7.4 任务租约、宿主与 epoch

A2A 任务可能跨越较长时间，也可能在程序重启后恢复。`AgentSupervisorService` 使用本地任务租约防止两个宿主同时推进同一任务：

| 字段 | 含义 |
|---|---|
| `TaskId` | 稳定任务标识 |
| `OwnerBootId` | 当前持有任务的 NOVA 进程启动标识 |
| `Epoch` | 每次重新取得执行权递增的世代号 |
| `State` | Active、Paused、Recoverable、Completed、Failed 等状态 |
| `Checkpoint` | 最近一个持久化阶段边界 |
| `ExecutionSequence` | 当前宿主内的执行序号 |

磁盘文件锁提供进程级互斥，持久化租约提供重启后的解释能力。出现“任务已由宿主持有”时，系统不能简单覆盖旧租约，否则两个 Runtime 可能同时写入或重复产生外部副作用。

正确的恢复策略是：

1. 检查旧宿主进程是否仍存活以及文件锁是否仍被持有。
2. 如果检查点已经明确终结，例如“模型完成”或“任务完成”，新宿主可以取得新的 recovery epoch，完成收尾而不是重跑模型。
3. 如果旧宿主仍在运行，当前请求进入等待、取消旧任务或创建独立任务，不能双写。
4. 如果旧宿主异常退出，任务转为 Recoverable，从最后阶段检查点继续。
5. 外部动作必须记录副作用回执；即使恢复，也不能重复发送、发布、删除或付费。

### 7.5 A2A 预算计算建议

A2A 的预算不能只限制“整个任务最多一轮”，否则一个正常的 2～4 Worker 计划在启动前就会失败。预算应至少拆成：

| 预算项 | 计算方式 |
|---|---|
| Planner | 1 次结构化计划请求，失败可允许有限重试 |
| Worker | 当前波次 Worker 数 × 每个 Worker 最大轮数 |
| Integration Repair | 仅在 Patch 冲突或验证失败时预留，不应默认全部消耗 |
| Council | 1 次只读裁决；格式错误可使用一次结构修复请求 |
| Tool Calls | 按工作包声明和全局上限双重计数 |
| Token | 按角色分配输入与输出上限，未使用额度不应强制消耗 |

运行前应展示“预计最多增加多少模型请求”，但实际计费按真实调用记录。失败包的重试预算不能迫使已成功包重新执行。

---

## 8. 自进化系统的三种能力

NOVA 把“自进化”拆成三个不同问题，避免把个性化、专业 Agent 设计和核心自修改混为一谈。

### 8.1 Living Memory：习惯与偏好学习

目标是让 NOVA 更理解用户的协作方式，例如偏好的执行模式、结果优先、低打扰、连续对话和交付风格。

- 最多分析约 120 条本地任务快照和对话。
- 从重复证据中形成习惯候选。
- 候选状态为 Proposed、Accepted 或 Rejected。
- 只有用户明确接受的习惯才进入后续模型上下文。
- 当前指令与当前工作区事实始终高于长期偏好。
- 接受的习惯可蒸馏成 Skill 候选，再由用户安装。

数据默认保存在：

`%LOCALAPPDATA%\NOVA\living-memory\profile.json`

### 8.2 Evolution Lab：能力插件实验

目标是把可重复、可审查的工作方法封装为声明式 Skill，而不是修改核心程序。

- 可从近期任务摩擦中提出实验候选。
- 用户决定是否准备实验。
- 模型只能在专用沙箱修改四类声明文件。
- 实验必须经过静态验证和人工审阅。
- 通过后安装为可停用 Skill。

### 8.3 Agent Pack Workshop：行业 Agent 设计

Agent Pack Workshop 用于设计专业 Agent 的角色、流程、能力、输入输出契约与启动引导。它属于专业能力编排，不等同于 Evolution Lab。两者的关系是：

- Workshop 回答“这个行业 Agent 应如何工作”。
- Evolution Lab 回答“某个重复工作方法是否值得沉淀为安全插件”。
- Living Memory 回答“用户希望 NOVA 以什么方式协作”。

---

## 9. Evolution Lab 状态机

### 9.1 实验状态

`Proposed → Ready → Running → Evaluating → Passed → Adopted`

失败与人工分支：

- `Running / Evaluating → Failed`
- `Proposed / Ready / Passed → Rejected`
- `Failed → 重试或放弃`

状态定义：

| 状态 | 含义 |
|---|---|
| Proposed | 本地发现或用户提出的候选，尚未创建沙箱 |
| Ready | 沙箱和初始声明文件已准备，可请求模型 |
| Running | 模型正在限定工具和目录内工作 |
| Evaluating | 正在进行声明、大小、哈希和安全验证 |
| Passed | 静态验证通过，等待人工审阅与采纳 |
| Failed | 模型或验证失败，保留阻断原因 |
| Adopted | 已安装为可停用 Skill |
| Rejected | 用户拒绝，不进入能力注册表 |

### 9.2 策略默认值

| 策略 | 默认值 | 可配置范围 |
|---|---:|---:|
| 插件式进化 | 关闭 | 开 / 关 |
| 定时提出候选 | 关闭 | 开 / 关 |
| 单次实验 Token 上限 | 16,000 | 2,000～64,000 |
| 每月 Token 上限 | 100,000 | 5,000～2,000,000 |
| 每周实验上限 | 3 | 1～20 |
| 单次模型轮数 | 4 | 1～12 |

单次实验预算不得大于月度预算。预算不是提示词建议，而是运行前的硬预留。

---

## 10. Evolution Lab 候选发现

### 10.1 触发条件

只有同时开启“插件式进化”和“定时提出候选”时，系统才会执行发现。

- 首次候选扫描在开启后约 10 分钟进入窗口。
- 后续扫描间隔约 6 小时。
- 存在正在运行或等待审阅的实验时，不叠加候选。
- 每周实验次数或月度 Token 已达上限时，不创建新候选。

### 10.2 数据范围

发现阶段只读取本地任务快照元数据，不复制核心源码、不自动调用模型、不访问外部网络。它关注最近 30 天且存在有效工作区的非归档任务。

典型信号包括：

- 失败、预算耗尽、停滞或取消。
- 相似目标重复出现两次以上。
- 同类说明反复出现，可能需要固定工作流。

候选评分以摩擦信号和任务数量为基础，例如 `friction × 10 + task count`。候选使用 SHA-256 指纹去重，并检查相同工作区与目标在 30 天内是否已经出现。

### 10.3 发现阶段不做的事情

- 不自动进入模型生成。
- 不自动安装。
- 不读取密钥。
- 不复制 NOVA 核心源代码。
- 不在后台悄悄消耗 Token。

---

## 11. 插件沙箱与声明式 SDK

### 11.1 沙箱路径

实验状态：

`%LOCALAPPDATA%\NOVA\evolution-lab\state.json`

实验沙箱：

`%LOCALAPPDATA%\NOVA\evolution-lab\plugin-workspaces\<experiment-id>`

### 11.2 允许的文件

每个实验只能包含：

| 文件 | 用途 |
|---|---|
| `nova.plugin.json` | 插件身份、版本、类型、入口、权限和能力声明 |
| `SKILL.md` | 可被模型调用的工作方法与安全边界 |
| `README.md` | 面向用户的说明、适用场景和验证方法 |
| `NOVA_PLUGIN_SDK.md` | 实验内只读参考契约 |

禁止新增脚本、动态库、可执行文件、包管理依赖、凭据文件或网络客户端。

### 11.3 Manifest 基线

```json
{
  "schemaVersion": 1,
  "id": "evolved.<unique-id>",
  "version": "0.1.0",
  "type": "instruction-extension",
  "entry": "SKILL.md",
  "permissions": [],
  "capabilities": ["task-guidance"],
  "generatedBy": "Evolution Lab"
}
```

`permissions` 必须为空。自进化插件只能改进指导方法，不能自行要求文件系统、桌面、网络、更新器或凭据权限。

### 11.4 准备阶段

用户批准准备实验后，服务创建沙箱和四个初始文件，并记录每个文件的 SHA-256 基线。源工作区只能作为任务背景，不会被复制到实验目录，核心代码也不会出现在模型工具范围中。

---

## 12. 模型运行与预算预留

### 12.1 可运行状态

只有 Ready、Running 或可重试的 Failed 实验可以进入运行。Passed、Adopted 和 Rejected 不允许继续消耗模型预算。

### 12.2 预算算法

- 整个实验预算在运行前预留。
- 月度已使用 Token 在预留时增加。
- 模型轮数被限制在 1～12。
- 每轮最大输出近似为 `单次预算 ÷ 轮数`，并限制在 512～4,096 Token。
- Failed 实验可重试，但重试会再次受月度与单次预算检查，不应把失败重试视为免费调用。

### 12.3 模型提示边界

Evolution Lab 的运行提示明确要求：

- 只在当前实验沙箱工作。
- 先读取 SDK 和已有 `SKILL.md`。
- 必须对 `SKILL.md` 产生实质性、可审阅变更。
- 不读取个人记忆、生产力统计、知识图谱、普通交付物、MCP 或网络。
- 不写入源工作区。
- 不生成可执行代码、依赖或凭据。
- 不改变 `permissions: []`。
- 不绕过审批。
- 安装动作与模型生成分离，由用户另行决定。

### 12.4 工具白名单

Evolution Runtime 只暴露限定插件文件的读取、写入和替换能力。普通工作区工具、桌面控制、网络、记忆、知识库、并行编排和命令执行工具不会进入实验运行时。

这是双层限制：提示词说明行为边界，工具宿主强制资源边界。安全性不能只依赖模型“听话”。

---

## 13. 静态验证与人工采纳

### 13.1 静态验证规则

实验完成后执行声明式验证：

- 插件总大小不超过 2 MB。
- 只能存在四个允许文件。
- 不允许删除基线文件。
- `SKILL.md` 必须发生变化。
- Manifest 必须为 schemaVersion 1、type `instruction-extension`、entry `SKILL.md`。
- `permissions` 必须为空。
- `SKILL.md` 长度为 120～24,000 字符。
- 必须明确包含“不得扩大权限”和“人工确认”等安全说明。
- 禁止出现绕过审批、关闭安全、读取密钥、窃取凭据或外传数据等指令。

系统记录文件哈希差异、变更类型和大小，形成验证证据。

### 13.2 采纳流程

只有 Passed 且 `VerificationPassed=true` 的实验可以采纳。用户点击采纳时会再次验证：

- 如果人工审阅期间文件发生变化，旧验证结果失效，必须重新验证。
- 通过后由 `SkillRegistryService.InstallFromFolderAsync` 安装为 Skill。
- 安装后的 Skill 可禁用或卸载。
- 核心程序不被修改。

### 13.3 拒绝与回滚

用户可拒绝候选或已通过实验。拒绝不会改变核心或能力注册表。已安装插件的回滚方式是禁用或卸载 Skill，而不是回滚 NOVA 二进制。

---

## 14. Living Memory 与 Skill 蒸馏

### 14.1 候选生成

Living Memory 从本地任务历史寻找重复协作信号，例如：

- 常用执行模式。
- 常用模型或提供方。
- 结果优先的交付方式。
- 是否偏好少打断、连续推进。
- 是否重视 UI/UX 或证据验证。

通常至少需要两次重复证据，置信度上限约 0.96，避免把一次偶然操作固化为长期偏好。

### 14.2 人工确认

候选不会自动生效。用户可以接受或拒绝；只有 Accepted 项进入后续上下文。当前任务的明确要求始终可以覆盖长期偏好。

### 14.3 蒸馏为 Skill

系统可选取最多 8 条已接受习惯，生成带指纹 ID 的个人工作流 Skill。Skill 安装仍通过 SkillRegistry，并且不能扩大现有权限。

Living Memory 与 Evolution Lab 的关键区别：

| 维度 | Living Memory | Evolution Lab |
|---|---|---|
| 输入 | 本地任务和对话习惯 | 重复摩擦或用户提出的能力改进目标 |
| 产出 | 偏好候选、个人工作流 Skill | 声明式能力插件 |
| 模型调用 | 分析/蒸馏时可调用 | 仅在用户准备实验并预留预算后调用 |
| 安装 | 用户确认 | 静态验证 + 用户采纳 |
| 核心修改 | 不允许 | 不允许 |

---

## 15. 安全模型

### 15.1 威胁假设

系统假设模型可能：

- 误解路径范围。
- 生成不完整或虚假的完成声明。
- 尝试调用额外工具。
- 在并行合并时产生冲突。
- 生成带有越权意图的插件文本。
- 在长任务中超时、返回非结构化内容或部分失败。

因此模型输出不直接等于可信执行结果。

### 15.2 主要控制

| 风险 | 控制 |
|---|---|
| Worker 修改不属于自己的文件 | owned_paths + 工具宿主二次校验 |
| 覆盖用户未提交改动 | Mesh 只在干净 Git 工作区启动，最终再次校验 |
| Patch 被篡改 | SHA-256 + `git apply --check` |
| 并行请求失控 | 2～4 Worker 上限 + 用户审批 |
| 后台无限 Token | 单次、月度、每周、轮数硬预算 |
| 自进化修改核心 | 独立沙箱 + 只允许四个声明文件 |
| 插件扩大权限 | Manifest 权限必须为空 + 采纳前重验 |
| 模型输出虚假完成 | 工具证据、文件存在、构建/测试、委员会复核 |
| 密钥泄漏 | Worker/Council 输入脱敏；插件禁止凭据与网络客户端 |
| 未审阅自动安装 | Passed 后仍需人工采纳 |

### 15.3 审批原则

审批按动作风险触发，而不是按 Agent 名称触发。只读观察可以不审批；写入、命令、网络、桌面操作、并行模型请求、安装和最终 Patch 应用必须进入审批或已明确配置的低风险策略。

---

## 16. 本地数据、完整性与隐私

### 16.1 主要存储位置

| 数据 | 默认路径 |
|---|---|
| Agent 任务图 | `%LOCALAPPDATA%\NOVA\agent-os\task-graphs` |
| Living Memory | `%LOCALAPPDATA%\NOVA\living-memory\profile.json` |
| Evolution Lab 状态 | `%LOCALAPPDATA%\NOVA\evolution-lab\state.json` |
| Evolution Lab 沙箱 | `%LOCALAPPDATA%\NOVA\evolution-lab\plugin-workspaces` |
| Mesh 决策 | 对应 Mesh 运行记录目录中的 `decision.json` |

### 16.2 原子写入

Evolution Lab 等状态使用临时文件再替换正式文件的方式持久化，降低进程异常退出造成 JSON 半写入的概率。状态对象采用 camelCase 序列化，枚举使用 camelCase 字符串。

### 16.3 隐私边界

- 候选发现阶段只处理本地任务元数据。
- Living Memory 只在本地建立候选，未确认内容不会自动成为长期行为。
- Evolution Lab 不访问普通交付物、知识图谱、MCP、桌面或网络。
- 模型提供方仍可能接收经用户批准的任务上下文；部署时应结合企业数据策略选择本地模型或合规云模型。

---

## 17. 界面与 IPC 契约

桌面端通过受控 IPC 调用核心服务。与本文相关的界面能力包括：

- 获取 Evolution Lab 当前策略、预算、候选和实验状态。
- 保存开关、单次/月度预算、每周上限和模型轮数。
- 提出实验、准备沙箱、开始模型运行。
- 执行静态验证、采纳、拒绝和刷新状态。
- 展示 A2A 角色图、工作包、依赖、Worker 状态、工具调用与阶段产出。
- 对 Mesh 规划、执行和最终 Patch 应用分别发起审批。

IPC 只是桌面壳与内核的边界，不应把密钥、绝对权限或未经验证的模型结果直接暴露给渲染进程。

---

## 18. 测试与验收基线

当前代码中的自动化检查覆盖以下关键场景：

- 分析型并行委派能合并 Worker 结果。
- `delegate_parallel_tasks` 必须审批。
- 自动 Worker 不递归创建无限子 Agent。
- Agent Mesh 规划、依赖波次和硬写入所有权。
- 重叠路径计划被拒绝，越权写入被工具宿主阻止。
- 依赖 Worker 能看到前一波真实提交。
- 组合 Patch 通过受控应用并清理集成 worktree。
- Council 结构化 ACCEPT 解析与密钥脱敏。
- Living Memory 候选和安全 Skill 蒸馏。
- Evolution Runtime 只暴露限定插件工具。
- Evolution Lab 预算、声明式沙箱、非法可执行内容拒绝、安装守卫。
- 定时候选发现的本地化、节流和去重。

这些测试说明核心控制在当前版本有自动化回归覆盖，但不等同于第三方安全认证。正式 GA 仍应补充长时稳定性、异常断电、模型供应商故障、极端工作区规模和跨平台回归。

### 18.1 A2A 最小验收矩阵

| 场景 | 输入/前置 | 预期结果 | 禁止结果 |
|---|---|---|---|
| 分析型并行 | 3 个互相独立的只读问题 | 产生 3 个角色结果并由主 Agent 汇总 | Worker 声称写文件或访问未提供资料 |
| 合法 Mesh | 2 个并行包 + 1 个依赖包 | 波次为 `[2,1]`，下游读到上游提交 | 三个包串行运行却仍额外收费 |
| 路径重叠 | 两个包同时拥有同一文件或目录 | 计划阶段拒绝 | 启动后才发现冲突 |
| 空写入范围 | `owned_paths` 为空 | 计划无效并给出明确修复提示 | 进入 Runtime 后报通用 Access denied |
| Worker 越权 | Worker 尝试修改别人的路径 | Tool Host 拒绝，该包失败 | 越权文件进入 Patch |
| 部分 Worker 超时 | 1/3 Worker 超时 | 保留两个成功结果，仅重试失败包 | 重跑全部 Worker |
| Patch 冲突 | 同波次 Patch 无法集成 | 波次失败，主工作区不变 | 忽略冲突继续下游 |
| 验证失败 | 构建/测试退出码非 0 | `IsEligible=false` 或进入修复流程 | 显示“已完成” |
| Council 格式错误 | 缺少 VERDICT 或 CONFIDENCE | 解析为不可用，不自动通过 | 将自然语言“看起来不错”当 ACCEPT |
| 主 HEAD 漂移 | Mesh 后用户修改或提交主仓库 | Apply 拒绝并要求重新校准 | 把旧 Patch 强行应用 |
| 宿主冲突 | 同 TaskId 被另一个 BootId 持有 | 阻止双执行，显示 owner/epoch/checkpoint | 两个宿主同时写入 |
| 中断恢复 | 程序在模型完成后退出 | 从收尾检查点继续，不再消耗同一模型轮次 | 从头重跑并重复副作用 |

### 18.2 A2A 可观测性字段

面向 UI、日志和诊断报告，至少应保留：`task_id`、`mesh_id`、`owner_boot_id`、`epoch`、`package_id`、`wave_index`、`provider`、`model`、`model_rounds`、`tool_calls`、`owned_paths`、`base_head`、`integration_head`、`patch_sha256`、`verification_exit_code`、`council_verdict`、`applied`、`checkpoint`、开始与结束时间。

面向普通用户时，可以把这些字段归纳成“当前步骤、谁在做、改了哪些文件、验证是否通过、下一步需要什么”；技术详情仍应能展开查看。

---

## 19. 运维与故障排查

### 19.1 A2A 无法启动

按顺序检查：

1. 是否已经选择工作区。
2. 是否是 Git 仓库且工作树干净。
3. 模型是否已连接。
4. 工作包路径是否都是相对路径且互不重叠。
5. 是否批准额外 Agent 请求。
6. 是否存在任务宿主持有冲突、旧 epoch 或未恢复检查点。

### 19.2 Mesh 已执行但无法应用

检查主工作区 HEAD 是否变化、是否出现新未提交修改、Patch SHA-256 是否一致、`git apply --check` 是否通过，以及 Council 是否返回结构化通过。

### 19.3 Evolution Lab 不自动启动

检查两个开关是否同时开启、首次 10 分钟窗口是否到达、是否已有活动/待审实验、每周实验数和月度 Token 是否已达上限。

### 19.4 实验总是 Failed

常见原因：

- 模型没有实质修改 `SKILL.md`。
- 生成了不允许的文件。
- Manifest 改变了 `permissions: []`。
- Skill 太短、太长或缺少人工确认和不得扩大权限说明。
- 出现绕过审批、读取凭据或关闭安全等禁用表达。
- 模型超时或运行轮数/预算不足。

### 19.5 Passed 但无法采纳

通过验证后文件又发生变化，或者 SkillRegistry 安装目标冲突。重新验证并确认唯一插件 ID，再执行采纳。

---

## 20. 当前限制与下一阶段建议

### 20.1 当前限制

- A2A 仍主要面向本机进程与 Git worktree，没有跨机器调度。
- 规划器固定 2～4 个工作包，不适合超大型项目一次性拆分。
- Council 仍依赖模型结构化输出；失败时需要重试或人工裁决。
- Evolution Lab 只支持声明式 instruction-extension，不能生成可执行插件。
- 候选发现以规则信号为主，尚未形成更细的收益评估和离线基准评分。
- Living Memory 的偏好推断仍需要足够重复样本。

### 20.2 推荐演进路线

**P0：稳定性**

- 为每个工作包和实验增加统一超时、取消、续跑与幂等键。
- 把 Council 无输出转换为显式“未裁决”，禁止静默失败。
- 增加断电恢复与持有者 lease 自动回收测试。

**P1：可观察性**

- 统一显示 DAG、当前波次、Worker 输入范围、真实产出和阻断原因。
- 提供 Token 预算预测与实际消耗对比。
- 让每个 Council 决定可以定位到具体证据。

**P2：插件质量**

- 为 Skill 增加离线用例、前后对比和最小回归集。
- 采纳前显示能力变化摘要，而不只显示文件 Diff。
- 引入签名、发布者身份和插件来源信任级别。

**P3：开放 A2A**

- 定义外部 Agent Card、能力发现、任务状态和 Artifact 协议适配层。
- 外部 Agent 只获得代理化工具，不直接获得工作区凭据。
- 支持跨进程/跨机器 Worker，但继续沿用所有权、预算和证据规则。

**P4：企业治理**

- 团队级策略、集中审计、模型路由、成本中心和数据区域规则。
- 企业管理员允许的 Agent Pack、MCP、Skill 与进化插件白名单。

---

## 附录 A：核心源码索引

| 领域 | 主要文件 |
|---|---|
| 分析型并行委派 | `NovaDesktop/Services/ParallelAgentOrchestrator.cs` |
| 工具与审批边界 | `NovaDesktop/Services/WorkspaceToolHost.cs` |
| Mesh 计划 | `NovaDesktop/Services/AgentMeshPlannerService.cs` |
| Mesh 执行与集成 | `NovaDesktop/Services/AgentMeshService.cs` |
| Mesh 裁决 | `NovaDesktop/Services/AgentMeshCouncilService.cs` |
| 角色图 | `NovaDesktop/Services/AgentTaskGraphService.cs` |
| 候选竞争 | `NovaDesktop/Services/WorktreeTournamentService.cs` |
| 竞争裁决 | `NovaDesktop/Services/TournamentCouncilService.cs` |
| 独立验证 | `NovaDesktop/Services/IndependentVerificationCouncilService.cs` |
| 自进化实验 | `NovaDesktop/Services/EvolutionLabService.cs` |
| 用户习惯学习 | `NovaDesktop/Services/LivingMemoryService.cs` |
| Skill 注册安装 | `NovaDesktop/Services/SkillRegistryService.cs` |
| 专业 Agent Pack | `NovaDesktop/Services/AgentPackService.cs`、`AgentPackWorkshopService.cs` |
| 桌面协调 | `NovaDesktop/ViewModels/MainViewModel.cs` |
| Electron IPC | `NovaDesktop.Electron/electron/main.cjs`、`preload.cjs`、`src/types.ts` |
| 回归测试 | `NovaDesktop.SmokeTests/Program.cs` |

## 附录 B：安全审查清单

- [ ] 是否清楚区分只读分析 Worker 与可写 Mesh Worker？
- [ ] 是否展示了每个工作包的独占路径？
- [ ] 是否拒绝绝对路径、`..`、`.git` 和模糊通配？
- [ ] 是否在启动和最终应用前检查 Git 工作区？
- [ ] 是否记录 Patch 哈希和 Council 结论？
- [ ] 是否把并行模型请求数量和 Token 成本告诉用户？
- [ ] Evolution Lab 是否默认关闭？
- [ ] 定时发现是否与模型运行分离？
- [ ] 插件目录是否只允许四个声明文件？
- [ ] Manifest 权限是否为空？
- [ ] 采纳前是否重新验证未被修改？
- [ ] 插件是否可禁用或卸载？
- [ ] 当前指令是否始终高于长期记忆？
- [ ] 失败、未知项和未裁决是否清晰呈现？

## 附录 C：术语表

| 术语 | 定义 |
|---|---|
| A2A | Agent 与 Agent 之间的任务分工、依赖传递、产出合并与审查机制 |
| Worker | 执行一个有边界子任务的模型执行单元 |
| Agent Mesh | 带 DAG、Git 隔离、路径所有权与委员会裁决的工程型 A2A |
| Work Package | 含任务说明、独占路径和依赖的最小工程工作包 |
| Wave | DAG 中可并行执行的一组工作包 |
| Council | 对计划、候选实现或最终集成结果作结构化裁决的审查角色 |
| Living Memory | 经用户确认的长期协作习惯与偏好系统 |
| Evolution Lab | 在受限沙箱中生成、验证并采纳声明式插件的系统 |
| Agent Pack | 专业 Agent 的角色、工作流、能力和输入输出契约包 |
| Skill | 可启用、禁用或卸载的声明式工作方法 |
| Evidence | 来自工具、文件、构建、测试、哈希或状态账本的可复核证据 |

---

**文档基线说明：** 本文以 NOVA AgentOS `1.1.0-preview.16` 当前代码为准。凡标注为“下一阶段建议”的内容均不是对现有实现的功能承诺。
