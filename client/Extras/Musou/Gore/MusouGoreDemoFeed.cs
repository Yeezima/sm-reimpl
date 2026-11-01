using System;
using System.Collections.Generic;
using System.Numerics;
using SmNet;
using SmNet.Extras.Musou;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// Test aid (Musou pass 2 M10, <c>--musou-gore-demo [N]</c>): a scripted feed of N Orks (default 60, at most 200) through the wire's own writers and the client state, so a
/// windowed test shows the whole client path (the crowd's bodies, the events, the gore) without a battle. It replaces the server's Musou sections while it runs. A 36 s cycle
/// of the cases the owner named: a cleave sweep and melee kills (items 8 and 22), shots, two lascannon shots, two frag grenades, a barrage of artillery (item 23), then 8 s of
/// heavy death rates (about 30 kills a second). The lascannon's killer is <see cref="LascannonKiller"/> (<see cref="NetworkView"/> reads it as a lascannon in hand).
/// **Placeholder demonstration** of the events; the Orks drawn are the original assets. Deterministic.
/// </summary>
public sealed class MusouGoreDemoFeed
{
    public const int TicksPerSecond = 30, RecordEvery = 3, Cycle = 36, LascannonKiller = 99, Killer = 1;
    const float RespawnSeconds = 3f;

    sealed class Ork
    {
        public Vector3 Home;
        public MusouEnemyType Type;
        public float Phase, DeadAt = float.NegativeInfinity;
        public bool Alive = true, Fresh;
    }

    readonly Ork[] _orks;
    readonly List<MusouEntity> _entities = new();
    readonly List<MusouEvent> _events = new();
    readonly Random _rng = new(11);
    readonly Vector3 _centre;
    uint _tick;
    double _clock;
    readonly HashSet<int> _done = new();

    /// <summary>Kills made, hits made, by case (for the log).</summary>
    public int Kills, Hits;
    int _resetWindow = -1;

    /// <summary>With a single case: true once at the start of each window, when the view clears the ground so a still shows that case alone.</summary>
    public bool TakeReset()
    {
        if (_case is null) return false;
        int window = (int)((_clock - 0.15) / _window);
        if (window == _resetWindow) return false;
        _resetWindow = window;
        return true;
    }

    public MusouBox Box { get; }
    /// <summary>The render tick to sample at (as a client's interpolation delay).</summary>
    public double RenderTick => _tick - 3.0;
    public Vector3 Centre => _centre;

    /// <summary>One case alone, every window (8 s, or <c>--musou-gore-window s</c>) (<c>--musou-gore-case sweep|shot|lascannon|frag|barrage|churn</c>; a comma list takes them in turn), for stills; null: the whole 36 s cycle.</summary>
    readonly string? _case;
    readonly float _window;

    public MusouGoreDemoFeed(int count, Vector3 centre, string? only = null, float window = 8f)
    {
        _centre = centre;
        _case = only;
        _window = Math.Clamp(window, 1f, 30f);
        count = Math.Clamp(count, 1, 200);
        _orks = new Ork[count];
        // Home places on a 1.7 m grid around the centre, the nearest first, jittered.
        var cells = new List<(float D, Vector3 P)>();
        for (int x = -9; x <= 9; x++)
            for (int y = -9; y <= 9; y++)
            {
                var p = centre + new Vector3(x * 1.7f + (float)(_rng.NextDouble() - 0.5) * 0.5f, y * 1.7f + (float)(_rng.NextDouble() - 0.5) * 0.5f, 0f);
                cells.Add((Vector3.Distance(p, centre), p));
            }
        cells.Sort((a, b) => a.D.CompareTo(b.D));
        for (int i = 0; i < count; i++)
        {
            double r = _rng.NextDouble();
            var type = r < 0.62 ? MusouEnemyType.Boy : r < 0.74 ? MusouEnemyType.ShootaBoy : r < 0.86 ? MusouEnemyType.ArdBoy : r < 0.93 ? MusouEnemyType.Gretchin : MusouEnemyType.Nob;
            _orks[i] = new Ork { Home = cells[i].P, Type = type, Phase = (float)(_rng.NextDouble() * Math.Tau) };
        }
        Box = new MusouBox(centre - new Vector3(200f, 200f, 50f), centre + new Vector3(200f, 200f, 50f));
    }

    public IEnumerable<ExtrasSection> Step(double delta)
    {
        _clock += delta;
        uint to = (uint)(_clock * TicksPerSecond);
        while (_tick < to)
        {
            _tick++;
            foreach (var s in Tick()) yield return s;
        }
    }

    Vector3 Place(Ork o, float t, out float yaw)
    {
        float a = o.Phase + t * 1.8f;
        yaw = a + MathF.PI / 2f;
        return o.Home + new Vector3(MathF.Cos(a), MathF.Sin(a), 0f) * 0.55f;
    }

    ushort Id(int i) => (ushort)(i + 1);

    IEnumerable<ExtrasSection> Tick()
    {
        float t = _tick / (float)TicksPerSecond;
        float c = t % Cycle;
        _entities.Clear(); _events.Clear();
        for (int i = 0; i < _orks.Length; i++)
        {
            var o = _orks[i];
            if (!o.Alive && t - o.DeadAt > RespawnSeconds) { o.Alive = true; o.Fresh = true; }
        }
        Script(t, c);
        if (_tick % RecordEvery == 0)
        {
            for (int i = 0; i < _orks.Length; i++)
            {
                var o = _orks[i];
                if (!o.Alive) continue;
                var p = Place(o, t, out float yaw);
                _entities.Add(new MusouEntity(Id(i), p, yaw, o.Type, o.Fresh ? MusouEnemyState.Spawning : MusouEnemyState.Seek));
                o.Fresh = false;
            }
            for (int at = 0; at < _entities.Count; at += MusouWire.MaxEntities)
                yield return new ExtrasSection(_tick, MusouWire.EntitiesTag, MusouWire.WriteEntities(_entities.GetRange(at, Math.Min(MusouWire.MaxEntities, _entities.Count - at)), Box));
        }
        if (_tick % 10 == 1)
            yield return new ExtrasSection(_tick, MusouWire.DirectorTag, MusouWire.WriteDirector(new MusouDirectorView(1, 1, MusouPhase.Campaign, 0, 0, (ushort)_entities.Count, 0, 0f, 0f, 3, Box)));
        for (int at = 0; at < _events.Count; at += MusouWire.MaxEvents)
            yield return new ExtrasSection(_tick, MusouWire.EventsTag, MusouWire.WriteEvents(_events.GetRange(at, Math.Min(MusouWire.MaxEvents, _events.Count - at)), Box));
    }

    /// <summary>Fires step <paramref name="step"/> of the case once per cycle at <paramref name="at"/> s.</summary>
    bool Once(float c, float at, int step) => c >= at && c < at + 1f / TicksPerSecond * 1.5f && _done.Add(step * 1000 + (int)(_tick / (Cycle * TicksPerSecond)));

    void Script(float t, float c)
    {
        if (_case is not null)
        {
            float k = t % _window;
            int n = (int)(t / _window);
            var list = _case.Split(',');
            switch (list[n % list.Length])
            {
                case "melee": if (Once(k, 0.4f, 80 + n)) MeleeKill(t); break;
                case "sweep": if (k is >= 0.4f and < 0.7f && _tick % 4 == 0) Sweep(t); break;
                case "shot": if (k is >= 0.4f and < 1.2f && _tick % 4 == 0) Shoot(t); break;
                case "lascannon": if (Once(k, 0.4f, 20 + n)) Lascannon(t); break;
                case "frag": if (Once(k, 0.4f, 40 + n)) Frag(t); break;
                case "barrage": if (Once(k, 0.4f, 60 + n)) Barrage(t); break;
                case "churn": if (k >= 0.4f && k < _window - 0.2f && _tick % 3 == 0) Churn(t); break;
            }
            return;
        }
        // Melee: a sweep every half second from 1 s to 7 s, in front of the crowd's near edge.
        if (c is >= 1f and < 7f && _tick % 15 == 0) Sweep(t);
        // Shots: 8 to 12 s, five a second.
        if (c is >= 8f and < 12f && _tick % 6 == 0) Shoot(t);
        if (Once(c, 13f, 1) || Once(c, 14.5f, 2)) Lascannon(t);
        if (Once(c, 16f, 3) || Once(c, 18f, 4)) Frag(t);
        if (Once(c, 21f, 5)) Barrage(t);
        // Heavy death rates: 24 to 32 s, three kills every tick pair (about 30 a second), every one a melee or a blast.
        if (c is >= 24f and < 32f && _tick % 3 == 0) Churn(t);
    }

    int Alive(Func<Ork, bool>? where = null)
    {
        int n = 0;
        foreach (var o in _orks) if (o.Alive && (where is null || where(o))) n++;
        return n;
    }

    int Pick(Func<Ork, bool> where)
    {
        int n = 0, found = -1;
        for (int k = 0; k < _orks.Length; k++)
            if (_orks[k].Alive && where(_orks[k]) && _rng.Next(++n) == 0) found = k;
        return found;
    }

    Vector3 Where(int i, float t) => Place(_orks[i], t, out _);

    void Hit(int i, float t, byte marker, int attacker = Killer)
    {
        _events.Add(new MusouEvent(MusouEventKind.Hit, _tick, Id(i), (ushort)attacker, marker, Where(i, t)));
        Hits++;
    }

    void Kill(int i, float t, MusouDeathKind cause, int killer, Vector3 push)
    {
        var o = _orks[i];
        if (!o.Alive) return;
        o.Alive = false; o.DeadAt = t;
        _events.Add(new MusouEvent(MusouEventKind.Death, _tick, Id(i), (ushort)killer, (byte)cause, push));
        Kills++;
    }

    static Vector3 Away(Vector3 from, Vector3 to)
    {
        var d = new Vector3(to.X - from.X, to.Y - from.Y, 0.25f);
        return d.LengthSquared() > 1e-6f ? Vector3.Normalize(d) : Vector3.UnitY;
    }

    /// <summary>A cleave: a hit (marker 3) on up to 12 enemies near one, the nearest three die (a slash on each of 12, item 22).</summary>
    void Sweep(float t)
    {
        int first = Pick(o => o.Home.Y < _centre.Y - 2f);
        if (first < 0) first = Pick(_ => true);
        if (first < 0) return;
        var at = Where(first, t);
        var from = at + new Vector3(0f, -1.8f, 0f);
        var near = new List<(float D, int I)>();
        for (int k = 0; k < _orks.Length; k++)
            if (_orks[k].Alive) near.Add((Vector3.Distance(Where(k, t), at), k));
        near.Sort((a, b) => a.D.CompareTo(b.D));
        for (int k = 0; k < near.Count && k < 12; k++) Hit(near[k].I, t, 3);
        for (int k = 0; k < near.Count && k < 3; k++) Kill(near[k].I, t, MusouDeathKind.Melee, Killer, Away(from, Where(near[k].I, t)));
    }

    /// <summary>One melee kill: a hit (marker 3) and a Melee death on one enemy in the front rows (for a still of a single kill).</summary>
    void MeleeKill(float t)
    {
        int i = -1;   // the enemy nearest the camera's side (the lowest y), so a still sees it clear of the crowd
        for (int k = 0; k < _orks.Length; k++) if (_orks[k].Alive && (i < 0 || _orks[k].Home.Y < _orks[i].Home.Y)) i = k;
        if (i < 0) return;
        var at = Where(i, t);
        Hit(i, t, 3);
        Kill(i, t, MusouDeathKind.Melee, Killer, Away(at + new Vector3(0f, -1.8f, 0f), at));
    }

    void Shoot(float t)
    {
        int i = Pick(o => o.Home.Y < _centre.Y);
        if (i < 0) return;
        Hit(i, t, (byte)(_rng.Next(4) == 0 ? 2 : 1));
        if (_rng.Next(3) == 0) Kill(i, t, MusouDeathKind.Shot, Killer, Away(Where(i, t) + new Vector3(0f, -8f, 0f), Where(i, t)));
    }

    void Lascannon(float t)
    {
        for (int n = 0; n < 2; n++)
        {
            int i = Pick(o => o.Home.Y < _centre.Y + 2f);
            if (i < 0) return;
            Hit(i, t, 1, LascannonKiller);
            Kill(i, t, MusouDeathKind.Shot, LascannonKiller, Away(Where(i, t) + new Vector3(0f, -8f, 0f), Where(i, t)));
        }
    }

    void Frag(float t)
    {
        int i = Pick(o => o.Home.Y < _centre.Y - 4.5f && Math.Abs(o.Home.X - _centre.X) < 3f);   // the front rows, so a still sees it
        if (i < 0) i = Pick(_ => true);
        if (i < 0) return;
        var at = Where(i, t);
        for (int k = 0; k < _orks.Length; k++)
            if (_orks[k].Alive && Vector3.Distance(Where(k, t), at) <= 3.6f) Kill(k, t, MusouDeathKind.Blast, Killer, Away(at, Where(k, t)));
    }

    void Barrage(float t)
    {
        int i = Pick(_ => true);
        if (i < 0) return;
        var at = Where(i, t);
        for (int k = 0; k < _orks.Length; k++)
            if (_orks[k].Alive && Vector3.Distance(Where(k, t), at) <= 9f) Kill(k, t, MusouDeathKind.Barrage, 0, Away(at, Where(k, t)));
    }

    void Churn(float t)
    {
        for (int n = 0; n < 3; n++)
        {
            int i = Pick(_ => true);
            if (i < 0) return;
            var at = Where(i, t);
            Hit(i, t, 3);
            bool blast = _rng.Next(4) == 0;
            Kill(i, t, blast ? MusouDeathKind.Blast : MusouDeathKind.Melee, Killer, Away(at + new Vector3(0f, -3f, 0f), at));
        }
    }
}
