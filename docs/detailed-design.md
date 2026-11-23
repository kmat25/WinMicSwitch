# WinMicSwitch 詳細設計書

操作仕様と受入基準は [基本設計書](basic-design.md)、Release ビルド・テスト・配布のコマンドは [README](../README.md) を参照する。

## 実行基盤と構成

ソリューションはアプリとテスト実行ファイルの 2 プロジェクトで構成する。アプリは C#／WinForms の WinExe、ターゲットは net10.0-windows。DPI モードは PerMonitorV2、マニフェストの実行レベルは asInvoker。配布は win-x64 の自己完結型フォルダーで、publish 時に LICENSE を同梱する。

| ソース | 責務 |
| --- | --- |
| [Program](../src/WinMicSwitch/Program.cs)、[Ui](../src/WinMicSwitch/Ui.cs) | STA のエントリーポイント。Local 名前空間の名前付き Mutex でセッション内の二重起動を防ぎ、起動時の UI カルチャを保持する。 |
| [TrayContext](../src/WinMicSwitch/TrayContext.cs) | ApplicationContext としてトレイ、メニュー、通知配送用 Control、オーディオと入力の登録を所有する。 |
| [AudioState](../src/WinMicSwitch/AudioState.cs) | IAudio を介した一覧・既定入力の取得、採番、ミュート・音量・ロールの操作と結果確認。 |
| [CoreAudio](../src/WinMicSwitch/Audio/CoreAudio.cs)、[Interop](../src/WinMicSwitch/Audio/Interop.cs)、[PolicyConfig](../src/WinMicSwitch/Audio/PolicyConfig.cs) | Core Audio の COM 定義、エンドポイント操作と通知購読、非公開の既定入力変更 API。 |
| [InputCapture](../src/WinMicSwitch/InputCapture.cs)、[OperationGate](../src/WinMicSwitch/OperationGate.cs) | 入力イベントと元時刻の対応付け、追加操作の破棄、更新中の予約と完了境界。 |
| [TrayWheel](../src/WinMicSwitch/TrayWheel.cs) | Raw Input の登録、アイコン位置の判定、ホイール差分と時刻の配送。 |
| [TrayText](../src/WinMicSwitch/TrayText.cs)、[TrayIconFactory](../src/WinMicSwitch/TrayIconFactory.cs) | ツールチップの整形、状態記号・番号の描画とテーマ色の選択。 |

## COM 相互運用

| 実装上の宣言 | Windows 側の契約と扱い |
| --- | --- |
| `IEnumeratorApi` | MMDeviceEnumerator（CLSID `BCDE0395-E52F-467C-8E3D-C4579291692E`）を必要時に生成する。GetDefaultAudioEndpoint は PreserveSig で HRESULT を受け、`0x80070490` を「既定なし」として扱い、他の失敗は例外に変換する。 |
| `IDeviceApi`、`IStore` | GetId は LPWStr の out string を使用する。名前は読み取り専用ストアのプロパティ（fmtid `A45C254E-DF1C-4EFD-8020-67D146A850E0`、pid 14）から取得する。Variant は明示レイアウトの 24 バイトで、VT_LPWSTR のときだけ文字列を読む。 |
| `IVolume` | IMMDevice.Activate で取得する。通知、スカラー音量、ミュートの各メソッドが正しい vtable 位置に来るよう、途中の未使用スロットも宣言する。ミュートは Windows BOOL としてマーシャリングし、書き込みには CoreAudio インスタンスのイベントコンテキスト GUID を渡す。 |
| `IDeviceEvents`、`IVolumeEvents` | コールバックを明示インターフェイスで COM に公開する。OnNotify のペイロードは保存せず、登録時の VolumeSource を通知キューへ渡す。 |
| `PolicyConfig.IPolicy` | CLSID `870AF99C-171D-4F9E-AF0D-E63DF40C2BC9`、IID `F8679F50-850A-41CF-9C72-430F290290C8`。SetDefaultEndpoint までのスロットを宣言し、ロールごとの呼び出しで COM インスタンスを生成・解放する。 |

多くの呼び出しは .NET の COM 相互運用が失敗 HRESULT を例外に変換する。AudioState の Result は想定する取得・設定エラーに HRESULT を保持する。非公開の PolicyConfig を含む実機での確認範囲は検証記録に示す。

## 状態取得と通知

1. TrayContext は状態不明のアイコンを表示し、最初の更新を UI スレッドへ送る。AudioState.Refresh はデバイス通知を登録し、IMMDeviceEnumerator で eCapture の DEVICE_STATE_ACTIVE を列挙する。名前はプロパティストアの PKEY_Device_FriendlyName から読む。取得できなければ代替名を使う。
2. 表示名を OrdinalIgnoreCase、同名時の ID を Ordinal で比較して並べ、ID と番号の対応表を作り直す。Console、Multimedia、Communications の取得結果を個別に保持し、Multimedia を Current とする。既定なしと API 失敗は区別する。
3. Current が一覧にあれば、その ID の IAudioEndpointVolumeCallback を登録してミュートと入力音量を読み、Multimedia の ID を再確認する。一致したときだけ取得値を採用する。音量は 0～1 のスカラーを百分率に丸める。前回値は引き継がない。
4. 一覧と既定 ID が食い違う場合など、Refresh が不整合を返すと TrayContext は追加取得を 1 回予約する。一覧取得失敗時は CoreAudio の通知・列挙子を解放し、次の取得で作り直す。定期ポーリングは行わない。

デバイスの追加・削除・状態・プロパティ変更、および入力に関係する既定変更は全体の再取得を要求する。音量通知は購読ごとに付けた ID と世代を送る。通知データに含まれる値を直接表示せず、自アプリの書き込みによる通知も読み直しの機会とする。

TrayContext.Signal は通知元をロック下の集合に入れ、Control.BeginInvoke で Drain を予約する。全体更新か現行購読の通知が含まれていれば、表示中のメニューを閉じて更新する。旧世代だけなら破棄する。操作中・更新中は要求を残し、Resume が予約済み操作を先に配送する。Windows の表示設定・ユーザー設定変更も同じ更新経路を使う。

## 明示操作と確認値

| 入口 | 設定前の確認 | 書き込みと結果確認 |
| --- | --- | --- |
| AudioState.Toggle | 現在の Multimedia の ID、一覧での利用可否、GetMute を取得。書き込み直前に同じ既定 ID か確認する。 | 同じ ID に SetMute で反転値を設定し、その ID のミュートと Multimedia を再取得する。設定結果・再取得値・既定 ID が揃えば成功。 |
| AudioState.AdjustVolume | Multimedia と利用可否を確認。ホイール差分は対象ごとに蓄積し、120 単位で 1 ノッチとする。 | GetMasterVolumeLevelScalar の値にノッチ数 × 0.02 を加え、0～1 に収める。既定 ID を確認して値が変わるときだけ設定する。読み戻しと既定 ID を確認し、音量差の許容値は 0.01。 |
| AudioState.Select | メニュー項目の ID が利用可能なら変更前ミュートを読む。この読み取りだけの失敗ではロール変更を続ける。 | 各ロールの直前に対象を再確認し、Console → Multimedia → Communications の順に変更する。対象を見失ったら残りは未実行とする。各ロールの ID と変更後ミュートを再取得する。 |

Select は IPolicyConfig::SetDefaultEndpoint の生成・呼び出しを PolicyConfig.Set に局所化する。ロール間の変更は不可分ではない。全ロールの設定が成功し、読み戻した全 ID が一致し、選択先の前後のミュートも取得できて一致した場合にダイアログを省く。それ以外は各ロールの書き込み結果・確認結果、ミュートの前後値を知らせる。各操作後、TrayContext は全体を再取得して表示を更新し、必要な場合に警告を表示する。

## 入力の識別と直列化

- InputCapture は IMessageFilter でキュー配送のトレイ左クリック、メニュー上のマウス選択、Enter を識別する。GetMessageTime の 32 ビット値を Environment.TickCount64 に対応付け、対象と時刻をその配送中に一度だけ消費する。Explorer からの直接配送は同じトレイの左押下とクリックを対応付ける。操作完了後、UI がアイドルに戻るまで残った直接配送を破棄する。
- TrayWheel は NotifyIcon の非公開フィールドからウィンドウとアイコン ID を取得し、メッセージ専用ウィンドウでマウスの Raw Input を受ける。MouseMove 後の縦ホイールについて、入力位置が Shell_NotifyIconGetRect の矩形内にあり、最前面のウィンドウがタスクバーまたはオーバーフロー表示に属することを確認する。メニュー表示中は音量操作を受け付けない。登録できなければメニューに「ホイール入力を利用できません」と表示する。
- OperationGate は予約中・実行中の追加入力と、記録時刻が前回の完了時刻以前の入力を破棄する。単独の状態更新中なら最初の操作（入力元不明なら警告）を 1 件だけ保留する。操作後の状態更新と結果ダイアログを含めて完了時刻を記録する。終了時は予約を取り消す。

## 表示とリソースの所有

メニューは開く直前に再取得して項目を作り直す。処理中は開かない。見出しは選択不可の ToolStripLabel、マイク行は ID を捕捉した ToolStripMenuItem である。制御文字は空白に変え、メニューの機器名に含まれる & はエスケープする。ツールチップは状態情報を先に組み立て、63 UTF-16 コード単位に収まる範囲で機器名をテキスト要素単位で加える。通知停止中は音量を省き、「自動更新停止」と取得できたミュートの「最終取得」を表示する。

アイコンは TrayIconFactory がビットマップに線と文字で描画する。DPI に応じて 32～128 ピクセルで生成し、2 桁では配置を縮め、3 桁以上では番号を記号の下に置く。システムの明色テーマでは黒、暗色では白、高コントラストでは WindowText を使う。状態・番号・DPI・前景色の組が変わったときに作り直す。[アイコン例](tray-icon.png)・[メニュー例](tray-menu.png) はドキュメント用画像である。

CoreAudio は列挙・読み書きに使った一時 COM 参照を処理後に解放し、機器名の PROPVARIANT は PropVariantClear で解放する。監視対象が変わったら旧購読を解除し、新しい対象の通知を登録する。解除失敗時はその音量インターフェイスとコールバックを保持し、終了などの Dispose で解除を再試行する。

終了要求で通知キューと操作予約を破棄し、実行中の操作・更新が戻った後にメッセージループを終了する。TrayContext.Dispose はシステムイベント、Raw Input、メッセージフィルター、Core Audio 通知を解除し、トレイ、アイコン、メニュー、配送用 Control を破棄する。アイコン生成では HICON を複製して元ハンドルを DestroyIcon し、差し替え時に旧 Icon を解放する。

## テストの構成

[テストプロジェクト](../tests/WinMicSwitch.Tests/) は Windows の対話セッションで実行するコンソールアプリである。標準実行はキュー・直接配送の入力、IAudio の偽物による状態と表示、操作受付、実 Windows での起動・終了時の読み取りを確認する。追加モードは次のとおり。

| 引数 | 機器と確認内容 |
| --- | --- |
| --live-tray | 3 ロールが同じ入力を使用する環境。実マイクのミュート・入力音量の変更と通知、同じ入力の再選択を確認する。音量操作は AdjustVolume を直接呼び、Raw Input は登録の成否を確認する。 |
| --two-mics 名前1 名前2 | 異なる 2 台の表示名を指定し、メニューからの往復切り替えとミュート保持、外部変更とロール分離を確認する。終了処理で元の設定を復元する。 |
| --hotplug USB名 残るマイク名 | 2 台の表示名を指定し、指示に合わせて USB を抜き差しする。残った機器への切り替え、メニュー更新、再採番、再接続後の通知を確認する。正常完了時に元の設定を復元・確認する。 |

### 回帰確認の基準

以下は設計上の期待値である。各環境で実施した範囲は検証記録に示す。

| ID | 条件 | 期待する結果 |
| --- | --- | --- |
| D01 | 明示操作要求を含まない起動・更新・右クリック・終了 | SetMute／SetDefault の呼び出しが 0 回。 |
| D02 | ミュート初期値 true／false | 直前の読み取りの反転を同じ ID に 1 回設定し、確認値を表示。 |
| D03 | 左クリックで基準取得／ACTIVE 確認／GetMute が失敗 | SetMute は 0 回、再取得後の実状態と失敗理由を表示。ミュート取得失敗が続く場合は Unknown。 |
| D04 | 書き込み前の基準変更 | 反転を中止。新しい ID に書き込まない。 |
| D05 | 書き込み後の基準変更 | 元対象の結果と新基準表示を分離。新基準への追加書き込みなし。 |
| D06 | 3 ロール選択 | Console → Multimedia → Communications の順。全ロールを読み直し、SetMute は 0 回。 |
| D07 | 一部 API 失敗／確認失敗／未実行 | 結果行を 3 件生成し、全体成功としない。ロールバックなし。 |
| D08 | API 失敗だが選択先と一致 | 確認済み状態と API 失敗の両方が残る。 |
| D09 | 切断・再接続・改名・同名機器 | 現在の一覧を名前・ID 順に並べて欠番を詰める。 |
| D10 | 0 件／列挙失敗／基準なし／別ロールだけ失敗 | 0 件はマイクなし、一覧または基準の取得不能は状態不明。別ロールだけの失敗はメニューに示す。 |
| D11 | 通知連発・取得中の追加通知・現行通知 → 旧世代の遅延通知 → Drain | 同時取得なし。異なる ID・世代の組を保持し、現行分による再取得を 1 回実行。取り出し後の通知は次バッチに残る。順序を逆にしても現行分を失わない。 |
| D12 | 旧世代だけの通知、現行・旧世代が混在したバッチ、終了後通知 | 旧世代だけなら読み取り・表示更新・設定なし。混在時は旧世代の組だけを除外し、現行分の取得・表示更新は実行。終了時は全保留データを破棄し、後続通知で処理しない。 |
| D13 | デバイス通知／基準ミュート通知それぞれの登録失敗、外部ミュート解除、回復 | 読み取りは継続。値が読めても Unknown と自動更新停止・最終取得を表示。通知が来なければその区別を維持し、次の取得で値と未登録部分を再試行する。両通知の登録後に状態を読めた場合だけ通常表示へ戻り、正常登録の重複なし。 |
| D14 | 明示操作中／結果ダイアログ中の再入、Idle 復帰後の遅延配送、完了後の別入力の記録時刻が境界と同値／より新しい場合 | 操作中・遅延・同時刻の入力は追加書き込み 0 回。完了後の別入力でも同値なら破棄し、境界より新しい入力だけ 1 回実行する。Reporting 中の対応不明要求は追加書き込み・追加ダイアログともに 0 回。32 bit 時刻周回でも同じ規則。終了は実行中処理の解放後。 |
| D15 | 名前に &、改行、サロゲート文字、長い名前。最大桁の番号、ロール差分と通知失敗の併発 | アクセスキー誤解釈なし。UTF-16 63 コード単位以内で、状態・自動更新停止／最終取得の意味を保持する。ID は不変。 |
| D16 | マイク選択で ACTIVE 入力の選択前ミュートだけが取得失敗 | 3 ロールの変更と読み直しを続行し、SetMute は 0 回。変更後に値を読めればその値を表示し、前後の保持は未確認。変更後も読めなければ Unknown とし、既定変更の成否とは区別する。対象の利用可能性を確認できない場合は以降のロールを書き込まない。 |
| D17 | Multimedia と Communications／Console の ID が異なる、同じ、取得失敗、既定なし | 異なる ID はメニューで用途別に表示。未確認と既定なしを区別し、左クリックの SetMute は Multimedia の ID だけに 1 回。他用途のミュート値を推測しない。 |
| D18 | 単独のバックグラウンド更新中の最初の入力が、確認済み／対応不明。再入配送／更新後の配送 | 確認済みなら更新後の最新状態で 1 回実行。更新中の対応不明なら Refreshing を保ち、失敗報告を 1 件保留する。元の更新・後処理の完了前は通知 0 回、完了後は書き込み 0 回・通知 1 回。どちらの予約中も追加操作は実行せず、次の通知更新より優先する。終了時は保留を破棄。 |
| D19 | 入力元が対応しない時刻、別メッセージの介在、入れ子配送、入力記録 の再利用。Refreshing／Reporting 中の対応不明 | 対応しない情報で書き込まず、直前の記録を流用しない。Idle の対応不明は通知 1 回。単独の Refreshing 中は D18 の共通保留枠で更新完了後に 1 回だけ報告。失敗報告の保留中・実行予約中・実行中・Reporting 中の追加入力は、追加書き込み・追加ダイアログともに 0 回。確認済み入力は予約で再採取・二重実行しない。実イベントとの対応はA10 で別途確認。 |

## 検証記録

### 初版の検証記録（表示・採番変更前）

2026-11-22、Windows x64 ビルド 26300、.NET SDK 10.0.404、Windows Desktop Runtime 10.0.15、マイク (Realtek(R) Audio) 1台で実行した。3ロールの開始時の入力はすべて同じで、ミュートは解除状態だった。

| 確認 | 方法と結果 |
| --- | --- |
| 入力経路（A10 前半の一部） | 設定処理を接続する前に、実際の NotifyIcon と ContextMenuStrip の HWND へテストメッセージをキュー投入。トレイ左クリック、項目のマウス選択、Enter の各イベントで元時刻の1回消費を確認。追加検証では、配送を80 ms遅らせても元時刻が保持された。物理キー・マウスの手操作と Explorer 経由の全経路は未確認。 |
| 1巡目 | Release ビルドと偽デバイスのテストが成功。実際の一覧、3ロール、通知登録、ミュートを取得。トレイの起動と終了前後の読み取り値が一致した。 |
| 2巡目以降 | 操作保留・再入・時刻境界を分離し、長い名前の表示とキーボード入力先を修正。最終 Release ビルドは警告・エラー各0件。保留中の追加入力、同時刻・遅延入力の破棄、読み取り失敗、操作直前の既定変更、切断後の選択、Unicode名のテストが成功。 |
| 実マイク（A01～A05 の該当部分） | トレイのイベントからミュート・解除し、独立した Core Audio クライアントで値と通知を確認。逆方向の変更もトレイのツールチップに反映。同一マイクを再選択し、Console、Multimedia、Communications とミュートを再取得。終了時は開始時と一致。Windows 設定画面との目視照合は未実施。 |
| 配布 | Release と自己完結版の exe をそれぞれ直接起動し、実際の「終了」項目を UI Automation で実行。いずれも終了コード 0。 |
| 障害系 | 部分 API 失敗と一致する読み戻し、選択前ミュート取得失敗、入力0件、通知登録失敗と回復、再接続時の番号保持は偽デバイスで確認。実サービス停止や実機切断の再現は未実施。 |

2026-11-23、直接配送の左クリックが「入力元を確認できません」で中止される問題を修正した。別スレッドから直接送った押下・解放と、完了直後に保留されていた追加通知の破棄を回帰テストに追加。更新した自己完結版で、Explorer の通知領域に表示されたアイコンへマウス入力を送り、H340 の表示が解除中 → ミュート中 → 解除中と切り替わり、両クリックでエラーダイアログが出ないことを確認した。

#### USB ヘッドセット接続後の追加確認

同じ Windows 環境で、Logi USB Headset H340（#1）と Realtek(R) Audio（#2）を使用した。開始時は Console・Multimedia・Communications のすべてが H340 で、両機ともミュート解除だった。

| 確認 | 実測結果 |
| --- | --- |
| 既定入力の往復とミュート保持（A04・A05） | H340 はミュート、Realtek は解除にしてトレイメニューから往復。毎回、別の Core Audio クライアントで3ロールの ID と両機のミュートを読み直した。選択先、ツールチップ、メニューのチェックが一致し、両機のミュートは保持された。 |
| 外部変更とロール分離（A03・A06・A13 の該当部分） | 外部からの H340 のミュート・解除をトレイに反映。一般用を Realtek、通信用を H340 にすると「一般用#2」「通信用#1 対象外」を表示し、メニューの3ロール内訳も一致した。左クリックで Realtek だけがミュート・解除された。Console だけを H340 に設定したケースでは、Windows の読み戻しで Multimedia も H340 に変わった。Console だけが異なる構成は再現できなかった。 |
| 物理切断・再接続（A07） | メニュー表示中に H340 を抜くとメニューが自動で閉じた。Realtek は #2 を維持し、再度開いた一覧では H340 が消え、Realtek にチェックが付いた。同じ USB ポートに戻すと同じ ID・#1 が復帰した。復元処理前に Windows の3ロールすべてが H340 に戻り、両ミュートの最終値も開始時と一致した。 |
| Windows 画面照合 | 再接続後、サウンドの「録音」タブで H340 の「既定のデバイス」と Realtek の「準備完了」を確認した。 |
| 残る実機条件 | Teams の通話前・通話中（A11）、機器側操作によるミュート、Windows 10、複数 DPI・高コントラスト・Explorer 再起動・長時間の資源計測（A12）など。 |

Release ビルド後、リポジトリのルートで次のテストを個別に実行した。1本目は設定を一時変更して終了時に復元する。2本目は初期一覧を取得してから `READY remove USB` と `READY reconnect USB` を順に表示する。表示に合わせて手で抜き差しする。

```powershell
.\tests\WinMicSwitch.Tests\bin\Release\net10.0-windows\WinMicSwitch.Tests.exe --two-mics 'マイク (Realtek(R) Audio)' 'マイク (Logi USB Headset H340)'
.\tests\WinMicSwitch.Tests\bin\Release\net10.0-windows\WinMicSwitch.Tests.exe --hotplug 'マイク (Logi USB Headset H340)' 'マイク (Realtek(R) Audio)'
```

### トレイ操作更新後の検証

Windows 11 x64 ビルド 26300、Realtek マイク1台で Release ビルドと `--live-tray` を実行。実マイクで音量変更と独立クライアントからの読み戻し、ミュート保持、ミュート通知の往復を確認し、開始時の設定へ戻した。exe を2回起動し、最初のプロセスだけが残ることを確認した。

メニュー見出しの選択不可、日本語表示と英語フォールバック、音量の上下限、接続台数の変化に伴う再採番は回帰テストで確認した。今回は USB マイクの物理的な抜き差しと通話アプリでの確認は未実施。ホイール入力は OS から配送されたが、ロック画面がアイコンを覆っていたため、実際にアイコン上で回して音量が変わるところまでの確認は保留。

細線アイコンと音量表示の更新後、Realtek マイクの入力音量を 85% → 80% → 85% と変え、通知後のツールチップと Windows の読み戻しが一致した。消音時も設定音量を表示し、通知前後で日本語表示が維持された。英語表示と未対応言語から英語へのフォールバックは UI カルチャを切り替える回帰テストで確認した。

## API の参照資料

- [Microsoft Learn: GetMessageTime](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getmessagetime)
- [Microsoft Learn: MSG](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-msg)
- [Microsoft Learn: IMessageFilter.PreFilterMessage](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.imessagefilter.prefiltermessage?view=windowsdesktop-10.0)
- [dotnet/winforms: NotifyIcon.cs（調査用）](https://github.com/dotnet/winforms/blob/main/src/System.Windows.Forms/System/Windows/Forms/NotifyIcon.cs)

- [Microsoft Learn: IMMDeviceEnumerator](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immdeviceenumerator)
- [Microsoft Learn: IMMNotificationClient](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immnotificationclient)
- [Microsoft Learn: IAudioEndpointVolume](https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nn-endpointvolume-iaudioendpointvolume)
- [Microsoft Learn: IAudioEndpointVolumeCallback](https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nn-endpointvolume-iaudioendpointvolumecallback)
- [Microsoft Learn: PropVariantClear](https://learn.microsoft.com/en-us/windows/win32/api/propidl/nf-propidl-propvariantclear)
- [Microsoft Learn: NotifyIcon](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.notifyicon)
- [AudioDeviceCmdlets: PolicyConfigClient.cs（非公開 API の参考実装）](https://github.com/frgnca/AudioDeviceCmdlets/blob/master/SOURCE/PolicyConfigClient.cs)
