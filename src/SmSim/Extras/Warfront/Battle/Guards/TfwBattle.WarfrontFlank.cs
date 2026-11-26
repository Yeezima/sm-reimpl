using System.Numerics;
using SmSim.Extras.Warfront;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The approach by sectors and the flank answer (Guardsmen G1c). The approach is the heaviest block of three neighbouring 30 degree sectors of the enemies within 70 m (at least six of them), turned only after it
/// is more than 30 degrees off three steps in a row, so a flanking crowd no longer drags the whole line round. A block at least 75 degrees off the main bearing weighing 8 or more and 35 % of the main block for 4 s is a flank: one squad (two at
/// six squads or more; healthy, not in the front row, nearest the flank slot) turns to face it from half the radius out along its bearing while the others keep their slots; it goes back when the block weighs 3 or less
/// for 6 s. A battered flank squad is swapped at most once every 10 s.
/// </summary>
public sealed partial class TfwBattle
{
    readonly int[] _wfTurnSteps = new int[Positions], _wfFlankSteps = new int[Positions], _wfFlankClearSteps = new int[Positions], _wfFlankSwapAt = new int[Positions];
    readonly bool[] _wfFlankOn = new bool[Positions];
    readonly Vector2[] _wfFlankDir = new Vector2[Positions];
    readonly int[,] _wfFlankSquad = InitFlank();

    static int[,] InitFlank()
    {
        var a = new int[Positions, WarfrontLineRules.FlankSquadsMost];
        for (int k = 0; k < Positions; k++) for (int j = 0; j < WarfrontLineRules.FlankSquadsMost; j++) a[k, j] = -1;
        return a;
    }

    /// <summary>Whether point <paramref name="k"/> answers a flank now, its bearing, and the squads that answer it (tests).</summary>
    public bool WarfrontFlankOn(int k) => k is >= 0 and < Positions && _wfFlankOn[k];
    /// <summary>Flanks raised so far (a sustained flank's start; tests, the tools' rates).</summary>
    public int WarfrontFlanksRaised { get; private set; }
    public Vector2 WarfrontFlankBearing(int k) => k is >= 0 and < Positions ? _wfFlankDir[k] : Vector2.Zero;
    public IEnumerable<int> WarfrontFlankSquads(int k)
    {
        if (k is < 0 or >= Positions) yield break;
        for (int j = 0; j < WarfrontLineRules.FlankSquadsMost; j++) if (_wfFlankSquad[k, j] >= 0) yield return _wfFlankSquad[k, j];
    }

    /// <summary>The heaviest block of three neighbouring sectors of point <paramref name="k"/> (ties to the lower first sector), optionally only among sectors at least <paramref name="awayDegrees"/> from <paramref name="from"/>; its first sector and weight (-1: none).</summary>
    (int First, float Weight) WfHeaviestBlock(int k, Vector2 from, float awayDegrees)
    {
        int best = -1;
        float most = 0f;
        for (int i = 0; i < WarfrontLineRules.Sectors; i++)
        {
            float w = 0f;
            bool ok = true;
            for (int j = 0; j < 3 && ok; j++)
            {
                int sct = (i + j) % WarfrontLineRules.Sectors;
                if (awayDegrees > 0f && SectorAngleFrom(sct, from) < awayDegrees) ok = false;
                w += _wfSector[k, sct];
            }
            if (ok && w > most) { most = w; best = i; }
        }
        return (best, most);
    }

    static float SectorAngleFrom(int sector, Vector2 from)
    {
        float width = 360f / WarfrontLineRules.Sectors, centre = (sector + 0.5f) * width * MathF.PI / 180f;
        var c = new Vector2(MathF.Cos(centre), MathF.Sin(centre));
        return MathF.Acos(Math.Clamp(Vector2.Dot(c, from), -1f, 1f)) * 180f / MathF.PI;
    }

    Vector2 WfBlockBearing(int k, int first)
    {
        var sum = Vector2.Zero;
        for (int j = 0; j < 3; j++) sum += _wfSectorSum[k, (first + j) % WarfrontLineRules.Sectors];
        return sum.LengthSquared() > 1e-6f ? Vector2.Normalize(sum) : Vector2.Zero;
    }

    /// <summary>The line's approach of point <paramref name="k"/> (sectors and hysteresis); <see cref="Approach"/> is the restore switch's mean bearing.</summary>
    Vector2 WfApproach(int k)
    {
        var point = _layout.Positions[k];
        var want = Vector2.Zero;
        if (_wfNearCount[k] >= WarfrontGuardRules.ApproachOrks)
        {
            var (first, _) = WfHeaviestBlock(k, Vector2.Zero, 0f);
            if (first >= 0) want = WfBlockBearing(k, first);
        }
        if (want == Vector2.Zero)
        {
            if (_wfApproach[k] != Vector2.Zero) { _wfTurnSteps[k] = 0; return _wfApproach[k]; }
            var src = _layout.Sources.Length > k ? _layout.Sources[k] : _layout.Hq;
            want = new Vector2(src.X - point.X, src.Y - point.Y);
            if (want.LengthSquared() < 4f) want = new Vector2(_layout.Hq.X - point.X, _layout.Hq.Y - point.Y);
            if (want.LengthSquared() < 4f) want = Vector2.UnitY;
            want = Vector2.Normalize(want);
        }
        var now = _wfApproach[k];
        if (now == Vector2.Zero) { _wfTurnSteps[k] = 0; return _wfApproach[k] = want; }
        bool off = MathF.Acos(Math.Clamp(Vector2.Dot(now, want), -1f, 1f)) * 180f / MathF.PI > WarfrontGuardRules.ApproachTurnDegrees;
        _wfTurnSteps[k] = off ? _wfTurnSteps[k] + 1 : 0;
        if (_wfTurnSteps[k] >= WarfrontLineRules.TurnSteps) { _wfTurnSteps[k] = 0; _wfApproach[k] = now = want; WfFlankOff(k); }   // the line turned: a flank is read again from the new front
        return now;
    }

    /// <summary>Once a second: each Holding point's flank, sustained or cleared.</summary>
    void WfFlankStep()
    {
        for (int k = 0; k < Positions; k++)
        {
            if (!Imperial(k) || _wfLine[k] != WarfrontLineState.Holding || _wfApproach[k] == Vector2.Zero || _wfMen[k] == 0) { WfFlankOff(k); continue; }   // G3 finding 4: no flank where no squad stands
            var (first, weight) = WfHeaviestBlock(k, _wfApproach[k], WarfrontLineRules.FlankDegrees);
            float raise = MathF.Max(WarfrontLineRules.FlankWeight, WarfrontLineRules.FlankMainShare * WfHeaviestBlock(k, Vector2.Zero, 0f).Weight);   // relative to the main push (lead 42)
            if (!_wfFlankOn[k])
            {
                _wfFlankSteps[k] = first >= 0 && weight >= raise ? _wfFlankSteps[k] + 1 : 0;
                if (_wfFlankSteps[k] < WarfrontLineRules.FlankSeconds) continue;
                _wfFlankOn[k] = true;
                WarfrontFlanksRaised++;
                _wfFlankClearSteps[k] = 0;
                _wfFlankDir[k] = WfBlockBearing(k, first);
                WfVox(WarfrontVoxKind.Flank, k);
                continue;
            }
            if (first < 0 || weight <= WarfrontLineRules.FlankClearWeight)
            {
                if (++_wfFlankClearSteps[k] >= WarfrontLineRules.FlankClearSeconds) WfFlankOff(k);
                continue;
            }
            _wfFlankClearSteps[k] = 0;
            if (weight >= raise) _wfFlankDir[k] = WfBlockBearing(k, first);
        }
    }

    void WfFlankOff(int k)
    {
        _wfFlankOn[k] = false;
        _wfFlankSteps[k] = 0;
        _wfFlankClearSteps[k] = 0;
        for (int j = 0; j < WarfrontLineRules.FlankSquadsMost; j++) _wfFlankSquad[k, j] = -1;
    }

    /// <summary>
    /// The formation's flank pick at point <paramref name="k"/> (its squads <paramref name="squads"/> in rank order): keeps the flank squads that still qualify, fills the rest with the nearest healthy squad behind the
    /// front row (a battered one is swapped at most once every <see cref="WarfrontLineRules.FlankSwapSeconds"/>).
    /// </summary>
    void WfPickFlank(int k, List<int> squads, Vector3 point)
    {
        if (!WfLine || !_wfFlankOn[k] || squads.Count < 2)
        {
            for (int j = 0; j < WarfrontLineRules.FlankSquadsMost; j++) _wfFlankSquad[k, j] = -1;
            return;
        }
        int want = squads.Count >= WarfrontLineRules.FlankTwoAt ? 2 : 1;
        bool swapOk = _now >= _wfFlankSwapAt[k];
        for (int j = 0; j < WarfrontLineRules.FlankSquadsMost; j++)
        {
            int s = _wfFlankSquad[k, j];
            if (s < 0) continue;
            bool gone = j >= want || !squads.Contains(s) || _guards.AliveIn(s) == 0;
            bool battered = _guards.AliveIn(s) <= WarfrontGuardRules.BatteredMen;
            if (gone || (battered && swapOk)) { _wfFlankSquad[k, j] = -1; if (battered && !gone) { _wfFlankSwapAt[k] = _now + Ticks(WarfrontLineRules.FlankSwapSeconds); swapOk = false; } }
        }
        for (int j = 0; j < want; j++)
        {
            if (_wfFlankSquad[k, j] >= 0) continue;
            var slot = SlotPlace(point, _wfFlankDir[k], j, want, RadiusOf(k));
            int best = -1;
            float bestD = float.PositiveInfinity;
            for (int i = 0; i < squads.Count; i++)
            {
                int s = squads[i];
                if (SlotGrid(i, squads.Count).Row == 0 || _guards.AliveIn(s) <= WarfrontGuardRules.BatteredMen || WfIsFlank(k, s)) continue;
                float d = Vector3.DistanceSquared(_guards.Centre(s), slot);
                if (d < bestD || (d == bestD && s < best)) { bestD = d; best = s; }
            }
            _wfFlankSquad[k, j] = best;
        }
    }

    bool WfIsFlank(int k, int s)
    {
        for (int j = 0; j < WarfrontLineRules.FlankSquadsMost; j++) if (_wfFlankSquad[k, j] == s) return true;
        return false;
    }

    int WfFlankCount(int k)
    {
        int n = 0;
        for (int j = 0; j < WarfrontLineRules.FlankSquadsMost; j++) if (_wfFlankSquad[k, j] >= 0) n++;
        return n;
    }

    int WfFlankIndex(int k, int s)
    {
        for (int j = 0; j < WarfrontLineRules.FlankSquadsMost; j++) if (_wfFlankSquad[k, j] == s) return j;
        return -1;
    }
}
