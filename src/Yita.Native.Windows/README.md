# Yita.Native.Windows

该项目承载 Windows 原生能力。当前阶段已提供一条可运行的快捷键取词链路：Avalonia 壳启动后注册 `Ctrl+Shift+T`，读取鼠标位置，向当前前台窗口发送受控 `Ctrl+C`，等待剪贴板序列变化，读取 Unicode 文本并在剪贴板所有权仍属于目标应用时恢复原文。

这条链路是兼容性回退，不会替换旧 WPF 版本的 UI Automation。UIA、WPS PDF 专用路径和鼠标划词钩子将在后续迁移中接入相同的 `ISelectionReader` 契约。

下一阶段将把以下模块从 WPF 宿主移动到这里：

- `IUIAutomation` 直接取词和选区边界；
- 安全的 Ctrl+C 剪贴板事务与恢复；
- RegisterHotKey、低级鼠标钩子和 DPI 坐标转换；
- Windows 托盘、开机启动、单实例和权限边界；
- 外部 UIA provider 隔离进程与 watchdog。
