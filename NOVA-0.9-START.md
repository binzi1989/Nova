# NOVA 0.9 · Agent Supervisor

0.9 的目标是让 NOVA 从“窗口打开时执行任务”升级为拥有持久任务所有权、心跳、检查点和恢复决策的 Agent Runtime Supervisor。

## Preview 1：已经进入代码

- 每个运行任务取得唯一 Supervisor Lease。
- Lease 记录 Task ID、工作区、执行模式、Boot ID、尝试次数和最近检查点。
- Thinking/Tool/Batch/Completed/Failed 运行事件推动心跳。
- 普通心跳最多每两秒落盘一次；阶段边界和终态强制落盘。
- 上一宿主未释放的 Active/Paused Lease 在新 Boot 自动转为 Recoverable。
- 完成、失败和取消状态不会在重启后被误判为活动任务。
- Supervisor 作为 AgentOS 内建服务出现在控制中心。

状态文件：

`%LOCALAPPDATA%\NOVA\agent-os\supervisor\supervisor-state.json`

## Preview 2：安全互操作与快速开始

- 增加四步快速开始：工作区、模型、可选扩展、首个可验证目标。
- 空 Threadspace 提供真实工程目标模板，直接缩短首次价值路径。
- 增加 MCP 本机配置发现，覆盖工作区、Claude、Cursor、Windsurf 与 Codex。
- 扫描、导入、启用和测试拆成独立授权阶段，权限影响在动作前可见。
- 发现阶段只读且离线；导入阶段不执行连接并强制保持停用。
- 环境变量与 Header 只接受变量引用，拒绝把来源配置中的明文密钥写进 NOVA。
- 可能下载或启动软件的 MCP 命令独立标记，不能被“低风险项”批量选中。
- 扩展互操作与 NOVA 的任务图、证据链、Supervisor、本地认知形成同一工作流。

## 0.9 后续切片（已收紧）

Windows 1.0 主线已经冻结，以 [NOVA-1.0-SCOPE-FREEZE.md](NOVA-1.0-SCOPE-FREEZE.md)
为唯一范围基准：

1. Preview 12：真实暂停、预算硬限制、副作用收据、退出屏障、原子租约。
2. Preview 13：定向修复回路、证据失效、故障分类和统一执行日志。
3. RC1：单一状态真值、恢复操作、首启引导与性能/无障碍收口。
4. RC2：安全审计、故障注入、签名安装器与可信更新链。

后台宿主、系统托盘、关窗后持续执行、远程多宿主和 Mac 功能追平全部延期到
1.x，不再阻塞 Windows AgentOS 1.0。

## 进入 Beta 的硬门槛

- 强制终止进程后，任务恢复点丢失不超过一个持久阶段。
- 同一个 Task ID 不允许两个有效宿主持有活动 Lease。
- 500 个历史 Lease 的冷启动读取不阻塞 UI 首帧。
- 任何自动恢复都不得绕过原任务的审批策略与工作区边界。
- 恢复后的任务图、证据账本和最终交付状态必须一致。
