#define MyAppName "写真资源管理器"
#define MyAppVersion "3.3.4"
#define MyAppPublisher "Private Local Tools"
#define MyAppExeName "XiurenManager.exe"

[Setup]
AppId={{79028188-CB1B-46CF-AA53-57AAE895C10E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} 私人版 {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion=3.3.4.0
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=写真下载、整理、浏览与收藏工具私人版安装程序
DefaultDirName=E:\Apps\写真资源管理器
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=写真资源管理器-私人版-Setup-3.3.4
SetupIconFile=..\src\XiurenDownloader\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern dynamic
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousGroup=yes
ShowLanguageDialog=no

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

[Files]
Source: "..\publish-v3\*"; DestDir: "{app}"; Excludes: "libvlc\win-x86\*,libvlc\win-arm64\*,tools\ffmpeg\*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\tools\ffmpeg\bin\ffmpeg.exe"; DestDir: "{app}\tools\ffmpeg\bin"; Flags: ignoreversion
Source: "..\tools\ffmpeg\bin\ffprobe.exe"; DestDir: "{app}\tools\ffmpeg\bin"; Flags: ignoreversion
Source: "..\tools\ffmpeg\LICENSE"; DestDir: "{app}\tools\ffmpeg"; Flags: ignoreversion
Source: "..\tools\ffmpeg\README.txt"; DestDir: "{app}\tools\ffmpeg"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
Root: HKCU; Subkey: "Software\XiurenManager"; ValueType: string; ValueName: "DataRoot"; ValueData: "F:\秀人\_Tool"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent
