using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;

namespace SmClient;

/// <summary>
/// Test aid (hotfix/ashfall-floor-shimmer, 2026-11-08): <c>--ashfall-dress --ashfall-shimmer view1,view2,..</c> measures how much Ashfall's floor shimmers. For each named view (the gallery's shot
/// names, e.g. street_spawn) the camera turns by a sub-pixel yaw step per frame (<see cref="YawStepPixels"/> of a pixel), every animated layer frozen (the map's nodes not processed, the particles and the
/// around-camera fields hidden), and consecutive frames are compared over the floor region (the bottom <see cref="FloorRows"/> of the frame): a well filtered floor changes by about its
/// gradient times the step, an aliased one (sparkle, z-fighting, unfiltered noise) flips whole pixels. SHIMMER lines: mean |dDetail| (0..255; detail = luma minus its 9 x 9 mean, so global and slow brightness changes drop out) and the share of pixels changing more than
/// <see cref="FlipThreshold"/>, and the same for two frames with no camera change (the noise floor). Frames 0 and 1 of each view saved to <c>--out</c>. Never shipped behaviour.
/// <c>--shimmer-ssao off</c>, <c>--shimmer-ssil off</c>, <c>--shimmer-vfog off</c> leave those effects out, <c>--shimmer-ground-drop m</c> draws the ground that much lower and <c>--shimmer-move dolly</c> walks forward instead of turning (attribution), <c>none</c> keeps it still; <c>--shimmer-live</c> leaves everything animating (fires, particles), <c>--shimmer-save-all</c> saves every frame, <c>--shimmer-hide a,b</c> hides the nodes of those names; <c>--shimmer-aa taa|fsr</c> the Options' TAA or FSR 2 Quality instead of MSAA 4x + SMAA; <c>--shimmer-ssaa</c> renders at twice the resolution each way (the reference: native minus it is the aliasing part).
/// </summary>
public partial class AshfallKitGallery
{
    const float YawStepPixels = 0.35f;
    const float FloorRows = 0.40f;
    const float DollyStep = 0.04f;
    const int FlipThreshold = 10;
    const int ShimmerFrames = 9;
    /// <summary>The flicker check's raw-luma step (0..255) above which a pixel counts as changed (ashfall-no-flicker).</summary>
    const int RawFlipThreshold = 8;
    /// <summary>A changed pixel counts as part of a flickering patch when at least this many of its 5 x 5 neighbours changed too: a whole block or patch switching, not an edge or a texel creeping.</summary>
    const int BlobNeighbours = 20;
    int FramesWanted => int.TryParse(_args.GetValueOrDefault("shimmer-frames"), out var n) && n > 2 ? Math.Min(n, 240) : ShimmerFrames;
    bool FullRegion => _args.GetValueOrDefault("shimmer-region") == "full";
    byte[]? _shPrevRaw;
    double _shSumRaw, _shSumBlob, _shMaxBlob;
    int[]? _shBlobHeat;

    int _shView, _shFrame, _shWait;
    bool _shStarted, _shViewStarted;
    byte[]? _shPrev;
    int[]? _shHeat;
    double _shSumMotion, _shSumFlip, _shStatic, _shStaticFlip;
    int _shPairs;
    Vector3 _shAt, _shLook;
    readonly List<string> _shLines = [];

    bool ShimmerMode => _args.ContainsKey("ashfall-shimmer");

    /// <summary>Game-view extras for the shimmer runs: the Medium preset's screen-space indirect light as the match draws it (MapLightingView), unless switched off.</summary>
    void ShimmerEnvironment()
    {
        _camera.Near = 0.05f; _camera.Far = 3000f;   // the match camera's planes (NetworkView): the depth precision the game draws with
        if (_args.ContainsKey("shimmer-ssaa"))   // reference: 2 x 2 supersampled (render scale 2, bilinear down): what the same motion does with almost no aliasing; native minus this is the shimmer
        {
            GetViewport().Scaling3DMode = Viewport.Scaling3DModeEnum.Bilinear;
            GetViewport().Scaling3DScale = 2f;
        }
        bool compat = RenderingServer.GetCurrentRenderingMethod() == "gl_compatibility";
        // the match's default anti-aliasing (RenderQuality: MSAA 4x + SMAA; the Compatibility renderer MSAA 4x)
        GetViewport().Msaa3D = Viewport.Msaa.Msaa4X;
        GetViewport().ScreenSpaceAA = compat ? Viewport.ScreenSpaceAAEnum.Disabled : Viewport.ScreenSpaceAAEnum.Smaa;
        void Fsr(float scale) { GetViewport().Msaa3D = Viewport.Msaa.Disabled; GetViewport().ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled; GetViewport().Scaling3DMode = Viewport.Scaling3DModeEnum.Fsr2; GetViewport().Scaling3DScale = scale; }
        switch (_args.GetValueOrDefault("shimmer-aa"))   // the Options' other choices (RenderQuality): TAA, or an FSR 2 preset (which replaces MSAA, SMAA and TAA; the Upscaling scales)
        {
            case "taa": GetViewport().Msaa3D = Viewport.Msaa.Disabled; GetViewport().ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled; GetViewport().UseTaa = true; break;
            case "off": GetViewport().Msaa3D = Viewport.Msaa.Disabled; GetViewport().ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled; break;
            case "fsr" or "fsr-quality": Fsr(0.67f); break;
            case "fsr-native": Fsr(1f); break;
            case "fsr-balanced": Fsr(0.59f); break;
            case "fsr-performance": Fsr(0.5f); break;
        }
        AshfallGrime.UpdateForViewport(GetViewport());   // as the storm does each frame in a match: the grime folded under FSR 2, the pulled passes otherwise
        GD.Print($"SHIMMER aa: msaa {GetViewport().Msaa3D}, ssaa {GetViewport().ScreenSpaceAA}, taa {GetViewport().UseTaa}, scaling {GetViewport().Scaling3DMode} x{GetViewport().Scaling3DScale:0.##}, size {GetViewport().GetVisibleRect().Size}");
        if (compat) return;
        _env.SsaoEnabled = _args.GetValueOrDefault("shimmer-ssao") != "off";
        _env.SsaoRadius = 1.4f; _env.SsaoPower = 1.6f; _env.SsaoDetail = 0.6f; _env.SsaoHorizon = 0.06f; _env.SsaoSharpness = 0.98f; _env.SsaoLightAffect = 0.15f; _env.SsaoAOChannelAffect = 0.3f;
        RenderingServer.EnvironmentSetSsaoQuality(RenderingServer.EnvironmentSsaoQuality.Medium, true, 0.5f, 2, 50f, 300f);
        if (_args.GetValueOrDefault("shimmer-ssil") != "off")
        {
            _env.SsilEnabled = true; _env.SsilRadius = 6f; _env.SsilIntensity = 2f; _env.SsilSharpness = 0.98f; _env.SsilNormalRejection = 1f;
            RenderingServer.EnvironmentSetSsilQuality(RenderingServer.EnvironmentSsilQuality.Medium, true, 0.5f, 4, 50f, 300f);
        }
        if (_args.GetValueOrDefault("shimmer-vfog") == "off") _env.VolumetricFogEnabled = false;
        if (_args.GetValueOrDefault("shimmer-sun-shadow") == "off") foreach (var n in FindChildren("*", "DirectionalLight3D", true, false)) if (n is DirectionalLight3D l) l.ShadowEnabled = false;   // attribution
    }

    static void Freeze(Node n)
    {
        if (n is GpuParticles3D g) g.Visible = false;
        else if (n is CpuParticles3D c) c.Visible = false;
        else if (n is Node3D { Name: var name } d && name == "AroundCamera") d.Visible = false;
        foreach (var child in n.GetChildren()) Freeze(child);
    }

    static byte[] Luma(Image img, out int w, out int h)
    {
        img.Convert(Image.Format.Rgb8);
        w = img.GetWidth(); h = img.GetHeight();
        var data = img.GetData();
        var y = new byte[w * h];
        for (int i = 0; i < y.Length; i++) y[i] = (byte)((data[i * 3] * 54 + data[i * 3 + 1] * 183 + data[i * 3 + 2] * 19) >> 8);
        return y;
    }

    /// <summary>The detail layer: luma minus its 9 x 9 box mean, plus 128 (clamped). Shimmer is in the detail; a global or slowly varying brightness change (cloud shadows, the fog's light) drops out.</summary>
    static byte[] HighPass(byte[] y, int w, int h)
    {
        const int R = 4;
        var sat = new long[(w + 1) * (h + 1)];
        for (int j = 0; j < h; j++)
        {
            long row = 0;
            for (int i = 0; i < w; i++) { row += y[j * w + i]; sat[(j + 1) * (w + 1) + i + 1] = sat[j * (w + 1) + i + 1] + row; }
        }
        var o = new byte[w * h];
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                int x0 = Math.Max(0, i - R), x1 = Math.Min(w, i + R + 1), y0 = Math.Max(0, j - R), y1 = Math.Min(h, j + R + 1);
                long sum = sat[y1 * (w + 1) + x1] - sat[y0 * (w + 1) + x1] - sat[y1 * (w + 1) + x0] + sat[y0 * (w + 1) + x0];
                int mean = (int)(sum / ((x1 - x0) * (y1 - y0)));
                o[j * w + i] = (byte)Math.Clamp(y[j * w + i] - mean + 128, 0, 255);
            }
        return o;
    }

    (double Mean, double Flip) Diff(byte[] a, byte[] b, int w, int h)
    {
        int y0 = FullRegion ? 0 : (int)(h * (1f - FloorRows));
        long sum = 0, flips = 0, n = 0;
        for (int y = y0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, d = Math.Abs(a[i] - b[i]);
                sum += d; if (d > FlipThreshold) flips++; n++;
            }
        return (sum / (double)Math.Max(1, n), flips * 100.0 / Math.Max(1, n));
    }

    /// <summary>
    /// The flicker check (ashfall-no-flicker): on raw luma, the share of pixels changing by more than <see cref="RawFlipThreshold"/> and the share of those inside a changed patch (<see cref="BlobNeighbours"/> of 25),
    /// over the region; <paramref name="blob"/> marks the patch pixels. A still camera with FSR 2 (its own jitter) or a sub-pixel jitter of the camera without it moves well filtered surfaces by a fraction of their
    /// gradient; a surface that picks a value by a hard threshold of an interpolated quantity (a hash of floor(x) on a face at an integer x, a depth tie) switches whole blocks.
    /// </summary>
    (double Raw, double Blob) RawFlicker(byte[] a, byte[] b, int w, int h, bool[] blob)
    {
        int y0 = FullRegion ? 0 : (int)(h * (1f - FloorRows));
        var on = new bool[w * h];
        for (int y = y0; y < h; y++) for (int x = 0; x < w; x++) { int i = y * w + x; on[i] = Math.Abs(a[i] - b[i]) > RawFlipThreshold; }
        long raw = 0, patch = 0, n = 0;
        for (int y = y0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x; n++;
                if (!on[i]) continue;
                raw++;
                int c = 0;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int yy = y + dy, xx = x + dx;
                        if (yy >= y0 && yy < h && xx >= 0 && xx < w && on[yy * w + xx]) c++;
                    }
                if (c >= BlobNeighbours) { patch++; blob[i] = true; }
            }
        return (raw * 100.0 / Math.Max(1, n), patch * 100.0 / Math.Max(1, n));
    }

    List<string>? _shGroups;
    bool _shProbesAdded;
    List<(string Name, Vector3 At, Vector3 Look)>? _shProbeViews;

    /// <summary>Attribution (<c>--shimmer-probe</c>): undoes every probe, then applies one.</summary>
    void SetProbe(string probe)
    {
        AshfallGrime.SetOverlay(probe != "overlay");
        foreach (var n in FindChildrenAll(GetTree().Root))
        {
            if (n is OmniLight3D o) o.Visible = probe != "omni";
            if (n is DirectionalLight3D d) { if (!d.HasMeta("probe_shadow")) d.SetMeta("probe_shadow", d.ShadowEnabled); d.ShadowEnabled = probe != "dirshadow" && d.GetMeta("probe_shadow").AsBool(); if (d.Name == "Fill") d.Visible = probe != "fill"; }
        }
        GetViewport().MeshLodThreshold = probe == "lod" ? 0f : (_kit?.LodThreshold ?? 1f);
        float pull = probe.StartsWith("pull", StringComparison.Ordinal) && float.TryParse(probe[4..], NumberStyles.Float, CultureInfo.InvariantCulture, out var pv) ? pv : AshfallGrime.Pull;
        AshfallGrime.OverlayMaterial().SetShaderParameter("pull", pull);
        AshfallGrime.DarkenMaterial().SetShaderParameter("pull", pull);
        AshfallGrime.OverlayMaterial().SetShaderParameter("amount", probe == "darkonly" ? 0f : 1f);
        AshfallGrime.OverlayMaterial().NextPass = probe == "nodark" ? null : AshfallGrime.DarkenMaterial();
        GD.Print($"SHIMMER probe {probe}");
    }

    static IEnumerable<Node> FindChildrenAll(Node n) { foreach (var c in n.GetChildren()) { yield return c; foreach (var cc in FindChildrenAll(c)) yield return cc; } }

    /// <summary>The kit look's groups for <c>--shimmer-bisect</c>: each asset id (items are named <c>AC-xxx_i</c>) and each ground kind of the remainder.</summary>
    List<string> BisectGroups()
    {
        var g = new SortedSet<string>(StringComparer.Ordinal);
        if (_kit!.GetNodeOrNull("Items") is { } items) foreach (var c in items.GetChildren()) { string n = c.Name; int u = n.IndexOf('_'); if (u > 0) g.Add(n[..u]); }
        if (_kit.GetNodeOrNull("Remainder") is { } rem) foreach (var c in rem.GetChildren()) g.Add("R." + c.Name);
        GD.Print($"SHIMMER bisect groups {g.Count}: {string.Join(",", g)}");
        return [.. g];
    }

    void SetGroupHidden(string? group)
    {
        if (_kit!.GetNodeOrNull("Items") is { } items)
            foreach (var c in items.GetChildren()) if (c is Node3D n) n.Visible = group is null || !((string)n.Name).StartsWith(group + "_", StringComparison.Ordinal);
        if (_kit.GetNodeOrNull("Remainder") is { } rem)
            foreach (var c in rem.GetChildren()) if (c is Node3D n) n.Visible = group is null || "R." + n.Name != group;
    }

    static float Halton(int i, int b) { float f = 1f, r = 0f; for (i++; i > 0; i /= b) { f /= b; r += f * (i % b); } return r; }

    /// <summary>One step of the shimmer run per frame; true when finished.</summary>
    bool Shimmer()
    {
        var names = (_args.GetValueOrDefault("ashfall-shimmer") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<(string Name, Vector3 At, Vector3 Look)> views = names.Select(n => _shots.FirstOrDefault(s => s.Name == n)).Where(s => s.Name is not null).ToList();
        // --shimmer-bisect (attribution): after the views, each view again once per kit asset (and ground kind) with that asset's instances hidden: the group whose hiding removes the flicker is in it
        if (_args.ContainsKey("shimmer-bisect") && _kit is not null)
        {
            _shGroups ??= BisectGroups();
            var baseViews = views.ToList();
            foreach (var bv in baseViews) foreach (var g in _shGroups) views.Add((bv.Name + "~" + g, bv.At, bv.Look));
        }
        // --shimmer-probe a,b,..: after the views, each view again with one change at a time, a plain repeat between them (the state can drift within a run): overlay, omni, dirshadow, lod, fill, sky
        if (_args.GetValueOrDefault("shimmer-probe") is { } probes && !_shProbesAdded)
        {
            var baseViews = views.ToList();
            foreach (var bv in baseViews)
                foreach (var pr in probes.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    views.Add((bv.Name + "!" + pr, bv.At, bv.Look));
                    views.Add((bv.Name + "!base", bv.At, bv.Look));
                }
            _shProbeViews = views;
        }
        if (_shProbeViews is not null) views = _shProbeViews;
        _shProbesAdded = true;
        if (!_shStarted)
        {
            _shStarted = true;
            bool live = _args.ContainsKey("shimmer-live");   // live: everything animates as in a match (fires, particles, lights), so a still camera shows what time alone changes
            if (!live) _game.ProcessMode = ProcessModeEnum.Disabled;   // the storm's flicker and drift stop (fires, lights)
            GD.Print($"SHIMMER start: {views.Count} views{(live ? " (live)" : "")}");
            if (!live) Freeze(this);
            if (_args.ContainsKey("shimmer-fire-steady"))   // attribution: the storm's per-frame work stops (the fire lights keep their mean energy, no flicker); particles still run
                foreach (var n in FindChildren("AshfallStorm", "", true, false)) n.ProcessMode = ProcessModeEnum.Disabled;
            if (_args.GetValueOrDefault("shimmer-hide") is { } hide)   // attribution: hide these named nodes (e.g. FloorDust,Dust,Ash)
                foreach (var name in hide.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    foreach (var n in FindChildren(name, "", true, false)) if (n is Node3D n3) { n3.Visible = false; GD.Print($"SHIMMER hidden {n3.GetPath()}"); }
            ShimmerEnvironment();
            if (_args.GetValueOrDefault("shimmer-ground-drop") is { } drop && float.TryParse(drop, NumberStyles.Float, CultureInfo.InvariantCulture, out var dz)
                && _kit?.GetNodeOrNull<Node3D>("Remainder") is { } rem)
                rem.Position = new Vector3(0, 0, -dz);   // attribution: the drawn ground this far lower (game Z), so a layer just above it no longer competes with it
        }
        if (_shView >= views.Count)
        {
            string tag = _args.GetValueOrDefault("shimmer-tag") ?? "run";
            foreach (var l in _shLines) GD.Print(l);
            GD.Print($"SHIMMER {tag} renderer {RenderingServer.GetCurrentRenderingMethod()} views {_shLines.Count}");
            return true;
        }
        var v = views[_shView];
        if (!_shViewStarted && _shGroups is not null) SetGroupHidden(v.Name.Contains('~') ? v.Name[(v.Name.IndexOf('~') + 1)..] : null);
        if (!_shViewStarted && v.Name.Contains('!')) SetProbe(v.Name[(v.Name.IndexOf('!') + 1)..]);
        if (!_shViewStarted) { _shViewStarted = true; _shAt = v.At; _shLook = v.Look; _shWait = 30; GD.Print($"SHIMMER view {v.Name}"); }   // let shadows, SSAO/SSIL and the fog settle
        if (_shWait > 0)
        {
            // the step: a sub-pixel turn about the up axis (game Z); frame index k (k > 0), the turn k steps; the static pair repeats frame 0
            int k = Math.Max(0, _shFrame - 1);
            float fovRad = Mathf.DegToRad(_camera.Fov);   // vertical
            var size = GetViewport().GetVisibleRect().Size;
            float pxAngle = 2f * MathF.Atan(MathF.Tan(fovRad / 2f) * size.X / size.Y) / size.X;
            var dir = _shLook - _shAt;
            if (_args.GetValueOrDefault("shimmer-move") == "none") Aim(_shAt, _shLook);
            else if (_args.GetValueOrDefault("shimmer-move") == "jitter")   // a sub-pixel jitter of the view (Halton 2,3 within +-0.5 px, as a temporal AA jitters): no net motion
            {
                float jx = (Halton(k, 2) - 0.5f) * pxAngle, jy = (Halton(k, 3) - 0.5f) * pxAngle;
                var side = new Vector3(-dir.Y, dir.X, 0f).Normalized() * (dir.Length() * MathF.Tan(jx));
                var up = new Vector3(0, 0, 1) * (dir.Length() * MathF.Tan(jy));
                Aim(_shAt, _shLook + side + up);
            }
            else if (_args.GetValueOrDefault("shimmer-move") == "dolly")   // a slow walk forward (DollyStep a frame): distances and mesh LODs change as in a match
            {
                var fwd = new Vector3(dir.X, dir.Y, 0f).Normalized() * (k * DollyStep);
                Aim(_shAt + fwd, _shLook + fwd);
            }
            else
            {
                float yaw = k * YawStepPixels * pxAngle;
                var turned = new Vector3(dir.X * MathF.Cos(yaw) - dir.Y * MathF.Sin(yaw), dir.X * MathF.Sin(yaw) + dir.Y * MathF.Cos(yaw), dir.Z);
                Aim(_shAt, _shAt + turned);
            }
            _shWait--;
            return false;
        }
        var img = GetViewport().GetTexture().GetImage();
        string tag2 = _args.GetValueOrDefault("shimmer-tag") ?? "run";
        if (_shFrame <= 1 || _args.ContainsKey("shimmer-save-all"))
        {
            System.IO.Directory.CreateDirectory(_outDir);
            img.SavePng(System.IO.Path.Combine(_outDir, $"{tag2}_{v.Name}_f{_shFrame:D2}.png"));
        }
        var rawY = Luma(img, out int w, out int h);
        var y = HighPass(rawY, w, h);
        if (_shPrevRaw is not null && _shFrame >= 2)
        {
            var blob = new bool[w * h];
            var (rawP, blobP) = RawFlicker(_shPrevRaw, rawY, w, h, blob);
            _shSumRaw += rawP; _shSumBlob += blobP; _shMaxBlob = Math.Max(_shMaxBlob, blobP);
            if (_args.ContainsKey("shimmer-trace")) GD.Print(string.Create(CultureInfo.InvariantCulture, $"TRACE {v.Name} f{_shFrame} t {Time.GetTicksMsec() / 1000.0:F2} raw% {rawP:F3} blob% {blobP:F3}"));
            _shBlobHeat ??= new int[w * h];
            for (int i = 0; i < blob.Length; i++) if (blob[i]) _shBlobHeat[i]++;
        }
        _shPrevRaw = rawY;
        if (_shPrev is not null)
        {
            var (mean, flip) = Diff(_shPrev, y, w, h);
            if (_shFrame == 1) { _shStatic = mean; _shStaticFlip = flip; }   // frame 0 and 1: same camera (frame 1 is k = 0)
            else
            {
                _shSumMotion += mean; _shSumFlip += flip; _shPairs++;
                _shHeat ??= new int[w * h];
                for (int i = 0; i < y.Length; i++) _shHeat[i] += Math.Abs(y[i] - _shPrev[i]);
            }
        }
        _shPrev = y;
        _shFrame++;
        _shWait = _args.ContainsKey("shimmer-live") ? 1 : 3;
        if (_shFrame > FramesWanted)
        {
            _shLines.Add(string.Create(CultureInfo.InvariantCulture,
                $"SHIMMER {tag2} {v.Name,-18} motion mean {_shSumMotion / Math.Max(1, _shPairs):F2} flip% {_shSumFlip / Math.Max(1, _shPairs):F2} | static mean {_shStatic:F2} flip% {_shStaticFlip:F2} ({w}x{h}, yaw {YawStepPixels} px/frame, floor = bottom {FloorRows * 100:F0}%)"));
            _shLines.Add(string.Create(CultureInfo.InvariantCulture,
                $"FLICKER {tag2} {v.Name,-18} aa {_args.GetValueOrDefault("shimmer-aa") ?? "msaa+smaa"} move {_args.GetValueOrDefault("shimmer-move") ?? "yaw"} raw% {_shSumRaw / Math.Max(1, _shPairs):F3} blob% {_shSumBlob / Math.Max(1, _shPairs):F3} blobmax% {_shMaxBlob:F3} pairs {_shPairs} region {(FullRegion ? "full" : "floor")} {w}x{h}"));
            if (_shBlobHeat is not null)   // where whole patches switched: red over the last frame's grey, by how many pairs
            {
                var heat = Image.CreateEmpty(w, h, false, Image.Format.Rgb8);
                for (int yy = 0; yy < h; yy++)
                    for (int xx = 0; xx < w; xx++)
                    {
                        int i = yy * w + xx; float g = rawY[i] / 255f * 0.55f;
                        float r = Math.Min(1f, g + _shBlobHeat[i] * 4f / Math.Max(1, _shPairs));
                        heat.SetPixel(xx, yy, new Color(r, g, g));
                    }
                heat.SavePng(System.IO.Path.Combine(_outDir, $"{tag2}_{v.Name}_blobheat.png"));
                _shBlobHeat = null;
            }
            _shPrevRaw = null; _shSumRaw = _shSumBlob = _shMaxBlob = 0;
            if (_shHeat is not null)   // where the floor flips: the summed |dLuma| of the moving pairs, x4, as a grey image
            {
                var heat = new byte[w * h];
                for (int i = 0; i < heat.Length; i++) heat[i] = (byte)Math.Min(255, _shHeat[i] * 4 / Math.Max(1, _shPairs));
                Image.CreateFromData(w, h, false, Image.Format.L8, heat).SavePng(System.IO.Path.Combine(_outDir, $"{tag2}_{v.Name}_heat.png"));
                _shHeat = null;
            }
            _shView++; _shFrame = 0; _shPrev = null; _shSumMotion = _shSumFlip = 0; _shPairs = 0; _shWait = 0; _shViewStarted = false;
        }
        return false;
    }
}
