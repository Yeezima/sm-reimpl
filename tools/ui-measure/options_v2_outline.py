"""
Measures the Options frame's silhouette on the v2 reference (owner-ref-panel04-v2.png, 825 x 465) and its Real-ESRGAN x4 (3300 x 1860), for
UiTextureBuilder.OptionsV2.cs (docs/ui-concept-textures.md "Phase 4b-2"). Developer aid, not part of any build; needs numpy and opencv-python.

    python tools/ui-measure/options_v2_outline.py <panel04-v2-x4.png> [--emit <UiTextureBuilder.OptionsV2.cs>]

It keys the scene round the frame (a flood from the borders that steps only over smooth, equal pixels: the steel's edge stops it), closes the
leaks into the interior, drops the dark shadow fringe from the outside in (not at the stones), makes the steel mirror-symmetric about x = 412
(left half kept where both halves agree, right half its mirror) and writes the outline as a polygon of 1/100 px v2 coordinates. The stone pieces'
left edge is scanned per row (the stone is darker than the scene), the inner one of the two sides is kept, and the chain is simplified.
With --emit the two arrays in the source file are replaced; without it they are printed.
"""
import sys, re, cv2, numpy as np

K = 4
AXIS = 412.0   # v2 pixels: the frame's mirror axis (found by correlation, 0.5 px steps)

def flood_silhouette(im4):
    h, w = im4.shape[:2]
    g = cv2.GaussianBlur(im4, (0, 0), 1.2)
    m = np.zeros((h + 2, w + 2), np.uint8)
    flags = 4 | (255 << 8) | cv2.FLOODFILL_MASK_ONLY
    for sx, sy in [(5, 5), (w - 5, 5), (5, h - 5), (w - 5, h - 5), (w // 2, 5), (5, h // 2), (w - 5, h // 2), (w // 2, h - 5), (300, h - 5), (w - 300, h - 5)]:
        cv2.floodFill(g.copy(), m, (sx, sy), None, (4,) * 3, (4,) * 3, flags)
    nb = (m[1:-1, 1:-1] == 0).astype(np.uint8)
    hull = np.zeros((h, w), np.uint8); cv2.rectangle(hull, (40 * K, 25 * K), (785 * K, 375 * K), 1, -1)
    nb &= hull
    cl = cv2.morphologyEx(nb, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (25, 25)))
    cnts, _ = cv2.findContours(cl, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    sil = np.zeros((h, w), np.uint8)
    cv2.drawContours(sil, [c for c in cnts if cv2.contourArea(c) > 3000], -1, 1, -1)
    sil[355 * K:, :] = 0   # the toast chip below the frame
    return sil

def mirror(a, w):
    return np.roll(a[:, ::-1], -(w - 2 * int(AXIS * K)), axis=1)   # pixel x <-> 2 * 1648 - 1 - x

def steel_polygon(im4, sil):
    h, w = sil.shape
    s = cv2.medianBlur(sil * 255, 9) > 0
    core = s & mirror(s, w)
    x = np.arange(w)[None, :] * np.ones((h, 1)); y = np.arange(h)[:, None] * np.ones((1, w))
    zl = ((x < 93 * K) & (y > 86 * K)) | ((x < 102 * K) & (y < 100 * K) & (y > 80 * K)); zr = mirror(zl, w)
    core &= ~zl & ~zr
    fin = (core | (s & zl)).astype(np.uint8); fr = (core | (s & zr)).astype(np.uint8)
    fin[:, w // 2:] = fr[:, w // 2:]
    fin = cv2.medianBlur(fin * 255, 7) > 0
    # the dark shadow fringe, eaten from the outside in (the steel is brighter; not at the stones, which are dark themselves)
    lum = cv2.cvtColor(cv2.GaussianBlur(im4, (0, 0), 1.0), cv2.COLOR_BGR2GRAY).astype(float)
    xs = np.arange(w)[None, :] + 0.5; zone = (xs >= 372) & (xs <= 2924)
    cross = np.array([[0, 1, 0], [1, 1, 1], [0, 1, 0]], np.uint8)
    for _ in range(40):
        er = cv2.erode(fin.astype(np.uint8), cross) > 0
        kill = fin & ~er & (lum < 50) & zone
        if not kill.any(): break
        fin &= ~kill
    steel = (fin & (xs >= 370) & (xs <= 2926)).astype(np.uint8)
    half = cv2.medianBlur(cv2.morphologyEx(steel[:, :int(AXIS * K)], cv2.MORPH_OPEN, np.ones((3, 3), np.uint8)) * 255, 9)
    cnts, _ = cv2.findContours((half > 0).astype(np.uint8), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    ap = cv2.approxPolyDP(max(cnts, key=cv2.contourArea), 2.0, True)[:, 0, :].astype(float)
    n = len(ap); seam = [i for i, (px, _) in enumerate(ap) if px >= 1645]
    top = min(seam, key=lambda i: ap[i][1]); bot = max(seam, key=lambda i: ap[i][1])
    def chain(a, b, st):
        out = []; i = a
        while True:
            out.append(i)
            if i == b: break
            i = (i + st) % n
        return out
    c1, c2 = chain(top, bot, 1), chain(top, bot, -1)
    pick = c1 if sum(1 for i in c1 if ap[i][0] < 1640) > sum(1 for i in c2 if ap[i][0] < 1640) else c2
    L = [(px + 0.5, py + 0.5) for px, py in ap[pick]]
    L[0] = (AXIS * K, L[0][1]); L[-1] = (AXIS * K, L[-1][1])
    R = [(2 * AXIS * K - px, py) for px, py in reversed(L)]
    return [(round(a / K, 2), round(b / K, 2)) for a, b in L + R[1:-1]]

def stone_left_edge(im4):
    g = cv2.cvtColor(cv2.GaussianBlur(im4, (0, 0), 1.5), cv2.COLOR_BGR2GRAY).astype(float)
    def scan(side):
        out = {}
        for y in range(86 * K, 350 * K):
            row = g[y]
            if side == 'L':
                ref = row[42 * K:44 * K].mean()
                e = next((x for x in range(44 * K, 94 * K) if row[x] < ref - 16), None)
            else:
                ref = row[(824 - 42) * K - 2:(824 - 42) * K].mean()
                e = next((x for x in range(824 * K - 44 * K, 824 * K - 94 * K, -1) if row[x] < ref - 16), None)
            if e is not None: out[y] = e
        return out
    el, er = scan('L'), scan('R')
    ys, xs = [], []
    for y in range(100 * K, 317 * K):
        c = ([el[y]] if y in el else []) + ([2 * AXIS * K - er[y]] if y in er and y < 305 * K else [])
        if c: ys.append(y); xs.append(max(c))   # the inner of the two sides
    xs = np.array([np.median(xs[max(0, i - 12):i + 13]) for i in range(len(xs))], float)
    pts = np.stack([xs, np.array(ys, float)], 1).astype(np.float32).reshape(-1, 1, 2)
    return [(round(float(a) / K, 2), round(float(b) / K, 2)) for a, b in cv2.approxPolyDP(pts, 4.0, False)[:, 0, :]]

def lit(pts):
    return ',\n        '.join(', '.join(f'new({x}f, {y}f)' for x, y in pts[i:i + 6]) for i in range(0, len(pts), 6))

def main():
    if len(sys.argv) < 2: print(__doc__); return 2
    im4 = cv2.imread(sys.argv[1])
    if im4 is None or im4.shape[1] != 825 * K: print('give the x4 of the v2 reference (3300 x 1860)'); return 2
    steel = steel_polygon(im4, flood_silhouette(im4)); stone = stone_left_edge(im4)
    print(f'steel {len(steel)} vertices, stone edge {len(stone)} vertices')
    if '--emit' in sys.argv:
        p = sys.argv[sys.argv.index('--emit') + 1]; t = open(p, encoding='utf-8').read()
        a = t.index('V2Steel =\n    [\n') + len('V2Steel =\n    [\n'); b = t.index('\n    ];', a)
        t = t[:a] + '        ' + lit(steel) + ',' + t[b:]
        open(p, 'w', encoding='utf-8').write(t)
        print('steel written; the stone chain (check by eye, the cap and the inner side are hand-set):', stone)
    else:
        print(lit(steel)); print(stone)
    return 0

if __name__ == '__main__':
    sys.exit(main())
