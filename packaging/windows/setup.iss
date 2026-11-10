; Script generated for Inno Setup 6
; VSN AI Rate Tracker Windows Installer

#define MyAppName "VSN AI Rate Tracker"
#define MyAppVersion "0.0.1"
#define MyAppPublisher "VSN"
#define MyAppURL "https://github.com/luis-vsn/VSNAiRateTracker"
#define MyAppExeName "VSNAiRateTracker.exe"
#ifndef PublishDir
  #define PublishDir "publish"
#endif

[Setup]
AppId={{E6F18B99-4A9B-4D88-97F2-72B97E4A2E77}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\VSN\VSNAiRateTracker
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=..\dist
OutputBaseFilename=VSNAiRateTracker-Setup-{#MyAppVersion}-win-x64
SetupIconFile=..\..\VSNAiRateTracker\Assets\icon.ico
; Wizard header image generated from the application icon (VSNAiRateTracker\Assets\icon.png), one file per DPI scale
WizardSmallImageFile=assets\wizard-small-55.bmp,assets\wizard-small-83.bmp,assets\wizard-small-110.bmp,assets\wizard-small-138.bmp
WizardImageAlphaFormat=defined
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Iniciar con Windows (Auto-arranque en segundo plano)"; GroupDescription: "Opciones del sistema:"

[Files]
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "{#MyAppExeName}"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
