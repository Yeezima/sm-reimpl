using System.Numerics;

namespace SmSim;

/// <summary>What a pickup refills (fidelity gap 1): ammunition for the carried weapons, or the thrown equipment.</summary>
public enum PickupKind : byte { Ammo = 0, Grenades = 1 }

/// <summary>
/// One kind of pickup a death may drop (the original's <c>ebps\props\pickup_mp_drop_ammo</c> and <c>pickup_mp_drop_grenades</c>,
/// read by <c>SmContent.PickupTuning</c>; spec/behavior.md "Ammunition and grenade drops").
/// </summary>
/// <param name="Blueprint">The pickup blueprint's file name (e.g. "pickup_mp_drop_ammo").</param>
/// <param name="Weight">The death action's <c>random_actions</c> weight of the entry that spawns it (ammo 3, grenades 1).</param>
/// <param name="Radius">Metres from the pickup to a player's feet within which an interact press collects it (<c>interaction_style/interact_distance</c>: 4).</param>
/// <param name="Lifetime">Seconds until it disappears uncollected (<c>lifetime_action</c>'s <c>delayed_action</c> with <c>kill_entity</c>: 25).</param>
/// <param name="WeaponAmounts">Rounds added per weapon, index for index with <see cref="SimConfig.Weapons"/> (the pickup's
/// <c>replenish_wargear_ammunition</c> amount for the weapon's <c>common_data/ammunition/entry/name</c>; 0: none).</param>
/// <param name="ThrownAmounts">Items added per thrown item, index for index with <see cref="SimConfig.Thrown"/> (0: none).</param>
public sealed record PickupType(string Blueprint, PickupKind Kind, int Weight, float Radius, float Lifetime, IReadOnlyList<int> WeaponAmounts, IReadOnlyList<int> ThrownAmounts)
{
    /// <summary>Rounds this pickup adds to weapon <paramref name="weapon"/> (an index into <see cref="SimConfig.Weapons"/>); 0 for none or out of range.</summary>
    public int WeaponAmount(int weapon) => weapon >= 0 && weapon < WeaponAmounts.Count ? Math.Max(0, WeaponAmounts[weapon]) : 0;

    /// <summary>Items this pickup adds to thrown item <paramref name="item"/> (an index into <see cref="SimConfig.Thrown"/>); 0 for none or out of range.</summary>
    public int ThrownAmount(int item) => item >= 0 && item < ThrownAmounts.Count ? Math.Max(0, ThrownAmounts[item]) : 0;

    /// <summary>The lifetime in whole ticks (at least 1).</summary>
    public uint LifetimeTicks(SimConfig c) => (uint)Math.Clamp(MathF.Round((float.IsFinite(Lifetime) ? Lifetime : 0f) * c.TickRate), 1f, 1e7f);
}

/// <summary>
/// The drops of a death (fidelity gap 1). Data: every multiplayer player blueprint's <c>health_ext/death_action</c> has a
/// <c>random_actions</c> command whose entries spawn one of <see cref="Types"/> by weight, <c>force_spawn_on_ground</c>; each pickup is
/// collected with one interact press (<c>progress_need_button_press</c>, <c>progress_per_button_press</c> 1) within its radius, by anyone
/// (no team filter, <c>ignore_blockers</c>, <c>ignore_operator_facing</c>), and dies after its lifetime. Ours: <see cref="MaxLive"/>,
/// <see cref="GroundSearch"/>, the draw (<see cref="Pickups.Draw"/>), who wins a contested pickup and the caps (<see cref="Pickups.Collect"/>).
/// Null in <see cref="SimConfig.Pickups"/>: no drops (tests, no install).
/// </summary>
public sealed record PickupSpec(IReadOnlyList<PickupType> Types)
{
    /// <summary>Most pickup types a config may have (the wire carries the type in 4 bits).</summary>
    public const int MaxTypes = 16;
    /// <summary>Most pickups in the world at once (ours; one more expires the oldest early), clamped to 1..<see cref="MaxLiveLimit"/>. The pickup message carries all of them.</summary>
    public int MaxLive { get; init; } = DefaultMaxLive;
    public const int DefaultMaxLive = 64;
    /// <summary>The largest <see cref="MaxLive"/> that takes effect: what one pickup message carries (<c>SmNet.Protocol.MaxPickupRecords</c>).</summary>
    public const int MaxLiveLimit = 64;
    /// <summary>
    /// How far below the death point the ground is searched for (<c>force_spawn_on_ground</c>; metres, ours). No ground within it (a pit,
    /// a fall out of the level) or ground inside a kill volume: nothing drops.
    /// </summary>
    public float GroundSearch { get; init; } = 50f;

    /// <summary>The sum of the positive weights (0: nothing ever drops).</summary>
    public int TotalWeight => Types.Sum(t => Math.Max(0, t.Weight));

    /// <summary>Writes every field for a config hash (null: a single 0).</summary>
    public static void Write(BinaryWriter w, PickupSpec? s)
    {
        w.Write(s is not null);
        if (s is null) return;
        w.Write(s.MaxLive); w.Write(s.GroundSearch);
        w.Write(s.Types.Count);
        foreach (var t in s.Types)
        {
            w.Write(t.Blueprint); w.Write((byte)t.Kind); w.Write(t.Weight); w.Write(t.Radius); w.Write(t.Lifetime);
            w.Write(t.WeaponAmounts.Count); foreach (int a in t.WeaponAmounts) w.Write(a);
            w.Write(t.ThrownAmounts.Count); foreach (int a in t.ThrownAmounts) w.Write(a);
        }
    }
}

/// <summary>A pickup lying in the world: its id (1..65535), type (index into <see cref="PickupSpec.Types"/>), place (game space, on the ground), and the tick it goes.</summary>
public readonly record struct Pickup(int Id, int Type, Vector3 Position, uint SpawnTick, uint ExpireTick, int Dropper);

/// <summary>How a pickup left the world.</summary>
public enum PickupEnd : byte { Collected = 1, Expired = 2 }

/// <summary>What a collection added (rounds to the primary and the pistol, items to the equipment), after the caps.</summary>
public readonly record struct PickupGain(int Primary, int Pistol, int Grenades)
{
    public bool Any => Primary > 0 || Pistol > 0 || Grenades > 0;
}

/// <summary>A pickup that left the world during the last <see cref="World.Step"/>: collected by <see cref="Collector"/> (0 when it expired).</summary>
public readonly record struct PickupEvent(uint Tick, int Id, int Type, Vector3 Position, PickupEnd End, int Collector, PickupGain Gain = default);

/// <summary>The rules of the drops (fidelity gap 1), pure functions shared by the world and the tests.</summary>
public static class Pickups
{
    /// <summary>
    /// The type a death drops (index into <see cref="PickupSpec.Types"/>), or -1 when the weights sum to 0. Deterministic and independent of any
    /// shared random stream (ours): a hash of the death's tick and the victim's id picks a number in [0, total weight), and the types cover
    /// consecutive ranges of it in their order (with the install's 3:1, ammo for 0..2, grenades for 3).
    /// </summary>
    public static int Draw(PickupSpec spec, uint tick, int victim)
    {
        int total = spec.TotalWeight;
        if (total <= 0) return -1;
        int roll = (int)(Seed(tick, victim) % (ulong)total);
        for (int i = 0; i < spec.Types.Count; i++)
        {
            int w = Math.Max(0, spec.Types[i].Weight);
            if (roll < w) return i;
            roll -= w;
        }
        return -1;
    }

    /// <summary>The draw's seed: SplitMix64 of (tick, victim).</summary>
    public static ulong Seed(uint tick, int victim)
    {
        ulong z = (((ulong)tick << 32) | (uint)victim) + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Whether feet at <paramref name="feet"/> are within the type's radius of a pickup at <paramref name="pickup"/> (3-D distance; ours: the feet stand for the operator).</summary>
    public static bool InReach(PickupType type, Vector3 pickup, Vector3 feet) => Vector3.DistanceSquared(pickup, feet) <= type.Radius * type.Radius;

    /// <summary>
    /// The player after collecting a pickup of <paramref name="type"/>: each carried weapon (primary and pistol, as the perks make them:
    /// <see cref="Loadouts.Weapon(SimConfig, PlayerState, WeaponSlot)"/>) gets the type's amount for it in its reserve, capped at the reserve size;
    /// a weapon without a reserve (the heavy bolter, the plasma weapons: everything is in the clip) gets it in the clip, capped at the clip size;
    /// the equipment item gets its amount, capped at its count. Ours: the caps and the clip rule (the data says "absolute" amounts, not where
    /// they go). A dead player gains nothing.
    /// </summary>
    public static (PlayerState State, PickupGain Gain) Collect(SimConfig c, PlayerState s, PickupType type)
    {
        if (!s.Alive) return (s, default);
        int primaryGain = 0, pistolGain = 0;
        foreach (var slot in new[] { WeaponSlot.Ranged, WeaponSlot.Pistol, WeaponSlot.Second })   // protocol 44: the second primary too
        {
            if (Loadouts.Weapon(c, s, slot) is not { } weapon) continue;
            int amount = type.WeaponAmount(Loadouts.WeaponIndex(c, s, slot));
            if (amount <= 0) continue;
            bool inHand = s.Slot == slot, isOther = !inHand && Loadouts.Other(s.Slot) == slot;
            int clip = inHand ? s.Clip : isOther ? s.OtherClip : s.ThirdClip, reserve = inHand ? s.Reserve : isOther ? s.OtherReserve : s.ThirdReserve;
            int gained;
            if (weapon.ReserveSize > 0)
            {
                int now = Math.Clamp(reserve + amount, reserve, Math.Max(reserve, weapon.ReserveSize));
                gained = now - reserve; reserve = now;
            }
            else
            {
                int now = Math.Clamp(clip + amount, clip, Math.Max(clip, weapon.ClipSize));
                gained = now - clip; clip = now;
            }
            s = inHand ? s with { Clip = clip, Reserve = reserve } : isOther ? s with { OtherClip = clip, OtherReserve = reserve } : s with { ThirdClip = clip, ThirdReserve = reserve };
            if (slot == WeaponSlot.Pistol) pistolGain = gained; else primaryGain += gained;
        }
        int grenadeGain = 0;
        if (Loadouts.Equipment(c, s) is { } item && type.ThrownAmount(Loadouts.EquipmentIndex(c, s)) is var add and > 0)
        {
            int now = Math.Clamp(s.Grenades + add, s.Grenades, Math.Max(s.Grenades, item.Count));
            grenadeGain = now - s.Grenades;
            s = s with { Grenades = now };
        }
        return (s, new PickupGain(primaryGain, pistolGain, grenadeGain));
    }
}

/// <summary>
/// The world side of the drops (fidelity gap 1): every player who dies drops one pickup on the ground under the death point
/// (<see cref="Pickups.Draw"/>); an interact press (<see cref="InputButtons.Interact"/>, press edge) collects the nearest pickup in reach;
/// pickups expire after their lifetime; a new round clears them. Server only: clients learn of pickups from the pickup messages
/// (<c>SmNet.PickupUpdate</c>) and never predict a collection (their next snapshot carries the ammunition).
/// </summary>
public sealed partial class World
{
    readonly List<Pickup> _pickups = new();
    readonly List<Pickup> _pickupSpawns = new();
    readonly List<PickupEvent> _pickupEvents = new();
    readonly HashSet<int> _interactHeld = new();
    readonly List<int> _interactPresses = new();
    int _nextPickupId = 1;

    /// <summary>Pickups in the world, oldest first.</summary>
    public IReadOnlyList<Pickup> LivePickups => _pickups;
    /// <summary>Pickups dropped during the last <see cref="Step"/>.</summary>
    public IReadOnlyList<Pickup> LastPickupSpawns => _pickupSpawns;
    /// <summary>Pickups collected or expired during the last <see cref="Step"/>.</summary>
    public IReadOnlyList<PickupEvent> LastPickupEvents => _pickupEvents;

    readonly List<PickupEvent> _pickupEventLog = new();
    readonly List<Pickup> _pickupSpawnLog = new();
    /// <summary>Most entries <see cref="PickupEventLog"/> and <see cref="PickupSpawnLog"/> keep (the oldest go).</summary>
    public const int PickupLogLength = 512;
    /// <summary>Every collection and expiry so far, also those of <see cref="SpawnPickup"/> calls between ticks (the newest <see cref="PickupLogLength"/>); read new ones by <see cref="PickupEventsTotal"/>.</summary>
    public IReadOnlyList<PickupEvent> PickupEventLog => _pickupEventLog;
    /// <summary>Collections and expiries so far.</summary>
    public long PickupEventsTotal { get; private set; }
    /// <summary>Every drop so far (the newest <see cref="PickupLogLength"/>); read new ones by <see cref="PickupSpawnsTotal"/>.</summary>
    public IReadOnlyList<Pickup> PickupSpawnLog => _pickupSpawnLog;
    /// <summary>Drops so far.</summary>
    public long PickupSpawnsTotal { get; private set; }

    void AddPickupEvent(PickupEvent e)
    {
        _pickupEvents.Add(e);
        _pickupEventLog.Add(e);
        if (_pickupEventLog.Count > PickupLogLength) _pickupEventLog.RemoveRange(0, _pickupEventLog.Count - PickupLogLength);
        PickupEventsTotal++;
    }

    /// <summary>Notes an interact press edge of the player in this tick's input (called from <see cref="Step"/>'s player loop).</summary>
    void NoteInteract(int id, PlayerInput input)
    {
        bool down = (input.Buttons & InputButtons.Interact) != 0;
        if (down && _interactHeld.Add(id)) _interactPresses.Add(id);
        else if (!down) _interactHeld.Remove(id);
    }

    /// <summary>
    /// Drops a pickup of type <paramref name="type"/> at <paramref name="at"/> (taken as on the ground) for <paramref name="dropper"/>; null
    /// without a <see cref="SimConfig.Pickups"/> or with a type out of range. Used by the death drop and by tests. One over
    /// <see cref="PickupSpec.MaxLive"/> expires the oldest.
    /// </summary>
    public Pickup? SpawnPickup(int type, Vector3 at, int dropper = 0)
    {
        if (Config.Pickups is not { } spec || type < 0 || type >= spec.Types.Count || !float.IsFinite(at.X + at.Y + at.Z)) return null;
        int id = NextPickupId();
        var p = new Pickup(id, type, at, Tick, Tick + spec.Types[type].LifetimeTicks(Config), dropper);
        _pickups.Add(p);
        _pickupSpawns.Add(p);
        _pickupSpawnLog.Add(p);
        if (_pickupSpawnLog.Count > PickupLogLength) _pickupSpawnLog.RemoveRange(0, _pickupSpawnLog.Count - PickupLogLength);
        PickupSpawnsTotal++;
        while (_pickups.Count > Math.Clamp(spec.MaxLive, 1, PickupSpec.MaxLiveLimit)) Expire(0);
        return p;
    }

    /// <summary>Ids run 1..65535 (the wire carries 16 bits) and wrap, skipping ids still in the world.</summary>
    int NextPickupId()
    {
        for (int k = 0; k < ushort.MaxValue; k++)
        {
            int id = _nextPickupId;
            _nextPickupId = _nextPickupId >= ushort.MaxValue ? 1 : _nextPickupId + 1;
            if (!_pickups.Exists(p => p.Id == id)) return id;
        }
        throw new InvalidOperationException("No free pickup id.");
    }

    void Expire(int index)
    {
        var p = _pickups[index];
        _pickups.RemoveAt(index);
        AddPickupEvent(new PickupEvent(Tick, p.Id, p.Type, p.Position, PickupEnd.Expired, 0));
    }

    /// <summary>Whether collections and drops happen now: no match rules, or a match in warm-up or playing (also in the tick a match ended, whose deaths ended it).</summary>
    public bool PickupsActive => Rules is null || State is MatchState.Warmup or MatchState.Playing || (State == MatchState.Ended && _endedTick == Tick);

    /// <summary>A new round (after an ended match): every pickup goes, without events (clients drop what the next message no longer lists).</summary>
    void ClearPickups()
    {
        _pickups.Clear();
        _pickupSpawns.Clear();
    }

    /// <summary>
    /// After the tick's deaths: (1) collections: each living player who pressed interact this tick claims every pickup in reach; claims are
    /// granted nearest first (ties: lower player id, then lower pickup id), one pickup per player and one player per pickup, so of players
    /// pressing on the same pickup in the same tick exactly one gets it (ours); (2) expiry; (3) one drop for every player who died this tick,
    /// on the ground under the death point (<see cref="GroundUnder"/>), type by <see cref="Pickups.Draw"/>.
    /// </summary>
    void StepPickups(List<int> aliveBefore)
    {
        _interactHeld.RemoveWhere(id => !_players.ContainsKey(id));
        try
        {
            if (Config.Pickups is not { } spec) return;
            // Collections and drops only while the match is in warm-up or playing (or without match rules); expiry always runs. A match
            // that has ended keeps its pickups until the next round clears them (ours; review 2026-10-28: a countdown state must clear too).
            bool active = PickupsActive;
            if (active && _interactPresses.Count > 0 && _pickups.Count > 0)
            {
                var claims = new List<(float D2, int Player, int Index)>();
                foreach (int id in _interactPresses)
                {
                    if (!_players.TryGetValue(id, out var p) || !p.Alive) continue;
                    for (int i = 0; i < _pickups.Count; i++)
                    {
                        var q = _pickups[i];
                        if (Pickups.InReach(spec.Types[q.Type], q.Position, p.Position)) claims.Add((Vector3.DistanceSquared(q.Position, p.Position), id, i));
                    }
                }
                claims.Sort((a, b) => a.D2 != b.D2 ? a.D2.CompareTo(b.D2) : a.Player != b.Player ? a.Player.CompareTo(b.Player) : _pickups[a.Index].Id.CompareTo(_pickups[b.Index].Id));
                var taken = new HashSet<int>();
                var served = new HashSet<int>();
                foreach (var (_, player, index) in claims)
                {
                    if (taken.Contains(index) || served.Contains(player)) continue;
                    taken.Add(index); served.Add(player);
                    var q = _pickups[index];
                    var (after, gain) = Pickups.Collect(Config, _players[player], spec.Types[q.Type]);
                    _players[player] = after;
                    AddPickupEvent(new PickupEvent(Tick, q.Id, q.Type, q.Position, PickupEnd.Collected, player, gain));
                }
                foreach (int index in taken.OrderDescending()) _pickups.RemoveAt(index);
            }
            for (int i = _pickups.Count - 1; i >= 0; i--)
                if (_pickups[i].ExpireTick <= Tick) Expire(i);
            foreach (int victim in active ? aliveBefore : [])
            {
                if (!_players.TryGetValue(victim, out var dead) || dead.Alive) continue;
                DropPickup(spec, victim, dead.Position);
            }
        }
        finally { _interactPresses.Clear(); }
    }

    /// <summary>One death's drop: a type by <see cref="Pickups.Draw"/>, on the ground under <paramref name="at"/> (<see cref="GroundUnder"/>); none when the draw or the ground gives none.</summary>
    void DropPickup(PickupSpec spec, int victim, Vector3 at)
    {
        int type = Pickups.Draw(spec, Tick, victim);
        if (type < 0 || GroundUnder(at, spec.GroundSearch) is not { } ground) return;
        SpawnPickup(type, ground, victim);
    }

    /// <summary>
    /// Where a drop lands (<c>force_spawn_on_ground</c>): without a level the flat ground (z = 0); with one, the first character-collision
    /// surface straight below the point (from 0.5 m above it, at most <paramref name="search"/> m down), unless the point or that ground lies in a kill
    /// volume; null when there is no such ground (ours).
    /// </summary>
    Vector3? GroundUnder(Vector3 at, float search)
    {
        if (!float.IsFinite(at.X + at.Y + at.Z)) return null;
        if (Collision is null) return new Vector3(at.X, at.Y, 0f);
        foreach (var k in Collision.KillVolumes) if (k.Contains(at)) return null;
        var from = at + new Vector3(0f, 0f, 0.5f);
        if (Collision.Character.Raycast(from, new Vector3(0f, 0f, -1f), MathF.Max(0.5f, search + 0.5f)) is not { } hit) return null;
        foreach (var k in Collision.KillVolumes) if (k.Contains(hit.Point)) return null;
        return hit.Point;
    }
}
