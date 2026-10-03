# Yita Windows Avalonia 预览版

此版本与同一 Release 中的 M 系列 Mac 包使用相同最新源码、Core 与 Avalonia 界面。Windows 主界面是 Avalonia，不是旧 WPF 产品；独立 UI Automation helper 因 Windows 系统接口需要仍携带 Windows Desktop Runtime。

支持 Windows 10 1809 及以上 / Windows 11 x64。公开 Release 提供自包含 Setup，无需安装 .NET、SDK 或开发工具。备用 ZIP 仅保留在 Actions 构建产物中。需要联网及自己的 DeepSeek API Key。

## 安装

1. 退出旧 Yita 和源码预览，避免多个版本同时捕获鼠标或争用 `Ctrl+Shift+T`。
2. 运行 `Yita-Setup-*-preview.*-win-x64.exe`，按照 Yita 安装向导操作。默认仅为当前用户安装到 `%LOCALAPPDATA%\Programs\Yita Desktop`，无需管理员权限。
3. 从开始菜单/桌面的 `Yita Preview` 打开。预览版使用独立安装标识及快捷方式，保留旧 WPF 版，不自动卸载旧版。
4. 设置页配置 API Key、服务地址和模型，测试连接并保存。自动划词开关及 WPS/复制回退按需要设置。

尚未使用商业代码签名证书，Windows 可能提示未知发布者。请核对本仓库下载来源及 SHA256。备用 ZIP 必须完整解压，在 `Yita` 目录运行 `Yita.Desktop.exe`；不要只复制单个 EXE 或移动/删除 `Native/WindowsUIA`。

## 验收

- 普通网页、VS Code/Markdown、WPS 及其他可复制 PDF：拖选后显示附近浮窗，并增量显示译文。
- 普通 `Ctrl+C` / `Ctrl+V` 不受影响；自动取词失败时，先手动复制，再按 `Ctrl+Shift+T` 或使用托盘“翻译剪贴板”。
- 原文/译文、固定、关闭、拖动偏移和窗口缩放正常；缩放边缘的鼠标标识生效。
- 设置、托盘菜单和浮窗的语言同步，外观与字号可保存；解释、问答及记录按当前设置运行。
- 关闭设置后托盘常驻；再次启动唤醒已有实例；退出释放全局输入与 helper。开机启动应指向安装目录。
- 在未安装 .NET 的朋友电脑复验安装、实际取词与翻译；CI 包启动检查不能替代真实软件兼容性验收。

反馈请附包文件名、Windows 版本、目标软件及版本、复现步骤，以及托盘“复制性能诊断”的文本。不要发送 API Key 或私人文档正文。

## 更新与数据

当前没有应用内自动更新。退出程序后运行新版本 Setup，或完整替换 ZIP 目录。不要在运行中单独替换 DLL。卸载保留个人设置、系统凭据及用户记录。

旧 WPF 与新版本并存时，请只运行一个取词程序。新版本的设置 schema/旧偏好导入遵循已有迁移保护；安装器本身不修改用户设置或凭据，不强制迁移旧数据。

许可证在安装目录的 `Licenses` 中；主程序与 Windows UIA helper 的完整运行时及依赖声明分别附带。构建元数据记录版本、源码提交、架构及签名状态。本预览使用 .NET 8，正式长期发布前仍需升级 LTS、签名及更多安装/兼容性验收。
