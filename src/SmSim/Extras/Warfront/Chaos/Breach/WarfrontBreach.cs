using System.Numerics;

namespace SmSim.Extras.Warfront.Chaos;

/// <summary>Where a breach stands in its life: <see cref="Opening"/> from the landing to <see cref="WarfrontBreach.OpenAt"/>, <see cref="Open"/> (it spawns), <see cref="Closed"/> for good. The wire's state byte.</summary>
public enum WarfrontBreachState : byte { Opening = 0, Open = 1, Closed = 2 }

/// <summary>One warp breach of a Chaos battle (Chaos Warfront C4): its place, its state, its clocks in the battle's ticks, and the Daemons of it still alive.</summary>
public sealed class WarfrontBreach
{
    internal WarfrontBreach(byte id, Vector3 position, int openAt, int firstSquadAt)
    {
        Id = id;
        Position = position;
        OpenAt = openAt;
        FirstSquadAt = firstSquadAt;
        NextSquadAt = firstSquadAt;
    }

    /// <summary>Its number in the battle (1 up; the wire's id).</summary>
    public byte Id { get; }
    /// <summary>Its foot on the walking graph (the wire's position).</summary>
    public Vector3 Position { get; }
    /// <summary>Its centre (<see cref="WarfrontBreachRules.CentreHeight"/> over the foot): the close rule and the sphere measure from it.</summary>
    public Vector3 Centre => Position + new Vector3(0f, 0f, WarfrontBreachRules.CentreHeight);
    public WarfrontBreachState State { get; internal set; }
    /// <summary>The battle ticks it opens at and its first squad comes at; the tick the next squad is due.</summary>
    public int OpenAt { get; }
    public int FirstSquadAt { get; }
    public int NextSquadAt { get; internal set; }
    /// <summary>The world tick it was closed at, and by whom (a player id; 0: closed silently by the battle, <see cref="WarfrontBreaches.CloseAll"/>). 0 and 0 while it stands.</summary>
    public uint ClosedAt { get; internal set; }
    public int ClosedBy { get; internal set; }
    /// <summary>Daemons it has sent in all.</summary>
    public int Spawned { get; internal set; }

    internal readonly List<ushort> Daemons = new();
    /// <summary>Its Daemons still alive (pruned each step).</summary>
    public IReadOnlyList<ushort> Alive => Daemons;
    public bool Standing => State != WarfrontBreachState.Closed;
}

/// <summary>A breach closed by a human's explosive (the wire's kind 2): the world tick, the breach and the player who closed it.</summary>
public readonly record struct WarfrontBreachClosure(uint Tick, byte Breach, int Closer);

/// <summary>What the breaches need of the battle they stand in (<c>TfwBattle.WarfrontChaosBreach.cs</c>; a test may supply its own).</summary>
public interface IWarfrontBreachField
{
    /// <summary>The enemies the battle may still add now: its alive cap and the pool's room, whichever is less.</summary>
    int Room { get; }
    /// <summary>Spawns one Daemon at (or near, on the walking graph) <paramref name="spot"/> by the breach whose foot is <paramref name="breach"/>; its id, or 0 when none came (the pool is full).</summary>
    ushort Spawn(Vector3 spot, Vector3 breach);
    /// <summary>Whether <paramref name="id"/> is still a living Daemon (a slot taken since by another enemy is not).</summary>
    bool IsDaemon(ushort id);
}
