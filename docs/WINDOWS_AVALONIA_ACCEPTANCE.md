# Windows Avalonia 原版还原验收

## 范围与启动

本次在 `codex/windows-avalonia-parity` 分支恢复原版 Yita 的 Windows 产品界面与业务机制。界面由 Avalonia 渲染，设置布局以原版 XAML 转换为基准；平台无关的原版翻译、提示词、重试、缓存、AI 记录和总结代码通过编译链接复用。没有将旧 WPF 应用作为新界面启动。

在仓库根目录双击 `Start-Yita-cross-platform.cmd`。它启动 `src/Yita.Desktop/bin/Release/net8.0/Yita.Desktop.exe`；这是源码预览入口，不是已经安装的正式版。

修改代码后重新构建，再从托盘退出旧预览并重新启动：

```powershell
$env:DOTNET_ROOT = 'F:\DevTools\dotnet'
$env:DOTNET_CLI_HOME = 'F:\DevTools\dotnet-user'
$env:NUGET_PACKAGES = 'F:\DevTools\nuget\packages'
& 'F:\DevTools\dotnet\dotnet.exe' build Yita.CrossPlatform.sln -c Release
```

正式版和预览都会使用 `Ctrl+Shift+T`，验收时只运行一个。预览启动后的第二次正常启动会打开已经运行的设置窗口；带 `--background` 的启动只进入后台。

## 原版保护

以下源码与正式入口没有修改：`src/Yita.App`、`tests/Yita.Tests`、`Yita.sln`、正式打包脚本、版本文件和正式标签。以下 Git 引用维持原值：

| 引用 | SHA |
| --- | --- |
| 本地 main | a6a5c32086d0c24e1e322f57a46e75b89ea26833 |
| origin/main | 4ad91b758e55f4ca5abe86d96dd62a1d157708c1 |
| v0.8.4 | 5082d8571d3eed9db0cc0c6666f73db5eaa8673d |

预览读入原版偏好时不会启用原版的开机启动或沿用其 AI 记录目录。预览设置、凭据、加密修正和诊断独立保存；实际翻译使用已经保存的设置，设置页未保存的模型输入只参与显式连接测试。

| 预览数据 | 位置 |
| --- | --- |
| 偏好设置 | `%LOCALAPPDATA%\Yita\desktop-settings.json` |
| API Key | Windows 凭据管理器，`Yita:DeepSeekApiKey` |
| 加密翻译修正 | `%LOCALAPPDATA%\Yita\desktop-translation-memory.dat` |
| 运行健康日志 | `%LOCALAPPDATA%\Yita\desktop-runtime-health.log` |
| AI 记录与总结 | 用户在预览中选择的目录 |

## 自动化结果

2026-10-02，本机 Windows x64、.NET 8、Release 构建：

| 测试组 | 通过数 |
| --- | ---: |
| 跨平台 Core 合约与可靠性 | 27 |
| 链接的原版业务回归及迁移运行时 | 258 |
| Windows 适配器、ABI 和实例唤醒 | 8 |
| Avalonia 设置、浮窗与交互 | 23 |
| 跨平台解决方案合计 | 316 |
| 未修改的原版 WPF 测试 | 455 |

关键覆盖包括缓存身份、请求合并、超时、取消、重试、解释与问答、术语和修正记忆、连接测试不命中翻译缓存、未保存设置隔离、固定多窗口、旧响应不能覆盖新窗口、关闭后重开、混合文字字体、长文滚动、手动记录去重、解释追问上下文和选择文本时布局不跳动。

`tools/Yita.WindowsSmoke` 使用独立原生编辑器实测并通过：UI Automation 取词与选区坐标、Ctrl+C 回退、文本和 HTML 剪贴板恢复、复制发送后取消仍能恢复剪贴板、原生托盘、鼠标钩子、全局快捷键和钩子修复。工具保存并恢复剪贴板，不发起 API 请求，不写产品设置。

真实 Avalonia 应用已经启动并检查四个设置入口。`--background` 启动后没有显示设置窗口，第二次正常启动唤醒原进程，未产生第二个持有钩子的实例。

## 截图与复现

本地截图保存在 `artifacts/ui-parity`，该目录不提交。包含四个设置页、阅读浮窗、相同问答内容的 WPF/Avalonia 基准，以及 Yita/非 Yita、玻璃/普通、长文和 150%/200% 渲染样本。

```powershell
$env:YITA_PARITY_CAPTURE_DIRECTORY = 'F:\Project\InstantTranslate\artifacts\ui-parity'
& 'F:\DevTools\dotnet\dotnet.exe' test tests\Yita.Desktop.Tests\Yita.Desktop.Tests.csproj -c Release
& 'F:\DevTools\dotnet\dotnet.exe' run --project tools\Yita.ReferenceCapture\Yita.ReferenceCapture.csproj -c Release -- artifacts/ui-parity
& 'F:\DevTools\dotnet\dotnet.exe' run --project tools\Yita.WindowsSmoke\Yita.WindowsSmoke.csproj -c Release
```

参考截图工具实例化原版窗口但不运行其启动流程，避免启动旧版钩子、写设置或调用服务。缩放渲染样本验证图像和布局；它们不等同于真实多显示器的 DPI 切换验收。

运行 Windows 原生 smoke 前，从托盘退出预览和正式版，释放全局快捷键；否则快捷键占用会使该项检查失败。

## 手动验收

### 设置与托盘

1. 对照正式版检查四页：常规、AI 记录、翻译与外观、模型配置。检查微软雅黑、Logo、色调、边距、控件、滚动和按钮状态。
2. 切换中英文，确认导航和下拉框选中项同步变更；英文导航不截断。修改设置后取消，确认原偏好恢复；保存后关闭再打开，确认保存成功。
3. 关闭设置窗口后应用仍在托盘运行；双击托盘或重复运行启动脚本可以重新打开。检查右键菜单的翻译、暂停、设置、修复、诊断、关于和退出。
4. 在预览中启用开机启动，重新登录后检查后台就绪。关闭此开关，再检查预览启动项被移除。该操作只控制 `Yita.CrossPlatform` 启动项。

### Windows 划词与阅读

5. 分别在普通网页、VS Code Markdown、WPS 的可复制 PDF 中拖选英文短句和长段落；检查译文显示在本次选区附近。也检查双击单词和连续快速划词。
6. WPS 分别启用和关闭兼容取词。对其他取词失败的程序启用兼容剪贴板回退，检查选区能复制时是否可翻译。扫描件和禁止复制的内容不在本轮能力范围内。
7. 先复制文本再按 `Ctrl+Shift+T`，检查翻译的是剪贴板内容。暂停自动划词后，检查鼠标划词不触发，手动快捷键仍可使用。
8. 普通 Ctrl+C、Ctrl+V 保持正常；在 Word/WPS 中复制带格式内容后触发兼容取词，检查原剪贴板格式尽量保留。读取过程中再次主动复制不能被旧恢复结果覆盖。
9. 原文/译文切换、正文选择和复制正常；短文自动适应窗口，长文可以完整滚动。拖动把手移动，之后在另一处划词，位置沿用相对选区的偏移。
10. 调整浮窗大小，固定一个译文后继续划词，新窗口出现且旧窗口不变。外部点击和 Esc 只关闭未固定窗口；关闭窗口可取消请求，下次划词可重新打开。

### AI 与记录

11. 选择译文中的一部分，通过附近的解释按钮解释；检查右键解释、代码分析、返回、复制和失败重试。
12. 原文或译文追问使用翻译上下文；完成解释后的追问使用解释上下文。DeepSeek 快速聊天独立回答。多轮问答、流式显示、停止、重试、复制和 Enter/Shift+Enter 均正常。
13. 检查问答的六个尺寸预设、字号滑块、Ctrl 加减与 Ctrl+鼠标滚轮、拖动、缩放和固定；选择多段内容不会让排版跳动。
14. 修改并保存译文，重新翻译同一内容时显示修正；重启仍保存。设置中的修正数量和清除按钮有效，清除后不再复用旧修正。
15. 选择独立 AI 记录目录，检查自动解释/问答记录、关闭自动记录后的手动保存、一天一份 Markdown、重复点击不重复写入、新问答后可再次记录，以及今天/最近七天/全部的总结。

### 错误与视觉状态

16. 填写错误 Key、错误模型或断网，确认出现可读错误且程序继续运行。快速划词和关闭窗口后不得出现上一请求的文字覆盖。
17. 真实 DeepSeek 流式翻译、解释、问答与总结，需要用户配置的服务完成验收；本机自动化使用可控响应，不表示已经验证每个线上模型。
18. 对照原版检查全部颜色和浮窗样式，尤其两种玻璃样式、字号上下限、长词、长段落、加载和错误状态。
19. 在实际 100%/150%/200% 显示缩放与不同 DPI 的两个屏幕间移动窗口，检查选区定位、拖动偏移、工作区约束和文字裁切。
20. 关闭 Windows 动画或启用高对比度，检查读取和操作可用；恢复系统偏好后重启预览检查动效。

## 剩余差异与边界

- 基准截图已经接近原版，但 Avalonia/Skia 和 WPF 的文字栅格化、字形基线、部分字段内边距、默认菜单及滚动条仍可能不同。最终逐状态的像素一致性尚待验收，不能标记为完全相同。
- WPS、浏览器、VS Code 的实际鼠标手势还需要手动验收。生产钩子忽略注入的鼠标事件，软件模拟拖选不能代替真实鼠标的最终检查。
- 高对比度和减少动画偏好已接入，但本轮没有修改用户系统偏好进行实测；混合 DPI 的真实多屏切换同样待验收。
- UI Automation、剪贴板回退依赖目标程序暴露选区或允许复制，以及 Windows 权限边界；本轮没有增加 OCR。
- macOS 原生 Accessibility、Cmd+C、权限与菜单栏实现及真机/runner 验收继续暂缓。这份交付不是已经验收完成的 macOS 应用。
- 本轮没有发布正式 Setup、修改 main、重写正式版本或创建新的 GitHub Release。
