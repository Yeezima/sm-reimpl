namespace SmSim;

/// <summary>
/// The action a buffered press asks for (feel/buffer-speed; <see cref="InputBuffer"/>). Every record: none; the own record carries it (protocol 33,
/// 3 bits of the thrown-equipment flag byte).
/// </summary>
public enum BufferedAction : byte
{
    None = 0,
    /// <summary><see cref="InputButtons.Jump"/>: an evade (roll, hop), a jump-pack take-off (tap: the dash, hold: the jump).</summary>
    Jump = 1,
    /// <summary><see cref="InputButtons.Melee"/>: a light attack, a combo step, the dash attack, the slash or the pound.</summary>
    Melee = 2,
    /// <summary><see cref="InputButtons.MeleeHeavy"/>: the stun strike, a heavy chain, the charge (with W held; protocol 39).</summary>
    MeleeHeavy = 3,
    /// <summary><see cref="InputButtons.Grenade"/>: a throw.</summary>
    Grenade = 4,
    /// <summary>A weapon switch: <see cref="InputButtons.Switch"/>, or a slot press naming the holstered weapon (replayed as a Switch press).</summary>
    Switch = 5,
    /// <summary><see cref="InputButtons.Reload"/>: a reload, a brace change, a vent, a remote detonation.</summary>
    Reload = 6,
    /// <summary>
    /// feel/jump-pack-2 (protocol 38): a buffered <see cref="Jump"/> of a jump-pack class with the three jumps whose button was held past the dash's
    /// decision length (<see cref="InputBuffer.LongPressTicks"/>): replayed, it takes off committed to (at least) the small jump, held or not.
    /// </summary>
    JumpLong = 7,
}

/// <summary>
/// Input buffering (feel/buffer-speed; the owner's request of 2026-10-29: "when approaching the end of any animation the player should be able to
/// buffer the next inputs", a **deliberate change from the original** whose own buffering is not in the data; every rule and time below is
/// **ours**). Shared by the server and the client's prediction (pure; integer ticks so the state round-trips exactly through the own record).
/// <para>
/// Rules. A press edge of Jump, Melee, heavy Melee, Grenade, Switch (or a slot key naming the holstered weapon) or Reload that does not start its
/// action this tick (the step's started actions), made while the player is held by another action or recovery (<see cref="Locked"/>: a melee step, a throw,
/// a switch, an evade, a reaction, a charge or dash attack, any jump-pack phase from the take-off to the landing and pound or slash recoveries, a
/// brace transition, a pending charged shot or a raise; for Jump also the evade's spacing and being in the air; for a grenade its cooldown), is
/// kept (<see cref="PlayerState.Buffered"/>) and replayed as a fresh press on every later tick until its action starts (it is then gone), for
/// <see cref="SimConfig.InputBufferTicks"/> ticks after the press (the "press" window, <see cref="SimConfig.InputBufferWindow"/>: 0.3 s, 9 ticks at
/// 30 Hz), and for Jump, Melee and heavy Melee beyond that for as long as its button stays held (the "hold" rule, <see cref="HoldKeeps"/>: holding
/// one of them through a recovery acts on the first allowed tick however long it lasts; letting go after the window cancels it, letting go inside
/// it does not). Grenade, Switch and Reload have the window only; during a hit reaction every button has the window only (a held button does not
/// break out of a stun; a press in its last 0.3 s does); so does Jump in the air for a class without a jump pack (no roll on landing from a held
/// Space). The review of 2026-10-29 narrowed the hold rule to this (ours). One buffered action at a time: a newer
/// press of any of these buttons replaces it (and is itself buffered only when blocked). A replayed press that meets a player no longer held by
/// anything is spent like a normal press (whatever it did; a pointless Reload with a full clip does nothing and is dropped). Death and respawn
/// clear it. Fire is not buffered: the trigger is a held control (full-automatic and charge weapons already fire on the first allowed tick while
/// held), and a click would replace a held Space or melee press.
/// </para>
/// <para>
/// The replay sets the button for the tick and clears its held flag (so every rule sees a press edge); the held flags after the tick are the
/// real buttons'. A Jump press during a jump-pack take-off or ascent is not buffered, and a buffered Jump is not replayed there: the held Jump
/// button is the hold in those phases (the dash or jump decision and the small jump's cut; feel/jump-pack-2). feel/jump-pack-2 (the review, ours): a kept Jump of a jump-pack class
/// with the three jumps whose button is still down on the dash's decision tick after its press becomes <see cref="BufferedAction.JumpLong"/>: kept while held and
/// for a new window from its release, it takes off committed to the small jump (<see cref="JumpPack.Begin"/>'s <c>longJump</c>). The data's own windows still decide when the action is allowed (combo
/// windows, branch times, locks): the buffer only keeps the press until they open.
/// </para>
/// </summary>
public static class InputBuffer
{
    /// <summary>Largest <see cref="PlayerState.BufferTicks"/> and <see cref="SimConfig.InputBufferTicks"/> (4 bits in the own record, protocol 33).</summary>
    public const int MaxTicks = 15;

    /// <summary>The buttons a buffered action replays.</summary>
    public static InputButtons Button(BufferedAction a) => a switch
    {
        BufferedAction.Jump or BufferedAction.JumpLong => InputButtons.Jump,
        BufferedAction.Melee => InputButtons.Melee,
        BufferedAction.MeleeHeavy => InputButtons.MeleeHeavy,
        BufferedAction.Grenade => InputButtons.Grenade,
        BufferedAction.Switch => InputButtons.Switch,
        BufferedAction.Reload => InputButtons.Reload,
        _ => InputButtons.None,
    };

    /// <summary>Bit of <paramref name="a"/> in a used-actions mask (<see cref="Combat.StepPlayer"/>'s report).</summary>
    internal static int Bit(BufferedAction a) => 1 << (int)(a == BufferedAction.JumpLong ? BufferedAction.Jump : a);

    /// <summary>Whether the real button of <paramref name="a"/> is down in <paramref name="buttons"/> (Switch: the toggle or either slot key).</summary>
    public static bool Held(BufferedAction a, InputButtons buttons) => a == BufferedAction.Switch
        ? (buttons & (InputButtons.Switch | InputButtons.SlotPistol | InputButtons.SlotPrimary | InputButtons.SlotSecond)) != 0
        : a != BufferedAction.None && (buttons & Button(a)) != 0;

    /// <summary>
    /// This tick's newest press of a bufferable button (press edges against the state's held flags), or None. Several in one tick: the first of
    /// heavy melee, melee, Jump, Grenade, Switch, Reload (ours). A slot key counts only when it names the holstered weapon (<see cref="Combat.SwitchRequested"/>).
    /// </summary>
    public static BufferedAction Pressed(PlayerState s, InputButtons b, SimConfig c)
    {
        bool Edge(InputButtons button, bool held) => (b & button) != 0 && !held;
        if (Edge(InputButtons.MeleeHeavy, s.MeleeHeavyHeld)) return BufferedAction.MeleeHeavy;
        if (Edge(InputButtons.Melee, s.MeleeHeld)) return BufferedAction.Melee;
        if (Edge(InputButtons.Jump, s.JumpHeld)) return BufferedAction.Jump;
        if (Edge(InputButtons.Grenade, s.GrenadeHeld)) return BufferedAction.Grenade;
        if (Combat.SwitchRequested(c, s, Edge(InputButtons.Switch, s.SwitchHeld), Edge(InputButtons.SlotPistol, s.SlotPistolHeld), Edge(InputButtons.SlotPrimary, s.SlotPrimaryHeld), Edge(InputButtons.SlotSecond, s.SlotSecondHeld)))
            return BufferedAction.Switch;
        if (Edge(InputButtons.Reload, s.ReloadHeld)) return BufferedAction.Reload;
        return BufferedAction.None;
    }

    /// <summary>
    /// Whether the player at a tick's start is held by an action or recovery that keeps a press of <paramref name="a"/> from acting (see the class
    /// summary). Not alive: false (nothing is buffered for the dead).
    /// </summary>
    public static bool Locked(PlayerState s, BufferedAction a)
    {
        if (!s.Alive || a == BufferedAction.None) return false;
        if (s.MeleeStep > 0 || s.Throwing || s.SwitchLeft > 0f || s.EvadeTicks > 0 || s.Reaction != ReactionKind.None || s.Rush != RushPhase.None
            || s.Jump != JumpPhase.None || s.Brace is BracePhase.Entering or BracePhase.Exiting || s.PendingShot > 0 || s.RaiseTicks > 0)
            return true;
        // sim/combat-pass (owner's follow-up of 2026-10-30): a heavy press that cannot charge yet (the charge's spacing, or in the air) is kept for the window; one that starts
        // anything is spent (a strike without W, the charge itself), so this changes nothing for F without W.
        if (a == BufferedAction.MeleeHeavy && (s.RushLock > 0 || !s.Grounded)) return true;
        return a switch
        {
            BufferedAction.Jump or BufferedAction.JumpLong => s.EvadeLock > 0 || !s.Grounded,
            BufferedAction.Grenade => s.GrenadeCooldown > 0f,
            _ => false,
        };
    }

    /// <summary>
    /// Whether a press of <paramref name="a"/> is part of a control held in this state rather than an action to buffer: Jump in a jump-pack take-off or
    /// ascent (the hold and the small jump's cut read the button there; feel/jump-pack-2). Such a press is never buffered.
    /// </summary>
    static bool Hold(PlayerState s, BufferedAction a) => a is BufferedAction.Jump or BufferedAction.JumpLong && s.Jump is JumpPhase.TakeOff or JumpPhase.Ascent;

    /// <summary>
    /// Whether a buffered press of <paramref name="a"/> past its window is still kept while its button is held in state <paramref name="s"/> (the hold
    /// rule; the review of 2026-10-29, ours): only Jump, Melee and heavy Melee; never during a hit reaction; Jump not in the air for a class without a
    /// jump pack.
    /// </summary>
    public static bool HoldKeeps(PlayerState s, BufferedAction a, SimConfig c) =>
        a is BufferedAction.Jump or BufferedAction.JumpLong or BufferedAction.Melee or BufferedAction.MeleeHeavy && s.Reaction == ReactionKind.None
        && !(a == BufferedAction.Jump && !s.Grounded && Loadouts.JumpPack(c, s) is null);

    /// <summary>
    /// feel/jump-pack-2: ticks after the press (the press tick counting 0) Jump must still be down for a buffered press to become <see cref="BufferedAction.JumpLong"/>
    /// (the dash's decision tick: down on it is a jump, <see cref="JumpPack.TierTicks"/>); -1 when the player's class has no jump pack with the three jumps and a dash.
    /// </summary>
    public static int LongPressTicks(PlayerState s, SimConfig c) =>
        Loadouts.JumpPack(c, s) is { HoldTiers: true } pack && Loadouts.Evade(c, s) is { Dash: true } evade ? JumpPack.DecisionTick(pack, evade.ToJumpAt, c.TickSeconds) : -1;

    /// <summary>
    /// The state and input a tick of <see cref="Combat.StepPlayer"/> runs its rules on (the buffered press replayed: its button set, its held flag
    /// cleared; else unchanged). feel/melee-lock's client choice (<c>MeleeLock.Wants</c>, <c>Choose</c>) looks at these, so a replayed melee press
    /// picks or keeps its target like a fresh press.
    /// </summary>
    public static (PlayerState State, PlayerInput Input) Replayed(PlayerState s, PlayerInput input, SimConfig c)
    {
        var t = Begin(s, input, c);
        return (t.State, t.Input);
    }

    /// <summary>The start of a tick: what was pressed, whether the buffered press is replayed, and the state and input the tick runs on.</summary>
    internal readonly record struct Tick(BufferedAction Fresh, BufferedAction Replayed, PlayerState State, PlayerInput Input);

    /// <summary>
    /// The start of a tick (before <see cref="Combat.StepPlayer"/>'s rules): the newest real press, and the buffered press replayed when no other
    /// bufferable button was pressed this tick (a jump-pack take-off or ascent never replays Jump). Off (<see cref="SimConfig.InputBufferTicks"/> 0)
    /// or dead: the tick unchanged.
    /// </summary>
    internal static Tick Begin(PlayerState s, PlayerInput input, SimConfig c)
    {
        if (c.InputBufferTicks <= 0 || !s.Alive) return new(BufferedAction.None, BufferedAction.None, s, input);
        var fresh = Pressed(s, input.Buttons, c);
        var carried = s.Buffered;
        if (carried == BufferedAction.None || fresh != BufferedAction.None) return new(fresh, BufferedAction.None, s, input);
        if (Hold(s, carried)) return new(fresh, BufferedAction.None, s, input);
        var primed = carried switch
        {
            BufferedAction.Jump or BufferedAction.JumpLong => s with { JumpHeld = false },
            BufferedAction.Melee => s with { MeleeHeld = false },
            BufferedAction.MeleeHeavy => s with { MeleeHeavyHeld = false },
            BufferedAction.Grenade => s with { GrenadeHeld = false },
            BufferedAction.Switch => s with { SwitchHeld = false, SlotPistolHeld = false, SlotPrimaryHeld = false, SlotSecondHeld = false },
            BufferedAction.Reload => s with { ReloadHeld = false, DetonateHeld = false },
            _ => s,
        };
        // A Switch replays as the toggle alone (a slot key held for it is dropped this tick: the toggle names the same weapon). Protocol 44: the key-3 slot stays,
        // since the toggle does not name the second primary (a key 3 let go before the replay replays as the toggle: an estimate).
        var buttons = carried == BufferedAction.Switch ? input.Buttons & ~(InputButtons.SlotPistol | InputButtons.SlotPrimary) : input.Buttons;
        return new(fresh, carried, primed, input with { Buttons = buttons | Button(carried) });
    }

    /// <summary>
    /// The end of a tick: the buffer after it (see the class summary) and the held flags of a replayed button set back to the real input's.
    /// <paramref name="before"/> is the state at the tick's start, <paramref name="real"/> the real input, <paramref name="used"/> the actions
    /// that started this tick (bits of <see cref="Bit"/>).
    /// </summary>
    internal static PlayerState End(PlayerState before, PlayerInput real, PlayerState next, Tick t, int used, SimConfig c)
    {
        if (t.Replayed != BufferedAction.None)
        {
            var b = real.Buttons;
            next = t.Replayed switch
            {
                BufferedAction.Jump or BufferedAction.JumpLong => next with { JumpHeld = (b & InputButtons.Jump) != 0 },
                BufferedAction.Melee => next with { MeleeHeld = (b & InputButtons.Melee) != 0 },
                BufferedAction.MeleeHeavy => next with { MeleeHeavyHeld = (b & InputButtons.MeleeHeavy) != 0 },
                BufferedAction.Grenade => next with { GrenadeHeld = (b & InputButtons.Grenade) != 0 },
                BufferedAction.Switch => next with
                {
                    SwitchHeld = (b & InputButtons.Switch) != 0, SlotPistolHeld = (b & InputButtons.SlotPistol) != 0, SlotPrimaryHeld = (b & InputButtons.SlotPrimary) != 0,
                    SlotSecondHeld = (b & InputButtons.SlotSecond) != 0,
                },
                // A replayed Reload that detonated launcher grenades keeps its "no reload until released" latch while R is really down (review fix).
                BufferedAction.Reload => next with { ReloadHeld = (b & InputButtons.Reload) != 0, DetonateHeld = next.DetonateHeld && (b & InputButtons.Reload) != 0 },
                _ => next,
            };
        }
        var (action, ticks) = After(before, real.Buttons, t, used, c.InputBufferTicks, c);
        return next.Alive ? next with { Buffered = action, BufferTicks = ticks } : next with { Buffered = BufferedAction.None, BufferTicks = 0 };
    }

    /// <summary>The buffer after a tick (pure rule; see the class summary).</summary>
    internal static (BufferedAction Action, int Ticks) After(PlayerState before, InputButtons real, Tick t, int used, int window, SimConfig c)
    {
        if (window <= 0 || !before.Alive) return (BufferedAction.None, 0);
        window = Math.Min(window, MaxTicks);
        if (t.Fresh != BufferedAction.None)
            return (used & Bit(t.Fresh)) == 0 && Locked(before, t.Fresh) && !Hold(before, t.Fresh) ? (t.Fresh, window) : (BufferedAction.None, 0);
        var carried = before.Buffered;
        if (carried == BufferedAction.None) return (BufferedAction.None, 0);
        if (t.Replayed != BufferedAction.None && ((used & Bit(carried)) != 0 || !Locked(before, carried))) return (BufferedAction.None, 0);
        int left = Math.Max(0, Math.Min(before.BufferTicks, window) - 1);
        bool held = Held(carried, real);
        // feel/jump-pack-2: a long press is kept while held and for a new window from its release (so a hold let go before the lock ends still jumps).
        if (carried == BufferedAction.JumpLong)
            return held ? (HoldKeeps(before, carried, c) ? (carried, left) : (BufferedAction.None, 0)) : before.JumpHeld ? (carried, window) : left > 0 ? (carried, left) : (BufferedAction.None, 0);
        // feel/jump-pack-2: a buffered Jump still down on the dash's decision tick after its press (window − left ticks after it) becomes a long press.
        if (carried == BufferedAction.Jump && held && LongPressTicks(before, c) is int d and > 0 && window - left >= d) return (BufferedAction.JumpLong, left);
        return left > 0 || (held && HoldKeeps(before, carried, c)) ? (carried, left) : (BufferedAction.None, 0);
    }
}
