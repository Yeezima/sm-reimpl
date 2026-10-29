using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// Tester reproduction (raptor-drift investigation): the prediction acceptance tests step the client at a steady rate, so every input
/// reaches the server before the tick that applies it. A real client (Godot frame hitch, loaded PC) can stall for more than its input
/// lead; the inputs for the stalled ticks then arrive after the server already stepped them and are discarded
/// (<c>GameServer.OnInputs</c>), the server repeats the last applied input instead, and the client's prediction is corrected.
/// The two DEFECT_ tests fail on the current product (stalls of 300 ms and more): after a stall the client's tick is behind the server's, and
/// <c>GameClient.OnSnapshot</c> replays inputs of ticks the authoritative state already includes (an overshoot of (server tick - client tick) ticks
/// of travel). The other tests bound the unavoidable cost of inputs lost to a stall.
/// </summary>
public class StallPredictionTests(ITestOutputHelper output)
{
    const float Dt = 1f / 30f;

    /// <summary>One loopback client on flat ground that idles, then at tick <paramref name="moveFrom"/> sprints forward (and optionally taps Jump).
    /// The client stops updating for <paramref name="stallMs"/> ms when it reaches tick <paramref name="moveFrom"/> - 1.</summary>
    (float MaxError, int Corrections, PlayerState Server, PlayerState Predicted, GameClient Client) Run(int stallMs, bool jump, double loss, int seed, uint moveFrom = 150, uint stallAfter = 0)
    {
        var config = JumpPackTests.Config with { RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), loss);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        var c = h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = 0 });   // class 0 = assault, has the jump pack
        PlayerInput Input(uint t)
        {
            if (t < moveFrom) return new PlayerInput(t, 0, 0, 0, InputButtons.None);
            var b = InputButtons.Sprint;
            if (jump && t - moveFrom < 8) b |= InputButtons.Jump;
            return new PlayerInput(t, 0, 1, 0, b);
        }
        bool stalling = false, stalled = false;
        TimeSpan stallEnd = default;
        for (int ms = 0; ms < 12000; ms++)
        {
            h.Clock.Advance(TimeSpan.FromMilliseconds(1));
            h.Server.Update();
            if (!stalled && c.Status == ClientStatus.Connected && c.Tick >= moveFrom - 1 + stallAfter && stallMs > 0) { stalling = stalled = true; stallEnd = h.Clock.Now + TimeSpan.FromMilliseconds(stallMs); }
            if (stalling && h.Clock.Now >= stallEnd) stalling = false;
            if (!stalling) c.Update(Input);
        }
        Assert.Equal(ClientStatus.Connected, c.Status);
        Assert.True(c.Tick > moveFrom + 100, $"client reached only tick {c.Tick}");
        var errors = c.CorrectionErrors;
        var server = h.Server.World.Players[c.PlayerId];
        return (errors.Count == 0 ? 0f : errors.Max(), errors.Count(e => e > 1e-4f), server, c.Predicted, c);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    public void A_steady_client_that_starts_sprinting_is_never_corrected(int stallMs, int seed)
    {
        var r = Run(stallMs, jump: false, loss: 0.0, seed);
        output.WriteLine($"no stall: max correction {r.MaxError:F4} m, {r.Corrections} corrections");
        Assert.Equal(0, r.Corrections);
    }

    [Theory]
    [InlineData(33)]
    [InlineData(66)]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(250)]
    public void A_client_stall_while_it_starts_to_sprint_costs_a_bounded_correction(int stallMs)
    {
        var r = Run(stallMs, jump: false, loss: 0.0, seed: 3);
        output.WriteLine($"stall {stallMs} ms: max correction {r.MaxError:F4} m ({r.MaxError / (10f * Dt):F2} ticks of sprint), {r.Corrections} corrections");
        // Residual, by design: inputs that arrive after the server stepped the tick are lost (the server repeats the last one), so a stall that
        // swallows the first sprint ticks costs a fraction of a tick of travel. Bounded, not zero.
        Assert.True(r.MaxError < 0.5f, $"stall {stallMs} ms: max correction {r.MaxError:F4} m");
    }

    [Theory]
    [InlineData(66)]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(250)]
    public void A_client_stall_over_a_jump_pack_take_off_press_costs_a_bounded_correction(int stallMs)
    {
        var r = Run(stallMs, jump: true, loss: 0.0, seed: 4);
        output.WriteLine($"stall {stallMs} ms around the Jump press: max correction {r.MaxError:F4} m, {r.Corrections} corrections");
        Assert.True(r.MaxError < 0.5f, $"stall {stallMs} ms: max correction {r.MaxError:F4} m");   // residual lost-input cost, as above
    }

    [Theory]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(300)]
    [InlineData(500)]
    public void A_client_stall_in_a_steady_sprint_must_not_make_the_prediction_overshoot(int stallMs)
    {
        // Steady full-speed sprint (inputs identical tick to tick, so a repeated input on the server is exactly right).
        var r = Run(stallMs, jump: false, loss: 0.0, seed: 5, moveFrom: 100, stallAfter: 60);
        output.WriteLine($"stall {stallMs} ms mid-sprint: max correction {r.MaxError:F4} m ({r.MaxError / (10f * Dt):F2} ticks of sprint), {r.Corrections} corrections");
        Assert.True(r.MaxError < 1e-3f, $"stall {stallMs} ms: max correction {r.MaxError:F4} m");
    }

    [Theory]
    [InlineData(100)]
    [InlineData(300)]
    [InlineData(500)]
    public void A_client_stall_in_jump_pack_flight_must_not_make_the_prediction_overshoot(int stallMs)
    {
        // Jump held 8 ticks at tick 100 (a ~18 m jump, ~3 s of flight), the client stalls 20 ticks later, in the ascent / hang.
        var r = Run(stallMs, jump: true, loss: 0.0, seed: 6, moveFrom: 100, stallAfter: 20);
        output.WriteLine($"stall {stallMs} ms in flight: max correction {r.MaxError:F4} m, {r.Corrections} corrections");
        Assert.True(r.MaxError < 1e-3f, $"stall {stallMs} ms: max correction {r.MaxError:F4} m");
    }
}
