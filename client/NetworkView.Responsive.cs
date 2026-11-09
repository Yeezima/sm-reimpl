using System;
using System.Globalization;
using System.Linq;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// net/responsive (ours; client only, no wire change): the switches of the latency pass, the latency readout's feed, and the predicted ranged hit feedback.
/// <list type="bullet">
/// <item><c>--old-input-lead</c>: the input lead steered by whole ticks as before (<see cref="ClientOptions.SmoothInputLead"/> off).</item>
/// <item><c>--old-own-draw</c>: the own marine drawn between the previous predicted tick and the newest, a tick later (<see cref="ClientOptions.DrawOwnAhead"/> off).</item>
/// <item><c>--interp-delay &lt;ms&gt;</c>: the remote players' interpolation delay fixed at that (<see cref="ClientOptions.AdaptiveInterpolation"/> off; 100 was the value before).</item>
/// <item><c>--no-predicted-hits</c>: the hit marker and hit confirmation of an own ranged hit wait for the server's report, as before.</item>
/// <item><c>--lead-ticks</c>: the input lead steered on the server's whole-tick lead report to 2 ticks (net/responsive's first step) instead of the input slack.</item>
/// <item><c>--no-extrapolation</c>: remote players wait at the newest snapshot when it is late, as before.</item>
/// <item><c>--old-net-feel</c>: the four above and no extrapolation. The server's <c>--snapshot-rate 20</c> restores its old snapshot rate.</item>
/// <item><c>--net-latency [s]</c> (or <c>SM_NET_LATENCY=&lt;s&gt;</c>): a <c>NETLAT</c> line every s seconds (default 5) with each latency stage (<see cref="NetLatencyReadout"/>).</item>
/// </list>
/// </summary>
public partial class NetworkView
{
    bool OldNetFeel => CmdFlag("old-net-feel");
    bool NoPredictedHits => _noPredictedHits ??= CmdFlag("no-predicted-hits") || OldNetFeel;
    bool? _noPredictedHits;
    readonly PredictedHitLedger _predictedHits = new();

    /// <summary>The latency switches and the readout, applied to the connection's options.</summary>
    ClientOptions WithNetFeelArgs(ClientOptions o)
    {
        bool old = OldNetFeel;
        if (old || CmdFlag("old-input-lead")) o = o with { SmoothInputLead = false };
        if (CmdFlag("lead-ticks")) o = o with { PreciseLead = false };
        if (old || CmdFlag("no-extrapolation")) o = o with { MaxExtrapolationTicks = 0 };
        if (old || CmdFlag("old-own-draw")) o = o with { DrawOwnAhead = false };
        if (CmdArg("interp-delay") is { } ms && double.TryParse(ms, NumberStyles.Float, CultureInfo.InvariantCulture, out double delay) && delay >= 0 && delay <= 500)
            o = o with { AdaptiveInterpolation = false, InterpolationDelay = TimeSpan.FromMilliseconds(delay) };
        else if (old) o = o with { AdaptiveInterpolation = false };
        if (CmdFlag("net-latency"))
            o = o with { LatencyReadout = CmdArg("net-latency") is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0 ? seconds : 5.0 };
        o = o with { LatencyLog = line => GD.Print(line) };
        GD.Print($"net feel: input lead {(!o.SmoothInputLead ? "old steps" : o.PreciseLead ? $"on the server's slack ({o.LeadLossTicks:0.#} tick + jitter + {o.LeadMarginMs:0} ms)" : $"slewed to {o.TargetInputLead} ticks")}, extrapolation {o.MaxExtrapolationTicks:0.#} tick, own marine {(o.DrawOwnAhead ? "drawn ahead" : "old draw")}, " +
                 $"interpolation {(o.AdaptiveInterpolation ? $"adaptive {o.MinInterpolationDelay.TotalMilliseconds:F0}-{o.MaxInterpolationDelay.TotalMilliseconds:F0} ms" : $"fixed {o.InterpolationDelay.TotalMilliseconds:F0} ms")}, " +
                 $"predicted hits {(NoPredictedHits ? "off" : "on")}{(o.LatencyReadout > 0 || NetLatencyReadout.EnvSeconds > 0 ? ", latency readout on" : "")}");
        return o;
    }

    /// <summary>
    /// From the own shot's effects (<c>FxPlayOwnShot</c>): a hit-scan shot whose traced ray strikes an enemy as drawn shows the hit marker and plays the hit confirmation now
    /// (<see cref="PredictedHitLedger"/>). Not for bots, spectators, allies or a cone or projectile weapon.
    /// </summary>
    void PredictOwnHit(PlayerState me, WeaponSpec weapon, int victim, ShotHit hit)
    {
        if (NoPredictedHits || _bot || _client is null || _config is null || victim <= 0 || weapon.Delivery != DeliveryKind.Raycast || hit is not (ShotHit.Body or ShotHit.Head)) return;
        var target = System.Linq.Enumerable.FirstOrDefault(_lastOthers, o => o.PlayerId == victim);
        if (target.PlayerId != victim || !target.Alive) return;
        bool teams = _client.Scoreboard?.Teams ?? true;
        if (teams && target.Team == me.Team) return;
        bool head = hit == ShotHit.Head;
        _predictedHits.Predict(victim, head, _connectedFor);
        _hudView?.ShowHitMarker(false, head);
        _client.NoteHitMarker(predicted: true);
        if (Loadouts.InHand(_config, me) is { } w && _soundTriggers?.File(w.Name) is { } wf && wf.Of("player_hit_something_action").FirstOrDefault() is { } confirm)
            HitConfirmSound(confirm, new HitReport(1, 0f, 0f, 0, victim, head, false, false, false, 0f, 0f, OwnHitArmoured(victim) ?? true), w.Name);
    }

    /// <summary>
    /// From <c>UpdateHits</c>: whether the server's report still shows its marker and plays its ranged hit confirmation (false: the predictions showed all of it).
    /// </summary>
    bool ReportAddsFeedback(HitReport hits) =>
        NoPredictedHits || _predictedHits.Confirm(hits.Hits, hits.LastVictim, hits.LastHead, hits.Kills > 0 || hits.LastKilled, hits.LastMelee, hits.LastSplash, _connectedFor);
}
