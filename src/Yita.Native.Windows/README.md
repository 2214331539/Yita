# Yita.Native.Windows

该项目承载 main 中的 Windows 原生能力，包括鼠标手势、全局快捷键、取词、托盘、开机启动、凭据与加密修正适配。主界面由 `Yita.Desktop` 的 Avalonia 窗口实现。

自动划词先尝试独立 UI Automation worker 和 Edit/RichEdit/Scintilla 等原生控件；失败后按设置执行受控 Ctrl+C。WPS PDF 兼容路径优先复制，支持嵌入 PDF 进程和同一窗口内的焦点子进程。通用回退默认启用，可以关闭并保存；终端、密码控件和不能完整保存的剪贴板不自动复制。

`Ctrl+Shift+T` 和托盘“翻译剪贴板”只读取现有剪贴板，用户需要先自行复制。快捷键不会先模拟 Ctrl+C，这条路径在自动划词暂停后仍可使用。

这条链路已经通过 `Yita.Core.SelectionReaderPipeline` 统一编排。当前优先使用独立的 `Yita.UIA.Worker.exe` 读取 UI Automation 选区；Worker 缺失、超时或异常时，会继续尝试原生控件和安全剪贴板回退。Worker 不在 Avalonia 主进程内执行，避免 WPS、浏览器 PDF 或其他第三方 provider 的异常导致主程序退出。

`WindowsMouseSelectionService` 在另一个原生线程安装低级鼠标钩子，只把鼠标按下/抬起的坐标放入有序处理链路；它会过滤普通点击、窗口移动和跨窗口拖动，然后向桌面壳发送 `SelectionGesture`。UIA/剪贴板读取发生在钩子回调之外，防止读取阻塞导致 Windows 移除鼠标钩子。

剪贴板事务按原目标与焦点进程验证归属，等待 OLE 格式稳定再读取，随后尽可能恢复所有可保存格式；请求取消后也完成已发送复制的恢复，用户后续主动复制不能被旧事务覆盖。

`WindowsCredentialSecretStore` 使用 Windows Credential Manager 保存 DeepSeek API Key，标识为 `Yita:DeepSeekApiKey`。普通配置使用 `desktop-settings.json`，不包含密钥。macOS 使用独立的 Keychain 适配代码，其真机验证尚未完成。

`WindowsSingleInstanceGuard` 防止多个 Yita 进程同时注册全局快捷键和鼠标钩子；`WindowsStartupRegistration` 只写入当前用户的 Run 项，不需要管理员权限。

已接入 Avalonia 壳的 Windows 生命周期控制：

- `WindowsSingleInstanceGuard` 防止多个实例争抢快捷键和鼠标钩子；
- `WindowsStartupRegistration` 管理当前用户的开机启动项；
- 设置页和托盘菜单可以即时启用/暂停原生取词；
- UIA Worker、原生控件和安全剪贴板回退继续保持独立的超时边界。

原生 smoke 已验证 UIA、Ctrl+C 与取消恢复。维护者确认本机 WPS PDF 划词、普通复制与托盘验收通过；其他应用版本、权限及干净系统的安装包仍需验证。完整流程见 [架构说明](../../docs/ARCHITECTURE.md)。
