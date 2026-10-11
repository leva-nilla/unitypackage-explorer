# lilToon の同梱・Unity を使わないプレビューの実現性

調査日: 2026-10-11。現行構成は .NET 10 / WPF、HelixToolkit 3.1.2 / Assimp。調査のみで、シェーダーの導入・性能測定は行っていない。

## 今回の選択

ユーザーは調査中の選択肢から「今の描画で軽い近似対応（色・透過など）を追加」を選んだ。今回は Helix を維持し、材質の色・基本透過の近似を追加する。本家 shader の同梱、WebView2 / Three.js / three-liltoon の導入、renderer の置換は行わない。lilToon 本体を実行した表示という表記はしない。

ここでの「軽い」は追加 renderer を導入しない実装範囲を指す。現 PC における効果別の描画時間やメモリ増加は未測定で、性能達成の判断は実装後の比較で行う。

## 結論

- 本家 lilToon は MIT ライセンスで、著作権表示・許諾文を保持して同梱・改変・再配布できる。取り込むファイルに対応する第三者通知も配布物に残す。[本家 LICENSE](https://github.com/lilxyzw/lilToon/blob/master/LICENSE)、[本家 Third Party Notices](https://github.com/lilxyzw/lilToon/blob/master/Assets/lilToon/Third%20Party%20Notices.md)
- 同梱するだけでは現行ビューアーの描画は変わらない。本家は ShaderLab と Unity 向け HLSL からなり、Built-in RP 用ソースは Unity の include を参照する。現行 Helix 向けの描画入出力・ライト・テクスチャ・描画パスへ接続する移植が必要、というのがソースからの判断。[本家ファイル構成](https://lilxyzw.github.io/lilToon/ja_JP/dev/files.html)、[Built-in RP ソース](https://github.com/lilxyzw/lilToon/blob/master/Assets/lilToon/Shader/Includes/lil_pipeline_brp.hlsl)
- 非 Unity 向けの既存候補は `three-liltoon`。本家のコードを変換する MIT の非公式移植だが、作者は alpha と明記している。WebGL2 / Three.js 用であり、Helix の材質へ直接差し込むライブラリーではない。[作者 README](https://github.com/mochiya-labs/three-liltoon/blob/main/README.md)、[LICENSE](https://github.com/mochiya-labs/three-liltoon/blob/main/LICENSE)
- この PC の起動時間・初回表示時間・メモリ・GPU 負荷の比較値は未測定。「軽ければ実装」の条件を満たしたとは現時点で判断できない。

## 現行実装で不足している情報

ローカルソース `src/UnityPackageExplorer.Core/AssetModels.cs` と `UnityAssetCatalog.ReadMaterialInfo` は、材質名と対応／未対応のテクスチャ GUID を保持する。Shader GUID・描画モード・数値・色・UV の設定は保持していない。`MainWindow.ApplyUnityTextures` は FBX の材質／メッシュ名と照合し、`PhongMaterialCore` の diffuse map を置く。

従って、現在白く見える物件でマテリアルの対応先を特定できない場合、lilToon を追加してもその対応は解決しない。先に、Prefab の renderer / material slot と FBX の submesh を安全に対応付ける必要がある。これはローカル実装に対する判断で、lilToon 本家の制限ではない。

## 実装方法の比較

| 方法 | 現行アプリへの影響 | 表示の意味 | 判断 |
| --- | --- | --- | --- |
| 本家ファイルだけ同梱 | 通知・配布サイズが増える | 現行表示は変わらない | 表示目的には足りない |
| Helix に本家 HLSL の互換層・材質・描画パスを追加 | DX11 の材質とパスを新規実装。既存 FBX、カメラ、WPF は再利用可能 | 対応範囲を移植した lilToon | 大きな実装・検証が必要 |
| WebView2 + Three.js + three-liltoon を任意ビューとして追加 | 別 renderer、データ受け渡し、カメラ、ランタイム、資源破棄が必要 | 非公式移植による近似表示 | まず分離した試作で測定 |
| Helix で独自の簡易 Toon 材質を作る | 現行 renderer 内で収まる。対象機能を少なくできる | 独自近似であり lilToon ではない | 別案。lilToon 対応と表記しない |

Helix 3.1.2 は DX11 ベースで、公式 CustomShaderDemo は独自 technique、vertex/pixel shader、blend/depth state を登録する拡張口を示す。移植の入口はあるが、lilToon 互換の実装が備わっている証拠ではない。[Helix 3.1.2 README](https://github.com/helix-toolkit/helix-toolkit/blob/v3.1.2/README.md)、[CustomEffectsManager.cs](https://github.com/helix-toolkit/helix-toolkit/blob/v3.1.2/Source/Examples/Wpf.SharpDX/CustomShaderDemo/CustomEffectsManager.cs)

## three-liltoon の適合性

調査時の `package.json` は 0.1.4、Three.js の peer 範囲は `>=0.180.0 <0.190.0`。README の変換元は lilToon `72fc09625b24c9a750591c286e9192512a5177a1`。導入するときは最新版追従ではなく、確認済み runtime と依存版を固定する。[package.json](https://github.com/mochiya-labs/three-liltoon/blob/main/package.json)、[作者 README](https://github.com/mochiya-labs/three-liltoon/blob/main/README.md)

自動 GLB/VRM loader は `MOCHIYA_materials_liltoon` extension を読む。普通の GLB や FBX、Unity の YAML `.mat` をそのまま lilToon 材質として復元する機能ではない。一方、独立した `LilToonMaterialLoader` / factory はプロパティ辞書と caller が解決した texture を受けられるため、アプリが `.mat` を変換する道はある。[材質形式](https://github.com/mochiya-labs/three-liltoon/blob/main/docs/MATERIAL_FORMAT.md)、[API](https://github.com/mochiya-labs/three-liltoon/blob/main/docs/API.md)

最小試作は、現行 Assimp が読んだ頂点・index・法線・UV・submesh と Prefab transform を単一ローカル scene payload にし、対応が判明した `.mat` のみ元のプロパティ名で渡す方式。Unity に一度 import/export させる経路は、ユーザーの目的に合わない。shader GUID から元 shader variant を解決できない場合は lilToon と断定せず、現在の簡易表示と警告へ戻す。この統合方式は調査者の設計案で、既存の完成済み機能ではない。

runtime の配布には移植側 MIT と本家通知を含める。ビルド済み JavaScript / GLSL は提供され、利用者に HLSL コンパイラーや Unity を要求しない。[移植側 Third Party Notices](https://github.com/mochiya-labs/three-liltoon/blob/main/THIRD_PARTY_NOTICES.md)、[Architecture](https://github.com/mochiya-labs/three-liltoon/blob/main/docs/ARCHITECTURE.md)

## 負荷・再現性

- WebView2 は browser・renderer・GPU 等の複数プロセスを持つ。現行単独 renderer に対してプロセスが増える構造であり、常駐メモリと起動の追加費用は実測が必要。[Microsoft の process model](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/process-model)
- 移植の outline は追加描画、屈折は scene capture、blur は追加 pass を使う。fur は ribbon と追加資源を使い、一部機器では CPU skinning fallback がある。メッシュ・設定・GPU によって費用が変わる。[Rendering modes](https://github.com/mochiya-labs/three-liltoon/blob/main/docs/RENDERING_MODES.md)
- 材質・texture 機能の変更は shader specialization を起こす。材質の初回生成・GPU compile と定常描画を別に測る必要がある。単純な数値変更は uniform 更新で済む。[Architecture](https://github.com/mochiya-labs/three-liltoon/blob/main/docs/ARCHITECTURE.md)
- 作者の機能表は Unity との承認済み画像比較基準がまだないと明記する。toon 影・MatCap・emission 等に実装はあるが、全 Unity lighting や全 shader family の互換を約束しない。[Feature matrix](https://github.com/mochiya-labs/three-liltoon/blob/main/docs/FEATURE_MATRIX.md)

### 導入判断用の測定案

同じ PC、同じ ZIP・Prefab、同じ表示解像度とカメラで現行表示と比較する。冷起動／再表示を分け、各条件を複数回測る。購入素材のデータや撮影画像は公開しない。

1. 起動から操作可能になる時間、選択から初回モデル表示までの時間。
2. WPF 本体と全 WebView2 プロセスを含む working set、GPU 専用／共有メモリ。
3. 静止時・Unity 風の見渡し／移動時の CPU、GPU、frame time。空／非表示 viewport の idle 負荷。
4. 連続選択・cancel 後の旧結果反映、資源の残留、切替時の UI 停止。
5. main texture、toon 影、normal、MatCap、emission、cutout と transparent を自作 fixture で個別確認。

合格基準は製品側で決める。例えば UI の単一処理 50 ms 超を避け、既定表示を遅くしないよう必要時にだけ任意ビューを起動する。fur・屈折・高度な効果を省くときは、近似表示／省略内容を明示する。これらは推奨する検証条件であり、達成済みの性能値ではない。

## 選択した Helix 近似対応の仕様根拠

### 材質モードの識別

実装では `m_Shader` GUID を公式 lilToon 1.3.7 タグの `.shader.meta` と照合し、基本 toon (`lts` / `ltsl`) の opaque、cutout、transparent と各 outline variant の計12 GUIDだけを認識する。shader名や `_Cutoff` の有無から variantを推測しない。未知の lilToon variant は render mode を opaque 近似に留め、結果に警告する。GUID対応は[1.3.7 タグの shader meta](https://github.com/lilxyzw/lilToon/tree/36ff295b41cc4c398b95419d513adeecb0b322d4/Assets/lilToon/Shader)および現行タグを根拠にする。

outline variant と `_UseOutline`、MatCap、AlphaMask mode などの有効な機能設定は、描画しないことを警告する。opaqueでは `_Color.a` を無視し、transparent の明示モードでだけ色 alpha を使う。cutoutは元texture alphaと `_Color.a` を cutoff と比較してPNGのalphaを二値化する近似で、Helixの透明描画順・深度処理がUnityのdiscardと同じになるとは扱わない。

| 読む情報 | 本家での意味 | 今回の実装範囲・注意点 |
| --- | --- | --- |
| `m_Colors` の `_Color`、`m_TexEnvs` の `_MainTex` | main texture に材質色を乗算する。alpha は cutout / transparent のとき使う | 一致を確認できた材質に基本色を適用。色だけの材質も表示する。opaque の材質 alpha を無条件に透過へ変えない |
| texture scale / offset | 主 texture の UV 変換 | Helix `UVTransform` で再利用できる範囲だけ適用し、複数レイヤー等は省略を明示 |
| `_Cutoff`、shader variant / render state | cutout と半透明は異なる描画方法 | variant を解決できる場合に区別する。標準 Phong の API に任意 cutoff があるとは仮定せず、実装可能な方法で近似するか警告する |
| `_AlphaMaskMode`、`_AlphaMask` 等 | 別 texture による alpha 処理 | 今回対象外なら有効な設定のみ未対応と表示。main alpha への誤置換を避ける |
| `_Cull`、blend / queue | 表示する面・合成・描画順 | renderer 側に対応する部分だけ反映。高度な blend / queue の一致は約束しない |

main texture と色の乗算、および alpha を使う描画モードの説明は本家ドキュメントにある。プロパティ名と初期値は `lts.shader`、cutoff の discard は `lil_common_frag_alpha.hlsl` で確認できる。[メインカラー / 透過](https://lilxyzw.github.io/lilToon/ja_JP/color/maincolor.html)、[lts.shader](https://github.com/lilxyzw/lilToon/blob/master/Assets/lilToon/Shader/lts.shader)、[alpha 処理ソース](https://github.com/lilxyzw/lilToon/blob/master/Assets/lilToon/Shader/Includes/lil_common_frag_alpha.hlsl)

本家 alpha mask は別 sampling を増やす機能。描画面・depth・blend・render queue はそれぞれ異なる状態で、材質色 alpha だけから全部を復元できるものではない。[アルファマスク](https://lilxyzw.github.io/lilToon/ja_JP/color/alphamask.html)、[レンダリング設定](https://lilxyzw.github.io/lilToon/ja_JP/advanced/rendering.html)

Helix 3.1.2 の `PhongMaterialCore` は `DiffuseColor`、`DiffuseMap`、`DiffuseAlphaMap`、`UVTransform` を持つ。`MeshNode` は `CullMode` を持つ。これらは元の renderer の拡張範囲であり、本家 toon 影・MatCap・outline の再現にはならない。[PhongMaterialCore.cs](https://github.com/helix-toolkit/helix-toolkit/blob/v3.1.2/Source/HelixToolkit.SharpDX/Model/Material/PhongMaterialCore.cs)、[MeshNode.cs](https://github.com/helix-toolkit/helix-toolkit/blob/v3.1.2/Source/HelixToolkit.SharpDX/Model/Scene/MeshNode.cs)

実装・検証は自動選択と材質対応の修正に続けて行う。自作の色だけ／色×texture／opaque alpha／cutout／半透明 fixture を分け、旧 preview との比較、cancel、連続選択、一時出力所有権、未対応警告を確認する。測定後の数値と実画面は実装側の検証結果に記録し、本調査の未測定値としては補わない。

## Shader GUID だけが記録された材質の判定

`.mat` に shader 名がなく、`m_Shader` の GUID だけが記録される場合に使える最小の既知表を調べた。本家固定コミット `72fc09625b24c9a750591c286e9192512a5177a1` の `.shader` 名と `.shader.meta` を直接読んだ。下記 12 種の GUID は公式 `1.3.7` タグ（`36ff295b41cc4c398b95419d513adeecb0b322d4`）でも一致した。これは 2 点の確認であり、全バージョンで永久に固定される保証ではない。

| 近似 mode | ファイル | shader 名 | GUID / 固定 meta 出典 |
| --- | --- | --- | --- |
| Opaque | `lts.shader` | `lilToon` | [df12117ecd77c31469c224178886498e](https://github.com/lilxyzw/lilToon/blob/72fc09625b24c9a750591c286e9192512a5177a1/Assets/lilToon/Shader/lts.shader.meta) |
| Opaque | `lts_o.shader` | `Hidden/lilToonOutline` | [efa77a80ca0344749b4f19fdd5891cbe](https://github.com/lilxyzw/lilToon/blob/72fc09625b24c9a750591c286e9192512a5177a1/Assets/lilToon/Shader/lts_o.shader.meta) |
| Cutout | `lts_cutout.shader` | `Hidden/lilToonCutout` | [85d6126cae43b6847aff4b13f4adb8ec](https://github.com/lilxyzw/lilToon/blob/72fc09625b24c9a750591c286e9192512a5177a1/Assets/lilToon/Shader/lts_cutout.shader.meta) |
| Cutout | `lts_cutout_o.shader` | `Hidden/lilToonCutoutOutline` | [3b4aa19949601f046a20ca8bdaee929f](https://github.com/lilxyzw/lilToon/blob/72fc09625b24c9a750591c286e9192512a5177a1/Assets/lilToon/Shader/lts_cutout_o.shader.meta) |
| Transparent | `lts_trans.shader` | `Hidden/lilToonTransparent` | [165365ab7100a044ca85fc8c33548a62](https://github.com/lilxyzw/lilToon/blob/72fc09625b24c9a750591c286e9192512a5177a1/Assets/lilToon/Shader/lts_trans.shader.meta) |
| Transparent | `lts_trans_o.shader` | `Hidden/lilToonTransparentOutline` | [3c79b10c7e0b2784aaa4c2f8dd17d55e](https://github.com/lilxyzw/lilToon/blob/72fc09625b24c9a750591c286e9192512a5177a1/Assets/lilToon/Shader/lts_trans_o.shader.meta) |
| Opaque | `ltsl.shader` | `Hidden/lilToonLite` | [381af8ba8e1740a41b9768ccfb0416c2](https://github.com/lilxyzw/lilToon/blob/72fc09625b24c9a750591c286e9192512a5177a1/Assets/lilToon/Shader/ltsl.shader.meta) |
| Opaque | `ltsl_o.shader` | `Hidden/lilToonLiteOutline` | [583a88005abb81a4ebbce757b4851a0d](https://github.com/lilxyzw/lilToon/blob/72fc09625b24c9a750591c286e9192512a5177a1/Assets/lilToon/Shader/ltsl_o.shader.meta) |
| Cutout | `ltsl_cutout.shader` | `Hidden/lilToonLiteCutout` | [b957dce3d03ff5445ac989f8de643c7f](https://github.com/lilxyzw/lilToon/blob/72fc09625b24c9a750591c286e9192512a5177a1/Assets/lilToon/Shader/ltsl_cutout.shader.meta) |
| Cutout | `ltsl_cutout_o.shader` | `Hidden/lilToonLiteCutoutOutline` | [8cf5267d397b04846856f6d3d9561da0](https://github.com/lilxyzw/lilToon/blob/72fc09625b24c9a750591c286e9192512a5177a1/Assets/lilToon/Shader/ltsl_cutout_o.shader.meta) |
| Transparent | `ltsl_trans.shader` | `Hidden/lilToonLiteTransparent` | [0e3ece1bd59542743bccadb21f68318e](https://github.com/lilxyzw/lilToon/blob/72fc09625b24c9a750591c286e9192512a5177a1/Assets/lilToon/Shader/ltsl_trans.shader.meta) |
| Transparent | `ltsl_trans_o.shader` | `Hidden/lilToonLiteTransparentOutline` | [1c12a37046f07ac4486881deaf0187ea](https://github.com/lilxyzw/lilToon/blob/72fc09625b24c9a750591c286e9192512a5177a1/Assets/lilToon/Shader/ltsl_trans_o.shader.meta) |

対応する shader 名の出典は各行の `.shader.meta` から `.meta` を除いた `.shader` ファイル。例として [CutoutOutline 本体](https://github.com/lilxyzw/lilToon/blob/72fc09625b24c9a750591c286e9192512a5177a1/Assets/lilToon/Shader/lts_cutout_o.shader)、過去版確認の例は [1.3.7 CutoutOutline meta](https://github.com/lilxyzw/lilToon/blob/36ff295b41cc4c398b95419d513adeecb0b322d4/Assets/lilToon/Shader/lts_cutout_o.shader.meta)。Outline の GUID を通常 surface mode として扱っても、輪郭線を再現したことにはならない。省略内容を表示する。

不透明版 `lts.shader` 自体にも `_Cutoff` の初期値 `0.5` が定義されるので、`_Cutoff` が記録されることや正の値であることを cutout 判定に使わない。同様に `_Color.a < 1` だけで透明 variant と判定しない。[固定コミットの不透明 shader](https://github.com/lilxyzw/lilToon/blob/72fc09625b24c9a750591c286e9192512a5177a1/Assets/lilToon/Shader/lts.shader)

最小表には Multi、Tessellation、Fur、Refraction、Gem、Overlay、OnePass/TwoPass、OutlineOnly を含めない。これらは本家で別の shader family / pass を持つため、名前の部分一致だけで上記の通常表へ吸収しない。[本家 shader 構成](https://lilxyzw.github.io/lilToon/ja_JP/dev/files.html)

推奨する判定順は、参照先 shader が読み取れるときは実際の variant を確認し、それがない場合に既知 GUID を参照する方式。未知 GUID で明確な mode 情報もない場合は、不透明の近似を既定にして mode を特定できなかった警告を表示する。GUID の認識自体は本家コードの実行や shader 互換性の保証ではない。
