# Yita.Native.Windows

该项目承载 Windows 原生能力。当前阶段已提供一条可运行的快捷键取词链路：Avalonia 壳启动后注册 `Ctrl+Shift+T`，读取鼠标位置，先检查 Edit/RichEdit/Scintilla 等原生文本控件，再向当前前台窗口发送受控 `Ctrl+C`，等待剪贴板序列变化，读取 Unicode 文本并在剪贴板所有权仍属于目标应用时恢复原文。

这条链路已经通过 `Yita.Core.SelectionReaderPipeline` 统一编排。当前优先使用独立的 `Yita.UIA.Worker.exe` 读取 UI Automation 选区；Worker 缺失、超时或异常时，会继续尝试原生控件和安全剪贴板回退。Worker 不在 Avalonia 主进程内执行，避免 WPS、浏览器 PDF 或其他第三方 provider 的异常导致主程序退出。

`WindowsMouseSelectionService` 在另一个原生线程安装低级鼠标钩子，只把鼠标按下/抬起的坐标放入有序处理链路；它会过滤普通点击、窗口移动和跨窗口拖动，然后向桌面壳发送 `SelectionGesture`。UIA/剪贴板读取发生在钩子回调之外，防止读取阻塞导致 Windows 移除鼠标钩子。

`WindowsCredentialSecretStore` 使用 Windows Credential Manager 保存 DeepSeek API Key。`settings.json` 只保存普通配置，不包含密钥；非 Windows 平台暂时使用进程内存实现，后续由 macOS Keychain 适配器替换。

`WindowsSingleInstanceGuard` 防止多个 Yita 进程同时注册全局快捷键和鼠标钩子；`WindowsStartupRegistration` 只写入当前用户的 Run 项，不需要管理员权限。

已接入 Avalonia 壳的 Windows 生命周期控制：

- `WindowsSingleInstanceGuard` 防止多个实例争抢快捷键和鼠标钩子；
- `WindowsStartupRegistration` 管理当前用户的开机启动项；
- 设置页和托盘菜单可以即时启用/暂停原生取词；
- UIA Worker、原生控件和安全剪贴板回退继续保持独立的超时边界。

后续会补充 WPS 剪贴板健康诊断、Windows 权限状态页和打包验收。
