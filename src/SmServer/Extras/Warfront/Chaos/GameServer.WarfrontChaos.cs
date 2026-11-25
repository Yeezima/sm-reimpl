using SmNet;
using SmNet.Extras.Warfront;
using SmSim.Extras.Warfront.Chaos;

namespace SmServer;

/// <summary>
/// The Chaos section on the server (Chaos Warfront C4; tag 51, <see cref="WarfrontChaosWire"/>): only on a Warfront stage whose army is Chaos. The breach list goes to every identified client on a
/// change of the breaches and once a second (an empty list once when a battle's breaches go); each closure three times, three ticks apart. The breaches' lines (placement, squads, caps,
/// closures, unknown wargear keys) go to the Warfront log. C5's finale hangs off the two sub-partials declared here.
/// </summary>
public sealed partial class GameServer
{
    WarfrontBreaches? _wfBreachesSeen;
    int _wfBreachVersion = -1, _wfClosuresTaken;
    bool _wfBreachListOut;
    readonly List<(byte[] Bytes, int Left, uint Next)> _wfClosureSends = new();

    /// <summary>C4: the Chaos section's sends (the last line of <c>WarfrontSend</c>).</summary>
    partial void WarfrontChaosSend();
    /// <summary>C4: the Chaos stage's set-up (in <c>WarfrontCreated</c>).</summary>
    partial void WarfrontChaosCreated();
    /// <summary>C5's finale sends (kinds 16 to 31) and set-up; no-ops until it lands.</summary>
    partial void WarfrontChaosDuelSend();
    partial void WarfrontChaosDuelCreated();

    /// <summary>Whether this stage fights the Chaos army (the run's rules carry the roster).</summary>
    bool WarfrontChaosStage => World.Tfw?.Warfront?.ChaosRoster is not null;

    partial void WarfrontChaosCreated()
    {
        if (World.Config.WarfrontFaction != SmSim.Extras.Warfront.Faction.WarfrontFaction.Chaos) return;
        World.WarfrontChaosLog = WfLog;
        var unlisted = WarfrontBreachFilter.Unlisted(World.Config).ToList();
        WfLog($"chaos stage: warp breaches on (rules {WarfrontBreachRules.Version}); breach filter: {World.Config.Weapons.Count} weapons and {World.Config.Thrown.Count} thrown items, "
            + (unlisted.Count == 0 ? "every key classified" : $"{unlisted.Count} not in the table (they never close a breach): {string.Join(", ", unlisted)}"));
        WarfrontChaosDuelCreated();
    }

    partial void WarfrontChaosSend()
    {
        if (_wf is not { } s || !WarfrontChaosStage) return;
        uint tick = World.Tick;
        var breaches = World.WarfrontChaosBreaches;
        if (!ReferenceEquals(breaches, _wfBreachesSeen))
        {
            _wfBreachesSeen = breaches;
            _wfBreachVersion = -1;
            _wfClosuresTaken = 0;
        }
        var ids = s.KeyOf.Keys.ToList();
        if (breaches is not null)
        {
            bool changed = breaches.StateVersion != _wfBreachVersion;
            if (changed || tick % (uint)WarfrontRate == 0)
            {
                _wfBreachVersion = breaches.StateVersion;
                byte[] list = WarfrontChaosWire.EncodeBreaches(breaches);
                foreach (int id in ids) SendExtras(new ExtrasUpdate(tick, [(WarfrontChaosWire.Tag, list)]), id);
                _wfBreachListOut = true;
            }
            for (; _wfClosuresTaken < breaches.Closures.Count; _wfClosuresTaken++)
                _wfClosureSends.Add((WarfrontChaosWire.EncodeClosure(WarfrontChaosWire.Of(breaches.Closures[_wfClosuresTaken])), WarfrontChaosWire.ClosureRepeats, tick));
        }
        else if (_wfBreachListOut)
        {
            byte[] empty = WarfrontChaosWire.EncodeBreaches((WarfrontBreaches?)null);
            foreach (int id in ids) SendExtras(new ExtrasUpdate(tick, [(WarfrontChaosWire.Tag, empty)]), id);
            _wfBreachListOut = false;
        }
        for (int k = _wfClosureSends.Count - 1; k >= 0; k--)
        {
            var c = _wfClosureSends[k];
            if (tick < c.Next) continue;
            foreach (int id in ids) SendExtras(new ExtrasUpdate(tick, [(WarfrontChaosWire.Tag, c.Bytes)]), id);
            if (c.Left <= 1) _wfClosureSends.RemoveAt(k);
            else _wfClosureSends[k] = (c.Bytes, c.Left - 1, tick + WarfrontChaosWire.ClosureRepeatTicks);
        }
        WarfrontChaosDuelSend();
    }
}
