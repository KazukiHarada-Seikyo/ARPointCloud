#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""frames.csv / geolog_*.csv から精度を測る (フェーズ4)

精度をひとつの数字にまとめない。**相対**と**絶対**を分けて出す。

  相対 (形と大きさ)     … 点群そのものの正しさ。局所座標が担う
  絶対 (置き場所と向き) … 地球上のどこに・どの向きで置くか。VPSが担う

この2つは誤差源が別で、桁も別。混ぜると「精度10m」と読まれてしまう。

測り方の要点:

  * **緯度経度は独立な測位ではない**。実測すると、区間の前半で決めた
    変換を後半に外挿しても数cmしかずれない。つまり区間の中では
    緯度経度は局所座標に固定の変換を掛けたものにすぎない(§2)。
    「出どころが別の2つが一致した」という論法は使えないので、
    実寸を主張するには外部の基準が要る
  * 高さも同じ。alt_ellipsoid は local_py に定数を足しただけなので(§5)、
    鉛直を含めると自分自身と比べることになる。距離の比較は水平のみ
  * **VPSは途中で解を組み直す**。そのたびに緯度経度も方位も高さも
    不連続に飛ぶ。新しい測位の情報が入るのはこの瞬間だけ。
    またいで比べると相対精度が不当に悪く出るので、区間に切って測る(§1)

使い方:

    python tools\\analyze_accuracy.py Captures\\files\\rec_20260812_111139
    python tools\\analyze_accuracy.py Captures\\files\\geolog_20260812_111704.csv
"""

import argparse
import csv
import math
import os
import statistics as st
import sys

try:                     # PowerShell 5.1 は cp932。日本語が化けるので UTF-8 に寄せる
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import points_to_las as p2l


# ----------------------------------------------------------------------
# 読み込み
# ----------------------------------------------------------------------
def read_csv(path):
    """frames.csv も geolog_*.csv も同じ形に均して読む。

    古い書き出しにはBOMが付いているので utf-8-sig で開く。
    追跡が成立していない行は空欄なので、値は None のまま持つ。
    """
    with open(path, encoding="utf-8-sig", newline="") as f:
        raw = list(csv.DictReader(f))
    if not raw:
        sys.exit(f"行がありません: {path}")

    def fl(row, key):
        v = row.get(key)
        if v is None:
            return None
        v = v.strip()
        if not v:
            return None
        try:
            return float(v)
        except ValueError:
            return None

    rows = []
    for r in raw:
        rows.append({
            "t": fl(r, "elapsed_s"),
            "tracking": (r.get("tracking_state") or "").strip(),
            "earth": (r.get("earth_state") or "").strip(),
            "lp": (fl(r, "local_px"), fl(r, "local_py"), fl(r, "local_pz")),
            "lq": (fl(r, "local_qx"), fl(r, "local_qy"),
                   fl(r, "local_qz"), fl(r, "local_qw")),
            "eq": (fl(r, "eun_qx"), fl(r, "eun_qy"),
                   fl(r, "eun_qz"), fl(r, "eun_qw")),
            "lat": fl(r, "lat"),
            "lon": fl(r, "lon"),
            "alt": fl(r, "alt_ellipsoid"),
            "acc_h": fl(r, "acc_h"),
            "acc_v": fl(r, "acc_v"),
            "acc_yaw": fl(r, "acc_yaw"),
            "omega": fl(r, "angular_speed_deg_s"),
            "note": (r.get("note") or "").strip(),
            "fx": fl(r, "fx"),
        })
    return rows


def usable(rows):
    """局所座標と緯度経度の両方が揃っている行だけ返す"""
    return [r for r in rows
            if None not in r["lp"] and r["lat"] is not None
            and r["lon"] is not None and r["t"] is not None]


# ----------------------------------------------------------------------
# 座標
# ----------------------------------------------------------------------
def to_enu(rows):
    """緯度経度高 → 最初の行を原点とする ENU (東, 北, 上) [m]

    平面近似ではなく ECEF を経由する。数百m程度なら差は出ないが、
    points_to_las.py と同じ扱いに揃えておく。
    """
    lat0, lon0, h0 = rows[0]["lat"], rows[0]["lon"], rows[0]["alt"] or 0.0
    x0, y0, z0 = p2l.geodetic_to_ecef(lat0, lon0, h0)
    la, lo = math.radians(lat0), math.radians(lon0)
    sl, cl = math.sin(la), math.cos(la)
    so, co = math.sin(lo), math.cos(lo)

    out = []
    for r in rows:
        x, y, z = p2l.geodetic_to_ecef(r["lat"], r["lon"], r["alt"] or 0.0)
        dx, dy, dz = x - x0, y - y0, z - z0
        out.append((-so * dx + co * dy,
                    -sl * co * dx - sl * so * dy + cl * dz,
                    cl * co * dx + cl * so * dy + sl * dz))
    return out


def local_horizontal(rows):
    """局所座標の水平成分。Unityは Y が上なので (X, Z) が水平面"""
    return [(r["lp"][0], r["lp"][2]) for r in rows]


def align_azimuth(r):
    """その行の eun_q と local_q から、局所+X軸の方位角[度]を出す。

    eun_q は端末の EUN 系での姿勢、local_q は局所系での姿勢。
    両者を打ち消すと「局所系そのものが地球に対してどう置かれているか」
    が残る。これがVPSの言う方位で、点群全体の向きを決める。
    """
    if None in r["lq"] or None in r["eq"]:
        return None, None
    m = p2l.q_to_matrix(p2l.q_mul(r["eq"], p2l.q_conj(r["lq"])))
    e, u, n = m[0][0], m[1][0], m[2][0]      # 局所+X を EUN で見たもの
    return math.degrees(math.atan2(e, n)) % 360.0, u


def fit_similarity(lh, enu, idx):
    """局所の水平座標を ENU に重ねる (倍率+回転+平行移動) 最小二乗。

    複素数で書くと P = L * s * exp(-iθ) + t なので、
    倍率と回転をまとめて1つの複素数として解ける。
    倍率も一緒に解くのは、それ自体がスケールの独立な推定値になるため。
    """
    n = len(idx)
    mlx = sum(lh[i][0] for i in idx) / n
    mlz = sum(lh[i][1] for i in idx) / n
    mee = sum(enu[i][0] for i in idx) / n
    men = sum(enu[i][1] for i in idx) / n

    nr = ni = den = 0.0
    for i in idx:
        lx, lz = lh[i][0] - mlx, lh[i][1] - mlz
        px, pn = enu[i][0] - mee, enu[i][1] - men
        nr += lx * px + lz * pn
        ni += lx * pn - lz * px
        den += lx * lx + lz * lz
    if den <= 1e-9:
        return None
    sr, si = nr / den, ni / den
    scale = math.hypot(sr, si)
    theta = math.atan2(-si, sr)

    c, s = math.cos(theta), math.sin(theta)
    res = []
    for i in idx:
        lx, lz = lh[i][0] - mlx, lh[i][1] - mlz
        fe = scale * (c * lx + s * lz)
        fn = scale * (-s * lx + c * lz)
        res.append(math.hypot(fe - (enu[i][0] - mee), fn - (enu[i][1] - men)))

    return {
        "scale": scale,
        "theta": theta,
        # 局所+X軸が指す方位。ARCore由来の値と直接比べられるように揃える
        "az": math.degrees(math.atan2(math.cos(theta), -math.sin(theta))) % 360,
        "res": res,
        "lc": (mlx, mlz),
        "ec": (mee, men),
        "n": n,
    }


def apply_fit(f, p):
    c, s = math.cos(f["theta"]), math.sin(f["theta"])
    lx, lz = p[0] - f["lc"][0], p[1] - f["lc"][1]
    return (f["ec"][0] + f["scale"] * (c * lx + s * lz),
            f["ec"][1] + f["scale"] * (-s * lx + c * lz))


# ----------------------------------------------------------------------
# 小道具
# ----------------------------------------------------------------------
def quantiles(xs):
    s = sorted(xs)
    n = len(s)

    def q(p):
        return s[min(n - 1, max(0, int(round(p * (n - 1)))))]
    return q(0.05), q(0.25), st.median(s), q(0.75), q(0.95)


def line(title):
    print()
    print(title)
    print("-" * 66)


def find_events(use):
    """VPSが解を組み直した瞬間を拾う。

    手がかりは2つ。楕円体高の付け替え(§3)と、方位精度の急な改善。
    どちらも同じ出来事の別の顔なので、近いものは1件にまとめる。
    """
    ts = [r["t"] for r in use]
    off = [(r["alt"] - r["lp"][1]) if r["alt"] is not None else None
           for r in use]

    marks = []
    for i in range(1, len(use)):
        if off[i] is not None and off[i - 1] is not None \
                and abs(off[i] - off[i - 1]) > 0.05:
            marks.append(i)
        a, b = use[i]["acc_yaw"], use[i - 1]["acc_yaw"]
        if a is not None and b is not None and a - b < -0.3:
            marks.append(i)
    marks = sorted(set(marks))

    # 近接する印を1件に束ねる(再収束は数フレームかけて効く)
    ev = []
    for m in marks:
        if ev and ts[m] - ts[ev[-1][1]] < 3.0:
            ev[-1][1] = m
        else:
            ev.append([m, m])
    return [(a, b) for a, b in ev]


def segments(use, ev, min_len=20.0):
    """再照合と再照合のあいだの、連続した区間の索引範囲"""
    ts = [r["t"] for r in use]
    bounds = [0] + [b + 1 for _a, b in ev] + [len(use)]
    segs = []
    for i in range(len(bounds) - 1):
        a, b = bounds[i], bounds[i + 1]
        if b - a > 30 and ts[b - 1] - ts[a] >= min_len:
            segs.append((a, b))
    return segs


# ----------------------------------------------------------------------
# 各節
# ----------------------------------------------------------------------
def section_overview(rows, use):
    line("0. 概要")
    dur = (rows[-1]["t"] - rows[0]["t"]) if rows[0]["t"] is not None else 0.0
    print(f"  行数           {len(rows)}  (局所+緯度経度が揃った行 {len(use)})")
    print(f"  時間           {dur:.1f} 秒")

    bad = [r for r in rows if r["tracking"] and r["tracking"] != "Tracking"]
    print(f"  追跡の断絶     {len(bad)} 行"
          + ("" if bad else "  (全行 Tracking)"))

    lh = local_horizontal(use)
    path = sum(math.dist(lh[i], lh[i - 1]) for i in range(1, len(lh)))
    sub = lh[::max(1, len(lh) // 500)]
    span = max(math.dist(a, b) for i, a in enumerate(sub) for b in sub[i + 1:])
    print(f"  経路長(水平)   {path:.1f} m   / 広がり 約 {span:.1f} m")

    fx = [r["fx"] for r in rows if r["fx"] is not None]
    if fx:
        print(f"  内部パラメータ fx = {fx[0]:.3f}"
              + ("  (全行一定)" if len(set(fx)) == 1
                 else f"  ※{len(set(fx))}種類あります"))

    notes = sorted({r["note"] for r in rows if r["note"] and r["note"] != "-"})
    if notes:
        print(f"  メモ           {', '.join(notes)}")


def section_events(use, ev, segs):
    line("1. VPSが解を組み直した瞬間")
    print("  VPSは照合し直すたびに、緯度経度・方位・高さをまとめて付け替えます。")
    print("  ここを見つけておかないと、前後をまたいで比べたときに")
    print("  相対精度が不当に悪く出ます。")
    print()
    ts = [r["t"] for r in use]
    lh = local_horizontal(use)

    if not ev:
        print("  検出なし。全区間がひとつながりです。")
    else:
        print(f"  検出 {len(ev)} 件 (うち区間を分けるほど大きいもの):")
        for a, b in ev:
            doff = ((use[b]["alt"] - use[b]["lp"][1])
                    - (use[a - 1]["alt"] - use[a - 1]["lp"][1])) \
                if use[b]["alt"] is not None else 0.0
            dyaw = (use[b]["acc_yaw"] - use[a - 1]["acc_yaw"]) \
                if use[b]["acc_yaw"] is not None else 0.0
            if abs(doff) > 0.05 or abs(dyaw) > 0.3:
                print(f"    t={ts[a - 1]:7.2f}〜{ts[b]:7.2f} 秒   "
                      f"高さ {doff:+.3f} m / 方位精度 {dyaw:+.2f} 度")

    print()
    print(f"  → 連続した区間 {len(segs)} 個に分けて測ります: ", end="")
    print(", ".join(f"{ts[a]:.0f}〜{ts[b - 1]:.0f}秒" for a, b in segs))

    # 区間ごとの当てはめ。ここで「区間をまたぐと何が起きるか」を数字にする
    enu = to_enu(use)
    fits = []
    print()
    print("  区間ごとに、局所座標とVPSを1つの向き・1つの倍率で重ねる:")
    print("    区間            倍率      方位      残差(中央値)  残差(最大)")
    for a, b in segs:
        f = fit_similarity(lh, enu, list(range(a, b)))
        fits.append(((a, b), f))
        _, _, med, _, _ = quantiles(f["res"])
        print(f"    {ts[a]:5.0f}〜{ts[b - 1]:5.0f}秒  {f['scale']:.4f}  "
              f"{f['az']:7.2f} 度   {med:8.3f} m   {max(f['res']):8.3f} m")

    if len(fits) >= 2:
        (a1, b1), f1 = fits[0]
        (a2, b2), f2 = fits[-1]
        daz = ((f2["az"] - f1["az"] + 180) % 360) - 180
        # 同じ局所点を両方の当てはめで飛ばし、行き先の差を見る
        mid = ((f1["lc"][0] + f2["lc"][0]) / 2, (f1["lc"][1] + f2["lc"][1]) / 2)
        p1, p2 = apply_fit(f1, mid), apply_fit(f2, mid)
        print()
        print(f"  最初と最後の区間の差:")
        print(f"    方位 {daz:+.2f} 度   位置 {math.dist(p1, p2):.2f} m")
        print(f"    → これが『点群全体が回って動いた量』です。")
        print(f"      1回の撮影のあいだに、地球に対する置き方がこれだけ変わります。")
    return fits


def section_independence(use, segs):
    """緯度経度が局所座標と独立な情報かどうかを検定する。

    ここを間違えると、あとの数字の意味がすべて変わる。
    「出どころが別の2つが一致した」のか「同じものを2通りに書いただけ」
    なのかは、区間の前半で当てはめた変換を後半に外挿すれば分かる。
    """
    line("2. 緯度経度は局所座標と独立か  (これがすべての前提)")
    print("  区間の前半だけで『局所座標→緯度経度』の変換を決め、")
    print("  それを後半にそのまま当てはめます。")
    print()
    print("    独立な測位なら … 後半では合わない。時間とともに離れていく")
    print("    局所座標の写しなら … いつまでも合う。変換が固定されている")
    print()

    enu = to_enu(use)
    lh = local_horizontal(use)
    ts = [r["t"] for r in use]

    verdicts = []
    print("    区間            外挿した先での誤差")
    print("                    中央値      最大      経過時間  移動距離")
    for a, b in segs:
        half = a + (b - a) // 2
        f = fit_similarity(lh, enu, list(range(a, half)))
        if f is None:
            continue
        err = [math.dist(apply_fit(f, lh[i]), (enu[i][0], enu[i][1]))
               for i in range(half, b)]
        walked = sum(math.dist(lh[i], lh[i - 1]) for i in range(half + 1, b))
        med = st.median(err)
        # 動いていない区間は、変換が固定でも当たり前に合ってしまう。
        # 判定には使わず、表にだけ出す
        ok = walked >= 2.0 and (ts[b - 1] - ts[half]) >= 10.0
        if ok:
            verdicts.append(med)
        print(f"    {ts[a]:5.0f}〜{ts[b - 1]:5.0f}秒  {med * 100:8.1f} cm "
              f"{max(err) * 100:8.1f} cm  {ts[b - 1] - ts[half]:7.0f} 秒 "
              f"{walked:7.1f} m" + ("" if ok else "  ← 動きが足りず判定に使えません"))

    # フレーム間の動きそのものを比べる
    print()
    print("    フレームからフレームへの移動量:")
    for a, b in segs:
        dl = [math.dist(lh[i], lh[i - 1]) for i in range(a + 1, b)]
        dv = [math.hypot(enu[i][0] - enu[i - 1][0], enu[i][1] - enu[i - 1][1])
              for i in range(a + 1, b)]
        diff = [abs(x - y) for x, y in zip(dl, dv)]
        print(f"    {ts[a]:5.0f}〜{ts[b - 1]:5.0f}秒  "
              f"局所 {st.median(dl) * 1000:5.1f} mm / "
              f"VPS {st.median(dv) * 1000:5.1f} mm  "
              f"差の中央値 {st.median(diff) * 1000:.2f} mm")

    ah = [r["acc_h"] for r in use if r["acc_h"] is not None]
    print()
    print("  判定:")
    if verdicts and min(verdicts) < 0.10:
        print(f"    最も良い区間で、外挿しても {min(verdicts) * 100:.1f} cm しか離れません。")
        if ah:
            print(f"    自己申告の水平精度は {st.median(ah):.2f} m です。もし緯度経度が")
            print("    毎フレーム測位した独立な値なら、この桁のばらつきが見えるはずです。")
        print()
        print("    **区間の中では、緯度経度は局所座標に固定の変換を掛けたものです。**")
        print("    新しい測位の情報は連続には入らず、§1で見た『組み直し』の")
        print("    瞬間にだけまとめて入ります。")
        print()
        print("    ここから導かれること:")
        print("      * 区間内で局所座標とVPSを比べても、**実寸の裏付けにはなりません**。")
        print("        同じ姿勢を2通りに書いたものを比べているだけです")
        print("      * 精度値が時間とともに膨らむのは、新しい情報が入らないまま")
        print("        推測航法で延ばしているからです(§4-1と整合します)")
        print("      * 実寸を確かめるには**外部の基準が要ります**。")
        print("        巻尺で測った長さ、マーカー、公開データ(DEM/PLATEAU)のどれか")
        print(f"      * 1回の撮影で地球に対する置き方の情報が入るのは"
              f" {len(segs)} 回程度です")
    elif verdicts:
        print("    外挿すると離れていきます。緯度経度は独立な測位を含んでいます。")
        print("    この場合、局所座標との突き合わせは実寸の裏付けになります。")
    else:
        print("    判定に使える区間がありません(歩いた距離か時間が足りません)。")
        print("    10秒以上・2m以上動いた区間が要ります。")
    return verdicts


def section_relative(use, segs):
    line("3. 相対（形と大きさ）  点群そのものの正しさ")
    print("  局所座標の移動量と、VPSの緯度経度から出した移動量を、")
    print("  離れ方(距離)ごとに比べます。")
    print("  ※§2のとおり、区間内の一致は『独立な裏付け』ではなく")
    print("    『破綻していないことの確認』です。数字の読み方に注意してください。")
    print("  ※高さは局所座標の写しなので、水平距離だけで比べています。")
    print()

    enu = to_enu(use)
    lh = local_horizontal(use)
    bins = [(0.5, 1), (1, 2), (2, 5), (5, 10), (10, 20), (20, 50), (50, 200)]

    def collect(pairs_from):
        got = {b: [] for b in bins}
        for i, j in pairs_from:
            dl = math.dist(lh[i], lh[j])
            if dl < 0.5:
                continue
            dv = math.hypot(enu[j][0] - enu[i][0], enu[j][1] - enu[i][1])
            for b in bins:
                if b[0] <= dl < b[1]:
                    got[b].append(dv / dl)
                    break
        return got

    def within():
        for a, b in segs:
            ii = range(a, b, 3)
            jj = list(range(a, b, 7))
            for i in ii:
                for j in jj:
                    if j > i:
                        yield i, j

    def across():
        if len(segs) < 2:
            return
        a1, b1 = segs[0]
        a2, b2 = segs[-1]
        for i in range(a1, b1, 3):
            for j in range(a2, b2, 7):
                yield i, j

    wi = collect(within())
    ac = collect(across())

    print("  【同じ区間の中で】 VPSが解を組み直していない範囲")
    print("    離れ方        組数     比 (VPS/局所)              ばらつき")
    print("                          5%     中央値    95%       (幅の半分)")
    best = None
    for b in bins:
        xs = wi[b]
        if len(xs) < 30:
            continue
        p5, _, med, _, p95 = quantiles(xs)
        print(f"    {b[0]:5.1f}-{b[1]:<5.1f} m {len(xs):7d}  "
              f"{p5:.4f} {med:9.4f} {p95:8.4f}   ±{(p95 - p5) / 2:.4f}")
        if b[0] >= 5:
            best = (b, med, p5, p95, len(xs))

    if best:
        b, med, p5, p95, n = best
        print()
        print(f"  {b[0]:.0f}m以上離れた組で 比 {med:.5f} "
              f"→ 差 {(med - 1) * 100:+.3f}%  "
              f"(100m あたり {(med - 1) * 10000:+.1f} cm)")
        print(f"    ばらつきは ±{(p95 - p5) / 2 * 100:.2f}%。")
        print("    ただしこれは独立な2つが一致したという意味ではありません。")
        print("    §2のとおり両者は固定の変換で結ばれているので、")
        print("    **一致して当たり前の量**です。ここで言えるのは")
        print("    『変換の当てはめと座標変換の実装に破綻がない』ことまでです。")

    if any(len(ac[b]) >= 30 for b in bins):
        print()
        print("  【区間をまたぐと】 同じ計算を、再照合をはさんだ組で")
        print("    離れ方        組数     比 (VPS/局所)")
        for b in bins:
            xs = ac[b]
            if len(xs) < 30:
                continue
            p5, _, med, _, p95 = quantiles(xs)
            print(f"    {b[0]:5.1f}-{b[1]:<5.1f} m {len(xs):7d}  "
                  f"{p5:.4f} {med:9.4f} {p95:8.4f}")
        print()
        print("    → 同じデータでも比が崩れます。これはスケールの誤りではなく、")
        print("      VPSが途中で座標を付け替えたせいです。**区間で切らずに測ると")
        print("      相対精度を実際より悪く見積もります**。")


def section_absolute(use, fits):
    line("4. 絶対（置き場所と向き）  地球のどこに置くか")

    # --- 3-1 自己申告 --------------------------------------------------
    print("  4-1. ARCoreの自己申告")
    for key, label, unit in (("acc_h", "水平精度", "m"),
                             ("acc_v", "垂直精度", "m"),
                             ("acc_yaw", "方位精度", "度")):
        xs = [r[key] for r in use if r[key] is not None]
        if not xs:
            continue
        p5, _, med, _, p95 = quantiles(xs)
        print(f"    {label}  中央値 {med:6.2f} {unit}"
              f"   (5% {p5:.2f} / 95% {p95:.2f})")
    print("    ※これは68%信頼半径です。3回に1回はこの外に出ます。")

    yaw = [(r["t"], r["acc_yaw"]) for r in use if r["acc_yaw"] is not None]
    if len(yaw) > 30:
        print()
        print(f"    方位精度は 開始 {yaw[0][1]:.2f} 度 → 終了 {yaw[-1][1]:.2f} 度")
        # 飛びを除いた区間で、単位時間あたりどれだけ膨らむか
        rates = []
        run = [yaw[0]]
        for k in range(1, len(yaw)):
            if yaw[k][1] - yaw[k - 1][1] < -0.3:
                if run[-1][0] - run[0][0] > 20:
                    rates.append((run[-1][1] - run[0][1])
                                 / (run[-1][0] - run[0][0]))
                run = [yaw[k]]
            else:
                run.append(yaw[k])
        if run[-1][0] - run[0][0] > 20:
            rates.append((run[-1][1] - run[0][1]) / (run[-1][0] - run[0][0]))
        if rates:
            print(f"    照合が入らない区間では 毎分 {st.mean(rates) * 60:+.3f} 度 "
                  "のペースで膨らみます")
            print("    → 現地では『歩いて周囲を映してから読む』が正しい、")
            print("      という現場手順の裏付けになります。")

    # --- 3-2 申告値と実測のつき合わせ ----------------------------------
    if fits:
        print()
        print("  4-2. 申告値は当たっているか")
        allres = [x for _k, f in fits for x in f["res"]]
        _, _, med, _, p95 = quantiles(allres)
        ah = [r["acc_h"] for r in use if r["acc_h"] is not None]
        print(f"    区間内の残差   中央値 {med:.3f} m / 95% {p95:.3f} m")
        if ah:
            print(f"    申告の水平精度 中央値 {st.median(ah):.2f} m")
        print("    残差が申告値よりずっと小さいのは矛盾ではありません。")
        print("    残差は『区間の中でどれだけ形が合うか』(相対)、")
        print("    申告値は『地球上のどこか』(絶対)を指しています。")
        print("    絶対のずれは区間全体を平行移動させるので、残差には出ません。")

    # --- 3-3 方位誤差の距離換算 ----------------------------------------
    ay = [r["acc_yaw"] for r in use if r["acc_yaw"] is not None]
    if ay:
        print()
        print("  4-3. 方位の誤差が、離れた場所で何mになるか")
        medy, besty = st.median(ay), min(ay)
        print(f"      基準点からの距離   方位{medy:.2f}度のとき   "
              f"最良の{besty:.2f}度でも")
        for d in (10, 25, 50, 100, 130, 200):
            print(f"        {d:4d} m          "
                  f"{d * math.tan(math.radians(medy)):6.2f} m"
                  f"           {d * math.tan(math.radians(besty)):6.2f} m")
        print("    → 点群の形が正しくても、基準点から離れるほど")
        print("      置き場所のずれが線形に増えます。フェーズ5の動機はここです。")


def section_height(rows, use):
    line("5. 高さ  alt_ellipsoid の出どころ（§6-1）")
    pair = [(r["t"], r["alt"], r["lp"][1]) for r in use
            if r["alt"] is not None and r["lp"][1] is not None]
    if len(pair) < 10:
        print("  高さの列が足りません")
        return
    ts = [p[0] for p in pair]
    off = [p[1] - p[2] for p in pair]        # alt_ellipsoid - local_py
    py = [p[2] for p in pair]
    alt = [p[1] for p in pair]

    print("  ARCoreが返す楕円体高から、局所座標の上下動(local_py)を引きます。")
    print("  高さが本当に毎回測位で求まっているなら、この差は測位の雑音で")
    print("  ふらつくはずです。")
    print()
    print(f"    local_py       範囲 {max(py) - min(py):.3f} m "
          f"(min {min(py):.3f} / max {max(py):.3f})")
    print(f"    alt_ellipsoid  範囲 {max(alt) - min(alt):.3f} m "
          f"(min {min(alt):.3f} / max {max(alt):.3f})")
    print(f"    その差         範囲 {max(off) - min(off):.3f} m "
          f"(中央値 {st.median(off):.3f} m)")

    d = [abs(off[i] - off[i - 1]) for i in range(1, len(off))]
    small = sum(1 for x in d if x <= 0.02)
    print(f"    差の変化量     中央値 {st.median(d) * 1000:.2f} mm/フレーム  "
          f"({small}/{len(d)} = {small / len(d) * 100:.1f}% が 20mm以下)")

    big = []
    for i in range(1, len(off)):
        if abs(off[i] - off[i - 1]) > 0.05:
            big.append((ts[i], off[i] - off[i - 1]))
    if big:
        print()
        print(f"  0.05 m を超える付け替え {len(big)} 回、"
              f"合計 {sum(x[1] for x in big):+.3f} m")

    print()
    print("  判定:")
    if st.median(d) < 0.005:
        print("    差はフレーム間でほぼ動かず、たまに段差で付け替わるだけです。")
        print("    **楕円体高は毎回測位した値ではなく、局所座標の上下動に")
        print("      定数を足したもの**です。VPSが照合し直したときだけ")
        print("      その定数が更新されます。")
        print()
        print("    意味するところ:")
        print("      * 垂直精度の値は『毎フレームの高さの精度』ではなく")
        print("        『足している定数の精度』です")
        print("      * 短時間の高低差は局所座標の精度(cm級)で信用できます")
        print("      * 絶対標高は定数まかせなので、別系統で与える必要があります")
        print("        (既知点合わせ、公開点群やDEMとの突合 = フェーズ5)")
        print()
        print("    残る問い: その定数がどこから来るのか(GNSSの鉛直か、地形データか)。")
        print("    同じ緯度経度でDEMを引いて比べれば分かります。")
    else:
        print("    差がフレームごとに動いています。高さは測位由来の可能性があります。")

    notes = sorted({r["note"] for r in rows if r["note"] and r["note"] != "-"})
    if len(notes) > 1:
        print()
        print("  メモ別の高さ(階の比較):")
        for nt in notes:
            xs = [r["alt"] for r in use if r["note"] == nt and r["alt"] is not None]
            ys = [r["lp"][1] for r in use if r["note"] == nt and r["lp"][1] is not None]
            if xs:
                print(f"    {nt:6s} alt中央値 {st.median(xs):8.3f} m  "
                      f"local_py中央値 {st.median(ys):7.3f} m  (n={len(xs)})")


def section_las(use, fits):
    """LAS書き出しが使う基準の取り方を、この撮影データで検算する"""
    line("6. LAS書き出しへの影響 (points_to_las.py の基準の取り方)")
    if not fits:
        print("  区間が取れないため省略します")
        return

    lh = local_horizontal(use)
    enu = to_enu(use)

    # points_to_las.py と同じ手順で基準を作る
    lats = sorted(r["lat"] for r in use)
    lons = sorted(r["lon"] for r in use)
    alts = sorted(r["alt"] for r in use)
    lat0, lon0, alt0 = (lats[len(lats) // 2], lons[len(lons) // 2],
                        alts[len(alts) // 2])
    pxs = sorted(r["lp"][0] for r in use)
    pys = sorted(r["lp"][1] for r in use)
    pzs = sorted(r["lp"][2] for r in use)
    ref = (pxs[len(pxs) // 2], pys[len(pys) // 2], pzs[len(pzs) // 2])

    # lat0/lon0 を先頭行原点の ENU に直す
    x0, y0, z0 = p2l.geodetic_to_ecef(use[0]["lat"], use[0]["lon"], use[0]["alt"])
    xa, ya, za = p2l.geodetic_to_ecef(lat0, lon0, alt0)
    la, lo = math.radians(use[0]["lat"]), math.radians(use[0]["lon"])
    sl, cl, so, co = math.sin(la), math.cos(la), math.sin(lo), math.cos(lo)
    dx, dy, dz = xa - x0, ya - y0, za - z0
    anchor = (-so * dx + co * dy, -sl * co * dx - sl * so * dy + cl * dz)

    print("  現在の実装は、基準の緯度経度と基準の局所座標を")
    print("  **それぞれ別々に並べ替えた中央値**で決めています。")
    print("  経路が曲がっていると、この2つは同じコマを指しません。")
    print()

    i_loc = min(range(len(use)),
                key=lambda i: math.dist(lh[i], (ref[0], ref[2])))
    i_geo = min(range(len(use)),
                key=lambda i: math.dist((enu[i][0], enu[i][1]), anchor))
    print(f"    基準の局所座標に近いコマ  t={use[i_loc]['t']:7.2f} 秒")
    print(f"    基準の緯度経度に近いコマ  t={use[i_geo]['t']:7.2f} 秒")
    if abs(use[i_loc]["t"] - use[i_geo]["t"]) > 1.0:
        print("    → 別のコマです。この2つを同じ点として結んでいます。")

    (a, b), flast = fits[-1]
    got = apply_fit(flast, (ref[0], ref[2]))
    print()
    print(f"    最後の区間の当てはめでの正しい行き先  E={got[0]:+.3f} N={got[1]:+.3f}")
    print(f"    いまの実装が置く先                    E={anchor[0]:+.3f} N={anchor[1]:+.3f}")
    print(f"    → 点群全体が {math.dist(got, anchor):.2f} m 平行移動します")

    # 回転の平均化
    rots = [p2l.q_normalize(p2l.q_mul(r["eq"], p2l.q_conj(r["lq"])))
            for r in use if None not in r["eq"] and None not in r["lq"]]
    if rots:
        m = p2l.q_to_matrix(p2l.q_average(rots))
        az_avg = math.degrees(math.atan2(m[0][0], m[2][0])) % 360
        print()
        print(f"    方位も全コマ平均で決めています: {az_avg:.2f} 度")
        print(f"    最後の区間の当てはめ:           {flast['az']:.2f} 度")
        d = ((az_avg - flast["az"] + 180) % 360) - 180
        print(f"    → {d:+.2f} 度の回り込み。50m先で "
              f"{50 * abs(math.tan(math.radians(d))):.2f} m")

    print()
    print("  対処の方向:")
    print("    * 基準は『1つのコマ』から取る(緯度経度と局所座標を同じ行から)")
    print("    * 方位と基準は**最後の再照合以降の区間**で決める。")
    print("      その区間がVPSの最も新しい解であり、平均は古い解を混ぜます")
    print("    * 区間ごとに当てはめ直して書き出す手もありますが、")
    print("      点群自体は局所座標でひとつながりなので、")
    print("      置き方を1つ選ぶほうが形は保たれます")


# ----------------------------------------------------------------------
def main():
    ap = argparse.ArgumentParser(
        description="frames.csv / geolog_*.csv から精度を測る (フェーズ4)")
    ap.add_argument("path", help="CSVファイル、または rec_* ディレクトリ")
    args = ap.parse_args()

    path = args.path
    if os.path.isdir(path):
        path = os.path.join(path, "frames.csv")
        if not os.path.exists(path):
            sys.exit(f"frames.csv が見つかりません: {args.path}")

    rows = read_csv(path)
    use = usable(rows)
    if len(use) < 30:
        sys.exit("局所座標と緯度経度が揃った行が足りません")

    print("=" * 66)
    print(f"精度レポート  "
          f"{os.path.basename(os.path.dirname(os.path.abspath(path)))}"
          f" / {os.path.basename(path)}")
    print("=" * 66)

    ev = find_events(use)
    segs = segments(use, ev)
    if not segs:
        segs = [(0, len(use))]

    section_overview(rows, use)
    fits = section_events(use, ev, segs)
    section_independence(use, segs)
    section_relative(use, segs)
    section_absolute(use, fits)
    section_height(rows, use)
    section_las(use, fits)

    line("まとめ")
    print("  1. 相対(形と大きさ)と絶対(置き場所と向き)は別の数字です。")
    print("     記事に書くときは必ず分けてください。混ぜると読者は")
    print("     悪いほうの数字だけを『この手法の精度』として受け取ります。")
    print()
    print("  2. 局所座標とVPSの一致は、実寸の独立な裏付けには")
    print("     なりません(§2)。実寸を主張するには外部の基準が要ります。")
    print()


if __name__ == "__main__":
    main()
