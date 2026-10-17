; ViaSonic 安装脚本（Inno Setup 6）
; 编译：ISCC.exe installer\ViaSonic.iss，输入为 app\（dotnet publish 的输出）

#define AppName "ViaSonic"
; 版本号来自 exe（在 ui\ViaSonic.csproj 里改），去掉第四段 .0
#define AppVersion RemoveFileExt(GetVersionNumbersString(AddBackslash(SourcePath) + "..\app\ViaSonic.exe"))
#define AppExe "ViaSonic.exe"

[Setup]
AppId={{6E2B6A47-3C1D-4F7E-9B1A-5D0C9E8F2A11}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
; 2.0 从 ViaTTS 改名：开始菜单用新名字（目录沿用上次的安装位置）
UsePreviousGroup=no
DisableProgramGroupPage=yes
; 默认装到当前用户目录，不弹管理员确认；安装时可选择为所有用户安装
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\dist
OutputBaseFilename={#AppName}_{#AppVersion}_setup
SetupIconFile=..\ui\Assets\ViaSonic.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
ShowLanguageDialog=no
CloseApplications=yes

[Languages]
; 按系统界面语言自动选择；都不匹配时用第一个（英文）
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "zh"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\gsv_server.py"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\rvc_server.py"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; 2.0 之前叫 ViaTTS：删掉旧程序文件和旧快捷方式
Type: files; Name: "{app}\ViaTTS.*"
Type: files; Name: "{app}\Assets\ViaTTS.*"
Type: filesandordirs; Name: "{autoprograms}\ViaTTS"
Type: files; Name: "{autoprograms}\ViaTTS.lnk"
Type: files; Name: "{autodesktop}\ViaTTS.lnk"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; 配置和常用语在 %LOCALAPPDATA%\ViaSonic（2.0 之前是 ViaTTS，首次启动时搬过去），卸载时保留，重装后还在
