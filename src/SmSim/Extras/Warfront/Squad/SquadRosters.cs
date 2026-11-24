namespace SmSim.Extras.Warfront.Squad;

/// <summary>
/// The rosters of a Warfront run: one per campaign key, each holding exactly <see cref="SquadRules.RetainersPerPlayer"/> retainers in a run (owner 2026-11-23, card 2; <see cref="Conform(ulong, int)"/>);
/// <see cref="SquadRules.MaxPerPlayer"/> is only the capacity a bench may fill with <see cref="Recruit"/>. Rosters are separate and keyed by the campaign key, so a reconnect's new player id changes nothing; a Marine's name and record stay for the run whatever its loadout does (<see cref="Mirror"/>).
/// A roster on hold (W11's Dreadnought rule) keeps its Marines but sends none to a battle.
/// </summary>
public sealed class SquadRosters
{
    sealed class Roster
    {
        public ulong Key;
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

    /// <summary>Rosters kept (one per campaign key that ever got one; every key's roster stays for the run).</summary>
    public int OwnerCount => _rosters.Count;

    /// <summary>Marines in every kept roster (held ones included).</summary>
    public int Total { get { int n = 0; foreach (var r in _rosters) n += r.Marines.Count; return n; } }

    Roster? Of(ulong key) { foreach (var r in _rosters) if (r.Key == key) return r; return null; }

    /// <summary>Whether <paramref name="key"/> has a roster.</summary>
    public bool Has(ulong key) => Of(key) is not null;

    /// <summary>
    /// Gives campaign key <paramref name="key"/> a roster of <paramref name="start"/> Marines (default 1, the squad sim's own tests and benches; a run's class book passes <see cref="SquadRules.StartRetainers"/>,
    /// at most the roster limit). True when it has one now (an existing roster is left as it is); false only past <see cref="SquadRules.MaxKeptRosters"/>. At most four rosters fight at once: that limit is the sim's
    /// (<c>WarfrontSquadSim.Land</c>).
    /// </summary>
    public bool AddOwner(ulong key, int start = 1)
    {
        if (Has(key)) return true;
        if (_rosters.Count >= SquadRules.MaxKeptRosters) return false;
        var roster = new Roster { Key = key };
        _rosters.Add(roster);
        for (int i = 0; i < Math.Clamp(start, 0, SquadRules.MaxPerPlayer); i++) roster.Marines.Add(NewMarine());
        return true;
    }

    /// <summary>Marines in <paramref name="key"/>'s roster (0 with none).</summary>
    public int Count(ulong key) => Of(key)?.Marines.Count ?? 0;

    /// <summary>
    /// Makes <paramref name="key"/>'s roster hold exactly <paramref name="count"/> Marines (at most <see cref="SquadRules.MaxPerPlayer"/>): Marines past it are dropped from the end, missing ones are added with a new name and
    /// the first Marine's loadout (the class book sets the real one at its next look). Names and service records of the kept Marines stay, and so does a hold. The number of Marines added or dropped; 0 without a roster.
    /// </summary>
    public int Conform(ulong key, int count = SquadRules.RetainersPerPlayer)
    {
        var r = Of(key);
        if (r is null) return 0;
        count = Math.Clamp(count, 0, SquadRules.MaxPerPlayer);
        int changed = Math.Abs(r.Marines.Count - count);
        if (r.Marines.Count > count) r.Marines.RemoveRange(count, r.Marines.Count - count);
        while (r.Marines.Count < count)
        {
            var m = NewMarine();
            if (r.Marines.Count > 0) m.Loadout = r.Marines[0].Loadout;
            r.Marines.Add(m);
        }
        return changed;
    }

    /// <summary><see cref="Conform(ulong, int)"/> for every kept roster (a loaded run: a roster that grew by +1 Retainer cards, or a Dreadnought's empty one, holds <paramref name="count"/>). The Marines added or dropped.</summary>
    public int ConformAll(int count = SquadRules.RetainersPerPlayer)
    {
        int changed = 0;
        foreach (var r in _rosters) changed += Conform(r.Key, count);
        return changed;
    }

    /// <summary>One more Marine for <paramref name="key"/> (a bench filling the capacity; a run never calls it since 2026-11-23); false at nine, or without a roster.</summary>
    public bool Recruit(ulong key)
    {
        var r = Of(key);
        if (r is null || r.Marines.Count >= SquadRules.MaxPerPlayer) return false;
        r.Marines.Add(NewMarine());
        return true;
    }

    /// <summary>The Marine in slot <paramref name="slot"/> of <paramref name="key"/>'s roster; null when there is none.</summary>
    public RosterMarine? Marine(ulong key, int slot)
    {
        var r = Of(key);
        return r is not null && slot >= 0 && slot < r.Marines.Count ? r.Marines[slot] : null;
    }

    /// <summary>Whether the roster of <paramref name="key"/> is on hold (it sends nobody to battle).</summary>
    public bool IsHeld(ulong key) => Of(key)?.Held == true;

    /// <summary>Puts a roster on hold or releases it (W11's Dreadnought rule). False without a roster.</summary>
    public bool SetHeld(ulong key, bool held)
    {
        var r = Of(key);
        if (r is null) return false;
        r.Held = held;
        return true;
    }

    /// <summary>
    /// Names a Marine: trimmed, 1 to <see cref="SquadRules.NameMax"/> characters, no control characters. False, and the old name kept, for anything else or an unknown Marine.
    /// </summary>
    public bool Rename(ulong key, int slot, string name)
    {
        if (Marine(key, slot) is not { } m || name is null) return false;
        name = name.Trim();
        if (name.Length is 0 or > SquadRules.NameMax || name.Any(char.IsControl)) return false;
        m.Name = name;
        return true;
    }

    /// <summary>
    /// The loadout follows the player's: every Marine of <paramref name="key"/>'s roster takes <paramref name="loadout"/>; names and service records stay. False without a roster.
    /// </summary>
    public bool Mirror(ulong key, SquadLoadout loadout)
    {
        var r = Of(key);
        if (r is null) return false;
        foreach (var m in r.Marines) m.Loadout = loadout;
        return true;
    }

    /// <summary>One Marine takes its own loadout (the Librarian's rolls); name and record stay.</summary>
    public bool SetLoadout(ulong key, int slot, SquadLoadout loadout)
    {
        if (Marine(key, slot) is not { } m) return false;
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
            owners.Add(new SquadOwnerSave(r.Key, r.Held, r.Marines.Select(m => new SquadMarineSave(m.Name, m.Loadout, m.Record.Copy())).ToList()));
        return new SquadRosterSave(_nextName, owners);
    }

    /// <summary>
    /// Rebuilds the rosters from a save (the present ones are dropped). Limits are enforced: players and Marines beyond them are ignored. With <paramref name="perRoster"/> of 0 or more every roster then holds exactly
    /// that many (<see cref="Conform(ulong, int)"/>); the default (-1) keeps the saved counts. A loaded run's roster reaches <see cref="SquadRules.RetainersPerPlayer"/> at its owner's next class look aboard
    /// (<see cref="SquadClassBook.OnOwnerLoadout"/> conforms it: owner 2026-11-23, card 2, a roster grown by +1 Retainer cards keeps its first two, a Dreadnought's empty one gets two, held), always before he lands.
    /// </summary>
    public void Import(SquadRosterSave save, int perRoster = -1)
    {
        _rosters.Clear();
        _nextName = Math.Max(0, save.NextName);
        foreach (var o in save.Owners)
        {
            if (_rosters.Count >= SquadRules.MaxKeptRosters || Has(o.Key)) continue;
            var roster = new Roster { Key = o.Key, Held = o.Held };
            foreach (var m in o.Marines.Take(SquadRules.MaxPerPlayer)) roster.Marines.Add(new RosterMarine { Name = m.Name, Loadout = m.Loadout, Record = m.Record.Copy() });
            _rosters.Add(roster);
        }
        if (perRoster >= 0) ConformAll(perRoster);
    }
}
