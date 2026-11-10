using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// The enemies as one Librarian's client drew them when it sampled this tick's input (musou-balance 2026-11-11): the pool at the input's view tick and fraction, from <see cref="LibrarianHistory"/>. The chain
/// lightning is picked on these (<see cref="LibrarianChain"/>, the rule the client's foresight runs on its crowd at the same render tick), so the chain the caster sees drawn is the chain the server strikes.
/// Filled by the World for each casting Librarian; reused, no allocation.
/// </summary>
public sealed class LibrarianSight
{
    readonly LibrarianCandidate[] _candidates = new LibrarianCandidate[MusouSim.Capacity];
    readonly MusouEnemyType[] _types = new MusouEnemyType[MusouSim.Capacity];   // the type seen (a reused slot holds another type: not the same enemy; review 2026-11-11)
    int _count;
    float _slack2;

    /// <summary>The enemies seen (alive at the view tick, where they stood then).</summary>
    public ReadOnlySpan<LibrarianCandidate> Candidates => _candidates.AsSpan(0, _count);

    internal void Clear(float slack)
    {
        _count = 0;
        _slack2 = slack * slack;
    }

    internal void Add(in LibrarianCandidate c, MusouEnemyType type) { _types[_count] = type; _candidates[_count++] = c; }

    /// <summary>
    /// Whether the enemy of candidate <paramref name="index"/> alive now is still the one seen: the same type, within the slack of where it was seen (a slot the pool freed and filled again since,
    /// with a new enemy of another type or elsewhere, is not).
    /// </summary>
    internal bool Same(int index, MusouSim sim)
    {
        ushort id = _candidates[index].Id;
        if (sim.TypeNow(id) != _types[index]) return false;
        var then = _candidates[index].Feet;
        var now = sim.FeetNow(id);
        return Vector3.DistanceSquared(now, then) <= _slack2;
    }
}

/// <summary>
/// The pool's last ticks for the chain lightning's rewind (musou-balance 2026-11-11; <c>World.Librarian.cs</c>): each tick's end, every slot's feet, type and whether it was alive, as the snapshot after the tick
/// carries them. Kept for <see cref="Ticks"/> ticks (the core's rewind and two), only while a Librarian is in the match.
/// </summary>
public sealed class LibrarianHistory
{
    /// <summary>How far an enemy may move in one tick before an interpolation between two ticks counts as a jump and keeps the earlier place (as the players' <c>World.RewindTeleportDistance</c>).</summary>
    public const float TeleportDistance = 3f;

    readonly uint[] _tick;
    readonly bool[] _kept;
    readonly Vector3[][] _feet;
    readonly MusouEnemyType[][] _type;
    readonly bool[][] _alive;

    public LibrarianHistory(int ticks)
    {
        Ticks = Math.Max(2, ticks);
        _tick = new uint[Ticks];
        _kept = new bool[Ticks];
        _feet = new Vector3[Ticks][];
        _type = new MusouEnemyType[Ticks][];
        _alive = new bool[Ticks][];
        for (int k = 0; k < Ticks; k++) { _feet[k] = new Vector3[MusouSim.Capacity]; _type[k] = new MusouEnemyType[MusouSim.Capacity]; _alive[k] = new bool[MusouSim.Capacity]; }
    }

    /// <summary>How many ticks are kept.</summary>
    public int Ticks { get; }

    /// <summary>The newest tick kept (0: none).</summary>
    public uint Newest { get; private set; }

    /// <summary>The pool at the end of <paramref name="tick"/>.</summary>
    public void Record(uint tick, MusouSim sim)
    {
        int k = (int)(tick % (uint)Ticks);
        sim.CopyPlaces(_feet[k], _type[k], _alive[k]);
        _tick[k] = tick; _kept[k] = true;
        Newest = tick;
    }

    /// <summary>Forgets every tick (a Librarian-less stretch: the next record starts afresh).</summary>
    public void Clear()
    {
        Array.Clear(_kept);
        Newest = 0;
    }

    bool Has(uint tick, out int k)
    {
        k = (int)(tick % (uint)Ticks);
        return tick != 0 && _kept[k] && _tick[k] == tick;
    }

    /// <summary>
    /// The enemies at view tick <paramref name="tick"/>, <paramref name="fraction"/> of the way to the next tick when it is kept (a move over <see cref="TeleportDistance"/> keeps the earlier place), into
    /// <paramref name="into"/>: every one alive then, with its body height (<paramref name="sim"/>'s). False (and <paramref name="into"/> untouched) when the tick is not kept.
    /// <paramref name="slack"/>: how far one alive now may stand from where it was seen and still be taken for the same enemy.
    /// </summary>
    public bool SightAt(uint tick, float fraction, MusouSim sim, LibrarianSight into, float slack)
    {
        if (!Has(tick, out int a)) return false;
        int b = a;
        bool between = false;
        if (fraction > 0f && Has(tick + 1, out int next)) { b = next; between = true; }
        into.Clear(slack);
        var feetA = _feet[a]; var feetB = _feet[b]; var aliveA = _alive[a]; var aliveB = _alive[b]; var types = _type[a];
        for (int i = 0; i < MusouSim.Capacity; i++)
        {
            if (!aliveA[i]) continue;
            var feet = feetA[i];
            if (between && aliveB[i] && Vector3.DistanceSquared(feetB[i], feet) <= TeleportDistance * TeleportDistance) feet = Vector3.Lerp(feet, feetB[i], fraction);
            into.Add(new LibrarianCandidate((ushort)(i + 1), feet, sim.Body(types[i]).Height), types[i]);
        }
        return true;
    }
}
