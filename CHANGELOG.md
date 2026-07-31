# Changelog

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
