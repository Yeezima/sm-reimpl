"""classic-maps-perf-table.py <tag> [<tag2> ...]: turns the logs of tools/classic-maps-perf.ps1 into Markdown tables (test aid, never shipped).
Reads %LOCALAPPDATA%/sm-reimpl/perf/classic-maps-perf/<tag>/<map>-<mode>/{client.log,server.log,summary.txt,places.txt}.
One row per map: the mean over the places of the client's per-window mean frame time, the worst window p99 and worst frame, draw calls, objects,
lights (with shadows), mesh instances, VRAM; then the per-place table of one map with --places <map>.
"""
import os, re, sys, glob, statistics

base = os.path.join(os.environ['LOCALAPPDATA'], 'sm-reimpl', 'perf', 'classic-maps-perf')
LINE = re.compile(r'^perf: (\d+) s place (\d+): .*?: (\d+) frames, mean ([\d.]+) ms \((\d+) fps\), p99 ([\d.]+), worst ([\d.]+); render cpu ([\d.]+) ms gpu ([\d.]+) ms; draw calls (\d+), objects (\d+), primitives (\d+)k;.*?vram (\d+) MB.*?lights (\d+) \((\d+) with shadows\), decals (\d+), particle nodes (\d+), mesh instances (\d+);.*?working set (\d+) MB')

def windows(path, limit=None):
    out = []
    for l in open(path, encoding='utf8', errors='replace'):
        m = LINE.match(l)
        if m:
            g = m.groups()
            if limit is not None and int(g[1]) > limit: continue   # the tour's last place repeats until the client quits
            out.append(dict(t=int(g[0]), place=int(g[1]), frames=int(g[2]), mean=float(g[3]), fps=int(g[4]), p99=float(g[5]), worst=float(g[6]),
                            cpu=float(g[7]), gpu=float(g[8]), draws=int(g[9]), objs=int(g[10]), prims=int(g[11]), vram=int(g[12]), lights=int(g[13]),
                            shadows=int(g[14]), decals=int(g[15]), particles=int(g[16]), meshes=int(g[17]), ws=int(g[18])))
    return out

def server(path):
    s = open(path, encoding='utf8', errors='replace').read() if os.path.exists(path) else ''
    return [l for l in s.splitlines() if 'TICKSTATS' in l]

def places(d):
    p = os.path.join(d, 'places.txt')
    return open(p).read().split('\n') if os.path.exists(p) else []

args = sys.argv[1:]
show = None
if '--places' in args:
    i = args.index('--places'); show = args[i + 1]; del args[i:i + 2]
for tag in args:
    print(f'\n### tag {tag}\n')
    print('| map | places | mean ms | fps | worst place mean ms | worst p99 | worst frame | render cpu / gpu ms | draws (mean/max) | objects max | lights (shadowed) | mesh inst. | vram MB | client WS MB |')
    print('|---|---|---|---|---|---|---|---|---|---|---|---|---|---|')
    for d in sorted(glob.glob(os.path.join(base, tag, '*'))):
        name = os.path.basename(d)
        cl = os.path.join(d, 'client.log')
        if not os.path.exists(cl): continue
        w = [x for x in windows(cl, len(places(d))) if x['place'] >= 2]   # place 1 is the settle window
        if not w: print(f'| {name} | no windows | | | | | | | | | | | | |'); continue
        mean = statistics.mean(x['mean'] for x in w)
        wp = max(w, key=lambda x: x['mean'])
        print(f"| {name} | {len(w)} | {mean:.2f} | {1000 / mean:.0f} | {wp['mean']:.2f} (place {wp['place']}) | {max(x['p99'] for x in w):.1f} | {max(x['worst'] for x in w):.1f} | "
              f"{statistics.mean(x['cpu'] for x in w):.1f} / {statistics.mean(x['gpu'] for x in w):.1f} | {statistics.mean(x['draws'] for x in w):.0f}/{max(x['draws'] for x in w)} | {max(x['objs'] for x in w)} | {w[0]['lights']} ({w[0]['shadows']}) | {w[0]['meshes']} | {max(x['vram'] for x in w)} | {max(x['ws'] for x in w)} |")
    print('\nServer (TICKSTATS, last line):\n')
    for d in sorted(glob.glob(os.path.join(base, tag, '*'))):
        s = server(os.path.join(d, 'server.log'))
        if s: print(f'- {os.path.basename(d)}: {s[-1].strip()}')
    if show:
        d = os.path.join(base, tag, show)
        nm = places(d)
        print(f'\n#### places of {show}\n')
        print('| place | mean ms | p99 | worst | draws | objects | prims k | render cpu | gpu | lights (shadowed) |')
        print('|---|---|---|---|---|---|---|---|---|---|')
        for x in windows(os.path.join(d, 'client.log'), len(nm)):
            n = nm[x['place'] - 1] if 0 < x['place'] <= len(nm) else str(x['place'])
            print(f"| {n} | {x['mean']:.2f} | {x['p99']:.1f} | {x['worst']:.1f} | {x['draws']} | {x['objs']} | {x['prims']} | {x['cpu']:.2f} | {x['gpu']:.2f} | {x['lights']} ({x['shadows']}) |")
