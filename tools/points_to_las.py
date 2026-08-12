#!/usr/bin/env python3
"""COLMAPの点群を、平面直角座標系+標高のLASファイルに変換する。

  python tools/points_to_las.py Captures/rec_.../work/sparse_tri Captures/rec_... --zone 7

入力は次のどちらか:
  COLMAPのモデルフォルダ (points3D.bin または points3D.txt)
  PLYファイル (stereo_fusion が出す fused.ply)

出力は LAS 1.2 / 点フォーマット2 (XYZ + 強度 + RGB)。
座標系は VLR の GeoTIFFキーに EPSG コードで書く。

依存なし(標準ライブラリのみ)。

--------------------------------------------------------------------
局所座標を地球上に置くまで

点群はARCoreの局所座標(起動地点が原点・メートル)にある。
これを平面直角座標に移すには、局所座標系が地球上でどこに・
どの向きで置かれているかが要る。それを与えるのが frames.csv の
地球座標の列。

  1. 各コマの eun_q* と local_q* から、局所座標系→EUN(東,上,北)の
     回転を求める。コマごとに1つ出るので、全部を平均して安定させる
  2. 基準点(緯度経度高)を決める。追跡できているコマの中央値を使う
  3. 各点を EUN → ENU → ECEF → 緯度経度高 と戻し、1点ずつ投影する

3で近似(平行移動だけ)を使わないのは、子午線収差があるため。
名古屋付近で約0.215度あり、30m先で11cmずれる。
--------------------------------------------------------------------

標高について

LASのZに書くのは標高(ジオイドからの高さ)。ARCoreが返すのは楕円体高。
差がジオイド高で、名古屋付近で約37m。

**現状は定数で引いているだけ。** 正確にやるには国土地理院のジオイドモデル
(GSIGEO2011)が要る。数十m四方の点群なら定数で足りるが、
広い範囲や高精度が要るときは作り直すこと。
--------------------------------------------------------------------
"""

import argparse
import csv
import math
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import jgd2011


# GRS80
WGS_A = 6378137.0
WGS_F = 298.257222101
WGS_E2 = 2.0 / WGS_F - 1.0 / (WGS_F * WGS_F)


# ----------------------------------------------------------------------
# 測地計算
# ----------------------------------------------------------------------

def geodetic_to_ecef(lat_deg, lon_deg, h):
    lat = math.radians(lat_deg)
    lon = math.radians(lon_deg)
    s = math.sin(lat)
    n = WGS_A / math.sqrt(1.0 - WGS_E2 * s * s)
    return ((n + h) * math.cos(lat) * math.cos(lon),
            (n + h) * math.cos(lat) * math.sin(lon),
            (n * (1.0 - WGS_E2) + h) * s)


def ecef_to_geodetic(x, y, z):
    """Bowringの方法。数十kmの範囲なら1回の反復で十分収束する"""
    lon = math.atan2(y, x)
    p = math.hypot(x, y)
    b = WGS_A * (1.0 - 1.0 / WGS_F)
    ep2 = (WGS_A * WGS_A - b * b) / (b * b)

    theta = math.atan2(z * WGS_A, p * b)
    lat = math.atan2(z + ep2 * b * math.sin(theta) ** 3,
                     p - WGS_E2 * WGS_A * math.cos(theta) ** 3)

    s = math.sin(lat)
    n = WGS_A / math.sqrt(1.0 - WGS_E2 * s * s)
    h = p / math.cos(lat) - n

    return math.degrees(lat), math.degrees(lon), h


def enu_to_ecef(e, n, u, lat0_deg, lon0_deg, x0, y0, z0):
    lat = math.radians(lat0_deg)
    lon = math.radians(lon0_deg)
    sl, cl = math.sin(lat), math.cos(lat)
    so, co = math.sin(lon), math.cos(lon)
    return (x0 - so * e - sl * co * n + cl * co * u,
            y0 + co * e - sl * so * n + cl * so * u,
            z0 + cl * n + sl * u)


# ----------------------------------------------------------------------
# クォータニオン (Unity左手系のまま扱う)
# ----------------------------------------------------------------------

def q_mul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def q_conj(q):
    return (-q[0], -q[1], -q[2], q[3])


def q_normalize(q):
    n = math.sqrt(sum(c * c for c in q))
    return tuple(c / n for c in q)


def q_to_matrix(q):
    """列が (右, 上, 前) になる回転行列。行のタプル3つで返す"""
    x, y, z, w = q
    return (
        (1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)),
        (2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)),
        (2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)),
    )


def q_average(quats):
    """符号を揃えてから成分ごとに平均する。

    厳密には固有ベクトルを解くべきだが、ばらつきが小さければ差は出ない。
    ばらつきは呼び出し側で残差として確認する。
    """
    ref = quats[0]
    acc = [0.0, 0.0, 0.0, 0.0]
    for q in quats:
        # 同じ回転を表す ±q のうち、基準に近い方を採る
        if sum(a * b for a, b in zip(q, ref)) < 0:
            q = tuple(-c for c in q)
        for i in range(4):
            acc[i] += q[i]
    return q_normalize(tuple(acc))


def q_angle_deg(a, b):
    d = abs(sum(x * y for x, y in zip(a, b)))
    d = min(1.0, d)
    return math.degrees(2.0 * math.acos(d))


# ----------------------------------------------------------------------
# 点群の読み込み
# ----------------------------------------------------------------------

# 点は (x, y, z, r, g, b, 再投影誤差, トラック長) の8つ組で持つ。
# 誤差とトラック長は絞り込みに使う。PLYには入っていないので None。

def read_colmap_points_bin(path):
    pts = []
    with open(path, "rb") as f:
        (num,) = struct.unpack("<Q", f.read(8))
        for _ in range(num):
            f.read(8)                                     # point id
            x, y, z = struct.unpack("<3d", f.read(24))
            r, g, b = struct.unpack("<3B", f.read(3))
            (err,) = struct.unpack("<d", f.read(8))
            (track_len,) = struct.unpack("<Q", f.read(8))
            f.read(8 * track_len)
            pts.append((x, y, z, r, g, b, err, track_len))
    return pts


def read_colmap_points_txt(path):
    pts = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            if line.startswith("#") or not line.strip():
                continue
            p = line.split()
            # POINT3D_ID X Y Z R G B ERROR TRACK[] as (IMAGE_ID, POINT2D_IDX)
            track_len = max(0, (len(p) - 8) // 2)
            pts.append((float(p[1]), float(p[2]), float(p[3]),
                        int(p[4]), int(p[5]), int(p[6]),
                        float(p[7]), track_len))
    return pts


def read_ply(path):
    """stereo_fusion が出す binary_little_endian の PLY を読む"""
    with open(path, "rb") as f:
        if f.readline().strip() != b"ply":
            sys.exit(f"PLYではありません: {path}")

        fmt = None
        count = 0
        props = []
        while True:
            line = f.readline().decode("ascii").strip()
            if line.startswith("format"):
                fmt = line.split()[1]
            elif line.startswith("element vertex"):
                count = int(line.split()[2])
            elif line.startswith("property") and count:
                props.append(line.split()[1:])
            elif line == "end_header":
                break

        if fmt != "binary_little_endian":
            sys.exit(f"未対応のPLY形式です: {fmt}"
                     " (binary_little_endian のみ対応)")

        sizes = {"float": ("f", 4), "float32": ("f", 4),
                 "double": ("d", 8), "float64": ("d", 8),
                 "uchar": ("B", 1), "uint8": ("B", 1),
                 "char": ("b", 1), "int8": ("b", 1),
                 "short": ("h", 2), "ushort": ("H", 2),
                 "int": ("i", 4), "uint": ("I", 4)}

        code = "<"
        names = []
        stride = 0
        for typ, name in props:
            if typ not in sizes:
                sys.exit(f"未対応のプロパティ型: {typ}")
            c, s = sizes[typ]
            code += c
            names.append(name)
            stride += s

        ix, iy, iz = names.index("x"), names.index("y"), names.index("z")
        has_rgb = all(c in names for c in ("red", "green", "blue"))
        if has_rgb:
            ir, ig, ib = (names.index("red"), names.index("green"),
                          names.index("blue"))

        pts = []
        unpack = struct.Struct(code).unpack
        for _ in range(count):
            v = unpack(f.read(stride))
            if has_rgb:
                pts.append((v[ix], v[iy], v[iz], v[ir], v[ig], v[ib], None, None))
            else:
                pts.append((v[ix], v[iy], v[iz], 128, 128, 128, None, None))
        return pts


# ----------------------------------------------------------------------
# LASの書き出し
# ----------------------------------------------------------------------

def build_geokey_vlr(epsg):
    """GeoTIFFキー形式のVLR。UserID は LASF_Projection、RecordID は 34735"""
    keys = [
        (1024, 0, 1, 1),      # GTModelTypeGeoKey = 1 (投影座標系)
        (3072, 0, 1, epsg),   # ProjectedCSTypeGeoKey = EPSG
        (3076, 0, 1, 9001),   # ProjLinearUnitsGeoKey = 9001 (メートル)
    ]
    payload = struct.pack("<4H", 1, 1, 0, len(keys))      # ディレクトリ見出し
    for k in keys:
        payload += struct.pack("<4H", *k)

    header = struct.pack("<H", 0)                          # Reserved
    header += b"LASF_Projection".ljust(16, b"\0")          # User ID
    header += struct.pack("<HH", 34735, len(payload))      # Record ID, 長さ
    header += b"GeoTIFF GeoKeyDirectory".ljust(32, b"\0")  # 説明
    return header + payload


def write_las(path, points, epsg, scale=0.001):
    """LAS 1.2 / 点フォーマット2 で書き出す。

    points は (x_east, y_north, z, r, g, b) のならび。
    r,g,b は 0〜255 を想定し、LASの16bitの箱に入れる。
    """
    vlr = build_geokey_vlr(epsg)
    header_size = 227
    offset_to_points = header_size + len(vlr)
    stride = 26   # 点フォーマット2

    xs = [p[0] for p in points]
    ys = [p[1] for p in points]
    zs = [p[2] for p in points]

    with open(path, "wb") as f:
        f.write(b"LASF")
        f.write(struct.pack("<HH", 0, 0))          # File Source ID, Global Encoding
        f.write(b"\0" * 16)                        # GUID
        f.write(struct.pack("<BB", 1, 2))          # バージョン 1.2
        f.write(b"ARPointCloud".ljust(32, b"\0"))  # System Identifier
        f.write(b"points_to_las.py".ljust(32, b"\0"))
        f.write(struct.pack("<HH", 1, 2026))       # 作成日, 作成年
        f.write(struct.pack("<HI", header_size, offset_to_points))
        f.write(struct.pack("<I", 1))              # VLRの数
        f.write(struct.pack("<BH", 2, stride))     # フォーマット番号, 1点のバイト数
        f.write(struct.pack("<I", len(points)))    # 点の数

        f.write(struct.pack("<I", len(points)))    # リターン別の点数(5個)
        f.write(struct.pack("<4I", 0, 0, 0, 0))

        f.write(struct.pack("<3d", scale, scale, scale))
        f.write(struct.pack("<3d", 0.0, 0.0, 0.0))   # オフセットは0

        # 範囲は Max → Min の順
        f.write(struct.pack("<2d", max(xs), min(xs)))
        f.write(struct.pack("<2d", max(ys), min(ys)))
        f.write(struct.pack("<2d", max(zs), min(zs)))

        f.write(vlr)

        # 点フォーマット2 = 26バイト:
        #   X,Y,Z(int32×3) 強度(u16) 反射フラグ(u8) 分類(u8)
        #   スキャン角(i8) ユーザーデータ(u8) 出所ID(u16) R,G,B(u16×3)
        pack = struct.Struct("<iiiHBBbBH3H").pack
        for x, y, z, r, g, b in points:
            f.write(pack(
                int(round(x / scale)), int(round(y / scale)), int(round(z / scale)),
                0,        # 強度
                0x09,     # 1回のうち1回目の反射
                1,        # 分類 (1 = 未分類)
                0,        # スキャン角
                0,        # ユーザーデータ
                1,        # 点の出所ID
                r, g, b))


# ----------------------------------------------------------------------
# 本体
# ----------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser(
        description="COLMAPの点群を平面直角座標系のLASに変換する")
    ap.add_argument("model", help="COLMAPのモデルフォルダ、または .ply")
    ap.add_argument("rec_dir", help="frames.csv のある rec_ フォルダ")
    ap.add_argument("--zone", type=int, required=True,
                    help="平面直角座標系の系番号 (1〜19)。名古屋は7")
    ap.add_argument("--geoid", type=float, default=37.0,
                    help="ジオイド高(m)。楕円体高からこれを引いて標高にする。"
                         "名古屋付近の概算値が既定")
    ap.add_argument("--max-distance", type=float, default=50.0, metavar="M",
                    help="カメラの通り道からこの距離(m)より遠い点を捨てる。"
                         "2視点しか見ていない遠方の点は奥行き誤差が巨大になり、"
                         "点群の範囲だけを無意味に広げる。0で無効")
    ap.add_argument("--min-track", type=int, default=1, metavar="N",
                    help="この枚数未満にしか写っていない点を捨てる。"
                         "3にすると2視点だけの不安定な点が消える")
    ap.add_argument("--max-error", type=float, default=None, metavar="PX",
                    help="再投影誤差がこれ以上の点を捨てる")
    ap.add_argument("-o", "--out", default=None, help="出力する .las")
    args = ap.parse_args()

    # --- 点群 ---------------------------------------------------------
    if os.path.isdir(args.model):
        b, t = (os.path.join(args.model, "points3D.bin"),
                os.path.join(args.model, "points3D.txt"))
        if os.path.exists(b):
            pts = read_colmap_points_bin(b)
        elif os.path.exists(t):
            pts = read_colmap_points_txt(t)
        else:
            sys.exit(f"points3D.bin も .txt もありません: {args.model}")
    else:
        pts = read_ply(args.model)

    if not pts:
        sys.exit("点が1つもありません")
    print(f"点群 {len(pts):,} 点")

    # --- frames.csv ---------------------------------------------------
    csv_path = os.path.join(args.rec_dir, "frames.csv")
    with open(csv_path, newline="", encoding="utf-8-sig") as f:
        rows = [r for r in csv.DictReader(f)
                if r.get("tracking_state") == "Tracking" and r.get("lat")]

    if not rows:
        sys.exit("地球座標が取れているコマが1つもありません。"
                 "屋外でVPSが成立した状態で撮り直してください")
    print(f"地球座標のあるコマ {len(rows)} / 参照に使用")

    # --- 局所座標系 → EUN の回転 --------------------------------------
    # eun は「カメラ→EUN」、local_q は「カメラ→局所世界」。
    # よって eun * conj(local_q) が「局所世界→EUN」になる。
    rots = []
    for r in rows:
        q_local = q_normalize((float(r["local_qx"]), float(r["local_qy"]),
                               float(r["local_qz"]), float(r["local_qw"])))
        q_eun = q_normalize((float(r["eun_qx"]), float(r["eun_qy"]),
                             float(r["eun_qz"]), float(r["eun_qw"])))
        rots.append(q_normalize(q_mul(q_eun, q_conj(q_local))))

    q_w2eun = q_average(rots)
    spread = [q_angle_deg(q, q_w2eun) for q in rots]
    spread.sort()
    print(f"局所→EUN回転のばらつき 中央値 {spread[len(spread) // 2]:.2f}度"
          f" / 最大 {spread[-1]:.2f}度")
    if spread[len(spread) // 2] > 5.0:
        print("  ※ ばらつきが大きい。VPSが効いていない可能性が高く、"
              "方位が信用できない")

    # --- 基準点 -------------------------------------------------------
    lats = sorted(float(r["lat"]) for r in rows)
    lons = sorted(float(r["lon"]) for r in rows)
    alts = sorted(float(r["alt_ellipsoid"]) for r in rows)
    lat0 = lats[len(lats) // 2]
    lon0 = lons[len(lons) // 2]
    alt0 = alts[len(alts) // 2]

    accs = sorted(float(r["acc_h"]) for r in rows if r.get("acc_h"))
    acc_h = accs[len(accs) // 2] if accs else float("nan")
    yaws = sorted(float(r["acc_yaw"]) for r in rows if r.get("acc_yaw"))
    acc_yaw = yaws[len(yaws) // 2] if yaws else float("nan")

    print(f"基準点 {lat0:.8f}, {lon0:.8f}  楕円体高 {alt0:.3f} m")
    print(f"  ARCoreの自己申告 水平精度 {acc_h:.2f} m / 方位精度 {acc_yaw:.2f} 度")
    if acc_yaw > 5.0:
        print("  ※ 方位精度が悪い。点群全体がこの角度だけ回る。"
              "50m先で1度は約0.9mのずれになる")

    # 基準となる局所座標。回転の基準に使ったコマ群の中央値
    pxs = sorted(float(r["local_px"]) for r in rows)
    pys = sorted(float(r["local_py"]) for r in rows)
    pzs = sorted(float(r["local_pz"]) for r in rows)
    ref_local = (pxs[len(pxs) // 2], pys[len(pys) // 2], pzs[len(pzs) // 2])

    # --- 絞り込み -----------------------------------------------------
    before = len(pts)

    def report(label, kept):
        n = len(pts) - len(kept)
        if n:
            print(f"  {label} で {n:,} 点を除外")
        return kept

    if args.min_track > 1:
        pts = report(f"トラック長 {args.min_track}枚未満",
                     [p for p in pts
                      if p[7] is None or p[7] >= args.min_track])

    if args.max_error is not None:
        pts = report(f"再投影誤差 {args.max_error}px 以上",
                     [p for p in pts
                      if p[6] is None or p[6] < args.max_error])

    if args.max_distance and args.max_distance > 0:
        # カメラの通り道からの距離で測る。重心からだと経路の端が不当に遠くなる。
        # 全カメラと比べると点数×台数になるので、間引いた代表点を使う
        cams = []
        stride = max(1, len(rows) // 200)
        for r in rows[::stride]:
            cams.append((float(r["local_px"]), float(r["local_py"]),
                         -float(r["local_pz"])))

        lim2 = args.max_distance ** 2
        keep = []
        for p in pts:
            for c in cams:
                dx = p[0] - c[0]
                dy = p[1] - c[1]
                dz = p[2] - c[2]
                if dx * dx + dy * dy + dz * dz <= lim2:
                    keep.append(p)
                    break
        pts = report(f"通り道から {args.max_distance}m 超", keep)

    if len(pts) != before:
        print(f"絞り込み {before:,} → {len(pts):,} 点 "
              f"({len(pts)/before*100:.1f}%)")
    if not pts:
        sys.exit("絞り込みで点が全部消えました。条件を緩めてください")

    # --- 変換 ---------------------------------------------------------
    m = q_to_matrix(q_w2eun)
    x0, y0, z0 = geodetic_to_ecef(lat0, lon0, alt0)

    out_pts = []
    for px, py, pz, r, g, b, _err, _tl in pts:
        # COLMAPの世界は Unity左手系のZを反転したもの。まず戻す
        u = (px - ref_local[0], py - ref_local[1], -pz - ref_local[2])

        # 局所世界 → EUN (東, 上, 北)
        e = m[0][0] * u[0] + m[0][1] * u[1] + m[0][2] * u[2]
        up = m[1][0] * u[0] + m[1][1] * u[1] + m[1][2] * u[2]
        no = m[2][0] * u[0] + m[2][1] * u[1] + m[2][2] * u[2]

        # EUN → ECEF → 緯度経度高 → 平面直角座標
        ex, ey, ez = enu_to_ecef(e, no, up, lat0, lon0, x0, y0, z0)
        lat, lon, h = ecef_to_geodetic(ex, ey, ez)
        northing, easting = jgd2011.forward(lat, lon, args.zone)

        # LASは X=東, Y=北。Zは標高(楕円体高からジオイド高を引く)
        out_pts.append((easting, northing, h - args.geoid, r, g, b))

    epsg = jgd2011.epsg_for_zone(args.zone)
    out = args.out or os.path.join(args.rec_dir, f"pointcloud_zone{args.zone}.las")
    write_las(out, out_pts, epsg)

    xs = [p[0] for p in out_pts]
    ys = [p[1] for p in out_pts]
    zs = [p[2] for p in out_pts]
    print(f"\n出力 {out}")
    print(f"  EPSG:{epsg} (平面直角座標系{args.zone}系) / X=東, Y=北")
    print(f"  X(東)  {min(xs):.3f} 〜 {max(xs):.3f}  ({max(xs) - min(xs):.2f} m)")
    print(f"  Y(北)  {min(ys):.3f} 〜 {max(ys):.3f}  ({max(ys) - min(ys):.2f} m)")
    print(f"  Z(標高) {min(zs):.3f} 〜 {max(zs):.3f}  ({max(zs) - min(zs):.2f} m)")
    print(f"  ジオイド高 {args.geoid} m を定数で引いている(要改善)")


if __name__ == "__main__":
    main()
