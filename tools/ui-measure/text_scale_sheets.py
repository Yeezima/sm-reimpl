"""ui/text-scale evidence: before/after contact sheets per screen (one row per resolution) and 2x zoom crops of text and slider edges.
usage: python text_scale_sheets.py <media folder with before/ and after/> [screen ...]
Reads <name>-<WxH>.png from before/ and after/; writes sheets/<screen>.png and zoom/<screen>-<WxH>.png. Pillow only; no game files are involved."""
import os, sys
from PIL import Image, ImageDraw

root = sys.argv[1]
RES = ["1280x720", "1920x1080", "2560x1440", "2560x1080", "1280x1024"]
# text crop (fractions x0, y0, x1, y1) per screen; Options also gets the slider and pill edge crop.
TEXT = {"main": (0.08, 0.38, 0.5, 0.88), "options-graphics": (0.12, 0.30, 0.52, 0.55), "options-sound": (0.12, 0.30, 0.52, 0.55), "options-controls": (0.12, 0.30, 0.52, 0.55),
        "options-interface": (0.12, 0.30, 0.52, 0.55), "armoury": (0.04, 0.2, 0.40, 0.55)}
SLIDER = {"options-graphics": (0.62, 0.44, 0.88, 0.72), "options-sound": (0.12, 0.40, 0.6, 0.7), "options-interface": (0.62, 0.44, 0.88, 0.72)}
DEFAULT = (0.12, 0.10, 0.55, 0.40)

def load(kind, screen, res):
    p = os.path.join(root, kind, f"{screen}-{res}.png")
    return Image.open(p).convert("RGB") if os.path.exists(p) else None

def crop(im, f):
    w, h = im.size
    return im.crop((int(f[0] * w), int(f[1] * h), int(f[2] * w), int(f[3] * h)))

def label(im, text):
    d = ImageDraw.Draw(im); d.rectangle((0, 0, 8 * len(text) + 8, 16), fill=(0, 0, 0)); d.text((4, 2), text, fill=(255, 255, 255)); return im

screens = sys.argv[2:]
if not screens:
    names = {f.rsplit("-", 1)[0] for f in os.listdir(os.path.join(root, "after")) if f.endswith(".png")}
    screens = sorted(names)
os.makedirs(os.path.join(root, "sheets"), exist_ok=True); os.makedirs(os.path.join(root, "zoom"), exist_ok=True)
for s in screens:
    rows = []
    for res in RES:
        b, a = load("before", s, res), load("after", s, res)
        if a is None: continue
        th = 300
        def thumb(im): return im.resize((int(im.width * th / im.height), th), Image.LANCZOS)
        pair = [label(thumb(b), f"before {res}") if b else Image.new("RGB", (10, th)), label(thumb(a), f"after {res}")]
        row = Image.new("RGB", (sum(p.width for p in pair) + 10, th), (40, 40, 40)); x = 0
        for p in pair: row.paste(p, (x, 0)); x += p.width + 10
        rows.append(row)
        for kind, region in (("text", TEXT.get(s, DEFAULT)), ("edge", SLIDER.get(s))):
            if region is None: continue
            ca = crop(a, region); cb = crop(b, region) if b else None
            z = lambda im: im.resize((im.width * 2, im.height * 2), Image.NEAREST)
            za = z(ca); zb = z(cb) if cb else None
            w = za.width * (2 if zb else 1) + (10 if zb else 0)
            out = Image.new("RGB", (w, za.height), (40, 40, 40)); x = 0
            if zb: out.paste(label(zb, f"before {res} 2x"), (0, 0)); x = zb.width + 10
            out.paste(label(za, f"after {res} 2x"), (x, 0))
            out.save(os.path.join(root, "zoom", f"{s}-{kind}-{res}.png"))
    if rows:
        sheet = Image.new("RGB", (max(r.width for r in rows), sum(r.height + 6 for r in rows)), (20, 20, 20)); y = 0
        for r in rows: sheet.paste(r, (0, y)); y += r.height + 6
        sheet.save(os.path.join(root, "sheets", f"{s}.png"))
print("sheets done")
