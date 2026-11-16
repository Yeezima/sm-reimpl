using System.Numerics;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Extras.Musou.Campaign;

/// <summary>One owner-kit model mounted on a troop bay wall for a station: which file, which wall, where along the hull, how big, and the words on its brass label.</summary>
public readonly record struct TfwBayProp(TfwStation Station, string Name, string File, bool RightWall, float Y, float AboveFloor, float Scale, float Protrude, Vector3 UnitSize, string Label, float LabelHeight);

/// <summary>
/// The Thunderhawk troop bay's wall assets (owner, 2026-11-14: "use the model placements to replace the placeholder yellow, green blue circles on the floor"). Each of the three stations that used to be a coloured
/// pad on the floor is now an owner-kit model mounted on the wall beside the zone: EQUIPMENT is the Armory panel over the mag-lock weapon rack (the wargear and loadout picker), FIRING RANGE is the range airlock
/// (the door you travel through), DESTINATION is the drop deployment panel (select a front and start the battle on the war map). The trigger is unchanged: the same zone (<see cref="TfwMap.StationRadius"/> around
/// the station point on the floor, <see cref="TfwHudModel.StationAt"/>) and the same Use key, with its prompt in the HUD; nothing is drawn on the floor any more. Placements are ours (kind: ours); the kit is one
/// unit along its longest side, Y up, front +Z, each model scaled in metres and sunk 0.2 m into the wall panel. Engine-free so the tests read it.
/// </summary>
public static class TfwBayProps
{
    /// <summary>A prop counts as "in front of" its zone when the zone circle's nearest point is no further than this (m) from the prop's wall point.</summary>
    public const float MaxZoneGap = 0.75f;

    public static readonly TfwBayProp[] All =
    [
        new(TfwStation.Equipment, "Armory", "Armory.glb", true, TfwShip.Panel.Y, 1.7f, 1.5f, 0.2f, new Vector3(1f, 0.739f, 0.278f), "ARMORY", 0.72f),
        new(TfwStation.Equipment, "Rack", "Mag-Lock-Weapon-Rack.glb", true, TfwShip.Panel.Y, 0.88f, 1.3f, 0.2f, new Vector3(1f, 0.523f, 0.16f), "", 0f),
        new(TfwStation.Range, "Airlock", "Firing-Range-Airlock.glb", false, 13.5f, 1.0f, 2.0f, 0.2f, new Vector3(0.626f, 1f, 0.233f), "FIRING RANGE", 1.15f),
        new(TfwStation.Destination, "Deploy", "Drop-Deployment.glb", true, TfwShip.Monitor.Y, 1.7f, 1.3f, 0.2f, new Vector3(0.751f, 1f, 0.395f), "DEPLOY", 0.8f),
    ];

    /// <summary>The kit files the bay needs.</summary>
    public static IEnumerable<string> Files => All.Select(p => p.File).Distinct();

    /// <summary>The wall face (model-space X) a prop stands on.</summary>
    public static float WallX(in TfwBayProp p) => p.RightWall ? TfwShip.RightPanel : TfwShip.LeftPanel;

    /// <summary>The station point of <paramref name="station"/> in model space (across, along the hull).</summary>
    public static Vector2 StationModel(TfwStation station) => station switch
    {
        TfwStation.Equipment => TfwShip.Panel,
        TfwStation.Destination => TfwShip.Monitor,
        TfwStation.Range => new Vector2(-0.9f, 13.5f),   // = TfwHub.RangeStation's model point (a test pins it)
        _ => Vector2.Zero,
    };

    /// <summary>How far the prop's wall point is from the edge of its station's zone (0 when the zone reaches it): the zone is where the player stands to use it.</summary>
    public static float ZoneGap(in TfwBayProp p)
    {
        var s = StationModel(p.Station);
        float d = MathF.Sqrt((WallX(p) - s.X) * (WallX(p) - s.X) + (p.Y - s.Y) * (p.Y - s.Y));
        return MathF.Max(0f, d - TfwMap.StationRadius);
    }
}
