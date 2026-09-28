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
