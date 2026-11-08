using SmNet;
using SmNet.Extras.Musou;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;

namespace SmServer;

/// <summary>
/// The Librarian on the wire (tag 16, <see cref="LibrarianWire"/>; Musou Mode only): every living Librarian's stress, cooldowns, cast counts and this tick's lightning links, to every client in one section. Sent on a
/// change at most 10 times a second (the lightning's links change every tick it is held), and once a second while any Librarian lives so a lost datagram costs at most a second. Nothing is sent while there is none
/// (one empty section when the last one goes, so the clients clear). A few bytes a Librarian: well inside the war's send budget.
/// </summary>
public sealed partial class GameServer
{
    byte[]? _librarianSent;
    uint _librarianTick;
    readonly List<LibrarianView> _librarianViews = new();

    void SendTfwLibrarian(uint tick, int tickRate)
    {
        _librarianViews.Clear();
        foreach (var (id, psyker) in World.Librarians)
        {
            if (id is <= 0 or > ushort.MaxValue || !World.Players.TryGetValue(id, out var p) || !p.Alive) continue;
            _librarianViews.Add(new LibrarianView((ushort)id, psyker.Stress, psyker.Locked, psyker.Casting, psyker.PullCooldown, psyker.ShockwaveCooldown, psyker.PullCount, psyker.ShockwaveCount,
                psyker.Casting ? psyker.Links : []));
        }
        var bytes = LibrarianWire.Write(_librarianViews);
        bool empty = _librarianViews.Count == 0;
        if (empty && (_librarianSent is null || _librarianSent.Length == 1)) return;
        uint every = (uint)Math.Max(1, tickRate / 10), second = (uint)Math.Max(1, tickRate);
        bool changed = _librarianSent is null || !_librarianSent.AsSpan().SequenceEqual(bytes);
        bool due = changed ? tick - _librarianTick >= every || tick < _librarianTick : tick - _librarianTick >= second || tick < _librarianTick;
        if (!due) return;
        SendExtras(new ExtrasUpdate(tick, [(TfwWire.LibrarianTag, bytes)]));
        _librarianSent = bytes;
        _librarianTick = tick;
    }
}
