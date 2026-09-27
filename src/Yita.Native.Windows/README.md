# Yita.Native.Windows

该项目承载 Windows 原生能力。当前阶段先定义跨平台 Core 使用的适配器契约，生产环境仍由现有 `Yita.App` 的 UI Automation 隔离读取器提供委托实现，避免迁移期间改变 WPS/PDF 和浏览器行为。

下一阶段将把以下模块从 WPF 宿主移动到这里：

- `IUIAutomation` 直接取词和选区边界；
- 安全的 Ctrl+C 剪贴板事务与恢复；
- RegisterHotKey、低级鼠标钩子和 DPI 坐标转换；
- Windows 托盘、开机启动、单实例和权限边界；
- 外部 UIA provider 隔离进程与 watchdog。
