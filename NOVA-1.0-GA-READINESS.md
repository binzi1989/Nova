# NOVA 1.0 GA Readiness

更新时间：2026-07-29  
当前构建：`0.9.0-preview.29`

## 已解除的内部阻断

| 门槛 | 状态 | 可复验证据 |
|---|---|---|
| 统一单调执行日志 | PASS | `execution-events.jsonl`；任务快照、任务图、Supervisor 共享 `ExecutionSequence` |
| 崩溃/撕裂重放 | PASS | 删除状态快照并写入损坏尾记录后仍恢复最后提交终态 |
| 五类故障注入 | PASS | 模型、工具、写入、验证、交付各 20 次 |
| 副作用防重复 | PASS | Intent 阻止不确定重放；Commit 由幂等键返回既有结果 |
| 完整本地回归 | PASS | 沙箱外 72/72 |
| GA 发布硬门禁 | PASS | 稳定版必须同时具备基准报告、HTTPS URL、证书、签名安装器和隔离安装启动验证 |

## 已具备门禁、尚未获得真实结果

| 门槛 | 当前状态 | 退出条件 |
|---|---|---|
| 30 项固定端到端任务 | 目录和计量器已完成 | 每项运行 3 次，共 90 次，并保存可检查证据 |
| 可行任务 PROVEN 率 | 未运行 | ≥ 80% |
| 终态分类正确率 | 未运行 | ≥ 90% |
| 六界面 Truthful UX | 代码已统一核心投影，人工验收未完成 | 左栏、Threadspace、Mission、执行流、AgentOS 中心、交付台无终态冲突 |

基准目录：`ga/benchmark-catalog.json`  
计量器：`tools/Measure-GaBenchmark.ps1`

## 外部阻断

| 所需输入 | 原因 |
|---|---|
| Windows Authenticode 代码签名证书及私钥 | 签署 `NovaDesktop.exe`、Inno 安装器和发布清单 |
| 真实 HTTPS 下载地址 | 更新清单必须指向实际可下载、哈希一致的 ZIP |
| Inno Setup Compiler | 从已签署主程序构建正式安装器 |

在这些输入缺失时，NOVA 会继续生成明确标记的 `PREVIEW_UNSIGNED`，自动更新保持关闭；
`1.0.0` 构建会被拒绝，不会用自签名、假 URL 或人工改清单冒充 GA。

## 正式发布命令形态

```powershell
.\build-release.ps1 `
  -Version 1.0.0 `
  -PackageUrl "https://downloads.example.com/NOVA-1.0.0-win-x64.zip" `
  -InnoCompilerPath "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" `
  -CodeSigningCertificateThumbprint "<真实证书指纹>" `
  -GaBenchmarkReportPath ".\ga\benchmark-report.json" `
  -RequireTrustedRelease
```

示例域名只用于说明命令形态，不能通过脚本的真实 HTTPS 与签名校验。
