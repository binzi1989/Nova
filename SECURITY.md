# Security Policy

## Supported versions

当前仅维护最新 Preview。历史 Preview 不接收安全修复。

| Version | Supported |
|---|---|
| 0.9.0-preview.29 | Yes |
| Earlier previews | No |

## Reporting a vulnerability

请不要在公开 Issue 中提交 API Key、访问令牌、私钥、用户工作区内容或可直接利用的漏洞细节。

优先使用 GitHub Security Advisories 的私密报告入口。如果仓库尚未启用该功能，请仅提交不包含敏感细节的 Issue，请求项目所有者建立私密沟通渠道。

报告中建议包含：

- 受影响版本和平台；
- 最小复现步骤；
- 实际影响与权限边界；
- 是否涉及工作区越界、凭据泄露、命令执行、MCP 或桌面控制；
- 可安全公开的日志或脱敏证据。

## Secret handling

- 模型密钥不得提交到仓库；
- `.env`、证书、私钥、本地 MCP 配置和 Credential Manager 数据均被忽略；
- 测试中的 `sk-...` 字符串是专门用于验证脱敏行为的假凭据；
- 发现真实凭据后，应立即撤销并轮换，不应只从 Git 历史中删除。
