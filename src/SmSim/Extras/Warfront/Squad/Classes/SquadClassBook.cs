namespace SmSim.Extras.Warfront.Squad;

/// <summary>
/// What a player's class is for the squad, as the ship's customizer confirms it: <see cref="Kind"/> Standard (a Classic class and loadout, <see cref="Choice"/>), Dreadnought (no retainers) or Librarian (the squad's
/// class is <see cref="Librarian"/>). <see cref="LookCode"/> is the player's appearance code (<see cref="AppearanceCode.ToArray"/>), whose colours every retainer wears; null: the default look.
/// </summary>
public sealed record SquadOwnerLoadout(SquadOwnerKind Kind, LoadoutChoice Choice, byte[]? LookCode, SquadClass Librarian)
{
    public static SquadOwnerLoadout Standard(LoadoutChoice choice, AppearanceCode look = default) => new(SquadOwnerKind.Standard, choice, look.ToArray(), SquadClass.Tactical);
    public static SquadOwnerLoadout Dreadnought() => new(SquadOwnerKind.Dreadnought, LoadoutChoice.Default, null, SquadClass.Tactical);
    public static SquadOwnerLoadout ForLibrarian(SquadClass squad, AppearanceCode look = default) => new(SquadOwnerKind.Librarian, LoadoutChoice.Default, look.ToArray(), squad);

    /// <summary>The player's look.</summary>
    public AppearanceCode LookOf() => LookCode is { Length: AppearanceCode.Size } b ? AppearanceCode.FromBytes(b) : AppearanceCode.Default;
}

/// <summary>What one <see cref="SquadClassBook.OnOwnerLoadout"/> did.</summary>
/// <param name="Applied">The retainers' loadouts were set (false: the roster is on hold, or the class is not in the config).</param>
/// <param name="Held">The roster is on hold (the Dreadnought).</param>
/// <param name="Released">The roster came off hold.</param>
/// <param name="FreeRetainer">The once-per-run free retainer was given.</param>
/// <param name="Changed">Marines whose loadout is not what it was.</param>
public readonly record struct SquadClassChange(bool Applied, bool Held, bool Released, bool FreeRetainer, int Changed);

/// <summary>One campaign key's class state in a run's save.</summary>
public sealed record SquadClassKeySave(ulong Key, SquadOwnerKind FirstKind, bool FreeGranted, SquadOwnerLoadout? Last);

/// <summary>The class state of every key (W5's save carries it beside <see cref="SquadRosterSave"/>).</summary>
public sealed record SquadClassSave(IReadOnlyList<SquadClassKeySave> Keys);

/// <summary>
/// The squad classes of a run (slice W11; owner, 2026-11-17): who the retainers copy. A standard player's retainers mirror his class, weapons, perks, armour and colours; the roster of a Dreadnought is on hold
/// (no active retainers) and comes back, with its names and records, when he switches to an eligible class; a player whose first class this run was the Dreadnought gets one free retainer on his first switch to
/// an eligible class, once per run (kept across a save); the Librarian sets his whole squad to Assault/Raptor, Tactical or Devastator/Havoc, each Marine rolling a valid Classic PvP bot loadout
/// (<see cref="BotLoadouts"/>) of the class, stable while the selection stays (<see cref="SquadLoadoutBuilder.RollSeed"/>: run seed, key, roster slot, selection). Names and service records never change here.
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
    readonly Team _side;
    readonly Dictionary<ulong, State> _state = [];

    public SquadClassBook(SimConfig config, SquadRosters rosters, ulong runSeed, Team side = Team.SpaceMarines)
    {
        _config = config; _rosters = rosters; _runSeed = runSeed; _side = side;
    }

    /// <summary>Whether the once-per-run free retainer has been given to <paramref name="key"/>.</summary>
    public bool FreeRetainerGiven(ulong key) => _state.TryGetValue(key, out var s) && s.FreeGranted;

    /// <summary>The class the player's last confirmed loadout named; null before the first.</summary>
    public SquadOwnerLoadout? LastOf(ulong key) => _state.TryGetValue(key, out var s) ? s.Last : null;

    /// <summary>
    /// The player's class is now <paramref name="owner"/> (called on the ship when the customizer confirms, at join and at every change). A first call makes the roster: one retainer for an eligible class,
    /// none (and on hold) for the Dreadnought. A Dreadnought puts the roster on hold; an eligible class takes it off hold, gives the free retainer once if the player's first class was the Dreadnought, and
    /// sets every retainer's loadout. The result says what happened; a roster that cannot be made (too many kept) gives all false.
    /// </summary>
    public SquadClassChange OnOwnerLoadout(ulong key, SquadOwnerLoadout owner)
    {
        if (!_state.TryGetValue(key, out var st)) _state[key] = st = new State { FirstKind = owner.Kind };
        if (!_rosters.Has(key) && !_rosters.AddOwner(key, owner.Kind == SquadOwnerKind.Dreadnought ? 0 : SquadRules.StartRetainers)) { _state.Remove(key); return default; }
        st.Last = owner;
        if (owner.Kind == SquadOwnerKind.Dreadnought)
        {
            _rosters.SetHeld(key, true);
            return new SquadClassChange(false, true, false, false, 0);
        }
        bool released = _rosters.IsHeld(key);
        if (released) _rosters.SetHeld(key, false);
        bool free = false;
        if (st.FirstKind == SquadOwnerKind.Dreadnought && !st.FreeGranted)
        {
            st.FreeGranted = true;
            free = _rosters.Recruit(key);
        }
        int changed = Apply(key, owner);
        return new SquadClassChange(true, false, released, free, changed);
    }

    /// <summary>A card's +1 retainer: recruits and gives the new Marine the player's class loadout (a Marine recruited under the Dreadnought sits in the held roster with the rest). False at nine or without a roster.</summary>
    public bool Recruit(ulong key)
    {
        if (!_rosters.Recruit(key)) return false;
        if (_state.TryGetValue(key, out var st) && st.Last is { Kind: not SquadOwnerKind.Dreadnought } last) Apply(key, last);
        return true;
    }

    /// <summary>Sets every retainer of <paramref name="key"/>'s roster from the player's class; the number of Marines whose loadout changed.</summary>
    int Apply(ulong key, SquadOwnerLoadout owner)
    {
        var look = owner.LookOf();
        int changed = 0;
        if (owner.Kind == SquadOwnerKind.Librarian)
        {
            int selection = (int)owner.Librarian + 1;
            for (int slot = 0; slot < _rosters.Count(key); slot++)
            {
                var roll = SquadLoadoutBuilder.Roll(_config, owner.Librarian, _runSeed, key, slot, selection, look, _side);
                if (roll is null || _rosters.Marine(key, slot) is not { } m) continue;
                if (!Same(m.Loadout, roll)) changed++;
                _rosters.SetLoadout(key, slot, roll);
            }
            return changed;
        }
        var mirror = SquadLoadoutBuilder.FromChoice(_config, owner.Choice, look);
        if (mirror is null) return 0;
        for (int slot = 0; slot < _rosters.Count(key); slot++)
            if (_rosters.Marine(key, slot) is { } m && !Same(m.Loadout, mirror)) changed++;
        _rosters.Mirror(key, mirror);
        return changed;
    }

    static bool Same(SquadLoadout a, SquadLoadout b) =>
        a.ClassId == b.ClassId && a.Primary == b.Primary && a.Secondary == b.Secondary && a.Melee == b.Melee && a.Perks.SequenceEqual(b.Perks) && a.Armour.SequenceEqual(b.Armour)
        && a.Colours.SequenceEqual(b.Colours) && a.HealthMax == b.HealthMax && a.ArmourMax == b.ArmourMax && a.Weapon == b.Weapon;

    /// <summary>The class state as a plain value, for the run's save.</summary>
    public SquadClassSave Export() => new(_state.OrderBy(p => p.Key).Select(p => new SquadClassKeySave(p.Key, p.Value.FirstKind, p.Value.FreeGranted, p.Value.Last)).ToList());

    /// <summary>Rebuilds the class state from a save (the present is dropped).</summary>
    public void Import(SquadClassSave save)
    {
        _state.Clear();
        foreach (var k in save.Keys) _state[k.Key] = new State { FirstKind = k.FirstKind, FreeGranted = k.FreeGranted, Last = k.Last };
    }
}
