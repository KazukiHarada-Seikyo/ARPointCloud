# 撮影画面のUI設定ガイド（下書き）

対象: `Assets/Scenes/SampleScene.unity`
狙い: Googleレンズ風。カメラ映像を主役にして、文字とボタンを最小限に浮かせる

この文書は**下書き**。実機で見て数値を直したら、この文書のほうも直すこと。
数値は Canvas の基準解像度 1080 × 1920 での値。

---

## 0. いまの状態（読み取った事実）

| 対象 | 現状 | 問題 |
|---|---|---|
| `Canvas` | Screen Space Overlay / Scale With Screen Size / 1080×1920 / Match=0(幅) | 問題なし。このまま使う |
| `Panel` | 全画面の Image、黒 **alpha 0.59** | **カメラ映像を全面的に暗くしている。これが最大の問題** |
| `Text (TMP)` | `Panel` の子。左上アンカー、1000×500 の固定 | 文字量が変わるとはみ出す／余る |
| `FrameCapture` ボタン | 中央アンカー、pos y = **-1207** | 端末によって画面外。Pixel 9a でも下端ぎりぎり |
| `FrameRecord` ボタン | 中央アンカー、pos y = **-1204** | 同上 |
| `Record` ボタン | 左下アンカー、160×30 | 押しにくい。30px は指に対して小さすぎる |
| `CSVButton` | 右下アンカー、160×30 | 同上 |

ボタンの呼び先は次のとおり（変更しないこと）。

| ボタン | 呼び先 |
|---|---|
| `FrameCapture` | `FrameCapture.RequestCapture()` |
| `FrameRecord` | `FrameCapture.ToggleRecording()` |
| `Record` | `GeospatialCsvLogger.ToggleRecording()` |
| `CSVButton` | `GeospatialCsvLogger.ShareLatestFile()` |

---

## 1. 目指す形

```
┌─────────────────────┐
│ ● 撮影できます            │ ← 上に浮く半透明の帯
│ 録画を押してゆっくり…        │   中身に合わせて高さが伸びる
├─────────────────────┤
│                     │
│      （カメラ映像）        │ ← 何も載せない
│      粒がここに出る          │
│                     │
│                     │
├─────────────────────┤
│   [2F]   (●)   [i]  │ ← 下に浮くボタン列
└─────────────────────┘
```

考え方は2つだけ。

- **中央は空ける。** 粒が見える場所を塞がない
- **カメラアプリの配置を借りる。** 中央下に大きな録画ボタン、左右に小さな補助。説明が要らない

---

## 2. 手順

### 2-1. Panel の全画面の黒を外す

`Panel` を選び、Inspector の `Image` で:

- **Color の alpha を 0 にする**（コンポーネントごと消してもよいが、`Text (TMP)` の親なので残すほうが手戻りが少ない）

これだけでカメラ映像が出る。粒の見え方が変わるので、先にこれをやって一度確認するとよい。

### 2-2. 上部の帯を作る

`Canvas` を右クリック → UI → Panel。名前を **`StatusBar`** にする。

`RectTransform`:

| 項目 | 値 |
|---|---|
| Anchor | 上ストレッチ（min `0, 1` / max `1, 1`） |
| Pivot | `0.5, 1` |
| Pos X / Pos Y | `0` / `-48` |
| Width（Left/Right） | 左右とも `32`（＝ sizeDelta.x が `-64`） |
| Height | 後述の Content Size Fitter が決めるので触らない |

`Image`:

- Source Image: `Background`（Unity 内蔵の角丸）
- Color: 黒、**alpha 0.55**

コンポーネントを2つ足す。

`Vertical Layout Group`
- Padding: Left/Right `28`、Top/Bottom `22`
- Child Alignment: `Upper Left`
- Control Child Size: Width ✓ / Height ✓
- Child Force Expand: Width ✓ / Height ✗

`Content Size Fitter`
- Horizontal Fit: `Unconstrained`
- Vertical Fit: **`Preferred Size`**

> Content Size Fitter を入れるのは、文字量が状況で大きく変わるため。
> 「準備中」だけのときと、チェック項目が全部出るときで倍以上違う。
> 固定の高さにすると、どちらかで必ず破綻する。

### 2-3. 文字を帯の中へ移す

`Panel` の子にある **`Text (TMP)`** を、`StatusBar` の子へドラッグして移す。

移したあと `RectTransform` を直す:

| 項目 | 値 |
|---|---|
| Anchor | ストレッチではなく `Top Left` のままでよい（Layout Group が上書きする） |
| Width / Height | 触らない（Layout Group が決める） |

`TextMeshProUGUI`:

| 項目 | 値 |
|---|---|
| Font Size | `34` |
| Auto Size | **オフ** |
| Alignment | Left / Top |
| Wrapping | `Enabled` |
| Overflow | `Overflow` |
| Color | 白 |

> Auto Size を切るのは、行数で文字の大きさが勝手に変わると読みにくいため。
> 高さは Content Size Fitter が伸ばすので、縮める必要がない。

### 2-4. 下部のボタン列を作る

`Canvas` を右クリック → Create Empty。名前を **`BottomBar`** にする。

`RectTransform`:

| 項目 | 値 |
|---|---|
| Anchor | 下ストレッチ（min `0, 0` / max `1, 0`） |
| Pivot | `0.5, 0` |
| Pos X / Pos Y | `0` / `72` |
| Left / Right | `0` / `0` |
| Height | `240` |

`Horizontal Layout Group` を足す:

- Child Alignment: `Middle Center`
- Spacing: `120`
- Control Child Size: Width ✗ / Height ✗
- Child Force Expand: Width ✗ / Height ✗

> Control と Force Expand を全部切るのは、ボタンごとに大きさを変えたいため。
> 入れたままだと3つが均等に引き伸ばされる。

### 2-5. 3つのボタンを置く

既存のボタンを流用する。**新規に作るより、OnClick の設定が残るぶん安全。**

`FrameRecord` / `FrameCapture` / `CSVButton` を `BottomBar` の子へドラッグする。
`Record`（CSVロガーの録画）はひとまず `BottomBar` の外に残し、`Panel` の子など
目立たない場所へ移しておく（§6-1 の検証で使うため消さない）。

並び順は Hierarchy 上から順に左→右になる。**上から `FrameCapture` → `FrameRecord` → `CSVButton`** に並べ替える。

各ボタンの `RectTransform`:

| ボタン | 役割 | Width × Height |
|---|---|---|
| `FrameCapture` | メモ（後述で差し替え） | `120` × `120` |
| `FrameRecord` | 録画 | `200` × `200` |
| `CSVButton` | 詳細 | `120` × `120` |

各ボタンの `Image`:

| ボタン | Source Image | Color |
|---|---|---|
| `FrameCapture` | `Knob` | 白 alpha `0.16` |
| `FrameRecord` | `Knob` | 白 alpha `0.9` |
| `CSVButton` | `Knob` | 白 alpha `0.16` |

> `Knob` は Unity 内蔵の丸いスプライト。Source Image の右の丸をクリックし、
> 検索欄に `Knob` と入れると出る。外部素材を用意しなくて済む。

### 2-6. 録画ボタンの中身

`FrameRecord` を右クリック → UI → Image。名前を **`Inner`** にする。

| 項目 | 値 |
|---|---|
| Anchor / Pivot | 中央 `0.5, 0.5` |
| Pos | `0, 0` |
| Width × Height | `150` × `150` |
| Source Image | `Knob` |
| Color | `#E24B4A` |
| Raycast Target | **オフ** |

> Raycast Target を切らないと、内側の丸がタップを吸ってボタンが反応しない。
> ここは毎回忘れるところ。

外側の白（200）と内側の赤（150）が重なって、白いリングに見える。カメラアプリと同じ。

### 2-7. ボタンの文字

`FrameCapture`（メモ）と `CSVButton`（詳細）の子の `Text (TMP)` を直す。

| ボタン | 文字 | Font Size | Color |
|---|---|---|---|
| `FrameCapture` | `-`（スクリプトが上書きする） | `36` | 白 |
| `CSVButton` | `i` | `44` | 白 alpha `0.8` |

`FrameRecord` の子の `Text (TMP)` は**削除するか、文字を空にする**。赤い丸の上に文字があると濁る。

### 2-8. 見た目を動かすスクリプトをつなぐ

`DebugUI` に **`CaptureButtons`** を追加し、Inspector で接続する。

| 項目 | つなぐもの |
|---|---|
| Capture | `DebugUI`（`FrameCapture`） |
| Record Inner | `FrameRecord/Inner` の RectTransform |
| Record Inner Image | 同上の Image |
| Circle Sprite | `Knob` |
| Rounded Sprite | `UISprite` |
| Note Label | `FrameCapture` ボタンの子の `Text (TMP)` |

これで録画中に赤い丸が小さな角丸の四角に変わり、メモボタンに現在値（`2F` など）が出る。

### 2-9. OnClick の割り当てを直す

役割を変えたボタンだけ、OnClick を付け替える。

| ボタン | 変更前 | 変更後 |
|---|---|---|
| `FrameCapture` | `FrameCapture.RequestCapture()` | **`FrameCapture.CycleNote()`** |
| `CSVButton` | `GeospatialCsvLogger.ShareLatestFile()` | **`GeospatialStatusDisplay.ToggleDetail()`** |
| `FrameRecord` | `FrameCapture.ToggleRecording()` | 変更なし |

> 1枚撮影（`RequestCapture`）と CSV 共有（`ShareLatestFile`）は消したくない。
> `Record` ボタンと同じく、目立たない場所に小さいまま残しておく。
> 屋外で使わないが、不具合の切り分けには要る。

### 2-10. 詳細表示を既定でオフに

`DebugUI` の `GeospatialStatusDisplay` で **`Show Detail` のチェックを外す**。

上の帯が短くなり、カメラ映像が広く見える。数値は `i` ボタンで出せる。

---

## 3. 確認すること

実機で Play して、次を見る。

1. **カメラ映像が暗くない**（2-1 ができている）
2. **粒が見える**（帯とボタンに隠れていない）
3. **録画ボタンを押すと赤い丸が四角になる**
4. **メモボタンを押すと `-` → `1F` → `2F` … と変わる**
5. **`i` を押すと数値が出入りする**
6. 文字が画面からはみ出さない

---

## 4. 分かっている弱点

**セーフエリアに対応していない。** 最近の端末は上下に切り欠きや丸角がある。
いまは上 48 / 下 72 の余白でごまかしているだけなので、機種によっては
帯やボタンが欠ける。対応するなら `Screen.safeArea` を読んで
`RectTransform` を調整するスクリプトが要る。

**縦持ち専用。** 横持ちにするとボタン列が間延びする。撮影は縦持ち前提なので
当面は問題ないが、`Screen.orientation` を固定しておくほうが安全かもしれない。

**帯が伸びすぎることがある。** 案内文が長い状態（外挿の警告など）だと
上の帯が画面の3分の1を占める。実機で見て気になるなら、
`CaptureGuidance` の文言を短くするのが早い。
