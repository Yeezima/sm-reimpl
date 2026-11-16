using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The Guardsmen's vantage teams (owner, 2026-11-09; <see cref="GuardVantageRules"/>, spec "Musou Mode: Guard vantage teams"): every so often a small team of a field squad standing at its post goes up to a
/// vantage point (<see cref="GuardVantage"/>) within 50 m of a friendly-captured point (with none, in front of the base, towards the Ork HQ, within the base's radius), by the walking graph's way (its
/// real stairs), holds it on the spot's own floor facing its open side, fires from there, and after its hold goes back and rejoins its squad. A team whose point fell, or whose base-front spot no
/// longer counts once a point is taken, takes the nearest spot that counts, else goes back. A wiped team frees its spot. One team a spot. Field squads only: an army's squads keep their army's orders.
/// </summary>
public sealed partial class TfwBattle
{
    enum VantageStage : byte { Going, Holding, Returning }

    sealed class VantageTeam
    {
        public int Squad, Parent, Spot, HoldUntil;
        public VantageStage Stage;
        public Vector3 ParentAt;
    }

    readonly List<VantageTeam> _vantageTeams = new();
    readonly bool[] _vantageSquad = new bool[GuardSim.MaxSquads];
    Task<GuardVantagePoint[]>? _vantageTask;
    GuardVantagePoint[]? _vantagePoints;
    int _vantageTicks, _vantageOrderTicks, _vantageRolls;

    /// <summary>The vantage rules (tests may set their own; <see cref="GuardVantageRules.Enabled"/> false is the restore switch).</summary>
    public GuardVantageRules VantageRules { get; set; } = GuardVantageRules.Default;
    /// <summary>Teams sent up so far, and the teams out now (tests, the server's log).</summary>
    public int VantageTeamsSent { get; private set; }
    public int VantageTeamsOut => _vantageTeams.Count;

    bool VantageSquad(int s) => s is >= 0 and < GuardSim.MaxSquads && _vantageSquad[s];

    /// <summary>The vantage points of this battle's level and base (waits for the finder the first time).</summary>
    public IReadOnlyList<GuardVantagePoint> VantagePoints
    {
        get
        {
            if (_vantagePoints is not null) return _vantagePoints;
            _vantagePoints = _vantageTask?.Result ?? [];
            _baseRadius = VantageRules.BaseRadius;
            bool near = false;
            foreach (var v in _vantagePoints) near |= BaseFront(v, VantageRules.BaseRadius);
            if (!near) _baseRadius = VantageRules.BaseRadiusFar;   // nothing raised that near the base: out to the far radius
            return _vantagePoints;
        }
    }

    float _baseRadius;

    /// <summary>The base's radius the teams use now (<see cref="GuardVantageRules.BaseRadius"/>, or the far one on a level with no spot that near; tests).</summary>
    public float VantageBaseRadius { get { _ = VantagePoints; return _baseRadius; } }

    /// <summary>Whether spot <paramref name="v"/> stands within <paramref name="radius"/> of the base and in front of it (towards the Ork HQ).</summary>
    bool BaseFront(GuardVantagePoint v, float radius)
    {
        var home = _layout.LandingZone;
        var off = new Vector2(v.Position.X - home.X, v.Position.Y - home.Y);
        if (off.LengthSquared() > radius * radius) return false;
        var front = new Vector2(_layout.Hq.X - home.X, _layout.Hq.Y - home.Y);
        if (front.LengthSquared() < 1e-4f || off.LengthSquared() < 1e-4f) return true;
        return Vector2.Dot(Vector2.Normalize(off), Vector2.Normalize(front)) >= VantageRules.BaseFrontCos;
    }

    /// <summary>At the start: the finder starts off the tick (the level's nodes and the weapons mesh), and the first team waits a whole interval.</summary>
    void PlanVantage()
    {
        if (!_guardsmen || !VantageRules.Enabled || !_guards.Rules.Spread || _enemies.Walk is not { } walk || _guards.Sight is not { } sight) return;
        _vantageTask = GuardVantage.For(walk, sight.Weapons, _layout.LandingZone, VantageRules);
        _vantageTicks = NextVantageTicks();
    }

    int NextVantageTicks()
    {
        float roll = MusouSim.Unit(MusouSim.Mix((uint)_vantageRolls++, 0x7A47A6E5u));
        return Ticks(VantageRules.IntervalMin + (VantageRules.IntervalMax - VantageRules.IntervalMin) * roll);
    }

    /// <summary>Each tick after the squads' orders: once a second the teams are looked at; on the interval a new one may go.</summary>
    void StepVantage()
    {
        if (_vantageTask is null || Won) return;
        if (--_vantageOrderTicks <= 0)
        {
            _vantageOrderTicks = Ticks(TfwBattleRules.SquadOrderSeconds);
            StepVantageTeams();
        }
        if (--_vantageTicks > 0) return;
        _vantageTicks = NextVantageTicks();
        VantageDepart();
    }

    /// <summary>Whether spot <paramref name="v"/> may be held now: within the owner's 50 m of a secured point; with none secured, within the base's radius (VantageBaseRadius) and in front of it.</summary>
    bool VantageCounts(GuardVantagePoint v)
    {
        bool any = false;
        float r2 = VantageRules.PointRadius * VantageRules.PointRadius;
        for (int k = 0; k < Positions; k++)
        {
            if (_objectives[k].State != TfwPositionState.Secured) continue;
            any = true;
            if (FlatDistanceSquared(v.Position, _layout.Positions[k]) <= r2) return true;
        }
        return !any && BaseFront(v, VantageBaseRadius);
    }

    static float FlatDistanceSquared(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).LengthSquared();

    bool SpotTaken(int spot)
    {
        foreach (var t in _vantageTeams) if (t.Spot == spot && t.Stage != VantageStage.Returning) return true;
        return false;
    }

    /// <summary>A team leaves, when a spot counts and is free and a field squad at its post can spare one: the spot by a roll weighted by its score, the squad nearest it, the men by a roll.</summary>
    void VantageDepart()
    {
        var rules = VantageRules;
        if (_vantageTeams.Count >= rules.MostTeams) return;
        var points = VantagePoints;
        float total = 0f;
        Span<bool> open = stackalloc bool[points.Count];
        for (int v = 0; v < points.Count; v++)
        {
            open[v] = VantageCounts(points[v]) && !SpotTaken(v);
            if (open[v]) total += points[v].Score;
        }
        if (!(total > 0f)) return;
        float pick = MusouSim.Unit(MusouSim.Mix((uint)_now, 0x5A11E5u)) * total;
        int spot = -1;
        for (int v = 0; v < points.Count; v++)
        {
            if (!open[v]) continue;
            spot = v;
            pick -= points[v].Score;
            if (pick < 0f) break;
        }
        int parent = -1;
        float best = float.PositiveInfinity;
        for (int s = 0; s < GuardSim.MaxSquads; s++)
        {
            if (_guards.AliveIn(s) < rules.ParentKeeps + rules.MenMin || ArmySquad(s) || VantageSquad(s) || !_guards.Arrived(s) || _squadGoal[s] < 0) continue;
            float d = FlatDistanceSquared(_guards.Centre(s), points[spot].Position);
            if (d < best) { best = d; parent = s; }
        }
        if (parent < 0) return;
        int men = rules.MenMin + (int)(MusouSim.Unit(MusouSim.Mix((uint)_now, 0xC0A7u)) * (rules.MenMax - rules.MenMin + 1));
        men = Math.Min(Math.Min(men, rules.MenMax), _guards.AliveIn(parent) - rules.ParentKeeps);
        if (men < rules.MenMin) return;
        int squad = _guards.Detach(parent, men);
        if (squad < 0) return;
        _vantageSquad[squad] = true;
        _squadGoal[squad] = -1;
        var team = new VantageTeam { Squad = squad, Parent = parent, Spot = spot, Stage = VantageStage.Going, ParentAt = _guards.Centre(parent) };
        _vantageTeams.Add(team);
        SendToSpot(team, spot);
        VantageTeamsSent++;
    }

    void SendToSpot(VantageTeam team, int spot)
    {
        var v = VantagePoints[spot];
        team.Spot = spot;
        team.Stage = VantageStage.Going;
        if (!_guards.OrderPosts(team.Squad, v.Position, v.Posts, v.Yaw)) GoBack(team);
    }

    void GoBack(VantageTeam team)
    {
        team.Stage = VantageStage.Returning;
        if (_guards.AliveIn(team.Parent) > 0 && !ArmySquad(team.Parent) && !VantageSquad(team.Parent)) team.ParentAt = _guards.Centre(team.Parent);
        _guards.Order(team.Squad, Ground(team.ParentAt, team.ParentAt));
    }

    /// <summary>The nearest spot that counts and is free to a team's squad (-1: none).</summary>
    int NearestSpot(VantageTeam team)
    {
        var points = VantagePoints;
        int best = -1;
        float bestD = float.PositiveInfinity;
        for (int v = 0; v < points.Count; v++)
        {
            if (v == team.Spot || !VantageCounts(points[v]) || SpotTaken(v)) continue;
            float d = FlatDistanceSquared(_guards.Centre(team.Squad), points[v].Position);
            if (d < bestD) { bestD = d; best = v; }
        }
        return best;
    }

    void StepVantageTeams()
    {
        var rules = VantageRules;
        for (int n = _vantageTeams.Count - 1; n >= 0; n--)
        {
            var team = _vantageTeams[n];
            if (_guards.AliveIn(team.Squad) == 0) { Release(n); continue; }   // wiped: the spot is free
            bool parentOk = _guards.AliveIn(team.Parent) > 0 && !ArmySquad(team.Parent) && !VantageSquad(team.Parent);
            switch (team.Stage)
            {
                case VantageStage.Going:
                case VantageStage.Holding:
                    if (!VantageCounts(VantagePoints[team.Spot]))
                    {
                        int next = NearestSpot(team);
                        if (next >= 0) SendToSpot(team, next); else GoBack(team);
                        break;
                    }
                    if (team.Stage == VantageStage.Going && _guards.Arrived(team.Squad))
                    {
                        team.Stage = VantageStage.Holding;
                        float roll = MusouSim.Unit(MusouSim.Mix((uint)_now, (uint)team.Squad * 0x9E3779B1u + 0x401Du));
                        team.HoldUntil = _now + Ticks(rules.HoldMin + (rules.HoldMax - rules.HoldMin) * roll);
                    }
                    else if (team.Stage == VantageStage.Holding && _now >= team.HoldUntil) GoBack(team);
                    break;
                case VantageStage.Returning:
                    if (!parentOk) { Release(n); break; }   // its squad is gone: a field squad of its own
                    var at = _guards.Centre(team.Parent);
                    var here = _guards.Centre(team.Squad);
                    if (FlatDistanceSquared(here, at) <= rules.RejoinDistance * rules.RejoinDistance && MathF.Abs(here.Z - at.Z) <= 2f)
                    {
                        _guards.Merge(team.Squad, team.Parent);
                        Release(n);
                        break;
                    }
                    if (_guards.Arrived(team.Squad) || FlatDistanceSquared(at, team.ParentAt) > 64f) GoBack(team);
                    break;
            }
        }
    }

    /// <summary>Team <paramref name="n"/> is done: its squad (if any men are left) is a field squad again.</summary>
    void Release(int n)
    {
        int s = _vantageTeams[n].Squad;
        _vantageSquad[s] = false;
        _squadGoal[s] = -1;
        _vantageTeams.RemoveAt(n);
    }

    /// <summary>Test aid: a team leaves now (if any can), as on the interval.</summary>
    public void VantageDepartForTests() { _ = VantagePoints; VantageDepart(); }

    /// <summary>Test aid: the teams out now: their squad, its spot (an index into <see cref="VantagePoints"/>), and whether it holds the spot.</summary>
    public (int Squad, int Spot, bool Holding, bool Returning)[] VantageTeamsForTests() =>
        _vantageTeams.Select(t => (t.Squad, t.Spot, t.Stage == VantageStage.Holding, t.Stage == VantageStage.Returning)).ToArray();
}
