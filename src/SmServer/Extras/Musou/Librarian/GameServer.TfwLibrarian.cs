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
/// <item>while any Librarian casts, the changing links <see cref="CastingSendsPerSecond"/> times a second (a send credit, <see cref="LibrarianSendPacer"/>: every 1 or 2 ticks at 30 Hz); any other change at most 10 times a second;</item>
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
    readonly LibrarianSendPacer _librarianPacer = new();
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
        bool changed = _librarianSent is null || !_librarianSent.AsSpan().SequenceEqual(bytes);
        ulong edges = EdgeKey(_librarianViews);
        if (!_librarianPacer.Due(tick, tickRate, changed, edges, casting, old)) return;
        SendExtras(new ExtrasUpdate(tick, [(TfwWire.LibrarianTag, bytes)]));
        _librarianSent = bytes;
        _librarianPacer.Sent(tick, edges);
    }

    /// <summary>The cast command (<see cref="LibrarianWire.CastCommandTag"/>): the Librarian's lightning begins on the next tick (<see cref="World.LibrarianCastEarly"/>). False for another tag.</summary>
    bool OnLibrarianCommand(int player, byte tag, byte[] payload)
    {
        if (tag != LibrarianWire.CastCommandTag) return false;
        bool taken = LibrarianWire.ReadCast(payload) is { } pressTick && World.LibrarianCastEarly(player, pressTick);
        if (taken) LibrarianEarlyCasts++;
        if (LibrarianCastLog) Console.WriteLine($"librarian cast command: player {player}, press tick {(LibrarianWire.ReadCast(payload) is { } t ? t.ToString() : "malformed")}, server tick {World.Tick}: {(taken ? "taken" : "refused")}");
        return true;
    }

    /// <summary>Test aid <c>SM_LIBRARIAN_LOG=1</c>: a line per cast command (its press tick, the server's tick, taken or refused).</summary>
    static readonly bool LibrarianCastLog = Environment.GetEnvironmentVariable("SM_LIBRARIAN_LOG") == "1";

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

/// <summary>
/// When tag 16 goes out (<see cref="GameServer"/>'s Librarian section; separate so the tests can count sends). At once on a new <see cref="GameServer.EdgeKey"/>; while a Librarian casts and the section
/// changed, <see cref="GameServer.CastingSendsPerSecond"/> a second by a send credit (each tick earns rate / tick rate, a send spends 1, at most one tick's worth banked over 1: at 30 Hz every 1 or 2 ticks,
/// a true 20 a second); any other change every tick rate / 10 ticks; unchanged, once a second. The restore switch <c>send</c>: no edge sends and every change every tick rate / 10 ticks, as before pass 3.
/// </summary>
internal sealed class LibrarianSendPacer
{
    double _credit;
    uint _last;
    bool _any;
    ulong _edges;

    /// <summary>Whether this tick's section goes out.</summary>
    public bool Due(uint tick, int tickRate, bool changed, ulong edges, bool casting, bool old)
    {
        int rate = Math.Max(1, tickRate);
        double step = GameServer.CastingSendsPerSecond / (double)rate;
        _credit = Math.Min(1.0 + step, _credit + step);
        if (!_any || tick < _last || (!old && edges != _edges)) return true;
        if (!changed) return tick - _last >= (uint)rate;
        if (casting && !old) return _credit >= 1.0 - 1e-9;
        return tick - _last >= (uint)Math.Max(1, rate / 10);
    }

    /// <summary>The section went out on <paramref name="tick"/> with these edges.</summary>
    public void Sent(uint tick, ulong edges)
    {
        _credit = Math.Max(0.0, _credit - 1.0);
        _last = tick; _any = true; _edges = edges;
    }
}
