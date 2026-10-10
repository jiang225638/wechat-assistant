; ==============================================================================
; WeChat Copilot - Inno Setup 自动化安装包构建脚本
; ==============================================================================

#define MyAppName "微信副驾 (WeChat Copilot)"
#define MyAppShortName "WeChatCopilot"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "WeChat Copilot"
#define MyAppExeName "WeChatCopilot.exe"
#define MySourceDir "..\publish\win-x64"

[Setup]
; 应用程序基础标识（GUID 用于区分和升级覆盖现有安装）
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

; 高压缩算法（LZMA2 Ultra），大幅缩小包含 .NET 运行时的安装包体积
Compression=lzma2/ultra64
SolidCompression=yes

; 现代风格安装向导
WizardStyle=modern

; 权限模式：支持普通用户无管理员权限安装到用户目录，或管理员安装到系统目录
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; 卸载信息与图标配置
SetupIconFile=app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式:"; Flags: checkedonce

[Files]
; 主程序及所有依赖库（排除调试符号 .pdb）
Source: "{#MySourceDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent
