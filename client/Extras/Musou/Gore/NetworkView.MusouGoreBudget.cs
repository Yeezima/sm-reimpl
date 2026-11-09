using Godot;
using SmNet.Extras.Musou;

namespace SmClient;

/// <summary>
/// Who gets gore first in heavy fighting (musou-net-budget; the owner 2026-11-09: "ensure that all player attacks in particular - then allied space marine attacks have high
/// priority for generating gore effects during heavy fighting"): the own hits and kills, then the allied Marines' (humans and AI), then the rest (<see cref="MusouGoreBudget"/>,
/// <see cref="MusouGorePriority"/>). Each source has its own hit-effect rate and kills-a-frame cap, own blood plays above the allies', and the crowd's tracers sit below every gore
/// effect, so in a full effect player they can never push blood out (it replaces the oldest effect at or below a new one's priority). <c>--musou-old-gore-budget</c>: one shared budget, all blood at priority 0 and the tracers at 1, as before.
/// Every 10 s a <c>musou gore budget:</c> line by source (own / ally / other): hits seen, too far, given an effect, refused by the rate; kills seen, unseen, too far, given blood, refused
/// by the frame cap; then the effect player's drops and refusals by priority.
/// </summary>
public partial class NetworkView
{
    static readonly bool MusouOldGoreBudget = CmdFlag("musou-old-gore-budget");
    readonly MusouGoreBudget _musouGore = new() { Old = MusouOldGoreBudget };
    readonly long[] _musouGoreHits = new long[3], _musouGoreHitsFar = new long[3], _musouGoreKills = new long[3], _musouGoreKillsUnseen = new long[3], _musouGoreKillsFar = new long[3];
    readonly long[] _musouGorePlays = new long[4];
    double _musouGoreBudgetLogAt;

    /// <summary>The source of an attack by player <paramref name="attacker"/> as this client sees it.</summary>
    MusouGoreSource MusouGoreSourceOf(int attacker) => MusouGorePriority.SourceOf(attacker, _client?.PlayerId ?? -1);

    /// <summary>The effect priority of a gore effect from <paramref name="source"/> (0 for all with <c>--musou-old-gore-budget</c>).</summary>
    int MusouGoreFxPriority(MusouGoreSource source) => MusouOldGoreBudget ? 0 : MusouGorePriority.FxPriority(source);

    /// <summary>The effect priority of the crowd's tracers (1 with <c>--musou-old-gore-budget</c>).</summary>
    static int MusouCrowdFirePriority => MusouOldGoreBudget ? 1 : MusouGorePriority.CrowdFirePriority;

    void MusouGorePlayed(int priority) => _musouGorePlays[System.Math.Clamp(priority, 0, 3)]++;

    void MusouGoreBudgetLog()
    {
        if (_connectedFor < _musouGoreBudgetLogAt) return;
        _musouGoreBudgetLogAt = _connectedFor + 10.0;
        if (_musouGoreHits[0] + _musouGoreHits[1] + _musouGoreHits[2] + _musouGoreKills[0] + _musouGoreKills[1] + _musouGoreKills[2] == 0) return;
        string S(int s) => $"{(MusouGoreSource)s}: hits {_musouGoreHits[s]} (far {_musouGoreHitsFar[s]}, fx {_musouGore.HitsTaken[s]}, rate {_musouGore.HitsRefused[s]}), " +
                           $"kills {_musouGoreKills[s]} (unseen {_musouGoreKillsUnseen[s]}, far {_musouGoreKillsFar[s]}, blood {_musouGore.KillsTaken[s]}, cap {_musouGore.KillsRefused[s]})";
        string fx = _fx is { } f ? $"; effects live {f.Live}, dropped by priority {string.Join("/", f.Evicted)}, refused {string.Join("/", f.Refused)}" : "";
        GD.Print($"musou gore budget{(MusouOldGoreBudget ? " (old)" : "")}: t {_connectedFor:F0} s: {S(0)}; {S(1)}; {S(2)}; blood effects played by priority {string.Join("/", _musouGorePlays)}{fx}");
    }
}
