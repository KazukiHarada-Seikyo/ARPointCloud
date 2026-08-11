#!/usr/bin/env python3
"""
frames.csv (ARCoreの姿勢付き連番JPEG) を COLMAP のテキストモデルに変換する。

  python tools/frames_to_colmap.py Captures/rec_20260811_091935

出力: 入力フォルダの下に sparse/ を作り、次の3つを書く
  cameras.txt   カメラの内部パラメータ
  images.txt    1枚ごとの姿勢 (world-to-camera)
  points3D.txt  空 (COLMAPが要求するので置くだけ)

これで「姿勢は既知、3次元点はこれから」という状態のモデルになる。
COLMAPはSfM(姿勢推定)を飛ばして、三角測量から始められる。

依存なし(標準ライブラリのみ)。点群生成機にnumpyを入れなくても動く。

--------------------------------------------------------------------
座標系について (ここを間違えると点群が鏡像になる)

                Unity / ARCore        COLMAP
  世界座標      左手系                 右手系
  カメラのY軸   上                     下
  姿勢の向き    カメラを世界に置く     世界の点をカメラから見る(逆向き)

左手系→右手系の反転と、カメラY軸の反転。反転が2回なので、
掛け合わせると行列式が +1 に戻り、まっとうな回転になる。
片方だけ直すと鏡像になる。

  S = diag(1, 1, -1)   世界: 左手 → 右手
  C = diag(1, -1, 1)   カメラ: Yを上から下へ
  R_c2w = S * R_unity * C
  R_w2c = R_c2w の転置
  T     = -R_w2c * (S * 位置)
--------------------------------------------------------------------
"""

import argparse
import csv
import math
import os
import statistics
import sys

# sqlite3 は --database を使うときだけ読み込む。
# Unity同梱(NDK)のPythonには実体が入っていないため、
# 変換だけしたい場面で落ちないようにしておく


# ----------------------------------------------------------------------
# 座標変換
# ----------------------------------------------------------------------

def quat_to_axes(x, y, z, w):
    """Unityのクォータニオンから、カメラの right / up / forward を取り出す。

    回転行列の各列が、そのままカメラの3軸を世界座標で表したものになる。
    """
    right = (
        1 - 2 * (y * y + z * z),
        2 * (x * y + z * w),
        2 * (x * z - y * w),
    )
    up = (
        2 * (x * y - z * w),
        1 - 2 * (x * x + z * z),
        2 * (y * z + x * w),
    )
    forward = (
        2 * (x * z + y * w),
        2 * (y * z - x * w),
        1 - 2 * (x * x + y * y),
    )
    return right, up, forward


def unity_pose_to_colmap(px, py, pz, qx, qy, qz, qw):
    """Unityの姿勢を、COLMAPの world-to-camera (四元数と平行移動) に変換する。

    戻り値: (qw, qx, qy, qz), (tx, ty, tz)
    """
    right, up, forward = quat_to_axes(qx, qy, qz, qw)

    def s(v):
        """左手系 → 右手系 (Zを反転)"""
        return (v[0], v[1], -v[2])

    def neg(v):
        return (-v[0], -v[1], -v[2])

    # R_c2w の各列。cy はカメラY軸を下向きにするため符号を反転する
    cx = s(right)
    cy = neg(s(up))
    cz = s(forward)

    # 転置すると world-to-camera。行がそのまま cx, cy, cz になる
    r = (cx, cy, cz)

    # カメラ中心も右手系へ
    center = (px, py, -pz)

    # T = -R_w2c * center
    t = tuple(
        -(r[i][0] * center[0] + r[i][1] * center[1] + r[i][2] * center[2])
        for i in range(3)
    )

    return matrix_to_quat(r), t


def matrix_to_quat(r):
    """回転行列 (行のタプル3つ) から四元数 (w, x, y, z) を作る。

    対角和が負のときに桁落ちするので、いちばん大きい成分から求める
    (Shepperdの方法)。
    """
    m00, m01, m02 = r[0]
    m10, m11, m12 = r[1]
    m20, m21, m22 = r[2]
    trace = m00 + m11 + m22

    if trace > 0:
        s_ = math.sqrt(trace + 1.0) * 2
        w = 0.25 * s_
        x = (m21 - m12) / s_
        y = (m02 - m20) / s_
        z = (m10 - m01) / s_
    elif m00 > m11 and m00 > m22:
        s_ = math.sqrt(1.0 + m00 - m11 - m22) * 2
        w = (m21 - m12) / s_
        x = 0.25 * s_
        y = (m01 + m10) / s_
        z = (m02 + m20) / s_
    elif m11 > m22:
        s_ = math.sqrt(1.0 + m11 - m00 - m22) * 2
        w = (m02 - m20) / s_
        x = (m01 + m10) / s_
        y = 0.25 * s_
        z = (m12 + m21) / s_
    else:
        s_ = math.sqrt(1.0 + m22 - m00 - m11) * 2
        w = (m10 - m01) / s_
        x = (m02 + m20) / s_
        y = (m12 + m21) / s_
        z = 0.25 * s_

    n = math.sqrt(w * w + x * x + y * y + z * z)
    return (w / n, x / n, y / n, z / n)


def determinant(r):
    """検算用。1.0 でなければ鏡像になっている"""
    return (
        r[0][0] * (r[1][1] * r[2][2] - r[1][2] * r[2][1])
        - r[0][1] * (r[1][0] * r[2][2] - r[1][2] * r[2][0])
        + r[0][2] * (r[1][0] * r[2][1] - r[1][1] * r[2][0])
    )


# ----------------------------------------------------------------------
# 本体
# ----------------------------------------------------------------------

def load_rows(csv_path):
    with open(csv_path, newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))


def to_float(s):
    return float(s) if s not in ("", None) else None


def main():
    ap = argparse.ArgumentParser(
        description="frames.csv を COLMAP のテキストモデルに変換する")
    ap.add_argument("rec_dir",
                    help="rec_YYYYMMDD_HHMMSS フォルダ (frames.csv とJPEGが入っている)")
    ap.add_argument("--max-angular-speed", type=float, default=None,
                    metavar="DEG_S",
                    help="この角速度(度/秒)を超えるコマを除く。ブレの強いコマを落とす用")
    ap.add_argument("--step", type=int, default=1,
                    help="Nコマに1枚だけ使う (既定 1 = 全部)")
    ap.add_argument("--out", default=None,
                    help="出力先 (既定: <rec_dir>/sparse)")
    ap.add_argument("--database", default=None, metavar="DB",
                    help="COLMAPのdatabase.db。画像IDをこちらに合わせる。"
                         "feature_extractor を先に走らせた場合は必ず指定すること")
    args = ap.parse_args()

    rec_dir = os.path.abspath(args.rec_dir)
    csv_path = os.path.join(rec_dir, "frames.csv")
    if not os.path.exists(csv_path):
        sys.exit(f"frames.csv がありません: {csv_path}")

    out_dir = args.out or os.path.join(rec_dir, "sparse")
    os.makedirs(out_dir, exist_ok=True)

    rows = load_rows(csv_path)
    print(f"読み込み {len(rows)} 行")

    # --- 選別 ---------------------------------------------------------
    kept = []
    skipped_tracking = 0
    skipped_blur = 0
    skipped_missing = 0

    for i, r in enumerate(rows):
        # 追跡が成立していないコマは姿勢が信用できない
        if r.get("session_state") != "SessionTracking":
            skipped_tracking += 1
            continue

        # 内部パラメータが取れていない行は使えない
        if not r.get("fx"):
            skipped_missing += 1
            continue

        if args.max_angular_speed is not None:
            a = to_float(r.get("angular_speed_deg_s"))
            if a is not None and a > args.max_angular_speed:
                skipped_blur += 1
                continue

        kept.append(r)

    if args.step > 1:
        kept = kept[::args.step]

    if not kept:
        sys.exit("使えるコマが1枚も残りませんでした")

    print(f"採用 {len(kept)} 枚"
          f" (追跡外 {skipped_tracking} / 内部パラメータ無し {skipped_missing}"
          f" / ブレ {skipped_blur})")

    # --- 画像ID ---------------------------------------------------------
    # COLMAPは特徴点抽出のときに画像へIDを振る。こちらで勝手に1から振ると
    # point_triangulator でIDが食い違って通らない。DBがあれば必ず合わせる
    db_ids = None
    if args.database:
        if not os.path.exists(args.database):
            sys.exit(f"データベースがありません: {args.database}")
        try:
            import sqlite3
        except ImportError:
            sys.exit("このPythonには sqlite3 が入っていません。"
                     "python.org版などを使ってください")
        con = sqlite3.connect(args.database)
        db_ids = {name: image_id
                  for image_id, name in con.execute(
                      "SELECT image_id, name FROM images")}
        con.close()
        print(f"データベースから画像ID {len(db_ids)} 件を読み込み")

        missing = [r["filename"] for r in kept if r["filename"] not in db_ids]
        if missing:
            print(f"  ※ DBに無い画像が {len(missing)} 枚。この分は書き出さない"
                  f" (例: {missing[0]})")
            kept = [r for r in kept if r["filename"] in db_ids]
            if not kept:
                sys.exit("DBと突き合わせた結果、使えるコマが残りませんでした")

    # --- 内部パラメータ -----------------------------------------------
    # フォーカスと再校正で毎コマ動きうるので、ばらつきを見てから代表値を決める
    fxs = [float(r["fx"]) for r in kept]
    fys = [float(r["fy"]) for r in kept]
    cxs = [float(r["cx"]) for r in kept]
    cys = [float(r["cy"]) for r in kept]

    spread = max(fxs) - min(fxs)
    fx, fy = statistics.median(fxs), statistics.median(fys)
    cx, cy = statistics.median(cxs), statistics.median(cys)

    print(f"内部パラメータ(中央値) fx={fx:.3f} fy={fy:.3f} cx={cx:.3f} cy={cy:.3f}")
    print(f"  fx のばらつき {spread:.3f} px")
    if spread > 5.0:
        print("  ※ ばらつきが大きい。ピントが動いている可能性があるので、"
              "COLMAP側で内部パラメータを最適化させること")

    width = int(kept[0]["img_w"])
    height = int(kept[0]["img_h"])

    # --- cameras.txt --------------------------------------------------
    with open(os.path.join(out_dir, "cameras.txt"), "w", encoding="utf-8") as f:
        f.write("# Camera list with one line of data per camera:\n")
        f.write("#   CAMERA_ID, MODEL, WIDTH, HEIGHT, PARAMS[]\n")
        f.write("# Number of cameras: 1\n")
        # PINHOLE = 歪みなし。AR Foundationは歪み係数を出さないのでこれを使う
        f.write(f"1 PINHOLE {width} {height} {fx:.6f} {fy:.6f} {cx:.6f} {cy:.6f}\n")

    # --- images.txt ---------------------------------------------------
    det_min, det_max = 9.0, -9.0

    with open(os.path.join(out_dir, "images.txt"), "w", encoding="utf-8") as f:
        f.write("# Image list with two lines of data per image:\n")
        f.write("#   IMAGE_ID, QW, QX, QY, QZ, TX, TY, TZ, CAMERA_ID, NAME\n")
        f.write("#   POINTS2D[] as (X, Y, POINT3D_ID)\n")
        f.write(f"# Number of images: {len(kept)}, mean observations per image: 0\n")

        for seq, r in enumerate(kept, start=1):
            image_id = db_ids[r["filename"]] if db_ids else seq

            px, py, pz = (float(r["local_px"]), float(r["local_py"]),
                          float(r["local_pz"]))
            qx, qy, qz, qw = (float(r["local_qx"]), float(r["local_qy"]),
                              float(r["local_qz"]), float(r["local_qw"]))

            # 検算用に行列式も見ておく
            right, up, forward = quat_to_axes(qx, qy, qz, qw)
            rr = ((right[0], right[1], -right[2]),
                  (-up[0], -up[1], up[2]),
                  (forward[0], forward[1], -forward[2]))
            d = determinant(rr)
            det_min, det_max = min(det_min, d), max(det_max, d)

            (w_, x_, y_, z_), (tx, ty, tz) = unity_pose_to_colmap(
                px, py, pz, qx, qy, qz, qw)

            f.write(f"{image_id} {w_:.9f} {x_:.9f} {y_:.9f} {z_:.9f} "
                    f"{tx:.9f} {ty:.9f} {tz:.9f} 1 {r['filename']}\n")
            f.write("\n")   # 2次元点はまだ無いので空行

    # --- points3D.txt -------------------------------------------------
    with open(os.path.join(out_dir, "points3D.txt"), "w", encoding="utf-8") as f:
        f.write("# 3D point list with one line of data per point:\n")
        f.write("#   POINT3D_ID, X, Y, Z, R, G, B, ERROR, TRACK[] "
                "as (IMAGE_ID, POINT2D_IDX)\n")
        f.write("# Number of points: 0, mean track length: 0\n")

    # --- image_list.txt -----------------------------------------------
    # feature_extractor に --image_list_path で渡す。
    # 選別した分だけを対象にでき、frames.csv などの非画像ファイルも避けられる
    list_path = os.path.join(out_dir, "image_list.txt")
    with open(list_path, "w", encoding="utf-8") as f:
        for r in kept:
            f.write(r["filename"] + "\n")

    print(f"\n出力 {out_dir}")
    print(f"  cameras.txt / images.txt / points3D.txt / image_list.txt")
    print(f"検算 det(R) = {det_min:.7f} .. {det_max:.7f}  (1.0 なら鏡像になっていない)")
    if det_min < 0.99 or det_max > 1.01:
        print("  ※ 1.0 から外れている。座標変換を見直すこと")


if __name__ == "__main__":
    main()
