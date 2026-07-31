# NOVA AgentOS 1.0.2

这是 NOVA AgentOS 的维护版本，提供 Electron 主交互壳和 .NET 8 AgentOS Bridge/Core。

## 下载选择

- `NOVA-AgentOS-Electron-1.0.2-x64.zip`：Windows x64 Electron 主体验；
- `NOVA-AgentOS-Electron-1.0.2-mac-arm64.zip`：Apple Silicon Electron 主体验；
- `NOVA-AgentOS-Electron-1.0.2-mac-x64.zip`：Intel Electron 主体验；
- 项目介绍 PPT 与项目说明书：产品、架构和 1.0 发布边界。

## 重要说明

- 所有安装包当前均未签名；
- Windows 自动更新默认关闭；
- Mac 构建由 macOS 原生 CI 生成，与 Windows 共用 Electron UI 和 AgentOS Bridge/Core；
- Mac 包尚未完成 Developer ID 签名与 Apple 公证，Windows 专属桌面控制能力不可用；
- 请只从本仓库 Release 页面下载，并核对发布页面上的 SHA-256。

## 主要能力

- 多模型与本地模型端点；
- 多轮 Threadspace、附件和图片理解；
- 受控工程文件写入、构建与测试；
- MCP、Skills、SSH、云开发和组件扩展坞；
- Goal 模式、Agent Mesh、独立验证与 Proof-of-Done；
- 任务恢复、权限治理、预算治理和证据账本；
- 默认关闭、预算受限的插件式自进化实验；
- 空闲时进行零 Token 的本地候选发现，候选进入实验仍需用户确认。

## 已知问题

参见 [CHANGELOG.md](CHANGELOG.md) 与 [NOVA-1.0-GA-READINESS.md](NOVA-1.0-GA-READINESS.md)。
