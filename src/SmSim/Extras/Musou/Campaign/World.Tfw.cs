using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmSim;

/// <summary>
/// The Three-Front War in the World (TF0: the contract and stubs; the owner's decision of 2026-11-01: Musou mode is this war). <see cref="World.Musou"/> and its pool run a war
/// too; <see cref="_tfw"/>, set only by <see cref="StartTfw"/>, sends the World's Musou hooks here instead of the 5-wave director, which stays reachable as a test aid
/// (<c>--map mp_tdm_09</c>). Without a war every method here returns at once and the Skirmish replay is unchanged (<c>MusouSkirmishPinTests</c>).
/// </summary>
public sealed partial class World
{
    TfwRun? _tfw;

    /// <summary>The war of this match; null unless <see cref="StartTfw"/> ran (every other match, the 5-wave run included).</summary>
    public TfwRun? Tfw => _tfw;

    /// <summary>
    /// Makes this match the war (a Musou match, <see cref="StartMusou"/>, whose hooks go to the war). False, and nothing changed, under the same conditions as
    /// <see cref="StartMusou"/>; a second call keeps the first's.
    /// </summary>
    public bool StartTfw(NavGraph? nav)
    {
        if (!StartMusou(nav)) return false;
        _tfw ??= new TfwRun();
        _musou!.WidenBox(TfwMap.WireBox);   // the Stormbird lies outside the arena's graph
        _musou!.Director = default;   // no 5-wave director to show (nothing is sent; TF4 shows MusouPhase.Campaign with the run)
        return true;
    }

    /// <summary>The input of an AI Marine (<see cref="TfwRun.PuppetBrain"/>; none: it stands still, keeping its heading). Other players stay with the server's input source.</summary>
    void TfwPlayerInput(int id, ref PlayerInput input, ref bool handled)
    {
        if (!TfwContracts.IsPuppet(id) || !_players.TryGetValue(id, out var puppet)) return;
        input = _tfw!.PuppetBrain?.Invoke(id, puppet) ?? new PlayerInput(0, 0f, 0f, puppet.Yaw, InputButtons.None);
        handled = true;
    }

    /// <summary>
    /// The war's step, in place of the 5-wave step (from the <c>ExtrasStep</c> hook, inside <see cref="Step"/>, so a respawn here reaches <see cref="LastRespawns"/>). TF0: the tick's
    /// bookkeeping alone. TF2 to TF5 fill it: the war clock, the battle, the Guardsmen, the Marines; the director's view stays default until then, so nothing is sent.
    /// </summary>
    void TfwStep()
    {
        MusouBeginTick();
        MusouArm();
    }

    /// <summary>
    /// The war's match flow. False (TF0): the one-sided Musou flow runs (warm-up, countdown, play, restart). TF4 returns true once the run has its own end, which stops that flow.
    /// </summary>
    bool TfwStepMatch() => false;

    /// <summary>An Ork's attack on a Guard squad (<see cref="MusouAttack.Player"/> below 0: <see cref="TfwContracts.SquadOf"/>). TF0: nothing to hit; TF3 hits the squad's nearest member.</summary>
    void TfwGuardHit(MusouAttack attack) { }

    /// <summary>
    /// A fresh life for player <paramref name="id"/> at <paramref name="at"/>, alive or dead (the Deploy, an extraction): the core has no respawn routine to call (its respawn is inline in
    /// <see cref="Step"/>), so this does what that does: the waiting loadout change (a customizer Setup) applies, <see cref="Combat.Respawned"/> restores the player, and the tick's
    /// <see cref="LastRespawns"/> gets it so the server promotes the pending look; the next <see cref="MusouArm"/> strips it to the pistol. Call it inside the step
    /// (<see cref="TfwStep"/>): <see cref="LastRespawns"/> is cleared when a tick begins. False, and nothing changed, outside a war or for an unknown player.
    /// </summary>
    public bool TfwRespawnAt(int id, Vector3 at, float yaw)
    {
        if (_tfw is null || !_players.TryGetValue(id, out var p)) return false;
        _players[id] = Combat.Respawned(WithNextLoadout(p) with { Position = at, Yaw = yaw }, Config);
        _respawns.Add(id);
        _musouArmed.Remove(id);
        return true;
    }
}
