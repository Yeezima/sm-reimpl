using System.Numerics;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// musou/boss-collision (the owner, 2026-11-08): the big Orks (<see cref="MusouSolids"/>) are solid to the players on the server and in the own prediction (the same bodies,
/// built from the wire's records), the melee lock takes them, the crowd's Boyz stay walk-through, and Classic PvP's lock view and bodies are unchanged.
/// </summary>
public class MusouSolidsTests(ITestOutputHelper output)
{
    /// <summary>Orks that stand still and do no harm, so a test can place them.</summary>
    static readonly MusouRules Still = MusouRules.Default with
    {
        PlayerDamageScale = 0f,
        Types = [.. MusouRules.Default.Types.Select(t => t with { Speed = 0f })],
    };

    static SimConfig Config => MusouSimTests.Base with { MusouRules = Still };

    static World NewWorld()
    {
        var w = new World(Config) { Rules = MusouSimTests.Endless, BoundBodies = true };
        Assert.True(w.StartMusou(null));
        w.MusouDirector!.Hold = true;
        w.Spawn(1, w.AssignClass(0));
        w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, 0));   // in play, facing +y
        Assert.Equal(MatchState.Playing, w.State);
        return w;
    }

    static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));

    /// <summary>Walks player 1 forward (+y, sprinting) for <paramref name="ticks"/> ticks, each input naming the last tick as its body tick (as a client with no lag); the closest it came to <paramref name="axis"/>.</summary>
    static float WalkInto(World w, Vector3 axis, int ticks)
    {
        float closest = float.MaxValue;
        for (int t = 0; t < ticks; t++)
        {
            uint body = w.Tick;
            w.Step(_ => new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint, BodyTick: body));
            closest = MathF.Min(closest, Flat(w.Players[1].Position, axis));
        }
        return closest;
    }

    [Theory]
    [InlineData(MusouEnemyType.Warboss)]
    [InlineData(MusouEnemyType.BossNob)]
    [InlineData(MusouEnemyType.BossBoy)]
    [InlineData(MusouEnemyType.BossShootaBoy)]
    [InlineData(MusouEnemyType.BossArdBoy)]
    public void A_player_cannot_walk_through_a_big_ork_on_the_server(MusouEnemyType type)
    {
        Assert.True(MusouSolids.Blocks(type));
        var w = NewWorld();
        var (radius, _) = MusouSolids.Body(Still, type);
        var axis = w.Players[1].Position + new Vector3(0f, radius + 3f, 0f);
        w.Musou!.Spawn(type, axis, MathF.PI, 50000f);
        w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, 0));
        float closest = WalkInto(w, axis, 90);
        float contact = radius + Config.CapsuleRadius;
        output.WriteLine($"{type}: radius {radius:F2} m, closest {closest:F3} m (contact {contact:F3}), ended at {w.Players[1].Position}");
        Assert.InRange(closest, contact - 0.01f, contact + 0.1f);
        Assert.True(w.Players[1].Position.Y < axis.Y);   // never past him
    }

    /// <summary>The pass-through (the owner, 2026-11-09: "players should passthrough everything but the big boss enemies and the warboss"): every other kind is walked through, the elites too.</summary>
    [Theory]
    [InlineData(MusouEnemyType.Boy)]
    [InlineData(MusouEnemyType.ShootaBoy)]
    [InlineData(MusouEnemyType.ArdBoy)]
    [InlineData(MusouEnemyType.Gretchin)]
    [InlineData(MusouEnemyType.Nob)]
    [InlineData(MusouEnemyType.BigShootaNob)]
    [InlineData(MusouEnemyType.ScrapMek)]
    [InlineData(MusouEnemyType.ChaosMarine)]
    [InlineData(MusouEnemyType.RokkitBoy)]
    [InlineData(MusouEnemyType.Stormboy)]
    public void The_crowds_boyz_stay_walk_through(MusouEnemyType type)
    {
        Assert.False(MusouSolids.Blocks(type));
        var w = NewWorld();
        var axis = w.Players[1].Position + new Vector3(0f, 3f, 0f);
        w.Musou!.Spawn(type, axis, MathF.PI, 50000f);
        w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, 0));
        WalkInto(w, axis, 90);
        Assert.True(w.Players[1].Position.Y > axis.Y + 1f);   // walked through and on
    }

    [Fact]
    public void The_own_prediction_meets_the_big_orks_exactly_where_the_server_does()
    {
        var w = NewWorld();
        var sim = w.Musou!;
        var axis = w.Players[1].Position + new Vector3(0.6f, 6f, 0f);   // off the line: the walk slides round the Warboss's side
        sim.Spawn(MusouEnemyType.Warboss, axis, MathF.PI, 50000f);
        sim.Spawn(MusouEnemyType.BossNob, axis + new Vector3(-6f, 4f, 0f), MathF.PI, 50000f);
        var box = new MusouBox(new Vector3(-64f, -64f, -4f), new Vector3(64f, 64f, 12f));
        sim.WireBox = box;   // as the server's send hook sets it

        // The client: the director (the box) and every tick's records, as the wire carries them.
        var client = new MusouClientState();
        var view = new MusouSolidView(client, Config);
        Assert.True(client.Apply(new ExtrasSection(w.Tick, MusouWire.DirectorTag, MusouWire.WriteDirector(sim.Director with { Box = box })), 0));
        var enemies = new MusouEnemy[MusouSim.Capacity];
        void Send()
        {
            int n = sim.CopyTo(enemies);
            var list = enemies.Take(n).Select(e => new MusouEntity(e.Id, e.Position, e.Yaw, e.Type, e.State)).ToList();
            Assert.True(client.Apply(new ExtrasSection(w.Tick, MusouWire.EntitiesTag, MusouWire.WriteEntities(list, box)), w.Tick / 30.0));
        }
        w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, 0));
        Send();

        var predicted = w.Players[1];
        float closest = float.MaxValue;
        int stepsTouching = 0;
        for (int t = 0; t < 120; t++)
        {
            uint body = w.Tick;
            var input = new PlayerInput(w.Tick + 1, t < 60 ? 0f : -0.3f, 1f, 0f, InputButtons.Sprint, BodyTick: body);
            // The client's step: the players of the body tick (the server's record of them is what the snapshot carried), plus the big Orks from its own records.
            var bodies = PlayerCollision.See(w.BodiesAt(body), 1, body, input.Tick, Config);
            view.AddBodies(body, ref bodies);
            Assert.NotNull(bodies);
            var server = w.MusouSolidsAt(body);
            var mine = new List<MusouSolid>();
            client.SolidsAt(body, mine);
            Assert.Equal(server, mine);   // the same Orks at the same floats
            using (PlayerCollision.Use(bodies)) predicted = Combat.StepPlayer(predicted, input, Config, out _);
            w.Step(_ => input);
            Send();
            Assert.Equal(w.Players[1].Position, predicted.Position);   // no correction: the prediction is exact
            float d = Flat(predicted.Position, axis);
            closest = MathF.Min(closest, d);
            if (d < MusouSolids.Body(Still, MusouEnemyType.Warboss).Radius + Config.CapsuleRadius + 0.02f) stepsTouching++;
        }
        output.WriteLine($"closest {closest:F3} m to the Warboss's axis, {stepsTouching} ticks against him, ended at {predicted.Position}");
        Assert.True(stepsTouching > 5);   // it really met him
        Assert.True(closest >= MusouSolids.Body(Still, MusouEnemyType.Warboss).Radius + Config.CapsuleRadius - 0.01f);
    }

    [Fact]
    public void The_melee_lock_takes_a_big_ork_in_its_cone_and_not_a_boy()
    {
        var w = NewWorld();
        var me = w.Players[1];
        ushort nob = w.Musou!.Spawn(MusouEnemyType.Nob, me.Position + new Vector3(0.5f, 3f, 0f), MathF.PI, 50000f);
        ushort boy = w.Musou!.Spawn(MusouEnemyType.Boy, me.Position + new Vector3(-0.3f, 2f, 0f), MathF.PI, 50000f);
        w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, 0));
        w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, 0));
        me = w.Players[1];
        var press = new PlayerInput(w.Tick + 1, 0f, 0f, 0f, InputButtons.Melee, ViewTick: w.Tick);
        var lockView = w.MeleeLockView(1, press);
        Assert.False(lockView.ContainsKey(MusouSolids.LockId(boy)));
        int chosen = MeleeLock.Choose(me, press, lockView, Config, null, 0);
        Assert.Equal(MusouSolids.LockId(nob), chosen);
        Assert.NotNull(MeleeLock.Target(me, press, chosen, lockView, Config, null, MeleeLock.Phase.MeleeStart));
        // Its lock point stands at the Nob's front toward the attacker (the body's radius less a player's).
        var target = lockView[chosen];
        Assert.True(MusouSolids.IsLockId(chosen, out var id) && id == nob);
        Assert.Equal(3.04f - (0.6f - Config.CapsuleRadius), Flat(target.Position, me.Position), 2);

        // Behind the player (out of the cone): no lock; the server accepts the press with the lock and the step turns to it.
        Assert.Equal(0, MeleeLock.Choose(me with { Yaw = MathF.PI }, press with { Yaw = MathF.PI }, lockView, Config, null, 0));
        var locked = press with { MeleeTarget = chosen };
        w.Step(_ => locked);
        Assert.True(w.Players[1].MeleeStep > 0);
    }

    [Fact]
    public void Classic_pvp_keeps_its_lock_view_and_bodies()
    {
        // No Musou: the lock view is the players' rewound view itself, and the bodies are the players' alone.
        var c = MusouSimTests.Base with { Extras = ContentExtras.None };
        var w = new World(c) { Rules = MusouSimTests.Endless, BoundBodies = true };
        Assert.False(w.StartMusou(null));
        w.Spawn(1, w.AssignClass(0)); w.Spawn(2, w.AssignClass(1));
        for (int t = 0; t < 3; t++) w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, 0));
        var press = new PlayerInput(w.Tick + 1, 0f, 0f, 0f, InputButtons.Melee, ViewTick: w.Tick);
        Assert.Same(w.WholeTickView(press), w.MeleeLockView(1, press));
        Assert.Empty(w.MusouSolidsAt(w.Tick));
        // A player's body (radius and height 0, not fixed) relates and pushes as before: two capsules meet at two radii.
        var body = new PlayerBody(2, new Vector3(0f, 3f, 0f), new Vector3(0f, 3f, 0f));
        Assert.Equal((0f, 0f, false), (body.Radius, body.Height, body.Fixed));
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c);
        float closest = float.MaxValue;
        using (PlayerCollision.Use(BodyView.Of([body])))
            for (int k = 0; k < 60; k++) { s = Combat.StepPlayer(s, new PlayerInput((uint)k + 1, 0f, 1f, 0f, InputButtons.Sprint), c, out _); closest = MathF.Min(closest, Flat(s.Position, body.Feet)); }
        Assert.InRange(closest, 2f * c.CapsuleRadius - 2e-3f, 2f * c.CapsuleRadius + 0.05f);
    }

    [Fact]
    public void Only_the_warboss_and_the_boss_orks_block_while_every_big_ork_stays_lockable()
    {
        var blocking = Enum.GetValues<MusouEnemyType>().Where(MusouSolids.Blocks).ToHashSet();
        Assert.Equal(new HashSet<MusouEnemyType> { MusouEnemyType.Warboss, MusouEnemyType.BossBoy, MusouEnemyType.BossShootaBoy, MusouEnemyType.BossArdBoy, MusouEnemyType.BossNob }, blocking);
        foreach (var t in new[] { MusouEnemyType.Nob, MusouEnemyType.BigShootaNob, MusouEnemyType.Weirdboy, MusouEnemyType.ScrapMek }) Assert.True(MusouSolids.Is(t));   // still offered to the melee lock
    }

    [Fact]
    public void Players_and_allied_marines_leave_the_musou_view_and_the_old_switch_brings_them_back()
    {
        MusouSolids.OldBodies = true;
        try
        {
            Assert.True(MusouSolids.Blocks(MusouEnemyType.Nob));
            var view = BodyView.Of([new PlayerBody(2, Vector3.Zero, Vector3.Zero)], Vector3.One);
            BodyView? v = view;
            MusouSolids.AddBodies(ref v, [new MusouSolid(1, MusouEnemyType.Nob, Vector3.UnitY)], Still);
            Assert.Equal(2, v!.Bodies.Count);
        }
        finally { MusouSolids.OldBodies = false; }
        BodyView? now = BodyView.Of([new PlayerBody(2, Vector3.Zero, Vector3.Zero)], Vector3.One);
        MusouSolids.AddBodies(ref now, [new MusouSolid(1, MusouEnemyType.Nob, Vector3.UnitY), new MusouSolid(2, MusouEnemyType.BossBoy, Vector3.UnitX)], Still);
        Assert.Equal(MusouSolids.LockId(2), Assert.Single(now!.Bodies).Id);   // the Boss Boy alone: no player, no Nob
        Assert.Equal(Vector3.One, now.Self);
    }
}
