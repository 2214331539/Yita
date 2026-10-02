# Mac 桌面生命周期

本实现位于 `codex/platform-host-services`，尚未合入 `main`。菜单栏、登录项注册、休眠恢复和浮窗 Spaces 属性已有代码；真实 Mac 桌面验收与正式安装包仍暂缓。源码预览不注册登录项。

## 菜单栏

Desktop 复用 Avalonia 11.2.6 的 `TrayIcon` 和 `NativeMenu`。其 macOS 实现直接创建 `NSStatusItem` 和 `NSMenu`，不需要 helper 再创建第二个菜单栏图标。依据：[trayicon.mm](https://github.com/AvaloniaUI/Avalonia/blob/11.2.6/native/Avalonia.Native/src/OSX/trayicon.mm)。

菜单包含手动翻译剪贴板、设置、启用/暂停划词、修复输入、性能诊断、关于和退出。启用状态与界面语言同步；手动翻译调用同一 runtime，使用当前指针，不模拟 Cmd+C。命令异常受隔离，退出时隐藏/释放图标，已排队或已释放的入口不再执行命令。Windows 继续使用原有原生托盘与 Yita 自绘菜单。

## 登录时启动

`MacStartupRegistration` 使用当前用户的 `~/Library/LaunchAgents/com.yita.desktop.login.plist`。该方案覆盖当前 helper 的 macOS 12 构建目标；暂不引入仅 macOS 13+ 的 `SMAppService`。它只配置下次 GUI 登录，不执行 `launchctl bootstrap` 或立即启动另一个实例。

支持条件：

- 进程从 `.app/Contents/MacOS/Yita.Desktop` 启动，且可执行文件存在、具有执行权限。
- `Contents/Info.plist` 是有界 XML plist，声明 `CFBundleIdentifier=com.yita.desktop`、`CFBundleExecutable=Yita.Desktop`、`CFBundlePackageType=APPL`。
- 配置和应用路径不经过符号链接。源码可执行文件、`dotnet` 宿主、缺失/错误或二进制 plist 元数据禁用此入口；后续 `.app` 构建需输出符合上述约定的 XML 元数据。

plist 的 `ProgramArguments` 分别保存可执行路径和 `--background`，不拼接 shell 命令。`RunAtLoad=true`、`LimitLoadToSessionType=Aqua`、`ProcessType=Interactive`；`AssociatedBundleIdentifiers` 指向主应用，不注册 native helper 为登录项。不设置 `KeepAlive`，用户退出后不会被重新拉起。

写入使用同目录临时文件与重命名，并限制当前用户读写。已存在的 plist 只有在标签、完整字段、关联身份和参数结构符合本应用生成的配置时才更新/删除；冲突、非法 XML、超大文件或链接均保留并报告失败。应用移动后可重新保存以更新自身配置路径；禁用可清理原有注册，即使旧应用文件已移除。不会删除其他登录项。

注册失败发生在设置/凭据写入前；后续存储失败时尝试恢复原登录偏好。系统登录项允许状态仍由 macOS 管理，写入文件不等于系统已经授权运行。登录启动、系统背景项目提示、应用移动/更新和退出后不再启动，须通过真实安装版验收。

## 睡眠与用户会话

Swift helper 在 Cocoa 主线程订阅 NSWorkspace 的系统睡眠/唤醒、显示器睡眠/唤醒及用户会话失活/恢复通知。系统、显示器与用户会话分别保存暂停原因，全部解除后才恢复；重复通知不会重复增加状态代数。

协议 4 在每个 `input` snapshot 中返回 `sessionActive` 和 `sessionGeneration`。这些是持久状态，不依赖可能过期或溢出的短时输入事件。暂停时清空旧事件、增加选区序号、停止鼠标 tap 和注销快捷键；读取选区和剪贴板均返回取消。恢复时重新配置监听和快捷键，保留既有限频和权限检查。退出/EOF 清理通知订阅。

`MacSelectionRuntime` 实现共享 `IDesktopSessionRuntime`，取消旧 AX/复制请求并把会话状态派发给 Desktop。即使 host 错过一次完整睡眠/唤醒，状态代数的变化仍使旧拖选和请求失效；旧选区不自动重译。

Desktop 暂停时取消翻译、解释和问答，关闭/隐藏未固定窗口；固定窗口保留当前内容并隐藏。恢复只重新显示原先可见的固定窗口，保留固定和位置，不重新请求模型，也不激活窗口；不可见或关闭的窗口不被恢复。若外接屏移除，位置限制到现有工作区。初始等待被明确标为停止，流式半成品不冒充完成结果，后续可重新划词或重试问答。

这组公开 NSWorkspace 通知覆盖已收到的睡眠、显示器和会话变化；没有引入私有锁屏通知，也不承诺识别所有“屏幕仍亮”的锁屏情形。通知需要 Cocoa 主循环调度，主线程正执行有界 AX 调用时可能延后。真实锁屏、睡眠期间 helper 超时、快速切换用户和权限撤销仍待验收。

## 浮窗与 Spaces

Avalonia 创建和绘制现有浮窗，不建立第二套原生 UI。`MacPopupWindowBehavior` 仅在 macOS Cocoa 主线程、确认 `NSWindow` handle 后，以正确的指针宽度调用 Objective-C `collectionBehavior`。

- 设置 `CanJoinAllSpaces`、`Transient`、`IgnoresCycle`、`FullScreenAuxiliary`。
- 清除与上述组合冲突的 `MoveToActiveSpace`、`Managed`、`ParticipatesInCycle`、`FullScreenPrimary` 和 `FullScreenNone`，保留其他标志。
- 初次翻译仍沿用 `ShowActivated=false`，置顶沿用 Avalonia 的 floating window level；没有更换 NSWindow 类型或提高到遮盖系统 UI 的级别。

Avalonia 的普通窗口默认设置 `FullScreenPrimary`，因此只设置 `Topmost` 不足以表达阅读辅助窗口的 Spaces 意图。依据：[WindowImpl.mm](https://github.com/AvaloniaUI/Avalonia/blob/11.2.6/native/Avalonia.Native/src/OSX/WindowImpl.mm)、[WindowBaseImpl.mm](https://github.com/AvaloniaUI/Avalonia/blob/11.2.6/native/Avalonia.Native/src/OSX/WindowBaseImpl.mm)。这些属性是代码配置，不是所有全屏/Stage Manager/多屏场景都有效的实测结论；是否还需 NSPanel 必须由后续真机结果决定。

## 验证边界

自动化覆盖：登录 plist 的参数/身份/冲突/更新/删除/DTD 和链接保护；原生 session 多原因状态、重复通知、队列清空和快捷键过滤；实际 Swift/C# 管道的暂停/恢复与取消读取；共享窗口的固定保留、隐藏/恢复、取消后不重译、菜单本地化与退出清理。

2026-10-02，代码提交 `4b391b5` 的 [GitHub Actions #36982899304](https://github.com/2214331539/Yita/actions/runs/36982899304) 在 Windows/macOS 各通过 456 项测试和零警告/错误构建；Mac 另通过 34 项实际 Swift/C# 管道、17 组 AX、22 组剪贴板和 16 组原生输入策略检查。本机 Windows 源码预览已重新启动。

测试只在临时目录写入合成 `.app` 元数据和 plist，不注册 runner 的真实登录项，不申请授权或访问外部选区。Spaces 标志计算测试不替代真实 NSWindow 运行测试。

待真机验收：菜单点击和系统语言渲染、首次/撤销授权、GUI 登录、背景项目许可、正常退出、睡眠/锁屏/用户切换、来源焦点、Retina/外接屏移除、Spaces/全屏/Stage Manager、固定窗口恢复和正式签名更新。
