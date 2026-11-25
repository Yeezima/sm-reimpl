using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmContent.Customization;
using SmContent.Extras.Musou;
using SmContent.Extras.Warfront;
using SmNet.Extras.Gore;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Faction;

namespace SmClient;

/// <summary>
/// The Chaos army on the client (Chaos Warfront C3; design "Army aliasing"): the Hub's faction byte says which army this run fights, and in a Chaos battle the C0 seams draw, sound, bleed and flinch each
/// kind as its Chaos unit (<see cref="WarfrontChaosLooksPlan"/>; labels <c>WarfrontChaosRoster.Unit</c>). The type bytes on the wire are the Orks', so nothing here changes what the server sends.
/// <list type="bullet">
/// <item>Looks: <see cref="WarfrontChaosLooks"/>; the marine-rig units' PvP Chaos class looks are asked from the view's look builder (<see cref="WarfrontChaosMarines"/>).</item>
/// <item>The Chaos type table on the client's config (<c>WarfrontChaosTuning.Apply</c>), so the shot and contact bodies are the server's (the Daemon's is bigger than a Gretchin's; C2's note).</item>
/// <item>Sounds: the renegade's and the Bloodletter's co-op blueprints and weapons; the marine kinds Musou Mode's Chaos Marine set; a kind with none yet is silent, never an Ork.</item>
/// <item>Gore: no Ork blood or dark Ork gore for any Chaos kind (the hits play the PvP marine's impact, C0 review finding 1); marines and Daemons are never torn apart.</item>
/// <item>No ragdoll pool in a Chaos battle (the pool is the Ork boy's ragdoll): blast deaths tumble.</item>
/// </list>
/// In an Orks battle every hook returns at once (<see cref="_wfChaosOn"/> false), so Orks battles are drawn as before. Until the first Hub section of a Warfront connection says the army, the crowd draws
/// nothing (<see cref="MusouLooks.WarfrontOwnsLooks"/>), so a Chaos battle never shows an Ork for a frame.
/// </summary>
public partial class NetworkView
{
    WarfrontChaosLooks? _wfChaos;
    bool _wfChaosOn, _wfChaosKnown, _wfChaosConfigSet, _wfChaosSetsNoted;
    double _wfChaosLogAt;
    readonly Dictionary<MusouEnemyType, LookRequest?> _wfChaosWant = new();
    readonly Dictionary<MusouEnemyType, MusouSoundSet> _wfChaosSilent = new();
    MusouSoundSet?[]? _wfChaosSets;
    Task<SoundTriggers>? _wfChaosSoundLoad;
    bool _wfChaosSoundFailed;

    /// <summary>The config before the Chaos latch (restored if the Hub turns to the Orks).</summary>
    SimConfig? _wfOrksConfig;

    /// <summary>The last army a Hub section named, kept across the rejoin to the next stage process (null: none yet this session).</summary>
    static WarfrontFaction? s_wfLastArmy;

    /// <summary>The fortress map's loading caption: the CHAOS STRONGHOLD when the run fights Chaos (this connection's Hub, else the last one before the rejoin).</summary>
    string WarfrontFortressCaption => (_wfModel is { HubSections: > 0 } m ? m.Hub.View.Faction : s_wfLastArmy) == WarfrontFaction.Chaos ? "BUILDING THE CHAOS STRONGHOLD" : "BUILDING THE ORK FORTRESS";

    /// <summary>The view whose battle is drawn as Chaos (the gore table's hooks are static).</summary>
    static NetworkView? s_wfChaosView;

    /// <summary>Whether the live view draws a Chaos battle.</summary>
    static bool WarfrontChaosDrawn => s_wfChaosView is { } v && IsInstanceValid(v) && v._wfChaosOn;

    /// <summary>This frame's army (called from <c>WarfrontFrame</c>): the flags on the crowd's looks and the HUD, the config's table, the Chaos looks' build steps and the marine looks' requests.</summary>
    void WarfrontChaosFrame()
    {
        var m = WarfrontModel;
        bool known = m.HubSections > 0;
        bool chaos = known && m.Hub.View.Faction == WarfrontFaction.Chaos;
        if (known != _wfChaosKnown || chaos != _wfChaosOn)
            GD.Print($"t {_connectedFor:F1} s: warfront army: {(known ? (chaos ? "Chaos (the Chaos army's looks, sounds and gore)" : "Orks") : "not told yet (enemies hidden)")}");
        _wfChaosKnown = known;
        _wfChaosOn = chaos;
        if (known) s_wfLastArmy = m.Hub.View.Faction;   // survives the rejoin: the next stage's loading caption (WarfrontFortressClient.cs) reads it before any Hub of the new connection
        if (chaos) s_wfChaosView = this;
        if (_musouLooks is { } looks)
        {
            looks.WarfrontOwnsLooks = !known || chaos;
            looks.WarfrontChaos = chaos ? _wfChaos : null;
            looks.WarfrontSkipOrks = chaos;   // no Ork mesh, gun or rig is built in a Chaos battle
        }
        _musouOnlyChaosSounds = chaos;   // the Ork sound sets are not built in a Chaos battle (the Chaos Marine's is)
        TfwBanners.HideEnemyBanner ??= () => WarfrontChaosDrawn;   // the capture points' Ork totem (waaaghbanner) is not shown to a Chaos army; the Imperial banner still goes up when a point is secured
        if (_musouHud is { } hud) hud.WarfrontRelabel = chaos ? WarfrontChaosRelabel : null;   // C5c: one cached delegate (the shielded Lord's words in a Chaos fortress before the relay)
        if (_wfBattle is { } battle) battle.Relabel = chaos ? WarfrontChaosLooksPlan.HudLabel : null;   // the fortress strip and banners: KILL THE CHAOS LORD, THE CHAOS LORD IS DEAD
        if (!chaos)
        {
            if (_wfChaosConfigSet && _wfOrksConfig is { } orks)   // the Hub turned to the Orks (a new run): the latch is undone
            {
                _wfChaosConfigSet = false;
                _config = orks;
                _musouRulesOver = null;
                if (_musouCrowd is { } c) c.RulesOver = null;
                if (_client is { } cl) cl.ExtrasSolids = null;
                GD.Print("warfront chaos: the Hub turned to the Orks; the client's enemy table is the Orks' again");
            }
            return;
        }
        if (!_wfChaosConfigSet && _config is { } config)
        {
            _wfChaosConfigSet = true;
            _wfOrksConfig = config;
            _config = WarfrontChaosTuning.Apply(config, WarfrontFaction.Chaos);   // the clients' shot and contact bodies read the same table as the server's (not in the config hash)
            GD.Print("warfront chaos: the client's enemy table is the Chaos army's (" + WarfrontChaosTuning.Note(_config.MusouRules!) + ")");
            if (_client is { } client) client.ExtrasSolids = null;   // rebuilt next frame from the Chaos config (NetworkView.Musou.cs)
        }
        _musouRulesOver = _config?.MusouRules;   // the guns' bursts, the volleys and the crowd's speeds read the Chaos table
        if (_musouCrowd is { } crowd) crowd.RulesOver = _musouRulesOver;
        WarfrontChaosDuelFrame();   // C5c: the fortress's silent breach closes and the shielded Lord's bar (after the relabels above)
        if (_musouRagdolls is null) _musouRagdollsTried = true;   // the pool is the Ork boy's ragdoll: never built for a Chaos army
        _wfChaos ??= new WarfrontChaosLooks(_gameDir, WarfrontChaosHeldFromConfig);
        if (_musouLooks is { } l) l.WarfrontChaos = _wfChaos;
        if (_wfChaos.Step() is { } built) GD.Print($"warfront chaos: look {built.Name} ready ({OrkAssets.Describe(built.Assets)})");
        WarfrontChaosMarines(_wfChaos);
        foreach (var n in _wfChaos.Notes) { GD.Print(n); _notes.Add(n); }
        _wfChaos.Notes.Clear();
        if (_connectedFor >= _wfChaosLogAt)
        {
            _wfChaosLogAt = _connectedFor + 20.0;
            GD.Print("warfront chaos: looks " + string.Join(", ", _wfChaos.Built().Select(b => $"{WarfrontChaosLooksPlan.UnitName(b.Kind)}={b.Look.Name}")));
        }
    }

    /// <summary>The PvP weapons of the match's config whose names hold a word of the plan's held wargear (the Havocs' heavy bolter and missile launcher are the PvP class's).</summary>
    IEnumerable<string> WarfrontChaosHeldFromConfig(MusouEnemyType kind)
    {
        if (_config is not { } c || WarfrontChaosLooksPlan.MarineLook(kind) is not { } plan) return [];
        return c.Weapons.Select(w => w.Name).Where(n => plan.Held.Any(h => n.Contains(h, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Asks the view's look builder for each marine kind's PvP Chaos class look (its class's default armour, or a predefined Chaos set) and hands each built one to the Chaos looks.</summary>
    void WarfrontChaosMarines(WarfrontChaosLooks chaos)
    {
        if (!LooksReady || _config is null || _lookScheduler is null) return;
        foreach (var kind in WarfrontChaosLooks.MarineKinds)
        {
            if (chaos.HasMarine(kind)) continue;
            if (kind != MusouEnemyType.ChaosMarine && !chaos.HasMarine(MusouEnemyType.ChaosMarine)) break;   // the Tactical first: the others fall back to it
            if (!_wfChaosWant.TryGetValue(kind, out var want))
            {
                _wfChaosWant[kind] = want = WarfrontChaosLookRequest(kind);
                if (want is not null) _looks!.Prefetch(want.Look);
            }
            if (want is null) { chaos.OfferMarine(kind, null, "none"); continue; }
            if (_builtLooks.TryGetValue(want.Key, out var built)) { chaos.OfferMarine(kind, built, want.Look.PredefinedSet ?? want.Look.ClassKey); continue; }
            if (IsFailed(want.Key)) { chaos.OfferMarine(kind, null, "failed"); continue; }
            _wantedLooks[want.Key] = want.Look;
            _lookScheduler.Want(want.Key, 1);
        }
    }

    /// <summary>The look request of a marine kind: a Chaos class by the plan's word (raptor: the assault role, havoc: the heavy one), wearing the first predefined Chaos set the plan names, else its default armour.</summary>
    LookRequest? WarfrontChaosLookRequest(MusouEnemyType kind)
    {
        if (WarfrontChaosLooksPlan.MarineLook(kind) is not { } plan || _config is not { } c || _lookCatalog is not { } catalog) return null;
        var words = WarfrontChaosLookPick.ClassWords(plan.ClassWord);
        int cls = -1;
        for (int i = 0; i < c.Classes.Count && cls < 0; i++)
            if (c.Classes[i].Side == Team.Chaos && WarfrontChaosLookPick.Matches(c.Classes[i].Name, c.Classes[i].Role, words)) cls = i;
        for (int i = 0; i < c.Classes.Count && cls < 0; i++) if (c.Classes[i].Side == Team.Chaos) cls = i;
        if (cls < 0) return null;
        if (!_wfChaosSetsNoted) { _wfChaosSetsNoted = true; GD.Print($"warfront chaos: the install's predefined Chaos sets: {WarfrontChaosLookPick.SetNames(catalog)}"); }
        var code = WarfrontChaosLookPick.Code(catalog, plan, out string set);
        var state = Loadouts.Choose(c, new PlayerState(0, default, default, 0f, true) { Class = cls, Team = Team.Chaos }, -1, -1, -1);
        var request = LookFor(state, code);
        GD.Print($"warfront chaos: {WarfrontChaosLooksPlan.UnitName(kind)} asks for class {c.Classes[cls].Name} in {set}{(request is null ? " (no look: the Tactical's)" : "")}");
        return request;
    }

    // ---------------------------------------------------------------- sounds (the C0 sound seam)

    partial void WarfrontSoundSetOf(MusouEnemyType type, ref MusouSoundSet? set)
    {
        if (!_wfChaosOn) return;
        if (WarfrontChaosLooksPlan.IsMarine(type)) { set = _musouSets is { } all && (int)MusouEnemyType.ChaosMarine < all.Length && all[(int)MusouEnemyType.ChaosMarine] is { } marine ? marine : Silent(type); return; }
        set = WarfrontChaosSets() is { } chaos && (int)type < chaos.Length && chaos[(int)type] is { } own ? own : Silent(type);
    }

    MusouSoundSet Silent(MusouEnemyType type)
    {
        if (!_wfChaosSilent.TryGetValue(type, out var s)) _wfChaosSilent[type] = s = new MusouSoundSet { Type = type };
        return s;
    }

    /// <summary>The renegade's and the Bloodletter's sets, read on a worker once (null until then, or when they cannot be had: those kinds stay silent).</summary>
    MusouSoundSet?[]? WarfrontChaosSets()
    {
        if (_wfChaosSets is not null || _wfChaosSoundFailed || _audio is null || _sound is null) return _wfChaosSets;
        string gameDir = _gameDir;
        var files = WarfrontChaosLooksPlan.SoundFiles;
        _wfChaosSoundLoad ??= Task.Run(() => SoundTriggers.Load(gameDir, files.SelectMany(f => new[] { f.Melee, f.Gun }).OfType<string>().Distinct().ToList(),
            files.Select(f => f.Blueprint).Distinct().ToList(), (IReadOnlyList<SmFormats.Havok.BehaviorGraph>?)null));
        if (!_wfChaosSoundLoad.IsCompleted) return null;
        if (_wfChaosSoundLoad.IsFaulted)
        {
            _wfChaosSoundFailed = true;
            GD.PrintErr($"warfront chaos sounds: the renegade's and the Bloodletter's files could not be read ({_wfChaosSoundLoad.Exception?.GetBaseException().Message}); they stay silent");
            return null;
        }
        var triggers = _wfChaosSoundLoad.Result;
        var sets = new MusouSoundSet?[256];
        var events = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var set = sets[(int)f.Kind] = MusouSoundSet.Build(new MusouSoundFiles(f.Kind, f.Blueprint, f.Melee, Gun: f.Gun), triggers, _audio.HasEvent);
            foreach (var (_, cue) in set.Cues) foreach (var o in cue.Options) foreach (var b in o.Events) events.Add(b.StartEvent);
            GD.Print($"warfront chaos sounds: {WarfrontChaosLooksPlan.UnitName(f.Kind)} ({f.Blueprint}): character {set.Character ?? "none"}, {set.Cues.Count(c => !c.Cue.Empty)} cues" +
                     (set.Missing.Count == 0 ? "" : $"; not in the banks: {string.Join(", ", set.Missing)}"));
        }
        _sound.Prefetch(events.SelectMany(_audio.WemsOf).ToHashSet());
        return _wfChaosSets = sets;
    }

    // ---------------------------------------------------------------- gore (the C0 gore seam; static: they read the live view's army)

    static partial void WarfrontGoreOrk(MusouEnemyType type, ref bool ork)
    {
        if (WarfrontChaosDrawn) ork = false;   // no Chaos unit bleeds as an Ork: no Ork blood, no dark Ork gore (the hits play the marine's impact)
    }

    static partial void WarfrontGoreChest(MusouEnemyType type, ref float height)
    {
        if (WarfrontChaosDrawn) height = WarfrontChaosLooksPlan.ChestHeight(type);
    }

    static partial void WarfrontGorePlan(in MusouDeathShown death, ref GoreBurst plan)
    {
        if (!WarfrontChaosDrawn || plan.Class == GoreClass.None) return;
        // An armoured marine and a Daemon are never torn apart (ours: the marine's death binds no gore, docs "Death effects"; the Bloodletter's vanishes in its own effect): the body stays.
        if (WarfrontChaosLooksPlan.BodyOf(death.Type) is WarfrontChaosBody.Marine or WarfrontChaosBody.Bloodletter) plan = plan with { Gibbed = false, Gibs = 0, Limbs = 0 };
    }
}
