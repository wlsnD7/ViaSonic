; ViaTTS 安装脚本（Inno Setup 6）
; 编译：ISCC.exe installer\ViaTTS.iss，输入为 app\（dotnet publish 的输出）

#define AppName "ViaTTS"
; 版本号来自 exe（在 ui\ViaTTS.csproj 里改），去掉第四段 .0
#define AppVersion RemoveFileExt(GetVersionNumbersString(AddBackslash(SourcePath) + "..\app\ViaTTS.exe"))
#define AppExe "ViaTTS.exe"

[Setup]
AppId={{6E2B6A47-3C1D-4F7E-9B1A-5D0C9E8F2A11}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; 默认装到当前用户目录，不弹管理员确认；安装时可选择为所有用户安装
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\dist
OutputBaseFilename={#AppName}_{#AppVersion}_setup
SetupIconFile=..\ui\Assets\ViaTTS.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "zh"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\gsv_server.py"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; 配置和常用语在 %LOCALAPPDATA%\ViaTTS，卸载时保留，重装后还在
