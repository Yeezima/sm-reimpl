using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>The run's side of <see cref="TfwFeed"/> (TF4): what the war shows this tick, from <see cref="SendTfw"/>, once a tick.</summary>
public sealed partial class GameServer
{
    uint _tfwFedTick;

    /// <summary>The war, the reserves, the objectives, the Guardsmen (each slot once) and this tick's Guard events.</summary>
    void FeedTfw()
    {
        if (World.Tfw is not { } run || World.TfwGuards is not { } guards || run.Idle || _tfwFedTick == World.Tick) return;
        _tfwFedTick = World.Tick;
        var feed = TfwFeed;
        feed.War = run.War;   // a new run makes a new one, and tag 7 echoes the commands: never null
        feed.Transport = (byte)Math.Clamp(run.Transport, 0, byte.MaxValue);
        feed.Supplies = (byte)Math.Clamp(run.Supplies, 0, byte.MaxValue);
        feed.Objectives.Clear();
        feed.Objectives.AddRange(run.Objectives);
        feed.GuardCount = guards.CopyTo(feed.Guards);
        foreach (var e in guards.Events)
            feed.AddEvent(new TfwGuardEvent((TfwGuardEventKind)e.Kind, e.Tick, e.Guard, e.Enemy, e.Vector));
    }
}
