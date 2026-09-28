# UnityPackage Explorer

Unity を起動せずに `.unitypackage`、それを含む `.zip`、Prefab の階層、参照 FBX を確認する Windows ツールです。ファイルをアプリへドラッグ＆ドロップするか、エクスプローラーの「送る」から専用画面を開けます。

## できること

- `.unitypackage` 内をフォルダ階層、検索、実際に含まれるファイル種別で絞り込み。画像、Prefab、FBX をその場でプレビュー。
- `.zip`、`.unitypackage`、`.prefab` をウィンドウへドラッグ＆ドロップして開けます。一度に扱うファイルは1つです。読み込みは元ファイルを移動・展開しません。
- `.zip` は展開せず、内包する `.unitypackage` の仮想ツリーとZIP直収録ファイルを表示。内容を確認後、分類先を選んで移動ボタンを押した場合だけ、外側のZIPを既存フォルダーへ移動します。同名ファイルがある場合は番号を付け、上書きしません。
- PNG、JPEG、BMP、GIF、一般的な TGA、TIFF の画像を表示。PSD は一覧には出ますが、プレビューには対応していません。
- Prefab の GameObject 階層を表示し、参照 FBX を配置・回転・拡大して確認。同梱または同じ Unity プロジェクトにある画像を読み、基本テクスチャとして表示。
- 3D 表示は Unity Scene View 風の操作に対応。右ボタンを押しながらマウスで見回し、W/A/S/D で移動、Q/E で上下移動、Shift で加速します。右ボタンを押したままホイールで移動速度を調整し、通常のホイールはズーム、F キーは表示中モデルの bounds 中心へ再フレームします。
- エクスプローラーのプレビュー欄向けハンドラーをユーザー単位で登録。`.unitypackage` と `.zip` は画像と種類別件数、Prefab は階層と参照テクスチャを表示します。
- Windows 11 の新しい右クリックメニュー向けに、.zip、.unitypackage、.prefab を対象とする sparse MSIX + IExplorerCommand を用意しています。パッケージには manifest とネイティブ COM DLL のみを入れ、WPF アプリ本体は外部配置します。
- パッケージの展開は共通の .NET 読み取り処理を使い、指定した場所に新しい `<名前>_extracted` フォルダーを作成。既存名がある場合は番号を付けて、既存ファイルを上書きしない。閲覧だけでは隣にファイルを作りません。

シェーダー、Unity 独自コンポーネント、Unity による描画結果は再現しません。3D 表示は簡易プレビューで、基本色テクスチャをFBX内のマテリアル名に基づいて対応付けます。マテリアル名が一致しない場合や対応しない画像形式、参照先がない場合は、テクスチャが未適用となる理由を画面に表示します。

## セットアップ

Windows x64 と .NET 9 SDK が必要です。次を PowerShell で実行します。

```powershell
pwsh -ExecutionPolicy Bypass -File .\Install-UnityPackageExplorerContextMenu.ps1
```

スクリプトが Release ビルドを作成し、現在のユーザーの「送る」メニューに UnityPackage Explorer のショートカットを登録します。選択したファイルのパスをアプリへ渡します。管理者権限は使いません。

ファイルを右クリックし、「送る」→「UnityPackage Explorer」を選ぶと専用画面で開きます。複数ファイルを一度に送った場合はアプリ側で案内を表示します。

### Windows 11 の右クリック直下メニュー

右クリック直下の項目は sparse MSIX と署名が必要です。この環境では静的verbは実際の Explorer メニューに表示されず、sparse package の登録前なので新方式の表示もまだ確認できていません。既定のセットアップだけでは直下メニューを登録せず、「送る」とドラッグ＆ドロップが使えます。

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

Explorer のプレビュー欄用ハンドラーも登録しますが、現時点で Explorer 上の表示は確認できていません。Mark of the Web がないテスト ZIP でもプレビュー欄に内容が出ず、コンテキストメニューの直接項目もこの環境では表示されませんでした。そのため「送る」またはアプリへのドラッグ＆ドロップを使ってください。Windows 11 はインターネット由来の Mark of the Web が付いたファイルのプレビューを別途ブロックする場合もあります。unsigned package はmanifest検証・ビルドには使えますが、この実行可能アプリ起動を含む sparse package は `-AllowUnsigned` でもユーザー単位登録できませんでした。署名済みpackageの登録前のため、新メニューの表示と項目起動は未確認です。元ファイルのブロック解除は不要です。詳細は [Microsoft の案内](https://support.microsoft.com/en-us/servicing/os/windows/docs/2025/10/file-explorer-automatically-disables-the-preview-feature-for-files-downloaded-from-the-internet) を参照。

## 開発

```powershell
dotnet build .\UnityPackageExplorer.sln -c Release
dotnet test .\tests\UnityPackageExplorer.Tests\UnityPackageExplorer.Tests.csproj
```

アプリを直接起動する場合:

```powershell
dotnet run --project .\src\UnityPackageExplorer.App -- "C:\Assets\sample.unitypackage"
```

## 解除

```powershell
pwsh -ExecutionPolicy Bypass -File .\Uninstall-UnityPackageExplorerContextMenu.ps1
```

本ツールが登録したプレビューとメニューだけを解除します。Windows の既定アプリや `.unitypackage` / `.prefab` の既定の開き方は変更しません。

## 既存 PowerShell コマンド

`UnityPackageTools.ps1` の `-Action inspect` は `.unitypackage`、`.zip`、`.prefab` を専用画面で開きます。`-Action extract|open` は従来どおり `.unitypackage` に対応します。展開は共通 Core ライブラリが全パスを検証してから始めるため、危険なパスや壊れたアーカイブでは出力を作成せず、展開途中の失敗では新規出力だけを取り除きます。
