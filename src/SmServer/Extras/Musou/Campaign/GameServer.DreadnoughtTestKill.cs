namespace SmServer;

/// <summary>
/// Test aid <c>SM_DN_TEST_KILL=&lt;seconds&gt;</c> (Musou Dreadnought proof, 2026-11-15; never set in a real match): the server kills each living Dreadnought once, that many seconds after his entrance's lock ends, so his death and wreck can be filmed in an
/// open area before the Orks arrive. Prints <c>DNKILL</c> lines.
/// </summary>
public sealed partial class GameServer
{
    static readonly float? DreadnoughtTestKillAfter = float.TryParse(Environment.GetEnvironmentVariable("SM_DN_TEST_KILL"), System.Globalization.CultureInfo.InvariantCulture, out float k) && float.IsFinite(k) && k >= 0f ? k : null;   // NaN, infinity or a negative value is no setting
    readonly Dictionary<int, double> _dnKillSince = new();
    readonly HashSet<int> _dnKilled = new();

    /// <summary>Test aid <c>SM_DN_TEST_GUARD=1</c> (filming only): every living Dreadnought's health is topped up each step, so the Ork flood does not end a clip.</summary>
    static readonly bool DreadnoughtTestGuard = Environment.GetEnvironmentVariable("SM_DN_TEST_GUARD") == "1";

    void DreadnoughtTestKillStep()
    {
        if (DreadnoughtTestGuard) foreach (var id in World.Players.Keys.ToList()) World.DreadnoughtTestHeal(id);
        if (DreadnoughtTestKillAfter is not { } after) return;
        double now = World.Tick / (double)Math.Max(1, World.Config.TickRate);
        foreach (var (id, p) in World.Players)
        {
            if (!p.Alive || _dnKilled.Contains(id) || !SmSim.Extras.Musou.DreadnoughtRules.IsDreadnought(World.Config, p.Class) || World.DreadnoughtEntryLeft(id) > SmSim.Extras.Musou.DreadnoughtRules.InvulnerableSeconds - SmSim.Extras.Musou.DreadnoughtRules.EntranceSeconds) { _dnKillSince.Remove(id); continue; }
            if (!_dnKillSince.TryGetValue(id, out double since)) { _dnKillSince[id] = now; continue; }
            if (now - since < after) continue;
            _dnKilled.Add(id);
            World.DreadnoughtTestKill(id);
            Console.WriteLine($"DNKILL tick {World.Tick}: killed Dreadnought {id} at {p.Position}");
        }
    }
}
