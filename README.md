# UnityPackage Explorer

Unity を起動せずに `.unitypackage`、それを含む `.zip`、Prefab の階層、参照 FBX を確認する Windows ツールです。ファイルをアプリへドラッグ＆ドロップするか、エクスプローラーの「送る」から専用画面を開けます。

## できること

- `.unitypackage` 内をフォルダ階層、検索、実際に含まれるファイル種別で絞り込み。画像、Prefab、FBX をその場でプレビュー。
- `.zip`、`.unitypackage`、`.prefab` をウィンドウへドラッグ＆ドロップして開けます。一度に扱うファイルは1つです。読み込みは元ファイルを移動・展開しません。
- `.zip` は展開せず、内包する `.unitypackage` の仮想ツリーとZIP直収録ファイルを表示。内容を確認後、分類先を選んで移動ボタンを押した場合だけ、外側のZIPを既存フォルダーへ移動します。同名ファイルがある場合は番号を付け、上書きしません。
- PNG、JPEG、BMP、GIF、一般的な TGA、TIFF の画像を表示。PSD は一覧には出ますが、プレビューには対応していません。
- Prefab の GameObject 階層を表示し、参照 FBX を配置・回転・拡大して確認。同梱または同じ Unity プロジェクトにある画像を読み、基本テクスチャとして表示。
- パッケージ内容と Prefab 階層は最初にルート直下を展開し、さらに深い階層は必要な部分だけ手動で開けます。
- 3D 表示は Unity Scene View 風の操作に対応。右ボタンを押しながらマウスで見回し、W/A/S/D で移動、Q/E で上下移動、Shift で加速します。右ボタンを押したままホイールで移動速度を調整し、通常のホイールはズーム、F キーは表示中モデルの bounds 中心へ再フレームします。
- エクスプローラーのプレビュー欄向けハンドラーをユーザー単位で登録。`.unitypackage` と `.zip` は画像と種類別件数、Prefab は階層と参照テクスチャを表示します。
- Windows 11 の右クリックメニュー向けに sparse MSIX + IExplorerCommand を用意しています。manifest は wildcard 項目を宣言し、COM側で通常ファイルの `.zip`、`.unitypackage`、`.prefab` だけを表示対象にします。パッケージには manifest とネイティブ COM DLL のみを入れ、WPF アプリ本体は外部配置します。
- パッケージの展開は共通の .NET 読み取り処理を使い、指定した場所に新しい `<名前>_extracted` フォルダーを作成。既存名がある場合は番号を付けて、既存ファイルを上書きしない。閲覧だけでは隣にファイルを作りません。

シェーダー、Unity 独自コンポーネント、Unity による描画結果は再現しません。3D 表示は簡易プレビューで、基本色テクスチャをFBX内のマテリアル名に基づいて対応付けます。マテリアル名が一致しない場合や対応しない画像形式、参照先がない場合は、テクスチャが未適用となる理由を画面に表示します。

## セットアップ

Windows x64、.NET 9 SDK、Visual Studio C++ x64 build tools が必要です。初回インストールまたは更新時に、次を PowerShell で実行します。

```powershell
pwsh -ExecutionPolicy Bypass -File .\Install-UnityPackageExplorerContextMenu.ps1
```

スクリプトは自己完結型 win-x64 アプリを `%LOCALAPPDATA%\Programs\UnityPackageExplorer\Release` に配置し、Start Menu と現在のユーザーの「送る」にショートカットを作成します。ドラッグ＆ドロップと「送る」から開くアプリは、このインストール先の `UnityPackageExplorer.App.exe` です。署名済み sparse package が同じインストール先で CurrentUser に登録済みの場合は、対象ファイル用の右クリックコマンドも登録します。既存の空でないフォルダーは、製品の所有マーカーとユーザー単位のインストール記録が一致しない限り更新しません。

Preview Handler COM host は `PreviewHandler` サブフォルダーに配置します。これは自己完結型publishには含まれず、Explorerで使うには .NET 9 Desktop Runtime が必要です。インストール解除時は関連付けとショートカットだけを外し、アプリ本体と所有マーカーは保持します。安全に再インストールできるよう、ファイルの削除は行いません。

ファイルを右クリックし、「送る」→「UnityPackage Explorer」を選ぶと専用画面で開きます。複数ファイルを一度に送った場合はアプリ側で案内を表示します。

### Windows 11 の右クリック直下メニュー

右クリック直下の新メニューには sparse MSIX と署名を使います。manifestはWindows向けにwildcard項目を宣言し、IExplorerCommandの`GetState`が`.zip`、`.unitypackage`、`.prefab`以外を隠します。CurrentUserへのsparse package登録、COMクラスactivation、および `.zip` で有効・`.exe` で非表示になる `GetState` は実機で確認済みです。COMの`GetTitle`は「UnityPackage Explorer で開く」を返します。新メニュー上の表示とInvoke起動は、Explorer画面での確認が残っています。

「その他のオプションを確認」（従来メニュー）には、sparse packageが同じCurrentUserに登録されている場合だけ、ユーザー単位のwildcard verbを追加します。こちらも同じCOMクラスが対応する3拡張子だけを有効にします。パッケージ未登録時は利用できず、「送る」とドラッグ＆ドロップを使えます。従来メニューでの表示・起動も実画面確認が残っています。

開発用の自己署名証明書を使う場合、署名用秘密鍵は `Cert:\CurrentUser\My` にエクスポート不可で作成し、sparse packageはCurrentUserに登録します。ただし、この環境では署名証明書を `Cert:\LocalMachine\TrustedPeople` に信頼登録する必要があり、管理者権限とPC全ユーザーへの影響があります。証明書はCode Signing EKUと明示的なCA:false Basic Constraintsを持ち、公開 `.cer` のみ `%LOCALAPPDATA%\UnityPackageExplorer\Signing` に保存します。生成スクリプトは証明書ストアとファイルへの変更前に確認し、`-WhatIf` で変更内容だけ確認できます。

```powershell
.\tools\context-menu\New-DevelopmentSigningCertificate.ps1 -WhatIf
.\tools\context-menu\New-DevelopmentSigningCertificate.ps1 -Confirm
```

実機では `Cert:\CurrentUser\TrustedPeople` に公開証明書を追加しても `Add-AppxPackage` が `0x800B0109` で失敗しました。Windows 11 のMSIXトラブルシューティング資料は `Cert:\LocalMachine\TrustedPeople` が必要で、管理者権限が要ると説明しています。sparse packageのper-user資料にはCurrentUserの例もありますが、この環境の実測に合わせ、インストーラーはLocalMachine storeに同じthumbprintがあることを確認し、なければ停止して管理者用コマンドを表示します。インストーラーは昇格も証明書追加も行いません。LocalMachineへの追加はこのPCの全ユーザーに関わる信頼変更なので、以下のコマンドを実行する前に内容と影響を確認してください。

```powershell
# 管理者として開いたPowerShellで、影響を確認した後に必要なら1回だけ実行
Import-Certificate -FilePath "$env:LOCALAPPDATA\UnityPackageExplorer\Signing\UnityPackageExplorer.Development.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPeople

# 証明書追加後、通常のPowerShellでCurrentUser packageを登録
.\Install-UnityPackageExplorerContextMenu.ps1 `
  -ContextMenuCertificateThumbprint '<thumbprint>' `
  -TrustCertificatePath "$env:LOCALAPPDATA\UnityPackageExplorer\Signing\UnityPackageExplorer.Development.cer"
```

解除スクリプトはpackageをCurrentUserから外しますが、全ユーザーに影響する `Cert:\LocalMachine\TrustedPeople` の証明書は削除しません。LocalMachine証明書は他ユーザーやアプリが使っていないことを確認し、削除を明示的に決めた場合だけ、管理者PowerShellでthumbprintを指定して解除してください。`Cert:\CurrentUser\My` の秘密鍵と公開CERはアプリ解除時も既定で保持します。

```powershell
.\Uninstall-UnityPackageExplorerContextMenu.ps1

# 管理者として開いたPowerShellで、他の利用がないと確認してから実行
Remove-Item -LiteralPath 'Cert:\LocalMachine\TrustedPeople\<thumbprint>' -Force
```

### 現在の制約

Explorer のプレビュー欄用ハンドラーは登録済みですが、Explorer上の表示は確認できていません。Mark of the Web がないテスト ZIP でもプレビュー欄に内容が出ませんでした。従来の静的verbも実際の右クリック画面では表示されませんでしたが、sparse packageとCOM activationは確認済みで、新方式メニューの実画面確認は残っています。Windows 11 はインターネット由来の Mark of the Web が付いたファイルのプレビューを別途ブロックする場合もあります。unsigned packageはmanifest検証・ビルドには使えますが、この実行可能アプリ起動を含むsparse packageは `-AllowUnsigned` で登録できませんでした。元ファイルのブロック解除は不要です。詳細は [Microsoft の案内](https://support.microsoft.com/en-us/servicing/os/windows/docs/2025/10/file-explorer-automatically-disables-the-preview-feature-for-files-downloaded-from-the-internet) を参照。

## 開発

```powershell
dotnet build .\UnityPackageExplorer.sln -c Release
dotnet test .\tests\UnityPackageExplorer.Tests\UnityPackageExplorer.Tests.csproj
```

インストール後にアプリを直接起動する場合:

```powershell
& "$env:LOCALAPPDATA\Programs\UnityPackageExplorer\Release\UnityPackageExplorer.App.exe" "C:\Assets\sample.unitypackage"
```

## 解除

```powershell
pwsh -ExecutionPolicy Bypass -File .\Uninstall-UnityPackageExplorerContextMenu.ps1
```

本ツールが登録したプレビューとメニューだけを解除します。Windows の既定アプリや `.unitypackage` / `.prefab` の既定の開き方は変更しません。

## 既存 PowerShell コマンド

`UnityPackageTools.ps1` の `-Action inspect` は `.unitypackage`、`.zip`、`.prefab` を専用画面で開きます。`-Action extract|open` は従来どおり `.unitypackage` に対応します。展開は共通 Core ライブラリが全パスを検証してから始めるため、危険なパスや壊れたアーカイブでは出力を作成せず、展開途中の失敗では新規出力だけを取り除きます。
