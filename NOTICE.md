# 上游与第三方声明

Yita 基于 Frank Lai 的开源项目 InstantTranslate v0.7.3 修改，原项目地址：
https://github.com/franklai-rise/InstantTranslate

Yita 更改包括产品命名、应用身份标识、译獭品牌图标、阅读切换、字体、界面动效与湖畔配色。Yita 自有修改采用根目录 LICENSE 中的 MIT 许可；上游原始版权和 MIT 授权完整保留在 LICENSES/InstantTranslate-MIT.txt 中，并随所有构建分发。

Source Sans Pro 字体的版权及 SIL Open Font License 1.1 见 src/Yita.App/Assets/Fonts/LICENSE-SourceSans.md。旧 WPF 包对应 Assets/Fonts/LICENSE-SourceSans.md；Avalonia Windows 包在 Licenses/LICENSE-SourceSans.md，Mac 包在 Contents/Resources/Licenses/LICENSE-SourceSans.md。

CHANGELOG.md 保留了继承自上游的版本功能历史；这些历史条目不代表 Yita 曾独立发布对应版本。

Windows Setup 使用 Inno Setup 6.7.3 构建，版权归 Jordan Russell / Martijn Laan 等贡献者所有，https://jrsoftware.org/ 。旧安装目录中的 LICENSE-InnoSetup.txt 或 Avalonia 包 Licenses/LICENSE-InnoSetup.txt 包含其许可；源码内的简体中文翻译文件来自对应版本的官方源码仓库，保留原有署名。

自包含安装包附带 Microsoft .NET 运行时和 Windows Desktop Runtime，对应的 LICENSE-Microsoft.*.txt 与 THIRD-PARTY-NOTICES-Microsoft.*.txt 随程序一并提供。

Avalonia Windows 包的完整依赖及运行时许可位于 Licenses/Packages，独立 Windows UIA helper 的声明位于 Licenses/WindowsUIA。Mac 包仅携带 macOS .NET 运行时，声明位于 Contents/Resources/Licenses/Packages。dependency-inventory.json 记录所收集的依赖与许可文件；其中保守保留了恢复阶段的构建和其他平台依赖声明，不表示全部对应二进制随当前平台包分发。
