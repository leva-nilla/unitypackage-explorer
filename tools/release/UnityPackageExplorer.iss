#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceRoot
  #error "Pass /DSourceRoot=<repository root> to ISCC."
#endif
#ifndef PublishRoot
  #error "Pass /DPublishRoot=<self-contained publish output> to ISCC."
#endif

#define ProductName "UnityPackage Explorer"
#define ProductExe "UnityPackageExplorer.App.exe"

[Setup]
AppId={{7FA14A9B-8822-4C87-AFC0-4E3459D90A80}
AppName={#ProductName}
AppVersion={#AppVersion}
AppVerName={#ProductName} {#AppVersion}
AppPublisher=Levanilla
DefaultDirName={localappdata}\Programs\UnityPackageExplorer
DefaultGroupName={#ProductName}
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0.22000
OutputBaseFilename=UnityPackageExplorer-Setup-{#AppVersion}-x64
SetupIconFile={#SourceRoot}\src\UnityPackageExplorer.App\Assets\UnityPackageExplorer.ico
UninstallDisplayIcon={app}\{#ProductExe}
LicenseFile={#SourceRoot}\LICENSE
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
Uninstallable=yes
ChangesAssociations=yes
OutputDir={#SourceRoot}\.artifacts\release
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName={#ProductName}
VersionInfoDescription={#ProductName} per-user setup

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "contextmenu"; Description: "従来の右クリックメニューに追加"; GroupDescription: "エクスプローラー連携:"; Flags: checkedonce
Name: "startmenu"; Description: "スタートメニューに追加"; GroupDescription: "エクスプローラー連携:"; Flags: checkedonce
Name: "sendto"; Description: "「送る」に追加"; GroupDescription: "エクスプローラー連携:"; Flags: checkedonce
Name: "explorerpreview"; Description: "エクスプローラーのプレビュー欄にZIP / UnityPackageを表示"; GroupDescription: "エクスプローラー連携:"; Flags: checkedonce

[Files]
Source: "{#PublishRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"
Source: "{#SourceRoot}\tools\release\Migrate-LegacyInstallFiles.ps1"; Flags: dontcopy
Source: "{#SourceRoot}\tools\release\Cleanup-LegacyExplorerRegistration.ps1"; Flags: dontcopy
Source: "{#SourceRoot}\tools\release\LegacyInstallSafety.psm1"; Flags: dontcopy
Source: "{#SourceRoot}\tools\release\Register-ExplorerPreview.ps1"; Flags: dontcopy
Source: "{#SourceRoot}\tools\release\Unregister-ExplorerPreview.ps1"; Flags: dontcopy
Source: "{#SourceRoot}\tools\release\PreviewMachineRegistration.ps1"; Flags: dontcopy
Source: "{#SourceRoot}\tools\release\PreviewRegistration.Common.psm1"; Flags: dontcopy
Source: "{#PublishRoot}\Tools\CurrentInstallFiles.txt"; Flags: dontcopy

[Icons]
Name: "{group}\{#ProductName}"; Filename: "{app}\{#ProductExe}"; WorkingDir: "{app}"; Tasks: startmenu
Name: "{userappdata}\Microsoft\Windows\SendTo\{#ProductName}"; Filename: "{app}\{#ProductExe}"; WorkingDir: "{app}"; Tasks: sendto

[Registry]
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.unitypackage\shell\UnityPackageExplorer.Open"; ValueType: string; ValueName: ""; ValueData: "UnityPackage Explorer で開く"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.unitypackage\shell\UnityPackageExplorer.Open"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#ProductExe},0"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.unitypackage\shell\UnityPackageExplorer.Open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#ProductExe}"" ""%1"""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.prefab\shell\UnityPackageExplorer.Open"; ValueType: string; ValueName: ""; ValueData: "UnityPackage Explorer で開く"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.prefab\shell\UnityPackageExplorer.Open"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#ProductExe},0"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.prefab\shell\UnityPackageExplorer.Open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#ProductExe}"" ""%1"""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\UnityPackageExplorer.Open"; ValueType: string; ValueName: ""; ValueData: "UnityPackage Explorer で開く"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\UnityPackageExplorer.Open"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#ProductExe},0"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\UnityPackageExplorer.Open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#ProductExe}"" ""%1"""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.rar\shell\UnityPackageExplorer.Open"; ValueType: string; ValueName: ""; ValueData: "UnityPackage Explorer で開く"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.rar\shell\UnityPackageExplorer.Open"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#ProductExe},0"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.rar\shell\UnityPackageExplorer.Open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#ProductExe}"" ""%1"""; Tasks: contextmenu
[Run]
Filename: "{app}\{#ProductExe}"; Description: "{cm:LaunchProgram,{#StringChange(ProductName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\.unitypackage-explorer-inno-install.json"

[Code]
procedure CurPageChanged(CurPageID: Integer);
var
  i: Integer;
begin
  if CurPageID = wpSelectTasks then
    for i := 0 to WizardForm.TasksList.Items.Count - 1 do
      WizardForm.TasksList.Checked[i] := True;
end;
