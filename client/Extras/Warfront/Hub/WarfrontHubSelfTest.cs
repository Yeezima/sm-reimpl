using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmClient.Customizer;
using SmSim.Extras.Warfront.Campaign;

namespace SmClient.Extras.Warfront.Hub;

/// <summary>
/// Self-test of the Warfront hub UI (W6): runs the war map and the travel screen through every state the plan lists, fed by real <see cref="WarfrontCampaign"/> runs (forced rolls) through
/// <see cref="WarfrontHubView.FromCampaign"/>, no server. Headless: <c>godot.cmd res://Extras/Warfront/Hub/WarfrontHubSelfTest.tscn -- --wfhub-selftest</c>. Add <c>--wfhub-shots &lt;dir&gt;</c> in a windowed
/// run (<c>godot.cmd --windowed ...</c>) to save a still of each state. Prints one SELFTEST PASS or FAIL per check and WFHUB SELFTEST RESULT, quits non-zero on a failure.
/// </summary>
public sealed partial class WarfrontHubSelfTest : Node
{
    const ulong Ash = 11, Brann = 22, Cato = 33;
    int _passed, _failed, _shot;
    string? _shotDir;
    WarfrontHubModel _model = null!;
    WarfrontHubPanel _panel = null!;
    WarfrontTravelPanel _travel = null!;

    public override void _Ready() => _ = RunAsync();

    async Task Frames(int n = 3) { for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }

    void Check(string name, bool ok, string detail = "")
    {
        if (ok) _passed++; else _failed++;
        GD.Print($"SELFTEST {(ok ? "PASS" : "FAIL")} {name}{(!ok && detail.Length > 0 ? " - " + detail : "")}");
    }

    static WarfrontCampaign Run(params bool[] rolls)
    {
        var c = new WarfrontCampaign(7, new ForcedRolls(rolls));
        c.Join(Ash, "Captain Ash", out _);
        c.Join(Brann, "Brann", out _);
        c.Join(Cato, "", out _);
        c.Embark();
        return c;
    }

    /// <summary>One deployment: the leader deploys, the battle runs, it ends as <paramref name="outcome"/> (forced rolls, in the order of the other lanes).</summary>
    static void Deployment(WarfrontCampaign c, WarfrontTarget target, WarfrontOutcome outcome)
    {
        if (!c.TryDeploy(c.Leader!.Value, target, out var err)) throw new InvalidOperationException("deploy refused: " + err);
        c.BeginBattle();
        c.Resolve(outcome);
    }

    void Show(WarfrontCampaign c, ulong viewer)
    {
        _model.Apply(WarfrontHubView.FromCampaign(c, viewer));
        _travel.Visible = _model.IsTravelling;
        _panel.Visible = !_model.IsTravelling;
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

    async Task RunAsync()
    {
        var args = OS.GetCmdlineUserArgs();
        int at = Array.IndexOf(args, "--wfhub-shots");
        if (at >= 0 && at + 1 < args.Length) { _shotDir = args[at + 1]; System.IO.Directory.CreateDirectory(_shotDir); }
        UiKit.EnterVirtualSpace(GetWindow());
        var root = new Control { Name = "Root", Theme = UiKit.Theme };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);
        _model = new WarfrontHubModel();
        _panel = new WarfrontHubPanel(_model, () => { });
        _travel = new WarfrontTravelPanel(_model) { Visible = false };
        root.AddChild(_panel);
        root.AddChild(_travel);
        var requested = new List<WarfrontTarget>();
        _model.DeployRequested += requested.Add;
        await Frames(5);
        int changes = 0;
        _model.Changed += () => changes++;

        // 1. Start: three lanes at node 1 (the floor holds), the leader sees every lane and the HQ.
        var c = Run();
        Show(c, Ash);
        await Still("start-leader");
        for (int l = 0; l < 3; l++)
        {
            Check($"start: lane {l} front is node 1 and marked", _panel.NodeLabelOf(l, 1).Text.EndsWith("FRONT") && _panel.NodeLabelOf(l, 2).Text.EndsWith("ORK-HELD") && _panel.NodeLabelOf(l, 3).Text.EndsWith("ORK-HELD"), _panel.NodeLabelOf(l, 1).Text);
            Check($"start: lane {l} shows the floor", _panel.FloorLabel(l).Text.Contains("FLOOR"));
            Check($"start: lane {l} has no roll yet", _panel.LastLabel(l).Text.Contains("NONE YET"));
        }
        Check("start: three node names per lane", _panel.NodeLabelOf(0, 1).Text.Contains("WASTELAND") && _panel.NodeLabelOf(0, 2).Text.Contains("OUTSKIRTS") && _panel.NodeLabelOf(0, 3).Text.Contains("URBAN CORE"));
        Check("start: Imperial HQ secure, fortress locked", _panel.HqLabel.Text == "IMPERIAL HQ\nSECURE" && _panel.FortressLabel.Text == "ORK FORTRESS\nLOCKED", _panel.HqLabel.Text + " / " + _panel.FortressLabel.Text);
        Check("start: header names the deployment", _panel.HeaderLabel.Text.Contains("DEPLOYMENT 1"));
        Check("start: the leader's three lane rows are open", Enumerable.Range(0, 3).All(r => !_panel.DeployRow(r).Disabled));
        Check("start: the HQ row is not offered (not mandatory)", _panel.DeployRow(3).Disabled);
        Check("start: the party lists the leader and the viewer", _panel.PartyLabel(0).Text == "CAPTAIN ASH    LEADER    YOU" && _panel.PartyLabel(1).Text == "BRANN" && _panel.PartyLabel(2).Text == "PLAYER 3", _panel.PartyLabel(0).Text + "|" + _panel.PartyLabel(1).Text + "|" + _panel.PartyLabel(2).Text);
        Check("start: the note says you lead", _panel.NoteLabel.Text.StartsWith("YOU LEAD"));
        Check("start: the first open row has the focus", await FocusIs(_panel.DeployRow(0)));
        requested.Clear();
        _panel.DeployRow(1).EmitSignal(BaseButton.SignalName.Pressed);
        Check("start: pressing lane B asks for node B", requested.Count == 1 && requested[0] == WarfrontTarget.Node(1), string.Join(",", requested));
        FitCheck("start");

        // 2. A non-leader: the same map, nothing pressable, who is choosing.
        Show(c, Brann);
        await Still("start-other-player");
        Check("other player: no row can be pressed", Enumerable.Range(0, 4).All(r => _panel.DeployRow(r).Disabled));
        Check("other player: sees who is choosing", _panel.NoteLabel.Text == "CAPTAIN ASH IS CHOOSING THE DEPLOYMENT", _panel.NoteLabel.Text);
        requested.Clear();
        Check("other player: the model refuses a deploy", !_model.RequestDeploy(WarfrontTarget.Node(0)) && requested.Count == 0);
        Check("other player: marked YOU on their own line", _panel.PartyLabel(1).Text == "BRANN    YOU", _panel.PartyLabel(1).Text);

        // 3. Rolls: a win on lane A, lane B's roll lost at the floor, lane C's roll won.
        c = Run(true, false);
        Deployment(c, WarfrontTarget.Node(0), WarfrontOutcome.Win);
        Show(c, Ash);
        await Still("win-rolls-floor");
        Check("rolls: lane A advanced to node 2", _panel.NodeLabelOf(0, 1).Text.EndsWith("CLEARED") && _panel.NodeLabelOf(0, 2).Text.EndsWith("FRONT"), _panel.NodeLabelOf(0, 2).Text);
        Check("rolls: lane A reports the win", _panel.LastLabel(0).Text == "LAST DEPLOYMENT: WON", _panel.LastLabel(0).Text);
        Check("rolls: lane B lost its roll but held at the floor", _panel.LastLabel(1).Text == "LAST ROLL: LOST, HELD AT FLOOR" && _panel.FloorLabel(1).Text.Contains("FLOOR"), _panel.LastLabel(1).Text);
        Check("rolls: lane C won its roll", _panel.LastLabel(2).Text == "LAST ROLL: WON, LANE HELD", _panel.LastLabel(2).Text);
        Check("rolls: the advanced lane no longer shows the floor", _panel.FloorLabel(0).Text == "");
        Check("rolls: the header counts the next deployment", _panel.HeaderLabel.Text.Contains("DEPLOYMENT 2"));
        Check("rolls: the lane A row names node 2", _panel.DeployRow(0).Text.Contains("NODE 2, OUTSKIRTS"), _panel.DeployRow(0).Text);
        FitCheck("rolls");

        // 4. A real drop: lane B wins, lane A's roll is lost (2 to 1), lane C's is lost at the floor.
        c = Run(false, false, true, true);
        Deployment(c, WarfrontTarget.Node(0), WarfrontOutcome.Win);
        Deployment(c, WarfrontTarget.Node(1), WarfrontOutcome.Win);
        Show(c, Ash);
        await Still("roll-lost-one-node");
        Check("drop: lane A lost a node to a roll", _panel.LastLabel(0).Text == "LAST ROLL: LOST, ONE NODE BACK" && _panel.NodeLabelOf(0, 1).Text.EndsWith("FRONT"), _panel.LastLabel(0).Text);
        Check("drop: lane B reports its win", _panel.LastLabel(1).Text == "LAST DEPLOYMENT: WON");
        Check("drop: lane C held at the floor", _panel.LastLabel(2).Text == "LAST ROLL: LOST, HELD AT FLOOR");

        // 5. A wipe at the floor on the played lane.
        c = Run(false, false);
        Deployment(c, WarfrontTarget.Node(2), WarfrontOutcome.Wipe);
        Show(c, Ash);
        await Still("wipe-held-at-floor");
        Check("wipe at the floor: lane C stays at node 1 and says held", _panel.LastLabel(2).Text == "LAST DEPLOYMENT: WIPED, HELD AT FLOOR" && _panel.NodeLabelOf(2, 1).Text.EndsWith("FRONT"), _panel.LastLabel(2).Text);
        Check("wipe at the floor: the other lanes show their rolls", _panel.LastLabel(0).Text.StartsWith("LAST ROLL: WON") && _panel.LastLabel(1).Text.StartsWith("LAST ROLL: WON"));

        // 6. A cleared lane: the fortress opens, through that lane.
        c = Run(false, false, false, false, false, false);
        for (int i = 0; i < 3; i++) Deployment(c, WarfrontTarget.Node(0), WarfrontOutcome.Win);
        Show(c, Ash);
        await Still("lane-cleared-fortress-open");
        Check("cleared: lane A reads cleared", _panel.StatusLabel(0).Text == "CLEARED, THE FORTRESS IS OPEN" && Enumerable.Range(1, 3).All(n => _panel.NodeLabelOf(0, n).Text.EndsWith("CLEARED")), _panel.StatusLabel(0).Text);
        Check("cleared: the fortress is unlocked", _panel.FortressLabel.Text == "ORK FORTRESS\nUNLOCKED");
        Check("cleared: lane A's row deploys to the fortress", _panel.DeployRow(0).Text == "DEPLOY: ORK FORTRESS, THROUGH LANE A" && !_panel.DeployRow(0).Disabled, _panel.DeployRow(0).Text);
        requested.Clear();
        _panel.DeployRow(0).EmitSignal(BaseButton.SignalName.Pressed);
        Check("cleared: pressing it asks for the fortress through lane A", requested.Count == 1 && requested[0] == WarfrontTarget.Fortress(0), string.Join(",", requested));
        FitCheck("cleared");

        // 7. HQ defense mandatory: a lane that advanced and then broke through.
        c = Run(false, false, false, false, false, false);
        Deployment(c, WarfrontTarget.Node(0), WarfrontOutcome.Win);
        Deployment(c, WarfrontTarget.Node(0), WarfrontOutcome.Wipe);
        Deployment(c, WarfrontTarget.Node(0), WarfrontOutcome.Wipe);
        Show(c, Ash);
        await Still("hq-mandatory-leader");
        Check("mandatory: the HQ is marked", _panel.HqLabel.Text == "IMPERIAL HQ\nMUST BE HELD", _panel.HqLabel.Text);
        Check("mandatory: lane A reads broken through", _panel.StatusLabel(0).Text == "BROKEN THROUGH TO THE HQ" && _panel.DeployRow(0).Text == "LANE A: BROKEN THROUGH", _panel.StatusLabel(0).Text);
        Check("mandatory: the HQ row is the only open row", !_panel.DeployRow(3).Disabled && Enumerable.Range(0, 3).All(r => _panel.DeployRow(r).Disabled));
        Check("mandatory: the note says so", _panel.NoteLabel.Text.StartsWith("HQ DEFENSE IS MANDATORY"), _panel.NoteLabel.Text);
        Check("mandatory: the focus is on the HQ row", await FocusIs(_panel.DeployRow(3)));
        requested.Clear();
        _model.RequestDeploy(WarfrontTarget.Node(1));
        _panel.DeployRow(1).EmitSignal(BaseButton.SignalName.Pressed);
        _panel.DeployRow(3).EmitSignal(BaseButton.SignalName.Pressed);
        Check("mandatory: only the HQ defense can be asked for", requested.Count == 1 && requested[0] == WarfrontTarget.Hq, string.Join(",", requested));
        Show(c, Brann);
        await Still("hq-mandatory-other-player");
        Check("mandatory, other player: nothing pressable, sees who is choosing", Enumerable.Range(0, 4).All(r => _panel.DeployRow(r).Disabled) && _panel.NoteLabel.Text.Contains("IS CHOOSING"), _panel.NoteLabel.Text);

        // 8. The leader leaves: leadership passes, the old leader is marked away.
        c.Leave(Ash);
        Show(c, Brann);
        await Still("leader-passed");
        Check("leader change: the next player leads", _panel.PartyLabel(1).Text == "BRANN    LEADER    YOU" && _panel.PartyLabel(0).Text == "CAPTAIN ASH    AWAY", _panel.PartyLabel(0).Text + "|" + _panel.PartyLabel(1).Text);
        Check("leader change: the new leader can deploy the HQ defense", !_panel.DeployRow(3).Disabled);

        // 9. HQ defense won: all fronts to node 2, mandatory off, nothing rolled.
        c = Run(false, false, false, false, false, false);
        Deployment(c, WarfrontTarget.Node(0), WarfrontOutcome.Win);
        Deployment(c, WarfrontTarget.Node(0), WarfrontOutcome.Wipe);
        Deployment(c, WarfrontTarget.Node(0), WarfrontOutcome.Wipe);
        Deployment(c, WarfrontTarget.Hq, WarfrontOutcome.Win);
        Show(c, Ash);
        await Still("hq-won-reset");
        Check("HQ won: every lane is back at node 2 and the HQ is secure", Enumerable.Range(0, 3).All(l => _panel.NodeLabelOf(l, 2).Text.EndsWith("FRONT")) && _panel.HqLabel.Text == "IMPERIAL HQ\nSECURE");
        Check("HQ won: no roll line, and the floor is off for lane A only", _panel.LastLabel(0).Text.Contains("NONE YET") && _panel.FloorLabel(0).Text == "");

        // 10. Travel screen.
        c = Run();
        c.TryDeploy(Ash, WarfrontTarget.Node(1), out _);
        Show(c, Brann);
        _model.SetTravelProgress(1, 3);
        await Still("travel");
        Check("travel: the travel screen replaces the map", _travel.Visible && !_panel.Visible);
        Check("travel: the destination names the lane and node", _travel.DestinationLabel.Text == "TO LANE B, NODE 1, WASTELAND", _travel.DestinationLabel.Text);
        Check("travel: the status counts who arrived", _travel.StatusLabel.Text.StartsWith("LOADING THE BATTLEFIELD    1 OF 3 PLAYERS ARRIVED"), _travel.StatusLabel.Text);
        Check("travel: the dots step along", await DotsStep());
        FitCheckTravel();
        _model.SetTravelProgress(3, 3);
        await Frames(2);
        Check("travel: all arrived is shown", _travel.StatusLabel.Text.Contains("3 OF 3"));

        // 11. End of the war: won, then lost.
        c = Run(false, false, false, false, false, false);
        for (int i = 0; i < 3; i++) Deployment(c, WarfrontTarget.Node(0), WarfrontOutcome.Win);
        c.TryDeploy(Ash, WarfrontTarget.Fortress(0), out _);
        c.BeginBattle();
        c.WarbossKilled();
        Show(c, Ash);
        await Still("war-won");
        Check("won: header and note say so, nothing can be deployed", _panel.HeaderLabel.Text == "THE WAR IS WON" && _panel.NoteLabel.Text.Contains("WARBOSS") && Enumerable.Range(0, 4).All(r => _panel.DeployRow(r).Disabled));
        c = Run(false, false, false, false, false, false);
        Deployment(c, WarfrontTarget.Node(0), WarfrontOutcome.Win);
        Deployment(c, WarfrontTarget.Node(0), WarfrontOutcome.Wipe);
        Deployment(c, WarfrontTarget.Node(0), WarfrontOutcome.Wipe);
        Deployment(c, WarfrontTarget.Hq, WarfrontOutcome.Wipe);
        Show(c, Ash);
        await Still("war-lost");
        Check("lost: header and note say so", _panel.HeaderLabel.Text == "THE WAR IS LOST" && _panel.NoteLabel.Text.Contains("HQ HAS FALLEN"));

        // 12. The feed: an unchanged state raises nothing; a change raises once.
        c = Run();
        Show(c, Ash);
        int before = changes;
        Show(c, Ash);
        Check("feed: an identical view raises no change", changes == before);
        c.Leave(Cato);
        Show(c, Ash);
        Check("feed: a changed view raises one change", changes == before + 1);
        Check("model: a direct plain view (no campaign) draws", PlainViewDraws());

        GD.Print($"WFHUB SELFTEST RESULT {(_failed == 0 ? "PASS" : "FAIL")} ({_passed} passed, {_failed} failed)");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    async Task<bool> FocusIs(Button b)
    {
        _panel.Focus();
        await Frames(3);
        return b.HasFocus() || !b.IsVisibleInTree();   // a hidden desktop run may not give the window focus: the grab is what counts
    }

    async Task<bool> DotsStep()
    {
        var seen = new HashSet<int>();
        ulong start = Time.GetTicksMsec();
        while (Time.GetTicksMsec() - start < 2600 && seen.Count < 3)
        {
            seen.Add(_travel.Dots);
            await Frames(1);
        }
        return seen.Count >= 3;
    }

    /// <summary>The model takes a plain view built by hand, as W5 will.</summary>
    bool PlainViewDraws()
    {
        var v = new WarfrontHubView
        {
            Phase = WarfrontPhase.Ship, Fronts = new[] { 3, 0, 4 }, Advanced = new[] { true, true, true }, HqMandatory = true,
            Targets = new[] { WarfrontTarget.Hq }, ViewerKey = 5, LeaderKey = 5,
            Players = new[] { new HubPlayer(5, "Tester", true) },
            Last = new[] { HubLastKind.RollWon, HubLastKind.RollLost, HubLastKind.PlayedWon },
        };
        _model.Apply(v);
        return _panel.NodeLabelOf(0, 3).Text.EndsWith("FRONT") && _panel.NodeLabelOf(2, 3).Text.EndsWith("CLEARED") && _panel.StatusLabel(1).Text.Contains("BROKEN") && !_panel.DeployRow(3).Disabled;
    }

    /// <summary>Every label fits its box and the screen, and no two labels overlap.</summary>
    void FitCheck(string state)
    {
        var rects = new List<(string, Rect2)>();
        foreach (var l in _panel.Labels())
        {
            if (l.Text.Length == 0) continue;
            var min = l.GetCombinedMinimumSize();
            var r = new Rect2(l.Position, min);
            Check($"{state}: label {l.Name} fits its width", min.X <= l.Size.X + 0.5f && min.Y <= l.Size.Y + 0.5f, $"{min} in {l.Size} \"{l.Text}\"");
            Check($"{state}: label {l.Name} is on screen", r.Position.X >= 0 && r.Position.Y >= 0 && r.End.X <= UiKit.VirtualWidth && r.End.Y <= UiKit.VirtualHeight, r.ToString());
            rects.Add((l.Name, r));
        }
        foreach (var b in new[] { 0, 1, 2, 3 }.Select(i => _panel.DeployRow(i)))
        {
            var min = b.GetCombinedMinimumSize();
            Check($"{state}: row {b.Name} text fits", min.X <= b.Size.X + 0.5f, $"{min.X} in {b.Size.X} \"{b.Text}\"");
            rects.Add((b.Name, new Rect2(b.Position, new Vector2(min.X, Math.Min(min.Y, b.Size.Y)))));
        }
        int overlaps = 0;
        string first = "";
        for (int i = 0; i < rects.Count; i++)
            for (int j = i + 1; j < rects.Count; j++)
                if (rects[i].Item2.Intersects(rects[j].Item2)) { overlaps++; if (first.Length == 0) first = rects[i].Item1 + " x " + rects[j].Item1; }
        Check($"{state}: no two texts overlap", overlaps == 0, first);
    }

    void FitCheckTravel()
    {
        foreach (var l in new[] { _travel.DestinationLabel, _travel.StatusLabel })
        {
            var min = l.GetCombinedMinimumSize();
            Check($"travel: label {l.Name} fits", min.X <= l.Size.X + 0.5f, $"{min} in {l.Size}");
        }
    }
}
