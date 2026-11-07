using System;
using System.Linq;
using SmNet;

namespace SmClient;

/// <summary>
/// Musou Mode's Tab scoreboard (added content; the owner, 2026-11-07: "for Musou its own key stats such as kills and score"): one column (one side), the columns KILLS (the server's KOs),
/// DEATHS, SCORE and PING, no assists; a player's score is the run's points the server sends (tag 12: the own <c>RunTotal</c>, the others' from the score list; 0 before the first).
/// </summary>
public partial class NetworkView
{
    partial void ExtrasScoreboard(ref bool single, ref Func<ScoreEntry, int>? score, ref bool assists)
    {
        if (!MusouActive || _client is null) return;
        single = true;
        assists = false;
        int self = _client.PlayerId;
        var own = _tfwState.ScoreOwn;
        var others = _tfwState.ScoreOthers;
        score = e => e.PlayerId == self ? (int)Math.Min(int.MaxValue, own?.RunTotal ?? 0u)
            : others.FirstOrDefault(p => p.Player == e.PlayerId) is { Player: > 0 } p ? (int)Math.Min(int.MaxValue, p.RunTotal) : 0;
    }
}
