using System;
using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The battle's markers in the world (design section G): a ring of each capture position's radius and the HQ's, coloured by its state, with a small letter over A, B and C and no other text but the commander's small status while aimed at (the owner, 2026-11-02;
/// the tracker with the states is on the M map); a pillar at each Ork source still sending; a beacon at the landing zone. Placeholder demonstration, drawn from tag 8 and the map's layout. Lives in game space (Z up, metres); hidden outside a battle. Colours and sizes are ours.
/// </summary>
public partial class TfwMarkers : Node3D
{
    const float RingHeight = 0.15f, PillarHeight = 40f, LabelHeight = 9f;
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
            _ringPaint[k] = Paint(Locked, 1f);
            _rings[k] = new MeshInstance3D { Name = "Ring" + k, Mesh = new TorusMesh { InnerRadius = 0.98f, OuterRadius = 1.02f, Rings = 96, RingSegments = 6 }, MaterialOverride = _ringPaint[k], Basis = Upright, Visible = false };
            AddChild(_rings[k]);
        }
        for (int k = 0; k < _labels.Length; k++)
        {
            // A letter over each position, seen through walls and from afar (placeholder look, ours): the letter is the position's name on the map too.
            _labels[k] = new Label3D { Name = "Label" + k, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, FixedSize = true, PixelSize = 0.0012f, FontSize = 40, OutlineSize = 14, Modulate = Locked, OutlineModulate = new Color(0f, 0f, 0f, 0.9f), RenderPriority = 10, TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear };
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
        for (int k = 0; k < _rings.Length; k++)
        {
            bool shown = k < objectives.Count;
            _rings[k].Visible = shown;
            if (k < _labels.Length) _labels[k].Visible = shown;
            if (!shown) continue;
            var o = objectives[k];
            float radius = MathF.Max(o.Radius, 1f);
            _rings[k].Basis = Upright.Scaled(new Vector3(radius, radius, 1f));
            _rings[k].Position = new Vector3(o.Position.X, o.Position.Y, o.Position.Z + RingHeight);
            var paint = o.State switch
            {
                TfwPositionState.Held => Held, TfwPositionState.Clearing => Clearing, TfwPositionState.Securing => Securing, TfwPositionState.Secured => Secured, _ => Locked,
            };
            _ringPaint[k].AlbedoColor = paint;
            if (k >= _labels.Length)   // the commander's position: its ring, and a small status while aimed at
            {
                _commander.Position = new Vector3(o.Position.X, o.Position.Y, o.Position.Z + LabelHeight);
                _commander.Text = "COMMANDER " + TfwHudModel.ObjectiveState(o);
                _commander.Modulate = paint;
                _commander.Visible = Aimed(_commander.Position);
                continue;
            }
            _labels[k].Text = ((char)('A' + k)).ToString();
            _labels[k].Modulate = paint;
            _labels[k].Position = new Vector3(o.Position.X, o.Position.Y, o.Position.Z + LabelHeight);
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
