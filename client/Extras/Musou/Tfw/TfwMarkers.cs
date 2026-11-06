using System;
using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The battle's markers in the world (design section G): a ring of each capture position's radius and the HQ's, with a letter over A, B and C and no other text but the commander's small status while aimed at (the owner, 2026-11-02;
/// the tracker with the states is on the M map); a pillar at each Ork source still sending; a beacon at the landing zone. Placeholder demonstration, drawn from tag 8 and the map's layout. Lives in game space (Z up, metres); hidden outside a battle. Colours and sizes are ours.
/// Concept pass (the owner, 2026-11-06: "thin, opaque black ground rings that fit the battlefield"; letters "much higher above their points", shown "only when the player is looking toward its point", never "obscuring combat" on it):
/// the ring is a black decal projected on the ground and the rubble (<see cref="RingWidth"/> wide; players, Orks and pickups are on render layer 2, which decals skip), a flat black band on the Compatibility renderer (no decals there);
/// the letter stands <see cref="LabelHeight"/> m up, fades in within <see cref="LookDegrees"/> of the view's heading toward the point and out near and inside the ring (<see cref="NearMargin"/>). The state is on the HUD's capture tracker
/// (<see cref="TfwHud"/>) and in the letter's colour. <c>--tfw-old-markers</c> (test aid): the coloured torus rings and the low letters always shown.
/// </summary>
public partial class TfwMarkers : Node3D
{
    const float RingHeight = 0.15f, PillarHeight = 40f, OldLabelHeight = 9f;
    public const float LabelHeight = TfwMarkerRules.LabelHeight, RingWidth = TfwMarkerRules.RingWidth, LookDegrees = TfwMarkerRules.LookDegrees, NearMargin = TfwMarkerRules.NearMargin;
    static readonly Color RingBlack = new(0.012f, 0.012f, 0.012f);
    readonly bool _old = Array.IndexOf(OS.GetCmdlineUserArgs(), "--tfw-old-markers") >= 0;
    readonly Decal?[] _decals = new Decal?[WarRules.Positions + 1];
    readonly float[] _decalRadius = new float[WarRules.Positions + 1];
    static readonly Dictionary<int, Texture2D> RingTextures = new();
    /// <summary>Godot's meshes stand along Y; game space stands along Z.</summary>
    static readonly Basis Upright = new(Vector3.Right, Mathf.Pi / 2f);
    static readonly Color Locked = new(0.45f, 0.45f, 0.48f), Held = new(0.9f, 0.12f, 0.08f), Clearing = new(1f, 0.5f, 0.08f), Securing = new(1f, 0.85f, 0.2f), Secured = new(0.3f, 0.9f, 0.35f);

    readonly MeshInstance3D[] _rings = new MeshInstance3D[WarRules.Positions + 1];
    readonly StandardMaterial3D[] _ringPaint = new StandardMaterial3D[WarRules.Positions + 1];
    readonly MeshInstance3D[] _pillars = new MeshInstance3D[TfwWire.MaxSites - 1];
    readonly Label3D[] _labels = new Label3D[WarRules.Positions];
    MeshInstance3D _beacon = null!;
    /// <summary>The commander's status (the owner, 2026-11-02): much smaller than the letters, only while the crosshair is within <see cref="AimDegrees"/> of his position (ours).</summary>
    Label3D _commander = null!;
    public const float AimDegrees = 6f;
    public const int CommanderFontSize = 14;

    static StandardMaterial3D Paint(Color c, float alpha) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(c, alpha), CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        Transparency = alpha < 1f ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
    };

    MeshInstance3D Pillar(string name, Color c, float radius)
    {
        var m = new MeshInstance3D { Name = name, Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = PillarHeight, RadialSegments = 16, Rings = 1 }, MaterialOverride = Paint(c, 0.3f), Basis = Upright, Visible = false };
        AddChild(m);
        return m;
    }

    public override void _Ready()
    {
        for (int k = 0; k < _rings.Length; k++)
        {
            _ringPaint[k] = Paint(_old ? Locked : RingBlack, 1f);
            _rings[k] = new MeshInstance3D { Name = "Ring" + k, Mesh = new TorusMesh { InnerRadius = 0.98f, OuterRadius = 1.02f, Rings = 96, RingSegments = 6 }, MaterialOverride = _ringPaint[k], Basis = Upright, Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Layers = 2 };
            AddChild(_rings[k]);
        }
        for (int k = 0; k < _labels.Length; k++)
        {
            // A letter over each position, seen through walls and from afar (placeholder look, ours): the letter is the position's name on the map too.
            _labels[k] = new Label3D { Name = "Label" + k, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, FixedSize = true, PixelSize = 0.0012f, FontSize = _old ? 40 : 48, OutlineSize = 14, Modulate = Locked, OutlineModulate = new Color(0f, 0f, 0f, 0.9f), RenderPriority = 10, TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear };
            AddChild(_labels[k]);
        }
        _commander = new Label3D { Name = "Commander", Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, FixedSize = true, PixelSize = 0.0012f, FontSize = CommanderFontSize, OutlineSize = 6, OutlineModulate = new Color(0f, 0f, 0f, 0.9f), RenderPriority = 10, TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear, Visible = false };
        AddChild(_commander);
        for (int k = 0; k < _pillars.Length; k++) _pillars[k] = Pillar("Source" + k, Held, 1f);
        _beacon = Pillar("Beacon", new Color(0.3f, 0.7f, 1f), 0.5f);
    }

    /// <summary>Whether the crosshair (the screen centre) is within <see cref="AimDegrees"/> of a point in this node's space.</summary>
    bool Aimed(Vector3 local)
    {
        if (GetViewport()?.GetCamera3D() is not { } camera) return false;
        var to = ToGlobal(local) - camera.GlobalPosition;
        return to.LengthSquared() > 1e-4f && Mathf.RadToDeg((-camera.GlobalTransform.Basis.Z).AngleTo(to)) <= AimDegrees;
    }

    /// <summary>A ring texture for a decal of half-size <paramref name="half"/> m with the ring at <paramref name="radius"/> m (black, opaque, its edges antialiased), cached by the radius in 5 cm steps.</summary>
    static Texture2D RingTexture(float radius, float half)
    {
        int key = (int)MathF.Round(radius * 20f);
        if (RingTextures.TryGetValue(key, out var cached)) return cached;
        int n = radius > 14f ? 2048 : 1024;
        var img = Image.CreateEmpty(n, n, false, Image.Format.La8);
        float metresPerPixel = 2f * half / n, hw = RingWidth / 2f;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float px = ((x + 0.5f) / n * 2f - 1f) * half, py = ((y + 0.5f) / n * 2f - 1f) * half;
                float d = MathF.Abs(MathF.Sqrt(px * px + py * py) - radius);
                float a = Math.Clamp((hw - d) / metresPerPixel + 0.5f, 0f, 1f);
                img.SetPixel(x, y, new Color(0f, 0f, 0f, a));
            }
        img.GenerateMipmaps();
        return RingTextures[key] = ImageTexture.CreateFromImage(img);
    }

    /// <summary>The black decal of ring <paramref name="k"/> at <paramref name="at"/> (its ground) and <paramref name="radius"/>; null on the Compatibility renderer (the flat band is drawn instead).</summary>
    Decal? RingDecal(int k, Vector3 at, float radius)
    {
        if (SafeMode.Compatibility) return null;
        float half = radius + 0.6f;
        if (_decals[k] is not { } d || MathF.Abs(_decalRadius[k] - radius) > 0.01f)
        {
            _decals[k]?.QueueFree();
            d = _decals[k] = new Decal
            {
                Name = "RingDecal" + k, TextureAlbedo = RingTexture(radius, half), Modulate = new Color(1f, 1f, 1f, 1f), AlbedoMix = 1f, CullMask = 1u,
                Size = new Vector3(2f * half, 7f, 2f * half), UpperFade = 0.05f, LowerFade = 0.05f, NormalFade = 0.15f, Basis = Upright,
                DistanceFadeEnabled = true, DistanceFadeBegin = 260f, DistanceFadeLength = 40f,
            };
            _decalRadius[k] = radius;
            AddChild(d);
        }
        d.Position = at + new Vector3(0f, 0f, 1.5f);   // 7 m deep: 5 m below the point's ground, 2 m above it (steps and rubble inside the ring)
        return d;
    }

    /// <summary>A pillar shows only from afar: seen from beside or inside it, it would tint the whole screen.</summary>
    static bool Far(Num.Vector3 pillar, Num.Vector3 viewer) => new Vector2(pillar.X - viewer.X, pillar.Y - viewer.Y).Length() > TfwMap.LandingRadius;

    /// <summary>The markers of the war as it stands; <paramref name="viewer"/>: the own player's feet.</summary>
    public void Show(in TfwWarView war, IReadOnlyList<TfwObjective> objectives, IReadOnlyList<Num.Vector3> sites, Num.Vector3 viewer)
    {
        if (_beacon is null) return;
        bool battle = TfwHudModel.InBattle(war.Phase) && objectives.Count > 0;
        Visible = battle;
        if (!battle) return;
        _commander.Visible = false;
        var camera = GetViewport()?.GetCamera3D();
        Num.Vector3 eye = default, forward = default;
        if (camera is not null)
        {
            var e = ToLocal(camera.GlobalPosition);
            var f = ToLocal(camera.GlobalPosition - camera.GlobalTransform.Basis.Z) - e;
            eye = new Num.Vector3(e.X, e.Y, e.Z);
            forward = new Num.Vector3(f.X, f.Y, f.Z);
        }
        for (int k = 0; k < _rings.Length; k++)
        {
            bool shown = k < objectives.Count;
            if (k < _labels.Length) _labels[k].Visible = shown;
            if (!shown) { _rings[k].Visible = false; if (_decals[k] is { } gone) gone.Visible = false; continue; }
            var o = objectives[k];
            float radius = MathF.Max(o.Radius, 1f);
            var ground = new Vector3(o.Position.X, o.Position.Y, o.Position.Z);
            var decal = _old ? null : RingDecal(k, ground, radius);
            if (decal is not null) decal.Visible = true;
            _rings[k].Visible = decal is null;
            if (_old) _rings[k].Basis = Upright.Scaled(new Vector3(radius, radius, 1f));
            else
            {
                // the Compatibility renderer's band: a torus pressed flat, RingWidth wide at any radius
                float w = RingWidth / 2f / radius;
                if (_rings[k].Mesh is TorusMesh t && MathF.Abs(t.OuterRadius - (1f + w)) > 1e-4f) _rings[k].Mesh = new TorusMesh { InnerRadius = 1f - w, OuterRadius = 1f + w, Rings = 128, RingSegments = 4 };
                _rings[k].Basis = Upright.Scaled(new Vector3(radius, radius, 0.05f));
            }
            _rings[k].Position = new Vector3(o.Position.X, o.Position.Y, o.Position.Z + (_old ? RingHeight : 0.06f));
            var paint = o.State switch
            {
                TfwPositionState.Held => Held, TfwPositionState.Clearing => Clearing, TfwPositionState.Securing => Securing, TfwPositionState.Secured => Secured, _ => Locked,
            };
            if (_old) _ringPaint[k].AlbedoColor = paint;
            if (k >= _labels.Length)   // the commander's position: its ring, and a small status while aimed at
            {
                _commander.Position = new Vector3(o.Position.X, o.Position.Y, o.Position.Z + OldLabelHeight);
                _commander.Text = "WARBOSS " + TfwHudModel.ObjectiveState(o);   // the commander is the Warboss (owner, 2026-11-06)
                _commander.Modulate = paint;
                _commander.Visible = Aimed(_commander.Position);
                continue;
            }
            _labels[k].Text = ((char)('A' + k)).ToString();
            float alpha = _old ? 1f : TfwMarkerRules.LetterAlpha(o.Position, radius, eye, forward, viewer);
            _labels[k].Modulate = new Color(paint, alpha);
            _labels[k].OutlineModulate = new Color(0f, 0f, 0f, 0.9f * alpha);
            _labels[k].Visible = alpha > 0.01f;
            _labels[k].Position = new Vector3(o.Position.X, o.Position.Y, o.Position.Z + (_old ? OldLabelHeight : LabelHeight));
        }
        bool final = war.Phase == TfwRunPhase.FinalAssault;
        for (int k = 0; k < _pillars.Length; k++)
        {
            bool shown = k + 1 < sites.Count;
            _pillars[k].Visible = shown && TfwHudModel.SourceActive(k, objectives, war.Own.BattleWon, final) && Far(sites[k + 1], viewer);
            if (!shown) continue;
            var at = sites[k + 1];
            _pillars[k].Position = new Vector3(at.X, at.Y, at.Z + PillarHeight / 2f);
        }
        _beacon.Visible = sites.Count > 0 && Far(sites[0], viewer);
        if (sites.Count > 0) _beacon.Position = new Vector3(sites[0].X, sites[0].Y, sites[0].Z + PillarHeight / 2f);
    }
}
