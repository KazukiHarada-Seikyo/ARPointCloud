# ARPointCloud — 撮影アプリ 引き継ぎ

作成: 2026-08-10 / 更新: 2026-08-10（実装との突き合わせで訂正）
このファイルはコードを書く担当への作業指示。プロジェクト全体像は同ディレクトリの `ROADMAP.md` を先に読むこと。

---

## 0. まず読む順番

1. `ROADMAP.md` — ゴール、3段階の構造、未解決課題
2. このファイル — 現在地と次にやること
3. `Assets/Scripts/` の既存4本

**このプロジェクトの構造を一行で**: ①点群を取れるようにする → ②座標＋スケールをつける → ③精度を上げる。いま①に入るところ。②の下地(ARCore姿勢・地球座標のログ)は完成済み。

---

## 1. 現在地

### 動いているもの

| ファイル | 役割 | 状態 |
|---|---|---|
| `GeospatialStatusDisplay.cs` | 4段ステータス＋精度＋方位角(参考)＋ローカル座標の画面表示 | 完成 |
| `GeospatialCsvLogger.cs` | 毎フレーム生値のみCSV記録＋NativeShare共有 | 完成 |
| `VpsCoverageChecker.cs` | Inspectorに登録した座標リストを順に一括問い合わせ | 完成（実用性は低いと判明） |
| `CameraConfigLister.cs` | 映像設定の一覧表示＋最高解像度への切替 | 完成（調査用） |

4本とも同一GameObject(`Debug UI`)に貼り、Inspectorで相互参照を接続済み。UIはCanvas＋TextMeshPro(Noto Sans JP, Dynamicアトラス)。ボタンからOnClickで各メソッドを呼ぶ構成。

**画面の方位角だけは計算値**: `GeospatialStatusDisplay.HeadingFromEun()` がEUNクォータニオンから方位角を出して表示している。§2の「計算はしない」原則の例外だが、**画面表示のみでCSVには書いていない**。この線引きは意図的なので、消さないこと・CSVに足さないこと。

### バージョン（API仕様の確認時はこれを見ること）

- Unity **6000.5.6f1**
- AR Foundation / ARCore XR Plugin **6.5.0**
- ARCore Extensions **1.54.0**（`com.google.ar.core.arfoundation.extensions` の `arf6` ブランチ）
- NativeShare（yasirkula、gitURL参照）

`XRCpuImage` 系はAR Foundationのバージョンで API が変わっている。ネット上のサンプルを引くときは6.5系か確認すること。

### 実機で確認済みの事実

- Pixel 9a / Android 16
- Geospatialは動作。屋内でも4段すべて成立するが、**値が出ること＝VPSが効いていること、ではない**
- 選べる映像設定は **640×480 / 1280×720 / 1920×1080（すべて30fps）**、既定は640×480
- 1920×1080時の内部パラメータ: `fx=1394.7 fy=1395.6 cx=970.9 cy=534.5`
- 640×480時: `fx=463.1 fy=463.0 cx=322.6 cy=237.9`

---

## 2. 次にやること（フェーズ1・撮影アプリ）

### 全体像

「1枚ずつシャッターを切る」形にはしない。**かざして動かし続けている間ずっと取り込む**。ただし保存は動画ファイルではなく **姿勢付きの連番JPEG**。

動画ファイルを採らない理由:
- フレーム間圧縮が特徴点の手がかり（高周波成分）を最初に捨てる
- ローリングシャッター歪み（このデバイスは読み出し最大35ms）
- エンコーダはARCoreの姿勢を1コマずつ刻んでくれない

### 実装の順序（この順で進める）

**Step 0 — git初期化とキーの隔離**
このプロジェクトはまだgit管理下にない。root の `ignore.conf` はPlastic SCM用でgitは読まない。写真が増える前に `git init` と `.gitignore` を済ませる。同時に `ProjectSettings/ARCoreExtensionsProjectSettings.json`（APIキーが平文）をgitから除外する。§3参照。

**Step 1 — 1枚だけ保存する**
`ARCameraManager.frameReceived` を購読し、`TryAcquireLatestCpuImage()` で1枚取得 → JPEGにして `Application.persistentDataPath` に保存 → 画面にファイル名とバイト数を出す。ここまでで一度実機確認。

**Step 2 — 連続保存**
録画中は一定レート（初期値10fps）で保存し続ける。同時に `frames.csv` を書く。

**Step 3 — 負荷調整**
1080pの変換が間に合うか実機で見て、レートを決める。

### Step 1 の実装メモ

- `XRCpuImage` は **YUV形式**。RGBへの変換が要る。`XRCpuImage.ConvertAsync()` を使えばメインスレッドを止めずに済む
- `XRCpuImage` は使い終わったら必ず `Dispose()`。放置するとフレームバッファが枯れて映像が止まる
- **姿勢は `frameReceived` ハンドラの中でその場で読む。** `ConvertAsync` のコールバック内や後続の `Update()` で `_arCamera.transform` を読むと、その時点ではカメラがもう動いている。「画像とその画像を撮った瞬間の姿勢」の対応が静かに壊れる、いちばんやりがちな取り違え
- **時刻は2種類とも記録する。** `DateTimeOffset.UtcNow`（＝行を書いた時刻）と `ARCameraFrameEventArgs.timestampNs`（＝フレーム自身の時刻）はずれる。数十msのずれは歩行中で数cmに相当し、§6-2で別撮り方式を退けた理由と同じ問題になる。生値主義に従って両方書く
- JPEGエンコードは `ImageConversion.EncodeToJPG(texture, 92)` 程度。品質は90〜95で
- ファイル名に `unix_ms` を入れる。CSVの `unix_ms` 列と突き合わせる鍵になる

### Step 3（負荷調整）で打てる手

レートを落とす前に検討する順:

1. `ImageConversion.EncodeToJPG` は `Texture2D` を取るため**メインスレッド専用**。`ImageConversion.EncodeNativeArrayToJPG` は `NativeArray` を直接受けるので、ワーカースレッド（Job / Task）に逃がせる。1080p×10fpsではここが律速になる可能性が高い
2. ファイル書き込みも別スレッドに逃がす（キューに積んで書き手を1本回す）
3. それでも間に合わなければレートを落とす

### 容量の見積もり（撮影前に必ず確認）

1080p JPEG（品質92）でおよそ300〜500KB／枚。10fpsだと:

- 1分 ≒ 200〜300MB
- 10分 ≒ 2〜3GB

**録画開始前に空き容量を確認して画面に出すこと。** 撮影の途中で書けなくなるのが最悪。1回の撮影は数分を上限に考える。

### frames.csv に書くもの（1フレーム1行）

```
unix_ms, frame_timestamp_ns, elapsed_s, frame,
session_state, earth_state, tracking_state,
local_px, local_py, local_pz, local_qx, local_qy, local_qz, local_qw,
lat, lon, alt_ellipsoid, eun_qx, eun_qy, eun_qz, eun_qw, acc_h, acc_v, acc_yaw,
filename, img_w, img_h, fx, fy, cx, cy,
angular_speed_deg_s
```

列名と定義は既存の `geolog_*.csv` に合わせてある（`frame_index` ではなく `frame`）。前半24列は `geolog_*.csv` と同じ意味なので、解析スクリプトを使い回せる。

**`elapsed_s` は「アプリ起動からの経過秒」**（`Time.realtimeSinceStartup`）。録画開始からではない。`geolog_*.csv` と同じ意味に揃えてある。録画開始時刻は1行目を見れば分かる。

**`angular_speed_deg_s` の定義**: **直前に保存したフレーム**との `local_q*` の角度差(度) ÷ `unix_ms` の差(秒)。1行目は空欄。画像処理なしで計算できるブレの目安。**速いフレームを端末側で捨てない**、記録だけしてPC側で判断する。

> これは唯一の計算値の例外。`local_q*` から再計算できるので原則としては冗長だが、**CSVをそのままAIに渡して相談する用途**があるため残す判断をした（本人決定）。定義を変えるときは必ずこの節も直すこと。

### GeospatialCsvLogger との関係

**撮影中は `frames.csv` 一本。`GeospatialCsvLogger` とは排他にする。** 両方走らせると同じ値を2回書くうえ、StreamWriter 2本とJPEG保存が書き込みを取り合う。

`GeospatialCsvLogger` は消さない。§6-1の「階を変えた楕円体高の検証」のように、撮影せずログだけ取る作業で使う。録画ボタンに相互ガードを入れること。

### 絶対に守る設計原則（本人が決めたもの）

- **生の値だけ書く。計算は一切しない。** 方位角も標高も書かない。後から計算式を直したくなったとき、撮り直しになるのを避けるため（例外は `angular_speed_deg_s` の1列のみ。上記の理由による）
- **現地で判断しない、全部持って帰る。** フレームの取捨選択を端末側でやらない
- 回転はオイラー角ではなくクォータニオンのまま
- 追跡できていない行は空欄（0を書くと有効値として読まれる）
- `CultureInfo.InvariantCulture` を必ず付ける

---

## 3. 罠と注意（実機で踏んだもの）

### 解像度は起動のたびに640×480へ戻る
`currentConfiguration` はセッションと共に作り直される。**自動で1920×1080を選ぶ処理を入れること。** ボタン押し忘れで低解像度データが残るのが最悪の事故。切替時はカメラが再起動しトラッキングが一度リセットされる。

**ただし「起動時」には設定できない。** `GetConfigurations()` は映像が流れ始めるまで0件を返す（`CameraConfigLister.cs` の「映像がまだ流れていません」分岐がこれ）。**設定一覧が空でなくなった最初のタイミングで1回だけ**切り替える形にする。

さらに、切替でトラッキングがリセットされる以上、**切替は録画開始より前に完了していなければならない**。録画ボタン側にガードを入れる（現在の解像度が1920×1080でなければ録画を開始しない）。

### 1920×1080は4:3から16:9に縦がトリミングされている
水平視野は69度のまま、垂直視野が約55度→約42度に狭まる。cyが3倍にならない（713ではなく534）のはこのため。

検算: fxとcxはちょうど3倍（463.1→1389.3、322.6→967.8）。cyは 713.7−534.5＝179.2 で、1440→1080 の**上下180ずつの対称クロップ**と一致する。撮影ガイダンスに影響する。

### 内部パラメータは一定ではない
AR Foundationがフォーカスを`FIXED`→`AUTO`に変更している。ピントが動けば焦点距離も動く。加えてARCore側も`camera_intrinsics_recalibration_mode: 1`で常時再校正している。**1回測って固定、は不可。毎フレーム記録すること。**

### 露出・ホワイトバランスもAUTO
AR Foundationから固定できない。フレーム間で明るさが変わるのは前提として受け入れる。

### XR Origin の Camera Y Offset
既定値1.1176がカメラYに常時加算される。0に設定済み。**再びプレハブを作り直すときは必ず確認すること。** これを見落とすと点群が丸ごと1.1m浮く。

### トラッキングは壊れる
屋内で単調な面を映していると `Lack of valid visual measurements` でVIOがリセットされる。リセットが起きると前後で座標系が別物になる。`session_state` に出るので記録はできている。

### ビルド環境
- iOS Build Support 未導入だとARCore ExtensionsのiOS用エディタスクリプトがCS0234で落ちる（Unity Hubでモジュール追加）
- Publishing Settings で **Custom Main Gradle Template** と **Custom Gradle Properties Template** の両方が必要（EDM4UのJetifier）
- Build Settings の Scenes In Build にシーン未登録だと "not used by any Scenes" 警告が出続ける

### APIキーとバージョン管理
`ProjectSettings/ARCoreExtensionsProjectSettings.json` に `AndroidCloudServicesApiKey` が**平文**で入っている（`AndroidAuthenticationStrategySetting: 2` ＝ APIキー認証）。

- **このファイルはgitから除外する。** 一度コミットすると履歴に残り、あとから消すのが面倒
- 代わりに `.template` を置いて、必要な項目だけ分かるようにする
- 公開前に必ずAndroidアプリ制限（パッケージ名＋SHA-1）をかけること
- root の `ignore.conf` はPlastic SCM用。**gitは読まない**ので `.gitignore` が別に要る

### 取り出し
```
adb pull /sdcard/Android/data/jp.seikyo.arpointcloud/files/ .
```
Android 11以降、この領域はファイルマネージャから見えない。CSV1枚ならNativeShareの共有シートでも可。**写真が数百〜数千枚（数GB）になる**ので撮影データはUSB前提。

---

## 4. 進め方の流儀

本人はこの方針を明示している。守ること。

- 返答は標準語・です／ます調。断定的・説教くさい言い回しを避ける。平易な言葉で、略語は初出時に意味を明示
- **選択肢を出す前に、判断の土台になる仕組みを先に説明する**
- 技術は段階的に。一度に大量のコードや数式を出さない
- **スクリプトを出すときは差分ではなく、変更したファイルの全文を出す**（部分差分の貼り直しで何度か詰まった経緯がある）
- 結果だけ渡さない。本人が仕組みを理解しながら進めたい
- 誠実な指摘を歓迎する。実際、こちらの設計ミス（屋内でVPS前提の検証を組んだ件、撮影を静止画1枚ずつと想定した件）は本人の指摘で修正された

### このアプリの守備範囲

コードとコマンド（adb、git）は担当できる。**Unityエディタ側は手作業**（Inspectorの接続、シーンへの配置、ボタンのOnClick割り当て）。ここは本人に依頼する形になるので、必要な操作を明示的に伝えること。

---

## 5. 後回しにしている宿題

- **高さの出どころ**: 垂直精度が水平より良いのが不自然。地形の標高データ由来の疑いがある。屋外で両階ともVPSが成立した状態を作って測り直す（屋外に出る回にまとめる）
- **外挿の検出**: 精度値が固着している区間は測定ではなく外挿。画面とCSVに検出を入れると撮影中に気づける。まだ未実装
- **遡り補正（ループクロージャ）**: フェーズ0では素の値を記録する方針。対策はアンカー。まだ手を付けていない
