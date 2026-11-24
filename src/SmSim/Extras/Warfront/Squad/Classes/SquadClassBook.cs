namespace SmSim.Extras.Warfront.Squad;

/// <summary>
/// What a player's class is for the squad, as the ship's customizer confirms it: <see cref="Kind"/> Standard (a Classic class and loadout, <see cref="Choice"/>), Dreadnought (no retainers) or Librarian (the squad's
/// class is <see cref="Librarian"/>, on side <see cref="Side"/>: a Chaos Librarian's squad rolls Raptors and Havocs). <see cref="LookCode"/> is the player's appearance code (<see cref="AppearanceCode.ToArray"/>), whose colours every retainer wears; null: the default look.
/// </summary>
public sealed record SquadOwnerLoadout(SquadOwnerKind Kind, LoadoutChoice Choice, byte[]? LookCode, SquadClass Librarian, Team Side = Team.SpaceMarines)
{
    public static SquadOwnerLoadout Standard(LoadoutChoice choice, AppearanceCode look = default) => new(SquadOwnerKind.Standard, choice, look.ToArray(), SquadClass.Tactical);
    public static SquadOwnerLoadout Dreadnought() => new(SquadOwnerKind.Dreadnought, LoadoutChoice.Default, null, SquadClass.Tactical);
    public static SquadOwnerLoadout ForLibrarian(SquadClass squad, AppearanceCode look = default, Team side = Team.SpaceMarines) => new(SquadOwnerKind.Librarian, LoadoutChoice.Default, look.ToArray(), squad, side);

    /// <summary>The player's look.</summary>
    public AppearanceCode LookOf() => LookCode is { Length: AppearanceCode.Size } b ? AppearanceCode.FromBytes(b) : AppearanceCode.Default;
}

/// <summary>What one <see cref="SquadClassBook.OnOwnerLoadout"/> did.</summary>
/// <param name="Applied">The retainers' loadouts were set (false: the roster is on hold, or the class is not in the config).</param>
/// <param name="Held">The roster is on hold (the Dreadnought).</param>
/// <param name="Released">The roster came off hold.</param>
/// <param name="FreeRetainer">Always false since 2026-11-23 (every roster holds two; the once-per-run free retainer is gone). Kept for the callers.</param>
/// <param name="Changed">Marines whose loadout is not what it was.</param>
public readonly record struct SquadClassChange(bool Applied, bool Held, bool Released, bool FreeRetainer, int Changed);

/// <summary>One campaign key's class state in a run's save.</summary>
public sealed record SquadClassKeySave(ulong Key, SquadOwnerKind FirstKind, bool FreeGranted, SquadOwnerLoadout? Last);

/// <summary>The class state of every key (W5's save carries it beside <see cref="SquadRosterSave"/>).</summary>
public sealed record SquadClassSave(IReadOnlyList<SquadClassKeySave> Keys);

/// <summary>
/// The squad classes of a run (slice W11, owner 2026-11-17; rules of owner 2026-11-23 card 2 and the retainer brief): who the retainers are. Every roster holds exactly <see cref="SquadRules.RetainersPerPlayer"/> Marines
/// (<see cref="SquadRosters.Conform(ulong, int)"/>). A standard player's retainers each wear a valid Classic PvP bot loadout (<see cref="BotLoadouts"/>) of his own class and side, rolled per Marine
/// (<see cref="SquadLoadoutBuilder.RollClass"/>), in his armour and colours; the roster of a Dreadnought is on hold (no retainers in the field; a retainer is never a Dreadnought) and comes back, with its names and records,
/// when he switches to an eligible class; each of a Librarian's retainers independently rolls Assault/Raptor, Tactical or Devastator/Havoc on his side and a valid bot loadout of it, in the class armour and his colours
/// (the squad-class option is no longer read). Every roll is stable for the run (<see cref="SquadLoadoutBuilder.RollSeed"/>: run seed, key, slot, and the class for a standard player). Names and service records never change here.
/// </summary>
public sealed class SquadClassBook
{
    sealed class State
    {
        public SquadOwnerKind FirstKind;
        public bool FreeGranted;
        public SquadOwnerLoadout? Last;
    }

    readonly SimConfig _config;
    readonly SquadRosters _rosters;
    readonly ulong _runSeed;
    readonly Dictionary<ulong, State> _state = [];

    public SquadClassBook(SimConfig config, SquadRosters rosters, ulong runSeed)
    {
        _config = config; _rosters = rosters; _runSeed = runSeed;
    }

    /// <summary>Whether the once-per-run free retainer was given to <paramref name="key"/> (only a run saved before 2026-11-23 can say yes; it is never given now).</summary>
    public bool FreeRetainerGiven(ulong key) => _state.TryGetValue(key, out var s) && s.FreeGranted;

    /// <summary>The class the player's last confirmed loadout named; null before the first.</summary>
    public SquadOwnerLoadout? LastOf(ulong key) => _state.TryGetValue(key, out var s) ? s.Last : null;

    /// <summary>
    /// The player's class is now <paramref name="owner"/> (called on the ship when the customizer confirms, at join and at every change). A first call makes the roster of <see cref="SquadRules.RetainersPerPlayer"/>
    /// (held at once for the Dreadnought); every call keeps it at that count. A Dreadnought puts the roster on hold; an eligible class takes it off hold and sets every retainer's loadout. The result says what happened;
    /// a roster that cannot be made (too many kept) gives all false.
    /// </summary>
    public SquadClassChange OnOwnerLoadout(ulong key, SquadOwnerLoadout owner)
    {
        if (!_state.TryGetValue(key, out var st)) _state[key] = st = new State { FirstKind = owner.Kind };
        if (!_rosters.Has(key) && !_rosters.AddOwner(key, SquadRules.RetainersPerPlayer)) { _state.Remove(key); return default; }
        _rosters.Conform(key, SquadRules.RetainersPerPlayer);
        st.Last = owner;
        if (owner.Kind == SquadOwnerKind.Dreadnought)
        {
            _rosters.SetHeld(key, true);
            return new SquadClassChange(false, true, false, false, 0);
        }
        bool released = _rosters.IsHeld(key);
        if (released) _rosters.SetHeld(key, false);
        int changed = Apply(key, owner);
        return new SquadClassChange(true, false, released, false, changed);
    }

    /// <summary>Formerly a +1 Retainer card's recruit. Retainer cards are gone (owner 2026-11-23, card 2) and a roster never grows past <see cref="SquadRules.RetainersPerPlayer"/>: always false, nothing changes.</summary>
    public bool Recruit(ulong key) => false;

    /// <summary>Sets every retainer of <paramref name="key"/>'s roster from the player's class; the number of Marines whose loadout changed.</summary>
    int Apply(ulong key, SquadOwnerLoadout owner)
    {
        var look = owner.LookOf();
        int changed = 0;
        for (int slot = 0; slot < _rosters.Count(key); slot++)
        {
            var roll = owner.Kind == SquadOwnerKind.Librarian
                // each retainer its own class (the retainer brief: "A Librarian's retainers independently roll Tactical, Assault or Devastator"), then a bot loadout of it
                ? SquadLoadoutBuilder.Roll(_config, LibrarianClassOf(_runSeed, key, slot), _runSeed, key, slot, LibrarianSelection, look, owner.Side)
                : SquadLoadoutBuilder.RollClass(_config, owner.Choice.Class, _runSeed, key, slot, StandardSelection + owner.Choice.Class, look, ownPieces: true);
            if (roll is null || _rosters.Marine(key, slot) is not { } m) continue;
            if (!Same(m.Loadout, roll)) changed++;
            _rosters.SetLoadout(key, slot, roll);
        }
        return changed;
    }

    /// <summary>The roll seeds' selection values: a Librarian's retainers (one value: the squad-class option is no longer read) and a standard player's (plus his class index, so a class change rolls anew).</summary>
    const int LibrarianSelection = 0x4C1B, StandardSelection = 0x5100;

    /// <summary>The squad class a Librarian's retainer in roster slot <paramref name="slot"/> rolls: Assault, Tactical or Devastator, a third each, from the run seed, the key and the slot alone (stable for the run).</summary>
    public static SquadClass LibrarianClassOf(ulong runSeed, ulong key, int slot) =>
        (SquadClass)(int)((SquadLoadoutBuilder.RollSeed(runSeed, key, slot, LibrarianSelection ^ 0x7F) >> 32) % 3UL);

    static bool Same(SquadLoadout a, SquadLoadout b) =>
        a.ClassId == b.ClassId && a.Primary == b.Primary && a.Secondary == b.Secondary && a.Melee == b.Melee && a.Perks.SequenceEqual(b.Perks) && a.Armour.SequenceEqual(b.Armour)
        && a.Colours.SequenceEqual(b.Colours) && a.HealthMax == b.HealthMax && a.ArmourMax == b.ArmourMax && a.Weapon == b.Weapon && a.Sidearm == b.Sidearm && a.Jump == b.Jump
        && a.ArmourRegenDelay == b.ArmourRegenDelay && a.ArmourRegenRate == b.ArmourRegenRate && a.HealthRegenDelay == b.HealthRegenDelay && a.HealthRegenRate == b.HealthRegenRate;

    /// <summary>The class state as a plain value, for the run's save.</summary>
    public SquadClassSave Export() => new(_state.OrderBy(p => p.Key).Select(p => new SquadClassKeySave(p.Key, p.Value.FirstKind, p.Value.FreeGranted, p.Value.Last)).ToList());

    /// <summary>Rebuilds the class state from a save (the present is dropped).</summary>
    public void Import(SquadClassSave save)
    {
        _state.Clear();
        foreach (var k in save.Keys) _state[k.Key] = new State { FirstKind = k.FirstKind, FreeGranted = k.FreeGranted, Last = k.Last };
    }
}
