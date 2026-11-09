using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// Client-built tracers (owner, 2026-11-09: "rebuild tracers on the client from burst cues"; added content, Musou Mode only). The bullet-gunning Orks (Shoota Boy, Boss Shoota Boy, Big Shoota Nob)
/// announce a burst with one <see cref="MusouEventKind.Telegraph"/> of <see cref="Cue"/> (the target in the event's player, the aim point in its vector) instead of one <see cref="MusouEventKind.Tracer"/>
/// a round on the wire: the server stops sending those rounds (<see cref="Synthesized"/>), and each client draws the rounds itself (<see cref="MusouGunBursts"/>) at the gun's own rate. The
/// damage stays the server's: the drawn hit or miss is only visual (a roll by the same distance chances).
/// </summary>
public static class MusouGunBurst
{
    /// <summary>The burst cue: <see cref="MusouEvent.Player"/> the target's id, <see cref="MusouEvent.Vector"/> the aim point (the target's chest when the burst began).</summary>
    public const byte Cue = 46;

    /// <summary>Whether a kind's rounds are drawn by the client from the burst cue (its per-round tracer events are not sent).</summary>
    public static bool Synthesized(MusouEnemyType type) => type is MusouEnemyType.ShootaBoy or MusouEnemyType.BossShootaBoy or MusouEnemyType.BigShootaNob;
}

/// <summary>The rounds of the bursts a client knows (engine-free, deterministic given the same cues): <see cref="Start"/> on a cue, <see cref="Step"/> each frame for the rounds now due.</summary>
public sealed class MusouGunBursts
{
    struct Burst { public ushort Enemy, Target; public Vector3 Aim; public double Next, Interval; public int Left, Round; public float HitChance; }
    readonly List<Burst> _bursts = new();

    /// <summary>Bursts running.</summary>
    public int Count => _bursts.Count;

    /// <summary>A burst of <paramref name="rounds"/> rounds <paramref name="interval"/> s apart from <paramref name="now"/>, the first at once; a new one of the same Ork replaces his old.</summary>
    public void Start(ushort enemy, ushort target, Vector3 aim, double now, int rounds, double interval, float hitChance)
    {
        Cancel(enemy);
        if (rounds <= 0 || !(interval > 0.0)) return;
        _bursts.Add(new Burst { Enemy = enemy, Target = target, Aim = aim, Next = now, Interval = interval, Left = rounds, HitChance = hitChance });
    }

    /// <summary>Ends an Ork's burst (the gun jammed, he fell or left).</summary>
    public void Cancel(ushort enemy) => _bursts.RemoveAll(b => b.Enemy == enemy);

    public bool Has(ushort enemy) { foreach (var b in _bursts) if (b.Enemy == enemy) return true; return false; }

    /// <summary>The rounds due by <paramref name="now"/> as <see cref="MusouEventKind.Tracer"/> events (<paramref name="tick"/> for their order) into <paramref name="into"/>; at most <paramref name="maxPerBurst"/> a burst a call.</summary>
    public void Step(double now, uint tick, List<MusouEvent> into, int maxPerBurst = 6)
    {
        for (int k = _bursts.Count - 1; k >= 0; k--)
        {
            var b = _bursts[k];
            int made = 0;
            while (b.Left > 0 && b.Next <= now && made < maxPerBurst)
            {
                uint roll = MusouSim.Mix((uint)b.Enemy * 0x9E3779B1u + (uint)b.Round * 0x85EBCA6Bu, tick);
                bool hit = b.Target != 0 && MusouSim.Unit(roll) < b.HitChance;
                var end = b.Aim;
                if (!hit)
                {
                    float angle = MusouSim.Unit(MusouSim.Mix(roll, 1u)) * MathF.Tau, radius = 0.5f + 1.5f * MusouSim.Unit(MusouSim.Mix(roll, 2u));
                    end += new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, (MusouSim.Unit(MusouSim.Mix(roll, 3u)) - 0.5f) * 1.0f);
                }
                into.Add(new MusouEvent(MusouEventKind.Tracer, tick, b.Enemy, hit ? b.Target : (ushort)0, 0, end));
                b.Left--; b.Round++; b.Next += b.Interval; made++;
            }
            if (b.Left <= 0 || (b.Next < now - 1.0 && made == 0)) _bursts.RemoveAt(k); else _bursts[k] = b;
        }
    }
}
