using System.Numerics;
using SmContent;
using SmFormats.Baf;
using SmNet;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// sim/jump-hang (the owner, 2026-10-31: the medium jump drops out of the air too fast after its peak): the small jump hangs
/// <see cref="JumpPackSpec.SmallHangTime"/> longer at its apex, its gravity easing up from a small fraction of the data's. The tap (dash) and the full
/// jump keep the data's hang. Synthetic install-shaped pack (the M44 dash config with the tiers on). No original bytes.
/// </summary>
public class JumpHangTests
{
    const int Assault = 0;
    const float Dt = 1f / 30f;

    static readonly JumpPackSpec Plain = JumpPackDashTests.Config.JumpPacks[0] with { HoldTiers = true };
    static readonly JumpPackSpec Hang = Plain with { SmallHangTime = 0.35f, SmallHangFloor = 0.05f };
    static readonly SimConfig Off = JumpPackDashTests.Config with { JumpPacks = [Plain] };
    static readonly SimConfig On = JumpPackDashTests.Config with { JumpPacks = [Hang] };

    static PlayerState Fresh(SimConfig c) => Combat.Respawned(new PlayerState(1, default, Vector3.Zero, 0f, true) { Class = Assault }, c);

    static List<PlayerState> Jump(SimConfig c, int held, float my = 0f, Func<int, PlayerState, InputButtons>? extra = null)
    {
        var s = Fresh(c);
        var states = new List<PlayerState>();
        for (int k = 0; k < 400; k++)
        {
            var b = k < held ? InputButtons.Jump : InputButtons.None;
            if (extra is not null) b |= extra(k, s);
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, my, 0f, b, 0f), c, out _, null);
            states.Add(s);
            if (s.Jump is JumpPhase.Landed or JumpPhase.Recover || (k > 6 && s.Jump == JumpPhase.None && s.EvadeTicks == 0 && s.Grounded)) break;
        }
        return states;
    }

    static int HangTicks(List<PlayerState> states) => states.Count(s => s.Jump == JumpPhase.Hang);

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(12)]   // let go on the last tick that still gives the small jump
    public void The_small_jump_hangs_longer_and_drops_less_in_the_first_moments(int held)
    {
        var plain = Jump(Off, held);
        var hang = Jump(On, held);
        int extra = (int)Math.Round(0.35f / Dt);
        Assert.InRange(HangTicks(hang) - HangTicks(plain), extra, extra + 1);   // 0.35 s is 10.5 ticks
        Assert.All(hang.Where(s => s.Jump == JumpPhase.Hang), s => Assert.True(s.JumpSmall));
        Assert.DoesNotContain(hang, s => s.Jump != JumpPhase.Hang && s.JumpSmall);
        // Same apex (the hang starts at the same tick); a third of a second into the hang the drop is a fraction of the data's.
        Assert.Equal(plain.Max(s => s.Position.Z), hang.Max(s => s.Position.Z), 3);
        float apex = hang.Max(s => s.Position.Z);
        int first = hang.FindIndex(s => s.Jump == JumpPhase.Hang);
        float dropPlain = apex - plain[first + 10].Position.Z, dropHang = apex - hang[first + 10].Position.Z;
        Assert.True(dropPlain > 0.6f, $"data's hang drops {dropPlain:F2} m in 0.33 s");
        Assert.True(dropHang < 0.3f, $"the hover drops {dropHang:F2} m in 0.33 s");
        // Eased: the vertical speed never jumps by more than the data's gravity step, and falls monotonically through the hang.
        var vs = hang.Skip(first).TakeWhile(s => s.Jump == JumpPhase.Hang).Select(s => s.Velocity.Z).ToList();
        for (int k = 1; k < vs.Count; k++) Assert.True(vs[k] <= vs[k - 1] + 1e-4f && vs[k - 1] - vs[k] <= Hang.HangGravity * Dt + 1e-4f);
    }

    [Fact]
    public void The_small_jump_released_in_the_ascent_hangs_longer_too()
    {
        var hang = Jump(On, 9);
        Assert.Contains(hang, s => s.JumpCut);
        Assert.True(hang.Count(s => s.Jump == JumpPhase.Hang && s.JumpSmall) >= (int)Math.Round((0.5f + 0.35f) / Dt) - 1);
    }

    [Theory]
    [InlineData(13)]   // Jump down through tick 12: the full jump
    [InlineData(40)]
    public void The_full_jump_is_unchanged(int held)
    {
        var plain = Jump(Off, held);
        var hang = Jump(On, held);
        Assert.Equal(plain.Count, hang.Count);
        for (int k = 0; k < plain.Count; k++) Assert.Equal(plain[k], hang[k]);
        Assert.DoesNotContain(hang, s => s.JumpSmall);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void The_tap_dash_is_unchanged(int held)
    {
        var plain = Jump(Off, held, my: 1f);
        var hang = Jump(On, held, my: 1f);
        Assert.Equal(plain.Count, hang.Count);
        for (int k = 0; k < plain.Count; k++) Assert.Equal(plain[k], hang[k]);
    }

    [Fact]
    public void Air_control_works_in_the_hang()
    {
        var s = Fresh(On);
        int ticks = 0;
        for (int k = 0; k < 200 && !(s.Jump == JumpPhase.Hang && ticks > 5); k++)
        {
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, k < 8 ? InputButtons.Jump : InputButtons.None, 0f), On, out _, null);
            if (s.Jump == JumpPhase.Hang) ticks++;
        }
        Assert.True(s.JumpSmall);
        float before = new Vector2(s.Velocity.X, s.Velocity.Y).Length();
        for (int k = 0; k < 5; k++) s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, InputButtons.None, 0f), On, out _, null);
        Assert.Equal(JumpPhase.Hang, s.Jump);
        Assert.True(new Vector2(s.Velocity.X, s.Velocity.Y).Length() > before + 1f);
    }

    [Fact]
    public void The_ground_pound_starts_from_the_hang()
    {
        bool pounded = false;
        var states = Jump(On, 8, extra: (k, p) =>
        {
            if (pounded || p.Jump != JumpPhase.Hang || !p.JumpSmall) return InputButtons.None;
            pounded = true;
            return InputButtons.Melee;
        });
        Assert.True(pounded);
        int dive = states.FindIndex(s => s.Jump == JumpPhase.Dive);
        Assert.True(dive > 0);
        Assert.False(states[dive].JumpSmall);
        Assert.Equal(JumpPhase.Hang, states[dive - 1].Jump);
    }

    [Fact]
    public void The_wire_carries_the_small_hang_and_the_config_hash_covers_it()
    {
        Assert.Equal(52, Protocol.Version);
        var hang = Jump(On, 8).First(s => s.Jump == JumpPhase.Hang && s.JumpSmall) with { PlayerId = 7 };
        var bytes = Protocol.Write(new Snapshot(1, 1, 1, [hang], Self: 7));
        var back = Assert.IsType<Snapshot>(Protocol.Read(bytes)).Players[0];
        Assert.Equal((JumpPhase.Hang, true, hang.JumpTicks, hang.JumpMark), (back.Jump, back.JumpSmall, back.JumpTicks, back.JumpMark));
        Assert.NotEqual(Off.Hash(), On.Hash());
        Assert.NotEqual(On.Hash(), (On with { JumpPacks = [Hang with { SmallHangTime = 0.4f }] }).Hash());
        Assert.NotEqual(On.Hash(), (On with { JumpPacks = [Hang with { SmallHangFloor = 0.1f }] }).Hash());
    }

    [Fact]
    public void The_reader_sets_the_hang_from_the_tuning()
    {
        var doc = BafDocument.Read(SyntheticBafWriter.JumpPack([(0.1f, 4f), (0.25f, 13f), (0.4f, 18f)], [(0.1f, 35f), (0.25f, 35f), (0.4f, 55f)],
            100f, 10f, 20f, 12f, 0.5f, 30f, 16f, 0.02f, 0.01f));
        var pack = JumpPackTuning.Read("sm_mp_abil_jumppack", doc, [])!;
        Assert.Equal((JumpPackTuning.SmallJumpHangTime, JumpPackTuning.SmallJumpHangFloor), (pack.SmallHangTime, pack.SmallHangFloor));
        Assert.Equal(0.35f, pack.SmallHangTime);
    }
}
