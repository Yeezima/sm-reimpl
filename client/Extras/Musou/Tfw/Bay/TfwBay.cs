using System;
using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The Thunderhawk troop bay's wall assets (owner, 2026-11-14): the owner's kit models (<see cref="OwnerKit"/>) mounted on the hull's tech panels where the coloured floor pads used to be, placed by
/// <see cref="TfwBayProps"/> (EQUIPMENT: Armory over the mag-lock rack; FIRING RANGE: the airlock; DESTINATION: the drop deployment panel), each with a brass label. Pure picture: the trigger is the unchanged station
/// zone and Use key (<c>TfwStationsFrame</c>), its prompt is the HUD's. No collision (a 0.2 m prop inside the 0.4 m body radius is a picture, not an obstacle). Node names are the props' (<c>Armory</c>, <c>Rack</c>,
/// <c>Airlock</c>, <c>Deploy</c>). A missing file is a box stand-in (placeholder demonstration).
/// </summary>
public partial class TfwBay : Node3D
{
    readonly TfwHub _hub;
    readonly List<string> _notes;

    /// <summary>Props placed, and how many of them are box stand-ins (the log reads these).</summary>
    public int Props { get; private set; }
    public int StandIns { get; private set; }

    TfwBay(TfwHub hub, List<string> notes) { _hub = hub; _notes = notes; Name = "TfwBay"; }

    public static TfwBay Build(TfwHub hub, List<string> notes)
    {
        var bay = new TfwBay(hub, notes);
        foreach (var p in TfwBayProps.All) bay.Mount(p);
        return bay;
    }

    void Mount(in TfwBayProp p)
    {
        // A frame on the wall: local X along the wall to the viewer's right, Y up, Z out of the wall into the room (the ship is turned half a circle in the world, TfwShip.ToWorld).
        float nx = p.RightWall ? -1f : 1f;
        var normal = new Vector3(-nx, 0f, 0f);
        var up = new Vector3(0f, 0f, 1f);
        var at = _hub.ToWorld(new Num.Vector3(TfwBayProps.WallX(p), p.Y, TfwShip.FloorZ + p.AboveFloor));
        var frame = new Node3D { Name = p.Name, Transform = new Transform3D(new Basis(up.Cross(normal), up, normal), new Vector3(at.X, at.Y, at.Z)) };
        AddChild(frame);

        var unit = new Vector3(p.UnitSize.X, p.UnitSize.Y, p.UnitSize.Z);
        var mesh = OwnerKit.Mesh(p.File, _notes);
        Props++;
        if (mesh is null) StandIns++;
        var b = OwnerKit.Bounds(mesh, unit);
        var c = b.GetCenter();
        float s = p.Scale;
        frame.AddChild(new MeshInstance3D
        {
            Name = "Body", Mesh = mesh ?? new BoxMesh { Size = unit }, MaterialOverride = OwnerKit.Steel(), Scale = new Vector3(s, s, s),
            Position = new Vector3(-c.X * s, -c.Y * s, p.Protrude - b.End.Z * s),
        });
        if (p.Label.Length > 0) Label(frame, p.Label, new Vector3(0f, p.LabelHeight, 0.03f));
    }

    /// <summary>A brass label plate over a station: plain capitals, unlit so the bay's dark does not hide it.</summary>
    static void Label(Node3D frame, string text, Vector3 at)
    {
        const float size = 0.11f;
        frame.AddChild(new MeshInstance3D
        {
            Name = "LabelPlate", Mesh = new BoxMesh { Size = new Vector3(0.1f + text.Length * size * 0.62f, size * 1.5f, 0.02f) }, MaterialOverride = OwnerKit.Brass(), Position = at,
        });
        frame.AddChild(new Label3D
        {
            Name = "Label", Text = text, FontSize = 64, PixelSize = size / 52f, Modulate = new Color(0.06f, 0.05f, 0.03f), OutlineSize = 0, Shaded = false, DoubleSided = false,
            Position = at + new Vector3(0f, 0f, 0.016f),
        });
    }
}
