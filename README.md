# UnityPackage Explorer

Unity を起動せずに `.unitypackage` の中身、Prefab の階層、参照 FBX を確認する Windows ツールです。エクスプローラーの右クリックから専用画面を開けます。

## できること

- `.unitypackage` 内をフォルダ階層、検索、実際に含まれるファイル種別で絞り込み。画像、Prefab、FBX をその場でプレビュー。
- PNG、JPEG、BMP、GIF、一般的な TGA、TIFF の画像を表示。PSD は一覧には出ますが、プレビューには対応していません。
- Prefab の GameObject 階層を表示し、参照 FBX を配置・回転・拡大して確認。同梱または同じ Unity プロジェクトにある画像を読み、基本テクスチャとして表示。
- エクスプローラーのプレビュー欄向けハンドラーをユーザー単位で登録。パッケージの代表画像と種類別件数、または Prefab の階層要約を表示します。
- パッケージの展開は共通の .NET 読み取り処理を使い、指定した場所に新しい `<名前>_extracted` フォルダーを作成。既存名がある場合は番号を付けて、既存ファイルを上書きしない。閲覧だけでは隣にファイルを作りません。

シェーダー、Unity 独自コンポーネント、Unity による描画結果は再現しません。基本色テクスチャだけを対応するマテリアルと FBX 名から対応付けます。参照先がない、FBX 以外のメッシュ形式、対応しない画像形式など、表示できないものは画面に理由を出します。

## セットアップ

Windows x64 と .NET 9 SDK が必要です。次を PowerShell で実行します。

```powershell
pwsh -ExecutionPolicy Bypass -File .\Install-UnityPackageExplorerContextMenu.ps1
```

スクリプトが Release ビルドを作成し、現在のユーザーの HKCU に右クリックメニューとプレビューハンドラーを登録します。管理者権限は使いません。

エクスプローラーでプレビュー欄を表示するには `Alt+P` を押し、`.unitypackage` または `.prefab` を選択します。右クリックの「内容を表示（UnityPackage Explorer）」または「Prefab を表示（UnityPackage Explorer）」で専用画面を開きます。

### 現在の制約

以前の Windows 11 実機確認では、登録後もプレビュー欄が「プレビューを利用できません」と表示されました。COM 登録仕様に合わせて更新・再登録していますが、この更新後の Explorer 画面は操作環境で開けていないため、プレビュー表示は未確認です。確実に閲覧する場合は右クリックから専用画面を開いてください。

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

`UnityPackageTools.ps1` の `-Action extract|open` は引き続き使えます。`-Action inspect` は新しい専用画面を起動します。展開は共通 Core ライブラリが全パスを検証してから始めるため、危険なパスや壊れたアーカイブでは出力を作成せず、展開途中の失敗では新規出力だけを取り除きます。
