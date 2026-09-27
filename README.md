# UnityPackage Explorer Tools

UnityPackage Explorer は、Windows Explorer の `.unitypackage` 向けコンテキストメニューから
「内容を表示」「展開」「開く」を実行するためのPowerShellスクリプトです。

## 構成

- `UnityPackageTools.ps1` ... 本体。
  - `-Action inspect|extract|open`
  - `-File "..."`
  - `-Destination "..."`（`extract` / `open` の出力先。未指定時は同階層の `<元ファイル名>_extracted`）
- `Install-UnityPackageExplorerContextMenu.ps1` ... 右クリックメニューを HKCU に登録
- `Uninstall-UnityPackageExplorerContextMenu.ps1` ... 登録解除

## 使い方

### 1. メニュー登録（要実行）

```powershell
pwsh -ExecutionPolicy Bypass -File .\Install-UnityPackageExplorerContextMenu.ps1
```

### 2. 動作

- `.unitypackage` を右クリック
  - `内容を表示（UnityPackage）`
  - `展開（UnityPackage）`
  - `開く（UnityPackage）`
- 「内容を表示」は、`pathname` を一覧化したHTMLを作成し開きます。`<対象>.unitypackage.viewer.html`
- 「展開」は、`asset` と `asset.meta` を安全化した相対パスで復元します。
- 「開く」は展開後にそのフォルダを Explorer で表示します。

## 既知の仕様 / 制約

- 展開先は、同一フォルダに `<unitypackage名>_extracted` を作ります。
- 指定先が既に存在する場合は `-2`, `-3`, ... を付与して新規作成します。
- `pathname` の相対パス保護:
  - `..` は除去して、サンドボックス外へ書き込まないようにしています。
  - Windows禁止文字は `_` に置換します。
- `asset`/`asset.meta` 以外の `pathname` ベース（例: フォルダメタ情報）については現状復元対象から除外しています。
- 文字コードは UTF-8 前提。壊れたアーカイブでは失敗報告します。

## ロールバック

```powershell
pwsh -ExecutionPolicy Bypass -File .\Uninstall-UnityPackageExplorerContextMenu.ps1
```

## 例外テスト想定

- 存在しないファイル、拡張子不一致
- パスに空白や日本語を含む `.unitypackage`
- 右クリックからの `%1` 引数受け渡し
- 同名保存先を繰り返し指定し直し（上書き回避）

## 変更履歴

- 2026-09-09: 初版
