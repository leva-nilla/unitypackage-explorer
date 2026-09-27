# UnityPackage Explorer

Unity を起動せずに `.unitypackage` の中身、Prefab の階層、参照 FBX を確認する Windows ツールです。エクスプローラーの右クリックから専用画面を開けます。

## できること

- `.unitypackage` 内をフォルダ階層、ファイル種別、検索で確認。画像、Prefab、FBX をその場でプレビュー。
- Prefab の GameObject 階層を表示し、参照 FBX を配置・回転・拡大して確認。同梱または同じ Unity プロジェクトにある画像を読み、基本テクスチャとして表示。
- エクスプローラーのプレビュー欄向けハンドラーをユーザー単位で登録。パッケージの代表画像と種類別件数、または Prefab の階層要約を表示します。
- パッケージの展開は、指定したフォルダーの中に新しい `<名前>_extracted` フォルダーを作成。既存名がある場合は番号を付けて、既存ファイルを上書きしない。

シェーダー、Unity 独自コンポーネント、Unity による描画結果は再現しません。参照先がない、FBX 以外のメッシュ形式など、表示できないものは画面に理由を出します。

## セットアップ

Windows x64 と .NET 9 SDK が必要です。次を PowerShell で実行します。

```powershell
pwsh -ExecutionPolicy Bypass -File .\Install-UnityPackageExplorerContextMenu.ps1
```

スクリプトが Release ビルドを作成し、現在のユーザーの HKCU に右クリックメニューとプレビューハンドラーを登録します。管理者権限は使いません。

エクスプローラーでプレビュー欄を表示するには `Alt+P` を押し、`.unitypackage` または `.prefab` を選択します。右クリックの「内容を表示（UnityPackage Explorer）」または「Prefab を表示（UnityPackage Explorer）」で専用画面を開きます。

### 現在の制約

この環境の Windows 11 では、登録後もプレビュー欄が「プレビューを利用できません」と表示されました。登録・解除と COM インターフェイスの起動は確認できましたが、Explorer からのプレビュー表示は未解決です。確実に閲覧する場合は右クリックから専用画面を開いてください。

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

`UnityPackageTools.ps1` の `-Action extract|open` は引き続き使えます。`-Action inspect` は新しい専用画面を起動します。パスに `..` や Windows の予約名が含まれるアーカイブ項目は展開しません。
