# Yita.Native.Mac

macOS 原生适配器目前包含 Keychain 凭据存储、修正数据密钥管理和选区读取契约。Keychain 直接调用 Security.framework 的 `SecItemCopyMatching/Add/Update/Delete`，不创建子进程，API Key 不写入设置文件或进程参数。缺失项目与锁定/拒绝访问分开处理，读取时禁止系统认证弹窗并报告失败。

修正记录使用共享 Core 中的 .NET AES-GCM，独立的 256-bit 密钥存入 Keychain。后台初始化时复用已有密钥；首次创建遇到竞争时读取获胜的密钥，不更新它。已有文件的密钥丢失或损坏时禁止新建替代密钥，以保留恢复机会。加密失败不退回明文。账户身份、格式与恢复规则见 [技术架构](../../docs/ARCHITECTURE.md)。

`Yita.Native.Mac.Tests` 使用可控 Keychain 验证错误、取消、竞争及真实 AES 加密文件读写；不访问开发者的真实 Keychain。原生绑定、系统授权、锁定和签名身份仍待 Mac 真机验证，不宣称 macOS 桌面产品可用。

Swift/AppKit helper 已在 `../Yita.Native.Mac.Helper` 实现权限检查和授权入口；Desktop 在 macOS 构建时生成并开发签名。`MacHelperClient` 处理握手、编号、长度限制、超时/取消、崩溃和重启限频，`MacSelectionAdapter` 将响应映射为共享权限与选区模型。协议见 [Mac helper 协议](../../docs/MAC_HELPER_PROTOCOL.md)。

当前 `readSelection` 明确报告尚未实现。后续通过同一 helper 接入：

1. AXUIElement 读取 `AXSelectedText` 和 `AXSelectedTextRange`；
2. `AXBoundsForRange` 返回选区锚点；
3. 读取失败后以 Cmd+C + NSPasteboard changeCount 执行安全回退；
4. 使用 Carbon/CGEvent 处理全局快捷键和鼠标事件；
5. 使用 NSStatusItem 提供菜单栏入口；
6. 检查 Accessibility 与 Input Monitoring 权限，并返回可诊断状态。

helper 必须签名并设置 watchdog；AX provider 异常、超时或权限拒绝只能返回失败结果，不能关闭 Yita 主进程。
