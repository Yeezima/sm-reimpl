using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// The owner, 2026-11-09 (clip 1152): "the weird boy currently does nothing. ensure he shoots his magic bolt ranged attack". In a war's crowd the Shoota Boyz nearer the target always held its shooter
/// tokens, so he never threw. His bolt is now his own (no token): with a target in range and in sight he casts (<see cref="MusouEnemyState.Casting"/>) and throws within seconds, and the bolt lands and hurts,
/// on a player, on a Guard squad, and through the World on a real map.
/// </summary>
public class MusouWeirdboyBoltTests
{
    static int Ticks(float seconds) => (int)MathF.Round(seconds * 30f);

    /// <summary>A war's crowd: the Weirdboy 30 m from the target and a dozen Shoota Boyz between, nearer, who take every shooter token the target has.</summary>
    static (MusouSim Sim, ushort Me) Crowd(Vector3 target)
    {
        var sim = new MusouSim(null) { Swarm = new MusouSwarmRules(), War = TfwCombatRules.Default };
        ushort me = sim.Spawn(MusouEnemyType.Weirdboy, target + new Vector3(0f, -30f, 0f), MathF.PI / 2f, 50f);
        for (int k = 0; k < 12; k++) sim.Spawn(MusouEnemyType.ShootaBoy, target + new Vector3(-6f + k, -15f, 0f), MathF.PI / 2f, 85f);
        return (sim, me);
    }

    [Theory]
    [InlineData(1, 0f)]      // a Marine player
    [InlineData(-3, 4f)]     // a Guard squad (its men spread 4 m round its centre)
    public void In_a_crowd_whose_Shoota_Boyz_hold_every_token_he_still_casts_and_his_bolt_lands_for_35(int id, float radius)
    {
        var at = new Vector3(5f, 40f, 0f);
        var (sim, me) = Crowd(at);
        MusouTarget[] targets = [new MusouTarget(id, at, true, radius)];
        var attacks = new List<MusouAttack>();
        uint tick = 0, cast = 0, thrown = 0, landed = 0;
        MusouEvent cue = default;
        for (; tick < Ticks(6f) && landed == 0; )
        {
            sim.BeginTick(++tick);
            attacks.Clear();
            sim.Step(targets, null, true, attacks);
            if (cast == 0 && sim.TryGet(me, out var e) && e.State == MusouEnemyState.Casting) cast = tick;
            foreach (var ev in sim.Events)
                if (ev.Kind == MusouEventKind.Telegraph && ev.Enemy == me && ev.Value == MusouWeirdboy.BoltCue) { thrown = tick; cue = ev; }
            if (attacks.FirstOrDefault(a => a.Enemy == me) is { Enemy: not 0 } hit)
            {
                landed = tick;
                Assert.Equal((id, 35f, false), (hit.Player, hit.Damage, hit.Melee));
            }
        }
        Assert.True(cast > 0 && cast <= Ticks(2f), $"he began a cast within 2 s (cast tick {cast})");
        Assert.Equal(cast + (uint)Ticks(MusouWeirdboy.BoltCastSeconds), thrown);   // the bolt leaves when the cast ends
        Assert.Equal(at, cue.Vector);                                                // at the target's feet
        int flight = (int)MathF.Round(Vector3.Distance(at + new Vector3(0f, -30f, 1.5f), at) / 40f * 30f);
        Assert.InRange((int)(landed - thrown), flight - 2, flight + 2);              // after its 40 m/s flight
    }

    [Fact]
    public void A_stagger_cuts_his_cast_and_the_bolt_is_lost()
    {
        var at = new Vector3(0f, 30f, 0f);
        var sim = new MusouSim(null);
        ushort me = sim.Spawn(MusouEnemyType.Weirdboy, Vector3.Zero, MathF.PI / 2f, 50f);
        MusouTarget[] targets = [new MusouTarget(1, at, true)];
        var attacks = new List<MusouAttack>();
        uint tick = 0;
        bool Casting() => sim.TryGet(me, out var e) && e.State == MusouEnemyState.Casting;
        for (; tick < Ticks(3f) && !Casting(); ) { sim.BeginTick(++tick); sim.Step(targets, null, true, attacks); }
        Assert.True(Casting());
        Assert.True(sim.Stagger(me, Vector2.UnitX, 1f));
        bool thrown = false;
        for (int k = 0; k < Ticks(1f); k++)
        {
            sim.BeginTick(++tick); sim.Step(targets, null, true, attacks);
            thrown |= sim.Events.Any(e => e.Kind == MusouEventKind.Telegraph && e.Enemy == me && e.Value == MusouWeirdboy.BoltCue);
        }
        Assert.False(thrown);
    }

    [Fact]
    public void In_a_war_on_the_real_map_he_casts_and_his_bolt_reaches_whom_he_aimed_at_and_hurts()
    {
        var w = TfwRig.War();
        w.Tfw!.Guardsmen = false;
        TfwRig.Deploy(w);
        for (int id = TfwContracts.PuppetIdFirst; id <= TfwContracts.PuppetIdLast; id++) w.TfwDismissMarine(id);   // the player alone is his target (the AI Marines move on and dodge)
        TfwRig.KillAll(w);
        TfwRig.Steps(w, 2);
        var player = w.Players[TfwRig.Human].Position;
        ushort me = 0;
        foreach (var dir in new[] { Vector2.UnitY, -Vector2.UnitY, Vector2.UnitX, -Vector2.UnitX })   // a spot 30 m off in a clear line
        {
            var spot = player + new Vector3(dir * 30f, 0f);
            var eye = spot + new Vector3(0f, 0f, 1.5f); var ray = player + new Vector3(0f, 0f, 1.2f) - eye;
            if (w.Collision.Weapons.Raycast(eye, Vector3.Normalize(ray), ray.Length()) is { } wall && wall.Distance < ray.Length() - 0.1f) continue;
            me = w.Musou!.Spawn(MusouEnemyType.Weirdboy, spot, MathF.Atan2(-dir.Y, -dir.X), 50f);
            break;
        }
        Assert.NotEqual(0, me);
        bool cast = false, hurt = false;
        int aimed = 0;
        for (int k = 0; k < Ticks(6f) && !hurt; k++)
        {
            TfwRig.KillAll(w, spare: me);   // only he fights
            float before = aimed != 0 && w.Players.TryGetValue(aimed, out var b) ? b.Health + b.Shield : 0f;
            w.Step(_ => default);
            cast |= w.Musou!.TryGet(me, out var e) && e.State == MusouEnemyState.Casting;
            if (aimed != 0 && w.Players.TryGetValue(aimed, out var a) && a.Health + a.Shield < before) hurt = true;
            if (aimed == 0 && w.Musou.Events.FirstOrDefault(x => x.Kind == MusouEventKind.Telegraph && x.Enemy == me && x.Value == MusouWeirdboy.BoltCue) is { Enemy: not 0 } cue) aimed = cue.Player;
        }
        Assert.True(cast, "he cast");
        Assert.Equal(TfwRig.Human, aimed);
        Assert.True(hurt, $"the bolt landed and hurt {aimed}");
    }
}
