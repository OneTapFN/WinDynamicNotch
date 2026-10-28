; Установщик WinDynamicNotch (Inno Setup 6).
; Собирается скриптом installer\build-installer.ps1 — он сначала делает
; self-contained publish в ..\publish, потом вызывает ISCC для этого файла.

#define AppName "WinDynamicNotch"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppExe "WinDynamicNotch.exe"
#define AppPublisher "WinDynamicNotch"
#define AppId "{{7C4A1F2E-9B31-4D6E-8F52-2B0C7A5E3D18}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppComments=Медиавиджет в стиле Dynamic Island для Windows
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Установщик {#AppName}
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=..\dist
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=..\Assets\app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; По умолчанию — установка только для текущего пользователя, без запроса UAC.
; Через диалог можно выбрать установку для всех пользователей (тогда нужен админ).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
RestartApplications=no
AllowNoIcons=yes
DisableWelcomePage=no
ShowLanguageDialog=auto

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.AutoStart=Запускать при входе в Windows
english.AutoStart=Start with Windows
russian.DesktopIcon=Создать ярлык на рабочем столе
english.DesktopIcon=Create a desktop shortcut

[Tasks]
Name: "autostart"; Description: "{cm:AutoStart}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Автозапуск — тот же ключ, которым управляет само приложение из трея.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "WinDynamicNotch"; ValueData: """{app}\{#AppExe}"""; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Лог ошибок и прочие данные приложения.
Type: filesandordirs; Name: "{localappdata}\WinDynamicNotch"

[Code]
// Приложение живёт в трее, поэтому перед установкой/удалением его нужно закрыть:
// иначе exe занят и Setup не сможет заменить файл.
procedure StopRunningApp();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe} /F', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(400);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    StopRunningApp();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopRunningApp();
end;
