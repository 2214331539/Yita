# Yita 技术架构

本文描述 `main` 当前代码的实际组织。主应用使用 C#、.NET 8 和 Avalonia 11.2.6；Windows 已实现划词翻译，macOS 原生取词仍待完成。旧 WPF 产品的完整快照保留在 `codex/csharp-wpf-legacy`。

本文的平台宿主与存储说明对应 `codex/platform-host-services` 功能分支，尚未合入 `main`。该分支保持 Windows 取词实现，新增非 Windows 单实例、手动剪贴板入口、设置 schema 和 Mac 加密存储实现；不代表 macOS 自动划词已完成。

## 模块边界

| 模块 | 入口与主要职责 |
| --- | --- |
| `Yita.Core` | `Parity/ReferenceTranslationRuntime.cs` 编排当前翻译；`Settings` 保存偏好、迁移和防止降级覆盖；`LatestRequestController.cs` 管理取消和版本；`Placement/PopupPlacement.cs` 处理位置；`Platform` 定义宿主契约、单实例和 AES-GCM 修正数据保护 |
| `Yita.Desktop` | `App.axaml.cs` 管理生命周期；`Platform/DesktopPlatformServices.cs` 创建系统实现；`MainWindow` 订阅共享输入事件并管理设置和阅读会话；`TranslationPopupWindow`、`QuestionAnswerWindow` 呈现翻译与问答 |
| `Yita.Native.Windows` | `WindowsSelectionRuntime` 接收鼠标/快捷键；`WindowsSelectionAdapter` 组织取词；剪贴板、托盘、凭据、启动项与显示偏好各自独立 |
| `Yita.Native.Windows.UIA.Worker` | Windows 专属 helper，独立访问 UI Automation provider |
| `Yita.Native.Mac` | 原生 helper 客户端、权限及结构化选区结果、Security.framework Keychain 适配器和修正数据密钥管理；全局输入尚未实现 |
| `Yita.Native.Mac.Helper` | Swift/AppKit agent，权限、AX 选区与边界、显式启用的安全 Cmd+C 回退及取消控制；当前不捕获全局输入 |

Desktop 当前使用 XAML 与窗口 code-behind/partial class 配合独立业务服务，不宣称已经完成完整 MVVM。App、设置和托盘已通过共享宿主接口接入，系统实现选择集中在工厂；字体/动效的显示偏好仍有 Windows 专属调用。macOS 的 `ISelectionRuntime` 与登录启动尚未实现，安全存储需真机复验，窗口原生行为仍需补齐。

## 平台宿主接口

`ISelectionRuntime` 提供启用配置、选区和外部点击事件、手动剪贴板翻译、修复及诊断。Windows 使用原 `WindowsSelectionRuntime` 实现；未来 Mac helper 通过同一事件模型连接设置和阅读浮窗。WPS 兼容参数为 Windows 特有偏好，Mac 不应将其视作自己的复制策略。

原生事件可能来自后台线程，`MainWindow` 负责派发到 Avalonia UI 线程。关闭服务时解除事件订阅，已排队的回调再次检查关闭状态；runtime 生命周期仍由 App 管理，窗口不自行销毁输入服务。

`ISingleInstanceGuard` 将实例归属和唤醒分离：Windows 沿用原互斥量/事件实现；非 Windows 使用文件独占句柄和当前用户专用管道。锁文件在退出时不删除，避免不同 inode 造成两个实例同时拥有锁。新进程发送有时限的一字节唤醒消息，主实例在设置窗口建立前保留待唤醒状态；后台启动不发送消息。

`IStartupRegistration` 明确是否支持启动项，未实现的平台禁用开关。`IStatusIcon` 提供原生图标事件；Windows 保持自绘 Yita 菜单，其他平台先使用 Avalonia NativeMenu。菜单手动翻译可以通过 Avalonia 剪贴板工作，但此时还没有 Mac 全局快捷键或选区锚点，只以主屏中心放置浮窗。

系统凭据和修正数据保护由平台工厂创建。Mac Keychain 使用 Security.framework 的 SecItem API，不再启动 `security` 子进程；后台初始化密钥和修正数据，不在窗口输入回调中访问 Keychain。缺少输入服务时禁用自动划词、复制回退与 WPS 控件，并说明手动剪贴板入口；诊断列出启动项、凭据、设置和加密记忆状态，不包含正文、Key 或数据路径。

## 原版代码复用

`Yita.Core/Parity/OriginalBehavior.props` 通过 MSBuild `Compile Include` 与 `Link` 复用 `Yita.App` 中不依赖 WPF 的代码，包括翻译提供器、提示词、SSE、重试、断路器、缓存、设置目录、AI 记录/总结、诊断和尺寸计算。

这些文件仍位于原路径，因此仓库中保留 `src/Yita.App` 是当前共享 Core 构建的一部分，不应直接删除。旧 WPF 的 UI、鼠标钩子和窗口协调器没有作为 Avalonia 产品 UI 运行。

Core 中早期的 `TranslationCoordinator`、`DeepSeekStreamingTranslator` 与通用历史接口仍保留。当前桌面实际入口使用 `ReferenceTranslationRuntime`，以复用原版翻译行为。后续统一接口时，需要保留现有协议与行为回归。

## Windows 取词链路

1. 低级鼠标钩子在独立消息线程捕获按下/抬起，将事件入队；回调不进行 UIA、剪贴板或网络请求。
2. 手势处理过滤普通点击、窗口拖动、跨窗口与本应用内选择。生产钩子忽略注入的鼠标事件。
3. `WindowsSelectionRuntime` 等待设置中的短延迟，建立 `SelectionRequest`，启动可取消的取词请求。
4. WPS PDF 兼容开启时优先复制；其他程序先尝试 UIA worker 和原生文本控件。Chromium/Electron 一类目标允许短暂的选区稳定重试。
5. 无可读选区且兼容回退开启时，向仍处于前台、具有有效焦点的原目标发送受控 Ctrl+C。密码控件、终端和无法完整保存的剪贴板停止复制。
6. 等待目标拥有的剪贴板序列与格式稳定，读取 Unicode 文本；在没有后续用户复制时恢复原有文本、富文本或其他可保存格式。
7. 结构化 `SelectionResult` 返回 Desktop；成功后在 UI 线程创建或复用未固定浮窗，再调用 Core 翻译。

WPS 识别包含 `wpspdf`、`kpdf` 与 WPS 窗口内的 PDF 渲染进程。剪贴板归属限定为原窗口、命中窗口和同一窗口内的焦点进程，不能接受任意其他 WPS 文档的内容。

`Ctrl+Shift+T` 与托盘“翻译剪贴板”走现有剪贴板读取路径，不模拟 Ctrl+C。它们在暂停自动划词后仍可使用。

## UIA 故障隔离

第三方 UI Automation provider 可能阻塞或发生原生崩溃。`WindowsUiAutomationWorkerClient` 在独立 helper 进程中访问它，通过标准输入/输出传递 JSON 行，约束响应大小和请求时限。helper 缺失、超时或异常返回失败，主程序继续执行原生控件或兼容回退。

Windows 构建自动将 `Yita.UIA.Worker.exe` 及其依赖放在 Desktop 输出目录。helper 使用 `net8.0-windows` 和 WindowsDesktop 运行时引用；它不显示产品窗口。发布时必须与主程序一起打包，不能仅分发 `Yita.Desktop.exe`。

## 翻译与会话

- 请求按文本、方向、模式、语气、上下文、术语和相关修正构造。已保存的精确修正可直接复用，普通缓存包含提供器、地址、模型与请求特征。
- `ReferenceTranslationRuntime` 执行请求合并、并发限制、超时和取消，并使用原版提供器处理 HTTP/SSE、重试与协议错误。
- 每个浮窗拥有独立请求控制器。后续选区取消未固定窗口的旧请求，版本检查阻止迟到内容覆盖；固定窗口保留自己的会话。
- 解释、追问和独立聊天使用不同上下文。AI 记录有独立保存状态和去重，不能因旧请求完成而改变新内容的保存状态。

浮窗尺寸结合文本测量、屏幕工作区与最大约束自动计算；用户手动缩放后尊重其尺寸。保存的拖动偏移是相对当前选区锚点的逻辑单位，下次结合新锚点与 DPI 重新计算，避免把上次绝对位置复用到其他选区。

## 设置、语言与数据

模型、服务与凭据在保存后用于系统划词；设置页未保存的模型输入只用于显式连接测试。界面语言支持即时预览，传播到托盘、翻译和问答窗口；取消恢复已保存语言，不改写现有译文。

| 数据 | Windows 实现 |
| --- | --- |
| 普通设置和相对偏移 | `%LOCALAPPDATA%\Yita\desktop-settings.json`，原子替换写入 |
| API Key | Windows Credential Manager，`Yita:DeepSeekApiKey` |
| 主动保存的修正 | `desktop-translation-memory.dat`，DPAPI 当前用户加密 |
| 普通缓存 | 内存，进程退出后清空 |
| AI 解释与问答记录 | 用户目录中的明文 Markdown，默认不自动保存 |
| 健康诊断 | `desktop-runtime-health.log` |

没有新设置时可导入旧 WPF 阅读偏好；开机启动和 AI 记录目的地保持独立。首次升级旧预览配置会开启通用剪贴板回退，后续显式关闭并保存会被保留。

`JsonSettingsStore` 当前 `schemaVersion` 为 1，无该字段的旧预览配置视为 0。加载只做内存迁移；第一次保存升级前，以原字节创建 `desktop-settings.json.schema-0.bak`，不覆盖已存在的备份。设置采用临时文件、刷新和原子替换，保存串行化且尊重取消。文件超过 1 MiB、内容损坏、不可读或版本高于当前实现时返回明确错误，禁止默认值覆盖和偷偷导入旧设置。保存前再次校验磁盘文件，避免已经加载的旧配置覆盖后来写入的新版本；更改凭据前也先检查设置版本。错误状态显示在设置页并禁用保存，恢复可读配置后重启加载。

Mac 数据目录由 `Environment.SpecialFolder.LocalApplicationData` 解析，并使用同样的 `Yita` 子目录和 Desktop 文件名；它不是 Windows 数据的自动同步副本。API Key 保存在 Keychain 服务 `com.yita.Yita` / 账户 `deepseek-api-key`，保留原适配器的项目身份。修正记忆采用独立随机 256-bit 密钥，存于同一服务的 `translation-memory-key-v1`；记录文件使用 .NET AES-GCM、随机 96-bit nonce、128-bit tag 和经过认证的格式头。首次创建密钥遇到竞争时读取获胜者，绝不更新已有加密密钥。

已存在记录却缺少密钥、密钥无效、Keychain 拒绝访问或密文验证失败时保留原文件，禁止保存修正，普通翻译仍可使用；没有明文回退。Windows DPAPI 文件不能直接复制到 Mac 解密。AES 密钥在退出时清零；显式清除修正只删除记录，保留账户密钥以便以后继续保存。Keychain 原生绑定、锁定/授权/签名变化仍需真实 Mac 验收；伪 Keychain 和 AES 测试不能代替它。

## 平台边界与发布

Core 和 Desktop 可以在 Windows/macOS runner 上构建。功能分支的 `MacSelectionAdapter` 已通过 Swift helper 接入权限、AX 和显式启用的 Cmd+C 回退代码，但 Desktop 尚未注册 macOS 全局输入。因此编译成功或可控测试通过不代表 macOS 划词功能完成。

功能分支已实现 Swift helper 的开发 `.app`、ad-hoc 签名、请求编号与版本校验、有限长度读写、超时/取消隔离、父进程监控和重启限频。Mac 设置页提供检查、请求 Accessibility 授权和系统设置入口；只检查不自动请求授权。状态检查和已实现能力分开，仍禁用未接入的自动取词与快捷键。协议及非交互 self-test 见 [Mac helper 协议](MAC_HELPER_PROTOCOL.md)。

AX 读取仅针对当前前台应用，匹配请求中的 PID/Bundle ID，并在返回前再次检查来源。检查聚焦元素、鼠标命中元素及各自最多 16 层祖先，拒绝安全文本框、循环或无法在限额内确认的路径。优先读取 `AXSelectedText`，无直接文本时根据合法 `AXSelectedTextRange` 调用 `AXStringForRange`，不查询全文 `AXValue`。读取预算 1.2 秒，远程 AX 消息最多 150ms；错误返回结构化选区失败，主程序不直接进入第三方 AX 调用。

文本上限为 20,000 个 UTF-16 单位，超限停止此次操作而不静默截断。上下文仅在明确启用时查询附近最多 2,000 个单位。`AXBoundsForRange` 坐标暂以 Quartz 全局点返回，边界不可用时为 null；Desktop 的坐标转换、Retina、多屏和原生窗口行为仍待后续接入与真机验证。Swift 可控测试与 self-test 的合成文本只能验证读取策略和通信，不替代真实应用的 AX 支持验收。

`ReadAsync(request, allowClipboardFallback:true)` 在 AX 空选区或不支持时尝试 Cmd+C。先检查前台和焦点、可复制角色、AX/事件投递权限、安全祖先、修饰键及可完整物化的多格式剪贴板。使用标记的 private CGEventSource 向原 PID 投递复制；以单次 changeCount、HID 输入计数与 80ms 稳定状态归属，在同序列下恢复原始字节。协议 2 支持复制取消和 EOF 清理，客户端等待清理完成后才启动新 helper。NSPasteboard 缺少原子序列写入和所有者校验，这套规则无法消除所有系统竞态；具体限制、手动回退和真实验收要求见 [Mac Cmd+C 回退](MAC_CLIPBOARD_FALLBACK.md)。

下一步 macOS 需要捕获全局快捷键与鼠标，将已有 AX/Cmd+C 读取连接到 Desktop，完成屏幕坐标转换与 NSStatusItem 菜单栏，并开展真实权限、窗口行为和签名验收。当前开发签名不替代正式包的 Developer ID、公证与更新后 TCC 检查。Linux 当前不在交付范围。

Avalonia 新架构的自包含发布、Setup 安装和升级尚未验收。旧 `Build-Setup.ps1` 与 `Publish.ps1` 构建 WPF；现有 release 工作流已禁止从包含 Desktop 项目的新标签发布该旧产物。历史 Release 与标签保留，不因 main 切换而重打包。

## 分支保留

2026-10-02，按项目维护者要求将 Avalonia 版本提升为 main，保留切换前两个不同的 main 基准：

| 保留分支 | 原引用 | 提交 |
| --- | --- | --- |
| `codex/csharp-wpf-legacy` | GitHub `origin/main`，旧 Yita WPF | `4ad91b758e55f4ca5abe86d96dd62a1d157708c1` |
| `codex/csharp-wpf-upstream-baseline` | 本地 `main`，上游 WPF | `a6a5c32086d0c24e1e322f57a46e75b89ea26833` |

main 从旧远程 main 的后代正常向前推进，不强制推送，也不重写已有提交与版本标签。

## 验证

当前解决方案的测试覆盖共享合约、原版业务回归、请求生命周期、Windows 取词策略、ABI、Avalonia 布局与交互。原生 smoke 另外验证实际 UIA、Ctrl+C、取消后的剪贴板恢复、托盘和输入钩子；它不能替代每个真实 PDF 阅读器的手动测试。

维护者已经确认本机 WPS PDF 划词、语言同步、托盘位置与普通复制验收通过。其他系统、应用版本、权限和混合 DPI 仍需相应环境验证。详见 [Windows 验收记录](WINDOWS_AVALONIA_ACCEPTANCE.md)。
