"""ui/text-scale evidence: the per-level text sizes of every audited screen, before and after.
usage: python text_scale_table.py <media folder with before/ and after/>
Reads the <screen>-<WxH>.txt files that `--text-audit` writes (kind|font size|window px|FRAC or int|face|text|path) and prints, per text level and resolution, the distinct font sizes and window pixel sizes seen on all screens."""
import os, sys, collections

root = sys.argv[1]
RES = ["1280x720", "1920x1080", "2560x1440", "2560x1080", "1280x1024"]
LEVELS = ["title", "plate/tab/button", "row label", "row value", "note/heading/other"]

def level(kind, path, text):
    name = path.rsplit("/", 1)[-1]
    if name in ("PanelTitle", "OptionsPlate"): return "title"
    if kind == "LineEdit" or name.endswith("Value") or name == "Count": return "row value"
    if kind == "Button":
        return "row label" if ("/Choice" in path or "Row/" in path) else "plate/tab/button"
    if kind == "Label" and "/Slider" in path: return "row label"
    return "note/heading/other"

def read(kind, res):
    out = collections.defaultdict(lambda: collections.defaultdict(set)); frac = 0; n = 0
    d = os.path.join(root, kind)
    for f in sorted(os.listdir(d)):
        if not f.endswith(f"-{res}.txt"): continue
        for line in open(os.path.join(d, f), encoding="utf-8"):
            p = line.rstrip("\n").split("|", 6)
            if p[0] == "scale" or len(p) < 7: continue
            lv = level(p[0], p[6], p[5]); out[lv]["size"].add(int(p[1])); out[lv]["px"].add(float(p[2])); n += 1
            if p[3] == "FRAC": frac += 1
    return out, frac, n

print("| level | resolution | before: font sizes (window px) | after: font sizes (window px) |\n|---|---|---|---|")
tot = {}
for res in RES:
    b, bf, bn = read("before", res); a, af, an = read("after", res); tot[res] = (bf, bn, af, an)
    for lv in LEVELS:
        f = lambda o: "-" if lv not in o else ", ".join(str(s) for s in sorted(o[lv]["size"])) + " (" + ", ".join(f"{p:g}" for p in sorted(o[lv]["px"])) + ")"
        print(f"| {lv} | {res} | {f(b)} | {f(a)} |")
print("\n| resolution | texts audited before | fractional window px before | texts audited after | fractional window px after |\n|---|---|---|---|---|")
for res, (bf, bn, af, an) in tot.items(): print(f"| {res} | {bn} | {bf} | {an} | {af} |")
