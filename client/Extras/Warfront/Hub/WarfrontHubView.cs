using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SmSim.Extras.Warfront.Campaign;

namespace SmClient.Extras.Warfront.Hub;

/// <summary>What a lane did in the last finished deployment (the war map's "last deployment" line).</summary>
public enum HubLastKind
{
    /// <summary>Nothing finished yet, or the last deployment did not touch this lane (HQ defense, which never rolls).</summary>
    None,
    PlayedWon,
    PlayedWiped,
    /// <summary>A wipe on the played lane that the never-advanced node-1 floor absorbed.</summary>
    PlayedWipedHeldAtFloor,
    /// <summary>The end-of-deployment roll on another lane: won = the lane held.</summary>
    RollWon,
    RollLost,
    RollLostHeldAtFloor,
}

public readonly record struct HubPlayer(ulong Key, string Name, bool Connected);

/// <summary>
/// The plain snapshot the Warfront war map draws (plan sections 3 and 8): lanes, the leader, the valid deployment targets and the last deployment's results. Built from W4's
/// <see cref="WarfrontCampaign"/> by <see cref="FromCampaign"/> (the host's own view; W5 feeds the clients the same fields over the wire and builds this type from them), or filled
/// directly by a test. Immutable; <see cref="Signature"/> changes exactly when something drawn changes.
/// </summary>
public sealed class WarfrontHubView
{
    public const int Lanes = WarfrontCampaign.Lanes;

    public WarfrontPhase Phase { get; init; } = WarfrontPhase.Ship;
    /// <summary>Per lane the node being fought, 1 to 3; 4 = cleared; 0 = broken through to the HQ.</summary>
    public IReadOnlyList<int> Fronts { get; init; } = new[] { 1, 1, 1 };
    public IReadOnlyList<bool> Advanced { get; init; } = new[] { false, false, false };
    public bool HqMandatory { get; init; }
    /// <summary>Completed deployments.</summary>
    public int Deployments { get; init; }
    /// <summary>What the leader may deploy to (empty off the ship). Authoritative: the panel never works targets out itself.</summary>
    public IReadOnlyList<WarfrontTarget> Targets { get; init; } = Array.Empty<WarfrontTarget>();
    /// <summary>The deployment in Travel (the travel screen).</summary>
    public WarfrontTarget? CurrentTarget { get; init; }
    public ulong ViewerKey { get; init; }
    public ulong? LeaderKey { get; init; }
    /// <summary>Everyone in the run in join order (disconnected players too).</summary>
    public IReadOnlyList<HubPlayer> Players { get; init; } = Array.Empty<HubPlayer>();
    public IReadOnlyList<HubLastKind> Last { get; init; } = new[] { HubLastKind.None, HubLastKind.None, HubLastKind.None };

    public bool ViewerIsLeader => LeaderKey is { } l && l == ViewerKey;
    public bool FortressUnlocked => Fronts.Any(f => f == 4);
    public HubPlayer? Leader
    {
        get
        {
            if (LeaderKey is not { } l) return null;
            foreach (var p in Players) if (p.Key == l) return p;
            return null;
        }
    }

    string? _sig;
    /// <summary>Equal signatures draw identically.</summary>
    public string Signature => _sig ??= Sign();

    string Sign()
    {
        var sb = new StringBuilder();
        sb.Append((int)Phase).Append('|').Append(string.Join(',', Fronts)).Append('|').Append(string.Join(',', Advanced.Select(a => a ? 1 : 0)))
          .Append('|').Append(HqMandatory ? 1 : 0).Append('|').Append(Deployments).Append('|');
        foreach (var t in Targets) sb.Append((int)t.Kind).Append(':').Append(t.Lane).Append(';');
        sb.Append('|');
        if (CurrentTarget is { } c) sb.Append((int)c.Kind).Append(':').Append(c.Lane);
        sb.Append('|').Append(ViewerKey).Append('|').Append(LeaderKey?.ToString() ?? "-").Append('|');
        foreach (var p in Players) sb.Append(p.Key).Append(':').Append(p.Name).Append(':').Append(p.Connected ? 1 : 0).Append(';');
        sb.Append('|').Append(string.Join(',', Last.Select(k => (int)k)));
        return sb.ToString();
    }

    /// <summary>The view of one campaign for the player <paramref name="viewerKey"/>: every field comes from the campaign's public state.</summary>
    public static WarfrontHubView FromCampaign(WarfrontCampaign c, ulong viewerKey)
    {
        var fronts = new int[Lanes];
        var advanced = new bool[Lanes];
        for (int l = 0; l < Lanes; l++) { fronts[l] = c.Front(l); advanced[l] = c.Advanced(l); }

        var last = new HubLastKind[Lanes];
        if (c.Deployments > 0)
        {
            var entry = c.History.LastOrDefault(h => h.Deployment == c.Deployments);
            if (entry is not null && entry.Target.Lane >= 0)
                last[entry.Target.Lane] = entry.Outcome == WarfrontOutcome.Win ? HubLastKind.PlayedWon
                    : entry.HeldAtFloor ? HubLastKind.PlayedWipedHeldAtFloor : HubLastKind.PlayedWiped;
            foreach (var r in c.Rolls)
                if (r.Deployment == c.Deployments)
                    last[r.Lane] = !r.Lost ? HubLastKind.RollWon : r.HeldAtFloor ? HubLastKind.RollLostHeldAtFloor : HubLastKind.RollLost;
        }

        var players = new List<HubPlayer>();
        foreach (var k in c.JoinOrder)
        {
            var p = c.Player(k);
            players.Add(new HubPlayer(k, p?.Name ?? "", c.IsConnected(k)));
        }
        return new WarfrontHubView
        {
            Phase = c.Phase, Fronts = fronts, Advanced = advanced, HqMandatory = c.HqMandatory, Deployments = c.Deployments,
            Targets = c.AvailableTargets().ToArray(), CurrentTarget = c.CurrentTarget, ViewerKey = viewerKey, LeaderKey = c.Leader,
            Players = players, Last = last,
        };
    }
}
