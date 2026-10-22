#define AppName "getSoundBoard"
#ifndef AppVersion
  #define AppVersion "0.1.5"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

[Setup]
AppId={{BD57060E-99C1-43F2-88CC-8A2BB1EFAB7B}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=getSoundBoard contributors
DefaultDirName={localappdata}\Programs\getSoundBoard
DefaultGroupName={#AppName}
OutputDir=..\artifacts\installer
OutputBaseFilename=getSoundBoard-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#AppName}

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\getSoundBoard.exe"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\getSoundBoard.exe"; Tasks: desktopicon

[Tasks]
Name: desktopicon; Description: "Criar atalho na área de trabalho"; GroupDescription: "Atalhos:"; Flags: unchecked

[Run]
Filename: "{app}\getSoundBoard.exe"; Description: "Abrir {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupCommand: String;
begin
  if (CurUninstallStep = usUninstall) and
     RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'getSoundBoard', StartupCommand) and
     (CompareText(StartupCommand, '"' + ExpandConstant('{app}\getSoundBoard.exe') + '" --startup') = 0) then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'getSoundBoard');
end;
