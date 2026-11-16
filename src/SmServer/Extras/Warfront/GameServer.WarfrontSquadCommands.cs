using System.Numerics;
using SmNet.Extras.Warfront;
using SmSim.Extras.Warfront.Squad;

namespace SmServer;

/// <summary>What the squad's command handler needs from the Warfront server (W5a/W3b implement it; tests use a fake).</summary>
public interface IWarfrontSquadCommandHost
{
    /// <summary>The campaign key of <paramref name="player"/>; null when not identified.</summary>
    ulong? KeyOf(int player);
    /// <summary>Whether the player is aboard the ship (the hub), where renaming is allowed.</summary>
    bool Aboard(int player);
    /// <summary>Whether the player is in a battle and alive.</summary>
    bool FightingAlive(int player);
    /// <summary>Whether the player's class has a jump pack (Assault, Raptor).</summary>
    bool HasJumpPack(int player);
    /// <summary>The player's position (a battle).</summary>
    Vector3 PositionOf(int player);
    /// <summary>Whether <paramref name="point"/> is on the walking graph.</summary>
    bool OnGraph(Vector3 point);
    /// <summary>Seconds on the server's clock.</summary>
    double Now { get; }
    /// <summary>Whether the player's class is the Librarian (only his squad class option is stored).</summary>
    bool IsLibrarian(int player);
    /// <summary><c>WarfrontSquadSim.OrderJumpPound</c> (W11); false when refused.</summary>
    bool OrderJumpPound(int player, Vector3 mark);
    /// <summary>A Librarian's stored squad class (<c>SquadClass</c> 0 Assault, 1 Tactical, 2 Devastator).</summary>
    void SetSquadClass(ulong key, byte option);
}

/// <summary>
/// The client commands of the squad (message kind 17, tag 41; slice W10): Rename, JumpPound, SquadClass. Pure over <see cref="SquadRosters"/> and <see cref="IWarfrontSquadCommandHost"/>; W5a's
/// <c>WarfrontSquadCommand</c> calls <see cref="Handle"/>. Nothing here touches Musou's <c>TfwCommand</c>.
/// </summary>
public sealed class WarfrontSquadCommands
{
    /// <summary>A jump-pound mark is at most this far from the sender [ours/estimate], and one is accepted a second [ours/estimate].</summary>
    public const float MaxMarkMetres = 60f;
    public const double PoundGapSeconds = 1.0;

    readonly SquadRosters _rosters;
    readonly IWarfrontSquadCommandHost _host;
    readonly Dictionary<int, double> _lastPound = [];

    public WarfrontSquadCommands(SquadRosters rosters, IWarfrontSquadCommandHost host) { _rosters = rosters; _host = host; }

    /// <summary>The name as the server keeps it: control characters removed, then trimmed.</summary>
    public static string Clean(string name) => new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();

    /// <summary>Handles one payload from <paramref name="player"/>; true when it changed something.</summary>
    public bool Handle(int player, ReadOnlySpan<byte> payload)
    {
        if (!SquadWire.TryDecodeCommand(payload, out var c) || _host.KeyOf(player) is not { } key) return false;
        switch (c.Kind)
        {
            case SquadCommandKind.Rename:
            {
                if (!_host.Aboard(player)) return false;   // only aboard the ship, only the sender's own roster (the key is the server's, never the wire's)
                string name = Clean(c.Name);
                return name.Length > 0 && _rosters.Rename(key, c.Slot, name);
            }
            case SquadCommandKind.JumpPound:
            {
                if (!_host.FightingAlive(player) || !_host.HasJumpPack(player)) return false;
                if (!float.IsFinite(c.Point.X) || !float.IsFinite(c.Point.Y) || !float.IsFinite(c.Point.Z)) return false;
                if (Vector3.Distance(_host.PositionOf(player), c.Point) > MaxMarkMetres || !_host.OnGraph(c.Point)) return false;
                double now = _host.Now;
                if (_lastPound.TryGetValue(player, out var at) && now - at < PoundGapSeconds) return false;
                if (!_host.OrderJumpPound(player, c.Point)) return false;
                _lastPound[player] = now;
                return true;
            }
            case SquadCommandKind.SquadClass:
                if (c.Option > 2 || !_host.Aboard(player) || !_host.IsLibrarian(player)) return false;   // option 0 to 2, aboard the ship, a Librarian only
                _host.SetSquadClass(key, c.Option);
                return true;
        }
        return false;
    }

    /// <summary>A player left: forget its pound clock.</summary>
    public void Left(int player) => _lastPound.Remove(player);
}
