using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Customizer;
using Num = System.Numerics;

namespace SmClient.Extras.Warfront.Squad;

/// <summary>
/// The downed markers of the squad (Phase B T5, in place of the retired crowd): the retainers are real World players, drawn, animated and heard by the players' own path, so the only thing the squad
/// draws in the world is the line over a downed Marine's body ("BROTHER CASTOR  DOWN 14", "REVIVING" while the own hold runs), always shown so the Marine to revive is found from afar. Game space.
/// </summary>
public sealed partial class WarfrontSquadMarkers : Node3D
{
    /// <summary>The line's height over the fallen body, and the ring's (m) [ours].</summary>
    public const float LineHeight = 1.5f, RingHeight = 0.6f;

    readonly Dictionary<int, Label3D> _labels = new();
    readonly Dictionary<int, Num.Vector3> _at = new();

    public WarfrontSquadMarkers() { Name = "WarfrontSquadMarkers"; }

    /// <summary>The markers shown this frame.</summary>
    public int Shown => _labels.Count;

    /// <summary>The ring's place over the downed Marine <paramref name="index"/> (game space); null when it is not marked.</summary>
    public Num.Vector3? RingOf(int index) => _at.TryGetValue(index, out var p) ? p + new Num.Vector3(0f, 0f, RingHeight) : null;

    public void Frame(WarfrontSquadModel model, double now)
    {
        var live = new HashSet<int>();
        _at.Clear();
        foreach (int index in model.Indices)
        {
            if (!model.TrySample(index, now, out var s) || !s.Downed) continue;
            live.Add(index);
            _at[index] = s.Position;
            if (!_labels.TryGetValue(index, out var label)) { _labels[index] = label = Tag(index); AddChild(label); }
            label.Position = new Vector3(s.Position.X, s.Position.Y, s.Position.Z + LineHeight);
            label.Text = s.ReviveShare > 0f ? $"{s.Name}  REVIVING" : $"{s.Name}  DOWN {MathF.Ceiling(s.DownedLeft)}";
        }
        foreach (int gone in _labels.Keys.Where(i => !live.Contains(i)).ToList()) { _labels[gone].QueueFree(); _labels.Remove(gone); }
    }

    static Label3D Tag(int index) => new()
    {
        Name = $"SquadDown{index}", Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 32, PixelSize = 0.0007f, FixedSize = true, Modulate = UiKit.LightRed,
        OutlineSize = 10, OutlineModulate = new Color(0, 0, 0, 0.9f), NoDepthTest = false, Font = UiKit.ConceptFont(), DoubleSided = true,
    };
}

/// <summary>
/// The squad's whole client side as one node (slice W10; Phase B T5): the model, the downed markers, the HUD and the panel, fed with the squad's envelope sections (<see cref="Section"/>) and a frame call
/// (<see cref="Frame"/>). The Marines themselves are World players: the networked view draws them as it draws every player. Keys (ours, free ones): <see cref="PanelKey"/> opens the squad panel aboard the ship,
/// <see cref="JumpKey"/> sends the jump-pound mark for an Assault or Raptor in a battle.
/// </summary>
public sealed partial class WarfrontSquadView : Node
{
    public const Key PanelKey = Key.F4, JumpKey = Key.J;

    public WarfrontSquadModel Model { get; } = new();
    public WarfrontSquadMarkers Markers { get; } = new();
    public WarfrontSquadHud Hud { get; }
    public WarfrontSquadPanel Panel { get; }

    /// <summary>A command payload for tag <see cref="SmNet.Extras.Warfront.SquadWire.CommandTag"/> (the networked view sends it as an extras command).</summary>
    public Action<byte[]>? SendCommand { get; set; }
    public Func<int, string?> PlayerName { get; set; } = _ => null;
    /// <summary>Whether the own class has a jump pack (Assault or Raptor): the hint and the key show for it only.</summary>
    public Func<bool> OwnHasJumpPack { get; set; } = () => false;
    /// <summary>The point the fireteam mark uses (the crosshair's point on the ground), game space; null when there is none.</summary>
    public Func<Num.Vector3?> MarkPoint { get; set; } = () => null;
    /// <summary>Whether the own Use is held (the revive's hold, read for the prompt's ring).</summary>
    public Func<bool> UseHeld { get; set; } = () => false;
    /// <summary>A jump-pound landed at a point (dust and sound).</summary>
    public Action<Num.Vector3>? Pound { get; set; }
    /// <summary>Whether the player is aboard the ship (the panel can open there only).</summary>
    public bool Aboard { get; set; }
    public string UseKeyLabel { get; set; } = "E";

    public int SectionsRead { get; private set; }
    public int EventLines { get; private set; }
    public int CommandsSent { get; private set; }

    public WarfrontSquadView(Func<byte, string> className)
    {
        Name = "WarfrontSquadView";
        Hud = new WarfrontSquadHud();
        Panel = new WarfrontSquadPanel(Model, className);
        Panel.RenameRequested += (slot, name) => Send(SmNet.Extras.Warfront.SquadWire.EncodeRename((byte)slot, name));
        Panel.CloseRequested += Panel.Close;
    }

    void Send(byte[]? payload)
    {
        if (payload is null) return;
        CommandsSent++;
        SendCommand?.Invoke(payload);
    }

    /// <summary>One envelope section of the squad; true when it was one of tags 32 to 34 (a malformed one is counted and ignored; tag 32 is retired and only taken).</summary>
    public bool Section(byte tag, ReadOnlySpan<byte> payload, double now)
    {
        switch (tag)
        {
            case SmNet.Extras.Warfront.SquadWire.StateTag:
                break;   // retired by Phase B T2 (the retainers are World players): an old server's section is taken and dropped
            case SmNet.Extras.Warfront.SquadWire.RosterTag:
                if (SmNet.Extras.Warfront.SquadWire.TryDecodeRoster(payload, out var roster)) Model.ApplyRoster(roster); else Model.CountMalformed();
                break;
            case SmNet.Extras.Warfront.SquadWire.EventsTag:
                if (SmNet.Extras.Warfront.SquadWire.TryDecodeEvents(payload, out var events))
                {
                    var pounds = new List<Num.Vector3>();
                    foreach (var line in Model.ApplyEvents(events, PlayerName, pounds, now)) { Hud.AddFeed(line, now); EventLines++; }
                    foreach (var p in pounds) { if (Pound is not null) Pound(p); else Puff(p); }
                }
                else Model.CountMalformed();
                break;
            default: return false;
        }
        SectionsRead++;
        return true;
    }

    /// <summary>The frame: the downed markers, the HUD line, the jump hint, the revive prompt and ring, the feed, the open panel.</summary>
    public void Frame(double now, Camera3D? camera, Node3D? gameSpace, Num.Vector3 player)
    {
        Model.Frame(now);
        Markers.Frame(Model, now);
        var (_, _, downed) = Model.OwnCounts(now);
        string? line = Model.HudLine(now);
        Hud.SetLine(line, alert: downed > 0);
        Hud.SetJumpHint(line is not null && OwnHasJumpPack() ? $"[{JumpKey}] JUMP ASSAULT" : null);
        var revive = Aboard ? null : Model.ReviveCandidate(player, now, UseHeld());
        if (revive is { } r)
        {
            Vector2? ring = null;
            if (camera is not null && gameSpace is not null && Markers.RingOf(r.Index) is { } head)
            {
                var world = gameSpace.ToGlobal(new Vector3(head.X, head.Y, head.Z));
                if (!camera.IsPositionBehind(world)) ring = camera.UnprojectPosition(world);
            }
            Hud.SetPrompt($"HOLD [{UseKeyLabel}] TO REVIVE {r.Name}", ring, r.Share);
        }
        else Hud.SetPrompt(null, null, 0f);
        Hud.Tick(now);
        if (Panel.Visible) Panel.Refresh();
    }

    /// <summary>Keys: the panel key (aboard the ship), Escape closes it, the jump key sends the mark. True when the event was used.</summary>
    public bool HandleKey(InputEventKey key)
    {
        if (!key.Pressed || key.Echo) return false;
        if (key.PhysicalKeycode == PanelKey && Aboard) { if (Panel.Visible) Panel.Close(); else Panel.Open(); return true; }
        if (key.PhysicalKeycode == Key.Escape && Panel.Visible) { Panel.Close(); return true; }
        if (key.PhysicalKeycode == JumpKey && !Aboard && !Panel.Visible && OwnHasJumpPack() && Model.HudLine(0) is not null)
        {
            if (MarkPoint() is { } mark) { Send(SmNet.Extras.Warfront.SquadWire.EncodeJumpPound(mark)); return true; }
        }
        return false;
    }

    /// <summary>The default pound effect: a ground-coloured dust puff that grows and fades (a stand-in for the pack's own pound effect; no sound here).</summary>
    void Puff(Num.Vector3 point)
    {
        var material = new StandardMaterial3D { AlbedoColor = new Color(0.62f, 0.55f, 0.45f, 0.55f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        var node = new MeshInstance3D { Mesh = new SphereMesh { Radius = 1f, Height = 1.2f }, MaterialOverride = material, Position = new Vector3(point.X, point.Y, point.Z + 0.3f), Scale = new Vector3(0.5f, 0.5f, 0.5f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        Markers.AddChild(node);
        var tween = node.CreateTween();
        tween.SetParallel();
        tween.TweenProperty(node, "scale", new Vector3(4f, 4f, 2.5f), 0.55);
        tween.TweenProperty(material, "albedo_color:a", 0f, 0.55);
        tween.Chain().TweenCallback(Callable.From(node.QueueFree));
    }

    /// <summary>The Librarian's stored squad class, after joining and on change (<c>SquadClass</c> 0 Assault, 1 Tactical, 2 Devastator).</summary>
    public void SendSquadClass(byte option) => Send(SmNet.Extras.Warfront.SquadWire.EncodeSquadClass(option));
}
