using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>What a Musou event reports (design section C, the envelope's tag 4).</summary>
public enum MusouEventKind : byte
{
    /// <summary>An enemy died: <see cref="MusouEvent.Enemy"/>, its killer in <see cref="MusouEvent.Player"/> (0: none), <see cref="MusouEvent.Value"/> a
    /// <see cref="MusouDeathKind"/>, <see cref="MusouEvent.Vector"/> the push (a direction, length up to 1).</summary>
    Death = 1,
    /// <summary>A player hit an enemy: shooter in <see cref="MusouEvent.Player"/>, <see cref="MusouEvent.Value"/> the hit marker (1 body, 2 head, 3 melee),
    /// <see cref="MusouEvent.Vector"/> the hit point.</summary>
    Hit = 2,
    /// <summary>An enemy struck a player in melee: target in <see cref="MusouEvent.Player"/>, <see cref="MusouEvent.Vector"/> the target's position.</summary>
    Strike = 3,
    /// <summary>An enemy fired: target in <see cref="MusouEvent.Player"/> (0: a miss), <see cref="MusouEvent.Vector"/> where the tracer ends.</summary>
    Tracer = 4,
    /// <summary>An enemy telegraphs an attack: <see cref="MusouEvent.Value"/> a <see cref="MusouBossAbility"/> for the boss, 0
    /// (<see cref="MusouSim.MeleeWindUpTelegraph"/>) for any enemy's melee wind-up (its target in <see cref="MusouEvent.Player"/>); <see cref="MusouEvent.Vector"/> where it lands.</summary>
    Telegraph = 5,
}

/// <summary>How an enemy died (<see cref="MusouEventKind.Death"/>'s value).</summary>
public enum MusouDeathKind : byte { Shot = 0, Melee = 1, Blast = 2, World = 3 }

/// <summary>The Warboss's abilities (<see cref="MusouEventKind.Telegraph"/>'s value; design section B).</summary>
public enum MusouBossAbility : byte { GroundSlam = 1, Charge = 2, Waaagh = 3, Enrage = 4 }

/// <summary>
/// One event of a tick (<see cref="MusouSim.Events"/>): its kind, the server tick it happened on (its identity: each is repeated in 3 messages and played
/// once), the enemy, the player, one byte of value and a vector, all by kind (<see cref="MusouEventKind"/>).
/// </summary>
public readonly record struct MusouEvent(MusouEventKind Kind, uint Tick, ushort Enemy, ushort Player, byte Value, Vector3 Vector);

/// <summary>The director's phase (design section B: the waves, the breaks between them, the boss, the end).</summary>
public enum MusouPhase : byte
{
    Waiting = 0, Wave = 1, Break = 2, Boss = 3, Won = 4, Lost = 5,
    /// <summary>The Three-Front War is on (the run's own state travels in tag 7): no waves, no lives pool.</summary>
    Campaign = 6,
}

/// <summary>
/// The box enemy positions are quantised over on the wire (the nav graph's area with its floors; sent with the director so a client decodes the entities).
/// </summary>
public readonly record struct MusouBox(Vector3 Min, Vector3 Max)
{
    /// <summary>A box of at least 1 m on every axis around these corners (no division by zero when quantising).</summary>
    public MusouBox Sane() => new(Vector3.Min(Min, Max), Vector3.Max(Vector3.Max(Min, Max), Vector3.Min(Min, Max) + Vector3.One));
}

/// <summary>
/// What the director tells clients (design section C, the envelope's tag 5, every 10 ticks and on a change): the wave and the waves in all, the phase, the
/// break's ticks left, enemies left in the wave and alive now, the boss (id 0: none) and its health, the lives left in the shared pool, the quantisation box.
/// </summary>
public readonly record struct MusouDirectorView(byte Wave, byte Waves, MusouPhase Phase, ushort BreakTicksLeft, ushort EnemiesLeft, ushort EnemiesAlive,
    ushort BossId, float BossHealth, float BossMaxHealth, byte Lives, MusouBox Box);
