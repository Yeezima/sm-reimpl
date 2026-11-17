namespace SmSim;

/// <summary>
/// sim/lascannon-shot-first (owner request 2026-11-17, Classic PvP only; no wire change): "its currently too easy for a lascannon shot not to go off if a hop move is initiated immediately
/// after the shot. if a player shoots it should be prioritized to go off and finish shooting before the hop initiates, but don't delay the hop by more than .1 seconds."
/// <para>
/// The cause: a hop (the Devastator's evade) that starts on the tick of the Fire press, or while the weapon's raise still waits for its shot, lowers the weapon in that same tick
/// (<c>evading</c> takes the weapon branch, a raise is dropped) so the shot never leaves. Now a Jump press (fresh, or replayed from the input buffer) that would start the hop on such a tick
/// is held back: the press is kept in the input buffer, the shot leaves, and the hop starts on a later tick, at most <see cref="MaxHoldSeconds"/> after the press (3 ticks at 30 Hz). A hop with
/// no shot pending is never held. Only the lascannon, grounded, with a plain hop (no jump pack); Musou Mode never.
/// </para>
/// </summary>
public static class LascannonShotFirst
{
    /// <summary>The longest the hop waits for the shot (the owner's number).</summary>
    public const float MaxHoldSeconds = 0.1f;

    /// <summary>Ticks of <see cref="MaxHoldSeconds"/> (3 at 30 Hz).</summary>
    public static int HoldTicks(SimConfig c) => (int)MathF.Floor(MaxHoldSeconds / c.TickSeconds + 1e-3f);

    /// <summary>
    /// Whether the tick holds the hop back. <paramref name="t"/> is the input buffer's tick (state and input after the replay), <paramref name="input"/> the input the tick runs on.
    /// </summary>
    internal static bool Holds(SimConfig c, InputBuffer.Tick t, PlayerInput input)
    {
        var s = t.State;
        if (!c.LascannonShotFirst || c.InputBufferTicks <= 0 || c.Extras.HasFlag(ContentExtras.Musou) || !s.Alive || !s.Grounded) return false;
        // The press must be a Jump press the buffer can keep: this tick's fresh press, or the buffered one replayed.
        bool jumpPress = (input.Buttons & InputButtons.Jump) != 0 && !s.JumpHeld;
        if (!jumpPress || !(t.Fresh == BufferedAction.Jump || (t.Fresh == BufferedAction.None && t.Replayed == BufferedAction.Jump))) return false;
        if (Loadouts.InHand(c, s) is not { } weapon || !LascannonTapTuning.IsLascannon(weapon)) return false;
        if (Loadouts.JumpPack(c, s) is not null || Loadouts.Evade(c, s) is not { Dash: false }) return false;
        // The hop must be able to start now (otherwise there is nothing to hold back), nothing else is holding the body.
        if (s.EvadeTicks > 0 || s.EvadeLock > 1 || s.MeleeStep > 0 || s.Throwing || s.Rush != RushPhase.None || s.Jump != JumpPhase.None || Reaction.Holds(s.Reaction)
            || s.Brace is BracePhase.Entering or BracePhase.Exiting) return false;
        // Age of the press in ticks: 0 when fresh; a replay is the buffer's age (the window minus what is left, plus the tick that kept it).
        int window = Math.Min(c.InputBufferTicks, InputBuffer.MaxTicks);
        int age = t.Replayed == BufferedAction.None ? 0 : window - s.BufferTicks + 1;
        if (age >= HoldTicks(c)) return false;
        // A shot is due: a raise is still bringing the weapon up for its shot, or the press is fresh and the weapon would fire.
        float dt = c.TickSeconds;
        if (s.RaiseTicks > 0) return true;
        bool firePress = (input.Buttons & InputButtons.Fire) != 0 && !s.FireHeld;
        bool sprinting = (input.Buttons & InputButtons.Sprint) != 0 && input.MoveY > 0f;   // the sprint takes the trigger (no shot to wait for)
        return firePress && !sprinting && s.SwitchLeft - dt <= 1e-5f && s.Clip > 0 && !s.Overheated && s.ReloadLeft <= 1e-6f && s.Cooldown - dt <= 1e-5f;
    }
}

public sealed partial record SimConfig
{
    /// <summary>sim/lascannon-shot-first (owner 2026-11-17): in Classic PvP a hop pressed with a lascannon shot due waits (at most 0.1 s) until the shot has left. False (restore switch <c>--old-lascannon-shot-first</c> on the server and every client; config hash): the hop starts at once and may swallow the shot. Hashed only when false.</summary>
    public bool LascannonShotFirst { get; init; } = true;
}
