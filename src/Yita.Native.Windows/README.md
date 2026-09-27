# Yita.Native.Windows

该项目承载 Windows 原生能力。当前阶段已提供一条可运行的快捷键取词链路：Avalonia 壳启动后注册 `Ctrl+Shift+T`，读取鼠标位置，先检查 Edit/RichEdit/Scintilla 等原生文本控件，再向当前前台窗口发送受控 `Ctrl+C`，等待剪贴板序列变化，读取 Unicode 文本并在剪贴板所有权仍属于目标应用时恢复原文。

这条链路已经通过 `Yita.Core.SelectionReaderPipeline` 统一编排。当前优先使用独立的 `Yita.UIA.Worker.exe` 读取 UI Automation 选区；Worker 缺失、超时或异常时，会继续尝试原生控件和安全剪贴板回退。Worker 不在 Avalonia 主进程内执行，避免 WPS、浏览器 PDF 或其他第三方 provider 的异常导致主程序退出。

`WindowsMouseSelectionService` 在另一个原生线程安装低级鼠标钩子，只把鼠标按下/抬起的坐标放入有序处理链路；它会过滤普通点击、窗口移动和跨窗口拖动，然后向桌面壳发送 `SelectionGesture`。UIA/剪贴板读取发生在钩子回调之外，防止读取阻塞导致 Windows 移除鼠标钩子。

`WindowsCredentialSecretStore` 使用 Windows Credential Manager 保存 DeepSeek API Key。`settings.json` 只保存普通配置，不包含密钥；非 Windows 平台暂时使用进程内存实现，后续由 macOS Keychain 适配器替换。

下一阶段将把以下模块从 WPF 宿主移动到这里：

- 独立 UIA Worker 的 `TextPattern` 取词和选区边界；
- 安全的 Ctrl+C 剪贴板事务与恢复；
- RegisterHotKey、低级鼠标钩子和 DPI 坐标转换；
- Windows 托盘、开机启动、单实例和权限边界；
- 外部 UIA provider 隔离进程与 watchdog。
