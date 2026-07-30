# Contributing

NOVA 当前处于 1.0 范围收口阶段。优先接受能够减少发布风险、提升结果真实性或修复回归的问题；暂不鼓励横向增加新面板。

## 开发流程

1. 从 Issue 或可复现缺陷开始；
2. 保持改动范围单一；
3. 不提交密钥、缓存、构建输出和二进制发布包；
4. 运行相关的 .NET 测试、Electron 构建与 Bridge 验证；
5. 在 PR 中说明真实用户影响、验证证据和未覆盖边界。

## 本地验证

```powershell
dotnet run --project NovaDesktop.SmokeTests/NovaDesktop.SmokeTests.csproj
cd NovaDesktop.Electron
npm ci
npm run build
npm run smoke:bridge
```

## Pull Request 要求

- 不以“模型说完成”作为验证；
- UI 状态必须来自同一任务真值；
- 新的副作用必须接入权限、预算、日志和恢复边界；
- 新模型、MCP、Skill、SSH 或插件接口不得绕过扩展坞治理；
- 失败必须进入明确终态或可恢复安全点。
