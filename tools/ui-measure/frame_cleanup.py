"""Post-process of the panel frame art (ui/overhaul, Opus review 1): removes the seam in the separator line and the lighter patch at the top band's right
half (the concept's own art differs there), and cuts the plate textures' dark backing so only the bevelled plate remains.
usage: python frame_cleanup.py <client/assets/ui>
"""
import sys, os
import numpy as np
from PIL import Image
import cv2

d = sys.argv[1]

def tr(a, x0, x1, y0, y1, dx):
    # translate a plain stretch: the pixels of [x0,x1) x [y0,y1) come from dx columns to the left
    a[y0:y1, x0:x1] = a[y0:y1, x0 + dx:x1 + dx]

for name in ('overlay-panel.png', 'overlay-options.png'):
    p = os.path.join(d, name)
    a = np.asarray(Image.open(p).convert('RGBA')).copy()
    # the lighter right half of the top band (y 300..360) and the seam of the separator line (y 425..475): take the left half's pixels
    tr(a, 1985, 2655, 327, 362, -1000)
    tr(a, 1990, 2650, 425, 475, -1000)
    Image.fromarray(a, 'RGBA').save(p)
    print(name, 'cleaned')

for name in ('opt-plate-blank.png', 'opt-plate-gold-blank.png'):
    p = os.path.join(d, name)
    im = Image.open(p).convert('RGBA'); a = np.asarray(im).copy()
    al = a[..., 3]
    solid = (al > 235).astype(np.uint8)
    solid = cv2.erode(solid, np.ones((3, 3), np.uint8), iterations=3)   # the plate's own bevel starts inside the soft ring
    soft = cv2.GaussianBlur(solid.astype(np.float32), (0, 0), 1.0)
    a[..., 3] = np.minimum(al, (soft * 255).astype(np.uint8))
    Image.fromarray(a, 'RGBA').save(p)
    print(name, 'alpha cut to the bevel')
