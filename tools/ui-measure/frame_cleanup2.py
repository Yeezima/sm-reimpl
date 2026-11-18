"""Second post-process of the panel frame art (ui/overhaul, Opus review 3): the page frame (overlay-panel.png) still showed three flaws in captures:
  1. a tone change in the separator line under the tab strip where the first cleanup's copy began (x 1985, y 425..475),
  2. the bottom separator and the tray row, copper-toned on the left and grey on the right with a step at x 2077.
The separator is made one plain profile across its length and the lower part of the frame (y 1040 down)
is mirrored from its right half onto its left, so the frame is symmetric again. Works from the committed art; run once.
usage: python frame_cleanup2.py <client/assets/ui>
"""
import sys, os
import numpy as np
from PIL import Image

d = sys.argv[1]
p = os.path.join(d, 'overlay-panel.png')
a = np.asarray(Image.open(p).convert('RGBA')).copy()
h, w = a.shape[:2]

# 1. the separator line: every row of the band takes the mean of a plain window (x 600..800), across the line's whole length
band = slice(425, 476)
profile = a[band, 600:800, :].astype(np.float32).mean(axis=1)           # rows x 4
a[band, 280:2650, :] = profile[:, None, :].astype(np.uint8)

# 2. the lower frame: mirrored from the right half (about the skull's centre, x 1470)
y0 = 1040
right = a[y0:, 1470:2941, :]
a[y0:, 0:1471, :] = right[:, ::-1, :]
a[y0:, 1470, :] = a[y0:, 1471, :]   # no tick at the mirror's join

Image.fromarray(a, 'RGBA').save(p)
print('overlay-panel.png cleaned again')
