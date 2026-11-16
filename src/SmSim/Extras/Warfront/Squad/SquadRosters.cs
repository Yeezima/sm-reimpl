namespace SmSim.Extras.Warfront.Squad;

/// <summary>
/// The rosters of a Warfront run: one per player (at most <see cref="SquadRules.MaxOwners"/>), each starting with <see cref="SquadRules.StartRetainers"/> retainer and growing to
/// <see cref="SquadRules.MaxPerPlayer"/> (36 in all). Rosters are separate; a Marine's name and record stay for the run whatever its loadout does (<see cref="Mirror"/>).
/// A roster on hold (W11's Dreadnought rule) keeps its Marines but sends none to a battle.
/// </summary>
public sealed class SquadRosters
{
    sealed class Roster
    {
        public int Owner;
        public bool Held;
        public readonly List<RosterMarine> Marines = [];
    }

    static readonly string[] Names =
    [
        "Cassian", "Titus", "Lysander", "Octavian", "Marius", "Severus", "Decimus", "Valerian", "Augustin", "Tiberius",
        "Corvin", "Leandros", "Gaius", "Aurelian", "Brutus", "Cato", "Draven", "Eumenes", "Fabian", "Gallus",
        "Hadrian", "Ignatius", "Julian", "Kasimir", "Livius", "Maximus", "Nestor", "Orion", "Pelias", "Quintus",
        "Remus", "Sabinus", "Thaddeus", "Ulric", "Varro", "Xander", "Yorick", "Zeno", "Aquila", "Balthus",
    ];

    readonly List<Roster> _rosters = [];
    int _nextName;

    /// <summary>Players with a roster.</summary>
    public int OwnerCount => _rosters.Count;

    /// <summary>Marines in every roster (held ones included).</summary>
    public int Total { get { int n = 0; foreach (var r in _rosters) n += r.Marines.Count; return n; } }

    /// <summary>The player id of roster slot <paramref name="ownerSlot"/> (the order the rosters were made in).</summary>
    public int OwnerId(int ownerSlot) => _rosters[ownerSlot].Owner;

    /// <summary>The roster slot of <paramref name="owner"/>; -1 with none.</summary>
    public int OwnerSlot(int owner) { for (int i = 0; i < _rosters.Count; i++) if (_rosters[i].Owner == owner) return i; return -1; }

    /// <summary>Whether <paramref name="owner"/> has a roster.</summary>
    public bool Has(int owner) => OwnerSlot(owner) >= 0;

    /// <summary>
    /// Gives <paramref name="owner"/> a roster of <paramref name="start"/> Marines (default <see cref="SquadRules.StartRetainers"/>, at most the roster limit). True when it has one now
    /// (an existing roster is left as it is); false when four other players already have one.
    /// </summary>
    public bool AddOwner(int owner, int start = SquadRules.StartRetainers)
    {
        if (Has(owner)) return true;
        if (_rosters.Count >= SquadRules.MaxOwners) return false;
        var roster = new Roster { Owner = owner };
        _rosters.Add(roster);
        for (int i = 0; i < Math.Clamp(start, 0, SquadRules.MaxPerPlayer); i++) roster.Marines.Add(NewMarine());
        return true;
    }

    /// <summary>Marines in <paramref name="owner"/>'s roster (0 with none).</summary>
    public int Count(int owner) => OwnerSlot(owner) is var s and >= 0 ? _rosters[s].Marines.Count : 0;

    /// <summary>One more Marine for <paramref name="owner"/> (a card pick); false at nine, or without a roster.</summary>
    public bool Recruit(int owner)
    {
        int s = OwnerSlot(owner);
        if (s < 0 || _rosters[s].Marines.Count >= SquadRules.MaxPerPlayer) return false;
        _rosters[s].Marines.Add(NewMarine());
        return true;
    }

    /// <summary>The Marine in slot <paramref name="slot"/> of <paramref name="owner"/>'s roster; null when there is none.</summary>
    public RosterMarine? Marine(int owner, int slot)
    {
        int s = OwnerSlot(owner);
        return s >= 0 && slot >= 0 && slot < _rosters[s].Marines.Count ? _rosters[s].Marines[slot] : null;
    }

    internal RosterMarine? MarineAt(int ownerSlot, int slot) =>
        ownerSlot >= 0 && ownerSlot < _rosters.Count && slot >= 0 && slot < _rosters[ownerSlot].Marines.Count ? _rosters[ownerSlot].Marines[slot] : null;

    internal int MarinesIn(int ownerSlot) => _rosters[ownerSlot].Marines.Count;

    /// <summary>Whether the roster of <paramref name="owner"/> is on hold (it sends nobody to battle).</summary>
    public bool IsHeld(int owner) => OwnerSlot(owner) is var s and >= 0 && _rosters[s].Held;

    /// <summary>Puts a roster on hold or releases it (W11's Dreadnought rule). False without a roster.</summary>
    public bool SetHeld(int owner, bool held)
    {
        int s = OwnerSlot(owner);
        if (s < 0) return false;
        _rosters[s].Held = held;
        return true;
    }

    internal bool IsHeldAt(int ownerSlot) => _rosters[ownerSlot].Held;

    /// <summary>
    /// Names a Marine: trimmed, 1 to <see cref="SquadRules.NameMax"/> characters, no control characters. False, and the old name kept, for anything else or an unknown Marine.
    /// </summary>
    public bool Rename(int owner, int slot, string name)
    {
        if (Marine(owner, slot) is not { } m || name is null) return false;
        name = name.Trim();
        if (name.Length is 0 or > SquadRules.NameMax || name.Any(char.IsControl)) return false;
        m.Name = name;
        return true;
    }

    /// <summary>
    /// The loadout follows the player's: every Marine of <paramref name="owner"/>'s roster takes <paramref name="loadout"/>; names and service records stay. False without a roster.
    /// </summary>
    public bool Mirror(int owner, SquadLoadout loadout)
    {
        int s = OwnerSlot(owner);
        if (s < 0) return false;
        foreach (var m in _rosters[s].Marines) m.Loadout = loadout;
        return true;
    }

    /// <summary>One Marine takes its own loadout (the Librarian's rolls); name and record stay.</summary>
    public bool SetLoadout(int owner, int slot, SquadLoadout loadout)
    {
        if (Marine(owner, slot) is not { } m) return false;
        m.Loadout = loadout;
        return true;
    }

    RosterMarine NewMarine()
    {
        int n = _nextName++;
        string name = Names[n % Names.Length];
        if (n >= Names.Length) name += " " + (n / Names.Length + 1);
        return new RosterMarine { Name = name };
    }

    /// <summary>The rosters as a plain value (names, loadouts, records, holds), for the run's save.</summary>
    public SquadRosterSave Export()
    {
        var owners = new List<SquadOwnerSave>();
        foreach (var r in _rosters)
            owners.Add(new SquadOwnerSave(r.Owner, r.Held, r.Marines.Select(m => new SquadMarineSave(m.Name, m.Loadout, m.Record.Copy())).ToList()));
        return new SquadRosterSave(_nextName, owners);
    }

    /// <summary>Rebuilds the rosters from a save (the present ones are dropped). Limits are enforced: players and Marines beyond them are ignored.</summary>
    public void Import(SquadRosterSave save)
    {
        _rosters.Clear();
        _nextName = Math.Max(0, save.NextName);
        foreach (var o in save.Owners)
        {
            if (_rosters.Count >= SquadRules.MaxOwners || Has(o.Owner)) continue;
            var roster = new Roster { Owner = o.Owner, Held = o.Held };
            foreach (var m in o.Marines.Take(SquadRules.MaxPerPlayer)) roster.Marines.Add(new RosterMarine { Name = m.Name, Loadout = m.Loadout, Record = m.Record.Copy() });
            _rosters.Add(roster);
        }
    }
}
