using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmNet;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// Added content "Musou" in the networked view (design section D), only when the match runs <see cref="ContentExtras.Musou"/>. The routed sections (tags 3 to 5,
/// from the dispatcher <c>NetworkView.Extras.cs</c>) go into the engine-free crowd state (<see cref="MusouClientState"/>), which is sampled at the view's render
/// clock (the remote players' interpolated tick); the crowd (<see cref="MusouCrowd"/>) draws the result, the dead play their death where they fell, with the
/// red splash of a body hit (<see cref="BloodSplashAt"/>) within 25 m. Test aids: <c>--musou-visible N</c> (most drawn, default 150), <c>--musou-no-vat</c>
/// (skinned only, the fallback). Every 10 s one log line: the frame times (mean fps, p50 and p99 ms) and what the crowd drew. A lean bot or a headless client
/// keeps the state and draws nothing (no HUD node either). MU3: the HUD, the hit feedback and the telegraphs (<c>MusouFx.cs</c>, <see cref="MusouHud"/>); the enemies' sounds are <c>NetworkView.MusouSounds.cs</c>.
/// </summary>
public partial class NetworkView
{
    readonly MusouClientState _musouState = new();
    MusouLooks? _musouLooks;
    MusouCrowd? _musouCrowd;
    MusouRagdolls? _musouRagdolls;
    bool _musouRagdollsTried;
    bool _musouNoDraw, _musouDrawChecked;
    double _musouStarted = -1, _musouLogAt;
    readonly List<float> _musouFrames = new();
    readonly List<MusouDeathShown> _musouDeaths = new();   // deaths not yet given a body (kept while the crowd is being made; at most MaxBodies)

    /// <summary>Enemy deaths the client has been told of (a test aid's count).</summary>
    internal long MusouDeathsSeen { get; private set; }
    /// <summary>Deaths far and out of view given no body (musou/perf-pass; the log's count).</summary>
    long _musouUnseenDeaths;

    bool MusouActive => _config is { } c && c.Extras.HasFlag(ContentExtras.Musou);

    /// <summary>The latest director the server sent (null: none yet, or not a Musou match).</summary>
    public MusouDirectorView? MusouDirector => _musouState.Director;
    /// <summary>The crowd state (test aids and the HUD of MU3).</summary>
    internal MusouClientState MusouState => _musouState;

    /// <summary>This feature's part of the frame hook: <paramref name="sections"/> are this frame's sections with Musou's tags, oldest first.</summary>
    void MusouFrame(PlayerState me, IReadOnlyList<PlayerState> others, double delta, IReadOnlyList<ExtrasSection> sections)
    {
        if (!MusouActive || _client is not { } client) { if (_musouHud is not null) _musouHud.Visible = false; return; }
        if (client.ExtrasSolids is null && _config is { } solidConfig) client.ExtrasSolids = new MusouSolidView(_musouState, solidConfig);   // musou/boss-collision: the big Orks are solid and lockable in the own prediction
        MusouDeathSpikeStart();   // --musou-death-spike-test (NetworkView.MusouDeathSpike.cs)
        Ph("musou: state");   // musou/perf-pass: --frame-profile's Musou phases
        double now = Time.GetTicksUsec() / 1e6;
        MusouMotionSections(sections);   // NetworkView.MusouMotion.cs: --musou-old-far-motion, and the measure's bytes
        foreach (var section in MusouGoreFeed(me, sections, delta)) if (!MusouDropSection(section)) _musouState.Apply(section, now);   // MU-D: tag 6 to the drops (MusouDrops.cs); M10: --musou-gore-demo replaces the server's sections
        var (renderTick, _) = client.RemotePlayers();
        renderTick = MusouGoreRenderTick(renderTick);
        var shown = _musouState.Sample(renderTick > 0 ? renderTick : _musouState.NewestTick, now);
        _tfwShown = shown;   // the scripted class test finds the elites here (Tfw/NetworkView.TfwClassTest.cs)
        MusouMotionFrame(me, shown, delta);   // --musou-motion-log: the shown crowd's motion by distance

        if (!_musouDrawChecked)
        {
            _musouDrawChecked = true;
            _musouNoDraw = (Lean || DisplayServer.GetName() == "headless") && !MusouDeathSpikeTest;   // the death-spike test draws headless (NetworkView.MusouDeathSpike.cs)
            GD.Print($"musou: crowd {(_musouNoDraw ? "not drawn (headless or lean)" : "drawn")}");
        }
        if (!_musouNoDraw)
        {
            MusouDeathsArrive(_musouState.NewDeaths, now);   // night batch 2026-11-06: a burst is handed out over a few frames (NetworkView.MusouDeathSpread.cs)
            Ph("musou: crowd, deaths, ragdolls");
            if (_gameSpace is not null) MusouDraw(shown, (float)delta);
            Ph("musou: hud, fx, sounds");
            ulong t0 = SpikeMark();
            MusouRoundsFrame(shown);   // the gun Orks' rounds: the server's, and those built from burst cues (NetworkView.MusouGuns.cs)
            MusouVolleysFrame(me, others, shown);   // musou-net-budget: the client's own volleys join them (NetworkView.MusouVolleys.cs)
            MusouPresent(me, shown, (float)delta);   // MU3: the HUD, hit feedback and telegraphs (MusouFx.cs)
            MusouSoundsFrame(me, shown, (float)delta);   // the enemies' original sounds (NetworkView.MusouSounds.cs)
            _spikeSoundsMs += SpikeMs(t0);
            TfwMeleeCount(shown);   // Tfw/NetworkView.TfwMelee.cs: the test aid's counts, nothing without it
            Ph("musou: gore");
            t0 = SpikeMark();
            MusouGoreFrame(me, shown, (float)delta);   // M10: gibs, sprays, pools and the cleave slashes (Gore/NetworkView.MusouGore.cs)
            _spikeGoreMs += SpikeMs(t0);
        }
        Ph("musou: drops, log");
        MusouDeathSpikeFrame(_musouState.NewDeaths.Count);   // --musou-death-spike-test (NetworkView.MusouDeathSpike.cs)
        TfwCoopBotDeaths();   // Tfw/NetworkView.TfwCoopBot.cs: co-op's --bot counts its kills
        MusouDeathsSeen += _musouState.NewDeaths.Count;   // the Firing Range script counts the strike's kills
        _musouState.TakeNew();
        MusouDropsFrame(me, (float)delta);   // MU-D: the own weapon drops (MusouDrops.cs)
        MusouLog(delta, shown.Count);
    }

    /// <summary>
    /// Whether the camera has a clear line to a flash (Godot space): the effect lights cast no shadows, so in Musou Mode a blast outside lit the spawn room through its walls, again and again. The last metre
    /// counts as clear, so a blast against a wall still lights that wall.
    /// </summary>
    bool MusouLightSeen(Vector3 at)
    {
        if (_camera is not { } camera || _gameSpace is null || _client?.Collision is not { } world) return true;
        var toGame = _gameSpace.GlobalTransform.AffineInverse();
        Vector3 from = toGame * camera.GlobalPosition, to = toGame * at;
        var way = new System.Numerics.Vector3(to.X - from.X, to.Y - from.Y, to.Z - from.Z);
        float length = way.Length();
        if (length <= 1f) return true;
        return !(world.Weapons.Raycast(new System.Numerics.Vector3(from.X, from.Y, from.Z), way / length, length) is { } wall && wall.Distance < length - 1f);
    }

    void MusouDraw(IReadOnlyList<MusouShown> shown, float delta)
    {
        if (_fx is not null) _fx.LightFilter ??= MusouLightSeen;
        if (_musouStarted < 0) _musouStarted = _connectedFor;
        if (_musouLooks is null)
        {
            if (Orks() is not { } orks) return;
            _musouLooks = new MusouLooks(_gameDir, orks, MusouChaosLook, guns: MusouGunsOn);
            _musouCrowd = new MusouCrowd(_musouLooks) { Visible = MusouArg("musou-visible") ?? MusouCrowd.DefaultVisible, NoVat = CmdFlag("musou-no-vat"), SolidBetween = MusouSolidBetween, OldTierSwitch = CmdFlag("musou-old-crowd-motion") };
            _gameSpace!.AddChild(_musouCrowd);
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 8 && _musouLooks.Step() is { } built) GD.Print($"musou: look {built.Name} ready ({OrkAssets.Describe(built.Assets)})");
        if (_connectedFor - _musouStarted > 15) _musouLooks.StopWaiting();
        MusouRagdollsBuild();
        foreach (var n in _musouLooks.Notes) { GD.Print(n); _notes.Add(n); }
        _musouLooks.Notes.Clear();

        var camera = _camera is null ? Vector3.Zero : new Vector3(_camera.GlobalPosition.X, -_camera.GlobalPosition.Z, _camera.GlobalPosition.Y);   // Godot to game space
        ulong t0 = SpikeMark();
        MusouDeathsTake(camera);   // this frame's share of the deaths waiting (NetworkView.MusouDeathSpread.cs)
        _musouDeathsHanded.Clear();
        _musouDeathsHanded.AddRange(_musouDeaths);   // their yells and falls (NetworkView.MusouSounds.cs)
        foreach (var d in _musouDeaths)
        {
            float metres = new Vector3(d.Position.X, d.Position.Y, d.Position.Z).DistanceTo(camera);
            // The owner, 2026-11-08 ("if a player cannot see it, it doesn't ever get drawn, unless the player is close by"; ours as to the distance): a death beyond the battle flow's near distance and
            // outside the view leaves no body, gore or ragdoll (its yell still plays). --army-far-bodies (test aid) keeps them all.
            // The corpse floor (the owner, 2026-11-09: "they should always leave a corpse for at least 10 seconds before disappearing"; the new floor wins over 2026-11-08's): every death is handled at
            // any distance, seen or not; a body out of the view costs a few floats a frame and is drawn once the view turns to it. --musou-old-corpses restores the rule above.
            if (!MusouCorpseRules.HandlesUnseenFar(CrowdView.OldCorpses) && metres > SmSim.Extras.Musou.Campaign.TfwArmyRules.CoreMetres && !ArmyFarBodies && _camera is { } eye
                && !eye.IsPositionInFrustum(_gameSpace!.Transform * new Vector3(d.Position.X, d.Position.Y, d.Position.Z + 1f))) { _musouUnseenDeaths++; continue; }
            bool gibbed = MusouGoreRegister(d, camera, out bool burstDrawn);   // M10: a gibbed body leaves no corpse (its pieces instead); the corpse floor: only when its burst is drawn
            if (MusouGoreRules.Thrown(d.Cause)) MusouThrow(d, metres);   // a blast or barrage death always leaves a body, thrown through the gore's burst: a ragdoll near, else a tumbling body
            else if (MusouCorpseRules.LeavesBody(gibbed, burstDrawn, farUnseen: false, CrowdView.OldCorpses)) _musouCrowd!.AddBody(d, MusouDeathWaited(d.Id), keepSkeleton: !_musouOldDeaths);
            var at = new Vector3(d.Position.X, d.Position.Y, d.Position.Z + 1.1f);
            if (GoreOff && at.DistanceTo(camera) <= MusouCrowd.NearMetres)   // the dark gore draws the death blood; the red glow only with --no-musou-gore
                BloodSplashAt(_gameSpace!.Transform * at, -(_gameSpace.Transform.Basis * new Vector3(d.Push.X, d.Push.Y, d.Push.Z)), 0.0);
        }
        _musouDeaths.Clear();
        _musouDeathAge.Clear();
        _spikeDeathsMs += SpikeMs(t0);
        // The shot Orks' flinch is off (the owner, 2026-11-06: it read as a 180-degree turn; "for now, simply remove those hit reactions completely"); --musou-ork-flinch brings it back.
        if (MusouOrkFlinch) foreach (var e in _musouState.NewEvents) if (e.Kind == MusouEventKind.Hit && e.Value is 1 or 2) _musouCrowd!.Flinch(e.Enemy);   // markers 1 and 2: a shot's hit
        MusouGunsFrame(shown, camera, delta);   // the ranged Orks' shots: flash, tracer, impact (NetworkView.MusouGuns.cs)
        t0 = SpikeMark();
        MusouDeathsWaitingItems();   // the deaths not yet handed out keep their place in the crowd, already dying
        _musouCrowd!.Frame(TfwArmyOrks(shown, camera, delta), camera, delta);   // with the distant armies' impostors (Tfw/NetworkView.TfwArmies.cs, expansion S6)
        _spikeCrowdMs += SpikeMs(t0);
        t0 = SpikeMark();
        _musouRagdolls?.Frame(delta, MusouCrowd.SinkSeconds, MusouCrowd.SinkDepth);
        _spikeDollsMs += SpikeMs(t0);
    }

    /// <summary>A blast death's body: a <see cref="MusouRagdolls"/> doll when there is one to use, else the crowd's tumbling body.</summary>
    void MusouThrow(in MusouDeathShown d, float metres)
    {
        var push = MusouGoreRules.BlastThrow(d.Push, d.Id, d.Tick, d.Type);
        var velocity = new Vector3(push.X, push.Y, push.Z);
        if (metres <= MusouRagdolls.Metres && MusouRagdolls.Fits(d.Type) && _musouRagdolls is { } dolls && _musouLooks?.For(d.Type) is { } look && dolls.Throw(d, look, velocity)) return;
        _musouCrowd!.AddFlung(d, velocity, keepSkeleton: !_musouOldDeaths);   // night batch 2026-11-06: the body keeps the skeleton that drew the Ork
    }

    /// <summary>Makes the pool of ragdolls once the boy's look is built, one doll a frame. <c>--no-musou-ragdolls</c>: the tumbling bodies only.</summary>
    void MusouRagdollsBuild()
    {
        if (_musouRagdolls is { } dolls) { dolls.BuildStep(); MusouRagdollsPrewarm(dolls); return; }
        if (_musouRagdollsTried || CmdFlag("no-musou-ragdolls") || _musouLooks?.For(MusouEnemyType.Boy) is not { } boy) return;
        _musouRagdollsTried = true;
        if (OrkRagdollDefinition(boy.Assets) is not { } def || boy.Clips[MusouLooks.Run] is not { } clip) { GD.Print("musou: no ragdolls (no Ork ragdoll to be had); blast deaths tumble"); return; }
        EnsureRagdollWorld();
        _musouRagdolls = new MusouRagdolls(def, boy.Assets, clip);
        _gameSpace!.AddChild(_musouRagdolls);
        GD.Print($"musou: ragdoll pool of {MusouRagdolls.Dolls}: {def.Description}");
    }

    /// <summary>The first solid thing between two points in game space (<see cref="CrowdView.SolidBetween"/>).</summary>
    float? MusouSolidBetween(Vector3 from, Vector3 to)
    {
        if (_client?.Collision is not { } world) return null;
        var way = new System.Numerics.Vector3(to.X - from.X, to.Y - from.Y, to.Z - from.Z);
        float length = way.Length();
        if (length < 1e-4f) return null;
        return world.Weapons.Raycast(new System.Numerics.Vector3(from.X, from.Y, from.Z), way / length, length) is { } hit ? hit.Distance : null;
    }

    /// <summary>The Chaos Marine's model: the default look of the first Chaos class, once the view has built it (the warm-up builds every class's default).</summary>
    MarineAssets? MusouChaosLook()
    {
        if (_config is null) return null;
        for (int i = 0; i < _config.Classes.Count; i++)
        {
            if (_config.Classes[i].Side != Team.Chaos) continue;
            var state = Loadouts.Choose(_config, new PlayerState(0, default, default, 0f, true) { Class = i, Team = Team.Chaos }, -1, -1, -1);
            if (LookFor(state, AppearanceCode.Default) is { } look && _builtLooks.TryGetValue(look.Key, out var built)) return built;
        }
        return null;
    }

    static int? MusouArg(string name)
    {
        var args = OS.GetCmdlineUserArgs();
        int at = Array.IndexOf(args, "--" + name);
        return at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out int v) ? Math.Clamp(v, 0, 2000) : null;
    }

    void MusouLog(double delta, int known)
    {
        _musouFrames.Add((float)(delta * 1000.0));
        if (_connectedFor < _musouLogAt) return;
        _musouLogAt = _connectedFor + 10.0;
        if (_musouFrames.Count == 0) return;
        var sorted = _musouFrames.OrderBy(f => f).ToArray();
        float P(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        double fps = sorted.Length / (sorted.Sum() / 1000.0);
        var c = _musouCrowd;
        GD.Print($"musou: t {_connectedFor:F0} s: {sorted.Length} frames, {fps:F1} fps, frame p50 {P(0.5):F1} ms p99 {P(0.99):F1} ms max {sorted[^1]:F1} ms; " +
                 $"enemies known {known}, drawn {(c is null ? "-" : $"{c.ShownSkinned} skinned + {c.ShownVat} vat, {c.LeftOut} left out, {c.Bodies} bodies ({c.Flung} flung, most {c.BodiesMost}, under the floor dropped {c.FloorBroken}), {_musouRagdolls?.Active ?? 0} ragdolls ({_musouRagdolls?.Thrown ?? 0} thrown of {_musouRagdolls?.Built ?? 0} built, highest {_musouRagdolls?.Highest ?? 0f:F1} m)")}; " +
                 $"sections {_musouState.SectionsRead} ({_musouState.SectionsMalformed} malformed), events {_musouState.EventsPlayed} (+{_musouState.EventCopies} copies), " +
                 $"silent drops {_musouState.DroppedSilent}; {MusouPresentNote()}; director {(_musouState.Director is { } d ? $"wave {d.Wave}/{d.Waves} {d.Phase}" : "none")}");
        _musouFrames.Clear();
    }
}
