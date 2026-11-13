# 設計仕様: プレビュー読み込みの段階化(モデル先表示・マテリアル後追い適用)と index キャッシュ

対象: `codex/feat-progressive-preview` ブランチ
関連コード: `src/UnityPackageExplorer.Rendering/LightweightPreviewLoader.cs`, `src/UnityPackageExplorer.PreviewHandler/{PreviewHandlerCom,ExplorerPreviewSurface,PreparedScenePayload}.cs`, `src/UnityPackageExplorer.Core/UnityAssetCatalog.cs`

## 1. 背景と目的

Explorer プレビュー欄で `.unitypackage`/`.zip` を選択した際、モデルが表示されるまで約 2.4 秒かかる(実測: 衣装パッケージ)。内訳:

| フェーズ | 実測 | 内容 |
|---|---|---|
| ストリーム一時コピー | ~0.65s | COM ストリーム → LocalLow へ全件コピー(サイズ比例) |
| index 構築 | ~0.45s | 外側アーカイブ走査 + ネスト unitypackage 走査 + meta GUID 読み |
| FBX パース〜マテリアル適用 | ~1.35s | StageAsset(FBX展開) + assimp パース + テクスチャ展開・マテリアル解決 |

現状は「マテリアル適用まで完了してから UI へ一括投下」のため、テクスチャ適用がボトルネックでもモデルが見えない。

目的:
1. **モデル先表示**: FBX パース完了時点でジオメトリを表示し、マテリアル/テクスチャは後追いで適用する。
2. **index キャッシュ**: 同一アーカイブの再選択時に index 構築を省略する。

非目的(今回やらない):
- COM ストリームの直接読み(一時コピー廃止)。COM マーシャリング経由のランダムアクセスはリスクが大きいため見送り。
- スタンドアロン App 側パイプラインの変更(`MainWindow` は既存 API `LoadAsync` をそのまま使用)。
- ネスト prefab の GUID→FBX マテリアル解決(既知の別課題)。

## 2. ユーザー意図の確認

- ファイル選択直後の待ち時間を短くしたい。
- 手段は「読み込み全体の高速化」または「モデルを先に表示して、マテリアル/テクスチャを非同期で順次適用」のいずれでもよい。両方の組み合わせ(A+B)で合意済み。
- 一時コピーの完全撤去(ストリーム直接読み)は、リスクが高いため今回は対象外でよい。
- 挙動の変化として「モデルが先に出て、その後テクスチャが載る」許容。

## 3. 段階化の設計(A)

### 3.1 API 分割(Rendering)

`LightweightPreviewLoader` に既存 `LoadAsync` を壊さず新 API を追加。

```
// フェーズ1: ジオメトリのみ(バックグラウンド実行)
LoadGeometryAsync(catalog, entry, temporaryRoot, ct)
  -> GeometryPreview { Scenes, Pending, Warnings }

// フェーズ2: マテリアル準備(バックグラウンド実行)
CompleteMaterialsAsync(GeometryPreview, catalog, entry, temporaryRoot, ct)
  -> MaterialApplyPlan { Specs: IReadOnlyList<SceneMaterialSpec>, Warnings, Bindings }
```

- `GeometryPendingMaterials`(Pending の要素): `Scene`, `ModelEntry`, `MaterialReferences`, `RendererAssignments`, `IsPrefabPreview`, `HasUnappliedMaterialOverrides`。フェーズ1終了時点でシーンは破棄されずフェーズ2で使う。
- `SceneMaterialSpec`(適用指示): `Mesh`(対象 MeshNode 参照), `TexturePath`, `Color`, `RenderMode`, `Cutoff`, `Visible`, `IsTransparent`。**Mesh への書き込みは含まない**(解決のみ)。フェーズ1で `MeshNode` のスナップショット(`EnumerateMeshes` 結果)を保持し、フェーズ2の解決はそのスナップに対して行う。
- 既存 `ApplyUnityMaterials` は内部を「解決(ResolveMaterialAssignments)」と「適用(ApplyMaterialSpecs)」に分割し、従来動作は `resolve → apply` の直列呼び出しとして維持。`MainWindow` の呼び出し経路は不変。

### 3.2 フェーズ1の内容

- prefab 解決(`ResolvePrefab`)→ placements 取得: 従来どおり。
- 各 placement: `StageAsset`(FBX 展開)→ `ImportModel`(assimp)まで。**マテリアル関連 I/O(PrepareMaterialBindings/StageImage)は行わない。**
- assimp が生成する素材のうち、赤い球(saturated placeholder)になる見た目を避けるため、フェーズ1の UI 投下直後に**名前を保持したまま**ニュートラル化する:
  - `PhongMaterialCore.Name` に元の assimp マテリアル名をコピーしてから置換。フェーズ2の解決はこの名前を使うため、名前を潰さないことが必須。
  - 対象は `IsSaturatedPrimaryColor` のみ。非該当メッシュは assimp の元素材のまま。
  - この処理は UI 投下前(バックグラウンド)に完了させる。

### 3.3 フェーズ2の内容

バックグラウンドスレッド:
1. `PrepareMaterialBindings`(マテリアル info 読み・テクスチャ展開。I/O が重い部分)。
2. `ResolveMaterialAssignments`: フェーズ1スナップの MeshNode に対し既存の名前一致ロジックで適用指示を作る。`SetMaterial` 相当の書き込みはしない。
3. 未一致メッシュの扱い: saturated → ニュートラル色 + 非表示、非該当 → assimp 元素材のまま(現行と同じ)。
4. `fallbackPaths` / single-material fallback / unused-material warnings も現行ロジックを移植。

UI スレッド(Dispatcher 経由、generation チェック付き):
5. `ApplyMaterialSpecs`: spec に従い `mesh.Material`/`IsTransparent`/`Visible` を設定。テクスチャなし+cutout の Visible 計算もここ。
6. 警告の追記表示。

### 3.4 ハンドラー側フロー(PreviewHandlerCom)

```
LoadSelectedAsync:
  phase1 = LoadGeometryAsync(...)          // TrackJob + DeferredTaskCleanup 既存パターン
  PostOwnedToSelectionUi(GeometryPayload)  // モデル表示 + ステータス "マテリアルを適用しています…"
  phase2 = CompleteMaterialsAsync(...)
  PostOwnedToSelectionUi(MaterialPayload)  // 適用 + 警告追記
```

- 世代管理: 既存 `requestId`/`selectionId` チェック(`PostOwnedToSelectionUi`)をフェーズ2にも適用。選択が変わったらフェーズ2の投稿は破棄される。
- キャンセル: `_selectionCancellation` がフェーズ2にも効く(既存の selection token を渡す)。フェーズ2完了前にシーンが破棄されるケース: 適用は「payload 生存確認(= 同じ PreparedPreview が surface に保持されていること)」を UI スレッド上で行い、破棄済みなら何もしない。
- temp ルート寿命: フェーズ1で surface に所有権移管(現行どおり)。フェーズ2は staging したテクスチャパス(同じ temp ルート配下)を参照するため、temp ルートの削除は surface 側 `ClearPrepared` に一本化する。フェーズ2失敗時は temp ルートを surface が既に持っているので追加クリーンアップ不要。
- タイムアウト: フェーズ1は既存 budget(初回選択は残り budget、手動切替は30秒)を維持。フェーズ2は30秒上限 + selection token。

### 3.5 UI(Surface)

- `ShowPrepared` は現行のまま(ジオメトリ + フェーズ1警告を受け取る)。警告は surface 側 `_activeWarnings` に累積し、フェーズ2でも先頭4件に統合表示。
- ステータス: フェーズ1投下時はサマリーに `· マテリアル適用中…` を付与し、フェーズ2完了時(`ApplyMaterialPlan`)に外す。フェーズ2失敗/キャンセル時は `MarkMaterialPhaseFinished` でサフィックスだけ外し、エラー表示はしない(ログのみ)。

### 3.6 競合・後始末の詳細(実装)

- フェーズ1投稿が拒否された場合(payload の世代不一致で Dispose 済み)は `geometry.IsDisposed` / token キャンセルを確認してフェーズ2を開始しない。
- 適用は UI スレッドで `ReferenceEquals(_prepared, owner) && !owner.IsDisposed` を確認してから行う。
- temp ルート寿命: フェーズ1投稿成功で surface に所有権移管。**投稿成功後は handler から触らない**(UI スレッドでのマテリアル適用がステージング済みテクスチャファイルを読むため)。
- フェーズ2の投稿拒否・失敗・キャンセル時は `SafeDeleteOwnedTemp(tempRoot)` を呼ぶ(この時点ではマテリアル適用が発生していないためステージングファイルは未使用。surface による削除と競合しても双方 no-op で無害)。

## 4. index キャッシュの設計(B)

### 4.1 キー

- zip/rar 経由: 外側アーカイブの「元ファイル名 + サイズ + 先頭64KB/末尾64KBのSHA-256」。サイズと端ハッシュで同一性を確実にする。パスベース入力でも同一ロジック。
- `.unitypackage` 直指定(`OpenPackage`): 同様に name+size+端ハッシュ。
- 保存先: `%LOCALAPPDATA%\..\LocalLow\UnityPackageExplorer\catalog-index-cache\<sha256(key)>.json`(書き込み不可なら Local 配下へフォールバック、それも不可ならキャッシュ無効)。上限 128 個、超えたら古いものから削除。
- **キャッシュは呼び出し側で明示的に有効化する**: `OpenZip/OpenRar/OpenPackage` の `cache` 引数に `CatalogIndexCache.ForArchive(path)` を渡す。既定は null(無効)で、単体テストや App の既存経路の決定性を保つ。PreviewHandler の `OpenCatalog` のみ渡す。

### 4.2 格納内容

```
CatalogIndexCache {
  Version: 1,
  ArchiveMembers: [ { Name, Length } ],          // 検証用
  Packages: { memberPath: [ { Guid, PathName, HasAsset, HasMeta, AssetPathName } ] },
  LooseMembers: [ { Name, Guid } ]               // meta から解決した GUID
}
```

### 4.3 命中判定と再構築

1. `archive.ReadIndex`(外側。zip 中心ディレクトリ等で安価)は常に行う。
2. members の Name/Length がキャッシュと完全一致 → 命中。
3. 命中時: ネスト `UnityPackageReader.ReadIndex` と meta 読みをスキップし、キャッシュから `UnityPackageEntry` を再構築。`UnityPackageReader` インスタンス(メンバー読み出し用)は再作成して `zipPackages` に紐付ける。
4. 不一致/破損/例外 → キャッシュを無視して従来フロー(キャッシュは上書き)。
5. 予算切れ等の動的 warnings はキャッシュ対象外(命中時は warnings 空)。表示上の警告は省略されることを仕様とする。

### 4.4 リスク

- 命中判定漏れによる古い index の表示 → name+size+端ハッシュでほぼ排除。誤命中時もキー不一致で再構築される。
- キャッシュ破損 → 例外時は miss 扱いで従来フロー。

## 5. 検証計画

- 単体テスト(CI):
  - `LoadGeometryAsync` がフェーズ1警告・scenes・pending を返すこと。
  - `CompleteMaterialsAsync` が解決のみで MeshNode を書き換えないこと(spec のみ)。
  - `ApplyMaterialSpecs` が spec 通り Material/Visible/IsTransparent を設定すること(タイポ・cutout 含む)。
  - 既存 `LoadAsync`(App 経路)の出力が変わらないこと。
  - index キャッシュ: 2回目の OpenZip がキャッシュ命中で同等 entries を返すこと、members 差分で miss になること。
- 手動検証(ユーザー):
  - 実機 Explorer で 同ファイル2回連続選択: 1回目はモデル先行表示→テクスチャ後追い、2回目は index 分が速くなる。
  - 選択連打: 世代管理で旧シーンに触れない。

## 6. 追加改善: ネスト unitypackage の展開キャッシュ(実測後に追加)

実機ログで、フェーズ2(マテリアル/テクスチャ解決)が `11_Full` で約 10 秒かかることが判明。原因は `UnityPackageReader.ReadAsset` が**呼び出しごとに**外側 zip member を再展開し tar を全走査するため(マテリアル情報 n 件 + テクスチャ m 枚 ≒ n+m 回の全展開)。

対策: `UnityPackageReader` に展開済み tar のメモリキャッシュを追加。

- 外側カタログの index 読み(`expansionBudget` 付き)または 2 回目以降の open で、展開済み tar を一度メモリに読み込む(1 回きりの読み出しパッケージは従来どおり)。
- 以降の `ReadAsset`/`ReadIndex` はメモリ走査(zip member の再展開なし)。
- メモリキャッシュの上限: 1 reader あたり 64MiB、プロセス合計 256MiB。超過時はディスクにスピルする。これは保持済みキャッシュの上限であり、構築中のバッファや複数プロセスを含むピークメモリの上限ではない。
- **エントリ索引**: キャッシュ作成時に tar 全体を1回走査して `エントリ名 → (DataOffset, Length)` を記録し、`ReadAsset` はシーク+ペイロード読み(743MB の全走査35回 ≒ 26GB のメモリ走査を、必要バイト読みだけに削減)。索引に無い/索引未構築時は従来の順次走査にフォールバック。索引は regular file のみ記録。
- **ディスクスピル**: メモリ上限超過時は展開済み tar を一時ファイルに保存し、同じ所有ハンドルを使ってオフセット索引で読む。Explorer プレビューは低整合性プロセスのため `%LOCALAPPDATA%\..\LocalLow\UnityPackageExplorer\ExpandedCache` を使い、その他は既定で `%TEMP%` を使う。ディスクの空き容量と速度に依存し、初回展開には時間がかかる。既存の展開後 2GiB 安全上限は変わらない。スピルファイルは `FileOptions.DeleteOnClose` の所有ハンドルで管理し、`Dispose()` またはハンドルのファイナライズ時に削除される。現状の catalog は reader を明示的に破棄しないため、プレビュー終了直後の削除は保証しない。
- キャッシュ済みデータは `MemoryStream(buffer, 0, length, writable: false)` またはスピルファイルの読み取り専用ハンドルとして渡すため、並列読み出しにも安全。`MemoryStream.ToArray()` を使わず成長バッファを再利用して大容量時の二重コピーを回避。
- 予算(aggregate decompressed size limit)の意味論は変わらない: キャッシュ読みも同じ budget で消費され、超過時は従来と同じ例外経路。
- 並列化は見送り: 索引読みの実測を優先する。必要になれば `PrepareMaterialBindings` の material reference 単位の bounded parallelism を検討する。

## 7. 残課題(対象外の記録)

- ネスト prefab の GUID→FBX マテリアル解決失敗(灰色・近似表示)。
- COM ストリーム一時コピーの完全撤去。
