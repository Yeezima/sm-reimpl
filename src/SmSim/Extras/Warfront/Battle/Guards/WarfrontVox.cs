namespace SmSim.Extras.Warfront;

/// <summary>The Guardsmen's vox callouts (Guardsmen G1e/G2), highest priority first.</summary>
public enum WarfrontVoxKind : byte { LineBreaking = 1, FallingBack = 2, Rallying = 3, Reinforcements = 4, Flank = 5 }

/// <summary>
/// One vox line: its sequence (1 up, wraps past 65535 to 1), its kind, the point it is about (<see cref="From"/>: 0..2 the points) and, for a fall back, where to (<see cref="To"/>: 0..2 a point,
/// <see cref="WarfrontVox.LandingZone"/> the landing zone, <see cref="WarfrontVox.None"/> none), and the server tick it was made.
/// </summary>
public readonly record struct WarfrontVoxLine(ushort Sequence, WarfrontVoxKind Kind, byte From, byte To, uint Tick);

/// <summary>
/// The vox rate limiter (Guardsmen G1e; pure and deterministic: ticks only, lists in offer order): at least <see cref="SpacingSeconds"/> between lines, <see cref="KindCooldownSeconds"/> for one kind anywhere and
/// <see cref="PointCooldownSeconds"/> for one kind about one point, at most <see cref="PerMinute"/> lines in a rolling minute (Line breaking and Falling back may go over it, never the spacing). A high priority line
/// (the first three kinds) waits up to <see cref="WaitSeconds"/> for its turn, and is replaced by a newer high priority line about the same point (Falling back replaces Line breaking); a low priority line that cannot
/// go at once is dropped. Values ours (spec "Hold, fall back, rally").
/// </summary>
public sealed class WarfrontVox
{
    public const byte LandingZone = 3, None = 255;
    public const float SpacingSeconds = 6f, KindCooldownSeconds = 12f, PointCooldownSeconds = 30f, WaitSeconds = 4f, WindowSeconds = 60f;
    public const int PerMinute = 5;

    readonly int _rate;
    readonly List<(WarfrontVoxKind Kind, byte From, byte To, uint Tick)> _pending = new();
    readonly List<WarfrontVoxLine> _sent = new();   // the last minute's lines, oldest first
    ushort _sequence;

    public WarfrontVox(int tickRate) => _rate = Math.Max(1, tickRate);

    /// <summary>The newest line made (null: none yet).</summary>
    public WarfrontVoxLine? Newest { get; private set; }
    /// <summary>Lines made, and offers dropped (tests, the log).</summary>
    public int Made { get; private set; }
    public int Dropped { get; private set; }

    static bool High(WarfrontVoxKind k) => k <= WarfrontVoxKind.Rallying;
    uint Ticks(float seconds) => (uint)MathF.Round(seconds * _rate);

    /// <summary>An event at <paramref name="tick"/>; a line may be made at once (see <see cref="Step"/>).</summary>
    public void Offer(WarfrontVoxKind kind, int from, int to, uint tick)
    {
        if (kind is < WarfrontVoxKind.LineBreaking or > WarfrontVoxKind.Flank) return;
        byte f = from is >= 0 and <= LandingZone ? (byte)from : None, t = to is >= 0 and <= LandingZone ? (byte)to : None;
        if (High(kind))
            for (int i = _pending.Count - 1; i >= 0; i--)
                if (_pending[i].From == f && High(_pending[i].Kind)) { _pending.RemoveAt(i); Dropped++; }   // a newer line about the same point replaces the older
        _pending.Add((kind, f, t, tick));
        Step(tick);
    }

    /// <summary>Makes at most one line at <paramref name="tick"/>: the waiting line of the highest priority (then the oldest) that the limits allow; the line made, or null.</summary>
    public WarfrontVoxLine? Step(uint tick)
    {
        uint window = Ticks(WindowSeconds);
        while (_sent.Count > 0 && tick - _sent[0].Tick >= window) _sent.RemoveAt(0);
        WarfrontVoxLine? made = null;
        int best = -1;
        for (int i = 0; i < _pending.Count; i++)
        {
            var p = _pending[i];
            if (!Allowed(p.Kind, p.From, tick)) continue;
            if (best < 0 || p.Kind < _pending[best].Kind) best = i;
        }
        if (best >= 0)
        {
            var p = _pending[best];
            _pending.RemoveAt(best);
            _sequence = (ushort)(_sequence == ushort.MaxValue ? 1 : _sequence + 1);
            var line = new WarfrontVoxLine(_sequence, p.Kind, p.From, p.To, tick);
            _sent.Add(line);
            Newest = made = line;
            Made++;
        }
        for (int i = _pending.Count - 1; i >= 0; i--)   // what may not wait: low priority lines blocked now, high priority lines past their wait
            if (!High(_pending[i].Kind) || tick - _pending[i].Tick >= Ticks(WaitSeconds)) { _pending.RemoveAt(i); Dropped++; }
        return made;
    }

    bool Allowed(WarfrontVoxKind kind, byte from, uint tick)
    {
        if (_sent.Count > 0 && tick - _sent[^1].Tick < Ticks(SpacingSeconds)) return false;
        if (_sent.Count >= PerMinute && kind > WarfrontVoxKind.FallingBack) return false;
        foreach (var s in _sent)
        {
            if (s.Kind != kind) continue;
            uint since = tick - s.Tick;
            if (since < Ticks(KindCooldownSeconds)) return false;
            if (s.From == from && since < Ticks(PointCooldownSeconds)) return false;
        }
        return true;
    }

    /// <summary>The line's text on the callout line: the letters are the points' (A, B, C).</summary>
    public static string Text(WarfrontVoxKind kind, byte from, byte to)
    {
        string f = Name(from), t = to == LandingZone ? "THE LANDING ZONE" : Name(to);
        return kind switch
        {
            WarfrontVoxKind.LineBreaking => $"LINE BREAKING AT {f}",
            WarfrontVoxKind.FallingBack => $"FALLING BACK TO {t}",
            WarfrontVoxKind.Rallying => $"RALLYING ON {f}",
            WarfrontVoxKind.Reinforcements => $"REINFORCEMENTS ARRIVING AT {f}",
            WarfrontVoxKind.Flank => $"FLANK AT {f}",
            _ => "",
        };
    }

    static string Name(byte point) => point < LandingZone ? ((char)('A' + point)).ToString() : point == LandingZone ? "THE LANDING ZONE" : "?";
}
