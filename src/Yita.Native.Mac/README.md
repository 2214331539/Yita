# Yita.Native.Mac

macOS 原生适配器目前包含 Keychain 凭据存储、修正数据密钥管理和选区读取契约。Keychain 直接调用 Security.framework 的 `SecItemCopyMatching/Add/Update/Delete`，不创建子进程，API Key 不写入设置文件或进程参数。缺失项目与锁定/拒绝访问分开处理，读取时禁止系统认证弹窗并报告失败。

修正记录使用共享 Core 中的 .NET AES-GCM，独立的 256-bit 密钥存入 Keychain。后台初始化时复用已有密钥；首次创建遇到竞争时读取获胜的密钥，不更新它。已有文件的密钥丢失或损坏时禁止新建替代密钥，以保留恢复机会。加密失败不退回明文。账户身份、格式与恢复规则见 [技术架构](../../docs/ARCHITECTURE.md)。

`Yita.Native.Mac.Tests` 使用可控 Keychain 验证错误、取消、竞争及真实 AES 加密文件读写；不访问开发者的真实 Keychain。原生绑定、系统授权、锁定和签名身份仍待 Mac 真机验证，不宣称 macOS 桌面产品可用。

Swift/AppKit helper 已在 `../Yita.Native.Mac.Helper` 实现权限检查和授权入口；Desktop 在 macOS 构建时生成并开发签名。`MacHelperClient` 处理握手、编号、长度限制、超时/取消、崩溃和重启限频，`MacSelectionAdapter` 将响应映射为共享权限与选区模型。协议见 [Mac helper 协议](../../docs/MAC_HELPER_PROTOCOL.md)。

`readSelection` 已接入 AX 读取代码：校验当前前台 PID/Bundle ID，检查安全控件与祖先，优先读取 `AXSelectedText`，再以 `AXSelectedTextRange` / `AXStringForRange` 读取选中范围。范围可用时查询 `AXBoundsForRange`；无边界时保留本次指针锚点。上下文仅在显式开启时读取附近最多 2,000 个 UTF-16 单位，不查询全文 `AXValue`。单次 AX 读取总预算 1.2 秒，每次远程调用限制为最多 150ms；外层 C# helper 时限仍为 3 秒。

坐标当前为 Quartz 全局点、左上角原点，未完成到 Avalonia 屏幕逻辑单位的转换与 Retina/多屏验收。选区读取已实现但尚未接入 Mac 拖选/快捷键事件，Desktop 的自动划词控件仍禁用；真实 AX、授权和应用兼容性仍待 Mac 真机验证。

后续通过同一 helper 接入：

1. 读取失败后以 Cmd+C + NSPasteboard changeCount 执行安全回退；
2. 使用 Carbon/CGEvent 处理全局快捷键和鼠标事件；
3. 坐标转换、来源身份和 `ISelectionRuntime` 接入 Desktop；
4. 使用 NSStatusItem 提供菜单栏入口；
5. 检查 Accessibility 与 Input Monitoring 权限变化，并返回可诊断状态。

helper 必须签名并设置 watchdog；AX provider 异常、超时或权限拒绝只能返回失败结果，不能关闭 Yita 主进程。
