# Yita 技术架构

本文描述 `main` 当前代码的实际组织。主应用使用 C#、.NET 8 和 Avalonia 11.2.6；Windows 已实现划词翻译，macOS 原生取词仍待完成。旧 WPF 产品的完整快照保留在 `codex/csharp-wpf-legacy`。

## 模块边界

| 模块 | 入口与主要职责 |
| --- | --- |
| `Yita.Core` | `Parity/ReferenceTranslationRuntime.cs` 编排当前翻译；`Settings/YitaSettings.cs` 保存偏好；`LatestRequestController.cs` 管理取消和版本；`Placement/PopupPlacement.cs` 处理位置 |
| `Yita.Desktop` | `App.axaml.cs` 管理生命周期与原生服务；`MainWindow` 管理设置和阅读会话；`TranslationPopupWindow`、`QuestionAnswerWindow` 呈现翻译与问答 |
| `Yita.Native.Windows` | `WindowsSelectionRuntime` 接收鼠标/快捷键；`WindowsSelectionAdapter` 组织取词；剪贴板、托盘、凭据、启动项与显示偏好各自独立 |
| `Yita.Native.Windows.UIA.Worker` | Windows 专属 helper，独立访问 UI Automation provider |
| `Yita.Native.Mac` | 选区与权限契约、Keychain 适配器；AX 和全局输入 helper 尚未实现 |

Desktop 当前使用 XAML 与窗口 code-behind/partial class 配合独立业务服务，不宣称已经完成完整 MVVM 或平台无关宿主抽象。它通过操作系统判断接入 Windows runtime；macOS 仍需补齐对应接入点。

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

## 平台边界与发布

Core 和 Desktop 可以在 Windows/macOS runner 上构建。macOS 已有 Keychain 适配代码，但 `MacSelectionAdapter` 尚未真正读取 AX 选区、请求权限或执行 Cmd+C，Desktop 也尚未注册 macOS 全局输入。因此编译成功不代表 macOS 功能完成。

下一步 macOS 需要实现和接入签名原生 helper：AXUIElement、选区坐标、Cmd+C/NSPasteboard、全局快捷键与鼠标、Accessibility/Input Monitoring、NSStatusItem，以及超时恢复和真机验收。Linux 当前不在交付范围。

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
