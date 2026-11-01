using System;
using System.Collections.Generic;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// <c>--log-lunge</c> (anim/hammer-swing test aid, ours): one line per melee strike a drawn player completes, with how far the body travelled over it in the game's own positions
/// (the simulation's for the own player, the interpolated snapshots' for the others): the strike, its weapon, the distance at the strike's hit time and at its move gate, and the end.
/// The step-forward distance before / after a change to the lunge.
/// </summary>
public partial class NetworkView
{
    static readonly bool LogLunge = OS.GetCmdlineUserArgs().Contains("--log-lunge");

    sealed class LungeRun
    {
        public int Step; public bool Heavy; public System.Numerics.Vector3 Start, Last; public float LastTime, HitAt = float.NaN, MoveAt = float.NaN; public float AtHit, AtMove; public float Peak;
        public string Weapon = "";
    }
    readonly Dictionary<int, LungeRun> _lungeRuns = new();

    void LungeLogSample(PlayerState s)
    {
        if (!LogLunge || _config is null) return;
        _lungeRuns.TryGetValue(s.PlayerId, out var run);
        bool striking = s.Alive && s.MeleeStep > 0;
        bool restarted = run is not null && striking && (s.MeleeStep != run.Step || s.MeleeHeavy != run.Heavy || s.MeleeTime + 0.05f < run.LastTime);
        if (run is not null && (!striking || restarted))
        {
            GD.Print($"LUNGE P{s.PlayerId} {run.Weapon} {(run.Heavy ? "heavy" : "light")} {run.Step}: hit at {run.AtHit:F2} m, move gate at {run.AtMove:F2} m, end {Flat(run.Last - run.Start):F2} m (peak {run.Peak:F2}; strike ran {run.LastTime:F2} s)");
            _lungeRuns.Remove(s.PlayerId); run = null;
        }
        if (!striking) return;
        if (run is null)
        {
            _lungeRuns[s.PlayerId] = run = new LungeRun { Step = s.MeleeStep, Heavy = s.MeleeHeavy, Start = s.Position, Last = s.Position, Weapon = MeleeWargear(s) ?? "?" };
            if (Combat.MeleeStepOf(_config, s) is { } m) { run.HitAt = m.HitTime; run.MoveAt = m.MoveAt; }
        }
        run.Last = s.Position; run.LastTime = s.MeleeTime;
        float d = Flat(run.Last - run.Start);
        run.Peak = MathF.Max(run.Peak, d);
        if (s.MeleeTime >= run.HitAt && run.AtHit == 0f) run.AtHit = d;
        if (s.MeleeTime >= run.MoveAt && run.AtMove == 0f) run.AtMove = d;
    }

    static float Flat(System.Numerics.Vector3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);
}
