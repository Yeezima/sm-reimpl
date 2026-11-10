using System.Numerics;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// The chain lightning's reliability (musou-balance, owner 2026-11-11: "librarian chain lightning spell doesn't seem to be reliably delivering damage, increase its range, damage, and reliability of
/// delivering damage"; "+25% range and damage"). A crowd runs across the Librarian's view; the own client draws its foreseen chain (<see cref="LibrarianPrediction"/>) on the crowd as it shows it, the server's
/// world <c>delay</c> ticks ago (the interpolation delay, the input lead, the round trip), with an aim that tracks the Ork it draws; the server casts with the same aim. Counted per casting tick: the targets
/// drawn, the targets damaged (the tick's Hit events of the Librarian), and both. On main the server picked the chain on its crowd now; since musou-balance it picks it on the crowd at the input's view tick
/// (<see cref="LibrarianHistory"/>, <see cref="LibrarianSight"/>), so the chain drawn is the chain struck.
/// </summary>
public class MusouLibrarianReliabilityTests(ITestOutputHelper output)
{
    const int Rate = 30;
    const float Dt = 1f / Rate;

    internal readonly record struct Counts(int Ticks, int DrawnTicks, int MissedTicks, int Drawn, int Damaged, int Both, int PrimaryMismatch)
    {
        public int DrawnNotDamaged => Drawn - Both;
        public int DamagedNotDrawn => Damaged - Both;
        public override string ToString() =>
            $"casting ticks {Ticks}, a chain drawn on {DrawnTicks}, the server struck none of it on {MissedTicks}; targets drawn {Drawn}, damaged {Damaged}, both {Both} ({(Drawn > 0 ? 100.0 * Both / Drawn : 0):F1} % of drawn), " +
            $"drawn not damaged {DrawnNotDamaged}, damaged not drawn {DamagedNotDrawn}, primary differs on {PrimaryMismatch}";
    }

    /// <summary>How the server casts: on its crowd now (main before musou-balance; the restore switch <c>rewind</c>), or on the crowd at the input's view tick.</summary>
    internal enum Cast { Present, Rewind }

    /// <summary>
    /// <paramref name="ticks"/> casting ticks against <paramref name="orks"/> Orks crossing the view (too tough to die), the client <paramref name="delay"/> ticks behind the server's crowd.
    /// </summary>
    internal static Counts Measure(int delay, int ticks, Cast cast, int orks = 140, int seed = 3)
    {
        var sim = new MusouSim(null) { TickRate = Rate };
        var rng = new Random(seed);
        for (int k = 0; k < orks; k++)   // a band left of his view running right, toward an ally at (40, 20): they cross the aim
            sim.Spawn(k % 9 == 0 ? MusouEnemyType.Nob : MusouEnemyType.Boy, new Vector3(-45f + 30f * rng.NextSingle(), 8f + 22f * rng.NextSingle(), 0f), 0f, 1e7f);
        var targets = new[] { new MusouTarget(2, new Vector3(40f, 20f, 0f), true) };
        var attacks = new List<MusouAttack>();
        var shown = new Dictionary<uint, MusouEnemy[]>();   // what the client is sent for each tick
        var history = new LibrarianHistory(new SimConfig().MaxRewindTicks + 2);
        var sight = new LibrarianSight();
        var psyker = new LibrarianPsyker();
        var feet = Vector3.Zero;
        var eye = new Vector3(0f, 0f, 1.6f);
        var links = new List<LibrarianLink>();
        var scratch = new bool[MusouSim.Capacity];
        var scratch2 = new bool[MusouSim.Capacity];
        var order = new List<LibrarianLink>();
        var chain = new int[LibrarianRules.MaxTargets];
        var chainScratch = new LibrarianChain.Scratch();
        var all = new MusouEnemy[MusouSim.Capacity];
        uint tick = 0;
        ushort tracked = 0;
        int total = 0, drawnTicks = 0, missed = 0, drawn = 0, damaged = 0, both = 0, primaryMismatch = 0;
        void EndOfTick() { shown[tick] = all[..sim.CopyTo(all)]; history.Record(tick, sim); shown.Remove(tick - 40); }
        for (int k = 0; k < 60; k++) { sim.BeginTick(++tick); sim.Step(targets, null, false, attacks); EndOfTick(); }   // let them run in
        for (int k = 0; k < ticks; k++)
        {
            sim.BeginTick(++tick);
            uint viewTick = tick - 1 - (uint)delay;
            var seen = shown[viewTick];
            var candidates = seen.Where(e => e.Alive).Select(e => new LibrarianCandidate(e.Id, e.Position, LibrarianPrediction.HeightOf(MusouRules.Default, e.Type))).ToArray();
            // The player's aim: on the Ork he tracks as drawn (a new one, nearest the view's centre ahead, when it is gone or out of reach).
            var aimAt = candidates.FirstOrDefault(c => c.Id == tracked);
            if (aimAt.Id == 0 || new Vector2(aimAt.Feet.X, aimAt.Feet.Y).Length() > 28f || aimAt.Feet.X > 20f)
            {
                aimAt = candidates.Where(c => c.Feet.Y > 4f && c.Feet.Length() < 26f && c.Feet.X < 5f).OrderBy(c => MathF.Abs(MathF.Atan2(-c.Feet.X, c.Feet.Y))).FirstOrDefault();
                tracked = aimAt.Id;
            }
            float yaw = 0f, pitch = 0f;
            if (aimAt.Id != 0)
            {
                var chest = MusouSim.Chest(aimAt.Feet, aimAt.Height) - eye;
                yaw = MathF.Atan2(-chest.X, chest.Y);
                pitch = MathF.Atan2(chest.Z, new Vector2(chest.X, chest.Y).Length());
            }
            int count = LibrarianPrediction.Chain(feet, eye, yaw, pitch, candidates, null, links, scratch);
            var drawnIds = new HashSet<ushort>();
            for (int i = 0; i < candidates.Length && count > 0; i++) if (scratch[i]) drawnIds.Add(candidates[i].Id);
            LibrarianChain.Run(feet, eye, yaw, pitch, candidates, null, order, scratch2, chain, chainScratch);   // the same chain in order: its primary
            ushort drawnPrimary = count > 0 ? candidates[chain[0]].Id : (ushort)0;

            var caster = new LibrarianCaster(1, feet, eye, yaw, pitch);
            LibrarianSight? at = cast == Cast.Rewind && history.SightAt(viewTick, 0f, sim, sight, 1000f) ? sight : null;
            psyker.Step(sim, caster, new LibrarianPress(true, false), Dt, sight: at);
            var hitIds = new HashSet<ushort>(sim.Events.Where(e => e.Kind == MusouEventKind.Hit && e.Player == 1 && e.Tick == tick).Select(e => e.Enemy));
            bool samePrimary = drawnPrimary == (psyker.Chained.Count > 0 ? psyker.Chained[0] : (ushort)0);

            total++;
            if (drawnIds.Count > 0) drawnTicks++;
            if (drawnIds.Count > 0 && hitIds.Count == 0) missed++;
            drawn += drawnIds.Count; damaged += hitIds.Count;
            both += drawnIds.Count(hitIds.Contains);
            if (!samePrimary) primaryMismatch++;

            sim.Step(targets, null, false, attacks);
            EndOfTick();
        }
        return new Counts(total, drawnTicks, missed, drawn, damaged, both, primaryMismatch);
    }

    [Fact]
    public void Measure_the_drawn_and_the_damaged_targets_before_and_after()
    {
        foreach (int orks in new[] { 140, 12 })
            foreach (int delay in new[] { 0, 3, 7, 10 })
            {
                output.WriteLine($"{orks} Orks, client {delay} ticks behind, the crowd now:      {Measure(delay, 450, Cast.Present, orks)}");
                output.WriteLine($"{orks} Orks, client {delay} ticks behind, the view's crowd: {Measure(delay, 450, Cast.Rewind, orks)}");
            }
    }

    [Theory]
    [InlineData(0, 140)]
    [InlineData(3, 140)]
    [InlineData(7, 140)]
    [InlineData(10, 140)]
    [InlineData(15, 140)]
    [InlineData(7, 12)]
    [InlineData(10, 12)]
    public void Every_target_drawn_is_damaged_and_nothing_else_whatever_the_clients_delay(int delay, int orks)
    {
        var c = Measure(delay, 450, Cast.Rewind, orks);
        output.WriteLine(c.ToString());
        Assert.True(c.DrawnTicks > 100, $"the chain was drawn on {c.DrawnTicks} ticks");
        Assert.Equal(0, c.MissedTicks);
        Assert.Equal(c.Drawn, c.Both);
        Assert.Equal(c.Drawn, c.Damaged);
        Assert.Equal(0, c.PrimaryMismatch);
    }

    [Fact]
    public void On_main_a_client_behind_the_server_saw_targets_struck_that_were_not_drawn()
    {
        // The cause, kept as a pin (the restore switch rewind casts on the crowd now): with the client 7 ticks behind (100 ms of interpolation and the input lead on a local server) the server struck
        // another chain on most casting ticks.
        var c = Measure(7, 450, Cast.Present);
        Assert.True(c.PrimaryMismatch > c.DrawnTicks / 3, c.ToString());
        Assert.True(c.DrawnNotDamaged > c.Drawn / 5, c.ToString());
    }

    // ---------------------------------------------------------------- the World

    static PlayerInput Hold(float yaw, float pitch, uint viewTick, float fraction = 0f) => new(0, 0f, 0f, yaw, InputButtons.Fire, Pitch: pitch, ViewTick: viewTick, ViewFraction: fraction);

    /// <summary>A Librarian in a Musou world (the crowd held still: <see cref="MusouSim.Frozen"/>), his yaw, the forward and side directions, and the pitch onto a Boy's chest <paramref name="at"/> m ahead.</summary>
    static (World W, PlayerState Me, Vector3 Forward, Vector3 Side, float Pitch) Scene(float at, LibrarianVariant variant = default)
    {
        var w = MusouLibrarianTests.NewWorld(out int cls, variant);
        w.Spawn(1, cls);
        w.Step(_ => default);
        w.Musou!.Frozen = true;
        var me = w.Players[1];
        var forward = new Vector3(-MathF.Sin(me.Yaw), MathF.Cos(me.Yaw), 0f);
        var side = new Vector3(forward.Y, -forward.X, 0f);
        float chest = MusouSim.Chest(Vector3.Zero, w.Musou.Body(MusouEnemyType.Boy).Height).Z - w.Config.EyeHeight;
        return (w, me, forward, side, MathF.Atan2(chest, at));
    }

    [Fact]
    public void In_the_world_the_chain_is_struck_where_his_input_view_tick_had_the_orks()
    {
        var (w, me, forward, side, pitch) = Scene(15f);
        var musou = w.Musou!;
        // Two Boys 15 m out: A on the aim, B 8.5 m to the side (out of the 12 degree cone and out of A's 8 m jump).
        ushort a = musou.Spawn(MusouEnemyType.Boy, me.Position + forward * 15f, 0f, 1e6f);
        ushort b = musou.Spawn(MusouEnemyType.Boy, me.Position + forward * 15f + side * 8.5f, 0f, 1e6f);
        for (int t = 0; t < 3; t++) w.Step(_ => default);
        uint view = w.Tick;   // the client draws this tick
        // Then they swap places on the server (within the slack, 3 m a tick since the view: 9 m two ticks on): A off the aim, B on it.
        Swap(musou, a, b);
        w.Step(_ => default);
        Assert.NotNull(w.LibrarianHistory);

        var before = (Health(musou, a), Health(musou, b));
        w.Step(_ => Hold(me.Yaw, pitch, view));
        Assert.True(Health(musou, a) < before.Item1, "the Boy he saw on the aim is struck");
        Assert.Equal(before.Item2, Health(musou, b));
        var psyker = w.LibrarianOf(1)!;
        Assert.Equal(1, psyker.LastStruck);
        Assert.True(Vector3.Distance(psyker.Links[0].To, MusouSim.Chest(me.Position + forward * 15f, musou.Body(MusouEnemyType.Boy).Height)) < 0.05f, "the link is drawn where he saw it");

        // A fraction of the way to the next tick: still where the view had them (they stood still between the two).
        before = (Health(musou, a), Health(musou, b));
        w.Step(_ => Hold(me.Yaw, pitch, view, 0.5f));
        Assert.True(Health(musou, a) < before.Item1);
        Assert.Equal(before.Item2, Health(musou, b));

        // Without a view tick (an AI Marine, a test's input) the crowd now: B.
        before = (Health(musou, a), Health(musou, b));
        w.Step(_ => Hold(me.Yaw, pitch, 0));
        Assert.Equal(before.Item1, Health(musou, a));
        Assert.True(Health(musou, b) < before.Item2);

        // A view tick in the future is held to the newest tick kept (the crowd as last sent: B on the aim); one too old to the rewind's reach.
        before = (Health(musou, a), Health(musou, b));
        w.Step(_ => Hold(me.Yaw, pitch, w.Tick + 50));
        Assert.True(Health(musou, b) < before.Item2);
        Assert.Equal(before.Item1, Health(musou, a));
        for (int t = 0; t < w.Config.MaxRewindTicks + 3; t++) w.Step(_ => default);
        before = (Health(musou, a), Health(musou, b));
        w.Step(_ => Hold(me.Yaw, pitch, view));   // the swap is older than the reach now: every tick kept has B on the aim
        Assert.True(Health(musou, b) < before.Item2);
        Assert.Equal(before.Item1, Health(musou, a));
    }

    static float Health(MusouSim sim, ushort id) => sim.TryGet(id, out var e) ? e.Health : -1f;

    static void Swap(MusouSim sim, ushort a, ushort b)
    {
        sim.TryGet(a, out var ea); sim.TryGet(b, out var eb);
        Assert.True(sim.Relocate(a, eb.Position));
        Assert.True(sim.Relocate(b, ea.Position));
    }

    [Fact]
    public void The_restore_switch_rewind_casts_on_the_crowd_now()
    {
        Assert.True(LibrarianVariant.Parse("rewind").NoRewind);
        Assert.True(LibrarianVariant.Parse("all").NoRewind);
        Assert.False(LibrarianVariant.Parse("early").NoRewind);
        Assert.Equal("rewind", LibrarianVariant.Parse("rewind").ToString());
        var (w, me, forward, side, pitch) = Scene(15f, LibrarianVariant.Parse("rewind"));
        var musou = w.Musou!;
        ushort a = musou.Spawn(MusouEnemyType.Boy, me.Position + forward * 15f, 0f, 1e6f);
        ushort b = musou.Spawn(MusouEnemyType.Boy, me.Position + forward * 15f + side * 10f, 0f, 1e6f);
        for (int t = 0; t < 3; t++) w.Step(_ => default);
        uint view = w.Tick;
        Swap(musou, a, b);
        w.Step(_ => default);
        float ha = Health(musou, a), hb = Health(musou, b);
        w.Step(_ => Hold(me.Yaw, pitch, view));
        Assert.True(Health(musou, b) < hb, "the switch: the crowd now");
        Assert.Equal(ha, Health(musou, a));
    }

    [Fact]
    public void An_ork_seen_that_has_died_since_stays_a_link_and_the_chain_goes_on_from_it()
    {
        var (w, me, forward, _, pitch) = Scene(12f);
        var musou = w.Musou!;
        ushort a = musou.Spawn(MusouEnemyType.Boy, me.Position + forward * 12f, 0f, 1e6f);
        ushort b = musou.Spawn(MusouEnemyType.Boy, me.Position + forward * 18f, 0f, 1e6f);   // 6 m behind A: A's jump
        for (int t = 0; t < 3; t++) w.Step(_ => default);
        uint view = w.Tick;
        musou.Damage(a, 2, 1e7f, Vector3.Zero, out bool killed);   // an ally kills A after the client drew it
        Assert.True(killed);
        w.Step(_ => default);
        float hb = Health(musou, b);
        w.Step(_ => Hold(me.Yaw, pitch, view));
        var psyker = w.LibrarianOf(1)!;
        Assert.Equal(2, psyker.Links.Count);   // hand to A, A to B: as drawn
        Assert.Equal(1, psyker.LastStruck);    // only B is alive to take it
        Assert.Equal(LibrarianRules.PrimaryDps * Dt * LibrarianRules.JumpFalloff, hb - Health(musou, b), 2);   // B's share is the second link's
    }

    [Fact]
    public void An_enemy_far_from_where_he_was_seen_takes_nothing()
    {
        // A slot the pool freed and filled again elsewhere carries the same id: beyond the slack (3 m a tick since the view) it is not the enemy seen.
        var (w, me, forward, _, pitch) = Scene(12f);
        var musou = w.Musou!;
        ushort a = musou.Spawn(MusouEnemyType.Boy, me.Position + forward * 12f, 0f, 1e6f);
        for (int t = 0; t < 3; t++) w.Step(_ => default);
        uint view = w.Tick;
        Assert.True(musou.Relocate(a, me.Position - forward * 80f));
        w.Step(_ => default);
        w.Step(_ => Hold(me.Yaw, pitch, view));
        Assert.Equal(1e6f, Health(musou, a));
        Assert.Equal(0, w.LibrarianOf(1)!.LastStruck);
        Assert.Single(w.LibrarianOf(1)!.Links);   // drawn as he saw it: the one link to where A stood
    }

    // ---------------------------------------------------------------- +25 % range and damage

    [Fact]
    public void The_chain_reaches_37_and_a_half_metres_and_its_first_target_takes_112_and_a_half_a_second()
    {
        Assert.Equal(37.5f, LibrarianRules.LightningRange);
        Assert.Equal(112.5f, LibrarianRules.PrimaryDps);
        Assert.Equal(30f * 1.25f, LibrarianRules.LightningRange);
        Assert.Equal(90f * 1.25f, LibrarianRules.PrimaryDps);
        Assert.Equal(8f, LibrarianRules.JumpRadius);   // not the range: our reading
        Assert.Equal(0.85f, LibrarianRules.JumpFalloff);

        foreach (var (distance, reached) in new[] { (35f, true), (37f, true), (39f, false) })
        {
            var sim = new MusouSim(null) { TickRate = Rate };
            ushort boy = sim.Spawn(MusouEnemyType.Boy, new Vector3(0f, distance, 0f), 0f, 1e6f);
            var psyker = new LibrarianPsyker();
            var eye = new Vector3(0f, 0f, 1.6f);
            var chest = MusouSim.Chest(new Vector3(0f, distance, 0f), sim.Body(MusouEnemyType.Boy).Height) - eye;
            float pitch = MathF.Atan2(chest.Z, chest.Y);
            for (int t = 1; t <= Rate; t++) { sim.BeginTick((uint)t); psyker.Step(sim, new LibrarianCaster(1, Vector3.Zero, eye, 0f, pitch), new LibrarianPress(true, false), Dt); }
            sim.TryGet(boy, out var e);
            if (reached) Assert.Equal(1e6f - LibrarianRules.PrimaryDps, e.Health, 1);   // a second of the primary's share
            else Assert.Equal(1e6f, e.Health);
        }
    }

    [Fact]
    public void Casting_on_a_sight_allocates_nothing_per_tick()
    {
        var sim = new MusouSim(null) { TickRate = Rate };
        for (int i = 0; i < 400; i++)
        {
            float angle = i * 2.399963f, distance = 3f + (i % 30);
            sim.Spawn(MusouEnemyType.Boy, new Vector3(MathF.Sin(angle) * distance * (i % 3 == 0 ? 1f : 0.2f), MathF.Abs(MathF.Cos(angle)) * distance + 2f, 0f), 0f, 1e7f);
        }
        var history = new LibrarianHistory(17);
        var sight = new LibrarianSight();
        var psyker = new LibrarianPsyker();
        var caster = new LibrarianCaster(1, Vector3.Zero, new Vector3(0f, 0f, 1.6f), 0f, 0f);
        var hold = new LibrarianPress(true, false);
        uint tick = 0;
        void One()
        {
            sim.BeginTick(++tick);
            Assert.True(history.SightAt(tick - 1, 0.5f, sim, sight, 30f));
            psyker.Step(sim, caster, hold, Dt, sight: sight);
            history.Record(tick, sim);
        }
        history.Record(0, sim);
        sim.BeginTick(++tick); history.Record(tick, sim);
        for (int k = 0; k < 10; k++) One();   // warm
        Assert.True(psyker.Casting && psyker.LastStruck > 1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int k = 0; k < 20; k++) One();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"{allocated} bytes over 20 casting ticks on a sight");
    }
}
