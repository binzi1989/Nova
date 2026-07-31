#ifndef MyAppName
  #define MyAppName "NOVA Desktop"
#endif
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef MyAppPublisher
  #define MyAppPublisher "NOVA"
#endif
#ifndef MyAppExeName
  #define MyAppExeName "NovaDesktop.exe"
#endif
#ifndef PublishDir
  #define PublishDir "..\dist\NOVA-1.0.0-win-x64"
#endif
#ifndef SetupOutputName
  #define SetupOutputName "NOVA-Setup-" + MyAppVersion + "-win-x64"
#endif

[Setup]
AppId={{A90369CC-F1BE-4C75-92D0-2B14DF950D70}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\NOVA
DefaultGroupName=NOVA
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename={#SetupOutputName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\NOVA Desktop"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\NOVA Desktop"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 NOVA Desktop"; Flags: nowait postinstall skipifsilent
