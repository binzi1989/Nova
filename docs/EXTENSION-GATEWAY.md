# NOVA Extension Gateway

> CLI 使用说明见 [CLI.md](CLI.md)，P0-P5 成熟度与验收标准见仓库根目录的 `NOVA-P0-P5-ROADMAP.md`。

> P1 Task Capsule 与上下文预算契约见 [SMART-CONTEXT-GOVERNOR.md](SMART-CONTEXT-GOVERNOR.md)。

Extension Gateway 是 NOVA AgentOS 提供给本机网页、自动化工具和微服务的稳定边界。它不暴露 AgentOS 核心源码，也不允许第三方直接绕过权限内核。

## P0 安全范围

- 仅监听 `127.0.0.1`，端口由系统随机分配。
- 每次应用启动生成新的 256 位访问令牌。
- 开放任务、交付物元数据和事件的读取能力。
- 外部服务只能提交待审阅的任务草稿，不能直接启动任务或消耗 Token。
- 不返回工作区绝对路径、模型密钥或附件原始路径。
- 不开放远程命令、任务创建、文件读取或文件写入。
- 停止服务或轮换令牌时，现有 SSE 连接立即断开。

## 启用方式

打开「扩展坞 → 服务接口」，启动本机服务并复制接口地址和访问令牌。令牌只应交给用户信任的本机程序。

HTTP 请求使用 Bearer Token：

```bash
curl -H "Authorization: Bearer <TOKEN>" \
  http://127.0.0.1:<PORT>/v1/tasks
```

浏览器 `EventSource` 不能设置 Authorization Header，因此 SSE 使用一次会话令牌查询参数：

```js
const events = new EventSource(
  "http://127.0.0.1:<PORT>/v1/events?access_token=<TOKEN>"
);

events.addEventListener("delivery.ready", (event) => {
  const hook = JSON.parse(event.data);
  console.log(hook.payload.delivery);
});
```

## API

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/v1/health` | 服务状态与安全范围 |
| GET | `/v1/manifest` | 接口、权限和 Hook 清单 |
| GET | `/v1/tasks` | 当前任务摘要 |
| GET | `/v1/tasks/{taskId}` | 单个任务及交付摘要 |
| GET | `/v1/tasks/{taskId}/artifacts` | 交付物安全元数据 |
| GET | `/v1/tasks/{taskId}/context` | Task Capsule 分层、预算与选择原因 |
| GET | `/v1/budget` | AgentOS 统一上下文预算与本地 Token 估算 |
| GET | `/v1/events` | SSE Hook 事件流 |
| GET | `/v1/action-requests` | 当前待审阅的外部任务请求 |
| POST | `/v1/action-requests` | 提交任务草稿，等待用户带入工作台 |

除任务草稿入口外，其他非 `GET` 请求会返回 `405 write_api_not_enabled`。

提交任务请求：

```bash
curl -X POST \
  -H "Authorization: Bearer <TOKEN>" \
  -H "Content-Type: application/json" \
  -d '{
    "source": "本机运营看板",
    "title": "复核今日商品数据",
    "prompt": "读取当前工作区的数据，生成异常清单和下一步建议。",
    "executionMode": "Plan"
  }' \
  http://127.0.0.1:<PORT>/v1/action-requests
```

请求只会出现在「扩展坞 → 服务接口 → 外部任务请求箱」。用户选择“带入新任务”后，它才会成为工作台草稿；仍需用户检查并点击开始处理。

## Hook 契约

首批事件使用 `nova.hook/1.0`：

- `task.started`：任务正式进入 AgentOS。
- `artifact.created`：本轮产生了真实交付文件。
- `delivery.ready`：交付结果完成归档，可以进入审阅。
- `action.requested`：本机扩展提交了一个待用户审阅的任务目标。

事件信封：

```json
{
  "schema": "nova.hook/1.0",
  "id": "hook-...",
  "sequence": 1,
  "type": "delivery.ready",
  "occurredAt": "2026-08-06T00:00:00.000Z",
  "taskId": "task-...",
  "payload": {}
}
```

## 后续阶段

P2 才考虑带签名的回调订阅、插件身份、反向代理和局域网模式。任何执行或写入能力都必须继续经过 AgentOS 的审批、预算、审计和恢复机制。
