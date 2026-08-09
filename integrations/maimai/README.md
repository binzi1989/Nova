# NOVA × 脉脉安全连接器（Windows）

这是可直接安装的本地闭环：Chrome 扩展只在用户点击时读取当前脉脉页面的可见文字，Windows Native Messaging Host 将核对后的职业信息写入本机加密仓，NOVA 再通过 stdio MCP 检索和管理这些档案。

它明确不做：读取 Cookie、后台爬取、自动翻页、验证码或风控绕过、批量加人、自动发信、猜测手机号/邮箱。这样既避免把脉脉账号交给 Nova，也避免把第三方页面变化变成后台爬虫故障。

## 唯一安装流程

在 PowerShell 中执行：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
cd D:\Agent\integrations\maimai
.\install-windows.ps1
```

脚本会完成以下动作，并在最后执行安装后自检：

1. 发布一个 Windows x64 自包含单文件连接器；
2. 注册 Chrome Native Messaging Host `ai.nova.maimai`；
3. 将连接器目录加入当前用户 PATH，并合并写入 NOVA 的 `nova-maimai` MCP 配置，不删除已有 MCP；
4. 安装并启用 `nova.maimai-relationship-research` Agent Pack；
5. 输出扩展目录并运行加密、协议和 CRUD 冒烟测试。

然后只做一次浏览器动作：打开 `chrome://extensions`，开启“开发者模式”，点击“加载已解压的扩展程序”，选择脚本输出的目录（默认是 `%LOCALAPPDATA%\NOVA\Connectors\Maimai\extension`）。扩展 ID 固定为 `nplhnfcoijedjoihpghfhnjkkgomhhpg`，与 Native Host 白名单完全一致。

重启 NOVA（必须退出进程后重新打开）后，MCP 列表中应出现并启用 `nova-maimai`，Agent Pack 中应出现并启用“NOVA 脉脉人才关系研究 Agent”。

## 使用

1. 在 Chrome 中由你本人正常登录脉脉并打开一个目标人物页面；
2. 点击“NOVA 脉脉安全采集”扩展；
3. 核对姓名、职位、公司等字段，确认保存目的和保留期限；
4. 点击“保存到 NOVA 加密仓”；
5. 在 NOVA 中说：“从我已保存的脉脉档案里，筛选上海地区懂 AgentOS 的企业软件人才。”

页面结构变化时，自动识别字段可能为空，但预览编辑表仍可直接核对和补正，因此不会静默保存错位字段。

## 可验证的安全边界

- Chrome 权限只有 `activeTab`、`scripting`、`nativeMessaging`、`storage`，没有 Cookie、历史记录、后台站点通配权限；
- URL 只接受 `https://maimai.cn` 及其子域；查询参数和 fragment 在入库前删除；
- 手机号和邮箱在入库前自动剔除；
- 档案使用 AES-256-GCM 加密，数据密钥再由 Windows DPAPI CurrentUser 保护；其他 Windows 用户无法解出密钥；
- 默认保留 90 天，可设 30/180/365 天；MCP 提供到期清理和单档案删除；
- 联系状态只记录 `contacted_manual` 等人工动作，不存在消息发送工具。

重新验证安装：

```powershell
.\test-installation.ps1
```

开发态完整验证：

```powershell
dotnet build .\connector\Nova.Maimai.Connector.csproj -c Release
.\connector\bin\Release\net8.0-windows\Nova.Maimai.Connector.exe smoke
node .\extension\smoke-extension.mjs
dotnet run --project .\validation\Nova.Maimai.Validation.csproj -- .\agent-pack
```

## 数据位置

默认数据目录：`%LOCALAPPDATA%\NOVA\Connectors\Maimai`

- `vault.key`：Windows DPAPI 保护后的随机数据密钥；
- `profiles.vault`：AES-256-GCM 密文档案；
- `app`：本地连接器；
- `extension`：Chrome 扩展；
- `ai.nova.maimai.json`：Chrome Native Messaging Host 清单。
