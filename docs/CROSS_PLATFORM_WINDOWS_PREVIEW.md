# Windows 跨平台迁移预览

> 历史阶段说明：本文记录早期 `codex/cross-platform-migration` 的行为，部分快捷键、记录和设置说明已经过时。当前 Avalonia 架构已提升为 main，请使用 [README](../README.md)、[架构说明](ARCHITECTURE.md) 与 [Windows 验收说明](WINDOWS_AVALONIA_ACCEPTANCE.md)。

本说明适用于 `codex/cross-platform-migration` 分支的 Avalonia 桌面壳。正式 Windows 下载仍使用 WPF 版本；当前预览没有单独发布 Setup，也没有 macOS 原生取词实现。

## 构建与启动

需要 Windows x64 和 .NET 8 SDK。在仓库根目录执行：

```powershell
dotnet restore Yita.CrossPlatform.sln
dotnet build Yita.CrossPlatform.sln -c Release --no-restore
dotnet run --project src/Yita.Desktop/Yita.Desktop.csproj -c Release --no-build
```

已构建后也可双击根目录的 `Start-Yita-cross-platform.cmd`。在当前开发机，该入口自动使用 `F:\DevTools\dotnet` 中的运行环境，避免直接点击 EXE 时找不到 .NET。其他电脑仍需安装 .NET 8；这不是自包含分发包。

运行前先通过托盘退出旧版 Yita，避免全局快捷键冲突。预览注册 `Ctrl+Shift+T`：保持外部应用文本选区后按此快捷键，会优先读取当前选区并根据设置回退到受控复制。这个入口与 WPF 版的“先复制后翻译剪贴板”行为不同。

在“模型配置”中填写 Endpoint、Model 和 API Key，测试连接并点击底部“保存设置”。手动翻译和测试连接使用正在编辑的字段；系统划词翻译使用保存后的配置。

自动划词功能需要“启用划词翻译”。某些 PDF 阅读器还需要打开“允许剪贴板回退取词”。“划词等待”可调整为 0–2000 毫秒，“最大选区字符数”可调整为 100–50000。

## 已有行为

- 自动拖选和全局快捷键通过 Windows 原生层取词，UI Automation 在独立 Worker 中执行。
- 原文与译文可切换，查看原文时翻译继续接收；正文可以选中复制。
- 固定窗口保留其内容，下一次选区使用另一窗口。关闭按钮取消该窗口请求。
- 拖动顶部空白区域移动窗口，拖动右下角调整尺寸；长文有滚动区域。
- 拖动后保存的是相对当前选区锚点的逻辑偏移，下一次在新选区和对应屏幕缩放下应用。
- 快速连续划词会取消并作废旧请求，即使服务延迟返回也不能覆盖当前内容。
- AI 记录默认关闭；开启并保存后，将完整的新译文写入本地 JSONL。缓存命中不会重复写入记录。
- 历史页显示最近 500 条记录，支持原文/译文搜索、完整详情与确认清空。

## 配置与数据

| 内容 | 预览位置或标识 |
| --- | --- |
| 设置与相对偏移 | `%LOCALAPPDATA%\Yita\desktop-settings.json` |
| 翻译记录 | `%LOCALAPPDATA%\Yita\desktop-history.jsonl` |
| Windows API Key | 凭据管理器 `Yita:DeepSeekApiKey` |
| Windows 启动项 | 当前用户 `Run` 项中的 `Yita.CrossPlatform` |

首次启动若没有预览设置，会读取 WPF `settings.json` 中名称兼容的字段；保存仅写入预览设置。WPF 的 `StartWithWindows`、单独浮窗位置文件和高级外观等未迁移字段不会自动导入。API Key 使用独立凭据标识，需要在预览中配置；不会覆盖 WPF 的 `Yita/DeepSeekApiKey`。

记录包含明文原文与译文，请按需启用。设置 JSON 不保存 API Key；翻译缓存只在进程内存中保存，并按请求参数和服务配置区分。

## 自动化检查

```powershell
dotnet test Yita.CrossPlatform.sln -c Release --no-restore
dotnet test Yita.sln -c Release --no-restore
```

2026-10-01 Windows 本地结果：Core 27、Windows 适配器 6、Avalonia 交互 9、WPF 回归 455，全部通过。桌面测试使用 Avalonia Headless 与模拟 HTTP 响应，不会访问真实 API 或修改用户配置、凭据和历史。

这些检查覆盖请求取消、缓存与协议错误、阅读切换、长文滚动、固定窗口、关闭/退出和历史查询。它们不证明真实软件已经兼容。

## 尚待验收

- 浏览器、VS Code、Markdown、Word 和 WPS PDF 的真实取词、速度及正常复制行为。
- 100%、150%、200% 缩放，以及不同 DPI 的多显示器之间拖动与相对位置恢复。
- 系统托盘、无激活浮窗、开机启动、普通用户权限和旧版本并存。
- 剪贴板富文本/图片等完整格式恢复、WPS 专用开关和可导出的运行诊断。
- 干净 Windows 上的自包含发布、Setup 安装与升级。

本轮桌面自动操作被用户中止，未完成上述真机 UI 验收。macOS 原生取词、权限、菜单栏及打包按当前安排暂缓。
