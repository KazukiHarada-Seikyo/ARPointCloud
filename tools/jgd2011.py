"""緯度経度 (JGD2011) と平面直角座標の相互変換。

国土地理院の「平面直角座標への換算」の式をそのまま実装したもの。
依存なし(標準ライブラリのみ)。

--------------------------------------------------------------------
用語の注意

日本の測量では **X が北、Y が東** と呼ぶ。この規約は書類上のもので、
このモジュールの forward() も (x=北, y=東) の順で返す。

一方 LAS ファイルに書くときは **X=東, Y=北** にする。理由は3つ:
  - LASを読む道具(CloudCompare, LAStools, PDAL, QGIS)がそう仮定している
  - X=東,Y=北,Z=上 は右手系。X=北,Y=東,Z=上 は左手系になり、
    法線や外積を使う処理が軒並み壊れる
  - EPSGの定義は(北,東)だが、実ファイルはほぼ(東,北)で書かれている

入れ替えは LAS を書く側(points_to_las.py)の責任とし、
このモジュールは測量の規約のまま返す。
--------------------------------------------------------------------
"""

import math

# GRS80 楕円体
A = 6378137.0
F = 298.257222101
M0 = 0.9999          # 平面直角座標系の縮尺係数

# 系番号 → (原点緯度, 原点経度) 度
# EPSG は JGD2011 で 6669 + (系番号 - 1)
ZONE_ORIGINS = {
    1:  (33.0, 129.5),
    2:  (33.0, 131.0),
    3:  (36.0, 132.0 + 10.0 / 60),
    4:  (33.0, 133.5),
    5:  (36.0, 134.0 + 20.0 / 60),
    6:  (36.0, 136.0),
    7:  (36.0, 137.0 + 10.0 / 60),
    8:  (36.0, 138.5),
    9:  (36.0, 139.0 + 50.0 / 60),
    10: (40.0, 140.0 + 50.0 / 60),
    11: (44.0, 140.0 + 15.0 / 60),
    12: (44.0, 142.0 + 15.0 / 60),
    13: (44.0, 144.0 + 15.0 / 60),
    14: (26.0, 142.0),
    15: (26.0, 127.5),
    16: (26.0, 124.0),
    17: (26.0, 131.0),
    18: (20.0, 136.0),
    19: (26.0, 154.0),
}


def epsg_for_zone(zone):
    """系番号 → EPSGコード (JGD2011 平面直角座標系)"""
    if zone not in ZONE_ORIGINS:
        raise ValueError(f"系番号は1〜19です: {zone}")
    return 6668 + zone


def _coefficients():
    n = 1.0 / (2.0 * F - 1.0)

    a = [
        1.0 + n ** 2 / 4.0 + n ** 4 / 64.0,
        -1.5 * (n - n ** 3 / 8.0 - n ** 5 / 64.0),
        (15.0 / 16.0) * (n ** 2 - n ** 4 / 4.0),
        -(35.0 / 48.0) * (n ** 3 - (5.0 / 16.0) * n ** 5),
        (315.0 / 512.0) * n ** 4,
        -(693.0 / 1280.0) * n ** 5,
    ]

    alpha = [
        0.5 * n - (2.0 / 3.0) * n ** 2 + (5.0 / 16.0) * n ** 3
        + (41.0 / 180.0) * n ** 4 - (127.0 / 288.0) * n ** 5,

        (13.0 / 48.0) * n ** 2 - (3.0 / 5.0) * n ** 3
        + (557.0 / 1440.0) * n ** 4 + (281.0 / 630.0) * n ** 5,

        (61.0 / 240.0) * n ** 3 - (103.0 / 140.0) * n ** 4
        + (15061.0 / 26880.0) * n ** 5,

        (49561.0 / 161280.0) * n ** 4 - (179.0 / 168.0) * n ** 5,

        (34729.0 / 80640.0) * n ** 5,
    ]

    beta = [
        0.5 * n - (2.0 / 3.0) * n ** 2 + (37.0 / 96.0) * n ** 3
        - (1.0 / 360.0) * n ** 4 - (81.0 / 512.0) * n ** 5,

        (1.0 / 48.0) * n ** 2 + (1.0 / 15.0) * n ** 3
        - (437.0 / 1440.0) * n ** 4 + (46.0 / 105.0) * n ** 5,

        (17.0 / 480.0) * n ** 3 - (37.0 / 840.0) * n ** 4
        - (209.0 / 4480.0) * n ** 5,

        (4397.0 / 161280.0) * n ** 4 - (11.0 / 504.0) * n ** 5,

        (4583.0 / 161280.0) * n ** 5,
    ]

    delta = [
        2.0 * n - (2.0 / 3.0) * n ** 2 - 2.0 * n ** 3
        + (116.0 / 45.0) * n ** 4 + (26.0 / 45.0) * n ** 5
        - (2854.0 / 675.0) * n ** 6,

        (7.0 / 3.0) * n ** 2 - (8.0 / 5.0) * n ** 3 - (227.0 / 45.0) * n ** 4
        + (2704.0 / 315.0) * n ** 5 + (2323.0 / 945.0) * n ** 6,

        (56.0 / 15.0) * n ** 3 - (136.0 / 35.0) * n ** 4
        - (1262.0 / 105.0) * n ** 5 + (73814.0 / 2835.0) * n ** 6,

        (4279.0 / 630.0) * n ** 4 - (332.0 / 35.0) * n ** 5
        - (399572.0 / 14175.0) * n ** 6,

        (4174.0 / 315.0) * n ** 5 - (144838.0 / 6237.0) * n ** 6,

        (601676.0 / 22275.0) * n ** 6,
    ]

    return n, a, alpha, beta, delta


def _meridian_arc(phi_rad, n, a):
    """赤道から緯度phiまでの子午線弧長に相当する量 (A_bar を掛ける前)"""
    s = a[0] * phi_rad
    for j in range(1, 6):
        s += a[j] * math.sin(2.0 * j * phi_rad)
    return s


def forward(lat_deg, lon_deg, zone):
    """緯度経度 → 平面直角座標。

    戻り値は測量の規約どおり (x=北方向[m], y=東方向[m])。
    LASに書くときは入れ替えること。
    """
    lat0_deg, lon0_deg = ZONE_ORIGINS[zone]

    n, a, alpha, _, _ = _coefficients()
    a_bar = (M0 * A) / (1.0 + n) * a[0]
    s_bar_phi0 = (M0 * A) / (1.0 + n) * _meridian_arc(math.radians(lat0_deg), n, a)

    phi = math.radians(lat_deg)
    dlon = math.radians(lon_deg - lon0_deg)

    lam_c = math.cos(dlon)
    lam_s = math.sin(dlon)

    two_sqrt_n = 2.0 * math.sqrt(n) / (1.0 + n)
    t = math.sinh(math.atanh(math.sin(phi))
                  - two_sqrt_n * math.atanh(two_sqrt_n * math.sin(phi)))
    t_bar = math.sqrt(1.0 + t * t)

    xi = math.atan2(t, lam_c)
    eta = math.atanh(lam_s / t_bar)

    x = xi
    y = eta
    for j in range(1, 6):
        x += alpha[j - 1] * math.sin(2.0 * j * xi) * math.cosh(2.0 * j * eta)
        y += alpha[j - 1] * math.cos(2.0 * j * xi) * math.sinh(2.0 * j * eta)

    return a_bar * x - s_bar_phi0, a_bar * y


def inverse(x, y, zone):
    """平面直角座標 (x=北, y=東) → 緯度経度。検算用。"""
    lat0_deg, lon0_deg = ZONE_ORIGINS[zone]

    n, a, _, beta, delta = _coefficients()
    a_bar = (M0 * A) / (1.0 + n) * a[0]
    s_bar_phi0 = (M0 * A) / (1.0 + n) * _meridian_arc(math.radians(lat0_deg), n, a)

    xi = (x + s_bar_phi0) / a_bar
    eta = y / a_bar

    xi2 = xi
    eta2 = eta
    for j in range(1, 6):
        xi2 -= beta[j - 1] * math.sin(2.0 * j * xi) * math.cosh(2.0 * j * eta)
        eta2 -= beta[j - 1] * math.cos(2.0 * j * xi) * math.sinh(2.0 * j * eta)

    chi = math.asin(math.sin(xi2) / math.cosh(eta2))

    lat = chi
    for j in range(1, 7):
        lat += delta[j - 1] * math.sin(2.0 * j * chi)

    lon = math.radians(lon0_deg) + math.atan2(math.sinh(eta2), math.cos(xi2))

    return math.degrees(lat), math.degrees(lon)


# ----------------------------------------------------------------------
# 検算
# ----------------------------------------------------------------------

def selftest():
    print("=== 原点が (0, 0) になるか ===")
    worst = 0.0
    for zone, (lat0, lon0) in sorted(ZONE_ORIGINS.items()):
        x, y = forward(lat0, lon0, zone)
        worst = max(worst, abs(x), abs(y))
        if zone in (1, 7, 19):
            print(f"  系{zone:2d} EPSG:{epsg_for_zone(zone)}  x={x:+.9f} y={y:+.9f}")
    print(f"  全19系の最大ずれ {worst:.3e} m")

    print("\n=== 往復して元の緯度経度に戻るか ===")
    tests = [
        ("名古屋付近 (系7)", 35.1706, 136.8816, 7),
        ("原点から北へ (系7)", 36.5, 137.0 + 10.0 / 60, 7),
        ("系の端 (系7)", 35.0, 138.0, 7),
        ("札幌付近 (系12)", 43.06, 141.35, 12),
        ("那覇付近 (系15)", 26.21, 127.68, 15),
    ]
    worst_m = 0.0
    for name, lat, lon, zone in tests:
        x, y = forward(lat, lon, zone)
        lat2, lon2 = inverse(x, y, zone)
        # 緯度1度 ≒ 111km で距離に直す
        dlat_m = abs(lat2 - lat) * 111000.0
        dlon_m = abs(lon2 - lon) * 111000.0 * math.cos(math.radians(lat))
        err = math.hypot(dlat_m, dlon_m)
        worst_m = max(worst_m, err)
        print(f"  {name:22s} x={x:12.4f} y={12 * ' '}"[:0] or
              f"  {name:22s} x={x:12.4f} y={y:12.4f}  往復誤差 {err * 1000:.6f} mm")
    print(f"  最大 {worst_m * 1000:.6f} mm")

    print("\n=== 縮尺の確認 (原点で 0.9999 に近いか) ===")
    # 原点付近で 1 秒ぶん北へ動かし、距離の比を見る
    lat0, lon0 = ZONE_ORIGINS[7]
    d = 1.0 / 3600.0
    x1, _ = forward(lat0, lon0, 7)
    x2, _ = forward(lat0 + d, lon0, 7)
    # GRS80 の子午線1秒の長さ(概算)
    e2 = 2.0 / F - 1.0 / (F * F)
    m = A * (1 - e2) / (1 - e2 * math.sin(math.radians(lat0)) ** 2) ** 1.5
    true_m = m * math.radians(d)
    print(f"  投影上 {x2 - x1:.6f} m / 地球上 {true_m:.6f} m"
          f" → 比 {(x2 - x1) / true_m:.7f}")


if __name__ == "__main__":
    selftest()
