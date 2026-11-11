# Explorerプレビュー欄の軽量3D

## 目的・表示

Windows 11 x64のExplorerで`.unitypackage`と`.zip`を選択し、代表Prefabの軽量3Dを表示する。既存のAutomaticPreviewSelectorを使い、上部にPrefab選択、中央に3D/画像、下部に内容の要約と読込み結果を配置する。左ドラッグで回転、ホイールで拡大、全体表示ボタンでカメラを戻す。モデルがなければ代表画像/参照テクスチャ、画像もなければ一覧を表示する。

FBX準備・材質対応・Prefab Variant解決は共通描画ライブラリへ分離し、専用アプリでも共有する。専用アプリのlilToon/分類/展開は維持するが、Explorer内には追加しない。共通APIはcatalog、selected entry、temp root、tokenを受け取り、明示的に破棄できるscene/materials/warningsを返す。

## 寿命・性能・安全

索引/解析/画像デコードはバックグラウンド、画像表示解像度最大2048px。未選択素材を一括展開しない。30秒で取消を要求し、利用できる要約を示す。古い選択結果を反映せず、取消できないnative importは完了まで一時データを保持してから回収する。

IInitializeWithStream優先、Initializeでは入力を保持するだけ、DoPreviewで開始。COMスレッド間マーシャリング、stream seek直列化、Unloadによる取消/描画停止/解放。専用AppIDでPrevhost.exeを使用し低IL隔離を維持する。要求ごとLocalLow領域へ必要素材だけ保存、元アーカイブ/隣接フォルダを変更しない。

## 導入・解除

COMは.NET 10 Desktop Runtime x64を使うframework-dependent配置。セットアップがruntimeを検出し不足時は公式URLを案内、preview登録を行わない（専用アプリ利用は可能）。Explorerプレビューは任意、配布既定OFF、このPCではON。ユーザー単位登録で既定アプリ/UserChoiceは変えず、既存handlerを記録、自分の登録が現存する場合だけ元に戻す。

ダウンロード品のMotWは自動解除しない。信頼できるファイルのみWindowsプロパティで解除する手順をREADMEへ記載する。MiniStack/11_FullにはZoneId=3あり。解除はユーザー操作が必要。検証用には原本を変更しない信頼済みコピーか自作サンプルを使用する。

## 完了条件

- 自作ZIP/UnityPackageで実Explorer欄の表示、回転/拡大/全体表示、Prefab切替、画像/要約を確認。
- MiniStackと11_FullのVariant/材質/大容量をローカル検証、購入素材を配布・公開画像へ含めない。
- 連続選択、取消、開閉/resize、再初期化、破損/不正パス/欠損参照を回帰検証。元ファイルSHA256と隣接フォルダを保護。
- CIでCOM生成/起動/破棄、登録と復元、runtime不足、setup更新/解除を確認。
- Spec/StandardsレビューとCIに成功し、このPCに修正版setupを適用。既存軽量/lilToon切替と右クリック起動も確認。
- Explorerと自作サンプルの確認に限りcomputer-use再開の許可あり。その他アプリの操作は行わない。
- Release公開・X告知・PR mergeは今回行わない。実装担当とreview担当のサブエージェントはLuna。

## 一次資料

- https://learn.microsoft.com/en-us/windows/win32/shell/preview-handlers
- https://learn.microsoft.com/en-us/windows/win32/shell/how-to-register-a-preview-handler
- https://learn.microsoft.com/en-us/dotnet/core/native-interop/expose-components-to-com
- https://support.microsoft.com/en-us/servicing/os/windows/docs/2025/10/file-explorer-automatically-disables-the-preview-feature-for-files-downloaded-from-the-internet
