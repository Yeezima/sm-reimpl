using System;
using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// Musou Mode's capture banners (owner, 2026-11-10: "a banner is red when ork held, is covered by smoke, then when the smoke obscuring the banner completely dissipates the banner is now blue when captured by the
/// player and his team"; "Original banners"; smoke "At the flip only"): a banner stands at the centre of each position, the multiplayer set's own <c>waaaghbanner</c> (the Orks', red) while the Orks hold it and
/// <c>sm_banner</c> (the Space Marines', blue) once it is secured (<see cref="TfwMarkerRules.BannerImperial"/>; docs/discovery.md "Musou capture banners"). When the owner changes, a cloud of smoke (<see cref="TfwSmoke"/>,
/// banner-sized, behind walls like any object) covers it; the banner changes under the opaque cloud, <see cref="SwapAt"/> s in, so the cloud clears off the new one. *Original asset loaded* when the models read;
/// a placeholder pole and flag in the side's colour when one does not. Sizes and timings ours. <c>--musou-old-capture-ui</c> leaves them out (the markers and HUD tracker before 2026-11-10).
/// Lives in <see cref="TfwMarkers"/> (game space, Z up, metres).
/// </summary>
public partial class TfwBanners : Node3D
{
    /// <summary><c>--musou-old-capture-ui</c>: the capture UI before 2026-11-10 (the HUD's tiles, line and bar, the letters in the state's colour, the source beams, no banners, the full ring).</summary>
    public static readonly bool Old = Array.IndexOf(OS.GetCmdlineUserArgs(), "--musou-old-capture-ui") >= 0;

    public static readonly MekModels.Part ImperialBanner = new("ebp_sets__mp_versus_set", "sm_banner");
    public static readonly MekModels.Part OrkBanner = new("ebp_sets__mp_versus_set", "waaaghbanner");

    /// <summary>The banner's height above its point (m; readable from afar, ours), the flip's cloud (opaque, gone, its scale on the class change's player-sized cloud) and when the banner changes under it (s).</summary>
    public const float BannerHeight = 8f, SmokeOpaque = 1.1f, SmokeTotal = 3.6f, SmokeScale = 3.8f, SwapAt = 0.35f;

    static readonly Color Red = new(0.78f, 0.1f, 0.06f), Blue = new(0.16f, 0.36f, 0.85f);

    sealed class Slot
    {
        public Node3D? Imperial, Ork;
        public bool Known, Shown, Target;
        public double SwapAt = -1;
    }

    readonly Slot[] _slots = new Slot[WarRules.Positions + 1];
    readonly MekModels? _models;
    bool _placeholders;
    double _now;

    /// <summary>Owner changes seen since the battle began (the log and the capture aid).</summary>
    public int Flips { get; private set; }

    public TfwBanners(string? gameDir)
    {
        Name = "TfwBanners";
        for (int k = 0; k < _slots.Length; k++) _slots[k] = new Slot();
        if (!string.IsNullOrEmpty(gameDir) && System.IO.Directory.Exists(gameDir)) _models = new MekModels(gameDir, [ImperialBanner, OrkBanner]);
        else _placeholders = true;
    }

    public override void _Process(double delta) => _now += delta;

    /// <summary>A new battle: the banners take the positions' owners as they stand, without a cloud.</summary>
    public void Reset()
    {
        foreach (var s in _slots) { s.Known = false; s.SwapAt = -1; }
        Flips = 0;
    }

    /// <summary>This frame's banners for <paramref name="objectives"/> (A, B, C, then the commander's position).</summary>
    public void Show(IReadOnlyList<TfwObjective> objectives)
    {
        if (_models is { Done: false } models)
        {
            models.Step();
            foreach (var note in models.Notes) GD.Print(note);
            models.Notes.Clear();
            if (!models.Done) return;   // the first banners wait for both models (a few frames at the battle's start)
        }
        if (_models is { } m && (m.Get(ImperialBanner) is null || m.Get(OrkBanner) is null) && !_placeholders)
        {
            _placeholders = true;
            GD.Print("musou: capture banners: an original banner does not load, placeholders instead");
        }
        for (int k = 0; k < _slots.Length; k++)
        {
            var s = _slots[k];
            if (k >= objectives.Count) { Hide(s); continue; }
            var o = objectives[k];
            var at = new Vector3(o.Position.X, o.Position.Y, o.Position.Z);
            s.Imperial ??= Build(true, k);
            s.Ork ??= Build(false, k);
            bool imperial = TfwMarkerRules.BannerImperial(o);
            if (!s.Known) { s.Known = true; s.Shown = s.Target = imperial; s.SwapAt = -1; }
            else if (imperial != s.Target)
            {
                // the flip: a cloud covers the banner; it changes under the cloud's opaque part (a flip back during the cloud starts another)
                s.Target = imperial;
                s.SwapAt = _now + SwapAt;
                Flips++;
                var smoke = new TfwSmoke((uint)(k * 7919 + Flips * 104729), 0f, SmokeOpaque, SmokeTotal, throughWalls: false)
                    { Position = at, Scale = new Vector3(SmokeScale, SmokeScale, SmokeScale) };
                AddChild(smoke);
                GD.Print($"musou: capture banner {TfwMarkerRules.TileName(k)} flips to {(imperial ? "the players" : "the Orks")} (smoke)");
            }
            if (s.SwapAt >= 0 && _now >= s.SwapAt) { s.Shown = s.Target; s.SwapAt = -1; }
            s.Imperial.Position = at;
            s.Ork.Position = at;
            s.Imperial.Visible = s.Shown;
            s.Ork.Visible = !s.Shown;
        }
    }

    static void Hide(Slot s)
    {
        if (s.Imperial is not null) s.Imperial.Visible = false;
        if (s.Ork is not null) s.Ork.Visible = false;
    }

    /// <summary>One banner: the original model (<see cref="BannerHeight"/> tall, standing on its point) or a placeholder pole and flag in the side's colour.</summary>
    Node3D Build(bool imperial, int k)
    {
        Node3D node;
        if (!_placeholders && _models?.Get(imperial ? ImperialBanner : OrkBanner) is { } built)
            node = MekModels.Place(built, Vector3.One * MekModels.ToHeight(built, BannerHeight), foot: true, (imperial ? "Imperial" : "Ork") + "Banner" + k);
        else node = Placeholder(imperial, k);
        node.Visible = false;
        AddChild(node);
        return node;
    }

    static Node3D Placeholder(bool imperial, int k)
    {
        var root = new Node3D { Name = (imperial ? "Imperial" : "Ork") + "BannerPlaceholder" + k };
        var pole = new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.11f, 0.1f) };
        var cloth = new StandardMaterial3D { AlbedoColor = imperial ? Blue : Red, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.08f, BottomRadius = 0.1f, Height = BannerHeight }, MaterialOverride = pole,
            Transform = new Transform3D(new Basis(Vector3.Right, Mathf.Pi / 2f), new Vector3(0f, 0f, BannerHeight / 2f)) });
        root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(2.2f, 0.04f, 3.2f) }, MaterialOverride = cloth, Position = new Vector3(0f, 0f, BannerHeight - 2.2f) });
        return root;
    }
}
