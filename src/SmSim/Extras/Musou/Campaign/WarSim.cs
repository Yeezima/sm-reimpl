using System.Security.Cryptography;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>What the other lanes add to the final assault (design section C).</summary>
public readonly record struct WarFinalBonus(float BossHealthFactor, int ExtraSquads);

/// <summary>
/// The war between the battles (design section C): three fronts that move by themselves, stepped once a second (<see cref="WarRules.StepSeconds"/>). Pure and deterministic: no
/// random numbers, no real clock, nothing of the World; the run (TF4) tells it the phase and a battle's result and reads <see cref="View"/>. Time runs at
/// <see cref="WarRules.TimeScale"/>; in a battle (only in the Battle phase) its own lane is frozen. A front holds its progress while it gathers, its strengths keep moving; a node
/// changing hands starts the next one from progress 0.
/// </summary>
public sealed class WarSim
{
    sealed class Lane
    {
        public sbyte Front;
        public double Progress, Imperial = WarRules.StartImperial, Ork = WarRules.StartOrk, GatherLeft, WoundLeft;
    }

    readonly Lane[] _lanes =
    [
        new() { Front = WarRules.StartFrontA },
        new() { Front = WarRules.StartFrontB },
        new() { Front = WarRules.StartFrontC },
    ];

    /// <summary>Where the run stands; <see cref="TfwRunPhase.Lost"/> also comes from the war itself (an HQ fell).</summary>
    public TfwRunPhase Phase { get; private set; } = TfwRunPhase.Hub;

    /// <summary>The lane of the battle, the travel or the pickup.</summary>
    public TfwLane BattleLane { get; private set; }

    /// <summary>The war clock (war seconds). It drives the Orks' growth and stops with the war.</summary>
    public double Clock { get; private set; }

    /// <summary>The lane whose HQ fell, once the war is lost that way.</summary>
    public TfwLane? FallenLane { get; private set; }

    /// <summary>The first lane at its stronghold, if any: the final assault is open while one holds it (a stronghold can be lost again).</summary>
    public TfwLane? FinalLane
    {
        get
        {
            for (int i = 0; i < _lanes.Length; i++)
                if (_lanes[i].Front == WarRules.FrontStronghold) return (TfwLane)i;
            return null;
        }
    }

    /// <summary>The current time scale.</summary>
    public double TimeScale => WarRules.TimeScale(Phase);

    /// <summary>The run moves on; the lane matters to Travel, Battle and Pickup. An ended run stays ended.</summary>
    public void Enter(TfwRunPhase phase, TfwLane lane = TfwLane.A)
    {
        if (Ended) return;
        Phase = phase;
        BattleLane = lane;
    }

    bool Ended => Phase is TfwRunPhase.Won or TfwRunPhase.Lost;

    /// <summary>One second of real time.</summary>
    public void Step()
    {
        double dt = WarRules.StepSeconds * TimeScale;
        if (dt <= 0.0) return;

        for (int i = 0; i < _lanes.Length; i++)
        {
            Lane lane = _lanes[i];
            lane.WoundLeft = Math.Max(0.0, lane.WoundLeft - dt);   // war-minutes: it runs on while the lane's own battle freezes the lane
            if (Phase == TfwRunPhase.Battle && (int)BattleLane == i) continue;
            if (StepLane(lane, dt))
            {
                FallenLane = (TfwLane)i;
                Phase = TfwRunPhase.Lost;
                break;
            }
        }
        Clock += dt;
    }

    /// <summary>One lane's step; true when its HQ fell.</summary>
    bool StepLane(Lane lane, double dt)
    {
        double imperial = lane.Imperial, ork = lane.Ork;
        lane.Imperial = Math.Clamp(imperial + (WarRules.ImperialReinforcement - WarRules.Attrition * ork) * dt, 0.0, WarRules.ImperialCap);
        lane.Ork = Math.Clamp(ork + (WarRules.OrkReinforcementAt(Clock) - WarRules.Attrition * imperial) * dt, 0.0, WarRules.OrkCap);

        if (lane.GatherLeft > 0.0)
        {
            lane.GatherLeft = Math.Max(0.0, lane.GatherLeft - dt);
            return false;
        }

        double multiplier = WarRules.Multiplier(lane.WoundLeft <= 0.0, lane.Front);
        lane.Progress += WarRules.Advantage(multiplier, imperial, ork) * dt / WarRules.ProgressSeconds;

        if (lane.Progress >= 1.0)
        {
            if (lane.Front == WarRules.FrontStronghold) lane.Progress = 1.0;
            else Move(lane, +1, WarRules.GatherSeconds);
        }
        else if (lane.Progress <= -1.0)
        {
            if (lane.Front == WarRules.FrontBesieged) { lane.Progress = -1.0; return true; }
            Move(lane, -1, WarRules.GatherSeconds);
        }
        return false;
    }

    static void Move(Lane lane, int by, double gather)
    {
        lane.Front = (sbyte)Math.Clamp(lane.Front + by, WarRules.FrontBesieged, WarRules.FrontStronghold);
        lane.Progress = 0.0;
        lane.GatherLeft = gather;
    }

    /// <summary>A battle won by a player: the node is captured, the Orks are beaten back, the Guard reinforced. Nothing once the run ended.</summary>
    public void PlayerWon(TfwLane which)
    {
        if (Ended) return;
        Lane lane = _lanes[(int)which];
        Move(lane, +1, WarRules.WinGatherSeconds);
        lane.Ork *= WarRules.WinOrkFactor;
        lane.Imperial = Math.Min(WarRules.ImperialCap, lane.Imperial + WarRules.WinImperial);
    }

    /// <summary>A battle a player left or lost: the secured positions set the progress, the kills and losses go to the strengths (negative counts as 0). Nothing once the run ended.</summary>
    public void PlayerLeft(TfwLane which, int secured, int orksKilled, int guardsLost)
    {
        if (Ended) return;
        Lane lane = _lanes[(int)which];
        lane.Progress = WarRules.LeftProgress(secured);
        lane.Ork = Math.Max(0.0, lane.Ork - WarRules.LeftOrkPerKill * Math.Max(0, orksKilled));
        lane.Imperial = Math.Max(0.0, lane.Imperial - WarRules.LeftImperialPerGuard * Math.Max(0, guardsLost));
    }

    /// <summary>A lord killed in a battle: wounded for the full time from now.</summary>
    public void WoundLord(TfwLane which) => _lanes[(int)which].WoundLeft = WarRules.LordWoundSeconds;

    /// <summary>The war seconds a lane's lord stays wounded.</summary>
    public double LordWoundLeft(TfwLane which) => _lanes[(int)which].WoundLeft;

    /// <summary>What the lanes other than <paramref name="assault"/> add to the final assault.</summary>
    public WarFinalBonus FinalBonus(TfwLane assault)
    {
        float health = 1f;
        int squads = 0;
        for (int i = 0; i < _lanes.Length; i++)
        {
            if ((TfwLane)i == assault || _lanes[i].Front < WarRules.FinalBonusFront) continue;
            health *= WarRules.FinalBossHealthFactor;
            squads += WarRules.FinalExtraSquads;
        }
        return new WarFinalBonus(health, squads);
    }

    /// <summary>A front as tag 7 carries it.</summary>
    public TfwLaneState LaneState(TfwLane which)
    {
        Lane lane = _lanes[(int)which];
        return new TfwLaneState(lane.Front, (float)lane.Progress, (float)lane.Imperial, (float)lane.Ork, WarRules.OwnedMask(lane.Front), (float)lane.GatherLeft,
            lane.WoundLeft > 0.0 ? WarRules.LordWounded : WarRules.LordPresent);
    }

    /// <summary>The war as tag 7 carries it; the reserves and the command echo are the run's and the sender's.</summary>
    public TfwWarView View(byte transport = 0, byte supplies = 0, ushort commandEcho = 0) =>
        new(Phase, BattleLane, (float)Clock, (float)TimeScale, transport, supplies, commandEcho, LaneState(TfwLane.A), LaneState(TfwLane.B), LaneState(TfwLane.C));

    /// <summary>A hash of the whole state, exact to the bit (for the determinism checks).</summary>
    public ulong StateHash()
    {
        using var bytes = new MemoryStream();
        using (var w = new BinaryWriter(bytes))
        {
            w.Write((byte)Phase);
            w.Write((byte)BattleLane);
            w.Write(Clock);
            w.Write(FallenLane.HasValue ? (int)FallenLane.Value : -1);
            foreach (Lane lane in _lanes)
            {
                w.Write(lane.Front);
                w.Write(lane.Progress);
                w.Write(lane.Imperial);
                w.Write(lane.Ork);
                w.Write(lane.GatherLeft);
                w.Write(lane.WoundLeft);
            }
        }
        return BitConverter.ToUInt64(SHA256.HashData(bytes.ToArray()));
    }
}
