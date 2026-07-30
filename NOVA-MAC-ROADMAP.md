# NOVA for Mac

## 当前版本：0.1.0 Preview 4

Preview 4 是首个真正接入共享 AgentOS 的 Mac 版本。它不是 Windows UI 的截图复刻，也不再只是一个独立聊天壳。

### 已同步

- 原生 Avalonia macOS 桌面应用，不依赖 Wine、浏览器或 WebView。
- Windows 与 macOS 共用 `Nova.AgentOS`：
  - AgentOS Kernel 与服务健康状态。
  - 单调执行事件账本。
  - 任务快照、异常中断恢复与本地任务空间。
  - Task Graph。
  - Durable Agent Supervisor 与任务租约。
  - 弹性预算、并行上限与输出限制。
- OpenAI、DeepSeek、Kimi 文本模型入口。
- Ask 多轮对话。
- Autopilot 自动创建 3 个只读子 Agent 并行分析，再由主 Agent 汇总。
- 原生工作区文件夹选择器与有边界的工程信号识别。
- 用户触发的 MCP 配置位置探测。
- API Key 仅停留在当前进程内存，不写入配置文件。

### 仍未同步

- 工作区文件读取/修改、Patch 预览、终端命令和代码交付。
- 图片/文件附件发送。
- macOS Keychain。
- MCP/Skills 能力市场、授权导入与真实调用。
- 计划任务、效率总结、知识图谱和交付工作台。
- Developer ID 签名、公证、自动更新和 Universal 单包。

未迁移的功能会在界面中明确标为不可用，不会产生模拟交付物。

## 构建

在 Windows 上交叉构建 Apple Silicon 与 Intel 的未签名 Preview：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build-macos.ps1
```

构建门禁会先运行：

- `--startup-smoke`
- `--agentos-smoke`
- Mach-O 架构验证
- TAR.GZ 可执行权限验证
- 包清单与 SHA256 生成

Windows 交叉构建时优先分发 `.tar.gz`，它能保留 Unix 执行权限。解压后在包目录运行：

```bash
zsh ./FIRST-LAUNCH.command
```

脚本只会为当前 `NOVA.app` 副本解除 quarantine、应用本机 ad-hoc 签名、验证后打开应用。

正式公开发行必须在 macOS 上完成 Developer ID 签名和 Apple 公证：

```bash
chmod +x ./build-macos.sh
NOVA_SIGNING_IDENTITY="Developer ID Application: ..." \
NOVA_NOTARY_PROFILE="notarytool-profile" \
./build-macos.sh 1.0.0 osx-arm64
```

## 下一同步切片

1. 跨平台 Workspace Tool Host：只读文件工具、Patch 预览、写入授权与命令授权。
2. macOS Keychain 与模型凭据管理。
3. MCP/Skills 能力市场和真实调用。
4. 附件、多模态模型输入与交付工作台。
5. Developer ID、公证、Universal 分发与自动更新。
