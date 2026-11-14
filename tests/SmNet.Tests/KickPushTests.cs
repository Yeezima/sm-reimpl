using System.Numerics;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// sim/kick-push (owner 2026-11-14: "only the devastator/havoc kick move (right mouse button) knocks back too far. reduce the knockback by 60% - the kick move only. don't touch the stomp"): the heavy classes'
/// kick (<see cref="MeleeStep.Kick"/>) pushes 40 % as far (2 m becomes 0.8 m); other light attacks, the stomp and Musou's enemies are unchanged; the restore switch gives the old push.
/// </summary>
public class KickPushTests
{
    static readonly SimConfig Config = StompUninterruptibleTests.Config;
    static readonly ReactionEntry Push2 = new(0f, ReactionKind.Stun, Backward: 2f, Duration: 1f);

    /// <summary>The knockback (1/16 m) a target takes from the first melee press, with the step given (a kick or not) or the stomp (F).</summary>
    static int PushOf(SimConfig c, bool kick, bool stomp = false)
    {
        var strike = StompUninterruptibleTests.Kick with { Kick = kick, PrimaryReaction = Push2 };
        var heavy = StompUninterruptibleTests.Stomp with { Stomp = StompUninterruptibleTests.Stomp.Stomp! with { Reactions = [Push2] } };
        var cfg = c with { Classes = [c.Classes[0] with { Melee = [strike], MeleeHeavy = [heavy] }, c.Classes[0] with { Name = "target", Side = Team.Chaos }] };
        var w = new World(cfg, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0f, 2f, 0f), 0f)]) { Rules = new MatchRules { TimeLimit = 0f } };
        w.Spawn(1, 0, team: Team.SpaceMarines);
        w.Spawn(2, 1, team: Team.Chaos);
        var events = new List<ReactionEvent>();
        for (int k = 0; k < 60; k++)
        {
            int tick = k; uint view = w.Tick;
            w.Step(id => (id == 1 ? new PlayerInput(0, 0f, 0f, 0f, tick == 8 ? (stomp ? InputButtons.MeleeHeavy : InputButtons.Melee) : InputButtons.None) : new PlayerInput(0, 0f, 0f, MathF.PI, InputButtons.None)) with { ViewTick = view });
            events.AddRange(w.LastReactions.Where(r => r.Target == 2));
        }
        return events.Count == 0 ? -1 : events[0].PushDistance;
    }

    [Fact]
    public void The_kick_pushes_40_percent_as_far_and_the_switch_restores_it()
    {
        Assert.Equal(32, PushOf(Config with { KickPushCut = false }, kick: true));   // 2 m = 32 sixteenths
        Assert.Equal(13, PushOf(Config, kick: true));                                // 0.8 m = 12.8
        Assert.Equal(0.4f, KickTuning.PushScale);
        Assert.NotEqual(Config.Hash(), (Config with { KickPushCut = false }).Hash());
    }

    [Fact]
    public void Another_light_attack_and_the_stomp_are_unchanged()
    {
        Assert.Equal(-1, PushOf(Config, kick: false));   // a light attack that is not the kick flinches only (no reaction, no push), as before
        Assert.Equal(32, PushOf(Config, kick: true, stomp: true));   // F: the stomp's blast, not the kick
    }

    [Fact]
    public void Musou_enemies_keep_their_own_knockback()
    {
        Assert.Equal(2f, new SmSim.Extras.Musou.MusouCombatRules().BlastKnockback);   // Musou's enemies take rules' constants, never the step's reaction entry
    }
}
