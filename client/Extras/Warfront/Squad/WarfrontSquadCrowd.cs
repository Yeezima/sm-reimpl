using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Customizer;
using SmSim;
using Num = System.Numerics;

namespace SmClient.Extras.Warfront.Squad;

/// <summary>The clips a squad Marine uses, found once per rig by name (a missing one is null and the actor falls back to a held pose).</summary>
public sealed class WarfrontSquadClips
{
    public string? Idle, Run, Aim, Fire, Melee, Down, DownEnter, GetUp, Air;

    static readonly string[] IdleNames = ["SM_NAV_IdleL_S_01", "SM_NAV_Idle_S_01", "SM_NAV_IdleR_S_01"];

    public static WarfrontSquadClips Of(MarineAssets a)
    {
        string? First(string[] names, params string[] contains)
        {
            foreach (var n in names) if (a.Animation(n) is not null) return n;
            foreach (var c in contains)
                if (a.Model.Rig.AnimationNames.FirstOrDefault(x => x.Contains(c, StringComparison.OrdinalIgnoreCase)) is { } found) return found;
            return null;
        }
        return new WarfrontSquadClips
        {
            Idle = First(IdleNames, "Idle"),
            Run = First([CharacterViewer.DefaultAnimation], "Run_F"),
            Aim = First(["SM_NAV_IdleL_Focus_S_01", "SM_NAV_Focus_Idle_S_01"], "Focus"),
            Fire = First(["SM_RNG_Fire_S_01"], "Fire"),
            Melee = First([], "SM_MEL_"),
            Down = First(["SM_REC_Incap_D_01"], "Incap", "Stun"),          // the incapacitated loop (the downed Marine's pose)
            DownEnter = First(["SM_REC_Idle2Incap_D_01"], "Idle2Incap"),   // going down into it
            GetUp = First(["SM_REC_Stun2Idle_D_01"], "Stun2Idle"),         // back to standing (no revive clip of its own was found: the stun's recovery stands in)
            Air = First([], "Jump", "Fall"),
        };
    }
}

/// <summary>
/// The squad's Marines as characters in the world (slice W10): one <see cref="CharacterViewer"/> per Marine, wearing the look its owner's appearance gives (<see cref="WarfrontSquadLooks"/>), posed from the wire alone:
/// idle, run (speed from the position delta), aim and fire (the flag), melee swing, jump, the downed pose and the way up. **Provisional approximation** (no recoil, no reload, no turn-in-place; the clips are
/// found by name). Beyond <see cref="WarfrontSquadModel.LodMetres"/> a Marine's pose can be stepped every <see cref="FarStride"/> frames (the cheap path; <see cref="Lod"/> switches it). Game space (Z up), metres.
/// </summary>
public sealed partial class WarfrontSquadCrowd : Node3D
{
    /// <summary>A Marine far away re-poses on one frame in this many [estimate].</summary>
    public const int FarStride = 4;
    /// <summary>Seconds between a firing Marine's tracers [estimate: the squad's weapon profile fires about four times a second].</summary>
    public const double ShotEvery = 0.25;
    /// <summary>Seconds the getting-up pose shows after a revive [estimate].</summary>
    public const double GetUpSeconds = 1.2;
    /// <summary>Seconds the going-down clip shows before the downed loop [estimate].</summary>
    public const double DownEnterSeconds = 1.0;

    sealed class Actor
    {
        public Node3D Root = null!;
        public CharacterViewer? Viewer;
        public MeshInstance3D? Placeholder;
        public Label3D Name = null!, Down = null!;
        public WeaponRig? Rig;
        public string? DressKey, RigKey;
        public double NextShot, GetUpUntil, DownedAt;
        public bool WasDowned;
        public string? Clip;
        public int Index;
        public float Dist;
    }

    readonly WarfrontSquadLooks _looks;
    readonly string _gameDir;
    readonly Dictionary<int, Actor> _actors = new();
    readonly Dictionary<string, WarfrontSquadClips> _clips = new();
    readonly Dictionary<string, WeaponAssets> _weapons = new();
    ulong _frame;
    WarfrontSquadModel? _model;
    readonly List<(Label3D Label, float Base, float Dist)> _tags = new();

    /// <summary>What a Marine wears and carries; the shot's end (an enemy id to a place); a shot to draw (from, to, class), game space.</summary>
    public IWarfrontSquadDresser? Dresser { get; set; }
    public Func<int, Num.Vector3?>? EnemyAt { get; set; }
    public Action<Num.Vector3, Num.Vector3, byte>? Shot { get; set; }
    /// <summary>
    /// The cheap path beyond 40 m: a Marine's pose is applied on one frame in <see cref="FarStride"/> (default on). 36 full characters cost 3.7 ms of frame time on the measuring machine (poses; drawing is free
    /// of it), at the 4 ms line the brief set, and the stride saves 3.4 ms of it when all are far; the self-test's <c>--squad-no-lod</c> and <c>--squad-cost</c> measure it.
    /// </summary>
    public bool Lod { get; set; } = true;
    /// <summary>The weapons are hung on the characters within full-detail range (default on).</summary>
    public bool Weapons { get; set; } = true;
    /// <summary>Test aid (the cost measurement): no pose is applied, so the cost of drawing is told from the cost of animating.</summary>
    public bool PoseFrozen { get; set; }
    /// <summary>The camera (names go to the Marine under its crosshair; overlapping labels are stacked upward).</summary>
    public Camera3D? Camera { get; set; }

    public int Drawn => _actors.Count;
    public int WithModel => _actors.Values.Count(a => a.Viewer is not null);
    public int Full { get; private set; }
    public int Far { get; private set; }
    public int ShotsFired { get; private set; }
    public double FrameMs { get; private set; }
    public List<string> Notes { get; } = new();

    public WarfrontSquadCrowd(WarfrontSquadLooks looks, string gameDir) { _looks = looks; _gameDir = gameDir; Name = "WarfrontSquadCrowd"; }

    /// <summary>The place of a drawn Marine's head, game space (for the revive ring); null for one not drawn.</summary>
    public Num.Vector3? HeadOf(int index) => _actors.TryGetValue(index, out var a) ? new Num.Vector3(a.Root.Position.X, a.Root.Position.Y, a.Root.Position.Z + 2.1f) : null;

    /// <summary>One frame: place and pose every known Marine, drop the ones the model no longer has. <paramref name="camera"/> is the camera's place in game space.</summary>
    public void Frame(WarfrontSquadModel model, double now, Num.Vector3 camera, double delta)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _frame++;
        Full = Far = 0;
        var live = new HashSet<int>();
        _model = model;
        int under = Crosshair(model, now);
        foreach (int index in model.Indices)
        {
            if (!model.TrySample(index, now, out var s)) continue;
            live.Add(index);
            if (!_actors.TryGetValue(index, out var a)) _actors[index] = a = NewActor(index);
            Update(a, s, now, camera, (float)delta, index == under);
        }
        foreach (var gone in _actors.Keys.Where(i => !live.Contains(i)).ToList()) { _actors[gone].Root.QueueFree(); _actors.Remove(gone); }
        StackLabels();
        FrameMs = watch.Elapsed.TotalMilliseconds;
    }

    Actor NewActor(int index)
    {
        var a = new Actor { Index = index, Root = new Node3D { Name = $"SquadMarine{index}" } };
        AddChild(a.Root);
        a.Placeholder = new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Radius = 0.32f, Height = 1.8f },
            Basis = new Basis(new Vector3(1, 0, 0), Mathf.Pi / 2f), Position = new Vector3(0, 0, 0.9f),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.38f, 0.5f) },
        };
        a.Root.AddChild(a.Placeholder);
        a.Name = Tag($"NameTag", new Color(1f, 1f, 1f), 2.45f);
        a.Down = Tag("DownTag", UiKit.LightRed, 2.85f);
        a.Root.AddChild(a.Name); a.Root.AddChild(a.Down);
        return a;
    }

    static Label3D Tag(string name, Color colour, float height) => new()
    {
        Name = name, Position = new Vector3(0, 0, height), Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 32, PixelSize = 0.0007f, FixedSize = true, Modulate = colour,
        OutlineSize = 10, OutlineModulate = new Color(0, 0, 0, 0.9f), NoDepthTest = false, Font = UiKit.Font, Visible = false, DoubleSided = true,
    };

    WarfrontSquadClips ClipsOf(MarineAssets m, string key)
    {
        if (!_clips.TryGetValue(key, out var set)) _clips[key] = set = WarfrontSquadClips.Of(m);
        return set;
    }

    void Update(Actor a, in WarfrontSquadShown s, double now, Num.Vector3 camera, float delta, bool underCrosshair)
    {
        var p = s.Position;
        a.Root.Transform = new Transform3D(new Basis(new Vector3(0, 0, 1), s.Yaw - Mathf.Pi / 2f), new Vector3(p.X, p.Y, p.Z));   // wire yaw 0 faces +X; the model faces +Y
        float dx = p.X - camera.X, dy = p.Y - camera.Y, dz = p.Z - camera.Z;
        float distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        bool full = WarfrontSquadModel.FullDetail(distance);
        if (full) Full++; else Far++;

        var dress = Dresser?.Dress(s);
        MarineAssets? model = dress is null ? null : _looks.TryModel(dress);
        if (model is not null && (a.Viewer is null || a.DressKey != dress!.Key)) AttachViewer(a, model, dress!);
        if (a.Viewer is { } viewer)
        {
            var clips = ClipsOf(model ?? viewer.Assets!, a.DressKey ?? "");
            bool throttled = Lod && !full && (_frame + (ulong)a.Index) % FarStride != 0;
            viewer.PoseLocked = throttled || PoseFrozen;
            if ((!throttled || a.Clip is null) && !PoseFrozen) Pose(a, viewer, clips, s, now);
            if (Weapons && full && dress is not null) EnsureRig(a, viewer, dress);
            // The pack wears the owner's backpack-slot paint, like a player's (cheap when unchanged). Only while the shown look's model is
            // ready: during a rebuild (TryModel null) the old rig keeps the paint it has, instead of flashing the side's default.
            if (model is not null) a.Rig?.SetPackPaint(WarfrontSquadModel.PackPaintFor(dress?.Pack, model.PackPaint));
            if (a.Rig is not null) { a.Rig.Update(WeaponSlot.Ranged, s.Melee && s.Firing, !s.Downed, throttled ? 0f : delta, 0.6f); }
            viewer.Visible = true;
        }
        Shoot(a, s, now);

        // The name and the downed marker, by distance and side.
        a.Name.Text = s.Name;
        a.Dist = distance;
        a.Name.Visible = !s.Downed && WarfrontSquadModel.NameVisible(underCrosshair, _model!.RecentEvent(s.Index, now), distance);
        a.Down.Visible = s.Downed;   // always: the Marine to revive is found from afar
        if (a.Down.Visible) a.Down.Text = s.BeingRevived ? $"{s.Name}  REVIVING" : $"{s.Name}  DOWN {MathF.Ceiling(s.DownedLeft)}";
        a.Down.Position = new Vector3(0, 0, 1.5f);
        a.Name.Position = new Vector3(0, 0, 2.45f);
        if (a.Placeholder is not null) a.Placeholder.Visible = a.Viewer is null;
    }

    /// <summary>The Marine under the crosshair within <see cref="WarfrontSquadModel.CrosshairNameMetres"/>: nearest to the camera's line of sight (within 0.6 m of it); -1 when none.</summary>
    int Crosshair(WarfrontSquadModel model, double now)
    {
        if (Camera is null) return -1;
        var origin = ToLocal(Camera.GlobalPosition);
        var dir = (GlobalTransform.Basis.Inverse() * (-Camera.GlobalBasis.Z)).Normalized();
        int best = -1; float bestAlong = float.MaxValue;
        foreach (int index in model.Indices)
        {
            if (!model.TrySample(index, now, out var s) || s.Downed) continue;
            var chest = new Vector3(s.Position.X, s.Position.Y, s.Position.Z + 1.3f) - origin;
            float along = chest.Dot(dir);
            if (along <= 0f || along > WarfrontSquadModel.CrosshairNameMetres) continue;
            if ((chest - dir * along).Length() > 0.6f || along >= bestAlong) continue;
            best = index; bestAlong = along;
        }
        return best;
    }

    /// <summary>Labels that would overlap on the screen are raised one above the other (the nearest keeps its place), never drawn through each other.</summary>
    void StackLabels()
    {
        _tags.Clear();
        foreach (var a in _actors.Values)
        {
            if (a.Name.Visible) _tags.Add((a.Name, 2.45f, a.Dist));
            if (a.Down.Visible) _tags.Add((a.Down, 1.5f, a.Dist));
        }
        if (Camera is null) return;
        var placed = new List<Rect2>();
        foreach (var (label, baseHeight, _) in _tags.OrderBy(t => t.Dist))
        {
            label.Position = new Vector3(0, 0, baseHeight);
            var at = Camera.UnprojectPosition(label.GlobalPosition);
            float pxPerMetre = MathF.Max(1f, MathF.Abs(Camera.UnprojectPosition(label.GlobalPosition + Vector3.Up).Y - at.Y));
            float w = 9f * label.Text.Length + 12f, h = 26f;
            var rect = new Rect2(at.X - w / 2f, at.Y - h / 2f, w, h);
            for (int guard = 0; guard < 12 && placed.Any(q => q.Intersects(rect)); guard++)
            {
                rect.Position -= new Vector2(0, h);
                label.Position += new Vector3(0, 0, h / pxPerMetre);
            }
            placed.Add(rect);
        }
    }

    void AttachViewer(Actor a, MarineAssets model, WarfrontMarineDress dress)
    {
        if (a.Viewer is not null) { a.Viewer.SetLook(model); a.DressKey = dress.Key; a.RigKey = null; a.Rig = null; return; }
        var clips = ClipsOf(model, dress.Key);
        a.Viewer = new CharacterViewer(model, clips.Idle ?? CharacterViewer.DefaultAnimation, gameSpaceRoot: false) { Name = "Marine" };
        a.Root.AddChild(a.Viewer);
        a.DressKey = dress.Key;
        a.Placeholder?.QueueFree(); a.Placeholder = null;
    }

    void EnsureRig(Actor a, CharacterViewer viewer, WarfrontMarineDress dress)
    {
        string key = $"{dress.Primary}|{dress.Pistol}|{dress.Melee}|{dress.Pack}";
        if (a.RigKey == key || viewer.Skeleton is null) return;
        a.RigKey = key;
        try
        {
            var names = new[] { dress.Primary, dress.Pistol, dress.Melee, dress.Pack }.Where(n => !string.IsNullOrEmpty(n)).Cast<string>().ToArray();
            string setKey = string.Join(",", names.OrderBy(n => n));
            if (!_weapons.TryGetValue(setKey, out var assets)) _weapons[setKey] = assets = WeaponAssets.Load(_gameDir, viewer.Assets!, names, Notes);
            a.Rig = new WeaponRig(viewer.Skeleton, assets);
            a.Rig.Configure((0, 1, 1, dress.Melee, dress.Pack), dress.Primary, dress.Pistol, dress.Melee, dress.Pack is null ? [] : [dress.Pack], alwaysMelee: dress.Pack is not null);
            viewer.Weapons = a.Rig;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { Notes.Add("squad weapons: " + ex.Message); a.Rig = null; }
    }

    void Pose(Actor a, CharacterViewer viewer, WarfrontSquadClips clips, in WarfrontSquadShown s, double now)
    {
        bool gettingUp = false;
        if (s.Downed) { if (!a.WasDowned) a.DownedAt = now; a.WasDowned = true; }
        else if (a.WasDowned) { a.WasDowned = false; a.GetUpUntil = now + GetUpSeconds; }
        gettingUp = !s.Downed && now < a.GetUpUntil;
        string? clip; bool hold = false; float holdTime = 0f;
        if (s.Downed) { bool entering = clips.DownEnter is not null && now - a.DownedAt < DownEnterSeconds; clip = entering ? clips.DownEnter : clips.Down; hold = entering; holdTime = (float)(now - a.DownedAt); }
        else if (gettingUp) { clip = clips.GetUp; hold = true; holdTime = (float)(GetUpSeconds - (a.GetUpUntil - now)); }
        else if (s.Airborne && clips.Air is not null) { clip = clips.Air; hold = true; holdTime = 0.3f; }
        else if (s.Firing && s.Melee && clips.Melee is not null) clip = clips.Melee;
        else if (s.Firing && (clips.Aim ?? clips.Fire) is { } aim && s.Speed < 1f) clip = aim;
        else if (s.Speed > 0.8f) clip = clips.Run;
        else clip = clips.Idle;
        clip ??= clips.Idle ?? CharacterViewer.DefaultAnimation;
        if (hold) viewer.Pose(clip, holdTime, 0.15f); else viewer.Play(clip, 0.2f);
        a.Clip = clip;
        // Without a downed clip the body is laid down: the character tipped over (a stand-in, said in the self-test).
        bool lying = s.Downed && clips.Down is null;
        viewer.Transform = lying ? new Transform3D(new Basis(new Vector3(1, 0, 0), -Mathf.Pi / 2f), new Vector3(0, 0, 0.3f)) : Transform3D.Identity;
    }

    void Shoot(Actor a, in WarfrontSquadShown s, double now)
    {
        if (!s.Firing || s.Downed || s.Melee || s.Target < 0 || now < a.NextShot) return;
        a.NextShot = now + ShotEvery;
        if (EnemyAt?.Invoke(s.Target) is not { } to) return;
        var from = new Num.Vector3(s.Position.X, s.Position.Y, s.Position.Z + 1.45f) + new Num.Vector3(-MathF.Sin(s.Yaw - MathF.PI / 2f), MathF.Cos(s.Yaw - MathF.PI / 2f), 0f) * 0.7f;
        Shot?.Invoke(from, to, s.Class);
        ShotsFired++;
    }
}

/// <summary>
/// The squad's whole client side as one node (slice W10): the model, the crowd, the HUD and the panel, fed with the squad's envelope sections (<see cref="Section"/>) and a frame call (<see cref="Frame"/>). The networked
/// view (<c>NetworkView.WarfrontSquad.cs</c>) and the self-test (a demo feed) both drive it, so what the demo shows is what a server's data would show. Keys (ours, free ones): <see cref="PanelKey"/> opens the squad
/// panel aboard the ship, <see cref="JumpKey"/> sends the jump-pound mark for an Assault or Raptor in a battle.
/// </summary>
public sealed partial class WarfrontSquadView : Node
{
    public const Key PanelKey = Key.F4, JumpKey = Key.J;

    public WarfrontSquadModel Model { get; } = new();
    public WarfrontSquadCrowd Crowd { get; }
    public WarfrontSquadHud Hud { get; }
    public WarfrontSquadPanel Panel { get; }

    /// <summary>A command payload for tag <see cref="SmNet.Extras.Warfront.SquadWire.CommandTag"/> (the networked view sends it as an extras command).</summary>
    public Action<byte[]>? SendCommand { get; set; }
    public Func<int, string?> PlayerName { get; set; } = _ => null;
    /// <summary>Whether the own class has a jump pack (Assault or Raptor): the hint and the key show for it only.</summary>
    public Func<bool> OwnHasJumpPack { get; set; } = () => false;
    /// <summary>The point the fireteam mark uses (the crosshair's point on the ground), game space; null when there is none.</summary>
    public Func<Num.Vector3?> MarkPoint { get; set; } = () => null;
    /// <summary>A jump-pound landed at a point (dust and sound).</summary>
    public Action<Num.Vector3>? Pound { get; set; }
    /// <summary>Whether the player is aboard the ship (the panel can open there only).</summary>
    public bool Aboard { get; set; }
    public string UseKeyLabel { get; set; } = "E";

    public int SectionsRead { get; private set; }
    public int EventLines { get; private set; }
    public int CommandsSent { get; private set; }

    public WarfrontSquadView(WarfrontSquadLooks looks, string gameDir, Func<byte, string> className)
    {
        Name = "WarfrontSquadView";
        Crowd = new WarfrontSquadCrowd(looks, gameDir);
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

    /// <summary>One envelope section of the squad; true when it was one of tags 32 to 34 (a malformed one is counted and ignored).</summary>
    public bool Section(byte tag, ReadOnlySpan<byte> payload, double now)
    {
        switch (tag)
        {
            case SmNet.Extras.Warfront.SquadWire.StateTag:
                if (SmNet.Extras.Warfront.SquadWire.TryDecodeState(payload, out var state)) Model.ApplyState(state, now); else Model.CountMalformed();
                break;
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

    /// <summary>The frame: prune, draw the crowd, the HUD line, the jump hint, the revive prompt and ring, the feed, the open panel.</summary>
    public void Frame(double now, double delta, Camera3D? camera, Node3D? gameSpace, Num.Vector3 player)
    {
        Model.Prune(now);
        Crowd.Camera = camera;
        var cameraGame = camera is not null && gameSpace is not null ? gameSpace.ToLocal(camera.GlobalPosition) : new Vector3(player.X, player.Y, player.Z + 2f);
        Crowd.Frame(Model, now, new Num.Vector3(cameraGame.X, cameraGame.Y, cameraGame.Z), delta);
        var (standing, roster, downed) = Model.OwnCounts(now);
        string? line = Model.HudLine(now);
        Hud.SetLine(line, alert: downed > 0);
        Hud.SetJumpHint(line is not null && OwnHasJumpPack() ? $"[{JumpKey}] JUMP ASSAULT" : null);
        var revive = Aboard ? null : Model.ReviveCandidate(player, now);
        if (revive is { } r)
        {
            Vector2? ring = null;
            if (camera is not null && gameSpace is not null && Crowd.HeadOf(r.Index) is { } head)
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
        Crowd.AddChild(node);
        var tween = node.CreateTween();
        tween.SetParallel();
        tween.TweenProperty(node, "scale", new Vector3(4f, 4f, 2.5f), 0.55);
        tween.TweenProperty(material, "albedo_color:a", 0f, 0.55);
        tween.Chain().TweenCallback(Callable.From(node.QueueFree));
    }

    /// <summary>The Librarian's stored squad class, after joining and on change (<c>SquadClass</c> 0 Assault, 1 Tactical, 2 Devastator).</summary>
    public void SendSquadClass(byte option) => Send(SmNet.Extras.Warfront.SquadWire.EncodeSquadClass(option));
}
