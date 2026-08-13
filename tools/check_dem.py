#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""ARCoreの楕円体高を、国土地理院の数値標高モデル(DEM)と突き合わせる

答えたい問い (ROADMAP §6-1 の残り):

  `alt_ellipsoid` が「局所座標の上下動 + 定数」であることは
  PHASE4_ACCURACY.md §3 で分かった。ではその**定数はどこから来たのか**。

    GNSSの鉛直から   … 地形とは無関係の値になる
    地形の標高データから … DEMを引いた値と噛み合うはず

  楕円体高 = 標高 + ジオイド高 なので、

    alt_ellipsoid - DEM標高 = ジオイド高 + 端末の地上高

  右辺は撮影中ほぼ一定のはず(ジオイド高は数十m四方で mm しか変わらず、
  端末の持ち方も大きくは変わらない)。名古屋付近のジオイド高は 36〜37 m
  程度なので、差から 36.5 を引いて出る「端末の地上高」が
  1.0〜1.6 m の常識的な値に収まるかを見る。

データの出どころ: 国土地理院 標高タイル
  https://maps.gsi.go.jp/development/ichiran.html
  dem5a (5mメッシュ・航空レーザ測量) を優先し、無ければ dem (10mメッシュ)。

  ※ タイル単位で取りに行くので、外部へ渡るのは「どの1kmタイルか」まで。
    正確な緯度経度は送らない。取得したタイルは --cache に貯めて使い回す。

使い方:

    python tools\\check_dem.py Captures\\files\\rec_20260812_111139
"""

import argparse
import csv
import json
import math
import os
import statistics as st
import sys
import urllib.error
import urllib.request

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

TILE_SETS = [("dem5a", 15), ("dem5b", 15), ("dem", 14)]
BASE = "https://cyberjapandata.gsi.go.jp/xyz/{name}/{z}/{x}/{y}.txt"


def lonlat_to_tile(lat, lon, z):
    """緯度経度 → タイル番号とタイル内の画素(256x256)"""
    n = 2 ** z
    fx = (lon + 180.0) / 360.0 * n
    la = math.radians(lat)
    fy = (1.0 - math.log(math.tan(la) + 1.0 / math.cos(la)) / math.pi) / 2.0 * n
    tx, ty = int(fx), int(fy)
    px = min(255, int((fx - tx) * 256))
    py = min(255, int((fy - ty) * 256))
    return tx, ty, px, py


def fetch_tile(name, z, tx, ty, cache):
    """標高タイルを1枚取る。256行×256列の数値。'e' は欠測"""
    key = f"{name}_{z}_{tx}_{ty}.txt"
    path = os.path.join(cache, key)
    if os.path.exists(path):
        text = open(path, encoding="utf-8").read()
    else:
        url = BASE.format(name=name, z=z, x=tx, y=ty)
        try:
            with urllib.request.urlopen(url, timeout=30) as f:
                text = f.read().decode("utf-8")
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return None          # そのタイルはこの種別では未整備
            raise
        os.makedirs(cache, exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)
        print(f"  取得 {name} z{z} {tx}/{ty}")

    grid = []
    for row in text.strip().split("\n"):
        grid.append([None if v.strip() == "e" else float(v)
                     for v in row.split(",")])
    return grid


def fetch_geoid(lat, lon):
    """国土地理院のジオイド高計算サービスから GSIGEO2011 の値を引く。

    緯度経度は 0.01度(約1km)に丸めて渡す。ジオイド高は1kmでcmしか
    変わらないので精度は落ちず、外へ渡る位置も粗くできる。
    """
    url = ("https://vldb.gsi.go.jp/sokuchi/surveycalc/geoid/calcgh/cgi/"
           f"geoidcalc.pl?outputType=json&latitude={lat:.2f}&longitude={lon:.2f}")
    try:
        with urllib.request.urlopen(url, timeout=30) as f:
            data = json.loads(f.read().decode("utf-8"))
        v = float(data["OutputData"]["geoidHeight"])
        print(f"  ジオイド高 {v:.4f} m (GSIGEO2011 / "
              f"問い合わせ位置は {lat:.2f}, {lon:.2f} に丸め)")
        return v
    except Exception as e:
        print(f"  ジオイド高の取得に失敗: {e}")
        return None


class Dem:
    """必要なタイルだけ取ってきて標高を引く"""

    def __init__(self, cache):
        self.cache = cache
        self.tiles = {}
        self.source = {}

    def elevation(self, lat, lon):
        for name, z in TILE_SETS:
            tx, ty, px, py = lonlat_to_tile(lat, lon, z)
            key = (name, z, tx, ty)
            if key not in self.tiles:
                self.tiles[key] = fetch_tile(name, z, tx, ty, self.cache)
            g = self.tiles[key]
            if g is None:
                continue
            try:
                v = g[py][px]
            except IndexError:
                continue
            if v is not None:
                self.source[name] = self.source.get(name, 0) + 1
                return v, name
        return None, None


def main():
    ap = argparse.ArgumentParser(
        description="ARCoreの楕円体高を国土地理院DEMと突き合わせる")
    ap.add_argument("path", help="CSVファイル、または rec_* ディレクトリ")
    ap.add_argument("--stride", type=int, default=25,
                    help="何コマに1つ調べるか (既定25)")
    ap.add_argument("--geoid", type=float, default=None,
                    help="ジオイド高 [m]。省略すると国土地理院から取る")
    ap.add_argument("--hold", type=float, default=1.4, metavar="M",
                    help="端末を持っていた高さの想定 [m] (既定1.4)")
    ap.add_argument("--cache", default=os.path.join("Captures", "dem_cache"),
                    help="取得したタイルの置き場")
    args = ap.parse_args()

    path = args.path
    if os.path.isdir(path):
        path = os.path.join(path, "frames.csv")
    if not os.path.exists(path):
        sys.exit(f"見つかりません: {path}")

    with open(path, encoding="utf-8-sig", newline="") as f:
        rows = [r for r in csv.DictReader(f)
                if r.get("lat") and r.get("alt_ellipsoid")]
    if not rows:
        sys.exit("緯度経度のある行がありません")

    print("=" * 66)
    print(f"DEM突合  {os.path.basename(os.path.dirname(os.path.abspath(path)))}")
    print("=" * 66)
    print(f"  {len(rows)} 行中 {args.stride} コマに1つ、"
          f"{len(rows) // args.stride} 点を調べます")
    print()

    dem = Dem(args.cache)
    got = []
    for r in rows[::args.stride]:
        lat, lon = float(r["lat"]), float(r["lon"])
        e, src = dem.elevation(lat, lon)
        if e is None:
            continue
        got.append({
            "t": float(r["elapsed_s"]) if r.get("elapsed_s") else 0.0,
            "alt": float(r["alt_ellipsoid"]),
            "py": float(r["local_py"]) if r.get("local_py") else 0.0,
            "dem": e,
            "src": src,
        })

    if not got:
        sys.exit("DEMの値が引けませんでした（未整備の区域かもしれません）")

    print(f"  引けた点 {len(got)} / 使ったデータ "
          f"{', '.join(f'{k}({v})' for k, v in dem.source.items())}")
    print()

    demv = [g["dem"] for g in got]
    altv = [g["alt"] for g in got]
    diff = [g["alt"] - g["dem"] for g in got]

    print("1. 地形はどれだけ起伏があるか")
    print(f"   DEM標高      {min(demv):7.2f} 〜 {max(demv):7.2f} m "
          f"(幅 {max(demv) - min(demv):.2f} m / 中央値 {st.median(demv):.2f} m)")
    print(f"   楕円体高     {min(altv):7.2f} 〜 {max(altv):7.2f} m "
          f"(幅 {max(altv) - min(altv):.2f} m)")
    print()

    print("2. 楕円体高 - DEM標高 = ジオイド高 + 端末の地上高")
    print(f"   {min(diff):7.2f} 〜 {max(diff):7.2f} m  "
          f"中央値 {st.median(diff):.2f} m  標準偏差 {st.pstdev(diff):.2f} m")

    geoid = args.geoid
    if geoid is None:
        geoid = fetch_geoid(st.median([float(r["lat"]) for r in rows]),
                            st.median([float(r["lon"]) for r in rows]))
        if geoid is None:
            print("   ジオイド高が取れませんでした。--geoid で与えてください")
            return
    implied = st.median(diff) - geoid
    print(f"   ジオイド高 {geoid:.2f} m を引くと、"
          f"端末の地上高は **{implied:.2f} m**")
    print()

    print("3. 判定  定数はどこから来たか")
    if 0.7 <= implied <= 2.0:
        print(f"   {implied:.2f} m は手に持って歩く高さとして無理がありません。")
        print("   **ARCoreの高さは地形データとcm級で噛み合っています**。")
        print("   定数の出どころが地形の標高データである可能性が高い。")
    else:
        bias = implied - args.hold
        print(f"   端末を {args.hold:.1f} m の高さで持っていたとすると、"
              f"ARCoreの高さは **{bias:+.2f} m** ずれています。")
        print()
        print("   もし定数が地形の標高データから引かれているなら、DEMとは")
        print("   cm級で噛み合うはずです。噛み合っていません。")
        print("   **地形データ由来という当初の疑いは支持されません。**")
        av = [float(r["acc_v"]) for r in rows if r.get("acc_v")]
        if av:
            m = st.median(av)
            print(f"   一方このずれは、自己申告の垂直精度 {m:.2f} m の"
                  f"{'内' if abs(bias) <= m else '外'}に収まります。")
            print("   GNSSの鉛直のような『測った値の誤差』として自然な大きさです。")
        print()
        print("   → 高さは測位由来で、1m級の偏りを持っている、と読むのが素直です。")
        print("     DEMを使えばこの偏りは机上で補正できます(下記)。")

    print()
    print("4. LAS書き出しへの反映")
    print("   points_to_las.py の既定は --geoid 37.0 です。")
    print(f"   この場所の正しいジオイド高は {geoid:.2f} m なので、"
          f"既定のままでは Z が {37.0 - geoid:+.2f} m ずれます。")
    print()
    print(f"     --geoid {geoid:.2f}      ← 正しいジオイド高で書き出す")
    if not (0.7 <= implied <= 2.0):
        print(f"     --geoid {geoid + implied - args.hold:.2f}      "
              f"← さらにARCoreの偏り {implied - args.hold:+.2f} m も打ち消し、")
        print("                         地面をDEMの標高に合わせる")

    print()
    print("5. 地形の起伏を追っているか")
    if max(demv) - min(demv) < 2.0:
        print(f"   地形の起伏が {max(demv) - min(demv):.2f} m しかなく、")
        print("   『追っているか』は**この撮影地では判定できません**。")
        print("   起伏のある場所で撮れば分かります。")
    else:
        n = len(got)
        md, ma = st.mean(demv), st.mean(altv)
        sxy = sum((g["dem"] - md) * (g["alt"] - ma) for g in got)
        sxx = sum((g["dem"] - md) ** 2 for g in got)
        syy = sum((g["alt"] - ma) ** 2 for g in got)
        if sxx > 0 and syy > 0:
            print(f"   相関 {sxy / math.sqrt(sxx * syy):+.3f} / "
                  f"傾き {sxy / sxx:+.3f}")
            print("   傾きが1に近ければ地形を追っている、0に近ければ追っていない。")

    print()
    print("   経過時間ごとの値:")
    print("     t[s]     DEM標高   楕円体高   差     local_py")
    for g in got[::max(1, len(got) // 12)]:
        print(f"   {g['t']:8.1f} {g['dem']:9.2f} {g['alt']:10.3f} "
              f"{g['alt'] - g['dem']:7.2f} {g['py']:9.3f}")


if __name__ == "__main__":
    main()
