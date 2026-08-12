#!/usr/bin/env python3
"""色がはっきり分かるテスト用のLASを作る。

  python tools/make_test_las.py out.las

読み込み側の切り分け用。10m四方の格子に、赤・緑・青・白の4色を
四分割で置く。標高は一定にしてあるので、標高で塗られた場合は
「全部同じ色」になり、RGBが効いていないことが一目で分かる。

出力は points_to_las.py とまったく同じ書式（LAS 1.2 / 形式2 /
16bitの色 / VLRにEPSG）。こちらが正しく表示できるなら、
書式の問題ではなく中身の問題ということになる。
"""

import struct
import sys


def write(path, epsg=6675, size=10.0, spacing=0.05, z=5.0):
    scale = 0.001
    # 名古屋あたりの平面直角座標系VII系の値。実データと同じ桁にしておく
    x0, y0 = -33940.0, -84870.0

    pts = []
    n = int(size / spacing)
    for iy in range(n):
        for ix in range(n):
            x = x0 + ix * spacing
            y = y0 + iy * spacing

            # 四分割で色を変える
            right = ix >= n / 2
            top = iy >= n / 2
            if not right and not top:
                c = (255, 0, 0)        # 赤
            elif right and not top:
                c = (0, 255, 0)        # 緑
            elif not right and top:
                c = (0, 0, 255)        # 青
            else:
                c = (255, 255, 255)    # 白

            pts.append((x, y, z, c[0], c[1], c[2]))

    xs = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    zs = [p[2] for p in pts]

    # --- VLR（GeoTIFFキー）---
    keys = [(1024, 0, 1, 1), (3072, 0, 1, epsg), (3076, 0, 1, 9001)]
    payload = struct.pack("<4H", 1, 1, 0, len(keys))
    for k in keys:
        payload += struct.pack("<4H", *k)
    vlr = struct.pack("<H", 0)
    vlr += b"LASF_Projection".ljust(16, b"\0")
    vlr += struct.pack("<HH", 34735, len(payload))
    vlr += b"GeoTIFF GeoKeyDirectory".ljust(32, b"\0")
    vlr += payload

    header_size = 227
    offset = header_size + len(vlr)
    stride = 26

    with open(path, "wb") as f:
        f.write(b"LASF")
        f.write(struct.pack("<HH", 0, 0))
        f.write(b"\0" * 16)
        f.write(struct.pack("<BB", 1, 2))
        f.write(b"ARPointCloud".ljust(32, b"\0"))
        f.write(b"make_test_las.py".ljust(32, b"\0"))
        f.write(struct.pack("<HH", 1, 2026))
        f.write(struct.pack("<HI", header_size, offset))
        f.write(struct.pack("<I", 1))
        f.write(struct.pack("<BH", 2, stride))
        f.write(struct.pack("<I", len(pts)))
        f.write(struct.pack("<I", len(pts)))
        f.write(struct.pack("<4I", 0, 0, 0, 0))
        f.write(struct.pack("<3d", scale, scale, scale))
        f.write(struct.pack("<3d", 0.0, 0.0, 0.0))
        f.write(struct.pack("<2d", max(xs), min(xs)))
        f.write(struct.pack("<2d", max(ys), min(ys)))
        f.write(struct.pack("<2d", max(zs), min(zs)))
        f.write(vlr)

        pack = struct.Struct("<iiiHBBbBH3H").pack
        for x, y, zz, r, g, b in pts:
            f.write(pack(int(round(x / scale)), int(round(y / scale)),
                         int(round(zz / scale)),
                         0, 0x09, 1, 0, 0, 1,
                         r * 257, g * 257, b * 257))

    print(f"{len(pts):,} 点 → {path}")
    print(f"  {size}m 四方 / 間隔 {spacing}m / 標高 {z}m（一定）")
    print("  左下=赤  右下=緑  左上=青  右上=白")
    print("  標高が一定なので、標高で塗られると全部同じ色になる")


if __name__ == "__main__":
    write(sys.argv[1] if len(sys.argv) > 1 else "test_colors.las")
