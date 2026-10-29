using System;
using System.Globalization;
using System.Linq;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// M44a test-bot shooting (test aid, **ours**, not the original AI):
/// <list type="bullet">
/// <item><c>--bot-accuracy &lt;0..1&gt;</c> (default <see cref="DefaultBotAccuracy"/>, but 1 when a probe or test-aid flag that needs the shots to land is present,
/// <see cref="ProbeFlags"/>): the fraction of shots meant to hit at medium range. The bot's view drifts off the line to its target by a slow wander (a smooth
/// move, never a 30 Hz shake), and every tick the trigger is held the shot itself is turned by a fresh scatter through the protocol's shot offsets
/// (<see cref="PlayerInput.ShotYawOffset"/>; <see cref="BotAimModel"/>); melee, grenades and everything that is not a ranged shot at an enemy keep the exact aim.
/// 1 = perfect aim (the old bots).</item>
/// <item><c>--bot-range &lt;metres&gt;</c>: the farthest distance at which the bot opens fire on an enemy it can see; the default is the range of the
/// weapon in hand (<see cref="WeaponSpec.MaxRange"/>, at least <see cref="BotFightRange"/>), so on a map the line of sight is the only limit
/// (M19 used a fixed 25 m). Beyond <see cref="BotAdvanceBeyond"/> m the bot also walks toward the enemy along its path while it fires.</item>
/// </list>
/// It also keeps shots, hits and engagement distances and prints an "aim:" line every 10 s (hit rate per distance band).
/// </summary>
public partial class NetworkView
{
    /// <summary>
    /// Fraction of shots meant to hit at medium range when <c>--bot-accuracy</c> is not given (ours: bots miss most shots, for footage; lowered
    /// from 0.25 to 0.03 on the owner's request of 2026-10-28, "the bots ... still absolutely obliterate me", together with the trigger discipline
    /// <see cref="BotFireDiscipline"/> and the data's weapon spread; the owner kept the result, about 7% hits and 72% less damage; spec "Test bots").
    /// </summary>
    public const float DefaultBotAccuracy = 0.03f;
    /// <summary>Metres beyond which a firing bot also advances toward its target (ours; the bot strafes closer than this).</summary>
    public const float BotAdvanceBeyond = 40f;

    /// <summary>The aim error model (created with the first bot input, seeded by the player id).</summary>
    BotAimModel? _botAim;
    float? _botAccuracyFlag;
    float? _botRangeFlag;
    bool _botAimFlagsRead;
    /// <summary>This tick's input aimed a ranged shot at an enemy (set by the fight code; consumed by <see cref="BotApplyAim"/>).</summary>
    bool _botAimNow;
    /// <summary>Horizontal distance to the enemy being shot at, this tick (bands for the statistics).</summary>
    float _botAimDistance;
    /// <summary>The enemy this tick's ranged shot is aimed at (set with <see cref="_botAimNow"/>), for the trigger discipline's reaction delay.</summary>
    int? _botAimTarget;
    /// <summary>The trigger discipline (reaction delay, bursts and pauses; <see cref="BotFireDiscipline"/>); off with a perfect aim or <c>--bot-no-discipline</c>.</summary>
    BotFireDiscipline? _botDiscipline;
    int _botDisciplineHeld;

    /// <summary>Flags of probes and test aids that measure or photograph shots and need them to land: with one of these present a bot's default accuracy is 1 (an explicit <c>--bot-accuracy</c> still wins).</summary>
    static readonly string[] ProbeFlags = ["aim-test", "bot-stand", "hip-probe", "reload-capture", "reload-test", "look-closeup", "look-churn", "look-log", "camera-probe", "camera-selftest",
        "yaw-probe", "pan-rate", "bot-gait", "anim-probe", "motion-probe", "damage-demo", "picker-demo", "action-sweep", "gait-sweep"];

    /// <summary>The accuracy this bot uses: <c>--bot-accuracy</c>, else 1 with a probe flag (<see cref="ProbeFlags"/>), else <see cref="DefaultBotAccuracy"/>.</summary>
    float BotAccuracy => _botAccuracyFlag ?? (ProbeFlags.Any(CmdFlag) ? 1f : DefaultBotAccuracy);

    static float? FloatArg(string name)
    {
        var a = CmdArgs(name, 1);
        return a.Length == 1 && float.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v) ? v : null;
    }

    void ReadBotAimFlags()
    {
        if (_botAimFlagsRead) return;
        _botAimFlagsRead = true;
        _botAccuracyFlag = FloatArg("bot-accuracy");
        _botRangeFlag = FloatArg("bot-range");
        if (_botAccuracyFlag is { } acc && (acc < 0f || acc > 1f)) { GD.PrintErr($"--bot-accuracy {acc} is outside 0 to 1; clamped"); _botAccuracyFlag = Math.Clamp(acc, 0f, 1f); }
        if (_botRangeFlag is { } r && r <= 0f) { GD.PrintErr($"--bot-range {r} must be above 0; the weapon's range is used"); _botRangeFlag = null; }
        if (_bot) GD.Print($"bot aim: accuracy {BotAccuracy}{(_botAccuracyFlag is null && BotAccuracy != DefaultBotAccuracy ? " (a probe flag is present)" : "")} at {BotAimModel.ReferenceDistance:F0} m (sigma {BotAimModel.SigmaFor(BotAccuracy) * 57.2958f:F2} deg per axis), engagement range {(_botRangeFlag is { } rr ? rr.ToString("F0") + " m" : "the weapon's range")}, spawn-room wait {(BotNoSpawnWait ? "off" : "on")}");
    }

    /// <summary>The farthest distance (metres) at which this bot opens fire: <c>--bot-range</c>, else the weapon in hand's range, never below <see cref="BotFightRange"/>.</summary>
    float BotEngageRange(PlayerState me)
    {
        ReadBotAimFlags();
        if (_botRangeFlag is { } r) return r;
        var weapon = _config is null ? null : Loadouts.InHand(_config, me);
        float reach = weapon is null ? 0f : weapon.Charge is { } c ? Math.Max(weapon.MaxRange, c.Stages.Select(s => s.Shot?.MaxRange ?? 0f).DefaultIfEmpty(0f).Max()) : weapon.MaxRange;
        return Math.Max(BotFightRange, reach);
    }

    /// <summary>
    /// The scripted input with this tick's aim error when it aims a ranged shot at an enemy (<see cref="_botAimNow"/>): the view turned by the slow wander (and
    /// the move vector turned back so the body still goes the same way in the world), the shot turned by the per-shot scatter through the shot offsets.
    /// Other inputs unchanged.
    /// </summary>
    PlayerInput BotApplyAim(PlayerInput input)
    {
        ReadBotAimFlags();
        BotTrace(input);
        bool aimed = _botAimNow;
        int? aimTarget = _botAimTarget;
        _botAimNow = false; _botAimTarget = null;
        if (_config is null || _client is null) return input;
        _botAim ??= new BotAimModel(BotAccuracy, Math.Max(_client.PlayerId, 0) * 7919 + 13);
        // The trigger discipline (owner's request 2026-10-28): a reaction delay on a new target, then bursts and pauses; a charge being held goes on.
        if (!_botAim.IsPerfect && !CmdFlag("bot-no-discipline"))
        {
            _botDiscipline ??= new BotFireDiscipline(Math.Max(_client.PlayerId, 0) * 104729 + 7);
            bool allow = _botDiscipline.Step(_config.TickSeconds, aimed ? aimTarget ?? -1 : null);
            if (aimed && !allow && (input.Buttons & InputButtons.Fire) != 0 && !_client.Predicted.Charging)
            { input = input with { Buttons = input.Buttons & ~InputButtons.Fire }; _botDisciplineHeld++; }
        }
        if (!aimed) { if (!_botAim.IsPerfect) _botAim.StepParts(_config.TickSeconds, false); return input; }
        bool firing = (input.Buttons & InputButtons.Fire) != 0;
        var (dy, dp, sy, sp) = _botAim.StepParts(_config.TickSeconds, firing);
        if (dy == 0f && dp == 0f && sy == 0f && sp == 0f) return input;
        // Turning the view by dy turns the move vector with it; rotate the stick the other way to keep the body's direction.
        float c = MathF.Cos(dy), s = MathF.Sin(dy);
        float mx = input.MoveX * c + input.MoveY * s, my = -input.MoveX * s + input.MoveY * c;
        float pitch = Math.Clamp(input.Pitch + dp, -MaxPitch, MaxPitch);
        // The shot goes view + offsets (Combat.ShotAim keeps it within the server's cone of the view; the prediction uses the same input).
        return input with { Yaw = input.Yaw + dy, Pitch = pitch, MoveX = Math.Clamp(mx, -1f, 1f), MoveY = Math.Clamp(my, -1f, 1f), ShotYawOffset = sy, ShotPitchOffset = sp };
    }

    // ---- trace ----

    System.IO.StreamWriter? _botTrace;
    bool _botTraceTried;
    double _botTraceFlushAt;

    /// <summary>
    /// <c>--bot-trace &lt;file&gt;</c> (test aid): one CSV line per input tick (time, position, speed, yaw, alive, stick, buttons, the bot's decision word) for finding where bots
    /// stand against walls or jitter. Refuses a file inside the game install.
    /// </summary>
    void BotTrace(PlayerInput input)
    {
        if (!_botTraceTried)
        {
            _botTraceTried = true;
            if (CmdArgs("bot-trace", 1) is [{ } file])
            {
                try
                {
                    string full = System.IO.Path.GetFullPath(file);
                    if (full.StartsWith(System.IO.Path.GetFullPath(_gameDir), StringComparison.OrdinalIgnoreCase)) GD.PrintErr("--bot-trace: refusing to write inside the game install");
                    else { _botTrace = new System.IO.StreamWriter(full); _botTrace.WriteLine("t,x,y,z,speed,yaw,alive,movex,movey,buttons,state"); }
                }
                catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or ArgumentException) { GD.PrintErr($"--bot-trace: {e.Message}"); }
            }
        }
        if (_botTrace is null || _client is null) return;
        var me = _client.Predicted;
        string state = _botNote.Length == 0 ? "-" : _botNote.Split(' ')[0];
        _botTrace.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{input.Tick * _config!.TickSeconds:F3},{me.Position.X:F2},{me.Position.Y:F2},{me.Position.Z:F2},{new Vector2(me.Velocity.X, me.Velocity.Y).Length():F2},{input.Yaw:F3},{(me.Alive ? 1 : 0)},{input.MoveX:F2},{input.MoveY:F2},{(int)input.Buttons},{state}"));
        if (_connectedFor - _botTraceFlushAt > 2.0) { _botTraceFlushAt = _connectedFor; _botTrace.Flush(); }
    }

    // ---- statistics ----

    static readonly float[] BandEdges = [10f, 25f, 50f, 100f, 1e9f];
    static readonly string[] BandNames = ["<10 m", "10-25 m", "25-50 m", "50-100 m", ">100 m"];
    readonly int[] _botBandShots = new int[5], _botBandHits = new int[5], _botBandTicks = new int[5];
    float _botMaxShotDistance;
    int _botHipShotsSeen;
    double _botAimLogAt;

    static int Band(float d) { for (int i = 0; i < BandEdges.Length; i++) if (d < BandEdges[i]) return i; return BandEdges.Length - 1; }

    /// <summary>Per tick of shooting (called with the target's distance): the engagement distance statistics.</summary>
    void BotNoteEngagement(float distance)
    {
        _botAimDistance = distance;
        _botBandTicks[Band(distance)]++;
    }

    /// <summary>A hit report of this bot's own shots: ranged hits (not melee or blast) count toward the distance band of the target it was aiming at.</summary>
    void BotNoteHits(HitReport hits)
    {
        if (!_bot || hits.LastMelee || hits.LastSplash || hits.Hits <= 0) return;
        _botBandHits[Band(_botAimDistance)] += hits.Hits;
        _botRangedDamage += hits.ShieldDamage + hits.HealthDamage;
    }

    /// <summary>Ranged damage (shield and health) this bot's hits dealt, from the server's hit reports (for the "damage per second while shooting" figure).</summary>
    float _botRangedDamage;

    /// <summary>The bot's "aim:" line every 10 s: shots fired (own fired events), ranged hits and the hit rate per distance band, plus the ticks spent shooting per band.</summary>
    void LogBotAim()
    {
        if (!_bot) return;
        int fired = _hipOwnShots;
        for (int shots = fired - _botHipShotsSeen; shots > 0; shots--) _botBandShots[Band(_botAimDistance)]++;
        if (fired > _botHipShotsSeen) _botMaxShotDistance = MathF.Max(_botMaxShotDistance, _botAimDistance);
        _botHipShotsSeen = fired;
        if (_connectedFor - _botAimLogAt < 10.0) return;
        _botAimLogAt = _connectedFor;
        int totalShots = _botBandShots.Sum(), totalHits = _botBandHits.Sum();
        string bands = string.Join("; ", Enumerable.Range(0, 5).Where(i => _botBandShots[i] > 0 || _botBandTicks[i] > 0)
            .Select(i => $"{BandNames[i]}: {_botBandShots[i]} shots, {_botBandHits[i]} hits ({(_botBandShots[i] > 0 ? 100f * _botBandHits[i] / _botBandShots[i] : 0f):F0}%), {_botBandTicks[i]} ticks shooting"));
        float engaged = _botBandTicks.Sum() * (_config?.TickSeconds ?? 1f / 30f);
        GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId} aim: shots {totalShots}, hits {totalHits} ({(totalShots > 0 ? 100f * totalHits / totalShots : 0f):F1}%), " +
                 $"ranged damage {_botRangedDamage:F0} over {engaged:F0} s aimed at enemies ({(engaged > 0f ? _botRangedDamage / engaged : 0f):F1} per s), trigger held back {_botDisciplineHeld} ticks, farthest shot {_botMaxShotDistance:F0} m; {bands}");
    }
}
