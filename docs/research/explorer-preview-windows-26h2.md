# Windows 11 26H2 での Explorer Preview Handler 調査

調査日: 2026-11-12。対象は Windows 11 26H2 / build 26300.9711 の UnityPackage Explorer 登録調査。一次資料（Microsoft Learn、Microsoft Support）の読み取り調査のみ。レジストリ変更、Explorer実機試験、ファイルのブロック解除はしていない。

## 結論

- Microsoft の 26H2 機能・既知問題資料を確認した範囲では、COM Preview Handler の登録モデルや Explorer の Preview Handler 起動条件を変える 26H2 固有の説明は見つからなかった。26H2 の公式機能ページは Explorer のナビゲーションや検索結果プレビュー等を列挙するが、COM Preview Handler を変更したとは記していない。[26H2 の新機能](https://learn.microsoft.com/en-us/windows/whats-new/whats-new-windows-11-version-26H2)、[26H2 の既知問題](https://learn.microsoft.com/en-us/windows/release-health/status-windows-11-26h2)
- build 26300.9711 は Microsoft のリリース履歴で 26H2 の 2026-10 B 更新として載っている。26H2 は 24H2 / 25H2 と同じ servicing branch を共有し、enablement package で機能を有効化するリリースと説明されている。[Windows 11 リリース情報](https://learn.microsoft.com/en-us/windows/release-health/windows11-release-information)、[26H2 の servicing 説明](https://learn.microsoft.com/en-us/windows/whats-new/whats-new-windows-11-version-26H2)
- よって、現時点の一次資料からは「26H2 のために登録形式が変わった」とは言えない。既存の file association / CLSID 登録、低整合性の Preview Host、ファイルの Mark of the Web (MotW) を分けて確認するのが筋。26H2 の非公開実装が従来と完全に同一だと証明したわけではない。

## 登録経路と確認点

Microsoft の標準例では、拡張子を ProgID に結び、ProgID の `shellex\\{8895b1c6-b41f-4c1c-a562-0d564250836f}` に Preview Handler CLSID を登録する。CLSID の COM server / AppID 登録は別段階で、Preview Handler 一覧は表示向け列挙の最適化として説明されている。[Preview Handler の登録手順](https://learn.microsoft.com/en-us/windows/win32/shell/how-to-register-a-preview-handler)

したがって `.zip` と `.unitypackage` の両方について、実際に有効な拡張子→ProgID→`shellex` の連鎖をそれぞれ確認する必要がある。拡張子から ProgID への対応はユーザーの file association で変わり得る。Shell は file type の association array から handler 情報を探し、登録済み ProgID と `SystemFileAssociations\\.<extension>` も参照対象にするため、値を一箇所だけ見て有効な handler と決めつけない。[File Types](https://learn.microsoft.com/en-us/windows/win32/shell/fa-file-types)、[Association Arrays](https://learn.microsoft.com/en-us/windows/win32/shell/fa-associationarray)、[How File Associations Work](https://learn.microsoft.com/en-us/windows/win32/shell/fa-how-work)

`PreviewHandlers` の CLSID エントリがあることは有用な確認点だが、Microsoft の説明ではこれは列挙・表示の最適化である。拡張子 / ProgID から Preview Handler CLSID へ結ぶ登録、およびその CLSID の COM 起動登録の代用にはならない。[登録手順](https://learn.microsoft.com/en-us/windows/win32/shell/how-to-register-a-preview-handler)

## HKCR、HKCU、COM activation の解釈

`HKEY_CLASSES_ROOT` (HKCR) は `HKCU\\Software\\Classes` と `HKLM\\Software\\Classes` の merged view であり、ユーザー設定は既定のマシン設定より優先される。COM の class lookup は `HKCU\\Software\\Classes` を HKCR より先に参照する。[HKCR merged view](https://learn.microsoft.com/en-us/windows/win32/sysinfo/merged-view-of-hkey-classes-root)、[Classes and Servers](https://learn.microsoft.com/en-us/windows/win32/com/classes-and-servers)

ただし、同じ CLSID の per-user key があるだけで、HKLM 側の別の値や子キーが必ず全て消えるとは結論しない。Microsoft の merged-view 例では重複する `CLSID\\4` の下に HKCU 側 `LocalServer` と HKLM 側 `InprocServer32` / `LocalServer32` が同じ merged key で現れる。今回の親エージェントによる通常ユーザーの実測でも、HKCR から HKCU の Default 値と HKLM の `AppID` / `InprocServer32` を読め、ネイティブ `CoCreateInstance` は両コンテキストで成功したとのこと。ここから言えるのは、通常ユーザーの直接 activation は成功したことまでで、Explorer の Preview Host 経路も成功することではない。実測は調査者が独立再実行していない。

## Preview Host とインターフェースの条件

Preview Handler は Explorer にホストされる COM オブジェクトで、Microsoft は通常 `prevhost.exe` を使う out-of-process surrogate 方式を推奨する。Preview Handler は既定で低整合性 (low IL) で実行される。managed handler は専用 AppID と `DllSurrogate=Prevhost.exe` を用い、共有 Preview Host とは別プロセスにする手順が示されている。[Preview Handlers and Shell Preview Host](https://learn.microsoft.com/en-us/windows/win32/shell/preview-handlers)

Explorer 等の通常ユーザーから直接行う COM activation と、Shell が作成する低 IL Preview Host 内での activation は実行条件が違う。従って直接 `CoCreateInstance` が通っても、ProgID の解決、surrogate 起動、低 IL プロセスでの activation、IPC、ファイル読み取り、window parenting まで確認したことにはならない。これは公式のホスト構成と親エージェントの実測範囲からの推論であり、この環境での失敗原因を特定したものではない。

インターフェース呼び出し順には明示的条件がある。`Initialize`（`IInitializeWithStream` 推奨）と `IPreviewHandler::SetWindow` の後で `DoPreview` を呼ぶ。`SetWindow` 前に preview window を作らず、`DoPreview` 内で作成し、指定 parent の子として配置する。`IOleWindow::GetWindow` は preview window が存在すれば HWND と `S_OK`、なければ `E_FAIL` を返す実装説明で、`GetWindow` を `DoPreview` より先に呼ばなければならないという要件は記されていない。[Building Preview Handlers](https://learn.microsoft.com/en-us/windows/win32/shell/building-preview-handlers)、[SetWindow](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ipreviewhandler-setwindow)、[DoPreview](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ipreviewhandler-dopreview)

## MotW によるプレビュー拒否

別系統の原因として、Microsoft は 2025-10-14 以後のセキュリティ更新で、インターネットから取得して MotW が付いたファイルの Explorer preview を既定で無効化したと説明している。対象は preview を悪用した NTLM hash 流出を防ぐためで、Internet Zone の file share も含む。Microsoft の案内する個別の Unblock 操作はそのファイルの internet security block を外す操作であり、セキュリティ状態を変える。今回の調査では解除していない。[Microsoft Support KB 5070960](https://support.microsoft.com/en-us/servicing/os/windows/docs/2025/10/file-explorer-automatically-disables-the-preview-feature-for-files-downloaded-from-the-internet)

この拒否時に Microsoft が示す文面は「ファイルをプレビューするとコンピューターに損害を与える可能性がある」という警告。ユーザー報告の「プレビューを利用できません」と同一表示だとは確認できていないので、`.zip` / `.unitypackage` の Zone.Identifier を読み取り確認するまでは MotW 原因と断定できない。MotW を消す、全体ポリシーで抑止する、信頼ゾーンを広げるような回避は今回の調査対象外。

## 次に切り分ける項目

1. `.zip` と `.unitypackage` について、HKCR merged view 上の拡張子の既定 ProgID と、実際の association array に含まれる ProgID / `SystemFileAssociations` を読み取り確認する。
2. 有効 ProgID ごとに `{8895b1c6-b41f-4c1c-a562-0d564250836f}` が対象 CLSID を指すか、CLSIDs の COM server / AppID / surrogate 登録が有効かを確認する。
3. 対象ファイルに Zone.Identifier があるかを読み取り確認し、Explorer の表示文面が KB 5070960 の MotW 警告かを区別する。
4. 上記が揃った後、Explorer が作る Preview Host の起動、実際の `Initialize` / `SetWindow` / `DoPreview` / `Unload` の呼び出しと HRESULT を観測する。親エージェントは現在、MapUrlToZone と低 IL 起動経路の再現を調査中。

本レポートの一次資料調査では Explorer 実動作、レジストリ状態、対象ファイルの MotW、低 IL 実行時の activation は未確認。
