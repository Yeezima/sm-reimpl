"""Re-cuts the main menu's SM-REIMPL wordmark from the owner's sharper reference (2026-11-18: the wordmark was clipped and misshapen around MPL).
The owner's image (2000 x 1125, outside the repo) is registered onto the 4x overlay with the similarity from the v2 reference (scale 2.0512, offset -37.65 / -79.04 in v2's full-image pixels, SIFT + RANSAC, 698 inliers).
The old letters (and their ragged matte) are removed from overlay-main.png (the rock behind them is inpainted from its surroundings, the sky stays transparent) and the new letters, their bevel and drop shadow are laid in from the
reference. wordmark.png (the live wordmark of the non-overlay pages) is re-cut from the same pixels.
usage: python wordmark_recut.py <client/assets/ui> <owner reference image> [<work dir for checks>]
"""
import sys, os
import numpy as np
import cv2
from PIL import Image

ui, refp = sys.argv[1], sys.argv[2]
work = sys.argv[3] if len(sys.argv) > 3 else None
S, OX, OY = 2.05119383, -37.6500268, -79.0353288          # v2 full image -> reference
ref = np.asarray(Image.open(refp).convert('RGB')).copy()
ov_path = os.path.join(ui, 'overlay-main.png')
ov = np.asarray(Image.open(ov_path).convert('RGBA')).copy()
H, W = ov.shape[:2]

# overlay px -> v2 screen px (/4) -> v2 full px (+ (18, 38)) -> reference px
k = S / 4.0
ref_x0 = S * 18 + OX
ref_y0 = S * 38 + OY
# the matrix that takes reference pixels to overlay pixels
Mr2o = np.array([[1 / k, 0, -ref_x0 / k], [0, 1 / k, -ref_y0 / k]], np.float32)

# ---- the letters' matte on the reference's own pixels ----
x0, y0, x1, y1 = 80, 215, 1000, 420
roi = ref[y0:y1, x0:x1]
hsv = cv2.cvtColor(roi, cv2.COLOR_RGB2HSV)
Hh, Ss, Vv = hsv[..., 0].astype(int), hsv[..., 1].astype(int), hsv[..., 2].astype(int)
blue = (Hh >= 95) & (Hh <= 125) & (Ss > 70) & (Vv > 55)
blue = cv2.morphologyEx(blue.astype(np.uint8), cv2.MORPH_CLOSE, np.ones((5, 5), np.uint8))
blue = cv2.morphologyEx(blue, cv2.MORPH_OPEN, np.ones((3, 3), np.uint8))
n, lab, stats, _ = cv2.connectedComponentsWithStats(blue, 8)
keep = np.zeros_like(blue)
for i in range(1, n):
    if stats[i, cv2.CC_STAT_AREA] > 220: keep[lab == i] = 1          # the letters' faces, not specks
blue = keep
near = lambda r: cv2.dilate(blue, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (2 * r + 1, 2 * r + 1)))
fog = (Vv > 105) & (Ss < 55)                                         # the grey sky and smoke
gold = (Hh >= 5) & (Hh <= 30) & (Ss > 60)                            # the bevel's brass edge
dark = Vv < 105
bevel = near(4) & (~fog | gold)
outline = near(10) & (gold | dark) & ~fog
shape = (blue | bevel.astype(bool) | outline.astype(bool)).astype(np.uint8)
shape = cv2.morphologyEx(shape, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (7, 7)))
# counters (the holes of R, P, E...) stay letter-coloured: small enclosed holes are filled
inv = (1 - shape).astype(np.uint8)
n2, lab2, st2, _ = cv2.connectedComponentsWithStats(inv, 4)
for i in range(1, n2):
    if st2[i, cv2.CC_STAT_AREA] < 3500 and st2[i, cv2.CC_STAT_LEFT] > 0 and st2[i, cv2.CC_STAT_TOP] > 0: shape[lab2 == i] = 1
# the drop shadow: dark pixels in a wider reach, kept partly transparent
shadow = (near(16).astype(bool) & (Vv < 80) & ~shape.astype(bool))
alpha = shape.astype(np.float32)
alpha = np.maximum(alpha, shadow.astype(np.float32) * np.clip((80 - Vv) / 80.0, 0, 1) * 0.85)
alpha = cv2.GaussianBlur(alpha, (0, 0), 0.9)
full_alpha = np.zeros(ref.shape[:2], np.float32); full_alpha[y0:y1, x0:x1] = alpha

# ---- onto the overlay ----
ref_up = cv2.warpAffine(ref, Mr2o, (W, H), flags=cv2.INTER_CUBIC)
a_up = cv2.warpAffine(full_alpha, Mr2o, (W, H), flags=cv2.INTER_CUBIC)
a_up = np.clip(a_up, 0, 1)

band0, band1 = 380, 775
old = ov.astype(np.float32)
# the rock behind the letters: the overlay's alpha above and below the band, blended across it
a_top, a_bot = old[band0 - 4, :, 3].copy(), old[band1 + 4, :, 3].copy()
rock_zone_x = 560
for y in range(band0, band1):
    t = (y - band0) / float(band1 - band0)
    ov[y, rock_zone_x:, :] = 0
    rock_a = (1 - t) * a_top[:rock_zone_x] + t * a_bot[:rock_zone_x]
    ov[y, :rock_zone_x, 3] = np.clip(rock_a, 0, 255).astype(np.uint8)
ov[band1:850, 1450:, :] = 0          # the old letters' drop shadow beyond the crest's right wing
# the rock's colour where the old letters stood: inpaint from the stone round them
rgb = np.ascontiguousarray(ov[band0:band1, :rock_zone_x, :3])
oh = cv2.cvtColor(rgb, cv2.COLOR_RGB2HSV)
oldl = ((oh[..., 0] >= 95) & (oh[..., 0] <= 125) & (oh[..., 1] > 60)).astype(np.uint8)
oldl = cv2.dilate(oldl, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (41, 41)))
# colours under the old letters come from the stone left of them (mirrored columns), not from a smear
rock_left = rgb[:, :230, :].copy()
fill = cv2.inpaint(rgb, oldl, 9, cv2.INPAINT_TELEA)
ov[band0:band1, :rock_zone_x, :3] = fill

# new letters over (straight alpha compositing onto the cleaned band)
for y in range(band0, band1):
    pass
sub = ov[band0:band1].astype(np.float32)
na = a_up[band0:band1][..., None]
nc = ref_up[band0:band1].astype(np.float32)
oa = sub[..., 3:4] / 255.0
out_a = na + oa * (1 - na)
out_c = np.where(out_a > 1e-4, (nc * na + sub[..., :3] * oa * (1 - na)) / np.maximum(out_a, 1e-4), 0)
ov[band0:band1, :, :3] = np.clip(out_c, 0, 255).astype(np.uint8)
ov[band0:band1, :, 3] = np.clip(out_a[..., 0] * 255, 0, 255).astype(np.uint8)
Image.fromarray(ov, 'RGBA').save(ov_path)
print('overlay-main.png: wordmark re-cut')

# ---- wordmark.png: the same letters, tight, at the reference's own pixels (aspect kept) ----
ys, xs = np.where(full_alpha > 0.5)
bx0, bx1, by0, by1 = xs.min() - 6, xs.max() + 6, ys.min() - 6, ys.max() + 6
rgba = np.dstack([ref[by0:by1, bx0:bx1], (np.clip(full_alpha[by0:by1, bx0:bx1], 0, 1) * 255).astype(np.uint8)])
Image.fromarray(rgba, 'RGBA').save(os.path.join(ui, 'wordmark.png'))
print('wordmark.png', rgba.shape)
if work:
    bg = Image.new('RGBA', (W, H), (255, 0, 255, 255)); bg.alpha_composite(Image.fromarray(ov, 'RGBA'))
    bg.crop((0, 300, W, 1000)).resize((W // 2, 350)).save(os.path.join(work, 'recut_check.png'))
