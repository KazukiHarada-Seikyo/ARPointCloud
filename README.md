# ARPointCloud

**スマートフォン1台で、実寸と座標の付いた点群をつくる。**

Android端末（ARCore対応機）で連続撮影し、そのときのカメラ位置・向きを一緒に記録します。
PC側でその姿勢を使って点群を起こし、**平面直角座標系と標高**の LAS ファイルとして書き出します。

マーカーや基準尺を置かず、**端末の加速度計を長さの基準**としました。

```
撮る（Android）           →  点群にする（PC）        →  LAS
姿勢付き連番JPEG              COLMAP で三角測量          EPSG:66xx
frames.csv                    姿勢はARCoreから与える     X=東 / Y=北 / Z=標高
```

---

## できること・できないこと

**できること**

- スマホを持って歩くだけで、**メートル単位で正しい**点群がとれる
- 点群に**日本の平面直角座標系（19系すべて）と標高**が付く
- 出力は LAS 1.2。Unity向けのLASインポータや CloudCompare で開ける

**できないこと・限界**

- **絶対位置は数m級**です。点群の「形と大きさ」は正しくても、「地球上のどこに置くか」は
  VPS（ARCore Geospatial）任せで、水平2.4m・方位4.7度ほどの誤差が乗ります
- レンズ歪みを補正していません（AR Foundation が係数を返さないため）
- 測量成果ではありません。**遊びと学習と、そこそこの実用**が守備範囲です

---

## 精度（実測値）

**「精度」をひとつの数字にまとめていません。** 誤差源が別で、桁も別だからです。

### 相対精度 — 形と大きさ

| | 値 | 測り方 |
|---|---|---|
| 実寸（スケール） | **1%以内** | 173cmの巻尺を三角測量して172.6cm |

100m歩いて1m以内。**外部の物差しで検証した値**です。

### 絶対精度 — 置き場所と向き

| | 値（中央値） |
|---|---|
| 水平位置 | 2.36 m |
| 方位 | 4.74 度 |
| 高さ | 測位由来。約1.2mの偏り |

方位誤差は距離に比例して効きます。

| 基準点からの距離 | 方位4.74度によるずれ |
|---|---|
| 10 m | 0.83 m |
| 50 m | 4.15 m |
| 100 m | 8.30 m |

**この2つを混ぜて「精度◯m」と言わないでください。** 形はcm〜%級、置き場所はm級です。

詳しい測り方と考察は [PHASE4_ACCURACY.md](PHASE4_ACCURACY.md) に、
原理の解説は [THEORY.md](THEORY.md) にあります。

---

## 必要なもの

**撮る側**

- ARCore対応のAndroid端末（実測は Google Pixel 9a）
- Unity 6000.5.6f1 / AR Foundation 6.5.0 / ARCore XR Plugin 6.5.0 /
  ARCore Extensions 1.54.0（arf6ブランチ）
- Geospatial API を使うので Google Cloud の APIキーが要ります

**点群にする側**

- [COLMAP](https://colmap.github.io/)（密な点群まで作るなら CUDA 版）
- Python 3（**標準ライブラリのみ**。`tools/measure_scale.py` だけ Pillow が要ります）

---

## 使い方

### 1. 撮る

アプリを起動し、画面が「撮影できます」になってから録画します。
`frames.csv`（姿勢と地球座標）と連番JPEGが端末に貯まります。

```powershell
.\tools\pull.ps1 -Latest     # 端末からPCへ取り出す
```

現地での手順は [FIELD_CHECKLIST.md](FIELD_CHECKLIST.md) に。
**「方位を調整中」のまま撮り始めない**、**基準物のまわりは歩く**あたりが要点です。

### 2. 点群にする

```bat
python tools\frames_to_colmap.py <rec_dir>          :: 姿勢をCOLMAPの形式に
colmap feature_extractor ...                        :: 特徴点
colmap sequential_matcher ...                       :: 突き合わせ
colmap point_triangulator ...                       :: 三角測量（姿勢は固定）
```

手順の全文は [PHASE2_COLMAP.md](PHASE2_COLMAP.md)、
GPU機での密な点群は [HANDOFF_GPU.md](HANDOFF_GPU.md) にあります。

### 3. LASにする

```bat
python tools\check_dem.py <rec_dir>                 :: 正しいジオイド高を調べる
python tools\points_to_las.py <model> <rec_dir> --zone 7 --geoid 37.75
```

`--zone` は平面直角座標系の系番号（1〜19）。愛知県は7系です。

---

## ツール

| ファイル | 何をするか |
|---|---|
| `tools/frames_to_colmap.py` | `frames.csv` → COLMAPのテキストモデル。Unity左手系→COLMAP右手系の変換と、縦持ち撮影の視線軸まわりの回転補正 |
| `tools/points_to_las.py` | COLMAPの点群 or PLY → LAS 1.2。局所座標を1点ずつ緯度経度に戻してから投影する |
| `tools/jgd2011.py` | 緯度経度 ⇔ 平面直角座標（GRS80/JGD2011、全19系）。**国土地理院の変換サービスと0.1mm差で一致** |
| `tools/analyze_accuracy.py` | `frames.csv` から精度を測る。相対と絶対を分けて出す |
| `tools/check_dem.py` | 国土地理院のDEM・ジオイド高と突き合わせる |
| `tools/measure_scale.py` | 巻尺を撮った連番フレームから実寸を検証する |
| `tools/pull.ps1` | 端末から撮影データを取り出す |

`jgd2011.py` は単体でも使えます。日本の平面直角座標をPythonで扱いたいだけの人にどうぞ。

---

## 文書

| ファイル | 中身 |
|---|---|
| [ARTICLE.md](ARTICLE.md) | 解説記事。原理から実測まで一本にまとめたもの |
| [THEORY.md](THEORY.md) | 原理の解説。針穴写真機から三角測量、加速度計がスケールを与える物理まで、導出込み |
| [PHASE4_ACCURACY.md](PHASE4_ACCURACY.md) | 精度の実測と考察。**ARCore Geospatial が実際どう動いているかの実測を含む** |
| [PHASE2_COLMAP.md](PHASE2_COLMAP.md) | COLMAPの手順と実測値 |
| [HANDOFF_GPU.md](HANDOFF_GPU.md) | GPU機での密な点群 |
| [FIELD_CHECKLIST.md](FIELD_CHECKLIST.md) | 現地作業の手順 |
| [VFX_SCAN.md](VFX_SCAN.md) | 撮影画面のスキャン演出 |
| [ROADMAP.md](ROADMAP.md) / [STATUS.md](STATUS.md) | 全体像と現在地 |

---

## 実測した副産物

精度を測る過程で、ARCore の Geospatial API について分かったことがあります。
公式には書かれていない挙動なので、使う人の役に立つかもしれません。

- **返ってくる緯度経度は、連続した測位ではありません。** 区間の前半で決めた
  「局所座標→緯度経度」の変換を後半に外挿しても、105秒・34.7m歩いて2.7cmしかずれません。
  つまり区間内では局所座標に**固定の変換を掛けたもの**です
- **新しい測位情報が入るのは、VPSが解を組み直す瞬間だけ**です。314秒の撮影で1回でした。
  そのとき方位が3.17度回り、高さが0.50m付け替わりました
- **精度の自己申告値は、照合が入らない間じわじわ悪化します**（実測で毎分0.113度）。
  「現地に着いたら歩いて周囲を映してから読む」に根拠があります
- **楕円体高は測位で毎回求めた値ではありません。** 局所座標の上下動に定数を足したものです

根拠と測り方は [PHASE4_ACCURACY.md](PHASE4_ACCURACY.md) に。

---

## ライセンス

MIT License（[LICENSE](LICENSE)）。

## 注意

`ProjectSettings/ARCoreExtensionsProjectSettings.json` には Google Cloud の APIキーが
平文で入るため、`.gitignore` で除外しています。**フォークして使う場合は自分のキーを
用意してください。** キーには必ずアプリ制限（パッケージ名＋SHA-1）をかけてください。
