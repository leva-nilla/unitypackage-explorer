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
DefaultDirName={localappdata}\Programs\UnityPackageExplorer\Release
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

[Files]
Source: "{#PublishRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"
Source: "{#SourceRoot}\tools\release\Migrate-LegacyInstallFiles.ps1"; Flags: dontcopy
Source: "{#SourceRoot}\tools\release\Cleanup-LegacyExplorerRegistration.ps1"; Flags: dontcopy
Source: "{#SourceRoot}\tools\release\LegacyInstallSafety.psm1"; Flags: dontcopy
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

[Run]
Filename: "{app}\{#ProductExe}"; Description: "{cm:LaunchProgram,{#StringChange(ProductName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\.unitypackage-explorer-inno-install.json"

[Code]
function HasOwnedInstallMarker(const InstallDirectory, MarkerName: String): Boolean;
var
  MarkerLines: TArrayOfString;
  EscapedDirectory: String;
  ProductFound, DirectoryFound: Boolean;
  Index: Integer;
begin
  Result := False;
  if not LoadStringsFromFile(InstallDirectory + '\' + MarkerName, MarkerLines) then Exit;
  EscapedDirectory := InstallDirectory;
  StringChangeEx(EscapedDirectory, '\', '\\', True);
  ProductFound := False;
  DirectoryFound := False;
  for Index := 0 to GetArrayLength(MarkerLines) - 1 do begin
    ProductFound := ProductFound or (Pos('"Product": "UnityPackageExplorer"', MarkerLines[Index]) > 0) or
      (Pos('"Product":"UnityPackageExplorer"', MarkerLines[Index]) > 0);
    DirectoryFound := DirectoryFound or (Pos('"InstallDirectory": "' + EscapedDirectory + '"', MarkerLines[Index]) > 0) or
      (Pos('"InstallDirectory":"' + EscapedDirectory + '"', MarkerLines[Index]) > 0);
  end;
  Result := ProductFound and DirectoryFound;
end;

function DirectoryIsEmpty(const DirectoryName: String): Boolean;
var
  FindRec: TFindRec;
  Found: Boolean;
begin
  Result := True;
  if not DirExists(DirectoryName) then Exit;
  Found := FindFirst(AddBackslash(DirectoryName) + '*', FindRec);
  try
    while Found do begin
      if (FindRec.Name <> '.') and (FindRec.Name <> '..') then begin
        Result := False;
        Exit;
      end;
      Found := FindNext(FindRec);
    end;
  finally
    FindClose(FindRec);
  end;
end;

function ShortcutTargetsApplication(const ShortcutFile, ApplicationPath: String): Boolean;
var
  Shell, Shortcut: Variant;
  TargetPath: String;
begin
  Result := not FileExists(ShortcutFile);
  if not FileExists(ShortcutFile) then Exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Shortcut := Shell.CreateShortcut(ShortcutFile);
    TargetPath := ExpandFileName(Shortcut.TargetPath);
    Result := CompareText(TargetPath, ExpandFileName(ApplicationPath)) = 0;
  except
    Result := False;
  end;
end;

function ReadTextFile(const FileName: String): String;
var
  Lines: TArrayOfString;
  Index: Integer;
begin
  Result := '';
  if not LoadStringsFromFile(FileName, Lines) then Exit;
  for Index := 0 to GetArrayLength(Lines) - 1 do begin
    if Result <> '' then Result := Result + #13#10;
    Result := Result + Lines[Index];
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  InstallDirectory, ApplicationPath, ExpectedCommand, RegisteredCommand: String;
  PowerShellPath, MigrationScript, CurrentFilesManifest, MigrationParameters: String;
  CleanupScript, SparseErrorLog, CleanupParameters, CleanupErrorText: String;
  ShellPaths: TArrayOfString;
  ShellPath: String;
  Index, MigrationResultCode, CleanupResultCode: Integer;
begin
  Result := '';
  InstallDirectory := ExpandConstant('{app}');
  ApplicationPath := InstallDirectory + '\UnityPackageExplorer.App.exe';

  if not DirectoryIsEmpty(InstallDirectory) and
     not HasOwnedInstallMarker(InstallDirectory, '.unitypackage-explorer-install.json') and
     not HasOwnedInstallMarker(InstallDirectory, '.unitypackage-explorer-inno-install.json') then begin
    Result := 'インストール先に別のファイルがあります。内容を保護するため、既存のUnityPackage Explorerインストールと確認できないフォルダーへの上書きを中止しました。';
    Exit;
  end;

  ExtractTemporaryFile('Migrate-LegacyInstallFiles.ps1');
  ExtractTemporaryFile('Cleanup-LegacyExplorerRegistration.ps1');
  ExtractTemporaryFile('LegacyInstallSafety.psm1');
  ExtractTemporaryFile('CurrentInstallFiles.txt');
  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  MigrationScript := ExpandConstant('{tmp}\Migrate-LegacyInstallFiles.ps1');
  CurrentFilesManifest := ExpandConstant('{tmp}\CurrentInstallFiles.txt');
  MigrationParameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' + #34 + MigrationScript + #34 +
    ' -InstallDirectory ' + #34 + InstallDirectory + #34 + ' -CurrentFilesManifest ' + #34 + CurrentFilesManifest + #34 + ' -Mode ValidateOnly';
  if not Exec(PowerShellPath, MigrationParameters, '', SW_HIDE, ewWaitUntilTerminated, MigrationResultCode) then begin
    Result := '旧版インストールの所有ファイルを検証できませんでした。既存ファイルを保護するため、セットアップを中止しました。';
    Exit;
  end;
  if MigrationResultCode <> 0 then begin
    if HasOwnedInstallMarker(InstallDirectory, '.unitypackage-explorer-inno-install.json') and
       not FileExists(InstallDirectory + '\Tools\CurrentInstallFiles.txt') then
      Result := '旧版Innoセットアップの所有ファイル一覧（Tools\CurrentInstallFiles.txt）が見つかりません。安全に確認できない既存ファイルを保護するため、更新を中止しました。Windowsの「設定 > アプリ > インストールされているアプリ」からUnityPackage Explorerをアンインストールし、完了後にこのセットアップを再実行してください。'
    else
      Result := '現在のセットアップが上書きするファイルに旧版の所有記録がありません。未知のファイルを保護するため、セットアップを中止しました。';
    Exit;
  end;

  if WizardIsTaskSelected('startmenu') and not ShortcutTargetsApplication(ExpandConstant('{userprograms}\UnityPackage Explorer\UnityPackage Explorer.lnk'), ApplicationPath) then begin
    Result := 'スタートメニューに同名の別アプリのショートカットがあります。上書きせず、名前を変更してから再実行してください。';
    Exit;
  end;
  if WizardIsTaskSelected('sendto') and not ShortcutTargetsApplication(ExpandConstant('{userappdata}\Microsoft\Windows\SendTo\UnityPackage Explorer.lnk'), ApplicationPath) then begin
    Result := '「送る」に同名の別アプリのショートカットがあります。上書きせず、名前を変更してから再実行してください。';
    Exit;
  end;

  ExpectedCommand := '"' + ApplicationPath + '" "%1"';
  SetArrayLength(ShellPaths, 3);
  ShellPaths[0] := 'Software\Classes\SystemFileAssociations\.unitypackage\shell\UnityPackageExplorer.Open\command';
  ShellPaths[1] := 'Software\Classes\SystemFileAssociations\.prefab\shell\UnityPackageExplorer.Open\command';
  ShellPaths[2] := 'Software\Classes\SystemFileAssociations\.zip\shell\UnityPackageExplorer.Open\command';
  for Index := 0 to GetArrayLength(ShellPaths) - 1 do begin
    ShellPath := ShellPaths[Index];
    if RegQueryStringValue(HKCU, ShellPath, '', RegisteredCommand) and (CompareText(RegisteredCommand, ExpectedCommand) <> 0) then begin
      Result := '右クリックメニューに同じ名前の別コマンドがあります。既存の登録を保護するため、登録を中止しました。';
      Exit;
    end;
  end;

  CleanupScript := ExpandConstant('{tmp}\Cleanup-LegacyExplorerRegistration.ps1');
  SparseErrorLog := ExpandConstant('{tmp}\UnityPackageExplorer-SparseRemovalError.txt');
  DeleteFile(SparseErrorLog);
  CleanupParameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' + #34 + CleanupScript + #34 +
    ' -InstallDirectory ' + #34 + InstallDirectory + #34 + ' -CurrentFilesManifest ' + #34 + CurrentFilesManifest + #34 +
    ' -MigrationScriptPath ' + #34 + MigrationScript + #34 + ' -Mode Upgrade -SparsePackageErrorLog ' + #34 + SparseErrorLog + #34;
  if WizardIsTaskSelected('sendto') then CleanupParameters := CleanupParameters + ' -KeepSendToOnUpgrade';
  if not Exec(PowerShellPath, CleanupParameters, '', SW_HIDE, ewWaitUntilTerminated, CleanupResultCode) then begin
    Result := '旧バージョンの登録と所有ファイルを整理できませんでした。既存ファイルを変更する前にセットアップを中止しました。';
    Exit;
  end;
  if CleanupResultCode <> 0 then begin
    CleanupErrorText := '旧バージョンの登録と所有ファイルを整理できませんでした。既存ファイルを変更する前にセットアップを中止しました。';
    if FileExists(SparseErrorLog) then begin
      CleanupErrorText := ReadTextFile(SparseErrorLog);
      if CleanupErrorText = '' then CleanupErrorText := '旧バージョンの登録と所有ファイルを整理できませんでした。';
      CleanupErrorText := CleanupErrorText + #13#10#13#10 + 'セットアップを中止しました。';
    end;
    Result := CleanupErrorText;
    Exit;
  end;
  DeleteFile(SparseErrorLog);
end;

function InitializeUninstall(): Boolean;
var
  PowerShellPath, CleanupScript, ErrorLogPath, Parameters, ErrorText: String;
  ExitCode: Integer;
begin
  Result := True;
  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  CleanupScript := ExpandConstant('{app}\Tools\Cleanup-LegacyExplorerRegistration.ps1');
  ErrorLogPath := ExpandConstant('{tmp}\UnityPackageExplorer-UninstallCleanupError.txt');
  DeleteFile(ErrorLogPath);
  Parameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' + #34 + CleanupScript + #34 +
    ' -InstallDirectory ' + #34 + ExpandConstant('{app}') + #34 + ' -CurrentFilesManifest ' + #34 +
    ExpandConstant('{app}\Tools\CurrentInstallFiles.txt') + #34 + ' -Mode Uninstall -SparsePackageErrorLog ' + #34 + ErrorLogPath + #34;
  if not Exec(PowerShellPath, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then begin
    ErrorText := 'UnityPackage Explorerの登録を解除できなかったため、アンインストールを中止しました。右クリックメニューの登録が残る可能性を避けるため、解除に成功してから再試行してください。';
    if FileExists(ErrorLogPath) then begin
      ErrorText := ReadTextFile(ErrorLogPath);
      if ErrorText = '' then ErrorText := 'UnityPackage Explorerの登録解除に失敗しました。';
      ErrorText := ErrorText + #13#10#13#10 + 'アンインストールを中止しました。';
    end;
    MsgBox(ErrorText, mbError, MB_OK);
    Result := False;
  end;
  DeleteFile(ErrorLogPath);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  InstallDirectory, EscapedDirectory, MarkerPath: String;
  MarkerText: AnsiString;
begin
  if CurStep = ssPostInstall then begin
    InstallDirectory := ExpandConstant('{app}');
    EscapedDirectory := InstallDirectory;
    StringChangeEx(EscapedDirectory, '\', '\\', True);
    MarkerPath := InstallDirectory + '\.unitypackage-explorer-inno-install.json';
    MarkerText := '{"Product":"UnityPackageExplorer","InstallDirectory":"' + EscapedDirectory + '"}';
    if not SaveStringToFile(MarkerPath, MarkerText, False) then
      Log('Could not write the UnityPackage Explorer ownership marker: ' + MarkerPath);
  end;
end;
