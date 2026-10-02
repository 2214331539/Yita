# Mac 原生 helper 协议

当前协议版本为 `1`。实现位于 `src/Yita.Native.Mac/Helper` 与 `src/Yita.Native.Mac.Helper`，尚在 `codex/platform-host-services` 功能分支。当前交付权限检查、通信和 AX 选区读取代码；Cmd+C、全局输入、Desktop 自动触发与菜单栏原生 helper 仍待实现。真实 Mac 桌面验收继续暂缓。

## 进程与身份

- helper 是 Swift/AppKit agent，Bundle ID 固定为 `com.yita.desktop.native-helper`。
- macOS 构建 Desktop 时使用 Xcode Command Line Tools 编译，生成 `Yita.Native.Mac.Helper.app` 并进行 ad-hoc 开发签名。这不是 Developer ID 签名、公证或可公开分发的完整 Mac 安装包。
- 默认从 Desktop 输出目录下的 `.app/Contents/MacOS/Yita.Native.Mac.Helper` 启动；开发时可用 `YITA_MAC_HELPER_PATH` 指向该可执行文件。
- stdin/stdout 仅传递 UTF-8 JSON Lines，每个消息以 LF 结束。stderr 被按固定块读取并丢弃，不记录消息正文或原生错误内容。
- helper 接收 `--parent-pid`，校验实际父进程并定时检查；stdin EOF、父进程退出或客户端释放均结束 helper。
- Cocoa 操作在 helper 主循环执行，stdin 解析在独立线程排队；业务 UI 不直接调用可能阻塞的原生 API。

## 握手

helper 启动后立即输出：

```json
{"version":1,"id":"ready","status":"ready","bundleIdentifier":"com.yita.desktop.native-helper","processId":12345}
```

客户端检查协议版本、约定的 Bundle ID、握手状态和实际启动的 PID。失败即终止 helper。握手是协议一致性检查，不替代安装包及二进制的签名真实性检查。

## 请求与响应

每个请求由客户端生成独立的 32 位十六进制 ID，同一 helper 上串行交换消息：

```json
{"version":1,"id":"d2e39e2187ed4b8aa3580c563a13697ab","command":"permissions","selection":null}
```

权限响应示例：

```json
{
  "version":1,
  "id":"d2e39e2187ed4b8aa3580c563a13697ab",
  "status":"ok",
  "permissions":{"accessibility":false,"inputMonitoring":false},
  "capabilities":{"selection":true,"clipboardFallback":false,"globalInput":false}
}
```

权限与能力分别表示“系统已授权”和“helper 已实现”。`selection:true` 表示 helper 有 AX 读取代码，不表示 Desktop 已接入自动划词、获得权限或经过真实应用验收。获得 Accessibility 权限不会使尚未实现的全局输入变为可用。

| 命令 | 当前行为 |
| --- | --- |
| `permissions` | 检查 AX 信任状态和 Input Monitoring 状态，不请求权限 |
| `requestAccessibility` | 用户点击时调用 `AXIsProcessTrustedWithOptions` 提示授权；返回时用户可能尚未完成授权，需要再次检查 |
| `openAccessibilitySettings` | 用户点击时打开系统辅助功能隐私设置 |
| `readSelection` | 读取当前前台应用的 AX 选区与可选范围边界，返回结构化 `selection`；不发送 Cmd+C |

`readSelection` 的 `selection` 请求使用共享 `SelectionRequest`，包含 `trigger`、本次 `pointer`、可选 `gestureBounds`、`includeContext`、可选 `foregroundApplication`（Mac Bundle ID）和可选 `foregroundProcessId`。有来源身份时必须匹配前台目标；来源在读取期间改变则取消结果，不能回传旧文本。PID/Bundle ID 只作运行时校验，不写入诊断。

取词成功和可预期失败均用 `status:ok` 携带共享选区结果。例如成功：

```json
{"version":1,"id":"d2e39e2187ed4b8aa3580c563a13697ab","status":"ok","selection":{"text":"hello","source":"accessibility","failure":"none","bounds":{"x":-800,"y":120,"width":90,"height":18}}}
```

权限拒绝、空选区、不支持、保护内容、目标变化、AX 超时等通过 `selection.failure` 和固定诊断代码区分。失败结果不携带文本、上下文或边界；客户端拒绝缺失 `source/failure` 或夹带内容的失败帧。文本使用 UTF-16 20,000 单位上限，上下文仅在明确请求时以范围接口读取附近最多 2,000 单位。坐标为 Quartz 全局点（左上角原点），无可靠边界时省略 `bounds`，后续宿主需完成 Avalonia/DPI 转换。

失败响应仍携带请求 ID：

```json
{"version":1,"id":"d2e39e2187ed4b8aa3580c563a13697ab","status":"error","diagnosticCode":"selection-not-implemented"}
```

客户端只接受 `ok/error`，拒绝错误版本、迟到/不同 ID、缺失必要字段或非法枚举。诊断代码通过固定白名单，未知错误不会把 helper 提供的任意文本带入界面或诊断。

## 时限与隔离

- 一次请求默认总时限 3 秒，包含排队、启动和握手。排队中的请求取消不会终止正在执行的其他请求。
- 请求最多 64,000 字节，响应最多 256,000 字节；读取过程中执行长度限制，不先使用无界 `ReadLine` 分配内存。
- 请求取消、超时、断连和协议异常会终止正在使用的 helper，下一请求可以创建新进程；不复用可能包含迟到数据的管道。
- 最多在 30 秒内启动 3 次，之后返回 `RestartBackoff`；窗口到期后可以重试，避免持续崩溃造成进程循环。
- 权限未授权、helper 缺失、协议不匹配、超时、不可用和待重启分别映射到共享 `PlatformPermissionStatus`。
- 预留的取词结果仍遵循 Core 的 `SelectionResult`；拒绝无效坐标、过长文本和错误来源，上下文仅在明确开启时保留。

## 构建与非交互验证

在 Mac 上构建跨平台解决方案会同时生成 helper，也可单独构建：

```bash
bash scripts/Build-Mac-Helper.sh src/Yita.Desktop/bin/Release/net8.0
```

开发 helper 的最低构建目标暂为 macOS 12.0、当前主机 CPU 架构；这不代表整个 App 的最低系统版本或另一种架构已通过验收。最终包身份、签名、最低版本和更新后 TCC 行为仍需分发阶段确定。

Windows/macOS 均运行可控 .NET 子进程测试协议故障。Mac CI 另将客户端连接到实际编译的 Swift helper，并使用 `--self-test`：

```bash
dotnet run --project tools/Yita.MacHelperSmoke -c Release --no-build -- \
  --helper src/Yita.Desktop/bin/Release/net8.0/Yita.Native.Mac.Helper.app/Contents/MacOS/Yita.Native.Mac.Helper
```

self-test 不初始化 NSApplication、不检查真实桌面权限、不打开系统设置、不请求授权、不访问外部选区/剪贴板/Keychain/API。它证明 Swift 二进制与 C# 协议可以通信，不能证明真实桌面权限、选区读取、签名稳定性或 Mac 产品可用。

新增 `--selection-self-test` 运行真实读取策略的可控 AX fixtures；另使用 `--self-test --selection-fixture range` 将固定合成选区经真实 Swift/C# 管道交换，覆盖范围回退、坐标、上下文选择和来源变化。fixture 参数仅在 `--self-test` 时启用，生产取词仍调用系统 AX API。两种模式均不读取真实桌面数据或申请授权。

2026-10-02，提交 `f85a8d8` 的 [GitHub Actions #36973376654](https://github.com/2214331539/Yita/actions/runs/36973376654) 已通过 Windows/macOS 构建、各 398 项测试、可控子进程通信和 Mac 实际 Swift helper 的 5 项 self-test。Swift 管道读取使用 `Darwin.read`，每个短请求在 stdin 仍保持打开时即可返回，不等待缓冲区填满或 EOF。

同日，AX 读取代码提交 `502d864` 的 [GitHub Actions #36974694338](https://github.com/2214331539/Yita/actions/runs/36974694338) 通过 Windows/macOS 构建、各 409 项测试，以及 Mac 实际 Swift helper 的 10 项管道检查、17 项 AX 策略 fixtures。真实 AX 权限、目标应用和屏幕坐标仍待真机验收。
