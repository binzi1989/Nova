# NOVA CLI · P0

`nova` 是 NOVA AgentOS 的终端入口。它不创建第二套 Runtime，而是连接桌面端的本机 Extension Gateway，因此桌面端、CLI、Hook 和后续微服务共享任务 ID、权限、事件与交付物。

## 当前能力

```text
nova status
nova doctor
nova configure http://127.0.0.1:<port>
nova auth login
nova auth logout
nova auth status
nova task list
nova task show <task-id>
nova task request "想达成的结果" --mode Plan
nova task watch
nova delivery list <task-id>
nova context inspect <task-id>
nova context explain <task-id>
nova budget show <task-id>
nova budget estimate "检查发布阻断项" --mode Build
nova hooks manifest
```

所有查询命令支持 `--json`，方便脚本、IDE 和本机微服务接入。

## 第一次连接

1. 打开 NOVA 桌面端的“扩展坞”。
2. 开启 Extension Gateway。
3. 在终端进入 `NovaDesktop.Electron`，执行 `nova.cmd doctor`。
4. 执行 `nova.cmd auth login`，粘贴桌面端显示的访问令牌。
5. 执行 `nova.cmd status`。

源码开发环境也可以使用：

```powershell
node cli/nova.mjs status
```

令牌在 Windows 上使用当前用户的 DPAPI 加密保存。Gateway 每次重新启动都会轮换令牌；遇到 401 时重新执行 `nova auth login`，不会暴露或打印令牌。

## 提交任务请求

```powershell
nova.cmd task request "检查当前工程，找出阻塞发布的问题并给出证据" --title "发布前检查" --mode Plan
```

该命令只会把请求放入 NOVA 桌面端审阅箱。未经用户确认，它不会调用模型、写文件或消耗 Token。

可选参数：

- `--source`：请求来源名称。
- `--mode`：`Ask`、`Plan`、`Build` 或 `Goal`。
- `--agent`：指定 Agent Pack ID。

## 安全约束

- P0 只接受 `127.0.0.1`、`localhost` 或 `::1` 的 HTTP Gateway。
- CLI 不提供绕过桌面审批的直接执行接口。
- 令牌不会写入普通配置或日志。
- 交付接口只返回受清洗的相对路径与元数据，不暴露工作区绝对路径。

## 验证

```powershell
npm.cmd run smoke:cli
npm.cmd run smoke:gateway:host
```

P1 已在同一 CLI 上增加 `context inspect/explain` 和 `budget show/estimate`。这些数据来自模型执行前真实编译的 Task Capsule，不是前端静态提示或假指标。详细契约见 [SMART-CONTEXT-GOVERNOR.md](SMART-CONTEXT-GOVERNOR.md)。
