using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// Fire support's numbers (Musou pass 2, milestone M8; the owner's items 12 and 13). Every value is an estimate of ours (spec "Added content: Musou: fire support"), kept here so the balance pass can
/// tune them; only <see cref="RunGapSeconds"/> is the owner's ("not more often than every 45 seconds").
/// </summary>
public static class TfwFireRules
{
    /// <summary>Thunderhawk runs: paused by the owner (2026-11-01). While false no run starts in a match and the client loads neither the aircraft nor the campaign's bank. Set it true to pick the runs up: what is left to do is in docs/milestones.md, M8 and M9.</summary>
    public static readonly bool RunsEnabled = false;
    /// <summary>Seconds a player waits between two orbital strikes (ours; the owner asked for a cooldown, 60 s the lead's default).</summary>
    public const float StrikeCooldownSeconds = 60f;
    /// <summary>Shells in a barrage, both ends included.</summary>
    public const int ShellsMin = 16, ShellsMax = 24;
    /// <summary>The length of the line a barrage walks (m), drawn in this range.</summary>
    public const float LineMin = 60f, LineMax = 100f;
    /// <summary>Seconds between two impacts.</summary>
    public const float ShellStep = 0.35f;
    /// <summary>A shell's scatter on each axis (m).</summary>
    public const float Scatter = 6f;
    /// <summary>The share of runs aimed at the Ork mass (a living enemy at random); the rest at a random place of the battlefield.</summary>
    public const float AimAtMass = 0.7f;
    /// <summary>Seconds from a strike's call to its first impact, and between each shell's record and its landing: the whistle's time (the owner: "the barrage should start instantly"; about 1 s keeps a whistle before each impact).</summary>
    public const float ShellDelay = 1f;
    /// <summary>Within this flat distance of the impact fodder dies (m).</summary>
    public const float FodderRadius = 6f;
    /// <summary>Elites, bosses, players and AI Marines are hurt, falling off linearly to nothing at this flat distance (m).</summary>
    public const float OuterRadius = 12f;
    /// <summary>Guardsmen within this flat distance die (m).</summary>
    public const float GuardRadius = 4f;
    /// <summary>What a shell deals at the point of impact: an elite or boss, a player or AI Marine.</summary>
    public const float EliteDamage = 150f, PlayerDamage = 60f;
    /// <summary>A shell reaches this far up and down from its impact (m): one on a roof spares those under it.</summary>
    public const float VerticalReach = 6f;
    /// <summary>How far the shock throws what it does not kill (m, at the rim of <see cref="FodderRadius"/> and within).</summary>
    public const float Knockback = 2.5f;

    /// <summary>A run never starts sooner than this after the last (s; the owner's item 13).</summary>
    public const float RunGapSeconds = 45f;
    /// <summary>What is added to <see cref="RunGapSeconds"/>: a hash's fraction of this (s).</summary>
    public const float RunGapSpread = 75f;
    /// <summary>The aircraft's speed (m/s): a client flies it from the run's one record.</summary>
    public const float RunSpeed = 60f;
    /// <summary>A run's path reaches this far either side of its aim (m).</summary>
    public const float RunReach = 300f;
    /// <summary>The path flies at least this high over the ground under its aim, and this far above the collision under the path (m).</summary>
    public const float RunHeight = 35f, RunClearance = 8f;
    /// <summary>The path's ground is sampled every this many metres.</summary>
    public const float RunSample = 20f;
    /// <summary>A gun reaches this far to its side of the path (m).</summary>
    public const float Corridor = 30f;
    /// <summary>Along the path a gun takes a target from <see cref="GunsBehind"/> behind the aircraft to <see cref="GunsAhead"/> ahead (m).</summary>
    public const float GunsBehind = 10f, GunsAhead = 45f;
    /// <summary>The guns' place on the aircraft: to each side of the path and below it (m, ours; the wing guns' bone is not read).</summary>
    public const float GunSide = 7f, GunBelow = 2f;
    /// <summary>Seconds between volleys, and the rounds of one gun in a volley ("twice per 0.1 s").</summary>
    public const float VolleySeconds = 0.1f;
    public const int RoundsPerVolley = 2;
    /// <summary>What a round deals, and the share of rounds that hit (the rest strike the ground by the target).</summary>
    public const float RoundDamage = 40f, RoundHitChance = 0.75f;
    /// <summary>Candidates a gun tries before it holds its fire this volley: each needs a clear line.</summary>
    public const int RoundTries = 5;

    /// <summary>Records of tag 11 kept for a server that takes none (a test, a run with no client).</summary>
    public const int MaxEvents = 256;
}

/// <summary>A shell on its way: the landing tick, its number in the barrage, the impact, whether its record has gone out.</summary>
public readonly record struct TfwShell(uint Tick, ushort Subject, Vector3 At, bool Sent);

/// <summary>A Thunderhawk run under way: its number, the first tick, the path.</summary>
public readonly record struct TfwStrafe(ushort Subject, uint Start, Vector3 From, Vector3 To, Vector3 Direction, Vector3 Side, float Length);

/// <summary>
/// Fire support of a battle (Musou pass 2, M8): the walking barrages a player calls (orbital support; the owner, 2026-11-01: none at random) and the Thunderhawk's strafing runs (paused: <see cref="TfwFireRules.RunsEnabled"/>). The World steps it from <c>TfwStep</c> (World.TfwFire.cs, the damage and the aiming); this
/// class holds the schedule, the shells in flight, the run under way and tag 11's records. Every roll comes from a hash of the tick (<see cref="Salt"/> is the balance harness's seed), so a replay
/// falls the same way. A client flies the aircraft from the run's one record (<see cref="TfwFireRules.RunSpeed"/>).
/// </summary>
public sealed class TfwFire
{
    readonly List<TfwShell> _shells = new();
    readonly List<TfwFireEvent> _events = new();
    TfwBattle? _battle;
    readonly Dictionary<int, uint> _strikeReady = new();
    uint _nextRun;
    ushort _runs, _rounds;

    /// <summary>The seed mixed into every roll (the balance harness; 0 otherwise).</summary>
    public ulong Salt { get; set; }

    /// <summary>Test aid (<c>SM_TFW_FIRE_TEST</c>): the server takes <see cref="TfwCommand.FireTest"/>. Off, the command does nothing.</summary>
    public bool TestCommands { get; set; }

    /// <summary>Test aid: the schedule's own barrages and runs stay away (a test or capture that triggers them itself).</summary>
    public bool NoSchedule { get; set; }

    /// <summary>Barrages begun and shells landed, this war (the log and tests).</summary>
    public int Barrages { get; private set; }
    public int ShellsLanded { get; private set; }
    /// <summary>Runs begun, rounds fired, rounds that hit (this war).</summary>
    public int Runs { get; private set; }
    public int RoundsFired { get; private set; }
    public int RoundsHit { get; private set; }
    /// <summary>Enemies killed by shells and by rounds (this war).</summary>
    public int ShellKills { get; internal set; }
    public int RoundKills { get; internal set; }

    /// <summary>The run under way.</summary>
    public TfwStrafe? Run { get; private set; }
    /// <summary>The first tick of the last run begun (0: none yet).</summary>
    public uint LastRunStart { get; private set; }
    public uint NextRunTick => _nextRun;
    public IReadOnlyList<TfwShell> Shells => _shells;

    /// <summary>The records added since the last <see cref="TakeEvents"/> (tag 11).</summary>
    public IReadOnlyList<TfwFireEvent> Events => _events;

    /// <summary>Moves the records into <paramref name="into"/>.</summary>
    public void TakeEvents(List<TfwFireEvent> into)
    {
        into.AddRange(_events);
        _events.Clear();
    }

    /// <summary>Draws a fraction in [0, 1) for this tick and purpose (FNV-1a over the values, then a finaliser).</summary>
    public float Roll(uint tick, ulong purpose, ulong index = 0)
    {
        ulong h = 14695981039346656037UL;
        foreach (ulong v in (ReadOnlySpan<ulong>)[Salt, tick, purpose, index])
            for (int b = 0; b < 8; b++) { h ^= (v >> (b * 8)) & 0xff; h *= 1099511628211UL; }
        h ^= h >> 33; h *= 0xff51afd7ed558ccdUL; h ^= h >> 33;
        return (float)((h >> 40) / (double)(1UL << 24));
    }

    /// <summary>Whether a new battle began: the schedule starts over from <paramref name="tick"/>.</summary>
    internal bool Follow(TfwBattle battle, uint tick, int rate)
    {
        if (ReferenceEquals(_battle, battle)) return false;
        _battle = battle;
        _shells.Clear();
        Run = null;
        ScheduleRun(tick, rate);
        return true;
    }

    /// <summary>The battle is over: nothing stays in the air.</summary>
    internal void Stop()
    {
        _battle = null;
        _shells.Clear();
        Run = null;
    }

    /// <summary>A new run: every cooldown is over.</summary>
    internal void NewRun() => _strikeReady.Clear();

    /// <summary>Ticks until <paramref name="player"/> may call a strike.</summary>
    public int StrikeTicksLeft(int player, uint tick) => _strikeReady.TryGetValue(player, out uint ready) && ready > tick ? (int)(ready - tick) : 0;

    /// <summary>Test aid: as <see cref="StrikeCalled"/>.</summary>
    public void StrikeCalledForTests(int player, uint tick, int rate) => StrikeCalled(player, tick, rate);

    /// <summary><paramref name="player"/> called a strike on <paramref name="tick"/>.</summary>
    internal void StrikeCalled(int player, uint tick, int rate) => _strikeReady[player] = tick + (uint)MathF.Ceiling(TfwFireRules.StrikeCooldownSeconds * rate);

    /// <summary>The next run (the design's <c>max(45 s, 45 + hash·75)</c>): never under <see cref="TfwFireRules.RunGapSeconds"/> from <paramref name="tick"/>.</summary>
    internal void ScheduleRun(uint tick, int rate) => _nextRun = tick + (uint)MathF.Ceiling(RunGap(tick) * rate);

    /// <summary>The seconds to the next run after one that starts on <paramref name="tick"/>.</summary>
    public float RunGap(uint tick) => MathF.Max(TfwFireRules.RunGapSeconds, TfwFireRules.RunGapSeconds + Roll(tick, 2) * TfwFireRules.RunGapSpread);

    internal void AddShell(in TfwShell shell) => _shells.Add(shell);

    internal void Started(uint tick)
    {
        Barrages++;
    }

    /// <summary>Records every shell whose telegraph is due.</summary>
    internal void SendDue(uint tick, int rate)
    {
        uint lead = (uint)MathF.Round(TfwFireRules.ShellDelay * rate);
        for (int k = 0; k < _shells.Count; k++)
        {
            var s = _shells[k];
            if (s.Sent || s.Tick > tick + lead) continue;
            _shells[k] = s with { Sent = true };
            Add(new TfwFireEvent(TfwFireKind.Shell, s.Tick, s.Subject, s.At));
        }
    }

    /// <summary>The shells that land on <paramref name="tick"/> go into <paramref name="into"/> and leave the air.</summary>
    internal void Landing(uint tick, List<TfwShell> into)
    {
        for (int k = _shells.Count - 1; k >= 0; k--)
        {
            if (_shells[k].Tick > tick) continue;
            into.Add(_shells[k]);
            _shells.RemoveAt(k);
            ShellsLanded++;
        }
    }

    /// <summary>A run begins now on the line from <paramref name="from"/> to <paramref name="to"/>.</summary>
    internal void BeginRun(uint tick, Vector3 from, Vector3 to, int rate)
    {
        var line = to - from;
        float length = line.Length();
        var direction = line / MathF.Max(length, 1e-3f);
        var side = Vector3.Normalize(new Vector3(direction.Y, -direction.X, 0f));   // the right of the flight
        Run = new TfwStrafe(++_runs, tick, from, to, direction, side, length);
        LastRunStart = tick;
        Runs++;
        Add(new TfwFireEvent(TfwFireKind.Run, tick, _runs, from, to));
        ScheduleRun(tick, rate);
    }

    internal void EndRun() => Run = null;

    /// <summary>Test aid: a run on the given line, as the World would begin one.</summary>
    public void BeginRunForTests(uint tick, Vector3 from, Vector3 to, int rate) => BeginRun(tick, from, to, rate);

    /// <summary>One round from <paramref name="muzzle"/> to <paramref name="impact"/>.</summary>
    internal void Round(uint tick, Vector3 muzzle, Vector3 impact, bool hit)
    {
        RoundsFired++;
        if (hit) RoundsHit++;
        Add(new TfwFireEvent(TfwFireKind.Round, tick, ++_rounds, muzzle, impact));
    }

    void Add(in TfwFireEvent e)
    {
        if (_events.Count >= TfwFireRules.MaxEvents) return;
        _events.Add(e);
    }
}

public sealed partial class TfwRun
{
    /// <summary>The barrages and the Thunderhawk (M8).</summary>
    public TfwFire Fire { get; } = new();
}
