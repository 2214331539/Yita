#ifndef AppVersion
  #define AppVersion "0.8.3"
#endif
#ifndef PayloadDir
  #error PayloadDir must point to the self-contained win-x64 publish output.
#endif
#ifndef ArtworkDir
  #error ArtworkDir must point to the generated installer artwork.
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts\release"
#endif
#define ProjectRoot "..\.."

[Setup]
AppId={{2F7C12F3-CF37-4B0B-B76C-73B7443709EE}
AppName=Yita
AppVersion={#AppVersion}
AppVerName=Yita {#AppVersion}
AppPublisher=Yita
AppPublisherURL=https://github.com/2214331539/Yita
AppSupportURL=https://github.com/2214331539/Yita/issues
AppUpdatesURL=https://github.com/2214331539/Yita/releases
DefaultDirName={localappdata}\Programs\Yita
DefaultGroupName=Yita
DisableProgramGroupPage=yes
DisableWelcomePage=no
DisableDirPage=no
DisableReadyPage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.17763
UninstallDisplayName=Yita
UninstallDisplayIcon={app}\Yita.exe
SetupIconFile={#ProjectRoot}\src\Yita.App\Assets\AppLogo.ico
WizardStyle=modern light zircon hidebevels
WizardSizePercent=115,115
WizardResizable=yes
WizardBackColor=#FFFCF7
WizardImageBackColor=#EFE9DF
WizardImageFile={#ArtworkDir}\wizard.png
WizardSmallImageFile={#ProjectRoot}\assets\branding\yita\v1\yita-icon-128.png
WizardSmallImageBackColor=#FFFCF7
OutputDir={#OutputDir}
OutputBaseFilename=Yita-Setup-{#AppVersion}-win-x64
Compression=lzma2/normal
SolidCompression=yes
LZMAUseSeparateProcess=yes
CloseApplications=yes
CloseApplicationsFilter=Yita.exe,*.dll
RestartApplications=no
AllowCancelDuringInstall=yes
SetupLogging=yes
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName=Yita
VersionInfoDescription=Yita Windows Setup
VersionInfoCompany=Yita

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
chinesesimplified.WelcomeLabel1=欢迎使用译獭 · Yita
chinesesimplified.WelcomeLabel2=少一点打扰，多一点理解。%n%nYita 让译文出现在阅读发生的地方。拖选一段文字，即可在附近浮窗中查看原文与译文。%n%n安装包已包含运行所需组件，无需单独安装 .NET。安装完成后，请在设置中填写自己的 API Key。%n%n点击“下一步”开始安装。
chinesesimplified.FinishedHeadingLabel=Yita 已准备就绪
chinesesimplified.FinishedLabel=打开 Yita，连接你的翻译服务，然后开始阅读。%n%n你可以在系统托盘中打开设置、暂停划词或退出。%n%n卸载会保留个人设置、API Key 和阅读记录。
english.WelcomeLabel1=Welcome to Yita
english.WelcomeLabel2=A little less distraction. A little more understanding.%n%nSelect a passage and read its translation beside your work.%n%nThis offline installer includes the required runtime. After installation, add your own API key in Settings.%n%nChoose Next to get started.
english.FinishedHeadingLabel=Yita is ready
english.FinishedLabel=Open Yita, connect your translation service, and start reading.%n%nUse the system tray to open Settings, pause translation, or quit.%n%nUninstalling keeps your settings, API key, and saved records.

[CustomMessages]
chinesesimplified.DesktopShortcut=在桌面创建 Yita 快捷方式
chinesesimplified.LaunchYita=立即打开 Yita
chinesesimplified.InstallDetails=仅为当前用户安装 · 已包含运行组件
chinesesimplified.OlderVersion=已安装更新版本的 Yita。请使用相同或更新版本的安装包，避免覆盖更新的程序文件。
english.DesktopShortcut=Create a Yita desktop shortcut
english.LaunchYita=Open Yita now
english.InstallDetails=For your account · Runtime included
english.OlderVersion=A newer version of Yita is already installed. Use the same or a newer installer to avoid overwriting newer application files.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; Flags: checkedonce

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Yita"; Filename: "{app}\Yita.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Yita"; Filename: "{app}\Yita.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Yita.exe"; Description: "{cm:LaunchYita}"; Flags: nowait postinstall skipifsilent unchecked

[Code]
const
  BrandInk = $00292D30;
  BrandMuted = $00586169;
  BrandTeal = $006B7524;

procedure InitializeWizard;
var
  Footer: TNewStaticText;
begin
  if not HighContrastActive then begin
    WizardForm.Font.Color := BrandInk;
    WizardForm.WelcomeLabel1.Font.Color := BrandTeal;
    WizardForm.FinishedHeadingLabel.Font.Color := BrandTeal;
    WizardForm.PageNameLabel.Font.Color := BrandTeal;
    WizardForm.WelcomeLabel1.Font.Size := 22;
    WizardForm.FinishedHeadingLabel.Font.Size := 22;
    WizardForm.WelcomeLabel2.Font.Size := 11;
    WizardForm.FinishedLabel.Font.Size := 11;
    WizardForm.WelcomeLabel1.Height := ScaleY(76);
    WizardForm.WelcomeLabel2.Top := WizardForm.WelcomeLabel1.Top + WizardForm.WelcomeLabel1.Height + ScaleY(12);
    WizardForm.WelcomeLabel2.Height := WizardForm.WelcomePage.ClientHeight - WizardForm.WelcomeLabel2.Top - ScaleY(16);
    WizardForm.FinishedHeadingLabel.Height := ScaleY(76);
    WizardForm.FinishedLabel.Top := WizardForm.FinishedHeadingLabel.Top + WizardForm.FinishedHeadingLabel.Height + ScaleY(12);
    WizardForm.FinishedLabel.Height := ScaleY(128);
    WizardForm.RunList.Top := WizardForm.FinishedLabel.Top + WizardForm.FinishedLabel.Height + ScaleY(12);
    WizardForm.RunList.Height := ScaleY(36);
  end;
  Footer := TNewStaticText.Create(WizardForm);
  Footer.Parent := WizardForm;
  Footer.Left := ScaleX(16);
  Footer.Top := WizardForm.NextButton.Top + ScaleY(8);
  Footer.Width := WizardForm.BackButton.Left - ScaleX(28);
  Footer.Height := ScaleY(20);
  Footer.Anchors := [akLeft, akBottom];
  Footer.Caption := CustomMessage('InstallDetails');
  if not HighContrastActive then Footer.Font.Color := BrandMuted;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ExistingVersion: String;
  InstalledVersion, SetupVersion: Int64;
begin
  Result := True;
  if CurPageID = wpReady then begin
    if GetVersionNumbersString(ExpandConstant('{app}\Yita.exe'), ExistingVersion) then begin
      if StrToVersion(ExistingVersion, InstalledVersion) and StrToVersion('{#AppVersion}.0', SetupVersion) then begin
        if ComparePackedVersion(InstalledVersion, SetupVersion) > 0 then begin
          SuppressibleMsgBox(CustomMessage('OlderVersion'), mbError, MB_OK, IDOK);
          Result := False;
        end;
      end;
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupCommand: String;
begin
  if CurUninstallStep = usUninstall then begin
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Yita', StartupCommand) then begin
      if CompareText(StartupCommand, '"' + ExpandConstant('{app}\Yita.exe') + '"') = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Yita');
    end;
  end;
end;
