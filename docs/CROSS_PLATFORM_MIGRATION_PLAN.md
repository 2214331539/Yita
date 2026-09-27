# Yita 跨平台迁移开发计划

## 目标

在保留当前 Windows 版本稳定性的前提下，将 Yita 演进为同时支持 Windows 10/11 x64、macOS Intel 和 Apple Silicon 的桌面应用。核心体验保持一致：用户在任意支持文本选择的应用中划词后，使用 Yita 快捷键或划词触发翻译，Yita 在选区附近显示可拖拽、可调整大小并支持流式输出的翻译浮窗。

“任意应用”按操作系统公开的无障碍和剪贴板能力定义为：浏览器、编辑器、办公软件、PDF 阅读器等常见文本应用优先支持；密码框、DRM、远程桌面、游戏画布和拒绝系统辅助功能的应用无法承诺读取文本，应用必须给出可理解的失败原因和手动复制回退入口。

## 当前基线与分支策略

- 基线：`v0.8.4` 的 Yita Windows 版本，包含 UI 重构、浮窗自适应、UI Automation 隔离和相对位置记忆。
- 开发分支：`codex/cross-platform-migration`。
- `main` 不直接修改。每个可审查阶段使用独立提交；Windows 回归通过后再合并。
- 旧的 WPF 应用在迁移完成前继续作为 Windows 回退实现，不删除现有安装和发布脚本。

## 目标架构

```text
Yita.Core                         纯跨平台业务层（net8.0）
├── DeepSeek 流式客户端
├── 翻译/解释/问答请求与取消
├── 设置、凭据和记录存储抽象
├── 翻译缓存、术语表和历史
├── 选区数据、错误模型和诊断事件
└── 浮窗相对定位与尺寸计算

Yita.Desktop                     Avalonia 桌面 UI
├── 设置页和侧边导航
├── 翻译/原文阅读浮窗
├── 托盘或菜单栏入口
├── 权限引导和诊断页
└── 通过 IPC/接口接收原生选区事件

Yita.Native.Windows              Windows 原生适配器
├── UI Automation 选区读取
├── 安全 Ctrl+C 剪贴板回退
├── 全局快捷键、鼠标钩子和 DPI 坐标
├── 托盘、开机启动和单实例
└── 隔离高风险外部 UIA provider

Yita.Native.Mac                  macOS 原生适配器
├── AXUIElement 选区和选区边界
├── Cmd+C + NSPasteboard 回退
├── Carbon/CGEvent 全局快捷键与鼠标事件
├── NSStatusItem 菜单栏入口
├── Accessibility/Input Monitoring 权限检查
└── NSPanel 浮窗行为与屏幕坐标
```

业务层只依赖抽象接口，不能引用 WPF、WinForms、Windows registry、System.Drawing 或 macOS 框架。原生适配器通过结构化事件传递以下信息：

```text
SelectionRequest  { trigger, point, foregroundApp, cancellation }
SelectionResult   { text, bounds?, source, diagnostics }
PermissionState   { accessibility, inputMonitoring, clipboardFallback }
```

## 迁移原则

1. 先抽取可测试的业务核心，再替换界面；不同时重写取词、翻译和 UI。
2. Windows 先保持现有 UI Automation 和剪贴板实现，确保当前 WPS、浏览器、Markdown 和 VS Code 行为不回退。
3. macOS 直接使用 AXUIElement 读取选区，剪贴板只作为回退；不使用 OCR 作为普通文本路径。
4. 原生读取器必须有超时、取消、进程隔离或 watchdog。第三方 accessibility provider 的异常不能终止 UI 进程。
5. API Key 继续使用系统凭据存储；选中文字、译文和历史默认不写入诊断日志。
6. 浮窗位置始终保存为相对当前选区锚点的 DIP/逻辑坐标，不能保存上一次桌面绝对坐标。
7. 每个阶段都要能独立构建和回退；跨平台版本未达到验收标准前不替换 Windows 发布入口。

## 实施状态

- [x] 阶段 0：分支、Core/桌面/原生项目骨架、跨平台契约和 CI 矩阵。
- [x] 阶段 1（垂直切片）：DeepSeek 流式翻译、取消和错误分类、设置 JSON 存储、内存缓存、JSONL 历史、浮窗定位和 Core 测试。
- [x] 阶段 2（起始切片）：Avalonia 侧边设置页面、模型配置、当前会话 API Key、手动流式翻译和取消按钮。
- [x] 阶段 3（第一条 Windows 链路）：Avalonia 壳注册 `Ctrl+Shift+T`，通过独立 Windows 原生层执行受控 Ctrl+C、读取剪贴板并打开选区翻译浮窗。
- [x] 阶段 3（读取流水线）：平台无关流水线支持优先读取原生 Edit/RichEdit/Scintilla 控件，再执行安全剪贴板回退。
- [x] 阶段 3（UIA 隔离链路）：新增独立 `Yita.UIA.Worker.exe`、JSON-lines 超时协议和 Worker 崩溃后的读取回退。
- [x] 阶段 3（UIA 文档读取）：增加浏览器/PDF 可见文档树候选搜索、WPS PDF 窗口识别和 Edge 可访问性短脉冲激活。
- [x] 阶段 3（WPS 复制回退）：增加 WPS PDF 识别、受控 Ctrl+C 重试、目标窗口校验和剪贴板所有权校验。
- [ ] 阶段 3（WPS 完整诊断）：迁移 WPS 剪贴板健康事件和设置开关到 Core/桌面壳。
- [ ] 阶段 1 完整迁移：将现有 WPF Core 实现和 455 项相关测试逐步改为引用 `Yita.Core`，接入系统凭据存储。
- [ ] 阶段 2 完整迁移：翻译浮窗、拖拽、置顶、托盘和问答页面。
- [ ] 阶段 3 完整迁移：把现有 UI Automation 隔离读取器、WPS PDF 专用路径、鼠标划词钩子、托盘和系统凭据接入 Avalonia。
- [ ] 阶段 4：macOS AXUIElement、NSPasteboard、权限、菜单栏和签名 helper。

## 开发阶段

### 阶段 0：基线与契约（当前阶段）

- 创建本分支和迁移计划。
- 新建 `Yita.Core`、`Yita.Desktop`、`Yita.Native.Windows`、`Yita.Native.Mac` 项目骨架。
- 定义平台无关的选区、权限、翻译流和浮窗定位契约。
- 为核心算法建立跨平台测试入口。
- 保持原 `Yita.App` 和原安装包脚本可构建。

### 阶段 1：抽取 Yita.Core

- 将 DeepSeek SSE 流、错误分类、重试和取消迁入 Core。
- 将 `AppSettings`、凭据存储接口、翻译缓存、AI 历史接口迁入 Core。
- 将 `PopupPlacement`、文本规范化、最大长度和输出防护迁入 Core。
- 将现有测试迁移或改为引用 Core；Windows 专属测试留在 Windows 测试项目。
- 添加 JSON/SQLite 存储实现和内存实现，便于单元测试。

### 阶段 2：Avalonia 桌面壳

- 用 Avalonia 重建设置页、侧边导航、翻译浮窗、问答窗口和托盘入口。
- 统一 Yita 字体、暖白/青绿色主题、动画减弱选项和高 DPI 行为。
- 将浮窗拖拽、调整大小、固定和选区相对定位做成平台无关 ViewModel。
- 为 Windows 和 macOS 分别实现置顶、透明、非激活窗口的宿主层。

### 阶段 3：Windows 适配器接入

- 把当前 UI Automation、剪贴板事务、快捷键和鼠标钩子包进 `Yita.Native.Windows`。
- 保留现有 UIA helper 隔离策略。
- 通过适配器契约接入 Avalonia 壳，先实现与 v0.8.4 等价的 Windows 功能。
- 继续使用当前 Inno Setup，增加新桌面壳的安装和诊断文件。

### 阶段 4：macOS 适配器

- 实现 AXUIElement 的 `AXSelectedText`、选区范围和边界读取。
- 实现 NSPasteboard changeCount 检测、Cmd+C 回退和安全恢复。
- 实现全局快捷键、鼠标锚点和菜单栏图标。
- 增加 Accessibility 与 Input Monitoring 权限检测、跳转设置按钮和诊断结果。
- 对 Intel 和 Apple Silicon 分别构建并合并 Universal 应用包。

### 阶段 5：稳定性和发布

- 建立 Windows/macOS CI 矩阵，执行 Core 测试、桌面启动测试和安装包检查。
- Windows 使用签名 Setup；macOS 使用 Developer ID 签名、notarization 和 DMG/PKG。
- 维护浏览器、VS Code、WPS、Word、Adobe/Preview、Markdown 编辑器兼容性矩阵。
- 记录只包含方法名、耗时、失败类型和权限状态的隐私安全诊断信息。
- 只有两个平台的核心场景达到验收标准后，才将跨平台发布设为默认下载入口。

## 关键接口验收标准

### 选区读取

- 直接 Accessibility/UIA 读取优先于剪贴板。
- 剪贴板回退必须检测内容变化和所有权，避免覆盖用户新复制的内容。
- 单次读取有明确超时；超时后可取消，不阻塞 UI。
- UIA/AX provider 失败不能导致主进程退出。
- 选区为空、超长、密码字段和无权限时返回可区分的失败类型。

### 翻译与浮窗

- DeepSeek 流式首段到达后立即显示，后续增量不会覆盖用户拖动后的位置。
- 翻译请求可取消，旧选区响应不能写入新浮窗。
- 浮窗以当前选区左下角或系统提供的选区边界为锚点。
- 用户移动后的偏移相对于当前选区保存，并在下一次选区中复用。
- 内容变化会自适应尺寸，但不能遮挡正文或超出工作区。

### 平台体验

- Windows：浏览器、VS Code、WPS PDF、Word、Markdown 可用。
- macOS：Safari/Chrome、VS Code、Preview、Word、常见 Markdown 编辑器可用。
- 权限关闭时，应用不闪退，并明确显示需要开启的权限。
- 安装、卸载、升级不会删除用户设置、凭据和历史记录。

## 风险与取舍

- 任何应用都能读取选中文字无法绝对保证，系统保护和应用自绘内容必须显示为受限场景。
- Avalonia 可以复用 C# 业务代码，但透明置顶窗口和托盘仍需要少量原生代码。
- Rust + Tauri 可以作为后续低资源版本，但一次性重写会丢失当前测试覆盖和 Windows 兼容性经验。
- macOS 分发的主要风险是 Accessibility 权限、代码签名、公证和系统版本差异，而不是 DeepSeek API。

## 完成定义

跨平台迁移只有在以下条件全部满足时才视为完成：

- Core 和两个平台适配器均有自动化测试或可重复的集成诊断。
- Windows 现有 455 项测试保持通过，且主要兼容性场景无回退。
- macOS 两种 CPU 架构都能启动、授权、划词、翻译、拖拽和退出。
- 两个平台的安装包都能在干净系统上安装，不依赖用户预装 .NET。
- Release 页面同时提供 Windows Setup、macOS DMG/PKG、SHA256 和版本说明。
- 失败场景可解释、可诊断、不可导致主进程闪退。
