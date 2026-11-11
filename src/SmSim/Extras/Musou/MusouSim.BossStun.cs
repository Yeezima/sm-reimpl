using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// The bosses' stun windows (owner, 2026-11-12: "make it so when the bosses take enough damage they are stunned for a 5s window. This 5s stun triggers every time a boss or warboss reaches 80%, 60%, 40%,
/// 20% hp"). The Warboss and the Boss Orks (<see cref="MusouPowerupRules.Boss"/>) in Musou Mode's war only (the 5-wave run's replay is pinned). Thresholds and window are the owner's; the rest of the
/// rule (one stun per hit however many shares it crosses, no extra damage while stunned, the abilities cancelled) is ours (spec "Musou Mode: boss danger and stun").
/// </summary>
public static class MusouBossStunRules
{
    /// <summary>The shares of its full health a boss is stunned at, each once (owner: 80, 60, 40, 20 %).</summary>
    public static readonly float[] Shares = [0.8f, 0.6f, 0.4f, 0.2f];
    /// <summary>The stun's length in seconds (owner: 5 s).</summary>
    public const float Seconds = 5f;
    /// <summary>The cue (a <see cref="MusouEventKind.Telegraph"/>'s value, ours) a stun begins with, at the boss's feet.</summary>
    public const byte BeginCue = 60;

    /// <summary>The bit set of the thresholds a boss with <paramref name="share"/> of its health left has reached (bit k: <see cref="Shares"/>[k]).</summary>
    public static byte Reached(float share)
    {
        byte bits = 0;
        for (int k = 0; k < Shares.Length; k++) if (share <= Shares[k]) bits |= (byte)(1 << k);
        return bits;
    }
}

public sealed partial class MusouSim
{
    readonly float[] _maxHealth = new float[Capacity];
    readonly byte[] _stunMarks = new byte[Capacity];
    readonly ushort[] _stunLeft = new ushort[Capacity];

    void SpawnedBossStun(int i) { _maxHealth[i] = _health[i]; _stunMarks[i] = 0; _stunLeft[i] = 0; }

    /// <summary>Whether boss <paramref name="id"/> stands in a stun window now (the Warboss's abilities read it).</summary>
    public bool BossStunned(ushort id) => id >= 1 && id <= Capacity && _used[id - 1] && _stunLeft[id - 1] > 0;

    /// <summary>Stun windows begun so far (the log and the tests).</summary>
    public int BossStuns { get; private set; }

    /// <summary>From <see cref="Damage"/>, the enemy alive after a hit: a boss in the war that fell to a share it had not reached is stunned (every share the hit crossed is marked, one stun).</summary>
    void BossHurt(int i)
    {
        if (War is null || !MusouPowerupRules.Boss(_type[i]) || _maxHealth[i] <= 0f) return;
        byte reached = MusouBossStunRules.Reached(_health[i] / _maxHealth[i]);
        byte fresh = (byte)(reached & ~_stunMarks[i]);
        if (fresh == 0) return;
        _stunMarks[i] |= fresh;
        BeginBossStun(i);
    }

    void BeginBossStun(int i)
    {
        _scripted[i] = false; _drive[i] = Vector2.Zero; _driveTicks[i] = 0;
        _token[i] = 0; _burst[i] = 0; _charging[i] = false; _velocity[i] = Vector2.Zero;
        SetState(i, MusouEnemyState.Stunned);
        _stunLeft[i] = Ticks(MusouBossStunRules.Seconds);
        BossStuns++;
        AddEvent(new MusouEvent(MusouEventKind.Telegraph, Tick, (ushort)(i + 1), 0, MusouBossStunRules.BeginCue, _position[i]));
    }

    /// <summary>One tick of a stunned boss (from <see cref="Step"/>): it stands, then goes back to its own decisions. True while it stood.</summary>
    bool StepBossStun(int i)
    {
        _headwayTick[i] = Tick; _anchor[i] = _position[i];
        _token[i] = 0; _burst[i] = 0; _velocity[i] = Vector2.Zero;
        if (--_stunLeft[i] == 0) SetState(i, MusouEnemyState.Seek);
        else if (_state[i] != MusouEnemyState.Stunned) SetState(i, MusouEnemyState.Stunned);
        return true;
    }
}
