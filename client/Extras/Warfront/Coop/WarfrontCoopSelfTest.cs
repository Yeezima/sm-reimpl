using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmClient.Customizer;
using SmNet.Extras.Warfront;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Coop;

namespace SmClient.Extras.Warfront.Coop;

/// <summary>
/// Self-test of the co-op UI (slice W12): drives <see cref="WarfrontCoopModel"/>, <see cref="WarfrontSpectateHud"/> and <see cref="WarfrontJoinPanel"/> through every state with real wire sections (encoded and decoded),
/// no server. The backdrop is a plain placeholder, not a scene. Headless: <c>godot.cmd res://Extras/Warfront/Coop/WarfrontCoopSelfTest.tscn -- --wfcoop-selftest</c>; add <c>--wfcoop-shots &lt;dir&gt;</c> and
/// <c>--wfcoop-size 1600x900</c> in a windowed run (<c>godot.cmd --windowed ...</c>) to save a still of each state. Prints SELFTEST PASS or FAIL per check and WFCOOP SELFTEST RESULT; quits non-zero on a failure.
/// </summary>
public sealed partial class WarfrontCoopSelfTest : Node
{
    int _passed, _failed, _shot;
    string? _shotDir;
    WarfrontCoopModel _model = null!;
    WarfrontSpectateHud _hud = null!;
    WarfrontJoinPanel _join = null!;

    public override void _Ready() => _ = RunAsync();

    async Task Frames(int n = 3) { for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }

    void Check(string name, bool ok, string detail = "")
    {
        if (ok) _passed++; else _failed++;
        GD.Print($"SELFTEST {(ok ? "PASS" : "FAIL")} {name}{(!ok && detail.Length > 0 ? " - " + detail : "")}");
    }

    async Task Still(string name)
    {
        await Frames(4);
        if (_shotDir is null) return;
        var img = GetViewport().GetTexture()?.GetImage();
        if (img is null || img.IsEmpty()) { GD.Print("SELFTEST NOTE no image for " + name); return; }
        string path = System.IO.Path.Combine(_shotDir, $"{++_shot:00}-{name}.png");
        img.SavePng(path);
        GD.Print("SELFTEST SHOT " + path);
    }

    /// <summary>A section as the client receives it: encoded and decoded.</summary>
    static WarfrontCoopWire.Section Wire(WarfrontCoopRole role, byte flags = 0, byte gate = 0, WarfrontWatchKind kind = WarfrontWatchKind.None, byte id = 0, params WarfrontAwaited[] awaited)
    {
        var bytes = WarfrontCoopWire.Encode(new WarfrontCoopWire.Section(role, kind, id, flags, gate, awaited));
        if (!WarfrontCoopWire.TryDecode(bytes, out var s)) throw new InvalidOperationException("a section did not round-trip");
        return s;
    }

    async Task RunAsync()
    {
        var args = OS.GetCmdlineUserArgs();
        int at = Array.IndexOf(args, "--wfcoop-shots");
        if (at >= 0 && at + 1 < args.Length) { _shotDir = args[at + 1]; System.IO.Directory.CreateDirectory(_shotDir); }
        UiKit.EnterVirtualSpace(GetWindow());
        int sizeAt = Array.IndexOf(args, "--wfcoop-size");
        if (sizeAt >= 0 && sizeAt + 1 < args.Length)
        {
            var wh = args[sizeAt + 1].Split('x');
            GetWindow().Size = new Vector2I(int.Parse(wh[0]), int.Parse(wh[1]));
            await Frames(10);
            GD.Print($"SELFTEST SIZE window {GetWindow().Size} viewport {GetViewport().GetVisibleRect().Size}");
        }
        var root = new Control { Name = "Root", Theme = UiKit.Theme };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);
        var back = new ColorRect { Name = "Backdrop", Color = new Color(0.1f, 0.12f, 0.15f), MouseFilter = Control.MouseFilterEnum.Ignore };
        back.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(back);
        _model = new WarfrontCoopModel();
        _hud = new WarfrontSpectateHud(_model);
        _join = new WarfrontJoinPanel(_model);
        root.AddChild(_hud);
        root.AddChild(_join);
        var confirms = new List<int>();
        var watches = new List<int>();
        _model.ConfirmRequested += () => confirms.Add(1);
        _model.WatchRequested += watches.Add;
        await Frames(5);

        // 1. A player: nothing shows.
        _model.Apply(Wire(WarfrontCoopRole.Player), "ASH", "", 1);
        await Frames(2);
        Check("player: no spectator HUD", !_hud.Visible);
        Check("player: no join panel", !_join.Visible);

        // 2. A joiner held aboard in a battle, watching the leader.
        _model.Apply(Wire(WarfrontCoopRole.SpectatingJoiner, kind: WarfrontWatchKind.Player, id: 1), "Ash", "Ash", 1);
        await Frames(2);
        Check("joiner: HUD shows", _hud.Visible && _hud.WatchingLabel.Visible);
        Check("joiner: WATCHING NAME", _hud.WatchingLabel.Text == "WATCHING ASH", _hud.WatchingLabel.Text);
        Check("joiner: the stop line", _hud.SubLabel.Text == "YOU JOIN AT THE NEXT THUNDERHAWK STOP", _hud.SubLabel.Text);
        Check("joiner: no join panel yet", !_join.Visible);
        Check("joiner: the bar shows the watch keys", _hud.KeysLabel.Text == "F: NEXT VIEW    SHIFT+F: PREVIOUS VIEW", _hud.KeysLabel.Text);
        FitCheck("joiner", _hud.WatchingLabel, _hud.SubLabel, _hud.KeysLabel, _hud.AnnounceLabel);
        await Still("joiner-spectating");
        _model.StepWatch(1); _model.StepWatch(-1);
        Check("joiner: the spectate keys send Watch -1 / +1", watches.SequenceEqual(new[] { 1, -1 }), string.Join(",", watches));

        // 3. A fallen player past the window: the same camera, the countdown.
        _model.Apply(Wire(WarfrontCoopRole.Player, kind: WarfrontWatchKind.Marine, id: 4), "Ash", "Brann's Marine Cato", 3);
        _model.ApplyDown(WarfrontDownPhase.Spectator, 41.2f);
        await Frames(2);
        Check("fallen: WATCHING a Marine", _hud.WatchingLabel.Text == "WATCHING BRANN'S MARINE CATO", _hud.WatchingLabel.Text);
        Check("fallen: the countdown", _hud.SubLabel.Text == "BACK IN THE FIGHT IN 0:42", _hud.SubLabel.Text);
        FitCheck("fallen", _hud.WatchingLabel, _hud.SubLabel, _hud.KeysLabel, _hud.AnnounceLabel);
        await Still("fallen-spectating");
        _model.ApplyDown(WarfrontDownPhase.None, 0f);
        await Frames(2);
        Check("fallen: the respawn ends the HUD", !_hud.Visible);

        // 4. Awaiting the class (own panel).
        _model.Apply(Wire(WarfrontCoopRole.AwaitClass, gate: 72), "Ash", "", 6);
        await Frames(2);
        Check("class: own panel shows", _join.Visible && _join.HeadingLabel.Text == "YOU JOIN AT LEVEL 6", _join.HeadingLabel.Text);
        Check("class: the customizer line and the clock", _join.BodyLabel.Text == "CONFIRM YOUR CLASS IN THE CUSTOMIZER (1:12)", _join.BodyLabel.Text);
        Check("class: the way in is the own box's hover hint, not text on the screen", _join.HintText == "OPEN IT AT THE EQUIPMENT STATION ON THE SHIP" && !_join.Labels().Any(l => l.Text.Contains("EQUIPMENT", StringComparison.Ordinal)), _join.HintText);
        FitCheck("class", _join.Labels().ToArray());
        Check("class: text is at the picker's sizes", _join.HeadingLabel.GetThemeFontSize("font_size") == Progression.WarfrontPickerPanel.HeadingSize && _join.BodyLabel.GetThemeFontSize("font_size") == Progression.WarfrontPickerPanel.BodySize);
        await Still("join-class");
        _model.CustomizerClosed();
        Check("class: closing without opening confirms nothing", confirms.Count == 0);
        _model.CustomizerOpened();
        _model.CustomizerClosed();
        Check("class: open then close sends ConfirmClass once", confirms.Count == 1);
        _model.CustomizerClosed();
        Check("class: a second close sends nothing", confirms.Count == 1);

        // 5. Awaiting the cards.
        var own = new WarfrontAwaited("Cato", WarfrontAwaitReason.Picks, 3);
        var sec = Wire(WarfrontCoopRole.AwaitPicks, awaited: own);
        _model.Apply(sec, "Ash", "", 6, WarfrontCoopModel.PicksLeftFor(sec, "Cato"));
        await Frames(2);
        Check("picks: heading and cards left", _join.HeadingLabel.Text == "CHOOSE YOUR CARDS" && _join.BodyLabel.Text == "3 LEFT", _join.HeadingLabel.Text + " / " + _join.BodyLabel.Text);
        Check("picks: a non-leader sees no awaited list", !_model.ShowAwaited);
        await Still("join-picks");

        // 6. The leader's awaited list (and the announcement).
        var flags = (byte)(WarfrontCoopWire.FlagYouLead | WarfrontCoopWire.FlagLeaderChanged);
        _model.Apply(Wire(WarfrontCoopRole.Player, flags, 0, awaited: new[] { new WarfrontAwaited("Cato", WarfrontAwaitReason.Class, 3), new WarfrontAwaited("Dorn", WarfrontAwaitReason.Picks, 1) }), "Ash", "", 4);
        await Frames(2);
        Check("leader: the list shows", _join.Visible && _join.ListHeadingLabel.Text == "WAITING FOR");
        Check("leader: one row per awaited player, the reasons in one column", _join.RowLabel(0).Text == "CATO" && _join.ReasonLabel(0).Text == "CLASS" && _join.RowLabel(1).Text == "DORN" && _join.ReasonLabel(1).Text == "1 CARD" && _join.RowLabel(2).Text == "", _join.RowLabel(0).Text + "|" + _join.ReasonLabel(1).Text);
        Check("leader: the box is sized to its two rows", _join.ListBox.Size.Y == MathF.Round(WarfrontJoinPanel.ListRowsTop + 2 * WarfrontJoinPanel.ListRowPitch + WarfrontJoinPanel.ListBottomPad - 6f), _join.ListBox.ToString());
        Check("leader: the reasons share one x", _join.ReasonLabel(0).Position.X == _join.ReasonLabel(1).Position.X);
        Check("leader: no own panel", _join.HeadingLabel.Text == "");
        Check("leader: the announcement line", _hud.AnnounceLabel.Text == "YOU LEAD THE SQUAD" && _hud.Visible, _hud.AnnounceLabel.Text);
        FitCheck("leader", _join.Labels().Concat(new[] { _hud.AnnounceLabel }).ToArray());
        await Still("leader-awaited");
        _model.Tick(WarfrontCoop.LeaderChangedSeconds + 0.5);
        await Frames(2);
        Check("leader: the announcement goes after a few seconds", _hud.AnnounceLabel.Text == "");

        // 7. Someone else leads now.
        _model.Apply(Wire(WarfrontCoopRole.Player), "Brann", "", 4);
        _model.Apply(Wire(WarfrontCoopRole.Player, WarfrontCoopWire.FlagLeaderChanged), "Brann", "", 4);
        Check("handoff: NAME now leads", _model.AnnouncementText == "BRANN NOW LEADS THE SQUAD", _model.AnnouncementText);

        // 8. The frame is equal on all four sides: two boxes, 2 px, whole pixels.
        Check("frames: the boxes sit on whole pixels", Whole(_join.OwnBox) && Whole(_join.ListBox), _join.OwnBox + " " + _join.ListBox);
        Check("frames: the boxes are on screen", OnScreen(_join.OwnBox) && OnScreen(_join.ListBox));
        Check("model: the spectate keys do nothing when not spectating", Spectates(0));

        GD.Print($"WFCOOP SELFTEST RESULT {(_failed == 0 ? "PASS" : "FAIL")} ({_passed} passed, {_failed} failed)");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    bool Spectates(int expected)
    {
        int before = 0;
        _model.WatchRequested += _ => before++;
        _model.StepWatch(1);
        return before == expected;
    }

    static bool Whole(Rect2 r) => r.Position.X == MathF.Round(r.Position.X) && r.Position.Y == MathF.Round(r.Position.Y) && r.Size.X == MathF.Round(r.Size.X) && r.Size.Y == MathF.Round(r.Size.Y);
    static bool OnScreen(Rect2 r) => r.Position.X >= 0 && r.Position.Y >= 0 && r.End.X <= UiKit.VirtualWidth && r.End.Y <= UiKit.VirtualHeight;

    /// <summary>Every non-empty label fits its box and the screen, and no two overlap.</summary>
    void FitCheck(string state, params Label[] labels)
    {
        var rects = new List<(string, Rect2)>();
        foreach (var l in labels)
        {
            if (l.Text.Length == 0) continue;
            var min = l.GetCombinedMinimumSize();
            var r = new Rect2(l.Position, new Vector2(min.X, Mathf.Min(min.Y, l.Size.Y)));
            Check($"{state}: label {l.Name} fits its width", min.X <= l.Size.X + 0.5f, $"{min} in {l.Size} \"{l.Text}\"");
            Check($"{state}: label {l.Name} is on screen", OnScreen(new Rect2(l.Position, l.Size)), l.Position + " " + l.Size);
            rects.Add((l.Name, new Rect2(l.Position, new Vector2(l.Size.X, min.Y))));
        }
        int overlaps = 0;
        string first = "";
        for (int i = 0; i < rects.Count; i++)
            for (int j = i + 1; j < rects.Count; j++)
                if (rects[i].Item2.Intersects(rects[j].Item2)) { overlaps++; if (first.Length == 0) first = rects[i].Item1 + " x " + rects[j].Item1; }
        Check($"{state}: no two texts overlap", overlaps == 0, first);
    }
}
