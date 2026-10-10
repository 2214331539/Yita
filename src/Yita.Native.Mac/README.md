# Yita.Native.Mac

macOS 原生适配器目前包含 Keychain 凭据存储、修正数据密钥管理和选区读取契约。Keychain 直接调用 Security.framework 的 `SecItemCopyMatching/Add/Update/Delete`，不创建子进程，API Key 不写入设置文件或进程参数。缺失项目与锁定/拒绝访问分开处理，读取时禁止系统认证弹窗并报告失败。

修正记录使用共享 Core 中的 .NET AES-GCM，独立的 256-bit 密钥存入 Keychain。后台初始化时复用已有密钥；首次创建遇到竞争时读取获胜的密钥，不更新它。已有文件的密钥丢失或损坏时禁止新建替代密钥，以保留恢复机会。加密失败不退回明文。账户身份、格式与恢复规则见 [技术架构](../../docs/ARCHITECTURE.md)。

`Yita.Native.Mac.Tests` 使用可控 Keychain 验证错误、取消、竞争及真实 AES 加密文件读写；不访问开发者的真实 Keychain。原生绑定、系统授权、锁定和签名身份仍待 Mac 真机验证，不宣称 macOS 桌面产品可用。

Swift/AppKit helper 已在 `../Yita.Native.Mac.Helper` 实现权限检查和授权入口；Desktop 在 macOS 构建时生成并开发签名。`MacHelperClient` 处理握手、编号、长度限制、超时/取消、崩溃和重启限频，`MacSelectionAdapter` 将响应映射为共享权限与选区模型。协议见 [Mac helper 协议](../../docs/MAC_HELPER_PROTOCOL.md)。

`readSelection` 已接入 AX 读取代码：校验当前前台 PID/Bundle ID，检查安全控件与祖先，优先读取 `AXSelectedText`，再以 `AXSelectedTextRange` / `AXStringForRange` 读取选中范围。范围可用时查询 `AXBoundsForRange`；无边界时保留本次指针锚点。上下文仅在显式开启时读取附近最多 2,000 个 UTF-16 单位，不查询全文 `AXValue`。单次 AX 读取总预算 1.2 秒，每次远程调用限制为最多 150ms；外层 C# helper 时限仍为 3 秒。

坐标为 Quartz 全局点、左上角原点，与当前 Avalonia.Native 11.2.6 的屏幕/窗口位置约定一致，不乘 Retina 渲染倍率。`MacSelectionRuntime` 已消费全局鼠标批次并将 AX/Cmd+C 连接到 Desktop，暂停、设置、外部点击和阅读流程复用共享接口。真实 AX、授权、Retina/多屏和应用兼容性仍待 Mac 真机验证。

原生输入代码已使用只读 CGEvent tap 和 Carbon `Cmd+Shift+T`；输入监控仅由设置页显式请求，快捷键只主动读取剪贴板。回调只入队，新输入序号使旧取词失效；自身窗口、过期事件、修饰键和来源变化受过滤。详见 [Mac 原生输入](../../docs/MAC_NATIVE_INPUT.md)。后续工作：

1. 真实 Accessibility/Input Monitoring 授权、撤销及输入兼容性验收；
2. Retina、多屏、Spaces/全屏和窗口交互验收；
3. 真实菜单栏、登录启动与睡眠恢复验收；
4. 正式 `.app`、签名、公证和更新身份验证。

Cmd+C 回退保存多项/多格式已物化字节，校验前台/焦点和剪贴板变化，在同序列下恢复内容；runtime 根据用户设置显式启用，直接使用 adapter 的默认读取仍只使用 AX。当前 helper 协议为 4，包含输入序号、复制取消与持久会话状态。EOF/父进程退出同样先清理，新的 helper 不与旧复制并发。NSPasteboard 不能原子比较并写入或验证复制所有者；真实格式和竞态仍需验收。详见 [Mac Cmd+C 回退](../../docs/MAC_CLIPBOARD_FALLBACK.md)。

菜单栏使用 Avalonia 已提供的 NSStatusItem/NSMenu，不由 helper 重复创建。`MacStartupRegistration` 为身份符合 `com.yita.desktop` 的 `.app` 配置当前用户下次登录项，源码预览禁用；`MacPopupWindowBehavior` 只修改已有 NSWindow 的 Spaces/全屏辅助属性。睡眠/显示器/用户会话通知取消旧输入，Desktop 隐藏固定窗口并在恢复时无激活地显示。可控测试不代表真实登录、全屏或窗口焦点已通过；详见 [Mac 桌面生命周期](../../docs/MAC_DESKTOP_LIFECYCLE.md)。

helper 必须签名并设置 watchdog；AX provider 异常、超时或权限拒绝只能返回失败结果，不能关闭 Yita 主进程。
