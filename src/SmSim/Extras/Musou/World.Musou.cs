using System.Numerics;
using SmSim.Extras.Musou;

namespace SmSim;

/// <summary>
/// Added content "Musou" in the World (design sections A and B): the World's hooks, each returning at once unless <see cref="_musou"/> is set, which only
/// <see cref="StartMusou"/> does and only under <see cref="ContentExtras.Musou"/> (the server calls it from its <c>ExtrasCreated</c> hook). Without it every
/// existing mode runs exactly as before (pinned in <c>MusouIsolationTests</c>). MU0: the side is forced (Space Marines only, no balance) and the match flow
/// is one-sided and endless (warm-up, the countdown, then play until every player has left); the enemies, the director and the combat come in MU1 to MU3.
/// </summary>
public sealed partial class World
{
    MusouSim? _musou;

    /// <summary>The Musou enemies of this match; null unless <see cref="StartMusou"/> ran (every match that is not Musou).</summary>
    public MusouSim? Musou => _musou;

    /// <summary>
    /// Makes this match a Musou match with enemies on <paramref name="nav"/> (the server's walking graph; null on the flat ground). Only when the config
    /// runs <see cref="ContentExtras.Musou"/> and the world has match rules; false (and nothing changed) otherwise. A second call keeps the first's.
    /// </summary>
    public bool StartMusou(NavGraph? nav)
    {
        if (!Config.Extras.HasFlag(ContentExtras.Musou) || Rules is null) return false;
        _musou ??= new MusouSim(nav);
        return true;
    }

    partial void ExtrasStep()
    {
        if (_musou is null) return;
        _musou.BeginTick(Tick);
        // MU1: the director's releases, the flow field and the enemies' moves and attacks. MU3: players' shots and cones against them.
    }

    partial void ExtrasMelee(int attacker, PlayerState state, MeleeStep step)
    {
        if (_musou is null) return;
        // MU3: the cleave (every enemy within MeleeRange + 0.5 m and cone x1.5 takes step.Damage, up to 8).
    }

    partial void ExtrasBlast(int owner, Vector3 point, SplashSpec splash, bool melee)
    {
        if (_musou is null) return;
        // MU3: the blast on the enemies within its radius.
    }

    partial void ExtrasJoinSide(ref Team want, ref bool forced)
    {
        if (_musou is null) return;
        // Players are Space Marines only (lead's default for Musou): every join is a loyalist, without the balance or the per-side limit.
        want = Team.SpaceMarines;
        forced = true;
    }

    /// <summary>
    /// The one-sided match flow (MU0): the core's needs both sides, so Musou runs its own. Warm-up until a Space Marine is in, then the countdown (as the
    /// core's, without attacks), then play, endless (the server gives Musou no score or time limit; MU1's director ends it). Every player gone sends it
    /// back to warm-up; an ended match restarts after the rules' delay, as the core's.
    /// </summary>
    partial void ExtrasStepMatch(ref bool handled)
    {
        if (_musou is null) return;
        handled = true;
        bool marines = CountSide(Team.SpaceMarines) > 0;
        switch (_state)
        {
            case MatchState.Warmup:
                if (marines) BeginRound(respawnWithoutCountdown: false);
                break;
            case MatchState.Countdown:
                if (!marines) { _state = MatchState.Warmup; TicksLeft = 0; }
                else if (--TicksLeft == 0)
                {
                    _projectiles.Clear(); _areas.Clear(); _perkBlasts.Clear();
                    CountProjectilesOut();
                    StartRound(respawn: false);
                }
                break;
            case MatchState.Playing:
                if (!marines) { _state = MatchState.Warmup; TicksLeft = 0; _musou.Clear(); }
                break;
            case MatchState.Ended:
                if (Tick > _endedTick && TicksLeft > 0) TicksLeft--;
                if (TicksLeft == 0)
                {
                    _state = MatchState.Warmup;
                    Winner = Team.None;
                    Array.Clear(_teamScores);
                    ResetPoints();
                    _musou.Clear();
                    if (marines) BeginRound(respawnWithoutCountdown: true);
                }
                break;
        }
    }
}
