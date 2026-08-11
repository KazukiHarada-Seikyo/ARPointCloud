# 撮影画面の仕上げ手順（第2版・クリック単位）

`UI_SETUP.md` の §2 まで（階層とレイアウト）は完了している前提。
この文書は**見た目の仕上げだけ**を、迷わない粒度で書いたもの。

所要 20〜30分。上から順に、飛ばさずにやること。
各段階に「ここまでで見えるはず」を書いてあるので、そこで一度確認する。

---

## 大事な制約（先に読む）

**1つのGameObjectに Graphic は1つしか付けられない。**

`Image` と `UICircle` はどちらも Graphic なので、同居できない。
必ず **Image を外してから UICircle を足す**。逆順だと Add Component が
無反応になって「なぜか足せない」と悩むことになる。

---

## 手順0. 壊れているものを片付ける

`UICircleImage` は廃止したので、スクリプトが存在しない。
付けた場所は Inspector に **「Missing (Mono Script)」** と赤く出ているはず。

次の4つを順に選び、Missing の行を右クリック → **Remove Component**。

1. `Canvas / BottomBar / FrameCapture`
2. `Canvas / BottomBar / CSVButton`
3. `Canvas / BottomBar / FrameRecord`
4. `Canvas / BottomBar / FrameRecord / Inner`

> **ここまでで**: Inspector から赤い警告が消える。見た目は変わらない。

---

## 手順1. シェーダを登録する

1. メニュー **Edit → Project Settings**
2. 左の一覧から **Graphics**
3. **Always Included Shaders** を探す（下のほうにある）
4. `Size` の数字を **1つ増やす**
5. 増えた一番下の空欄の右にある **◎**（丸いボタン）をクリック
6. 検索欄に `UICircle` と入力 → **ARPointCloud/UICircle** を選ぶ

既に `ARPointCloud/PointPreview` が入っているはずなので、これで2つ並ぶ。

> **なぜ必要か**: `Shader.Find()` は実行時に名前で探す。Unityはビルド時に
> 「どこからも参照されていないシェーダ」を削除するので、登録しないと
> **エディタでは動くのに実機で何も出ない**。

> **ここまでで**: 見た目は変わらない。ビルドしたときに効く。

---

## 手順2. メモボタンを丸くする

`Canvas / BottomBar / FrameCapture` を選ぶ。

### 2-1. Image を外す

1. Inspector の **Image** の見出しを右クリック
2. **Remove Component**

このとき Button の **Target Graphic** が `None` になる。あとで直すので今はよい。

### 2-2. UICircle を足す

1. Inspector 下部の **Add Component**
2. 検索欄に `UI Circle` と入力
3. **UI Circle** を選ぶ（`UI / AR Point Cloud / UI Circle` にもある）

### 2-3. 値を入れる

| 項目 | 値 |
|---|---|
| Color | `FFFFFF` / **A = 46**（0〜255表記。0.18相当） |
| Raycast Target | オン（既定のまま） |
| Roundness | `1` |
| Ring Width | `0` |
| Softness | `1.2` |

> Color をクリックするとカラーピッカーが出る。右下の **Hexadecimal** 欄に
> `FFFFFF2E` と入れると一発で入る（最後の2桁が透明度）。

### 2-4. Target Graphic を直す

1. Inspector の **Button** を見る
2. **Target Graphic** の欄に、**同じオブジェクトの UI Circle** を入れる
   - Hierarchy から `FrameCapture` 自身をドラッグしてもよい

> **ここまでで**: Scene ビューでメモボタンが**うっすら白い正円**になる。
> ギザギザが消えているはず。

---

## 手順3. 詳細ボタンを丸くする

`Canvas / BottomBar / CSVButton` を選び、**手順2とまったく同じ**ことをする。
値も同じ（Roundness `1` / Ring Width `0` / Color `FFFFFF2E`）。

> **ここまでで**: 左右2つの小さい丸が揃う。

---

## 手順4. 録画ボタンを白いリングにする

`Canvas / BottomBar / FrameRecord` を選ぶ。

### 4-1. Image を外して UICircle を足す

手順2-1・2-2と同じ。

### 4-2. 値を入れる

| 項目 | 値 |
|---|---|
| Color | Hexadecimal に **`FFFFFFF2`**（白 / A=242） |
| Roundness | `1` |
| **Ring Width** | **`0.055`** |
| Softness | `1.2` |

### 4-3. Target Graphic を直す

手順2-4と同じ。

> **ここまでで**: 太さのある**白い輪**になる。中は空。

---

## 手順5. 録画ボタンの赤い中身

`Canvas / BottomBar / FrameRecord / Inner` を選ぶ。

### 5-1. Image を外して UICircle を足す

手順2-1・2-2と同じ。**このオブジェクトには Button が無い**ので、
Target Graphic の直しは不要。

### 5-2. 値を入れる

| 項目 | 値 |
|---|---|
| Color | Hexadecimal に **`EF4B45FF`** |
| **Raycast Target** | **オフ**（チェックを外す） |
| Roundness | `1` |
| Ring Width | `0` |
| Softness | `1.2` |

> **Raycast Target を切り忘れると、内側の赤丸がタップを吸って
> 録画ボタンが反応しなくなる。** ここは必ず確認する。

> **ここまでで**: 白い輪の中に赤い丸。カメラアプリと同じ見た目になる。

---

## 手順6. 「録画」の文字を消す

`Canvas / BottomBar / FrameRecord / Text (TMP)` を選び、右クリック → **Delete**。

赤い丸の上に文字が乗っていると、丸→四角の変形が見えにくい。

> **ここまでで**: 録画ボタンが赤丸だけになる。

---

## 手順7. CaptureButtons をつなぎ直す

`DebugUI` を選ぶ。`CaptureButtons` の項目が変わっているので入れ直す。

| 項目 | 入れるもの | どこから |
|---|---|---|
| Capture | `DebugUI` | Hierarchy からドラッグ |
| **Record Inner** | `Inner` の **UI Circle** | `FrameRecord/Inner` をドラッグ |
| **Record Inner Rect** | `Inner` の RectTransform | 同じものをドラッグ |
| Note Label | `FrameCapture / Text (TMP)` | ドラッグ |

`Circle Sprite` と `Rounded Sprite` の欄は無くなっている。

その下の数値は既定のままでよい。

| 項目 | 既定 | 意味 |
|---|---|---|
| Idle Size | `150` | 待機中の赤丸の大きさ |
| Recording Size | `78` | 録画中の四角の大きさ |
| Idle Roundness | `1` | 待機中は真円 |
| Recording Roundness | `0.3` | 録画中は角丸の四角 |
| Transition Seconds | `0.18` | 変形にかける時間 |

> **ここまでで**: Play すると、録画ボタンを押したとき赤丸が
> **小さな角丸の四角になめらかに変形する**。もう一度押すと丸に戻る。

---

## 手順8. 背景パネルを消して、文字に影をつける

ここが見た目にいちばん効く。

### 8-1. パネルを消す

`Canvas / StatusBar` を選ぶ。

1. Inspector の **Image**
2. **Color** をクリック
3. Hexadecimal に **`00000000`**（完全透明）

これで文字が映像の上に直接乗る。**この時点では読みにくいはず。**次で直す。

### 8-2. 文字に影をつける

`Canvas / StatusBar / Text (TMP)` を選ぶ。

1. Inspector の **TextMeshPro - Text (UI)** の一番下、**Material** の行を開く
2. **Underlay** の見出しをクリックして開く
3. 次を入れる

| 項目 | 値 |
|---|---|
| Underlay Type | **Normal** |
| Underlay Color | 黒 / A = `0.7`（Hexadecimal `000000B3`） |
| Offset X | `0` |
| Offset Y | `0` |
| Dilate | `0.1` |
| Softness | `0.3` |

> Offset を 0 にして Dilate と Softness で広げると、**文字のまわりに
> ぼんやりした暗がり**ができる。落ち影ではないので、どんな背景でも読める。
> Google レンズも同じ手を使っている。

> **注意**: これは Material の設定なので、同じマテリアルを使う他の文字にも
> 影響する。ボタンの文字まで影が付いてしまう場合は、
> Material の右の歯車 → **Create Material Preset** で複製してから設定する。

> **ここまでで**: 黒い四角が消えて、映像の上に文字が浮く。
> 画面が一気に広く見えるはず。

---

## 手順9. ボタンの文字を整える

### 9-1. メモボタン

`FrameCapture / Text (TMP)`

| 項目 | 値 |
|---|---|
| Font Size | `40` |
| Color | `FFFFFFE6` |
| Alignment | 中央・中央 |

文字の中身は `CaptureButtons` が上書きするので何でもよい。

### 9-2. 詳細ボタン

`CSVButton / Text (TMP)`

| 項目 | 値 |
|---|---|
| Text | `i` |
| Font Size | `48` |
| Color | `FFFFFFE6` |
| Alignment | 中央・中央 |

---

## 手順10. Record ボタンの位置

`Canvas / Record`（CSVロガーの録画。§6-1の検証で使うので消さない）

いま画面のいちばん下（Pos Y `-2417`）にあり、機種によっては
ジェスチャーバーと重なる。左上へ動かす。

| 項目 | 値 |
|---|---|
| Anchor | 左上（min `0, 1` / max `0, 1`） |
| Pos X / Pos Y | `120` / `-120` |
| Width × Height | `160` × `56` |

`StatusBar` と重なるが、`Record` のほうが先に描かれる（背面）ので
`StatusBar` の文字が上に出る。気になるなら `StatusBar` の Pos Y を
`-200` まで下げる。

---

## 最後に確認すること

Play して、次を順に見る。

| | 見るところ | 期待 |
|---|---|---|
| 1 | ボタンの輪郭 | ギザギザが無い |
| 2 | 録画ボタン | 白い輪＋赤い丸 |
| 3 | 録画を押す | 赤丸が角丸の四角へなめらかに変形 |
| 4 | もう一度押す | 丸に戻る |
| 5 | メモを押す | `-` → `1F` → `2F` … と変わり、色が黄色になる |
| 6 | `i` を押す | チェック項目と数値が出る／消える |
| 7 | 画面上部 | 黒い四角が無く、文字が読める |
| 8 | 粒 | 白っぽく出て、点滅しない |

3が動かない場合は手順7の Record Inner / Record Inner Rect を確認する。
5が動かない場合は Note Label を確認する。

---

## それでも輪郭がギザギザなら

シェーダが効いていない。次の順で確かめる。

1. `UICircle` の Inspector に **Roundness / Ring Width / Softness** の3項目が
   出ているか（出ていなければ古いスクリプトが残っている）
2. Console に「シェーダ ARPointCloud/UICircle が見つかりません」が出ていないか
3. 出ていれば手順1のシェーダ登録をやり直す

## 円ではなく四角が出るなら

`Image` が残っている。`UICircle` と同居はできないので、
どちらか一方しか効いていない状態になっている。Image を消す。
