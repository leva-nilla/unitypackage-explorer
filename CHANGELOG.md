# Changelog

## Unreleased

- ZIP内の512 MiB超のUnityPackageを、メモリへ全量展開せずに読み込むよう修正。
- RAR4/RAR5とソリッドRARの閲覧、ドラッグ＆ドロップ、分類、右クリック登録に対応。
- 内包UnityPackageの項目数上限と、破損・大容量アーカイブの回帰テストを追加。

## 0.1.0 — preview

- ZIPを展開せずに内包するUnityPackageとPrefabを確認。
- UnityPackageの一覧、検索、画像表示、素材種類フィルターを追加。
- Prefab階層、FBX、基本テクスチャの近似表示と不足参照の警告。
- ドラッグ＆ドロップ、従来のコンテキストメニュー、「送る」から起動。
- 安全なUnityPackage展開、ユーザー単位のセットアップ、依存ライセンス通知。
- .NET 10 / Windows 11 x64の自己完結型アプリ。

既知の制約: Unityシェーダーや独自コンポーネントは再現しません。Prefab内のMesh fileIDをFBXの個別メッシュへ対応付けられない場合はFBX全体の近似表示となり、同じFBXの別参照やネストPrefabは省略して画面に警告します。Explorerのプレビュー欄とWindows 11の新コンテキストメニューには登録しません。
