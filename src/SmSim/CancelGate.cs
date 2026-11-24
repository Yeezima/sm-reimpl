using System.Numerics;

namespace SmSim;

/// <summary>Row indices of the cancel matrix the gate needs by name (looked up once from the baseline file's ids).</summary>
static class CancelRow
{
    static int R(string id) { int i = CancelLayout.RowIndex(id); return i >= 0 ? i : throw new InvalidOperationException("cancel matrix row missing: " + id); }
    public static readonly int MeleeLight = R("melee_light"), MeleeLightLast = R("melee_light_last"), MeleeHeavy = R("melee_heavy"), MeleeStomp = R("melee_stomp"),
        Charge = R("charge"), ChargeAir = R("charge_air"), ChargeStop = R("charge_stop"), DashAttack = R("dash_attack"), EvadeRoll = R("evade_roll"), EvadeHop = R("evade_hop"),
        JpTakeoff = R("jp_takeoff"), JpHop = R("jp_hop"), JpFlight = R("jp_flight"), JpDive = R("jp_dive"), JpRecover = R("jp_recover"), JpLanded = R("jp_landed"),
        HipFire = R("hip_fire"), AimFire = R("aim_fire"), ChargedShot = R("charged_shot"), Reload = R("reload"), Switch = R("switch"), Raise = R("raise"), Grenade = R("grenade"),
        Stim = R("stim"), Sprint = R("sprint"), SprintStop = R("sprint_stop"), BraceEnter = R("brace_enter"), Braced = R("braced"), BraceExit = R("brace_exit"), Vent = R("vent"),
        ReactInterrupt = R("react_interrupt"), ReactStun = R("react_stun"), ReactStagger = R("react_stagger"),
        MeleeStab = R(CancelData.StabRowId);   // sim/stab-key-only (owner 2026-11-23): the Power Sword Stab section's row
}

/// <summary>Column indices of the cancel matrix (inputs), by id.</summary>
static class CancelCol
{
    static int C(string id) { int i = CancelLayout.ColIndex(id); return i >= 0 ? i : throw new InvalidOperationException("cancel matrix column missing: " + id); }
    public static readonly int Light = C("light"), Heavy = C("heavy"), Charge = C("charge"), Roll = C("roll"), Pack = C("pack"), Fire = C("fire"), Aim = C("aim"), Reload = C("reload"),
        Switch = C("switch"), Grenade = C("grenade"), Sprint = C("sprint"), Stick = C("stick"), Back = C("back"), HitReact = C("hit_react"), HitPlain = C("hit_plain"),
        Stab = C(CancelData.StabColId);   // sim/stab-key-only: the Power Sword Stab section's column (the Sword Stab key)
}

/// <summary>
/// cancel-editor (owner, 2026-11-17): the gate between the player's input and the simulation's own cancel rules, driven by the table (<see cref="CancelData"/>). It looks at the action the player is
/// in (<see cref="ActiveRow"/>) and at each input this tick, finds the cell the table gives this class and weapon, and acts only when that cell is not what the code does (the baseline): so
/// with the default table the gate returns at its first line and the simulation runs exactly as before. An edited cell then (a) Yes: ends the action in progress and lets the input start its
/// own; (b) No: drops the input (its button stays "held" so it does not fire the moment the key is let go and pressed again); (c) Window: Yes inside from-until seconds of the action, No outside;
/// (d) Queued: drops the input now and keeps it in the input buffer (<see cref="InputBuffer"/>) so it starts when the action ends. Fire, Aim, Sprint and the stick are held inputs: they are
/// tested every tick they are down; Queued on them acts as No. Only Classic PvP (any content profile, the default extended included); never Musou Mode. Shared by the server and the client's prediction (pure).
/// </summary>
public static class CancelGate
{
    /// <summary>What the gate does with this tick: the buttons to drop, whether the stick is dropped, the presses to queue, the presses to ignore, and whether the action in progress ends.</summary>
    internal sealed class Plan
    {
        public InputButtons Drop;
        public bool ZeroStick;
        public int Queue, Block;   // bits of InputBuffer.Bit
        public int EndRow = -1;

        /// <summary>The state before the tick's rules: the action in progress ended when a cell said Yes.</summary>
        public PlayerState Begin(PlayerState s, SimConfig c) => EndRow >= 0 ? EndAction(s, c, EndRow) : s;

        /// <summary>The input the tick's rules see: the dropped buttons and stick removed.</summary>
        public PlayerInput Filter(PlayerInput input)
        {
            if (Drop != InputButtons.None) input = input with { Buttons = input.Buttons & ~Drop };
            if (ZeroStick) input = input with { MoveX = 0f, MoveY = 0f };
            return input;
        }

        /// <summary>The state after the tick: a dropped button's "held" flag follows the real button, so a blocked press is not taken for a fresh one while the key stays down.</summary>
        public PlayerState Restore(PlayerState next, PlayerInput real)
        {
            if (Drop == InputButtons.None) return next;
            var b = real.Buttons;
            if ((Drop & InputButtons.Melee) != 0) next = next with { MeleeHeld = (b & InputButtons.Melee) != 0 };
            if ((Drop & InputButtons.MeleeHeavy) != 0) next = next with { MeleeHeavyHeld = (b & InputButtons.MeleeHeavy) != 0 };
            if ((Drop & InputButtons.Jump) != 0) next = next with { JumpHeld = (b & InputButtons.Jump) != 0 };
            if ((Drop & InputButtons.Grenade) != 0) next = next with { GrenadeHeld = (b & InputButtons.Grenade) != 0 };
            if ((Drop & InputButtons.Reload) != 0) next = next with { ReloadHeld = (b & InputButtons.Reload) != 0 };
            if ((Drop & InputButtons.Fire) != 0) next = next with { FireHeld = (b & InputButtons.Fire) != 0 };
            if ((Drop & (InputButtons.Switch | InputButtons.SlotPistol | InputButtons.SlotPrimary | InputButtons.SlotSecond)) != 0)
                next = next with { SwitchHeld = (b & InputButtons.Switch) != 0, SlotPistolHeld = (b & InputButtons.SlotPistol) != 0, SlotPrimaryHeld = (b & InputButtons.SlotPrimary) != 0, SlotSecondHeld = (b & InputButtons.SlotSecond) != 0 };
            return next;
        }
    }

    /// <summary>Whether the gate can do anything for this config: the table in force differs from the code's behaviour and the mode is Classic PvP. One property read and one flag test when it cannot.</summary>
    public static bool Active(SimConfig c) => c.Cancel.Active.DiffersFromBaseline && (c.Extras & ContentExtras.Musou) == 0;   // Classic PvP with any content profile (the default is extended); never Musou (owner 2026-11-17: the edits were not felt in a default match)

    /// <summary>Whether the gate would do anything with this input now (the plan is not empty): false for every state and input with the default table.</summary>
    public static bool Acts(PlayerState s, PlayerInput input, SimConfig c) => Evaluate(s, input, c) is not null;

    /// <summary>The plan for this tick, or null when nothing is edited for what the player is doing (the usual case, and always with the default table).</summary>
    internal static Plan? Evaluate(PlayerState s, PlayerInput input, SimConfig c)
    {
        if (!s.Alive || !Active(c)) return null;
        int row = ActiveRow(c, s, input);
        if (row < 0) return null;
        var table = c.Cancel.Active;
        string? cls = Loadouts.ClassOf(c, s.Class)?.Name, weapon = Loadouts.Weapon(c, s, s.Slot)?.Name;
        int elapsed = ElapsedMs(c, s, row);
        Plan? plan = null;

        void Consider(int col, InputButtons button, BufferedAction action, bool back = false, bool stick = false)
        {
            if (!table.EditedFor(row, col, cls, weapon, out var cell) || cell.State is CancelState.NotApplicable or CancelState.Inherit) return;
            bool force, queue = false;
            switch (cell.State)
            {
                case CancelState.Yes: force = true; break;
                case CancelState.Window:
                    force = elapsed < 0 || ((cell.FromMs == CancelCell.Natural || elapsed >= cell.FromMs) && (cell.UntilMs == CancelCell.Natural || elapsed <= cell.UntilMs));
                    break;
                case CancelState.Queued: force = false; queue = action != BufferedAction.None; break;
                default: force = false; break;
            }
            plan ??= new Plan();
            if (force) { plan.EndRow = row; return; }
            plan.Drop |= button;
            if (stick || back) plan.ZeroStick = true;
            if (action != BufferedAction.None) { if (queue) plan.Queue |= InputBuffer.Bit(action); else plan.Block |= InputBuffer.Bit(action); }
        }

        var b = input.Buttons;
        bool Edge(InputButtons button, bool held) => (b & button) != 0 && !held;
        var rush = Loadouts.Rush(c, s);
        bool chargeHeld = rush is { } spec && Rush.ChargeHeld(input, spec);
        bool heavyEdge = Edge(InputButtons.MeleeHeavy, s.MeleeHeavyHeld), lightEdge = Edge(InputButtons.Melee, s.MeleeHeld);
        // The charge: F with Shift and W (a heavy press), or RMB with them held and the sprint-melee rule (Classic PvP): not a light or a heavy press.
        bool chargePress = chargeHeld && (heavyEdge || (lightEdge && c.SprintMeleeCharge));
        if (chargePress) Consider(CancelCol.Charge, heavyEdge ? InputButtons.MeleeHeavy : InputButtons.Melee, heavyEdge ? BufferedAction.MeleeHeavy : BufferedAction.Melee);
        else
        {
            if (heavyEdge) Consider(CancelCol.Heavy, InputButtons.MeleeHeavy, BufferedAction.MeleeHeavy);
            if (lightEdge) Consider(CancelCol.Light, InputButtons.Melee, BufferedAction.Melee);
        }
        if (Edge(InputButtons.Jump, s.JumpHeld)) Consider(Loadouts.JumpPack(c, s) is not null ? CancelCol.Pack : CancelCol.Roll, InputButtons.Jump, BufferedAction.Jump);
        if (Edge(InputButtons.Reload, s.ReloadHeld)) Consider(CancelCol.Reload, InputButtons.Reload, BufferedAction.Reload);
        if (Edge(InputButtons.Grenade, s.GrenadeHeld)) Consider(CancelCol.Grenade, InputButtons.Grenade, BufferedAction.Grenade);
        var switchButtons = InputButtons.Switch | InputButtons.SlotPistol | InputButtons.SlotPrimary | InputButtons.SlotSecond;
        if (InputBuffer.Pressed(s, b & switchButtons, c) == BufferedAction.Switch) Consider(CancelCol.Switch, switchButtons, BufferedAction.Switch);
        // sim/stab-key-only (owner 2026-11-23): the Sword Stab key. The client sends its bit as a short pulse (StabPulse), so it is weighed every tick it is set, like a held input; it never enters the
        // input buffer, so Queued acts as No. Only a weapon with a stab has a press to weigh.
        if ((b & InputButtons.Stab) != 0 && Combat.StabIndex(Loadouts.MeleeHeavy(c, s)) > 0) Consider(CancelCol.Stab, InputButtons.Stab, BufferedAction.None);
        if ((b & InputButtons.Fire) != 0) Consider(CancelCol.Fire, InputButtons.Fire, BufferedAction.None);
        if ((b & InputButtons.Aim) != 0) Consider(CancelCol.Aim, InputButtons.Aim, BufferedAction.None);
        if ((b & InputButtons.Sprint) != 0) Consider(CancelCol.Sprint, InputButtons.Sprint, BufferedAction.None);
        if (input.MoveX != 0f || input.MoveY != 0f)
        {
            if (ChargeCancel.BackHeld(input)) Consider(CancelCol.Back, InputButtons.None, BufferedAction.None, back: true);
            else Consider(CancelCol.Stick, InputButtons.None, BufferedAction.None, stick: true);
        }
        return plan;
    }

    /// <summary>
    /// The matrix row of the action the player is in at the start of a tick (the first that applies, in this order: a hit reaction, a charge phase, a jump-pack phase, an evade, a melee attack (the power sword's stab its own row), a brace,
    /// a throw, a vent, a switch, a reload, a charged shot, a raise, a sprint stop, the trigger held, a sprint), or -1 for none (nothing is in progress, so every input is free).
    /// </summary>
    public static int ActiveRow(SimConfig c, PlayerState s, PlayerInput input)
    {
        if (s.Reaction != ReactionKind.None) return s.Reaction == ReactionKind.Interrupt ? CancelRow.ReactInterrupt : s.Reaction == ReactionKind.Stun ? CancelRow.ReactStun : CancelRow.ReactStagger;
        switch (s.Rush)
        {
            case RushPhase.Rush: return s.Grounded ? CancelRow.Charge : CancelRow.ChargeAir;
            case RushPhase.DashAttack or RushPhase.DashHit: return CancelRow.DashAttack;
            case RushPhase.Collision or RushPhase.DashStop or RushPhase.DashCollision: return CancelRow.ChargeStop;
        }
        switch (s.Jump)
        {
            case JumpPhase.TakeOff: return CancelRow.JpTakeoff;
            case JumpPhase.Ascent or JumpPhase.Hang or JumpPhase.Fall: return CancelRow.JpFlight;
            case JumpPhase.Dive: return CancelRow.JpDive;
            case JumpPhase.Recover: return CancelRow.JpRecover;
            case JumpPhase.Landed: return CancelRow.JpLanded;
        }
        if (s.EvadeTicks > 0) return Loadouts.Evade(c, s) is { Dash: true } ? CancelRow.JpHop : Combat.IsRoll(Loadouts.Evade(c, s)) ? CancelRow.EvadeRoll : CancelRow.EvadeHop;
        if (s.MeleeStep > 0)
        {
            if (Combat.MeleeStepOf(c, s) is { } m) return m.InPlace ? CancelRow.MeleeStomp : s.MeleeHeavy && m.Stab ? CancelRow.MeleeStab : s.MeleeHeavy ? CancelRow.MeleeHeavy : m.StringEnd ? CancelRow.MeleeLightLast : CancelRow.MeleeLight;
            return CancelRow.MeleeLight;
        }
        switch (s.Brace)
        {
            case BracePhase.Entering: return CancelRow.BraceEnter;
            case BracePhase.Braced: return CancelRow.Braced;
            case BracePhase.Exiting: return CancelRow.BraceExit;
        }
        if (s.Throwing) return Loadouts.Equipment(c, s)?.Name.Contains("stim", StringComparison.OrdinalIgnoreCase) == true ? CancelRow.Stim : CancelRow.Grenade;
        if (s.Venting) return CancelRow.Vent;
        if (s.SwitchLeft > 0f) return CancelRow.Switch;
        if (s.ReloadLeft > 0f) return CancelRow.Reload;
        if (s.Charging || s.PendingShot > 0) return CancelRow.ChargedShot;
        if (s.RaiseTicks > 0) return CancelRow.Raise;
        if (s.SprintStop) return CancelRow.SprintStop;
        if ((input.Buttons & InputButtons.Fire) != 0 && s.FireHeld) return s.Aiming ? CancelRow.AimFire : CancelRow.HipFire;
        if (s.Grounded && (input.Buttons & InputButtons.Sprint) != 0 && input.MoveY > 0f) return CancelRow.Sprint;
        return -1;
    }

    /// <summary>Milliseconds into the action in <paramref name="row"/>, or -1 when the state does not keep a time for it (a window then always counts).</summary>
    public static int ElapsedMs(SimConfig c, PlayerState s, int row)
    {
        float dt = c.TickSeconds;
        float seconds = row switch
        {
            _ when row == CancelRow.MeleeLight || row == CancelRow.MeleeLightLast || row == CancelRow.MeleeHeavy || row == CancelRow.MeleeStomp || row == CancelRow.MeleeStab => s.MeleeTime,
            _ when row == CancelRow.Charge || row == CancelRow.ChargeAir || row == CancelRow.ChargeStop || row == CancelRow.DashAttack => s.RushTicks * dt,
            _ when row == CancelRow.EvadeRoll || row == CancelRow.EvadeHop || row == CancelRow.JpHop => s.EvadeTicks * dt,
            _ when row == CancelRow.JpTakeoff || row == CancelRow.JpFlight || row == CancelRow.JpDive || row == CancelRow.JpRecover || row == CancelRow.JpLanded => s.JumpTicks * dt,
            _ when row == CancelRow.BraceEnter || row == CancelRow.Braced || row == CancelRow.BraceExit => s.BraceTicks * dt,
            _ when row == CancelRow.Grenade || row == CancelRow.Stim => s.ThrowTime,
            _ when row == CancelRow.ChargedShot => s.ChargeTime,
            _ when row == CancelRow.ReactInterrupt || row == CancelRow.ReactStun || row == CancelRow.ReactStagger => s.ReactionTicks * dt,
            _ when row == CancelRow.Reload => Loadouts.Weapon(c, s, s.Slot) is { } w ? MathF.Max(0f, w.ReloadTime - s.ReloadLeft) : -1f,
            _ => -1f,
        };
        return seconds < 0f || !float.IsFinite(seconds) ? -1 : (int)MathF.Round(seconds * 1000f);
    }

    /// <summary>The table's say on a hit that would start a reaction (the "hit stun / stagger" column) in the action in progress: true to react, false to resist, null when that cell is the code's own (the rules decide).</summary>
    internal static bool? ReactDecision(PlayerState s, SimConfig c)
    {
        int row = ActiveRow(c, s, default);
        if (row < 0 || !c.Cancel.Active.EditedFor(row, CancelCol.HitReact, Loadouts.ClassOf(c, s.Class)?.Name, Loadouts.Weapon(c, s, s.Slot)?.Name, out var cell)) return null;
        return InForce(cell, ElapsedMs(c, s, row)) switch { true => true, false => false, null => null };
    }

    /// <summary>Whether a cell lets the input through now: Yes always, No never, Window inside its times (always when the action keeps no time), Queued and not-applicable cells leave it to the rules (null).</summary>
    static bool? InForce(CancelCell cell, int elapsed) => cell.State switch
    {
        CancelState.Yes => true,
        CancelState.No => false,
        CancelState.Window => elapsed < 0 || ((cell.FromMs == CancelCell.Natural || elapsed >= cell.FromMs) && (cell.UntilMs == CancelCell.Natural || elapsed <= cell.UntilMs)),
        _ => null,
    };

    /// <summary>The state after a damage that started no reaction: the action in progress ended when the "hit damage" cell of this action says so (Yes, or a window it is inside).</summary>
    internal static PlayerState OnDamage(PlayerState s, SimConfig c)
    {
        int row = ActiveRow(c, s, default);
        if (row < 0 || !c.Cancel.Active.EditedFor(row, CancelCol.HitPlain, Loadouts.ClassOf(c, s.Class)?.Name, Loadouts.Weapon(c, s, s.Slot)?.Name, out var cell)) return s;
        return InForce(cell, ElapsedMs(c, s, row)) == true ? EndAction(s, c, row) : s;
    }

    /// <summary>The state with the action in <paramref name="row"/> ended (the way the data's own cancels end it): nothing of it is left to wait for, and the next action may start this tick.</summary>
    public static PlayerState EndAction(PlayerState s, SimConfig c, int row)
    {
        if (row == CancelRow.MeleeLight || row == CancelRow.MeleeLightLast || row == CancelRow.MeleeHeavy || row == CancelRow.MeleeStomp || row == CancelRow.MeleeStab)
            return s with { MeleeStep = 0, MeleeTime = 0f, MeleeQueued = false, MeleeHeavy = false, MeleeQueuedHeavy = false, MeleeLock = 0f, MeleeDirection = EvadeDirection.Forward };
        if (row == CancelRow.Charge || row == CancelRow.ChargeAir || row == CancelRow.ChargeStop || row == CancelRow.DashAttack)
            return Rush.Cleared(s) with { RushLock = 0 };
        if (row == CancelRow.EvadeRoll || row == CancelRow.EvadeHop || row == CancelRow.JpHop)
            return s with { EvadeTicks = 0, EvadeDirection = EvadeDirection.Forward, EvadeLanded = false, EvadeLock = 0 };
        if (row == CancelRow.JpTakeoff || row == CancelRow.JpFlight || row == CancelRow.JpRecover || row == CancelRow.JpLanded)
            return s with { Jump = JumpPhase.None, JumpTicks = 0 };
        if (row == CancelRow.JpDive) return s;   // the dive ends at impact: nothing ends it early (the matrix row is "not cancellable")
        if (row == CancelRow.BraceEnter || row == CancelRow.Braced || row == CancelRow.BraceExit) return s with { Brace = BracePhase.None, BraceTicks = 0 };
        if (row == CancelRow.Grenade || row == CancelRow.Stim) return s with { Throwing = false, ThrowTime = 0f };
        if (row == CancelRow.Vent) return s with { Venting = false };
        if (row == CancelRow.Switch) return s with { SwitchLeft = 0f };
        if (row == CancelRow.Reload) return s with { ReloadLeft = 0f };
        if (row == CancelRow.ChargedShot) return s with { Charging = false, ChargeTime = 0f, PendingShot = 0, PendingLeft = 0f };
        if (row == CancelRow.Raise) return s with { RaiseTicks = 0 };
        if (row == CancelRow.SprintStop) return s with { SprintStop = false };
        if (row == CancelRow.ReactInterrupt || row == CancelRow.ReactStun || row == CancelRow.ReactStagger) return Reaction.Cleared(s);
        return s;   // hip fire, aimed fire and sprint are held inputs, not actions with a state to end
    }
}
