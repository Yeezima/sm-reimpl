using SmNet;
using SmNet.Extras.Musou;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;

namespace SmServer;

/// <summary>
/// The Librarian on the wire (tag 16, <see cref="LibrarianWire"/>; Musou Mode only): every living Librarian's stress, cooldowns, cast counts and this tick's lightning links, to every client in one section.
/// When it is sent (librarian-lightning, owner 2026-11-10: ".1 second max between clicking left mouse button and the chain lighting hitting its target"):
/// <list type="bullet">
/// <item>at once, on the tick it happens, when a cast starts or stops, a lock sets or clears, a Shockwave is cast or a Librarian comes or goes (<see cref="EdgeKey"/>): it used to wait for the next 10 Hz slot,
/// up to 100 ms on top of the network;</item>
/// <item>while any Librarian casts, the changing links at most <see cref="CastingSendsPerSecond"/> times a second; any other change at most 10 times a second;</item>
/// <item>once a second while any Librarian lives, so a lost datagram costs at most a second.</item>
/// </list>
/// Nothing is sent while there is none (one empty section when the last one goes, so the clients clear). A few hundred bytes for four casting Librarians (557 at 40 links each): inside the war's send budget.
/// The Warp Pull was removed (owner 2026-11-10): its two fields on the wire stay and are always 0, so the wire is unchanged.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>The links' rate while a Librarian casts (ours; was 10).</summary>
    public const int CastingSendsPerSecond = 20;

    byte[]? _librarianSent;
    uint _librarianTick;
    ulong _librarianEdges = ulong.MaxValue;
    readonly List<LibrarianView> _librarianViews = new();

    void SendTfwLibrarian(uint tick, int tickRate)
    {
        _librarianViews.Clear();
        bool casting = false;
        foreach (var (id, psyker) in World.Librarians)
        {
            if (id is <= 0 or > ushort.MaxValue || !World.Players.TryGetValue(id, out var p) || !p.Alive) continue;
            _librarianViews.Add(new LibrarianView((ushort)id, psyker.Stress, psyker.Locked, psyker.Casting, psyker.PullCooldown, psyker.ShockwaveCooldown, psyker.PullCount, psyker.ShockwaveCount,
                psyker.Casting ? psyker.Links : []));
            casting |= psyker.Casting;
        }
        var bytes = LibrarianWire.Write(_librarianViews);
        bool empty = _librarianViews.Count == 0;
        if (empty && (_librarianSent is null || _librarianSent.Length == 1)) return;
        bool old = World.Config.LibrarianVariant.OldSend;   // restore switch send: everything at most 10 a second, nothing at once
        ulong edges = old ? _librarianEdges : EdgeKey(_librarianViews);
        uint every = (uint)Math.Max(1, tickRate / (casting && !old ? CastingSendsPerSecond : 10)), second = (uint)Math.Max(1, tickRate);
        bool changed = _librarianSent is null || !_librarianSent.AsSpan().SequenceEqual(bytes);
        bool due = edges != _librarianEdges || tick < _librarianTick
            || (changed ? tick - _librarianTick >= every : tick - _librarianTick >= second);
        if (!due) return;
        SendExtras(new ExtrasUpdate(tick, [(TfwWire.LibrarianTag, bytes)]));
        _librarianSent = bytes;
        _librarianTick = tick;
        _librarianEdges = edges;
    }

    /// <summary>The cast command (<see cref="LibrarianWire.CastCommandTag"/>): the Librarian's lightning begins on the next tick (<see cref="World.LibrarianCastEarly"/>). False for another tag.</summary>
    bool OnLibrarianCommand(int player, byte tag, byte[] payload)
    {
        if (tag != LibrarianWire.CastCommandTag) return false;
        if (LibrarianWire.ReadCast(payload) is { } pressTick && World.LibrarianCastEarly(player, pressTick)) LibrarianEarlyCasts++;
        return true;
    }

    /// <summary>Cast commands taken (test aids, logs).</summary>
    public int LibrarianEarlyCasts { get; private set; }

    /// <summary>What must reach the clients at once: who the Librarians are, and each one's casting, lock and Shockwave count (a hash; equal keys mean nothing of it changed).</summary>
    internal static ulong EdgeKey(IReadOnlyList<LibrarianView> views)
    {
        ulong h = 1469598103934665603UL;
        foreach (var v in views)
        {
            ulong x = v.Player | (ulong)(v.Casting ? 1 : 0) << 16 | (ulong)(v.Locked ? 1 : 0) << 17 | (ulong)v.ShockwaveCount << 18;
            h = (h ^ x) * 1099511628211UL;
        }
        return h ^ (ulong)views.Count;
    }
}
