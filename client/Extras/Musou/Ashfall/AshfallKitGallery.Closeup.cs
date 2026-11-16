using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmSim.Extras.Musou.Ashfall;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Test aid (kit-hi-res, 2026-11-09): <c>--ashfall-closeup [arena|AC-001,..]</c> shows each kit model the city (<c>tfw_war</c>) places inside the battle arena, alone and at the size of its largest item there,
/// in the map's light, with the map's LOD threshold (<see cref="AshfallKitView.LodThreshold"/>, <c>--ashfall-lod-threshold</c>) and the arena's detail rules (<see cref="AshfallKitDetail"/>;
/// <c>--ashfall-kit-detail old</c> draws as before the pass). Two shots per model: <c>ID_near</c> (eye height, 5 to 14 m from its face) and <c>ID_mid</c> (35 m out), saved to <c>--out</c>.
/// </summary>
public partial class AshfallKitGallery
{
    bool _closeup;

    void BuildCloseup()
    {
        _closeup = true;
        var (_, dressing) = AshfallMap.BuildWithDressing(AshfallLayout.City);
        string want = _args.GetValueOrDefault("ashfall-closeup") ?? "arena";
        var wanted = want == "arena" ? null : want.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        var largest = dressing.Items.Where(AshfallKitDetail.InsideArena).GroupBy(i => i.Asset)
            .Where(g => wanted is null || wanted.Contains(g.Key))
            .Select(g => g.OrderByDescending(i => MathF.Max(i.Size.X, MathF.Max(i.Size.Y, i.Size.Z))).First()).OrderBy(i => i.Asset, StringComparer.Ordinal).ToList();
        AshfallKitDetail.Mark(_library, largest);
        _camera.Fov = 65f;   // the game camera's field of view
        AshfallView.ApplyLook(_sun, _env);
        _env.SsaoEnabled = true;
        float lod = _args.GetValueOrDefault("ashfall-lod-threshold") is { } t && float.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0.1f ? v : AshfallKitView.DefaultLodThreshold;
        GetViewport().MeshLodThreshold = lod;
        int n = 0;
        foreach (var it in largest)
        {
            // each model alone, 80 m apart, inside the arena square (so the arena rules apply to it as in the map)
            var at = new Num.Vector3(-200f + (n % 6) * 80f, -200f + (n / 6) * 80f, 0f);
            n++;
            var item = it with { Position = at, Yaw = 0f };
            var model = AshfallKit.Get(it.Asset);
            _boxes.Add((item, model, new Vector3(item.Size.X, item.Size.Y, item.Size.Z)));
            var c = new Vector3(at.X, at.Y, 0f);
            float half = item.Size.Y / 2f, span = MathF.Max(item.Size.X, item.Size.Z);
            float near = half + Math.Clamp(0.5f * span, 5f, 14f);
            var side = new Vector3(MathF.Sin(0.45f), -MathF.Cos(0.45f), 0f);   // from the front (-Y), turned 26 degrees
            var look = c + new Vector3(0f, 0f, MathF.Min(item.Size.Z * 0.45f, 6f));
            _shots.Add(($"{it.Asset}_near", c + side * near + new Vector3(0f, 0f, 2.2f), look));
            _shots.Add(($"{it.Asset}_mid", c + side * (half + 35f) + new Vector3(0f, 0f, 4f), look));
            GD.Print($"ashfall closeup: {it.Asset} at the size of its largest arena item {item.Size.X:0.0} x {item.Size.Y:0.0} x {item.Size.Z:0.0} m{(item.SwapXZ ? " (swapped)" : "")}, lod bias {AshfallKitDetail.LodBiasOf(item):0.#}, textures up to {(_library.HighDetail.Contains(it.Asset) ? _library.HighTextureMax : _library.TextureMax)} px");
        }
        GD.Print($"ashfall closeup: {largest.Count} models, mesh lod threshold {lod:0.#} px, detail {(AshfallKitDetail.Old ? "old (before kit-hi-res)" : "arena rules")}");
        _library.Request(largest.Select(i => i.Asset).Distinct());
        _game.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(600, 600, 0.2f) }, Position = new Vector3(0, 0, -0.1f), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.22f, 0.21f, 0.20f) } });
    }
}
