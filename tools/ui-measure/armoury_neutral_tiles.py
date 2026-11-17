import sys, numpy as np
from PIL import Image
# Turns the concept's navy icon-tile backing (dark, blue-dominant) into neutral dark steel so an empty tile reads as an empty tile.
for name in sys.argv[1:]:
    im = Image.open(name).convert('RGBA'); a = np.asarray(im).astype(np.float32)
    r, g, b, al = a[..., 0], a[..., 1], a[..., 2], a[..., 3]
    lum = (0.2126 * r + 0.7152 * g + 0.0722 * b)
    navy = (b > 1.5 * r + 6) & (b > 1.15 * g + 4) & (lum < 110) & (al > 0)
    # soft weight so the edge of the region blends
    out = a.copy()
    tone = np.clip(lum * 1.0 + 8, 0, 255)
    for c, k in ((0, 1.0), (1, 0.97), (2, 0.92)):
        out[..., c] = np.where(navy, tone * k, a[..., c])
    Image.fromarray(out.astype(np.uint8), 'RGBA').save(name)
    print(name, int(navy.sum()), 'px recoloured')
