using System.Numerics;
using SmSim.Extras.Warfront;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>The state of a held point's line (Guardsmen G1b).</summary>
public enum WarfrontLineState : byte { Holding = 0, Breaking = 1, Withdrawn = 2, Rallying = 3 }

/// <summary>
/// Warfront's Guardsmen hold, fall back and rally (Guardsmen G1b; owner brief 2026-11-24 rev 1, design state/design-guardsmen-1125.md and the owner's answers of 2026-11-25; every value in <see cref="WarfrontLineRules"/>).
/// Once a second, before the squads' posts are shared out, each secured point of a lane battle (Ork or Chaos stage; never the HQ defence or the fortress) reads its pressure (weighted enemies within 40 m) and its men.
/// A point whose squads are all battered, with the pressure at twice its men or more and no relief near, is Breaking; four seconds of it and every real squad there (field squads and the garrison's promoted ones)
/// is withheld (<see cref="WithheldSquad"/>) and walks together to the nearest friendly point that holds (else the landing zone), where it stands in that point's rows. The garrison's aggregate remainder retreats
/// there too (the retreat hook). When the push on the origin is broken (by anyone) while it still stands, the same squads walk back and re-form; a 45 s lockout follows. If the origin falls they stay where they went.
/// Owner answers kept here: the garrison's own 50 % loss retreat stays (no H1); reinforcements and reserves still flow to a fallen-back point (no H6, no H7, no zero need). Every hook is a no-op outside <see cref="WfLine"/>.
/// </summary>
public sealed partial class TfwBattle
{
    /// <summary>_squadArmy's mark for a squad withheld by a fall back: outside the army id range (bytes), so <see cref="ArmySquad"/> holds, StepSquads skips it and no army list holds it.</summary>
    const int WithheldSquad = 1000;

    sealed class WfRecord
    {
        public int Origin, Dest, Tick, RallyTick = -1, LastResend = -1;   // Dest: a point 0..2 or BasePlace
        public float PushAtBreak;
        public readonly List<int> Squads = new();
        public readonly List<bool> FromGarrison = new();
        public readonly List<bool> Resent = new();
    }

    readonly WarfrontLineState[] _wfLine = new WarfrontLineState[Positions];
    readonly int[] _wfBreakSteps = new int[Positions], _wfBrokenSteps = new int[Positions], _wfLockoutUntil = new int[Positions], _wfWithdrawnAt = Enumerable.Repeat(int.MinValue / 2, Positions).ToArray();
    readonly float[] _wfPressure = new float[Positions], _wfPushRef = new float[Positions];
    readonly int[] _wfMen = new int[Positions], _wfHealthy = new int[Positions], _wfNearCount = new int[Positions];
    readonly float[,] _wfSector = new float[Positions, WarfrontLineRules.Sectors];
    readonly Vector2[,] _wfSectorSum = new Vector2[Positions, WarfrontLineRules.Sectors];
    readonly List<WfRecord> _wfRecords = new();
    readonly int[] _wfArmyRetreatAt = new int[256];
    readonly ushort[] _wfQuery = new ushort[512];
    WarfrontVox? _wfVox;

    /// <summary>Test aid and the tools' paired runs: true turns the line off for this battle (as <c>SM_WF_GUARD_AI_OLD=1</c> does for every battle).</summary>
    public bool WarfrontLineOff { get; set; } = WarfrontLineRules.Old;

    /// <summary>Whether the line runs in this battle: a Warfront lane battle (Ork or Chaos stage, owner 2026-11-25 "New in both") with Guardsmen and the battle flow; never the HQ defence, the fortress, Musou Mode or Classic.</summary>
    bool WfLine => !WarfrontLineOff && !WarfrontGuardRules.Old && _enemies.Warfront is not null && WarfrontNode && !WarfrontHq && !IsWarfrontFortress && _guardsmen && Armies && _guards.Rules.Spread;

    /// <summary>Whether the line runs in this battle (tests).</summary>
    public bool WarfrontLineOn => WfLine;
    /// <summary>A point's line state (tests, the tools).</summary>
    public WarfrontLineState WarfrontLineOf(int k) => k is >= 0 and < Positions ? _wfLine[k] : WarfrontLineState.Holding;
    /// <summary>A point's pressure and men at the last line step (tests, the tools).</summary>
    public float WarfrontPressure(int k) => k is >= 0 and < Positions ? _wfPressure[k] : 0f;
    public int WarfrontMen(int k) => k is >= 0 and < Positions ? _wfMen[k] : 0;
    /// <summary>Squads withheld now (walking away, standing at the destination, or walking back), and their record's origin and destination (tests).</summary>
    public IEnumerable<(int Squad, int Origin, int Dest)> WarfrontWithheld()
    {
        foreach (var r in _wfRecords) foreach (int s in r.Squads) yield return (s, r.Origin, r.Dest);
    }
    /// <summary>Withdrawals made, refused (nothing real to withhold), and rallies of the line and of retreating armies (tests, the tools).</summary>
    public int WarfrontWithdrawals { get; private set; }
    public int WarfrontWithdrawalsRefused { get; private set; }
    /// <summary>Test aid (the tools' trace): a line fell back (origin, destination, pressure, men, healthy squads at the break).</summary>
    public Action<int, int, float, int, int>? OnWarfrontWithdraw { get; set; }
    public int WarfrontLineRallies { get; private set; }
    /// <summary>The newest vox line (null: none) and the limiter (tests, the server's send).</summary>
    public WarfrontVoxLine? WarfrontVoxNewest => _wfVox?.Newest;
    public WarfrontVox? WarfrontVoxLimiter => _wfVox;

    void WfVox(WarfrontVoxKind kind, int from, int to = WarfrontVox.None)
    {
        _wfVox ??= new WarfrontVox(Math.Max(1, _enemies.TickRate));
        _wfVox.Offer(kind, from, to, (uint)_now);
    }

    /// <summary>The once-a-second line step (top of StepSquads, before the field's posts are shared out).</summary>
    partial void WarfrontLine()
    {
        if (!WfLine) return;
        long section = SimProfile.Begin();
        _wfVox?.Step((uint)_now);
        for (int k = 0; k < Positions; k++) WfSnapshot(k);
        WfTidyRecords();
        for (int k = 0; k < Positions; k++)
        {
            float floor = MathF.Max(WarfrontLineRules.BrokenFloor, WarfrontLineRules.BrokenShare * _wfPushRef[k]);
            float ring = 0f;   // lead 42 (round 3): the approach ring (the sectors' enemies within RallyRingRadius) must be as quiet, so no survivor is fed back into a fight still on at the flank ring
            if (WarfrontLineRules.RallyRingQuiet) for (int i = 0; i < WarfrontLineRules.Sectors; i++) ring += _wfSector[k, i];
            _wfBrokenSteps[k] = Imperial(k) && _wfPressure[k] <= floor && ring <= floor ? _wfBrokenSteps[k] + 1 : 0;
        }
        for (int k = 0; k < Positions; k++) WfStepPoint(k);
        WfFlankStep();   // TfwBattle.WarfrontFlank.cs
        SimProfile.End("guards: warfront line", section);
    }

    /// <summary>Point <paramref name="k"/>'s pressure, sectors, men and healthy squads now.</summary>
    void WfSnapshot(int k)
    {
        for (int i = 0; i < WarfrontLineRules.Sectors; i++) { _wfSector[k, i] = 0f; _wfSectorSum[k, i] = Vector2.Zero; }
        _wfPressure[k] = 0f; _wfNearCount[k] = 0; _wfMen[k] = 0; _wfHealthy[k] = 0;
        var point = _layout.Positions[k];
        float reach = _layout.Reach(k), push2 = WarfrontLineRules.PushRadius * WarfrontLineRules.PushRadius;
        int n = _enemies.Query(point, WarfrontGuardRules.ApproachRadius, _wfQuery, WarfrontGuardRules.ApproachRadius);
        for (int i = 0; i < n; i++)
        {
            if (!_enemies.TryGet(_wfQuery[i], out var e) || !e.Alive) continue;
            var off = new Vector2(e.Position.X - point.X, e.Position.Y - point.Y);
            float w = WarfrontLineRules.Weight(e.Type), d2 = off.LengthSquared();
            if (d2 <= push2 && MathF.Abs(e.Position.Z - point.Z) <= reach) _wfPressure[k] += w;
            if (d2 < 1e-4f) continue;
            _wfNearCount[k]++;
            int sector = SectorOf(off);
            _wfSector[k, sector] += w;
            _wfSectorSum[k, sector] += off / MathF.Sqrt(d2) * w;
        }
        if (!Imperial(k)) return;
        float men2 = (RadiusOf(k) + WarfrontLineRules.MenSlack) * (RadiusOf(k) + WarfrontLineRules.MenSlack);
        for (int s = 0; s < GuardSim.MaxSquads; s++)
        {
            int living = _guards.AliveIn(s);
            if (living == 0 || VantageSquad(s) || !WfStandsFor(s, k)) continue;
            var c = _guards.Centre(s);
            if (new Vector2(c.X - point.X, c.Y - point.Y).LengthSquared() > men2) continue;
            _wfMen[k] += living;
            if (living > WarfrontGuardRules.BatteredMen) _wfHealthy[k]++;
        }
        if (GarrisonOf(k) is { } g)
        {
            _wfMen[k] += g.FarCount;
            _wfHealthy[k] += g.FarCount / WarfrontLineRules.AggregatePerHealthy;
        }
    }

    /// <summary>Whether squad <paramref name="s"/> belongs to point <paramref name="k"/>'s line: a field squad posted there, a squad of its garrison, or a withheld squad standing there in a record's rows.</summary>
    bool WfStandsFor(int s, int k)
    {
        int owner = _squadArmy[s];
        if (owner < 0) return _squadGoal[s] == k;
        if (owner == WithheldSquad)
        {
            foreach (var r in _wfRecords) if (((r.Dest == k && r.RallyTick < 0) || (r.Origin == k && r.RallyTick >= 0)) && r.Squads.Contains(s)) return true;
            return false;
        }
        foreach (var a in _armies) if (a.Id == owner) return a.Side == TfwArmySide.Guard && a.Order == TfwArmyOrder.Hold && a.Goal == k;
        return false;
    }

    static int SectorOf(Vector2 off)
    {
        float deg = MathF.Atan2(off.Y, off.X) * 180f / MathF.PI;
        if (deg < 0f) deg += 360f;
        return Math.Min(WarfrontLineRules.Sectors - 1, (int)(deg / (360f / WarfrontLineRules.Sectors)));
    }

    /// <summary>The dead leave their records (a dead squad's mark is cleared before GuardSim frees its slot, 2 s after the last death).</summary>
    void WfTidyRecords()
    {
        foreach (var r in _wfRecords)
            for (int j = r.Squads.Count - 1; j >= 0; j--)
                if (_guards.AliveIn(r.Squads[j]) == 0) { _squadArmy[r.Squads[j]] = -1; WfRemoveAt(r, j); }
    }

    static void WfRemoveAt(WfRecord r, int j) { r.Squads.RemoveAt(j); r.FromGarrison.RemoveAt(j); r.Resent.RemoveAt(j); }

    WfRecord? WfRecordOf(int k) { foreach (var r in _wfRecords) if (r.Origin == k) return r; return null; }

    void WfStepPoint(int k)
    {
        var rec = WfRecordOf(k);
        switch (_wfLine[k])
        {
            case WarfrontLineState.Holding or WarfrontLineState.Breaking:
            {
                if (!Imperial(k)) { _wfLine[k] = WarfrontLineState.Holding; _wfBreakSteps[k] = 0; return; }
                bool collapse = WfCollapse(k);
                if (!collapse)
                {
                    _wfLine[k] = WarfrontLineState.Holding;   // a Breaking that recovers says nothing
                    _wfBreakSteps[k] = 0;
                    return;
                }
                if (_wfLine[k] == WarfrontLineState.Holding)
                {
                    _wfLine[k] = WarfrontLineState.Breaking;
                    _wfBreakSteps[k] = 1;
                    WfVox(WarfrontVoxKind.LineBreaking, k);
                    return;
                }
                if (++_wfBreakSteps[k] >= WarfrontLineRules.BreakSeconds) WfFallBack(k);
                return;
            }
            case WarfrontLineState.Withdrawn:
            {
                if (rec is null) { WfHold(k, lockout: false); return; }
                if (!Imperial(k)) { WfRetire(rec); WfHold(k, lockout: false); return; }
                if (rec.Dest < Positions && !Imperial(rec.Dest)) WfRetarget(rec);
                if (rec.Squads.Count == 0) { WfDrop(rec); WfHold(k, lockout: false); return; }
                if (_now - rec.Tick >= Ticks(WarfrontLineRules.MinWithdrawnSeconds) && _wfBrokenSteps[k] >= WarfrontLineRules.BrokenSeconds) WfRally(rec);
                return;
            }
            case WarfrontLineState.Rallying:
            {
                if (rec is null) { WfHold(k, lockout: true); return; }
                if (!Imperial(k)) { WfRetire(rec); WfHold(k, lockout: false); return; }
                WfStepRally(rec);
                if (rec.Squads.Count == 0) { WfDrop(rec); WfHold(k, lockout: true); }
                return;
            }
        }
    }

    void WfHold(int k, bool lockout)
    {
        _wfLine[k] = WarfrontLineState.Holding;
        _wfBreakSteps[k] = 0;
        if (lockout) _wfLockoutUntil[k] = _now + Ticks(WarfrontLineRules.RallyLockoutSeconds);
    }

    void WfDrop(WfRecord r) => _wfRecords.Remove(r);

    /// <summary>The collapse rule: men at least one, pressure at least twice the men with no healthy squad (or six times with one: overwhelmed), no relief, not in the lockout.</summary>
    bool WfCollapse(int k)
    {
        if (_now < _wfLockoutUntil[k] || _wfMen[k] < 1) return false;
        if (_wfHealthy[k] > WarfrontLineRules.OverwhelmHealthyMost) return false;   // two healthy squads or more fight on, whatever the pressure (the G1 round 2 trials: a line of several healthy squads that fell back handed its point to the Orks and the next points were overrun from it)
        float ratio = _wfHealthy[k] > 0 ? WarfrontLineRules.OverwhelmRatio : WarfrontLineRules.CollapseRatio;
        if (_wfPressure[k] < ratio * _wfMen[k]) return false;
        return !WfRelief(k);
    }

    /// <summary>A healthy squad or a Guard army bound for point <paramref name="k"/> within <see cref="WarfrontLineRules.ReliefSeconds"/>.</summary>
    bool WfRelief(int k)
    {
        float most = WarfrontLineRules.ReliefSeconds * WarfrontLineRules.ReliefSpeed, radius = RadiusOf(k) + WarfrontLineRules.MenSlack;
        var point = _layout.Positions[k];
        for (int s = 0; s < GuardSim.MaxSquads; s++)
        {
            if (_guards.AliveIn(s) <= WarfrontGuardRules.BatteredMen || ArmySquad(s) || VantageSquad(s) || _squadGoal[s] != k) continue;
            float d = FlatDistance(_guards.Centre(s), point);
            if (d > radius && d - radius <= most) return true;
        }
        foreach (var a in _armies)
            if (a.Side == TfwArmySide.Guard && a.Order == TfwArmyOrder.March && a.Goal == k && a.Count > 0 && a.Way is { } way && (way.Length - a.Along) <= most) return true;
        return false;
    }

    /// <summary>
    /// Where point <paramref name="k"/>'s line falls back to: the nearest (by searched route) other Imperial point that holds, has a searched route from <paramref name="k"/> and pressure below twice its men (or none);
    /// ties to the lower index; else the landing zone (<see cref="BasePlace"/>).
    /// </summary>
    int WfFallBackPlace(int k)
    {
        int best = BasePlace;
        float bestLength = float.PositiveInfinity;
        for (int d = 0; d < Positions; d++)
        {
            if (d == k || !Imperial(d) || _wfLine[d] != WarfrontLineState.Holding) continue;
            if (k is < 0 or >= Positions || _ways[k, d] is not { } way) continue;
            if (_wfPressure[d] > 0f && _wfPressure[d] >= WarfrontLineRules.CollapseRatio * _wfMen[d]) continue;
            if (way.Length < bestLength) { bestLength = way.Length; best = d; }
        }
        return best;
    }

    /// <summary>Breaking confirmed: every real squad of the line is withheld and walks to the fall-back place; the garrison's aggregate remainder retreats there.</summary>
    void WfFallBack(int k)
    {
        int dest = WfFallBackPlace(k);
        var rec = new WfRecord { Origin = k, Dest = dest, Tick = _now, PushAtBreak = _wfPressure[k] };
        var garrison = GarrisonOf(k);
        for (int s = 0; s < GuardSim.MaxSquads; s++)
        {
            if (_guards.AliveIn(s) == 0 || VantageSquad(s)) continue;
            if (_squadArmy[s] == -1 && _squadGoal[s] == k) { WfWithhold(rec, s, fromGarrison: false); continue; }
            if (_squadArmy[s] == WithheldSquad && WfStandsFor(s, k))   // a newer record takes the squads of an older one that stood here (their origin is now k)
                foreach (var old in _wfRecords)
                {
                    int j = old.Squads.IndexOf(s);
                    if (j < 0) continue;
                    bool g = old.FromGarrison[j];
                    WfRemoveAt(old, j);
                    WfWithhold(rec, s, g);
                    break;
                }
        }
        if (garrison is not null)
            for (int j = garrison.Squads.Count - 1; j >= 0; j--)
            {
                int s = garrison.Squads[j];
                if (VantageSquad(s) || _guards.AliveIn(s) == 0) continue;
                garrison.Real -= garrison.SquadLiving[j];
                garrison.Squads.RemoveAt(j); garrison.SquadBlocks.RemoveAt(j); garrison.SquadLiving.RemoveAt(j);
                WfWithhold(rec, s, fromGarrison: true);
            }
        WfAlign(rec);   // squads in index order (the garrison's were taken back to front)
        _wfLine[k] = WarfrontLineState.Withdrawn;
        _wfBreakSteps[k] = 0;
        _wfBrokenSteps[k] = 0;
        _wfPushRef[k] = rec.PushAtBreak;
        _wfWithdrawnAt[k] = _now;
        if (garrison is { FarCount: > 0 }) Retreat(garrison);   // the aggregate remainder (fewer than ten men, or there would be a healthy squad) goes the same way, through the retreat hook
        if (rec.Squads.Count == 0 && garrison is not { FarCount: > 0 })
        {
            WarfrontWithdrawalsRefused++;
            WfHold(k, lockout: false);
            return;
        }
        _wfRecords.Add(rec);
        WarfrontWithdrawals++;
        OnWarfrontWithdraw?.Invoke(k, dest, rec.PushAtBreak, _wfMen[k], _wfHealthy[k]);
        for (int j = 0; j < rec.Squads.Count; j++) WfSendTo(rec.Squads[j], dest, j);
        WfVox(WarfrontVoxKind.FallingBack, k, dest);
    }

    readonly bool[] _wfAlignG = new bool[GuardSim.MaxSquads], _wfAlignR = new bool[GuardSim.MaxSquads];

    /// <summary>A record's squads in index order, their flags with them.</summary>
    void WfAlign(WfRecord rec)
    {
        for (int j = 0; j < rec.Squads.Count; j++) { _wfAlignG[rec.Squads[j]] = rec.FromGarrison[j]; _wfAlignR[rec.Squads[j]] = rec.Resent[j]; }
        rec.Squads.Sort();
        for (int j = 0; j < rec.Squads.Count; j++) { rec.FromGarrison[j] = _wfAlignG[rec.Squads[j]]; rec.Resent[j] = _wfAlignR[rec.Squads[j]]; }
    }

    void WfWithhold(WfRecord rec, int s, bool fromGarrison)
    {
        _squadArmy[s] = WithheldSquad;
        _squadGoal[s] = -1;
        rec.Squads.Add(s); rec.FromGarrison.Add(fromGarrison); rec.Resent.Add(false);
    }

    /// <summary>Squad <paramref name="s"/> (the <paramref name="j"/>th of its record) walks to its own spot round <paramref name="place"/> (a point or the base).</summary>
    void WfSendTo(int s, int place, int j)
    {
        int goal = place == BasePlace ? LandingGoal : place;
        var at = PlaceOf(goal);
        var offset = SquadOffset(j);
        float most = RadiusOf(goal) * TfwBattleRules.SquadOnPoint;
        if (offset.Length() > most) offset *= most / offset.Length();
        _guards.Order(s, Ground(at + offset, at), at, RadiusOf(goal));
    }

    /// <summary>The destination fell while the squads were there or on their way: the next fall-back place from it.</summary>
    void WfRetarget(WfRecord rec)
    {
        int next = WfFallBackPlace(rec.Origin);
        if (next == rec.Dest) return;
        rec.Dest = next;
        for (int j = 0; j < rec.Squads.Count; j++) WfSendTo(rec.Squads[j], next, j);
        WfVox(WarfrontVoxKind.FallingBack, rec.Origin, next);
    }

    /// <summary>The push is broken and the origin stands: the same squads walk back.</summary>
    void WfRally(WfRecord rec)
    {
        rec.RallyTick = _now;
        _wfLine[rec.Origin] = WarfrontLineState.Rallying;
        for (int j = 0; j < rec.Squads.Count; j++) { rec.Resent[j] = false; WfSendTo(rec.Squads[j], rec.Origin, j); }
        WarfrontLineRallies++;
        WfVox(WarfrontVoxKind.Rallying, rec.Origin);
    }

    /// <summary>
    /// Rallying (G3 finding 2, 2026-11-27): a squad that reached its point stands in its rows, still withheld, until every living squad of the record is back; one that stopped short is sent again (at most every
    /// <see cref="WarfrontLineRules.RallyResendSeconds"/>). When all are back, or after <see cref="WarfrontLineRules.RallyGiveUpSeconds"/>, they rejoin together: field squads posted at the point, garrison squads its
    /// garrison (a squad still away at the give-up goes to the field, posted at the point).
    /// </summary>
    void WfStepRally(WfRecord rec)
    {
        int k = rec.Origin;
        float reach = RadiusOf(k) + WarfrontLineRules.MenSlack;
        bool giveUp = _now - rec.RallyTick >= Ticks(WarfrontLineRules.RallyGiveUpSeconds), allBack = true, resend = rec.LastResend < 0 || _now - rec.LastResend >= Ticks(WarfrontLineRules.RallyResendSeconds), resent = false;
        for (int j = 0; j < rec.Squads.Count; j++)
        {
            int s = rec.Squads[j];
            if (FlatDistance(_guards.Centre(s), _layout.Positions[k]) <= reach) continue;
            allBack = false;
            if (!giveUp && resend && _guards.Arrived(s)) { rec.Resent[j] = true; WfSendTo(s, k, j); resent = true; }
        }
        if (resent) rec.LastResend = _now;
        if (!allBack && !giveUp) return;
        for (int j = rec.Squads.Count - 1; j >= 0; j--)
        {
            int s = rec.Squads[j];
            bool there = FlatDistance(_guards.Centre(s), _layout.Positions[k]) <= reach;
            WfRelease(s, rec.FromGarrison[j] && there ? k : -1, k);
            WfRemoveAt(rec, j);
        }
    }

    /// <summary>The origin fell: the record ends; its squads stay where they went (field squads posted at the destination, garrison squads into its garrison).</summary>
    void WfRetire(WfRecord rec)
    {
        int goal = rec.Dest == BasePlace ? LandingGoal : rec.Dest;
        bool held = rec.Dest < Positions && Imperial(rec.Dest);
        for (int j = rec.Squads.Count - 1; j >= 0; j--)
            WfRelease(rec.Squads[j], rec.FromGarrison[j] && held ? rec.Dest : -1, held || rec.Dest == BasePlace ? goal : -1);
        rec.Squads.Clear(); rec.FromGarrison.Clear(); rec.Resent.Clear();
        WfDrop(rec);
    }

    /// <summary>A withheld squad goes back: into point <paramref name="garrisonAt"/>'s garrison when it has room (-1: none), else to the field posted at <paramref name="post"/> (-1: wherever the field wants it).</summary>
    void WfRelease(int s, int garrisonAt, int post)
    {
        int living = _guards.AliveIn(s);
        if (living > 0 && garrisonAt is >= 0 and < Positions && EnsureGarrison(garrisonAt) is { } g && GarrisonCapNow - g.Count >= living)
        {
            int block = (g.SlotBase + GuardSim.SquadSize - 1) / GuardSim.SquadSize;
            g.Squads.Add(s); g.SquadBlocks.Add(block); g.SquadLiving.Add(living);
            g.SlotBase = (block + 1) * GuardSim.SquadSize;
            g.Real += living;
            _squadArmy[s] = g.Id;
            OrderSquad(g, g.Squads.Count - 1);
            return;
        }
        _squadArmy[s] = -1;
        _squadGoal[s] = living > 0 ? post : -1;
    }

    /// <summary>The withheld squads standing in point <paramref name="k"/>'s rows (a record's, after its fall back; not while they walk back).</summary>
    void WfRegrouped(int k, List<int> into)
    {
        float reach = RadiusOf(k) + WarfrontLineRules.MenSlack;
        foreach (var r in _wfRecords)
        {
            if (r.Dest == k && r.RallyTick < 0)
                foreach (int s in r.Squads) if (_guards.AliveIn(s) > 0 && !into.Contains(s)) into.Add(s);
            if (r.Origin == k && r.RallyTick >= 0)   // a rallying squad that is back stands in its own point's rows while the others come
                foreach (int s in r.Squads) if (_guards.AliveIn(s) > 0 && !into.Contains(s) && FlatDistance(_guards.Centre(s), _layout.Positions[k]) <= reach) into.Add(s);
        }
    }

    // ---- Hooks in the Musou files (each a no-op outside WfLine) ----

    /// <summary>H2: a Guard army's retreat goes to the fall-back place of its point (a friendly point that holds, else the base), not always the base.</summary>
    partial void WarfrontGuardFallBack(TfwArmy a, ref int place)
    {
        if (!WfLine) return;
        _wfArmyRetreatAt[a.Id] = _now;
        int k = a.Post;
        if (k is < 0 or >= Positions) return;
        place = WfFallBackPlace(k);
        if (Imperial(k)) _wfPushRef[k] = MathF.Max(_wfPressure[k], WarfrontLineRules.BrokenFloor);
    }

    /// <summary>H3: in a lane battle the proximity rally is off (owner 2026-11-25); a retreating Guard army rallies to its own point when the push there is broken and the point stands.</summary>
    partial void WarfrontRallies(ref bool skip)
    {
        if (!WfLine) return;
        skip = true;
        foreach (var a in _armies)
        {
            if (a.Side != TfwArmySide.Guard || a.Order != TfwArmyOrder.Retreat || _now < a.RallyReady) continue;
            int k = a.Post;
            if (k is < 0 or >= Positions || !Imperial(k)) continue;
            if (_now - _wfArmyRetreatAt[a.Id] < Ticks(WarfrontLineRules.MinWithdrawnSeconds) || _wfBrokenSteps[k] < WarfrontLineRules.BrokenSeconds) continue;
            Rally(a, k);
            WarfrontLineRallies++;
            WfVox(WarfrontVoxKind.Rallying, k);
        }
    }
}
