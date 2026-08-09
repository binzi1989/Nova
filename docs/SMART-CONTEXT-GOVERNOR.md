# Smart Context Governor · P1

P1 的目标不是简单缩短 Prompt，而是在不丢失用户目标、纠正和真实证据的前提下，减少重复上下文与无关文件进入模型。

## Task Capsule

每次正式模型执行前，AgentOS 会编译一份 `nova.task-capsule/1.0`：

- `L0.goal`：当前用户目标，最高优先级。
- `L0.thread`：原始目标、用户提供的事实、选择、纠正和近期指代。
- `L0.contract`：当前 Agent Pack 的角色、流程与交付约束。
- `L0.calibration`：用户已经确认的 Agent 修正。
- `L0.profile`：与当前工作方式有关的稳定偏好。
- `L1.workspace`：按目标相关度选择的工作区文件片段。

模型实际收到的是 Task Capsule，而不是把全部对话、所有工作区文件和所有 Agent 配置直接拼接。编译失败时会降级到原上下文，不会因为本地索引失败阻断模型任务。

## 增量复用

工作区证据会生成独立的 `SourceFingerprint`。它由工作区、目标检索词、预算，以及候选文件的相对路径、大小和修改时间共同决定：

- 相同工作区、相同目标信号和相同预算可跨任务、跨 Agent 复用已经编译的 Context Pack。
- 文件新增、删除或修改后指纹会变化，下一轮自动重新读取，不会继续使用旧证据。
- 缓存只保存经过裁剪和脱敏的相对路径片段，不保存模型密钥，也不通过 UI 暴露完整 Runtime Prompt。
- Task Capsule 仍然按任务单独生成；共享的只是工作区证据，不会混合两个任务的对话和用户纠正。

## 模式预算

| 模式 | Capsule 字符上限 | 工作区证据上限 | 策略 |
|---|---:|---:|---|
| Ask | 18,000 | 4,000 | 目标、近期纠正和最少相关证据 |
| Plan | 30,000 | 9,000 | 扩大证据范围，不注入完整仓库 |
| Build | 44,000 | 16,000 | 实施上下文与高信号工程片段 |
| Autopilot | 50,000 | 18,000 | 扩大证据并保留恢复线索 |
| Goal | 54,000 | 18,000 | 目标约束、未知项和跨轮决策 |

Token 数值是按字符数计算的本地估算，不冒充模型供应商账单。`EstimatedTokensAvoided` 只计算实际压缩的历史、配置和已选上下文，不把“扫描过但本来未必会发送”的全部仓库内容算作节省。

## 可解释性

Gateway：

```text
GET /v1/tasks/{taskId}/context
GET /v1/budget?mode=Build&characters=1200
```

CLI：

```powershell
nova context inspect <task-id>
nova context explain <task-id>
nova budget show <task-id>
nova budget estimate "检查项目发布阻断项" --mode Build
```

接口只暴露相对路径、命中原因、行号、预算和摘要，不返回 Task Capsule 的完整 Prompt，也不返回本机绝对存储路径。

桌面端的行动脉络提供可折叠的“本轮理解”面板，显示本轮上下文占用、估算 Token、是否命中复用缓存、纳入的上下文层和高信号文件。该面板用于让用户理解 NOVA 为什么读这些资料，而不是展示内部提示词或调试流水。

## 当前验收

- Capsule 字符数不能超过对应模式预算。
- 完整 Runtime Prompt 不写入诊断 JSON 或 Gateway。
- 工作区候选会过滤密钥文件，并对正文中的常见 Token、Bearer 和密码赋值进行脱敏。
- CLI 与 Gateway 使用的预算来自同一个 AgentOS 策略，不在前端重复写死。
- 上下文编译故障只能触发降级，不能直接让任务失败。
- 第二个相同目标任务必须命中共享缓存；任何候选文件发生变化后必须自动失效并重新编译。
