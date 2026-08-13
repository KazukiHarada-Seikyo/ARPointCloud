#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""巻尺を撮った連番フレームから、ARCoreの実寸(スケール)を検証する。

フェーズ4の最後の穴だったもの。**外部の基準に対する実寸の裏付け**。

  1. 各コマで巻尺(黒い壁の上の細長い明るい帯)を見つけ、両端を
     「帯らしさが半分になる位置」でサブピクセルに決める
  2. ARCoreの局所姿勢から視線を作り、両端をそれぞれ三角測量する
  3. 2点間の距離を、巻尺の読みと比べる

**VPSも緯度経度も一切使わない。** 加速度計とカメラだけで出した長さを
物差しと突き合わせる。PHASE4_ACCURACY.md §2 で「局所座標とVPSの一致は
実寸の裏付けにならない」と分かったので、これが唯一の外部検証になる。

--------------------------------------------------------------------
撮り方が結果を決める

三角測量なので、**基準物を色々な位置から撮る**必要がある。
2026-08-13の実測では、最初の45コマ(基線長7cm)だけだと 136 cm、
歩き回った42コマ(基線長72cm)だと 172.6 cm になった。実長は173cm。

  基線長 / 対象までの距離 が 0.3 以上あること。
  立ち止まって撮るだけでは足りない。**基準物のまわりを歩くこと。**
--------------------------------------------------------------------

Pillow が要る(画像を読むため)。他のツールと違って標準ライブラリだけでは
動かない。解析用なので本筋のパイプラインには影響しない。
"""

import argparse
import csv, json, math, os, sys, statistics as st
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import frames_to_colmap as f2c

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

TRUE_LEN = 1.73          # --true-length で上書きする



# ---------------- 画像側 ----------------
def sample(px, W, H, x, y):
    if not (0 <= x < W - 1 and 0 <= y < H - 1):
        return None
    x0, y0 = int(x), int(y)
    fx, fy = x - x0, y - y0
    a = px[x0, y0] * (1 - fx) + px[x0 + 1, y0] * fx
    b = px[x0, y0 + 1] * (1 - fx) + px[x0 + 1, y0 + 1] * fx
    return a * (1 - fy) + b * fy


def band_score(px, W, H, x, y, ax, ay, d):
    nx, ny = -ay, ax
    core = -1.0
    for t in (-5, -3, -1, 1, 3, 5):
        v = sample(px, W, H, x + nx * t, y + ny * t)
        if v is not None and v > core:
            core = v
    if core < 0:
        return None
    bg = []
    for sgn in (-1, 1):
        vals = [sample(px, W, H, x + nx * t * sgn, y + ny * t * sgn)
                for t in (d - 3, d, d + 3)]
        vals = [v for v in vals if v is not None]
        if not vals:
            return None
        vals.sort()
        bg.append(vals[len(vals) // 2])
    return core - max(bg)


def detect(path, d=16, thresh=50):
    im = Image.open(path).convert("L")
    W, H = im.size
    px = im.load()

    pts = []
    for y in range(d, H - d, 2):
        for x in range(0, W, 2):
            v = px[x, y]
            if v < 85:
                continue
            if v - max(px[x, y - d], px[x, y + d]) > thresh:
                pts.append((x, y))
    if len(pts) < 150:
        return None

    cell = 20
    grid = {}
    for p in pts:
        grid.setdefault((p[0] // cell, p[1] // cell), []).append(p)
    seen, comps = set(), []
    for k0 in grid:
        if k0 in seen:
            continue
        stack, comp = [k0], []
        seen.add(k0)
        while stack:
            k = stack.pop()
            comp += grid[k]
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    nk = (k[0] + dx, k[1] + dy)
                    if nk in grid and nk not in seen:
                        seen.add(nk)
                        stack.append(nk)
        if len(comp) >= 150:
            comps.append(comp)

    best = None
    for c in comps:
        n = len(c)
        mx = sum(p[0] for p in c) / n
        my = sum(p[1] for p in c) / n
        sxx = sum((p[0] - mx) ** 2 for p in c) / n
        syy = sum((p[1] - my) ** 2 for p in c) / n
        sxy = sum((p[0] - mx) * (p[1] - my) for p in c) / n
        th = 0.5 * math.atan2(2 * sxy, sxx - syy)
        ax, ay = math.cos(th), math.sin(th)
        maj = sorted((p[0] - mx) * ax + (p[1] - my) * ay for p in c)
        mnr = sorted(-(p[0] - mx) * ay + (p[1] - my) * ax for p in c)
        span = maj[-1] - maj[0]
        wid = mnr[int(n * 0.95)] - mnr[int(n * 0.05)]
        # 巻尺は「長くて細い」。壁の縁や路面はここで落ちる
        if span < 400 or wid > 26 or span / max(1.0, wid) < 30:
            continue
        if best is None or span > best[0]:
            best = (span, (mx, my), (ax, ay), maj[0], maj[-1])
    if best is None:
        return None

    span, (mx, my), (ax, ay), lo, hi = best

    core = []
    s = lo + span * 0.25
    while s < lo + span * 0.75:
        v = band_score(px, W, H, mx + s * ax, my + s * ay, ax, ay, d)
        if v is not None:
            core.append(v)
        s += 1.0
    if len(core) < 20:
        return None
    core.sort()
    blade = core[len(core) // 2]
    if blade < 40:
        return None
    level = blade * 0.5

    def edge(s_start, direction):
        prev_s = s_start
        prev_v = band_score(px, W, H, mx + s_start * ax, my + s_start * ay, ax, ay, d)
        if prev_v is None or prev_v < level:
            return None
        t = 0.0
        while t < 300:
            t += 0.5
            s = s_start + direction * t
            v = band_score(px, W, H, mx + s * ax, my + s * ay, ax, ay, d)
            if v is None:
                return None
            if v < level:
                f = (prev_v - level) / max(1e-6, prev_v - v)
                return prev_s + (s - prev_s) * f
            prev_s, prev_v = s, v
        return None

    e_lo = edge(lo + span * 0.12, -1)
    e_hi = edge(hi - span * 0.12, +1)
    if e_lo is None or e_hi is None:
        return None

    p0 = (mx + e_lo * ax, my + e_lo * ay)
    p1 = (mx + e_hi * ax, my + e_hi * ay)
    m = 15
    for p in (p0, p1):
        if not (m < p[0] < W - m and m < p[1] < H - m):
            return None
    return p0, p1, math.dist(p0, p1)


# ---------------- 幾何側 ----------------
def w2c(px_, py_, pz_, qx, qy, qz, qw, roll_deg=90):
    right, up, forward = f2c.quat_to_axes(qx, qy, qz, qw)
    s = lambda v: (v[0], v[1], -v[2])
    neg = lambda v: (-v[0], -v[1], -v[2])
    cx_, cy_, cz_ = s(right), neg(s(up)), s(forward)
    if roll_deg:
        c = round(math.cos(math.radians(roll_deg)))
        sn = round(math.sin(math.radians(roll_deg)))
        nx = tuple(c * cx_[i] + sn * cy_[i] for i in range(3))
        ny = tuple(-sn * cx_[i] + c * cy_[i] for i in range(3))
        cx_, cy_ = nx, ny
    return (cx_, cy_, cz_), (px_, py_, -pz_)


def ray(R, u, v, fx, fy, cx, cy):
    d = ((u - cx) / fx, (v - cy) / fy, 1.0)
    w = tuple(sum(R[k][j] * d[k] for k in range(3)) for j in range(3))
    n = math.sqrt(sum(c * c for c in w))
    return tuple(c / n for c in w)


def solve3(A, b):
    M = [list(A[i]) + [b[i]] for i in range(3)]
    for i in range(3):
        p = max(range(i, 3), key=lambda r: abs(M[r][i]))
        if abs(M[p][i]) < 1e-12:
            return None
        M[i], M[p] = M[p], M[i]
        for r in range(3):
            if r == i:
                continue
            f = M[r][i] / M[i][i]
            for c in range(i, 4):
                M[r][c] -= f * M[i][c]
    return tuple(M[i][3] / M[i][i] for i in range(3))


def triangulate(obs):
    A = [[0.0] * 3 for _ in range(3)]
    b = [0.0] * 3
    for C, d in obs:
        for i in range(3):
            for j in range(3):
                m = (1.0 if i == j else 0.0) - d[i] * d[j]
                A[i][j] += m
                b[i] += m * C[j]
    return solve3(A, b)


def reproj(P, R, C, fx, fy, cx, cy):
    X = tuple(P[j] - C[j] for j in range(3))
    cam = tuple(sum(R[i][j] * X[j] for j in range(3)) for i in range(3))
    if cam[2] <= 0.01:
        return None
    return (fx * cam[0] / cam[2] + cx, fy * cam[1] / cam[2] + cy)


def main():
    ap = argparse.ArgumentParser(
        description="巻尺の写った連番フレームからARCoreの実寸を検証する")
    ap.add_argument("rec_dir", help="frames.csv のある rec_ フォルダ")
    ap.add_argument("--true-length", type=float, required=True, metavar="M",
                    help="巻尺の読み [m]")
    ap.add_argument("--stride", type=int, default=1,
                    help="何コマに1つ調べるか (既定1。遅ければ増やす)")
    ap.add_argument("--cache", default=None,
                    help="検出結果のJSON。次回から読み直しを省ける")
    args = ap.parse_args()

    global TRUE_LEN
    TRUE_LEN = args.true_length
    D = args.rec_dir

    rows = list(csv.DictReader(
        open(os.path.join(D, "frames.csv"), encoding="utf-8-sig")))

    if args.cache and os.path.exists(args.cache):
        dets = json.load(open(args.cache))
        print(f"検出結果を再利用: {len(dets)} 枚")
    else:
        dets = []
        for i in range(0, len(rows), args.stride):
            res = detect(os.path.join(D, rows[i]["filename"]))
            if res is None:
                continue
            p0, p1, L = res
            dets.append({"i": i, "p0": list(p0), "p1": list(p1), "L": L})
        print(f"巻尺が写っていたコマ: {len(dets)} / {len(rows) // args.stride}")
        if args.cache:
            json.dump(dets, open(args.cache, "w"))

    if len(dets) < 8:
        sys.exit("巻尺の写ったコマが足りません(8枚以上要ります)")

    # 姿勢を引く
    recs = []
    for d in dets:
        r = rows[d["i"]]
        R, C = w2c(float(r["local_px"]), float(r["local_py"]), float(r["local_pz"]),
                   float(r["local_qx"]), float(r["local_qy"]),
                   float(r["local_qz"]), float(r["local_qw"]))
        recs.append({"i": d["i"], "R": R, "C": C,
                     "fx": float(r["fx"]), "fy": float(r["fy"]),
                     "cx": float(r["cx"]), "cy": float(r["cy"]),
                     "p0": tuple(d["p0"]), "p1": tuple(d["p1"]), "L": d["L"]})

    base = max(math.dist(a["C"], b["C"]) for a in recs for b in recs)
    print(f"カメラ位置の広がり(最大基線長) {base:.3f} m")

    # 外れ値を落としながら三角測量
    for it in range(6):
        obs0 = [(r["C"], ray(r["R"], r["p0"][0], r["p0"][1],
                             r["fx"], r["fy"], r["cx"], r["cy"])) for r in recs]
        obs1 = [(r["C"], ray(r["R"], r["p1"][0], r["p1"][1],
                             r["fx"], r["fy"], r["cx"], r["cy"])) for r in recs]
        P0, P1 = triangulate(obs0), triangulate(obs1)
        if P0 is None or P1 is None:
            sys.exit("解けません")

        errs = []
        for r in recs:
            q0 = reproj(P0, r["R"], r["C"], r["fx"], r["fy"], r["cx"], r["cy"])
            q1 = reproj(P1, r["R"], r["C"], r["fx"], r["fy"], r["cx"], r["cy"])
            e = 1e9 if (q0 is None or q1 is None) else max(
                math.dist(q0, r["p0"]), math.dist(q1, r["p1"]))
            errs.append(e)
        med = st.median(errs)
        L = math.dist(P0, P1)
        print(f"  反復{it}: {len(recs)}枚 長さ {L*100:6.2f} cm  残差中央値 {med:5.2f} px")
        keep = [r for r, e in zip(recs, errs) if e < max(3.0, med * 2.5)]
        if len(keep) == len(recs) or len(keep) < 8:
            break
        recs = keep

    L = math.dist(P0, P1)
    dists = [math.dist(r["C"], P0) for r in recs]
    print(f"\n{'='*54}")
    print(f"三角測量による長さ : {L:.4f} m ({L*100:.1f} cm)")
    print(f"巻尺の読み         : {TRUE_LEN:.4f} m ({TRUE_LEN*100:.1f} cm)")
    print(f"差                 : {(L-TRUE_LEN)*100:+.2f} cm")
    print(f"比 (計測/実長)     : {L/TRUE_LEN:.5f}  → {(L/TRUE_LEN-1)*100:+.2f}%")
    print(f"{'='*54}")
    print(f"使ったコマ {len(recs)} 枚 / 壁までの距離 {min(dists):.2f}〜{max(dists):.2f} m")
    print(f"最大基線長 {max(math.dist(a['C'],b['C']) for a in recs for b in recs):.3f} m")

    for r in recs[:1] + recs[len(recs)//2:len(recs)//2+1] + recs[-1:]:
        im = Image.open(os.path.join(D, rows[r["i"]]["filename"])).convert("RGB")
        dr = ImageDraw.Draw(im)
        dr.line([r["p0"], r["p1"]], fill=(255, 0, 0), width=2)
        for e in (r["p0"], r["p1"]):
            dr.ellipse([e[0]-10, e[1]-10, e[0]+10, e[1]+10], outline=(0,255,0), width=3)
        im.save(os.path.join(D, f"scale_check_{r['i']:04d}.png"))


if __name__ == "__main__":
    main()
