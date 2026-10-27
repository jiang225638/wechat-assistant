; ==============================================================================
; WeChat Copilot - Inno Setup 自动化安装包构建脚本
; 现代互联网极简风格界面（默认展示自定义安装路径与选项）
; ==============================================================================

#define MyAppName "微信副驾 (WeChat Copilot)"
#define MyAppShortName "WeChatCopilot"
#ifndef MyAppVersion
#define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "WeChat Copilot"
#define MyAppExeName "WeChatCopilot.exe"
#define MySourceDir "..\publish\win-x64"

[Setup]
; 应用程序基础标识
AppId={{D29E7F41-8A1C-46C5-9F1B-4E90B0E551A2}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppShortName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes

; 输出安装包目录与文件名
OutputDir=..\dist
OutputBaseFilename=WeChatCopilot_Setup_v{#MyAppVersion}

; 高压缩算法（LZMA2 Ultra），大幅缩小安装包体积
Compression=lzma2/ultra64
SolidCompression=yes

; 启用现代向导引擎
WizardStyle=modern

; 权限模式：优先安装到当前用户空间（无需UAC弹窗阻塞），支持用户自定义目录
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline

; 卸载与外观配置
SetupIconFile=app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
CloseApplications=yes
RestartApplications=no
DisableWelcomePage=yes
DisableReadyPage=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Files]
; 安装向导资源图片
Source: "logo_rounded_72.bmp"; Flags: dontcopy

; 主程序及所有依赖库（排除调试符号 .pdb）
Source: "{#MySourceDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Check: ShouldCreateDesktopIcon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
var
  LogoImageSelect, LogoImageInst, LogoImageFinish: TBitmapImage;
  AppTitleLabel, AppSubTitleLabel: TLabel;
  InstTitleLabel, InstSubTitleLabel: TLabel;
  FinishTitleLabel, FinishSubTitleLabel: TLabel;
  PathHeaderLabel: TLabel;
  DesktopIconCheckBox: TCheckBox;
  CardPanel: TPanel;
  BottomBar: TPanel;

function ShouldCreateDesktopIcon(): Boolean;
begin
  Result := DesktopIconCheckBox.Checked;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  // 跳过传统多步冗余向导，直接进入安装配置页
  Result := (PageID = wpWelcome) or 
            (PageID = wpSelectProgramGroup) or 
            (PageID = wpSelectTasks) or 
            (PageID = wpReady);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpSelectDir then
  begin
    WizardForm.NextButton.Caption := '立即安装';
    WizardForm.NextButton.Visible := True;
    WizardForm.CancelButton.Visible := True;
    WizardForm.CancelButton.Enabled := True;
  end
  else if CurPageID = wpInstalling then
  begin
    WizardForm.NextButton.Visible := False;
    WizardForm.CancelButton.Enabled := False;
  end
  else if CurPageID = wpFinished then
  begin
    WizardForm.NextButton.Caption := '立即体验';
    WizardForm.NextButton.Visible := True;
    WizardForm.CancelButton.Visible := False;
  end;
end;

procedure InitializeWizard();
var
  W, H, BtnBarH: Integer;
begin
  W := WizardForm.ClientWidth;
  H := WizardForm.ClientHeight;
  BtnBarH := ScaleY(56);

  WizardForm.Caption := '{#MyAppName} 安装程序';
  WizardForm.Position := poScreenCenter;
  WizardForm.Color := clWhite;

  // 隐藏传统向导顶栏和分割线
  WizardForm.MainPanel.Visible := False;
  WizardForm.Bevel.Visible := False;
  WizardForm.BeveledLabel.Visible := False;
  WizardForm.BackButton.Visible := False;

  // 底部按钮栏底板
  BottomBar := TPanel.Create(WizardForm);
  BottomBar.Parent := WizardForm;
  BottomBar.Left := 0;
  BottomBar.Top := H - BtnBarH;
  BottomBar.Width := W;
  BottomBar.Height := BtnBarH;
  BottomBar.BevelOuter := bvNone;
  BottomBar.Color := $F8F9FA;

  // 调整向导容器尺寸
  WizardForm.OuterNotebook.Width := W;
  WizardForm.OuterNotebook.Height := H - BtnBarH;
  WizardForm.InnerNotebook.Left := 0;
  WizardForm.InnerNotebook.Top := 0;
  WizardForm.InnerNotebook.Width := W;
  WizardForm.InnerNotebook.Height := H - BtnBarH;
  WizardForm.InnerPage.Color := clWhite;

  // 按钮样式与定位
  WizardForm.NextButton.Parent := BottomBar;
  WizardForm.NextButton.Top := (BtnBarH - ScaleY(32)) / 2;
  WizardForm.NextButton.Left := W - ScaleX(205);
  WizardForm.NextButton.Width := ScaleX(105);
  WizardForm.NextButton.Height := ScaleY(32);
  WizardForm.NextButton.Font.Name := 'Microsoft YaHei UI';
  WizardForm.NextButton.Font.Size := 9;
  WizardForm.NextButton.Font.Style := [fsBold];
  WizardForm.NextButton.BringToFront;

  WizardForm.CancelButton.Parent := BottomBar;
  WizardForm.CancelButton.Top := (BtnBarH - ScaleY(32)) / 2;
  WizardForm.CancelButton.Left := W - ScaleX(90);
  WizardForm.CancelButton.Width := ScaleX(75);
  WizardForm.CancelButton.Height := ScaleY(32);
  WizardForm.CancelButton.Font.Name := 'Microsoft YaHei UI';
  WizardForm.CancelButton.Font.Size := 9;
  WizardForm.CancelButton.BringToFront;

  // 释放品牌 Logo
  ExtractTemporaryFile('logo_rounded_72.bmp');

  // ============================================================================
  // 1. 自定义安装页 (wpSelectDir) 布局
  // ============================================================================
  WizardForm.SelectDirPage.Color := clWhite;
  WizardForm.SelectDirPage.Left := 0;
  WizardForm.SelectDirPage.Top := 0;
  WizardForm.SelectDirPage.Width := W;
  WizardForm.SelectDirPage.Height := H - BtnBarH;

  WizardForm.SelectDirLabel.Visible := False;
  WizardForm.SelectDirBrowseLabel.Visible := False;
  WizardForm.SelectDirBitmapImage.Visible := False;

  LogoImageSelect := TBitmapImage.Create(WizardForm.SelectDirPage);
  LogoImageSelect.Parent := WizardForm.SelectDirPage;
  LogoImageSelect.Bitmap.LoadFromFile(ExpandConstant('{tmp}\logo_rounded_72.bmp'));
  LogoImageSelect.Left := ScaleX(32);
  LogoImageSelect.Top := ScaleY(24);
  LogoImageSelect.Width := ScaleX(64);
  LogoImageSelect.Height := ScaleY(64);
  LogoImageSelect.Stretch := True;

  AppTitleLabel := TLabel.Create(WizardForm.SelectDirPage);
  AppTitleLabel.Parent := WizardForm.SelectDirPage;
  AppTitleLabel.Caption := '微信副驾 WeChat Copilot';
  AppTitleLabel.Font.Name := 'Microsoft YaHei UI';
  AppTitleLabel.Font.Size := 15;
  AppTitleLabel.Font.Style := [fsBold];
  AppTitleLabel.Left := ScaleX(112);
  AppTitleLabel.Top := ScaleY(28);

  AppSubTitleLabel := TLabel.Create(WizardForm.SelectDirPage);
  AppSubTitleLabel.Parent := WizardForm.SelectDirPage;
  AppSubTitleLabel.Caption := '让微信沟通更智能、更高效  |  版本 {#MyAppVersion}';
  AppSubTitleLabel.Font.Name := 'Microsoft YaHei UI';
  AppSubTitleLabel.Font.Size := 9;
  AppSubTitleLabel.Font.Color := $666666;
  AppSubTitleLabel.Left := ScaleX(112);
  AppSubTitleLabel.Top := ScaleY(62);

  // 自定义安装路径选项卡片
  CardPanel := TPanel.Create(WizardForm.SelectDirPage);
  CardPanel.Parent := WizardForm.SelectDirPage;
  CardPanel.Left := ScaleX(32);
  CardPanel.Top := ScaleY(105);
  CardPanel.Width := W - ScaleX(64);
  CardPanel.Height := ScaleY(165);
  CardPanel.BevelOuter := bvNone;
  CardPanel.Color := $F7F8FA;

  PathHeaderLabel := TLabel.Create(CardPanel);
  PathHeaderLabel.Parent := CardPanel;
  PathHeaderLabel.Caption := '安装路径';
  PathHeaderLabel.Font.Name := 'Microsoft YaHei UI';
  PathHeaderLabel.Font.Size := 9;
  PathHeaderLabel.Font.Style := [fsBold];
  PathHeaderLabel.Left := ScaleX(16);
  PathHeaderLabel.Top := ScaleY(14);

  WizardForm.DirEdit.Parent := CardPanel;
  WizardForm.DirEdit.Left := ScaleX(16);
  WizardForm.DirEdit.Top := ScaleY(38);
  WizardForm.DirEdit.Width := CardPanel.Width - ScaleX(112);
  WizardForm.DirEdit.Height := ScaleY(28);
  WizardForm.DirEdit.Font.Name := 'Microsoft YaHei UI';
  WizardForm.DirEdit.Font.Size := 9;

  WizardForm.DirBrowseButton.Parent := CardPanel;
  WizardForm.DirBrowseButton.Caption := '更改...';
  WizardForm.DirBrowseButton.Left := CardPanel.Width - ScaleX(88);
  WizardForm.DirBrowseButton.Top := ScaleY(37);
  WizardForm.DirBrowseButton.Width := ScaleX(72);
  WizardForm.DirBrowseButton.Height := ScaleY(28);
  WizardForm.DirBrowseButton.Font.Name := 'Microsoft YaHei UI';
  WizardForm.DirBrowseButton.Font.Size := 9;

  WizardForm.DiskSpaceLabel.Parent := CardPanel;
  WizardForm.DiskSpaceLabel.Left := ScaleX(16);
  WizardForm.DiskSpaceLabel.Top := ScaleY(76);
  WizardForm.DiskSpaceLabel.Font.Name := 'Microsoft YaHei UI';
  WizardForm.DiskSpaceLabel.Font.Size := 8;
  WizardForm.DiskSpaceLabel.Font.Color := $777777;

  DesktopIconCheckBox := TCheckBox.Create(CardPanel);
  DesktopIconCheckBox.Parent := CardPanel;
  DesktopIconCheckBox.Caption := '创建桌面快捷方式';
  DesktopIconCheckBox.Font.Name := 'Microsoft YaHei UI';
  DesktopIconCheckBox.Font.Size := 9;
  DesktopIconCheckBox.Left := ScaleX(16);
  DesktopIconCheckBox.Top := ScaleY(115);
  DesktopIconCheckBox.Width := ScaleX(200);
  DesktopIconCheckBox.Checked := True;

  // ============================================================================
  // 2. 安装中页面 (wpInstalling) 布局
  // ============================================================================
  WizardForm.InstallingPage.Color := clWhite;
  WizardForm.InstallingPage.Left := 0;
  WizardForm.InstallingPage.Top := 0;
  WizardForm.InstallingPage.Width := W;
  WizardForm.InstallingPage.Height := H - BtnBarH;

  LogoImageInst := TBitmapImage.Create(WizardForm.InstallingPage);
  LogoImageInst.Parent := WizardForm.InstallingPage;
  LogoImageInst.Bitmap.LoadFromFile(ExpandConstant('{tmp}\logo_rounded_72.bmp'));
  LogoImageInst.Left := ScaleX(32);
  LogoImageInst.Top := ScaleY(24);
  LogoImageInst.Width := ScaleX(64);
  LogoImageInst.Height := ScaleY(64);
  LogoImageInst.Stretch := True;

  InstTitleLabel := TLabel.Create(WizardForm.InstallingPage);
  InstTitleLabel.Parent := WizardForm.InstallingPage;
  InstTitleLabel.Caption := '正在安装 微信副驾...';
  InstTitleLabel.Font.Name := 'Microsoft YaHei UI';
  InstTitleLabel.Font.Size := 15;
  InstTitleLabel.Font.Style := [fsBold];
  InstTitleLabel.Left := ScaleX(112);
  InstTitleLabel.Top := ScaleY(28);

  InstSubTitleLabel := TLabel.Create(WizardForm.InstallingPage);
  InstSubTitleLabel.Parent := WizardForm.InstallingPage;
  InstSubTitleLabel.Caption := '正在解压并配置应用组件，请稍候...';
  InstSubTitleLabel.Font.Name := 'Microsoft YaHei UI';
  InstSubTitleLabel.Font.Size := 9;
  InstSubTitleLabel.Font.Color := $666666;
  InstSubTitleLabel.Left := ScaleX(112);
  InstSubTitleLabel.Top := ScaleY(62);

  WizardForm.ProgressGauge.Left := ScaleX(32);
  WizardForm.ProgressGauge.Top := ScaleY(130);
  WizardForm.ProgressGauge.Width := W - ScaleX(64);
  WizardForm.ProgressGauge.Height := ScaleY(20);

  WizardForm.StatusLabel.Left := ScaleX(32);
  WizardForm.StatusLabel.Top := ScaleY(165);
  WizardForm.StatusLabel.Font.Name := 'Microsoft YaHei UI';
  WizardForm.StatusLabel.Font.Size := 9;

  WizardForm.FilenameLabel.Left := ScaleX(32);
  WizardForm.FilenameLabel.Top := ScaleY(188);
  WizardForm.FilenameLabel.Font.Name := 'Microsoft YaHei UI';
  WizardForm.FilenameLabel.Font.Size := 8;
  WizardForm.FilenameLabel.Font.Color := $777777;

  // ============================================================================
  // 3. 安装完成页面 (wpFinished) 布局
  // ============================================================================
  WizardForm.FinishedPage.Color := clWhite;
  WizardForm.WizardBitmapImage2.Visible := False;
  WizardForm.FinishedHeadingLabel.Visible := False;
  WizardForm.FinishedLabel.Visible := False;

  LogoImageFinish := TBitmapImage.Create(WizardForm.FinishedPage);
  LogoImageFinish.Parent := WizardForm.FinishedPage;
  LogoImageFinish.Bitmap.LoadFromFile(ExpandConstant('{tmp}\logo_rounded_72.bmp'));
  LogoImageFinish.Left := ScaleX(32);
  LogoImageFinish.Top := ScaleY(24);
  LogoImageFinish.Width := ScaleX(64);
  LogoImageFinish.Height := ScaleY(64);
  LogoImageFinish.Stretch := True;

  FinishTitleLabel := TLabel.Create(WizardForm.FinishedPage);
  FinishTitleLabel.Parent := WizardForm.FinishedPage;
  FinishTitleLabel.Caption := '安装完成！';
  FinishTitleLabel.Font.Name := 'Microsoft YaHei UI';
  FinishTitleLabel.Font.Size := 15;
  FinishTitleLabel.Font.Style := [fsBold];
  FinishTitleLabel.Left := ScaleX(112);
  FinishTitleLabel.Top := ScaleY(28);

  FinishSubTitleLabel := TLabel.Create(WizardForm.FinishedPage);
  FinishSubTitleLabel.Parent := WizardForm.FinishedPage;
  FinishSubTitleLabel.Caption := '微信副驾 已成功部署在您的电脑上。';
  FinishSubTitleLabel.Font.Name := 'Microsoft YaHei UI';
  FinishSubTitleLabel.Font.Size := 9;
  FinishSubTitleLabel.Font.Color := $666666;
  FinishSubTitleLabel.Left := ScaleX(112);
  FinishSubTitleLabel.Top := ScaleY(62);

  WizardForm.RunList.Left := ScaleX(112);
  WizardForm.RunList.Top := ScaleY(115);
  WizardForm.RunList.Width := W - ScaleX(140);
  WizardForm.RunList.Font.Name := 'Microsoft YaHei UI';
  WizardForm.RunList.Font.Size := 9;
end;
