using System;
using System.Collections.Generic;
using Godot;
using SmClient.Extras.Musou;
using SmContent;
using SmContent.Extras.Musou;

namespace SmClient;

/// <summary>
/// musou/gpu-particles (Musou Mode, added content; the owner's choice of 2026-11-09, the first step to many more effects in a Musou fight): the effect player's
/// GPU spray path. A one-shot spray's ring sprites and streamers (sparks, embers, debris, blood drops and their streaks, flashes thrown by a spray: most of what a
/// fight draws) are written once, when the spray first shows, as spawn records (<see cref="FxGpuSprays"/>), with every value <see cref="DrawSpray"/> computes at
/// the particle's spawn: its direction in the spray's frame, offset, speed and seed. Each frame the CPU writes only the spray's frame (where its parent puts it)
/// and the time; the shader does the rest. So a spray costs the CPU the same whether it throws 1 particle or 64, and the CPU batch's 3,072-quad limit no longer
/// applies to them.
/// <para>
/// The GPU takes a spray that does not loop, in an effect that does not repeat and has no soft-headed streamers, and that is not a dropper of an effect that runs
/// collisions (a spray inside another spray's particle included: the CPU walks the parent particles and writes the inner spray's frame); and of it the ring and
/// streamer leaves that <see cref="DrawNode"/> would draw for that effect, directly under the particle or under one combo there (the combo's curves, offset and
/// turn in the shader). Everything else (beams, lights, decals, meshes, deeper combos, looping sprays) stays on the CPU path, which leaves out the GPU's leaves
/// (<c>_gpuLeafDepth</c>). The look is meant to be the CPU path's: same curves, sizes, colours, cells and atlas rects; the alpha-blended ones are not sorted back
/// to front (<see cref="FxGpuSprays"/>).
/// </para>
/// On only in Musou Mode (<see cref="MusouGpuEnable"/>, called by the Musou view); <c>--fx-cpu</c> keeps the CPU path there (before / after);
/// <c>--fx-gpu</c> (test aid) turns it on in any effect player (the effects test view <c>--fx</c>).
/// </summary>
public sealed partial class FxPlayer
{
    /// <summary>The live-effect cap with the GPU spray path (**ours**: <c>--fx-cap N</c> overrides it; 160 on the CPU path).</summary>
    public const int GpuMaxEffects = FxGpuLayout.GpuMaxEffects;
    /// <summary>The GPU path's live-effect cap (read once: <c>--fx-cap N</c> or 600, 1,200 until the owner's 2026-11-11 "Drop to 600 blood spray cap").</summary>
    static readonly int GpuCap = FxGpuLayout.EffectCap(gpu: true, FxCapArg());

    /// <summary>Segments of a streamer ribbon on the GPU (<see cref="DrawStreamer"/>'s N without a soft head).</summary>
    const int Segments = 8;

    FxGpuSprays? _gpu;
    bool _gpuArgsRead, _gpuCpuNoted;
    int _gpuFrame;
    long _gpuStarted, _gpuRecords, _gpuFallbacks, _gpuNoSlot, _gpuNoAdd, _gpuNoAlpha, _gpuDropped;

    sealed class GpuSpray
    {
        public CSpray? Node;
        public uint Seed;
        public int Slot = -1, Generation, AddStart = -1, AddCount, AlphaStart = -1, AlphaCount, LiveIndex = -1, Visited = -1;
        public double End;
        public bool Cpu, Done, AllGpu;
    }

    /// <summary>What the GPU takes of a spray for effects with the same filters at the same depth (built once: the children, their types, whether the CPU has anything left).</summary>
    sealed class GpuPlan
    {
        public GpuChild[] Children = [];
        public bool All, Cpu;
    }

    /// <summary>
    /// What the GPU draws of one child of a spray: a ring or streamer leaf (<see cref="Leaf"/>: its type, or -2 when it is not drawn), or a combo's leaves (<see cref="ComboLeaves"/>,
    /// one of them picked per particle for a one-at-random combo); <see cref="Combo"/> is null for a leaf.
    /// </summary>
    sealed class GpuChild
    {
        public int K, Leaf = -2;
        public CCombo? Combo;
        public int[] ComboLeaves = [];
    }

    readonly Dictionary<FxEffect, List<GpuSpray>> _gpuSprays = new(ReferenceEqualityComparer.Instance);
    readonly Stack<List<GpuSpray>> _gpuListPool = new();
    readonly Dictionary<(CSpray, int, int), GpuPlan> _gpuPlans = new();
    readonly float[] _gpuRecord = new float[FxGpuLayout.RecordTexels * 4];
    readonly System.Diagnostics.Stopwatch _gpuStartWatch = new();
    double _gpuStartFrameMs, _gpuStartSumMs, _gpuStartMaxMs;
    int _gpuStartFrames;
    // polish pass P3 (measurement): where a frame's GPU spray work goes: finding each spray, writing its frame, the uploads; the worst frame of each between two stats lines.
    long _gpuFindTicks, _gpuSlotTicks;
    int _gpuCalls, _gpuCallsMax, _gpuListMax;
    double _gpuFindMaxMs, _gpuSlotMaxMs, _gpuEndMaxMs;
    readonly Dictionary<(CSpray, CCombo?, CNode), int> _gpuTypes = new();
    readonly HashSet<int> _gpuStreamerTypes = new(), _gpuAdditiveTypes = new();
    readonly List<GpuSpray> _gpuLive = new();
    readonly List<int> _gpuParticleTypes = new();
    readonly float[] _gpuFrameValues = new float[FxGpuLayout.SlotTexels * 4];

    /// <summary>Whether the GPU spray path is on.</summary>
    public bool GpuSprays => _gpu is not null;

    /// <summary>Turns the GPU spray path on (Musou Mode; once the player is ready). <c>--fx-cpu</c> keeps the CPU path.</summary>
    public void MusouGpuEnable()
    {
        if (_gpu is not null || _gpuCpuNoted || !IsReady || _batch is null) return;
        bool fxCpu = Array.IndexOf(OS.GetCmdlineUserArgs(), "--fx-cpu") >= 0;
        if (!FxGpuLayout.UseGpu(fxCpu, SafeMode.Active))
        {
            GD.Print($"effects: GPU sprays off ({(fxCpu ? "--fx-cpu" : "safe mode")}), the CPU path draws every particle (cap {FxGpuLayout.EffectCap(gpu: false, FxCapArg())})");
            _gpuCpuNoted = true;
            return;
        }
        var gpu = new FxGpuSprays { Name = "GpuSprays" };
        AddChild(gpu);
        if (!gpu.Initialize(_batch))
        {
            // A texture array the renderer could not make: the CPU path keeps every particle (and its cap) rather than drawing nothing.
            GD.PushWarning("effects: GPU sprays could not start (texture arrays not created); the CPU path draws every particle");
            gpu.QueueFree();
            _gpuCpuNoted = true;
            return;
        }
        _gpu = gpu;
        GD.Print($"effects: GPU sprays on (pools of {FxGpuSprays.PoolCapacity} records, {FxGpuSprays.SlotCapacity} sprays; live-effect cap {GpuCap})");
    }

    partial void ExtrasFxCap(ref int cap)
    {
        if (_gpu is not null) cap = GpuCap;
    }

    partial void ExtrasFxBegin(double dt)
    {
        if (!_gpuArgsRead)
        {
            _gpuArgsRead = true;
            if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--fx-gpu") >= 0) MusouGpuEnable();   // test aid
        }
        if (_gpu is null) return;
        _gpu.Clock += dt;
        _gpuFrame++;
    }

    partial void ExtrasFxEnd()
    {
        if (_gpu is null) return;
        // A spray not drawn this frame (its effect hidden, too far, its parent particle gone) shows nothing, as on the CPU path.
        foreach (var s in _gpuLive)
            if (s.Visited != _gpuFrame) GpuHide(s);
        long endFrom = global::System.Diagnostics.Stopwatch.GetTimestamp();
        _gpu.EndFrame();
        double toMs = 1000.0 / global::System.Diagnostics.Stopwatch.Frequency;
        _gpuEndMaxMs = Math.Max(_gpuEndMaxMs, (global::System.Diagnostics.Stopwatch.GetTimestamp() - endFrom) * toMs);
        _gpuFindMaxMs = Math.Max(_gpuFindMaxMs, _gpuFindTicks * toMs); _gpuSlotMaxMs = Math.Max(_gpuSlotMaxMs, _gpuSlotTicks * toMs); _gpuCallsMax = Math.Max(_gpuCallsMax, _gpuCalls);
        _gpuFindTicks = 0; _gpuSlotTicks = 0; _gpuCalls = 0;
        _gpuStartSumMs += _gpuStartFrameMs; _gpuStartMaxMs = Math.Max(_gpuStartMaxMs, _gpuStartFrameMs); _gpuStartFrames++;
        _gpuStartFrameMs = 0;
    }

    partial void ExtrasFxRelease(FxEffect e)
    {
        if (_gpu is null || !_gpuSprays.Remove(e, out var list)) return;
        foreach (var s in list) GpuFree(s);
        list.Clear();
        _gpuListPool.Push(list);
    }

    partial void ExtrasFxStats(ref string text)
    {
        if (_gpu is null) return;
        text = $"gpu sprays {_gpuLive.Count} live ({_gpuStarted} started, {_gpuFallbacks} left to the CPU by a full table, {_gpuDropped} dropped by a full pool: {_gpuNoSlot} frames, {_gpuNoAdd} additive, {_gpuNoAlpha} alpha), records {_gpu.Additive.Blocks.Used} additive + {_gpu.Alpha.Blocks.Used} alpha " +
               $"(drawn up to {_gpu.Additive.Blocks.HighWater} + {_gpu.Alpha.Blocks.HighWater}; {_gpuRecords} written), uploads {(_gpu.Additive.UploadedBytes + _gpu.Alpha.UploadedBytes) / 1024} KiB records, " +
               $"{_gpu.SlotUploadBytes / 1024} KiB frames, {_gpu.TableUploadBytes / 1024} KiB tables, {_gpu.TypeCount} types; spray starts {(_gpuStartFrames > 0 ? _gpuStartSumMs / _gpuStartFrames : 0):F2} ms a frame avg, {_gpuStartMaxMs:F2} max; " +
               $"worst frame: {_gpuCallsMax} sprays walked (longest list {_gpuListMax}), finding them {_gpuFindMaxMs:F2} ms, their frames {_gpuSlotMaxMs:F2} ms, uploads {_gpuEndMaxMs:F2} ms";
        _gpuStartSumMs = 0; _gpuStartMaxMs = 0; _gpuStartFrames = 0;
        _gpuCallsMax = 0; _gpuListMax = 0; _gpuFindMaxMs = 0; _gpuSlotMaxMs = 0; _gpuEndMaxMs = 0;
    }

    partial void ExtrasSpray(FxEffect e, CSpray n, in Ctx c, double age, int depth, uint seed, ref int gpu)
    {
        // A soft-headed effect or one widening along its trail (plasma-original-fx) draws its streamers its own way: the CPU path keeps it.
        if (_gpu is null || n.Looping || e.Repeat || e.SoftHead || e.WidthAlongTrail || e.ReachCap > 0f || (e.Collisions && n.Collides)) return;   // hit-sparks-flashes: a reach-capped effect (a hit on a player) keeps the CPU path, which applies the cap
        if (!_gpuSprays.TryGetValue(e, out var list)) _gpuSprays[e] = list = _gpuListPool.Count > 0 ? _gpuListPool.Pop() : new List<GpuSpray>();
        GpuSpray? s = null;
        long findFrom = global::System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var known in list) if (known.Seed == seed && ReferenceEquals(known.Node, n)) { s = known; break; }
        _gpuFindTicks += global::System.Diagnostics.Stopwatch.GetTimestamp() - findFrom; _gpuCalls++;
        if (list.Count > _gpuListMax) _gpuListMax = list.Count;
        if (s is null)
        {
            _gpuStartWatch.Restart();
            s = GpuStart(e, n, age, depth, seed);
            _gpuStartFrameMs += _gpuStartWatch.Elapsed.TotalMilliseconds;
            s.Node = n; s.Seed = seed;
            list.Add(s);
        }
        if (s.Cpu) return;
        gpu = s.AllGpu ? 2 : 1;
        if (s.Done) return;
        if (age > s.End) { GpuFree(s); s.Done = true; return; }
        long slotFrom = global::System.Diagnostics.Stopwatch.GetTimestamp();
        // The spray's frame this frame: what its parents (combos, a parent particle, an attached effect) do to it, and the effect's opacity, smoke rein, tint and ground-soft flag.
        var v = _gpuFrameValues;
        var b = c.B;
        v[0] = c.Pos.X; v[1] = c.Pos.Y; v[2] = c.Pos.Z; v[3] = c.S;
        v[4] = b.X.X; v[5] = b.X.Y; v[6] = b.X.Z; v[7] = s.Generation;
        v[8] = b.Y.X; v[9] = b.Y.Y; v[10] = b.Y.Z; v[11] = e.Opacity;
        v[12] = b.Z.X; v[13] = b.Z.Y; v[14] = b.Z.Z; v[15] = e.SmokeRein;
        var tint = e.Tint ?? Colors.Black;
        v[16] = tint.R; v[17] = tint.G; v[18] = tint.B;
        v[19] = (e.Tint is not null ? 1 : 0) + (e.GammaColours ? 2 : 0) + (e.SoftGround && FxBatch.Fades && !FxBatch.NoGroundSoft ? 4 : 0);
        _gpu.WriteSlot(s.Slot, v);
        s.Visited = _gpuFrame;
        _gpuSlotTicks += global::System.Diagnostics.Stopwatch.GetTimestamp() - slotFrom;
    }

    /// <summary>Whether <see cref="DrawNode"/> would draw <paramref name="node"/> at <paramref name="depth"/> for this effect (the filters that do not change while it plays).</summary>
    bool GpuShown(FxEffect e, CNode node, int depth)
    {
        if (e.FlashOnly && node.Name is not ("quick flash" or "JET" or "")) return false;
        if (node.Hide || depth > MaxDepth) return false;
        if (!ShowSmoke && !e.KeepSmoke && (node.SmokeOnly || node.Smoke)) return false;
        if (DecalsOnly && node is not (CCombo or CSpray or CDecal)) return false;
        return node is not CSprite { Distortion: true };
    }

    int GpuRecordsOf(int type) => _gpuStreamerTypes.Contains(type) ? Segments : 1;

    /// <summary>The types a particle with seed <paramref name="ps"/> draws, in order (a one-at-random combo picks by the particle's combo seed, as <see cref="DrawCombo"/> does).</summary>
    static void GpuParticleTypes(GpuPlan plan, uint ps, List<int> into)
    {
        into.Clear();
        foreach (var ch in plan.Children)
        {
            if (ch.Combo is null) { if (ch.Leaf >= 0) into.Add(ch.Leaf); continue; }
            if (ch.Combo.OneAtRandom)
            {
                if (ch.Combo.Children.Length == 0) continue;   // nothing to pick (as DrawCombo)
                int pick = Math.Min(ch.Combo.Children.Length - 1, (int)(R(Mix(ps, ch.K + 5), 4) * ch.Combo.Children.Length));
                if (ch.ComboLeaves[pick] >= 0) into.Add(ch.ComboLeaves[pick]);
                continue;
            }
            foreach (int t in ch.ComboLeaves) if (t >= 0) into.Add(t);
        }
    }

    /// <summary>DrawSpray's particle seed (cycle 0; a copy of Musou's blood on its own seed).</summary>
    static uint GpuSeed(uint seed, int i, int copy)
    {
        uint ps = Mix(Mix(seed, i), 31);
        return copy > 0 ? Mix(ps, 1000 + copy) : ps;
    }

    /// <summary>The spray's plan for this effect's filters at this depth (built once per spray, depth and filter set).</summary>
    GpuPlan GpuPlanFor(FxEffect e, CSpray n, int depth)
    {
        int mask = (e.FlashOnly ? 1 : 0) | (e.KeepSmoke ? 2 : 0) | (ShowSmoke ? 4 : 0) | (DecalsOnly ? 8 : 0);
        if (_gpuPlans.TryGetValue((n, depth, mask), out var known)) return known;
        var plan = new GpuPlan();
        var children = new List<GpuChild>();
        bool all = true;
        for (int k = 0; k < n.Children.Length && !plan.Cpu; k++)
        {
            var child = n.Children[k];
            if (GpuLeaf(child))
            {
                int t = GpuShown(e, child, depth + 1) ? GpuType(n, null, child, k, 0) : -2;   // -2: not drawn on the CPU path either
                if (t == -1) plan.Cpu = true;
                children.Add(new GpuChild { K = k, Leaf = t });
                continue;
            }
            if (child is not CCombo combo) { all = false; continue; }
            // A combo under the particle: its ring and streamer children are the GPU's (the CPU walks the combo for any others: sprays, beams, ...).
            var leaves = new int[combo.Children.Length];
            bool shown = GpuShown(e, combo, depth + 1);
            for (int i = 0; i < combo.Children.Length; i++)
            {
                var leaf = combo.Children[i];
                if (!GpuLeaf(leaf)) { leaves[i] = -2; all = false; continue; }
                leaves[i] = shown && GpuShown(e, leaf, depth + 2) ? GpuType(n, combo, leaf, k, i) : -2;
                if (leaves[i] == -1) plan.Cpu = true;
            }
            children.Add(new GpuChild { K = k, Combo = combo, ComboLeaves = leaves });
        }
        plan.Children = children.ToArray();
        plan.All = all;
        return _gpuPlans[(n, depth, mask)] = plan;
    }

    /// <summary>A spray's first frame: its records, written once (what <see cref="DrawSpray"/> computes at each particle's spawn), or the CPU when a table or pool is full.</summary>
    GpuSpray GpuStart(FxEffect e, CSpray n, double age, int depth, uint seed)
    {
        var s = new GpuSpray();
        var g = _gpu!;
        var plan = GpuPlanFor(e, n, depth);
        if (plan.Cpu) { s.Cpu = true; _gpuFallbacks++; return s; }
        s.AllGpu = plan.All;
        bool more = e.Amount > 1f && HoldsAlpha(n);
        bool thin = e.DustShare < 1f && HoldsAlpha(n);   // musou/autocannon-dust: as DrawSprayParticles, cycle 0
        // First pass: how many records of each blend.
        for (int i = 0; i < n.Spawns.Length; i++)
        for (int copy = 0, copies = more ? FxAmount.Copies(e.Amount, R(Mix(seed, i), 77)) : 1; copy < copies; copy++)
        {
            GpuParticleTypes(plan, GpuSeed(seed, i, copy), _gpuParticleTypes);
            bool dropDust = thin && !FxAmount.Kept(e.DustShare, R(Mix(seed, i), 91));   // musou/autocannon-dust: only the alpha (dust) types go, the additive flash and fire stay
            foreach (int t in _gpuParticleTypes) { if (dropDust && !_gpuAdditiveTypes.Contains(t)) continue; if (_gpuAdditiveTypes.Contains(t)) s.AddCount += GpuRecordsOf(t); else s.AlphaCount += GpuRecordsOf(t); }
        }
        if (s.AddCount + s.AlphaCount == 0) { s.Done = true; return s; }
        s.Slot = g.NewSlot(out s.Generation);
        if (s.AddCount > 0) s.AddStart = g.Additive.Blocks.Allocate(s.AddCount);
        if (s.AlphaCount > 0) s.AlphaStart = g.Alpha.Blocks.Allocate(s.AlphaCount);
        if (s.Slot < 0 || (s.AddCount > 0 && s.AddStart < 0) || (s.AlphaCount > 0 && s.AlphaStart < 0))
        {
            // A full pool drops the spray's GPU particles (the CPU batch drops its quads when full too): handing them to the CPU in a burst cost more than the CPU path itself.
            if (s.Slot < 0) _gpuNoSlot++; else if (s.AddCount > 0 && s.AddStart < 0) _gpuNoAdd++; else _gpuNoAlpha++;
            GpuFree(s);
            s.Done = true; _gpuDropped++;
            return s;
        }
        float duration = MathF.Max(n.Duration, 0.001f);
        float life = MathF.Max(n.ChildLife, 0.02f);
        s.End = n.Spawns[^1] + life;
        double start = g.Clock - age;
        int ai = s.AddStart, li = s.AlphaStart;
        for (int i = 0; i < n.Spawns.Length; i++)
        for (int copy = 0, copies = more ? FxAmount.Copies(e.Amount, R(Mix(seed, i), 77)) : 1; copy < copies; copy++)
        {
            // DrawSpray's spawn-time values (cycle 0: the spray does not loop).
            uint ps = GpuSeed(seed, i, copy);
            float te = Math.Clamp(n.Spawns[i] / duration, 0f, 1f);
            float devMax = Math.Clamp(At(n.Deviation, te, 0f), 0f, MathF.PI), devMin = Math.Clamp(At(n.Inner, te, 0f), 0f, devMax);
            float cosT = Mathf.Lerp(MathF.Cos(devMin), MathF.Cos(devMax), R(ps, 1));
            float sinT = MathF.Sqrt(MathF.Max(0f, 1f - cosT * cosT)), phi = R(ps, 2) * MathF.Tau;
            var local = new Vector3(sinT * MathF.Cos(phi), cosT, sinT * MathF.Sin(phi));
            float speed = At(n.Speed, te, 0f);
            var off = new Vector3(At(n.OffX, te, 0f), At(n.OffY, te, 0f), At(n.OffZ, te, 0f));
            if (!n.FromPoint) off += new Vector3(R(ps, 3) - 0.5f, R(ps, 4) - 0.5f, R(ps, 5) - 0.5f) * new Vector3(At(n.VolX, te, 0f), At(n.VolY, te, 0f), At(n.VolZ, te, 0f));
            float spawnAt = (float)(start + n.Spawns[i]);
            var (lo, hi) = FxGpuLayout.SplitSeed(ps);
            GpuParticleTypes(plan, ps, _gpuParticleTypes);
            bool dropDust = thin && !FxAmount.Kept(e.DustShare, R(Mix(seed, i), 91));
            foreach (int t in _gpuParticleTypes)
            {
                bool additive = _gpuAdditiveTypes.Contains(t);
                if (dropDust && !additive) continue;
                var pool = additive ? g.Additive : g.Alpha;
                var r = _gpuRecord;
                r[0] = s.Slot; r[1] = t; r[2] = spawnAt; r[3] = s.Generation;
                r[4] = off.X; r[5] = off.Y; r[6] = off.Z; r[7] = 0f;
                r[8] = local.X; r[9] = local.Y; r[10] = local.Z; r[11] = speed;
                r[12] = lo; r[13] = hi; r[15] = 0f;
                for (int j = 0, segments = GpuRecordsOf(t); j < segments; j++)
                {
                    r[14] = j;
                    pool.WriteRecord(additive ? ai++ : li++, r);
                    _gpuRecords++;
                }
            }
        }
        s.LiveIndex = _gpuLive.Count;
        _gpuLive.Add(s);
        _gpuStarted++;
        return s;
    }

    static void Set(Span<float> t, float a, float b, float c, float d) { t[0] = a; t[1] = b; t[2] = c; t[3] = d; }

    /// <summary>
    /// The type-table entry of a spray, an optional combo child of it (<paramref name="k"/>: the child's place in the spray; <paramref name="i"/>: the leaf's place in the combo) and a ring or
    /// streamer leaf (written once): -2 when the atlas has no such sprite, -1 when a table is full.
    /// </summary>
    int GpuType(CSpray n, CCombo? combo, CNode leafNode, int k, int i)
    {
        if (_gpuTypes.TryGetValue((n, combo, leafNode), out int known)) return known;
        var g = _gpu!;
        var leaf = (CSprite)leafNode;
        if (!_batch!.BaseRect(leaf.Material, out var rect, out var sheet))
        {
            if (!leaf.Material.IsEmpty) _missing.Add("sprite:" + leaf.Material.Name);
            return _gpuTypes[(n, combo, leafNode)] = -2;
        }
        if (g.TypeCount >= FxGpuSprays.MaxTypes) return -1;
        (int Start, int Count) none = (0, 0);
        var ring = leaf as CRing;
        var streamer = leaf as CStreamer;
        // Curve slots: particle scale, radius | length, offset X | width, offset Y | master colour, offset Z, spin X, spin Y, spin Z, animation, colour; the combo's scale and offsets.
        var curves = new[]
        {
            g.Curve(n.ParticleScale), ring is not null ? g.Curve(ring.Radius) : g.Curve(streamer!.Length),
            ring is not null ? g.Curve(ring.OffX) : g.Curve(streamer!.Width), ring is not null ? g.Curve(ring.OffY) : g.Colours(streamer!.MasterColour),
            ring is not null ? g.Curve(ring.OffZ) : none, ring is not null ? g.Curve(ring.SpinX) : none,
            ring is not null ? g.Curve(ring.SpinY) : none, ring is not null ? g.Curve(ring.SpinZ) : none,
            ring is not null ? g.Curve(ring.Anim) : none, g.Colours(leaf.Colour),
            combo is not null ? g.Curve(combo.Scale) : none, combo is not null ? g.Curve(combo.OffX) : none,
            combo is not null ? g.Curve(combo.OffY) : none, combo is not null ? g.Curve(combo.OffZ) : none,
        };
        foreach (var r in curves) if (r.Count < 0) return -1;
        int t = g.TypeCount++;
        float D = MathF.PI / 180f;
        Set(g.TypeTexel(t, 0), n.Drag, n.Gravity, n.ScaleSpeed ? 1f : 0f, MathF.Max(n.ChildLife, 0.02f));
        Set(g.TypeTexel(t, 1), leaf.Duration, leaf.Epsilon, leaf.Looping ? 1f : 0f, leaf.Billboard ? 1f : 0f);
        if (ring is not null)
        {
            Set(g.TypeTexel(t, 2), ring.EpsRadius, ring.RandomSpinDirection ? 1f : 0f, 4f * (int)(ring.Billboard ? FxBatch.QuadKind.CameraBillboard : FxBatch.QuadKind.FlatRing), IsSmoke(ring) ? 1f : 0f);
            Set(g.TypeTexel(t, 3), ring.StartAngle.X * D, ring.StartAngle.Y * D, ring.StartAngle.Z * D, (ring.UseStart.X ? 1 : 0) + (ring.UseStart.Y ? 2 : 0) + (ring.UseStart.Z ? 4 : 0));
        }
        else
        {
            // A streamer: length as seconds, metres per U, the quad code (wrap, axis billboard), smoke.
            Set(g.TypeTexel(t, 2), streamer!.LengthAsSeconds ? 1f : 0f, streamer.MeterPerU, 1f + 4f * (int)FxBatch.QuadKind.AxisBillboard, IsSmoke(streamer) ? 1f : 0f);
            Set(g.TypeTexel(t, 3), 0f, 0f, 0f, 0f);
            _gpuStreamerTypes.Add(t);
        }
        if (leaf.Additive) _gpuAdditiveTypes.Add(t);
        Set(g.TypeTexel(t, 4), rect.X, rect.Y, rect.Z, rect.W);
        Set(g.TypeTexel(t, 5), sheet.X, sheet.Y, sheet.Z, sheet.W);
        Set(g.TypeTexel(t, 6), k, HoldsSmoke(n) ? 1f : 0f, streamer is not null ? 1 : leaf.Cells, streamer is not null ? 1f : 0f);
        for (int c = 0; c < 10; c += 2)
            Set(g.TypeTexel(t, 7 + c / 2), curves[c].Start, curves[c].Count, curves[c + 1].Start, curves[c + 1].Count);
        if (combo is not null)
        {
            Set(g.TypeTexel(t, 12), 1f, i, combo.Duration, combo.Looping ? 1f : 0f);
            Set(g.TypeTexel(t, 13), combo.StartAngle.X * D, combo.StartAngle.Y * D, combo.StartAngle.Z * D, (combo.UseStart.X ? 1 : 0) + (combo.UseStart.Y ? 2 : 0) + (combo.UseStart.Z ? 4 : 0));
        }
        else
        {
            Set(g.TypeTexel(t, 12), 0f, 0f, 0f, 0f);
            Set(g.TypeTexel(t, 13), 0f, 0f, 0f, 0f);
        }
        Set(g.TypeTexel(t, 14), curves[10].Start, curves[10].Count, curves[11].Start, curves[11].Count);
        Set(g.TypeTexel(t, 15), curves[12].Start, curves[12].Count, curves[13].Start, curves[13].Count);
        return _gpuTypes[(n, combo, leafNode)] = t;
    }

    void GpuHide(GpuSpray s)
    {
        var v = _gpuFrameValues;
        v.AsSpan().Clear();
        v[7] = -1f;
        _gpu!.WriteSlot(s.Slot, v);
    }

    void GpuFree(GpuSpray s)
    {
        var g = _gpu!;
        if (s.AddStart >= 0) { g.Additive.Blocks.Free(s.AddStart, s.AddCount); s.AddStart = -1; }
        if (s.AlphaStart >= 0) { g.Alpha.Blocks.Free(s.AlphaStart, s.AlphaCount); s.AlphaStart = -1; }
        if (s.Slot >= 0) { g.FreeSlot(s.Slot); s.Slot = -1; }
        if (s.LiveIndex >= 0)
        {
            int last = _gpuLive.Count - 1;
            var moved = _gpuLive[last];
            _gpuLive[s.LiveIndex] = moved; moved.LiveIndex = s.LiveIndex;
            _gpuLive.RemoveAt(last);
            s.LiveIndex = -1;
        }
    }
}
