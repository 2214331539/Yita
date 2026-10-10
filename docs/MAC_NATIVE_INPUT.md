# Mac 原生输入与 Desktop 接入

Swift helper 的全局输入、C# `MacSelectionRuntime` 和 Avalonia 阅读浮窗已接入代码链路并合入 `main`；Apple Silicon 公开预览 DMG 已提供，真实 Mac 授权、外部划词、Retina、Spaces 与全屏行为继续按验收记录确认。此文档不表示真机验收已完成。

## 输入到翻译

1. `configureInput` 启动只读 CGEvent tap 和 Carbon 全局快捷键。鼠标 tap 使用独立 run loop，回调只过滤并入队，不执行 AX、网络或剪贴板访问。
2. `MacSelectionRuntime` 约每 120ms 通过 `pollInput` 取回有界事件批次，复用权限与取词的同一个 helper/client。没有持续轮询外部选区，也不向 stdout 混入未编号的事件帧。
3. 拖选开始时保存来源 PID/Bundle ID 和指针；松开时验证两端来源一致、没有修饰键、不是自身窗口，并过滤普通点击。满足条件后等待用户设置的取词延迟。
4. 读取请求携带原生输入序号。新鼠标、键盘输入或前台变化会增加序号；helper 在读取前、AX 检查/复制等待期间及返回前验证它。失效结果不能进入下一次选区。复制取消仍按既有事务规则恢复。
5. 成功结果通过共享 `SelectionCaptured` 进入 `MainWindow`，使用现有请求生命周期、翻译提供器、流式输出、浮窗与相对位置逻辑。

鼠标服务暂停自动翻译后仍保留外部点击关闭和手动剪贴板入口，但不会发起自动 AX 或 Cmd+C。手动菜单/快捷键只读取当前剪贴板，不发送复制、不附加上下文。

## 权限与快捷键

| 能力 | 实现 | 所需条件 |
| --- | --- | --- |
| 全局拖选、外部点击和取消输入 | CGEvent tap，`listenOnly` | Input Monitoring；实际 tap 创建成功 |
| 自动直接取词 | AXUIElement | Accessibility；本次来源与安全检查通过 |
| 自动复制回退 | private CGEventSource + NSPasteboard | Accessibility 和事件投递权限；允许回退及可恢复剪贴板 |
| 手动翻译剪贴板 | Carbon 注册 `Cmd+Shift+T` | 注册成功；不依赖 AX 或鼠标监听权限 |

快捷键是“先 Cmd+C，再 Cmd+Shift+T”，与 Windows 的手动剪贴板语义一致。按住快捷键时只响应一次，释放后才能再次触发；注册失败保留菜单入口。

启动和状态查询不弹授权框。设置页分别提供用户主动请求 Accessibility、请求 Input Monitoring 和打开对应系统设置的按钮。授权请求返回不代表用户已经同意，需要再次检查；监听器根据实际权限定期更新。权限撤销时停止 tap，不继续翻译旧手势。

## 有界事件与自身窗口

- 队列最多 64 项，只传事件类别、坐标、序号、年龄、修饰键状态与来源身份。键盘事件仅使旧读取失效，不保留键码或输入文字；序号和身份不写入诊断。
- 超过 500ms 的排队事件、未来时间、溢出和前台变化转换为取消事件，不能完成一段旧拖选。C# 也计入收到批次后的处理时间，防止 UI 查询等待后使用旧事件。
- 前台身份通过 NSWorkspace 激活通知与定期刷新缓存；若拖选期间应用激活变化，两端来源不一致会放弃本次读取。首次从未激活窗口直接拖选可能需要重新选取，实际行为待兼容性验收。
- helper 在回调外按窗口前后层级检查可见窗口元数据，只过滤指针处最上层的自身或父进程窗口，覆盖不抢前台焦点的阅读浮窗。Desktop 不再按所有可见窗口的几何范围重复过滤，以免后台被遮挡的设置窗口阻止其他应用划词。窗口检查不查询或记录标题。
- 标记为 Yita 注入的复制事件不进入输入队列，避免回退再次触发翻译。真实系统对标记和 HID 计数的行为仍需验证。
- tap 停止后每 30 秒最多尝试创建 3 次；客户端另有 helper 重启限制。退出、EOF 和父进程消失释放监听、Carbon 注册及通知订阅；已有剪贴板事务先收尾。

## 坐标约定

协议 4 增加 `sessionActive/sessionGeneration`，每次轮询返回持久睡眠/会话状态。暂停时输入队列不接受新事件，旧选区取消、tap 停止、快捷键注销；恢复重新配置。Desktop 隐藏固定阅读窗口并在恢复时无激活地显示，不重译旧选区。菜单、登录项、Spaces 属性和验证边界见 [Mac 桌面生命周期](MAC_DESKTOP_LIFECYCLE.md)。

CGEvent 和 AX 边界均使用 Quartz 全局点、左上角原点。当前 Avalonia.Native 11.2.6 的屏幕范围和窗口位置也使用这些全局点，其 `Screen.Scaling` 为 1；Retina 的 `RenderScaling` 用于渲染，不应再次乘到浮窗位置或窗口命中范围上。

因此当前宿主直接传递 Quartz 点，保留外接屏的负坐标，并复用共享工作区约束和相对偏移。没有 AX 边界时使用本次拖选区域/指针，不伪造选区边界。升级 Avalonia 后须重新检查此约定。依据：[Screens.mm](https://github.com/AvaloniaUI/Avalonia/blob/11.2.6/native/Avalonia.Native/src/OSX/Screens.mm)、[WindowBaseImpl.mm](https://github.com/AvaloniaUI/Avalonia/blob/11.2.6/native/Avalonia.Native/src/OSX/WindowBaseImpl.mm)。

选中文字必须在 AX 总预算内取得。已经取得正文后，范围、边界和可选上下文查询的 `timeout/unsupported/unavailable` 不再使正文失效；预算用尽后跳过剩余元数据。返回前仍核验权限、前台来源及输入取消，关键安全错误继续拒绝全部正文。未取得正文时用于读取文本的范围和参数化查询仍属于必需操作。

## 非交互验证与后续验收

`--input-self-test` 验证队列的来源、顺序、溢出、过期、修饰键、自身进程与单次交付，完全使用合成输入，不创建 tap、注册热键或检查真实权限。

`--self-test --selection-fixture range --input-fixture drag` 将合成拖选通过实际 Swift/C# 管道送入 `MacSelectionRuntime`，再读取合成 AX 选区；另验证旧序号被拒绝、手动剪贴板、无重复事件和禁用授权动作。C# 回归覆盖暂停、过滤自身窗口、来源切换、取消被忽略后的迟到结果与权限宿主复用。

Mac 真机仍需复验：实际 tap 权限/撤销/恢复、Carbon 快捷键冲突和释放、浏览器/Preview/编辑器取词、普通复制、富文本恢复、非激活浮窗交互、Retina/多屏/Spaces/全屏、睡眠唤醒与长期运行。菜单栏复用 Avalonia NativeMenu/TrayIcon；登录启动和预览分发已有实现，正式签名公证与实际平台验收仍待完成。

2026-10-02，代码提交 `2f02aa2` 的 [GitHub Actions #36980861240](https://github.com/2214331539/Yita/actions/runs/36980861240) 在 Windows/macOS 各通过 434 项测试并零警告/错误构建。Mac 另通过实际 Swift/C# 管道的 27 项检查，以及 17 组 AX、22 组剪贴板和 12 组输入策略测试。此记录验证构建和可控链路，不替代上述真机验收。
