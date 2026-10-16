; Inno Setup script - WinBoost by Corpse
#define AppName "WinBoost"
#define AppVersion "1.0.0"
#define AppExe "WinBoost.exe"
; Publish folder (build.ps1 output). Override: ISCC /DPublishDir="C:\path\to\app" WinBoost.iss
#ifndef PublishDir
  #define PublishDir GetEnv("USERPROFILE") + "\Desktop\WinBoost"
#endif
#ifndef OutDir
  #define OutDir "setup"
#endif

[Setup]
AppId={{3F7A2C1E-9B4D-4E8F-A6C5-B0D1E2F3A4B5}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}.0
VersionInfoProductVersion={#AppVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
AppPublisher=Corpse
AppCopyright=(c) Corpse
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0.19041
OutputDir={#OutDir}
OutputBaseFilename={#AppName}Setup
SetupIconFile=WinBoost\Assets\WinBoost.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=yes
; Close a running WinBoost before install/uninstall (mutex created in Program.Main + Restart Manager)
AppMutex=Global\WinBoost_AppMutex,WinBoost_AppMutex
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[CustomMessages]
english.RemoveData=Also delete WinBoost settings, logs and tweak backups (%LocalAppData%\WinBoost)?%n%nChoose No to keep them (recommended if you may reinstall: backups let you revert tweaks).
french.RemoveData=Supprimer aussi les paramètres, journaux et sauvegardes de WinBoost (%LocalAppData%\WinBoost) ?%n%nChoisissez Non pour les conserver (recommandé si vous réinstallez : les sauvegardes permettent d'annuler les optimisations).

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
; WinUI 3 self-contained runtime: installed hidden so the folder shows only WinBoost.exe
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,\{#AppExe}"; Attribs: hidden; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent shellexec

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""{#AppName} Auto-Reapply"" /F"; Flags: runhidden; RunOnceId: "DelTask1"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""{#AppName} Auto-Reapply (Daily)"" /F"; Flags: runhidden; RunOnceId: "DelTask2"

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and DirExists(ExpandConstant('{localappdata}\WinBoost')) then
  begin
    if UninstallSilent then exit; { silent uninstall keeps user data }
    if SuppressibleMsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
      DelTree(ExpandConstant('{localappdata}\WinBoost'), True, True, True);
  end;
end;
