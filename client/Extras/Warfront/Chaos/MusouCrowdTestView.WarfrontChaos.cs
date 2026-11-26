using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmContent.Customization;
using SmContent.Extras.Warfront;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// Test aid <c>--musou-crowd-test --warfront-chaos [--kinds ...]</c> (Chaos Warfront C3): the crowd test stage draws each kind with the Chaos army's look (<see cref="WarfrontChaosLooks"/>), the marine-rig
/// units' PvP Chaos class looks built here as the networked view builds them (the class by the plan's word, the predefined set it names). Default kinds: every kind a Chaos battle fields. A caption
/// names the Chaos unit (<c>WarfrontChaosRoster.Unit</c>) and the look on each still. *Original assets loaded* (the install, read only) on a plain test stage.
/// </summary>
public partial class MusouCrowdTestView
{
    WarfrontChaosLooks? _wfChaos;
    CustomizationCatalog? _wfCatalog;
    AppearanceAssets? _wfAssets;
    int _wfMarine;
    bool _wfStarted, _wfDone;
    Label? _wfCaption;
    double _wfSince;

    static readonly MusouEnemyType[] WarfrontChaosKinds =
    [
        MusouEnemyType.Boy, MusouEnemyType.ShootaBoy, MusouEnemyType.ChaosMarine, MusouEnemyType.Nob, MusouEnemyType.BigShootaNob, MusouEnemyType.RokkitBoy, MusouEnemyType.Stormboy,
        MusouEnemyType.Gretchin, MusouEnemyType.BossNob, MusouEnemyType.Warboss,
    ];

    partial void WarfrontCrowdTest(ref bool waiting)
    {
        if (!_args.ContainsKey("warfront-chaos") || _looks is null) return;
        if (!_wfStarted)
        {
            _wfStarted = true;
            _wfSince = Time.GetTicksMsec() / 1000.0;
            if (!_args.ContainsKey("kinds")) _kinds = WarfrontChaosKinds;
            _looks.WarfrontOwnsLooks = true;
            _looks.WarfrontChaos = _wfChaos = new WarfrontChaosLooks(_gameDir, _ => []);
            var notes = new List<string>();
            _wfAssets = AppearanceAssets.Shared(_gameDir, notes);
            try { _wfCatalog = CustomizationCatalog.Load(_gameDir, notes, MarineAssets.Schemes(_gameDir, notes)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { GD.PrintErr("warfront chaos test: the catalogue: " + ex.Message); }
            foreach (var n in notes) GD.Print("  note: " + n);
            if (_wfCatalog is { } cat) GD.Print($"warfront chaos test: the install's predefined Chaos sets: {WarfrontChaosLookPick.SetNames(cat)}");
            var layer = new CanvasLayer();
            AddChild(layer);
            _wfCaption = new Label { Position = new Vector2(24f, 18f) };
            _wfCaption.AddThemeFontSizeOverride("font_size", 26);
            _wfCaption.AddThemeColorOverride("font_outline_color", Colors.Black);
            _wfCaption.AddThemeConstantOverride("outline_size", 6);
            layer.AddChild(_wfCaption);
        }
        var chaos = _wfChaos!;
        if (chaos.Step() is { } built) GD.Print($"warfront chaos test: look {built.Name} ready");
        if (_wfMarine < WarfrontChaosLooks.MarineKinds.Length)
        {
            var kind = WarfrontChaosLooks.MarineKinds[_wfMarine];
            if (WarfrontChaosMarine(kind)) _wfMarine++;
        }
        foreach (var n in chaos.Notes) GD.Print("  note: " + n);
        chaos.Notes.Clear();
        bool rigsDone = _kinds.Where(k => !WarfrontChaosLooksPlan.IsMarine(k)).All(k => chaos.For(k) is not null) || Time.GetTicksMsec() / 1000.0 - _wfSince > 120.0;
        if (!_wfDone && _wfMarine >= WarfrontChaosLooks.MarineKinds.Length && rigsDone)
        {
            _wfDone = true;
            var missing = _kinds.Where(k => _looks.For(k) is null).ToList();
            if (missing.Count > 0) { GD.Print($"warfront chaos test: no look for {string.Join(", ", missing)}; left out"); _kinds = _kinds.Except(missing).ToArray(); }
            GD.Print("warfront chaos test: " + string.Join(", ", _kinds.Select(k => $"{WarfrontChaosLooksPlan.UnitName(k)}={_looks.For(k)?.Name}")));
            if (_kinds.Length == 0) { GetTree().Quit(1); return; }
        }
        waiting = !_wfDone;
        if (_wfDone && _wfCaption is { } caption) caption.Text = "";
    }

    /// <summary>Builds one marine kind's look (true: done or given up).</summary>
    bool WarfrontChaosMarine(MusouEnemyType kind)
    {
        var chaos = _wfChaos!;
        if (chaos.HasMarine(kind)) return true;
        if (WarfrontChaosLooksPlan.MarineLook(kind) is not { } plan || _wfCatalog is not { } catalog || _wfAssets is not { } assets) return chaos.OfferMarine(kind, null, "none");
        var words = WarfrontChaosLookPick.ClassWords(plan.ClassWord);
        var cls = catalog.Classes.Where(c => c.Race == CustomizationCatalog.Chaos).FirstOrDefault(c => WarfrontChaosLookPick.Matches(c.Key, c.Role, words))
                  ?? catalog.Classes.FirstOrDefault(c => c.Race == CustomizationCatalog.Chaos);
        if (cls is null) return chaos.OfferMarine(kind, null, "no Chaos class");
        var code = WarfrontChaosLookPick.Code(catalog, plan, out string set);
        try
        {
            var look = AppearanceResolver.Resolve(code, cls.Key, catalog, assets.ResolveOptions());
            var built = assets.Build(look);
            GD.Print($"warfront chaos test: {WarfrontChaosLooksPlan.UnitName(kind)}: class {cls.Key} in {set}");
            return chaos.OfferMarine(kind, built, look.PredefinedSet ?? cls.Key);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            GD.PrintErr($"warfront chaos test: {kind}: {ex.Message}");
            return chaos.OfferMarine(kind, null, "failed");
        }
    }

    /// <summary>The caption of the kind on the stage (called each frame by the test's own loop through <see cref="_Process"/>'s still).</summary>
    public override void _PhysicsProcess(double delta)
    {
        if (_wfCaption is null || !_wfDone || _looks is null || _kind < 0 || _kind >= _kinds.Length) return;
        var k = _kinds[_kind];
        _wfCaption.Text = $"{WarfrontChaosLooksPlan.UnitName(k)}  ({_looks.For(k)?.Name})";
    }
}
