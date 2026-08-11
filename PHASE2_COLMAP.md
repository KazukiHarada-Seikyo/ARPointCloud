# フェーズ2 — 点群生成の手順（RTX 5070機）

対象: 撮影済みの `rec_YYYYMMDD_HHMMSS` フォルダから、実寸付きの点群を作る

---

## 何をしているのか

ふつうの写真測量（COLMAPの標準的な使い方）は、写真だけを見て

1. カメラがどこにあったかを推定する（SfM）
2. その姿勢をもとに立体を作る（MVS）

の2段構えで進む。ただし1で得られる姿勢には**実寸がない**。倍率が丸ごと不定で、10cmの箱か10mの建物かを写真からは決められない。

このプロジェクトでは**1をARCoreが済ませている**。ARCoreの姿勢はメートル実寸なので、それを渡してしまえば倍率の問題が最初から起きない。**ここがこの手法の肝**で、マーカーも基準点も要らない理由になっている。

だから手順は「姿勢を教えて、三角測量から始めてもらう」形になる。

---

## 前提

- COLMAP（CUDA有効版）。密な点群を作る段階でGPUを使う
- Python 3.8以上。**python.org版を使うこと**（Unity同梱のものは `sqlite3` が欠けている）
- 画像と中間ファイルはこの機械に置く。開発機（Ultra 5）には置かない

以下は `rec_20260811_091935` を例にする。パスは自分の環境に読み替える。

```bat
set REC=D:\Captures\rec_20260811_091935
set WORK=D:\work\rec_20260811_091935
mkdir %WORK%
```

---

## 手順

### 1. 姿勢をCOLMAPの形式に変換する

```bat
python tools\frames_to_colmap.py %REC%
```

`%REC%\sparse\` に4つできる。

| ファイル | 中身 |
|---|---|
| `cameras.txt` | 内部パラメータ（焦点距離と主点） |
| `images.txt` | 1枚ごとの姿勢 |
| `points3D.txt` | 空。これから作るので中身は無い |
| `image_list.txt` | 使う画像の一覧 |

**画面に出る `det(R)` が 1.0 であることを必ず確認する。** ここが -1 になっていると点群が鏡像になる。

ブレの強いコマを落としたいときは角速度で切れる。まずは全部使って、結果を見てから判断する。

```bat
python tools\frames_to_colmap.py %REC% --max-angular-speed 40
```

### 2. 特徴点を抽出する

`--ImageReader.camera_params` には、手順1で画面に出た値をそのまま入れる。ここが `cameras.txt` と食い違うと後で破綻する。

```bat
colmap feature_extractor ^
  --database_path %WORK%\database.db ^
  --image_path %REC% ^
  --image_list_path %REC%\sparse\image_list.txt ^
  --ImageReader.camera_model PINHOLE ^
  --ImageReader.single_camera 1 ^
  --ImageReader.camera_params "1388.773,1389.176,968.662,536.514"
```

`--image_list_path` を渡しているので、`frames.csv` などの画像でないファイルは自動的に対象外になる。

### 3. 画像IDを合わせて姿勢を書き直す

**この手順を飛ばすと次で必ず失敗する。**

COLMAPは手順2で画像に内部IDを振る。手順1で書いた `images.txt` は1から順に振っているだけなので、両者は一致しない。データベースから正しいIDを読んで書き直す。

```bat
python tools\frames_to_colmap.py %REC% --database %WORK%\database.db
```

### 4. 画像どうしを突き合わせる

連続撮影なので、時間的に近いコマだけを比べる `sequential_matcher` を使う。総当たり（`exhaustive_matcher`）は286枚だと現実的な時間で終わらない。

```bat
colmap sequential_matcher ^
  --database_path %WORK%\database.db ^
  --SequentialMatching.overlap 10 ^
  --SequentialMatching.quadratic_overlap 1
```

`quadratic_overlap` を有効にすると、10枚先だけでなく20枚先、40枚先とも比べる。同じ場所へ戻ってきたときに繋がりやすくなる。

### 5. 三角測量（姿勢は推定させない）

`mapper` ではなく `point_triangulator` を使う。姿勢は既知として動かさず、3次元点だけを起こす。

```bat
colmap point_triangulator ^
  --database_path %WORK%\database.db ^
  --image_path %REC% ^
  --input_path %REC%\sparse ^
  --output_path %WORK%\sparse_tri
```

ここまでで**疎な点群**ができる。実寸はARCore由来なので、この時点でメートルになっている。

### 6. 密な点群にする（GPU）

```bat
colmap image_undistorter --image_path %REC% --input_path %WORK%\sparse_tri --output_path %WORK%\dense
colmap patch_match_stereo --workspace_path %WORK%\dense
colmap stereo_fusion --workspace_path %WORK%\dense --output_path %WORK%\dense\fused.ply
```

`fused.ply` が成果物。これをフェーズ3で座標変換してLASにする。

---

## うまくいかないときの見どころ

**点がほとんど起きない**
姿勢が合っていない可能性が高い。まず `det(R)` を確認する。次に、`colmap gui` で `%WORK%\sparse_tri` を開き、カメラの並びが実際に歩いた軌跡の形をしているか目で見る。

**点は起きるが形が崩れる**
ARCoreのトラッキングが途中でリセットされている疑い。`frames.csv` の `session_state` 列を見て、`SessionTracking` 以外が混じっていないか確認する。混じっていればその前後で座標系が別物になっている。

**姿勢を信じずにやり直したい**
`point_triangulator` の代わりに `mapper` を使えば、COLMAPが姿勢から推定し直す。ただし実寸が失われるので、比較用と割り切る。

**内部パラメータのばらつきが大きいと言われた**
ピントが動いている。`point_triangulator` に `--Mapper.ba_refine_focal_length 1` を付けて最適化させる。

---

## 分かっている限界（フェーズ4以降の宿題）

- **レンズ歪みを与えていない。** AR Foundationは歪み係数を出さないので `PINHOLE`（歪みなし）で扱っている。広角側の直線が曲がる分は誤差として残る
- **ローリングシャッター。** 1枚の中で上と下で撮影時刻がずれる（読み出し最大35ms）。歩きながらだと像が斜めに歪む。`angular_speed_deg_s` の大きいコマほど影響が出る
- **遡り補正。** ARCoreはセッション途中で過去の姿勢を修正する。撮影の前半と後半で座標系が微妙に別物になっている可能性がある（ROADMAP §6-3）
