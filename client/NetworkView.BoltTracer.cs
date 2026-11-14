using System;
using Godot;
using SmClient.Bullets;
using SmClient.ImpactRules;

namespace SmClient;

/// <summary>
/// fx/visuals-2 (owner's playtest of 2026-10-30, round 2, item 45; client presentation only): the bolt weapons' tracers (bolter, bolt pistol, stalker, heavy bolter, storm bolter and the Chaos twins) read as
/// thick, blocky and too long. The data's tracer is a bright head of the <c>heavy_bolter_tracer_long_00</c> texture (a 0.25 m wide beam, doubled by a half-bright copy) flying down the shot and trailing
/// 40 metres of texture behind it (<c>MeterPerU</c>). The textures, the head speed and the width curve stay the data's; this draws them thinner (<see cref="BoltTracerWidth"/> of the data's width)
/// and with a shorter trail (<see cref="BoltTracerTrail"/> of the data's), both **ours** (estimates, refine register). The beam's edges are its texture's; thinning narrows the soft ramp with it.
/// <c>--old-bolt-tracer</c> restores the data's size.
/// </summary>
public partial class NetworkView
{
    /// <summary>The share of the data's beam width and of its 40 m trail the bolt tracers keep (ours).</summary>
    const float BoltTracerWidth = 0.42f, BoltTracerTrail = 0.3f;
    /// <summary>fx/bolt-tracer-impact (ours): the owner still read the tracer as thick and long; 0.28 of the data's width, 0.22 of its trail.</summary>
    const float BoltTracerWidthNow = 0.28f, BoltTracerTrailNow = 0.22f;

    bool OldBoltTracer => _oldBoltTracer ??= CmdFlag("old-bolt-tracer");
    bool? _oldBoltTracer;

    static bool IsBoltTracer(string effect) =>
        effect.Contains("bolter", StringComparison.OrdinalIgnoreCase) || effect.Contains("stalker", StringComparison.OrdinalIgnoreCase) || effect.Contains("bolt_pistol", StringComparison.OrdinalIgnoreCase);

    /// <summary>fx/weapon-visuals (owner, 2026-11-14): the Kraken Bolts tracer's red (ours; <see cref="KrakenTracer.Red"/>).</summary>
    static readonly Color KrakenTint = new(KrakenTracer.Red.R, KrakenTracer.Red.G, KrakenTracer.Red.B);
    int _krakenTracers;

    /// <summary><c>--old-kraken-tracer</c> (before / after): Kraken Bolts' tracer drawn as a plain bolter's.</summary>
    bool OldKrakenTracer => _oldKrakenTracer ??= CmdFlag("old-kraken-tracer");
    bool? _oldKrakenTracer;

    /// <summary>Whether a shot draws the Kraken tracer: a bolt tracer of a bolter that can take the perk, fired by a player who has it (every player seen, both modes).</summary>
    bool KrakenShot(string wargear, string effect, int shooter)
    {
        if (OldKrakenTracer || _config is null || _client?.Predicted is not { } me || !IsBoltTracer(effect) || !KrakenTracer.CanTake(wargear)) return false;
        var state = StateOfPlayer(shooter, me);
        return state is { } st && KrakenTracer.Applies(wargear, SmSim.Loadouts.Upgrades(_config, st).Has(KrakenTracer.Upgrade));
    }

    void ThinBoltTracer(FxEffect e, string effect, bool kraken = false)
    {
        if (kraken && !OldBoltTracer) { e.TracerWidthScale = KrakenTracer.WidthScale; e.TracerTrailScale = KrakenTracer.TrailScale; _krakenTracers++; return; }
        if (OldBoltTracer || !IsBoltTracer(effect)) return;
        e.TracerWidthScale = OldBoltFx ? BoltTracerWidth : BoltTracerWidthNow;   // fx/bolt-tracer-impact: thinner and shorter again (--old-bolt-fx: the values of #30)
        e.TracerTrailScale = OldBoltFx ? BoltTracerTrail : BoltTracerTrailNow;
    }
}
