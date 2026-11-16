namespace SmSim;

/// <summary>
/// sim/jump-escape (protocol 62; the owner, 2026-11-06: "assault marine/raptor in all game modes should be able to jump out of any animation. basically, cancel any
/// animation thats currently playing when the assault marine/raptor presses jump", and "... in classic pvp mode should be able to jump out of all stuns or loss of
/// control effects or animations"). For the jump-pack classes (a jump pack with the dash: the Assault Marine and the Raptor), in every mode, a fresh Jump press ends
/// at once, in the press tick, before anything else of the tick runs:
/// <list type="bullet">
/// <item>any hit reaction (the stun, the stagger, the light interrupt), knockback included, at any time of its clip (<see cref="Reaction.Cleared"/>);</item>
/// <item>any melee attack (light, heavy, any step) at any time of its clip, not only from the data's <c>BRANCH_Evade</c> (<see cref="Combat"/>: <c>meleeJumpBranch</c>; the attack
/// is kept when neither the take-off nor a dash takes the press, as before);</item>
/// <item>a grenade or item throw (before its release nothing is spent, as a ground pound's cancel);</item>
/// <item>any phase of the charge (bull rush, its collision stop, the dash attack and its hit, wall stop and run-out; <see cref="MeleeCancel.Apply"/>);</item>
/// <item>the jump-pack landing's locks and the ground pound's or slash's recovery (<see cref="JumpPack.Begin"/>: a press there starts the take-off at once).</item>
/// </list>
/// What the press then starts is the pack's usual rules: on the ground a tap is the hop, a hold the jump; in the air Classic PvP gets the hop alone (sim/air-jumps).
/// The hop itself (a dash in flight) is not cut: its own spacing (the data's <c>min_time_between_activations</c>) still applies. Which states count as "animations" is ours.
/// </summary>
public static class JumpEscape
{
    /// <summary>Whether the player's class jumps out of everything: a jump pack with the dash (Assault Marine, Raptor).</summary>
    public static bool Eligible(SimConfig c, PlayerState s) =>
        s.Alive && Loadouts.JumpPack(c, s) is not null && Loadouts.Evade(c, s) is { Dash: true };

    /// <summary>The state with what a fresh Jump press ends this tick (the reaction and the throw); unchanged when not eligible or no press.</summary>
    internal static PlayerState Apply(PlayerState s, SimConfig c, bool jumpPress)
    {
        if (!jumpPress || !Eligible(c, s)) return s;
        if (s.Reaction != ReactionKind.None) s = Reaction.Cleared(s);
        if (s.Throwing) s = s with { Throwing = false, ThrowTime = 0f };
        return s;
    }
}
