# NOVA AgentOS macOS 安装与故障排查

NOVA 的 macOS 包必须在真实 macOS Runner 上分别构建 Apple Silicon（arm64）与 Intel（x64）版本。请不要将 Windows 生成的目录改名后当作 Mac 应用使用。

## 先选对架构

- M1、M2、M3、M4 及后续 Apple 芯片：下载 `mac-arm64`。
- Intel 处理器 Mac：下载 `mac-x64`。
- 在“关于本机”中可以查看芯片类型。

## 推荐安装方式

1. 完整解压 ZIP，或打开 DMG。
2. 双击 `安装 NOVA.command`；它会把应用复制到 `/Applications`，修复隔离属性和可执行权限，然后启动 NOVA。
3. 如果系统拦截脚本，按住 Control 点击脚本，选择“打开”。

也可以手动把 `NOVA AgentOS.app` 拖到“应用程序”，随后按住 Control 点击应用并选择“打开”。

## 仍提示“无法打开”

在终端执行：

```bash
xattr -cr "/Applications/NOVA AgentOS.app"
chmod +x "/Applications/NOVA AgentOS.app/Contents/MacOS/NOVA AgentOS"
chmod +x "/Applications/NOVA AgentOS.app/Contents/Resources/bridge/Nova.AgentOS.Bridge"
open "/Applications/NOVA AgentOS.app"
```

如果仍失败，请收集：

```bash
uname -m
sw_vers
codesign --verify --deep --strict --verbose=2 "/Applications/NOVA AgentOS.app"
spctl --assess --type execute --verbose=4 "/Applications/NOVA AgentOS.app"
```

将输出与下载的文件名一起提交。不要上传模型密钥、工作区文件或私人资料。

## 关于签名状态

- `developer-id-notarized`：已经使用 Apple Developer ID 签名并通过 Apple 公证，可直接打开。
- `ad-hoc`：构建内容与嵌套可执行文件已经完成签名校验，但没有 Apple 公证。首次启动可能仍需使用上面的安装脚本或 Control + 打开。

项目不会把未公证包描述成“已通过 Apple 官方认证”。

---

## English quick start

Choose `mac-arm64` for Apple Silicon or `mac-x64` for Intel. Extract the ZIP or mount the DMG, then run `安装 NOVA.command`. If macOS blocks it, Control-click the installer and choose **Open**. The installer copies NOVA to `/Applications`, clears quarantine attributes, repairs executable permissions, and launches the application.
