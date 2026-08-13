# 5070機への引き継ぎ手順

開発機（Core Ultra 5 / Intel Arc）から、点群生成機（RTX 5070）へ移す。

---

## なぜ移すのか

当初は「疎な点群は開発機のCPUでできる、CUDA必須なのは密な点群だけ」と
書いていた。**286枚・特徴点6,168点ならそのとおりだったが、
2,732枚・12,000点では成り立たない。**

実測（開発機・CPU）

| 工程 | 286枚（屋内） | 2,732枚（屋外） |
|---|---|---|
| 特徴点抽出 | 0.7分 | 12.1分 |
| 突き合わせ | 3.7分 | **約160分（推定）** |
| 1枚あたり | 0.78秒 | **3.6秒** |

枚数は9.6倍だが、時間は43倍になっている。

**原因は特徴点の数**。屋外は模様が豊富で1枚12,000〜13,500点出る
（屋内は6,168点）。突き合わせの計算量は特徴点の数のほぼ2乗で効くので、
2倍の特徴点で約4倍の時間になる。

**この処理はGPUがいちばん得意**（数千次元の記述子どうしの総当たり比較）。
COLMAPには抽出・突き合わせの両方にCUDA実装がある。

開発機では**原理的に使えない**。入れてあるのは `nocuda` ビルドで、
Intel Arc は CUDA 非対応。

---

## 5070機に用意するもの

### 1. COLMAP（CUDA版）

https://github.com/colmap/colmap/releases から
**`colmap-x64-windows-cuda.zip`**（約359MB）を取得して展開する。

> 開発機に入れたのは `nocuda`（120MB）。5070機では **cuda** を使う。

### 2. Python

python.org 版（3.8以上）。**Unity同梱のものは `sqlite3` が欠けている**ので不可。

```
winget install Python.Python.3.12
```

### 3. このリポジトリ

```
git clone https://github.com/KazukiHarada-Seikyo/ARPointCloud.git
```

必要なのは `tools/` だけ。Unityプロジェクトは要らない。

### 4. 撮影データ

`Captures/files/rec_20260812_111139/`（写真2,732枚 + frames.csv、約1.4GB）

**gitには乗らない**ので、USBか共有フォルダで運ぶ。

---

## 手順

以下は `rec_20260812_111139` の例。

```bat
set REC=D:\Captures\rec_20260812_111139
set WORK=D:\work\rec_20260812_111139
mkdir %WORK%
```

### 1. 姿勢を変換する

```bat
python tools\frames_to_colmap.py %REC%
```

`det(R)` が 1.0 であることを確認する。
「視線軸まわりの回転: Portrait→90度」と出れば正しい。

### 2. 特徴点抽出（GPU）

**開発機と違うのは `use_gpu` を 1 にすること**（既定値なので省略可）。

```bat
colmap feature_extractor ^
  --database_path %WORK%\database.db ^
  --image_path %REC% ^
  --image_list_path %REC%\sparse\image_list.txt ^
  --ImageReader.camera_model PINHOLE ^
  --ImageReader.single_camera 1 ^
  --ImageReader.camera_params "1388.609,1387.241,969.972,534.586"
```

内部パラメータの4つの数字は、手順1の画面に出た値をそのまま入れる。
**この撮影では全2,732枚で完全に一定**だった（屋外でピントが動かなかった）。

### 3. 画像IDを合わせる

**飛ばすと次で必ず失敗する。**

```bat
python tools\frames_to_colmap.py %REC% --database %WORK%\database.db
```

### 4. 突き合わせ（GPU）

```bat
colmap sequential_matcher ^
  --database_path %WORK%\database.db ^
  --SequentialMatching.overlap 10 ^
  --SequentialMatching.quadratic_overlap 1
```

### 5. 三角測量

```bat
colmap point_triangulator ^
  --database_path %WORK%\database.db ^
  --image_path %REC% ^
  --input_path %REC%\sparse ^
  --output_path %WORK%\sparse_tri
```

ここまでで**疎な点群**ができる。実寸はARCore由来なのでメートル。

### 6. 密な点群（ここからが5070機の本領）

```bat
colmap image_undistorter --image_path %REC% --input_path %WORK%\sparse_tri --output_path %WORK%\dense
colmap patch_match_stereo --workspace_path %WORK%\dense
colmap stereo_fusion --workspace_path %WORK%\dense --output_path %WORK%\dense\fused.ply
```

> **容量に注意**。`image_undistorter` は画像を展開し直すので、
> 元の1.4GBに加えて同程度以上を使う。深度マップも大きい。
> **20GB以上の空きを見ておく。**

### 7. LASにする

```bat
python tools\points_to_las.py %WORK%\dense\fused.ply %REC% --zone 7
```

`--zone 7` は平面直角座標系VII系（名古屋）。
出力は `%REC%\pointcloud_zone7.las`（EPSG:6675、X=東 / Y=北）。

`UnityLasImporter` でそのまま読める形式。

---

## 間引きについて

開発機では時間短縮のため `--step 3`（3枚に1枚、911枚）で回した。

**5070機では間引かずに全2,732枚で回してよい。** GPUなら現実的な時間で終わる。

ただし品質面では、間引きに大きな損はない。撮影は8.7fps・歩行速度
秒速0.43mなので、**隣り合うコマの間隔は約5cm**。基線長として小さすぎ、
三角測量にほとんど寄与しない。3枚に1枚（15cm）のほうが条件が良いくらい。

両方回して比べれば、記事のネタになる。

---

## この撮影データについて

`rec_20260812_111139`（2026-08-12、住宅地）

| 項目 | 値 |
|---|---|
| 枚数 | 2,732枚（CSVと完全一致） |
| 時間 | 314秒 |
| 経路長 | 136.1 m |
| 広がり | 対角 49.0 m |
| トラッキング断絶 | **なし**（全行 SessionTracking） |
| 方位精度 | 中央値 4.74度（5度以下が66.8%） |
| 水平精度 | 中央値 2.36 m |
| 角速度 | 中央値 17.9度/秒（60度超は0.5%） |
| 内部パラメータ | 全行一定（fx=1388.609） |

**VPSが成立した状態で撮れている。** 屋内実測（方位16.5度）とは別物。

### 実寸の検証結果

局所座標（加速度計由来）と緯度経度（VPS由来）は出どころがまったく別。
両者の距離を比べた。

| 比較 | 局所 | VPS | 比 |
|---|---|---|---|
| 区間ごと（中央値） | — | — | **1.0010** |
| 合計経路長 | 112.78 m | 112.03 m | 0.993 |

**スケール差 +0.10%。** ARCoreの実寸が独立な測定で裏付けられた。
`THEORY.md` §11 の主張を実証したデータになる。

始点→終点の直線距離は 局所14.21 m / VPS 12.67 m で1.54mずれるが、
VPSの水平精度が2.36mなので想定内。**短区間の相対距離は正確、
絶対位置は数m級で揺れる**という、精度値どおりの挙動。

---

## 密な点群が終わらない・落ちるとき

2,732枚を丸ごと `patch_match_stereo` に流すと、素直にやると何時間もかかります。
止まっているのか進んでいるのか分からないときは、上から順に潰してください。

### 1. まずGPUが使えているか

RTX 5070 は Blackwell 世代（`sm_120`）です。**COLMAPのCUDA部分が
古いアーキテクチャ向けにビルドされていると、実行時に落ちます。**

```
no kernel image is available for execution on the device
```

これが出たら、CUDA 12.8以降でビルドされたCOLMAPが要ります。
公式の配布版が対応していない時期があるので、確認してください。

```bat
nvidia-smi                          :: ドライバとCUDAの版
colmap patch_match_stereo --help    :: CUDA無しビルドだとここで分かる
```

### 2. 枚数を減らす

`patch_match_stereo` は1枚ずつ処理します。**枚数に比例して時間がかかります。**
2,732枚は多すぎます。3枚に1枚（911枚）でも点群の密度はさほど落ちません。

```bat
python toolsrames_to_colmap.py %REC% --stride 3
```

### 3. 解像度を下げる

GPUメモリが足りないときはここです。既定は元画像のままなので、
1920×1080 × 多数の隣接画像をキャッシュに載せようとします。

```bat
colmap patch_match_stereo --workspace_path %WORK%\dense ^
  --PatchMatchStereo.max_image_size 1600 ^
  --PatchMatchStereo.cache_size 32
```

`max_image_size` を 1600 → 1200 → 1000 と下げると、
メモリも時間もはっきり減ります。点群は粗くなりますが、まず通すことが先です。

### 4. 途中経過を見る

`%WORK%\dense\stereo\depth_maps` に1枚ずつファイルが増えていきます。
**増えていれば進んでいます。** 増えないなら止まっています。

```bat
dir /b %WORK%\dense\stereo\depth_maps | find /c ".bin"
```

### 5. それでもだめなら

**疎な点群（121,846点）でも記事と成果物は成立します。**
密な点群は「あればより良い」もので、必須ではありません。
時間を使いすぎるようなら、いったん疎な点群で先に進めてください。

---

## 既知の落とし穴

| 症状 | 原因 |
|---|---|
| 円ではなく点がほぼ起きない | 姿勢の90度ずれ。`--camera-roll` を 0/90/180/270 で試す |
| point_triangulator が通らない | 手順3（画像ID合わせ）を飛ばした |
| CSVの1列目が読めない | 古いデータはBOM付き。`utf-8-sig` で開く（ツール側は対応済み） |
| PowerShellスクリプトが構文エラー | 日本語コメント入りはBOM付きで保存する（5.1はANSIとして読む） |
| bundle_adjuster が発散 | ゲージ自由度未固定＋トラック長2の点。`ROADMAP.md` §6-4 |
