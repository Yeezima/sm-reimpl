using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// The battle's markers in the world (design section G): a ring of each capture position's radius and the HQ's, coloured by its state; a pillar at each Ork source still sending; a beacon at the landing
/// zone. Placeholder demonstration, drawn from tag 8 and the map's layout. Lives in game space (Z up, metres); hidden outside a battle. Colours and sizes are ours.
/// </summary>
public partial class TfwMarkers : Node3D
{
    const float RingHeight = 0.15f, PillarHeight = 40f;
    /// <summary>Godot's meshes stand along Y; game space stands along Z.</summary>
    static readonly Basis Upright = new(Vector3.Right, Mathf.Pi / 2f);
    static readonly Color Locked = new(0.45f, 0.45f, 0.48f), Held = new(0.9f, 0.12f, 0.08f), Clearing = new(1f, 0.5f, 0.08f), Securing = new(1f, 0.85f, 0.2f), Secured = new(0.3f, 0.9f, 0.35f);

    readonly MeshInstance3D[] _rings = new MeshInstance3D[WarRules.Positions + 1];
    readonly StandardMaterial3D[] _ringPaint = new StandardMaterial3D[WarRules.Positions + 1];
    readonly MeshInstance3D[] _pillars = new MeshInstance3D[TfwMap.Sources.Length];
    MeshInstance3D _beacon = null!;

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
            float radius = k == TfwBattle.CommanderObjective ? TfwMap.HqRadius : TfwMap.CaptureRadius;
            _ringPaint[k] = Paint(Locked, 1f);
            _rings[k] = new MeshInstance3D { Name = "Ring" + k, Mesh = new TorusMesh { InnerRadius = radius - 0.2f, OuterRadius = radius + 0.2f, Rings = 96, RingSegments = 6 }, MaterialOverride = _ringPaint[k], Basis = Upright, Visible = false };
            AddChild(_rings[k]);
        }
        for (int k = 0; k < _pillars.Length; k++) _pillars[k] = Pillar("Source" + k, Held, 2.5f);
        _beacon = Pillar("Beacon", new Color(0.3f, 0.7f, 1f), TfwMap.LandingRadius / 5f);
    }

    /// <summary>The markers of the war as it stands.</summary>
    public void Show(in TfwWarView war, IReadOnlyList<TfwObjective> objectives)
    {
        if (_beacon is null) return;
        bool battle = TfwHudModel.InBattle(war.Phase) && objectives.Count > 0;
        Visible = battle;
        if (!battle) return;
        for (int k = 0; k < _rings.Length; k++)
        {
            bool shown = k < objectives.Count;
            _rings[k].Visible = shown;
            if (!shown) continue;
            var o = objectives[k];
            _rings[k].Position = new Vector3(o.Position.X, o.Position.Y, o.Position.Z + RingHeight);
            _ringPaint[k].AlbedoColor = o.State switch
            {
                TfwPositionState.Held => Held, TfwPositionState.Clearing => Clearing, TfwPositionState.Securing => Securing, TfwPositionState.Secured => Secured, _ => Locked,
            };
        }
        if (TfwHudModel.LayoutOf(war.BattleLane, objectives) is not { } layout) return;
        bool final = war.Phase == TfwRunPhase.FinalAssault;
        for (int k = 0; k < _pillars.Length; k++)
        {
            var at = layout.Sources[k];
            _pillars[k].Position = new Vector3(at.X, at.Y, PillarHeight / 2f);
            _pillars[k].Visible = TfwHudModel.SourceActive(k, objectives, war.Own.BattleWon, final);
        }
        _beacon.Position = new Vector3(layout.LandingZone.X, layout.LandingZone.Y, PillarHeight / 2f);
        _beacon.Visible = true;
    }
}
