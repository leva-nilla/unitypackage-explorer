# Windows エクスプローラーのコンテキストメニュー登録調査

調査日: 2026-09-28  
範囲: Windows Explorer のファイル右クリックメニュー。Microsoft Learn の一次資料を基準に、実機での観察と仕様上の推測を分ける。

実機環境: HKLM上のWindows表示名は `Windows 10 Pro`、OS build は `26200.9457`。Microsoftのリリース情報では build `26200.9457` は Windows 11 version 25H2 の build として掲載されている。表示名とbuild系列が一致しないため、ここでは両方を併記する。

## 結論

- `HKCU\Software\Classes` は `HKCR` にマージされる per-user 登録先で、通常ユーザー向け静的verbの登録に使える。ただし、キーを置けばどの対象にも出るわけではない。Shell は対象項目の association array を引き、そこに含まれる ProgID、拡張子別 `SystemFileAssociations`、汎用クラスなどからverbを集める。
- `.zip` に限るなら、実際の拡張子関連付けが使う ProgID を確認してから、その ProgID 配下へ登録する方法が最も直接的。ProgID が変わっても拡張子に対する補助verbを残したい用途では `SystemFileAssociations\.zip\shell` が候補になる。現在のアプリ既定値を置き換えないこと。
- `Folder` は Shell コンテナー、`Directory` はファイルシステム上のフォルダー、`*` は非フォルダーの全ファイル、`AllFilesystemObjects` はファイルとファイルシステムフォルダーを対象にする。`.zip` が `CompressedFolder` ProgID として扱われる場合、そのverbは圧縮フォルダー側のProgIDに紐づく。これらの登録先は同じ対象範囲ではない。
- Windows 11 の新しい右クリックメニューへ追加する Microsoft の現行経路は、パッケージIDを持つアプリがネイティブ COM DLL の `IExplorerCommand` を実装し、manifest の `windows.fileExplorerContextMenus` で対象を登録する方法。未パッケージのWin32アプリは sparse package でパッケージIDを付けられる。従来のレジストリverbや `IContextMenu` はlegacyコンテキストメニュー側の拡張として扱う。今回の実機で開いたメニューが新旧どちらの経路かは特定できていない。
- `Send To` は Explorer 標準メニュー内の別経路。ユーザー別の既知フォルダー `FOLDERID_SendTo` は `%APPDATA%\Microsoft\Windows\SendTo`。ここにアプリへのショートカットを置く方式は簡単なフォールバックだが、右クリック直下の独立項目にはならない。

## 登録場所ごとの適用範囲

| 登録先 | 公式資料で示される対象・役割 | このアプリでの見立て |
| --- | --- | --- |
| `HKCR\<ProgID>\shell\<verb>\command` | ファイル種別に対応するProgIDの静的verb。ProgIDは関連付けを制御する。 | `.zip` の現在のProgID（この環境では `CompressedFolder` と観測）に結び付けるなら候補。関連付けの変更を受ける。 |
| `HKCR\SystemFileAssociations\.zip\shell\<verb>\command` | 拡張子固有の共有・補助verb。ProgIDより低優先だが、ユーザーが既定アプリを変更しても関連付けが安定するよう使える。 | `.zip` 専用の補助コマンド候補。対象Shell項目がこのassociation arrayを使う必要がある。 |
| `HKCR\*\shell\...` | すべてのファイル（フォルダーを除く）。 | 3形式だけに絞るには広すぎる。 |
| `HKCR\AllFilesystemObjects\shell\...` | ファイルとファイルシステムフォルダー。 | 全ファイル・フォルダーに広がる。 |
| `HKCR\Directory\shell\...` | ファイルシステムフォルダー。 | 選択したフォルダー向け。ZIP等のファイルには該当しない。 |
| `HKCR\Folder\shell\...` | Shellコンテナー。 | `Directory` と同義ではなく、ZIPの仮想フォルダーなどShellコンテナー向け。普通のファイルverb登録先ではない。 |
| `FOLDERID_SendTo` 内の `.lnk` | Explorerの「送る」サブメニューの宛先。ユーザー別既定パスは `%APPDATA%\Microsoft\Windows\SendTo`。 | 低コストの代替導線。実際の項目選択は別途UI確認が必要。 |

Shellのassociation arrayはShell項目の種類ごとに異なる。Microsoftの例ではファイルの配列はProgID → `SystemFileAssociations` → perceived type → `*` → `AllFilesystemObjects` の順だが、すべての対象に同じ並びを仮定しないこと。ZIPで `CompressedFolder` が使われることも、Windows共通の固定値とみなさず、そのPCの関連付けを確認する。

静的verbは `shell` の下にverbキー、その下に `command` を置き、表示名とコマンドラインを登録する。`extended` が付けば Shift+右クリック時のみ、`ProgrammaticAccessOnly` が付けばメニューには表示されない。実行ファイルや引数に空白が含まれ得るため、コマンド文字列は引用符で囲む。

## Windows 11 の新旧メニューと拡張方式

- `IExplorerCommand` を manifest の `windows.fileExplorerContextMenus` で登録したコマンドは Windows 11 の新メニューに表示される。`*` はファイル、`Directory` は選択フォルダー、`Directory\Background` はフォルダー背景。
- Microsoftの手順はネイティブCOM DLL、`windows.comServer`、`windows.fileExplorerContextMenus` manifest登録を使う。未パッケージのWin32アプリでも sparse package により同じ登録方式を利用できる。したがって、現在の通常のWPF配布物にレジストリverbを足すだけで新メニューに出る、とは公式仕様から保証できない。
- 旧来の `IContextMenu` はShell拡張ハンドラーとして動的に項目を作る仕組み。Microsoftは最も強力だが複雑で、呼び出し元プロセス（通常はExplorer）上で実行されると説明し、可能なら静的verbを優先するよう勧める。少なくともメニュー構築処理は高速に保ち、重い処理は選択後に行う。
- Microsoftの新メニュー登録手順では、インストール後に項目が出ない場合はExplorer再起動またはサインアウト/サインインで拡張登録を読み直すよう案内している。PC全体の再起動が常に必要という説明ではない。

## この環境での確認事実と未確定点

**実機で確認済み（この作業環境のみ）**

- 実験中に複数の登録場所へ一時登録した静的verbは `Shell.Application` の `Verbs()` では列挙されたが、ユーザーが操作するExplorerの右クリック画面には出なかった。これを「現行直接メニューverbが列挙された」とは確認していない。
- Explorerを再起動して新しいウィンドウで見ても表示されなかった。右クリックUIが新メニューかlegacyメニューかを含め、今回の観測だけではShellがどのメニュー経路を表示したか特定できていない。不表示の理由も未確定。
- HKLM上のWindows表示名は `Windows 10 Pro`、OS build は `26200.9457`。Microsoftの公式リリース情報上、build `26200.9457` は Windows 11 version 25H2 の更新buildに該当する。
- `.zip` の `UserChoice` ProgIDとして `CompressedFolder` が使われていることを確認した。
- インストーラーがユーザーの `SendTo` フォルダーへショートカットを登録し、リンク先が現在のアプリを指すことは確認した。実際に「送る」UIから選択して起動する試験は未実施。

**仕様からの推測（実機確認ではない）**

- 複数の場所へ登録した静的verbが `Verbs()` には列挙されても実UIに出なかった。登録先とShell項目のassociation array、メニューの世代・UI経路、Windows側の表示条件のどれが原因かは未確定。
- Windows 11の新メニューに右クリック直下のアプリ項目が必要なら、Microsoftの現行経路である sparse package + `IExplorerCommand` を実装して検証するのが本筋。従来の静的verbはlegacyメニュー側の登録方式として扱い、UI経路を特定してから検証する。現在のOS buildだけでは今回開かれたメニュー世代までは判定できない。
- 「送る」は標準のサブメニュー経由で機能する見込みだが、今回のユーザー環境での実動作は未確認。

## 参照（Microsoft公式）

- [Association Arrays](https://learn.microsoft.com/en-us/windows/win32/shell/fa-associationarray) — association arrayの仕組み、ProgID / `SystemFileAssociations` / `*` / `AllFilesystemObjects` / `Directory` / `Folder` の適用対象。
- [Merged View of HKEY_CLASSES_ROOT](https://learn.microsoft.com/en-us/windows/win32/sysinfo/merged-view-of-hkey-classes-root) — `HKCR` と `HKCU\Software\Classes` のマージ規則。
- [Creating Shortcut Menu Handlers](https://learn.microsoft.com/en-us/windows/win32/shell/context-menu-handlers) — 静的verb、Shift専用verb、非表示verb、`IContextMenu` とExplorerプロセス上の注意。
- [Add a File Explorer context menu command to a packaged desktop app](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/integrate-packaged-app-with-file-explorer) — Windows 11の新メニュー向け `IExplorerCommand`、manifest登録、sparse package、Explorer再起動手順。
- [KNOWNFOLDERID: FOLDERID_SendTo](https://learn.microsoft.com/en-us/windows/win32/shell/knownfolderid) — ユーザー別SendTo既定パス。
- [IContextMenu::QueryContextMenu](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-icontextmenu-querycontextmenu) — Send Toメニュー向けの呼び出しフラグ `CMF_NOVERBS`。
- [Windows 11 release information](https://learn.microsoft.com/en-us/windows/release-health/windows11-release-information) — Windows 11 version 25H2 の OS build 系列 `26200` と、現行リリース build `26200.9457`。

## sparse package のunsigned登録実験（2026-09-28）

Microsoft Learn の [unsigned MSIX guidance](https://learn.microsoft.com/en-us/windows/msix/package/unsigned-package) を参照し、Windows 11 build 26200.9457 でmanifestとネイティブCOM DLLだけを含む sparse package を MakeAppx で生成した。WPF実行ファイルは外部配置を参照するmanifestである。

`Add-AppxPackage -Path <package> -ExternalLocation <app-output> -AllowUnsigned -ForceUpdateFromAnyVersion` は HRESULT `0x80073D2B` で拒否された。エラーはunsigned packageが実行可能ファイルのactivationを含むため登録できない旨。失敗後にCurrentUserの対象package登録が残っていないことも確認した。証明書ストア、Developer Mode、管理者設定には触れていない。

この時点では署名済みsparse packageでの新コンテキストメニュー表示・COM起動は未検証だった。その後の証明書ストア再試行と署名済みpackage登録結果は下記に記録する。

## 開発用署名証明書の形式

開発用証明書の生成はMicrosoft Learnの [Create a certificate for package signing](https://learn.microsoft.com/en-us/windows/msix/package/create-certificate-package-signing) にある `New-SelfSignedCertificate -Type Custom` の例に合わせる。Code Signing EKU (`1.3.6.1.5.5.7.3.3`) に加え、`2.5.29.19={text}` でBasic Constraintsを明示し、生成後・署名前にEKUと `CA:false` を確認する。秘密鍵を含む署名証明書は `Cert:\CurrentUser\My` に作成し、公開CERをMSIX署名信頼のためにどのストアへ追加するかは、以下の登録実測に従う。

## 署名済みsparse packageのCurrentUser登録と信頼ストア実測（2026-09-28）

Windows 11 build 26200.9457で、Code Signing EKUと明示的なCA:false Basic Constraintsを持つ署名済みidentity/sparse packageを `Add-AppxPackage -ExternalLocation` でCurrentUser登録しようとした。公開証明書を `Cert:\CurrentUser\TrustedPeople` へインポートした状態でも、登録は `0x800B0109`（CERT_E_UNTRUSTEDROOT）で失敗した。処理後にこの試行で追加したCurrentUser TrustedPeople証明書はrollbackされ、対象packageの登録がないことを確認した。LocalMachineストアへの変更・昇格・再試行は行っていない。

Microsoft Learnの資料には説明差がある。 [sparse packageによるpackage identity付与手順](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps) は、per-user registration手順の信頼追加例としてCurrentUser TrustedPeopleを示し、machine-wideの場合はLocalMachineを案内する。一方、[MSIX troubleshooting guideの0x800B0109項](https://learn.microsoft.com/en-us/windows/msix/msix-troubleshooting-guide) は、App InstallerはCurrentUserではなくLocalMachine Trusted Peopleのみを確認すると明記し、管理者権限でのImport-Certificateを案内する。このPCのAdd-AppxPackage実測は後者と一致したため、アプリのinstallerは `Cert:\LocalMachine\TrustedPeople` に正確な署名thumbprintが存在することを事前確認する。存在しない場合は管理者用コマンドを表示して停止し、自動昇格・ストア変更はしない。

LocalMachine TrustedPeopleへの追加は同じPCの全ユーザーに影響し、管理者権限を必要とする。Root storeへ追加しない。アプリのuninstallerもmachine trust certificateを削除しない。他ユーザーや別アプリの利用がないことを確認した上で、明示的に削除する管理者のみ、次のコマンドを実行する。

```powershell
Import-Certificate -FilePath '<公開CERの絶対パス>' -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Remove-Item -LiteralPath 'Cert:\LocalMachine\TrustedPeople\<thumbprint>' -Force
```

## 署名済みCurrentUser登録の証明書ストア再試行（2026-09-28）

前回の `0x800B0109` を受け、Code Signing EKUと明示的なCA:false Basic Constraintsを持つ新しい開発証明書を使い、公開CERを `Cert:\CurrentUser\TrustedPeople` に追加して再試行した。`Add-AppxPackage` は同じ `0x800B0109` で失敗し、失敗時のrollbackで今回追加したCurrentUser TrustedPeople entryは削除された。対象AppX packageの登録は0件。LocalMachineストアへの追加はこの段階では未実施。

この実機結果は、Microsoft Learnの [MSIX troubleshooting guide](https://learn.microsoft.com/en-us/windows/msix/msix-troubleshooting-guide) の説明（App InstallerはCurrentUserではなくLocalMachine Trusted Peopleを確認、追加は管理者権限）と一致する。一方、Microsoftの [sparse package identity guide](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps) はper-user手順にCurrentUser TrustedPeople例を示す。資料間の差と現場のAdd-AppxPackage結果を分けて記録し、このアプリではLocalMachine TrustedPeopleに署名証明書thumbprintがある場合のみ登録に進める。欠落時には正確な `Import-Certificate -FilePath '<公開CER>' -CertStoreLocation Cert:\LocalMachine\TrustedPeople` を提示して停止する。インストーラーは昇格も信頼追加もせず、アンインストーラーも全ユーザーに影響するmachine certificateを削除しない。

## LocalMachine trust後の署名済み登録とCOM activation確認

後続の実機確認では、管理者として公開CERを `Cert:\LocalMachine\TrustedPeople` にインポートした後、通常ユーザーとして `Install-ExplorerContextMenuPackage.ps1` を実行し、CurrentUserへの登録が成功した。`Get-AppxPackage` に `UnityPackageExplorer.SparseContextMenu_1.0.0.0_neutral__k490yjxqyg3te` が現れ、CLSID `1C490A47-4D85-4AA0-A2A3-4A7827B5DA79` のCOM class activationも成功した。Manifest上の対象は `.zip`、`.unitypackage`、`.prefab`。

証明書信頼、CurrentUser package登録、COM起動までは実機確認済み。Explorerの実際の右クリック画面で3拡張子向けメニュー項目が見えるか、選択後にアプリが起動するかは未確認。LocalMachine信頼は同じPCの他ユーザーにも及ぶため、アプリ解除時も自動削除しない。

その後、COM probeで`GetTitle`を呼び出したところ、日本語のwide literalがMSVC側で文字化けしていた。タイトルをUniversal Character Name（`\u3067\u958B\u304F`）で表記し、manifest versionを`1.0.0.1`へ上げて署名packageを更新した。同一versionの異なるpayloadは`0x80073CFB`で拒否されたため、version bump後に`UnityPackageExplorer.SparseContextMenu_1.0.0.1_neutral__k490yjxqyg3te`のCurrentUser更新が成功。再度のCOM probeでHRESULT `S_OK`、タイトル「UnityPackage Explorer で開く」を確認した。machine trust証明書は変更していない。Explorer右クリック画面での表示・Invoke起動は引き続き未確認。
