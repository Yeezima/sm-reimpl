using System;
using System.Collections.Generic;
using SmContent;
using SmContent.Extras.Musou.Arsenal;
using SmSim;
using SmSim.Extras.Musou.Arsenal;

namespace SmClient;

/// <summary>
/// Musou's arsenal in the networked view (W0, the base; docs/new-weapons-design.md section 8, hook 4, and the animation seam): the one implementation of <see cref="ExtrasThrownVisual"/>, a dispatcher to the thrown
/// item's own model, which W4 implements in its own file (<c>Arsenal/Charge/</c>: the Remote Detonators drawn as the melta bomb, not the blueprint's Ork stikkbomb). Asked only in a Musou match
/// (<see cref="ContentExtras.Musou"/> in the match's config: the charge is an install name, <c>sm_mp_eq_demolition_charge</c>) and only for the arsenal's names. Until then the item is drawn with
/// its blueprint's model, as before.
/// </summary>
public partial class NetworkView
{
    partial void ExtrasThrownVisual(ThrownSpec item, ref ProjectileVisual? visual)
    {
        if (_config is null || !_config.Extras.HasFlag(ContentExtras.Musou) || !ArsenalRules.IsArsenal(item.Name)) return;
        RemoteDetonatorsVisual(item, ref visual);
    }

    /// <summary>W4 (<c>Arsenal/Charge/</c>): the charge's model in flight and at rest.</summary>
    partial void RemoteDetonatorsVisual(ThrownSpec item, ref ProjectileVisual? visual);

    // ---- the animation seam (W0; lead 2026-11-10: the owner wants custom animations for the claw and the spear, authored to the design's hit times, approved before use)

    /// <summary>
    /// The one place a custom clip drops in (<see cref="ExtrasMeleeClip"/>): the claw's and the spear's strikes play the power sword's and the thunder hammer's clips as stand-ins (their maps,
    /// <c>MpClasses.ExtrasMeleeMap</c>, and their clip lists through <c>AnimationAlias</c>) until an entry here gives a strike its own clip and rate. The simulation never reads it: the hits,
    /// windows and durations stay the design's. Each weapon's table is its own milestone's file.
    /// </summary>
    partial void ExtrasMeleeClip(string wargear, bool heavy, int step, ref string? clip, ref float rate)
    {
        if (_config is null || !_config.Extras.HasFlag(ContentExtras.Musou) || !ArsenalRules.IsArsenal(wargear)) return;
        LightningClawClip(wargear, heavy, step, ref clip, ref rate);
        PowerSpearClip(wargear, heavy, step, ref clip, ref rate);
    }

    /// <summary>W2 (<c>Arsenal/Claw/</c>): the claw's custom clip of a strike, once approved; none: the sword's clip at 1.25.</summary>
    partial void LightningClawClip(string wargear, bool heavy, int step, ref string? clip, ref float rate);

    /// <summary>W3 (<c>Arsenal/Spear/</c>): the spear's custom clip of a strike, once approved; none: the hammer's clip at 0.85.</summary>
    partial void PowerSpearClip(string wargear, bool heavy, int step, ref string? clip, ref float rate);

    // ---- W0b: presentation by source, and the clip speed of the charge and the pack's moves (lead 2026-11-10)

    /// <summary>
    /// The arsenal's presentation sources (<c>Arsenal.PresentationSources</c>: the claw the power sword, the spear the thunder hammer, the autocannon its install twin then the heavy bolter), in
    /// a match that carries the item only (<see cref="ArsenalRules.Shown"/>: a Musou match, and the autocannon in a Classic match with <see cref="ContentExtras.Autocannon"/>).
    /// </summary>
    partial void ExtrasPresentationSources(string wargear, ref IReadOnlyList<string>? sources)
    {
        if (_config is null || !ArsenalRules.Shown(_config.Extras, wargear)) return;
        sources = Arsenal.PresentationSources(_config.Extras, wargear);
    }

    /// <summary>
    /// The arsenal's sound sources where they are not the effects' (<c>Arsenal.SoundSources</c>: the autocannon's are the Stalker Bolter's, the owner's choice), in a Musou match only: Classic PvP's
    /// autocannon keeps its install twin's own sounds (the owner, 2026-11-11: "no stalker bolter sound change").
    /// </summary>
    partial void ExtrasSoundSources(string wargear, ref IReadOnlyList<string>? sources)
    {
        if (_config is null || !_config.Extras.HasFlag(ContentExtras.Musou) || !ArsenalRules.IsArsenal(wargear)) return;
        sources = Arsenal.SoundSources(_config.Extras, wargear);
    }

    /// <summary>Test aid <c>--old-autocannon-fx</c>: the twin's own purple muzzle flash, tracer and impact burst (W1b's look, before W1c).</summary>
    static readonly bool OldAutocannonFx = CmdFlag("old-autocannon-fx");

    /// <summary>
    /// W1c: the arsenal's effects in place of its sources' (<c>Arsenal.EffectNames</c>: the autocannon's loyalist muzzle flash, tracer and impact burst for the twin's purple ones, the owner's choices), in a
    /// match that carries the item (a Musou match; the autocannon in Classic PvP too, the same look: the owner, 2026-11-11).
    /// </summary>
    partial void ExtrasEffectNames(string wargear, ref IReadOnlyDictionary<string, string>? names)
    {
        if (OldAutocannonFx || _config is null || !ArsenalRules.Shown(_config.Extras, wargear)) return;
        names = Arsenal.EffectNames(_config.Extras, wargear);
    }

    /// <summary>
    /// The autocannon pass 2 (the owner, 2026-11-11: "Give the chaos autocannon the chaos visual effects all the way through"): a shot of the autocannon by a shooter of the Chaos race (the Havoc in
    /// Classic PvP; in Musou Mode every player is a Space Marine) plays its install twin's own effects (<c>Arsenal.EffectsFor</c>: the purple flash, tracer, impact, stop smoke and casings), a Space
    /// Marine's the loyalist ones; in a match that carries the item only, and only when the catalogue read the twin (its presentation source, read beside the classes' files), else the autocannon's own.
    /// </summary>
    partial void ExtrasShooterEffects(int shooter, string wargear, ref string effects)
    {
        if (_config is null || !ArsenalRules.IsAutocannon(wargear) || !ArsenalRules.Shown(_config.Extras, wargear)) return;
        string chosen = Arsenal.EffectsFor(_config.Extras, wargear, ShooterRace(shooter) == Race.Chaos);
        if (!string.Equals(chosen, wargear, StringComparison.OrdinalIgnoreCase) && _catalogue?.Weapon(chosen) is { Bindings.Count: > 0 }) effects = chosen;
    }

    /// <summary>The race player <paramref name="shooter"/> is drawn as (<see cref="Races.Of"/> of its class and team: the own prediction, else the last snapshot, else its side).</summary>
    Race ShooterRace(int shooter)
    {
        if (_client is null || _config is null) return Race.None;
        if (shooter == _client.PlayerId) return Races.Of(_config, _client.Predicted.Class, _client.Predicted.Team);
        foreach (var o in _lastOthers) if (o.PlayerId == shooter) return Races.Of(_config, o.Class, o.Team);
        return Races.OfSide(TeamOf(shooter));
    }

    readonly Dictionary<string, float> _arsenalMoveSpeed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The clip speed of the arsenal's melee weapons (<see cref="MeleeTuning.WeaponMap.ClipSpeed"/> of their maps: the claw's 1.25, the spear's 0.85) on the charge and the jump pack's moves, in
    /// a Musou match only; 1 (unchanged) for every other weapon and for an arsenal item with no map. Read once per weapon.
    /// </summary>
    partial void ExtrasMoveClipSpeed(string wargear, ref float rate)
    {
        if (_config is null || !_config.Extras.HasFlag(ContentExtras.Musou) || !ArsenalRules.IsArsenal(wargear)) return;
        if (!_arsenalMoveSpeed.TryGetValue(wargear, out float speed))
            _arsenalMoveSpeed[wargear] = speed = MpClasses.MeleeMapFor(wargear) is { ClipSpeed: var k } && float.IsFinite(k) && k > 0f ? k : 1f;
        rate = speed;
    }
}
