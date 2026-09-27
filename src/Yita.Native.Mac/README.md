# Yita.Native.Mac

macOS 原生适配器的契约和占位实现。真实实现将在 macOS runner 上使用 Swift/Objective-C helper 完成，并通过受限的本地 IPC 返回结构化结果：

1. AXUIElement 读取 `AXSelectedText` 和 `AXSelectedTextRange`；
2. `AXBoundsForRange` 返回选区锚点；
3. 读取失败后以 Cmd+C + NSPasteboard changeCount 执行安全回退；
4. 使用 Carbon/CGEvent 处理全局快捷键和鼠标事件；
5. 使用 NSStatusItem 提供菜单栏入口；
6. 检查 Accessibility 与 Input Monitoring 权限，并返回可诊断状态。

helper 必须签名并设置 watchdog；AX provider 异常、超时或权限拒绝只能返回失败结果，不能关闭 Yita 主进程。
