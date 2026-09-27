# Windows Setup

Yita 0.8.4 使用 Inno Setup 6.7.3 打包自包含 .NET 8 win-x64 程序。支持 Windows 10 1809 及以上与 Windows 11 x64。安装程序仅为当前用户安装，默认目录为 `%LOCALAPPDATA%\Programs\Yita`，无需管理员权限或额外下载 .NET。

## 构建

在仓库根目录执行：

```powershell
.\scripts\Build-Setup.ps1 -Version 0.8.4
# 使用自定义工具路径：
.\scripts\Build-Setup.ps1 -DotnetRoot 'F:\DevTools\dotnet' -InnoCompiler 'F:\DevTools\InnoSetupPortable\tools\ISCC.exe'
```

不传 `-InnoCompiler` 时，脚本下载并解压固定的 `Tools.InnoSetup` 6.7.3 NuGet 包到 `.work/tools/`，不会执行工具安装程序。下载包 SHA256 为 `F780898E402FF80612CC8D9FCB8C6E02932BD1CB4C900FFDAA31F9341CFB49F4`，并校验 ISCC.exe 和 ISCmplr.dll 的有效 Authenticode 签名及 Pyrsys B.V. 发布者。工具来源为 NuGet 的 tools.innosetup/6.7.3；签名说明见 https://jrsoftware.org/isdl-verify.php 。

脚本依次运行测试、自包含发布、必需组件检查、许可文件复制、品牌插图生成、Setup 编译、安装包 SHA256 与逐文件清单生成。仅重新调整安装器资源时可传 `-SkipTests`；它不会跳过发布或内容检查。每次构建使用独立 `.work/setup-*` 目录，避免覆盖正在运行的开发版本。

产物位于 `artifacts/release/`：

- `Yita-Setup-<版本>-win-x64.exe`：完整安装程序。
- 同名 `.exe.sha256`：安装程序校验值。
- `Yita-<版本>-payload.json`：安装内容逐文件 SHA256，用于验证。

当前产物未签名。正式分发时可使用发布者自己的代码签名证书签名 EXE；签名后必须重新生成 `.sha256`。构建不自动创建 GitHub Release 或上传文件。

## 验证

在未安装 Yita 的测试账号或干净虚拟机执行：

```powershell
.\scripts\Test-Setup.ps1 -Version 0.8.4
Get-FileHash .\artifacts\release\Yita-Setup-0.8.4-win-x64.exe -Algorithm SHA256
```

验证脚本在独立临时路径安装，禁用快捷方式和安装后启动，逐项核对安装内容、卸载注册、重复安装、卸载和用户文件保留。发现当前账号已有安装注册时直接停止。日志保留在 `.work/install-test-*`。该检查不会启动应用或调用翻译服务，不能替代无 .NET 环境中的首次启动和真实 API 验证。

## 安装与维护约定

- AppId 固定为 `{2F7C12F3-CF37-4B0B-B76C-73B7443709EE}`，升级时不要更改。
- 安装前应退出正在运行的 Yita；安装器通过 Restart Manager 提示处理目标文件占用，不强制终止其他目录中的开发版本。
- 禁止向已有更新版本的目标目录降级覆盖。
- 卸载只删除安装器管理的文件；保留 `%LOCALAPPDATA%\Yita`、Windows 凭据中的 API Key 和用户记录。仅当开机启动项指向本安装路径时移除该项。
- 不打包开发者设置、API Key、日志或记录。MIT、字体、运行时与安装器许可随包保留。
- 中文语言文件来自 Inno Setup 官方 `is-6_7_3` 源码中的 `Files/Languages/Unofficial/ChineseSimplified.isl`，保留译者署名。
