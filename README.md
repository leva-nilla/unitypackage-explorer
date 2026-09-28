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
- パッケージの展開は共通の .NET 読み取り処理を使い、指定した場所に新しい `<名前>_extracted` フォルダーを作成。既存名がある場合は番号を付けて、既存ファイルを上書きしない。閲覧だけでは隣にファイルを作りません。

シェーダー、Unity 独自コンポーネント、Unity による描画結果は再現しません。3D 表示は簡易プレビューで、基本色テクスチャをFBX内のマテリアル名に基づいて対応付けます。マテリアル名が一致しない場合や対応しない画像形式、参照先がない場合は、テクスチャが未適用となる理由を画面に表示します。

## セットアップ

Windows x64 と .NET 9 SDK が必要です。次を PowerShell で実行します。

```powershell
pwsh -ExecutionPolicy Bypass -File .\Install-UnityPackageExplorerContextMenu.ps1
```

スクリプトが Release ビルドを作成し、現在のユーザーの「送る」メニューに UnityPackage Explorer のショートカットを登録します。選択したファイルのパスをアプリへ渡します。管理者権限は使いません。

ファイルを右クリックし、「送る」→「UnityPackage Explorer」を選ぶと専用画面で開きます。複数ファイルを一度に送った場合はアプリ側で案内を表示します。

### 現在の制約

Explorer のプレビュー欄用ハンドラーも登録しますが、現時点で Explorer 上の表示は確認できていません。Mark of the Web がないテスト ZIP でもプレビュー欄に内容が出ず、コンテキストメニューの直接項目もこの環境では表示されませんでした。そのため「送る」またはアプリへのドラッグ＆ドロップを使ってください。Windows 11 はインターネット由来の Mark of the Web が付いたファイルのプレビューを別途ブロックする場合もあります。元ファイルのブロック解除は不要です。詳細は [Microsoft の案内](https://support.microsoft.com/en-us/servicing/os/windows/docs/2025/10/file-explorer-automatically-disables-the-preview-feature-for-files-downloaded-from-the-internet) を参照。

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
