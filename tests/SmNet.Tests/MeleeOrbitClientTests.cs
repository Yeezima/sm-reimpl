using System.Numerics;
using SmClient.Customizer.Model;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// camera/melee-orbit: the client's melee-mode input rule (<see cref="MeleeOrbitClient.Apply"/>, the code the live client runs) stepped against
/// <see cref="Combat.StepPlayer"/>, with the locked camera (orbit off) as the reference. The review findings of 76de1bb0: S cancels a charge, a queued strike keeps its
/// heading, held fire stays on the crosshair, a buffered dash keeps its direction, the slam follows the mouse. Synthetic data only.
/// </summary>
public class MeleeOrbitClientTests(ITestOutputHelper output)
{
    const float Deg = MathF.PI / 180f;
    static readonly SimConfig C = Armed(RushTests.Config);

    /// <summary>The config with every class carrying a chainsword (a melee weapon frees the camera); its steps are the class's own.</summary>
    static SimConfig Armed(SimConfig c) => c with { Classes = [.. c.Classes.Select(k => k with { MeleeChoices = [new MeleeCombo("mp_melee_chainsword", k.Melee) { Rush = k.MeleeRush }] })] };

    sealed class Client(bool orbit, SimConfig cfg)
    {
        readonly MeleeOrbitClient _orbit = new();
        public float Cam;
        public bool Active => _orbit.Mode.Active;
        public PlayerInput Sample(PlayerState me, uint tick, float x, float y, InputButtons b) =>
            _orbit.Apply(me, new PlayerInput(tick, x, y, Cam, b, 0f), Cam, cfg, orbit, me.Alive, false, out _).WithShotDirection(Combat.AimDirection(Cam, 0f));
    }

    static PlayerState Fresh(SimConfig c) => Combat.Respawned(Loadouts.Choose(c, new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, -1, -1, -1, 0), c);

    /// <summary>Strikes once and waits for the strike to end; melee mode is then on (the class's melee frees the camera).</summary>
    static (PlayerState, uint) StrikeAndSettle(Client client, PlayerState s, SimConfig c, int ticks = 60)
    {
        uint t = 1;
        s = Combat.StepPlayer(s, client.Sample(s, t++, 0f, 0f, InputButtons.Melee), c, out _);
        for (int i = 0; i < ticks; i++) s = Combat.StepPlayer(s, client.Sample(s, t++, 0f, 0f, InputButtons.None), c, out _);
        return (s, t);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Back_away_from_the_body_still_cancels_a_charge_in_melee_mode(bool orbit)
    {
        var client = new Client(orbit, C);
        var (s, t) = StrikeAndSettle(client, Fresh(C), C);
        Assert.Equal(0, s.MeleeStep);
        Assert.True(client.Active);
        s = Combat.StepPlayer(s, client.Sample(s, t++, 0f, 1f, InputButtons.Sprint | InputButtons.MeleeHeavy), C, out _);
        Assert.Equal(RushPhase.Rush, s.Rush);
        float body = s.Yaw;
        int rushTicks = 1;
        for (int i = 0; i < 60 && s.Rush == RushPhase.Rush; i++)
        {
            bool back = s.RushTicks >= 20;
            // Orbit: the camera has swung 90 degrees left of the body, so A (camera-relative) points straight back from the body; locked: S.
            if (orbit) client.Cam = body + 90f * Deg;
            float x = back && orbit ? -1f : 0f, y = back && !orbit ? -1f : 0f;
            s = Combat.StepPlayer(s, client.Sample(s, t++, x, y, InputButtons.None), C, out _);
            if (s.Rush == RushPhase.Rush) rushTicks++;
        }
        output.WriteLine($"orbit {orbit}: rush ticks {rushTicks} (back held from rush tick 20)");
        Assert.InRange(rushTicks, 20, 21);
    }

    [Fact]
    public void A_side_key_relative_to_the_camera_does_not_cancel_a_charge()
    {
        var client = new Client(true, C);
        var (s, t) = StrikeAndSettle(client, Fresh(C), C);
        s = Combat.StepPlayer(s, client.Sample(s, t++, 0f, 1f, InputButtons.Sprint | InputButtons.MeleeHeavy), C, out _);
        float body = s.Yaw;
        int rushTicks = 1;
        for (int i = 0; i < 60 && s.Rush == RushPhase.Rush; i++)
        {
            client.Cam = body + 90f * Deg;   // W relative to this camera is the body's left: no cancel
            s = Combat.StepPlayer(s, client.Sample(s, t++, 0f, s.RushTicks >= 20 ? 1f : 0f, InputButtons.None), C, out _);
            if (s.Rush == RushPhase.Rush) rushTicks++;
        }
        Assert.True(rushTicks > 21, $"the charge ran {rushTicks} ticks");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Fire_held_through_a_strike_stays_on_the_crosshair(bool orbit)
    {
        var s = Fresh(C);
        var client = new Client(orbit, C);
        float worst = 0f;
        for (uint t = 1; t <= 90; t++)
        {
            var b = InputButtons.None;
            if (t >= 5) b |= InputButtons.Fire;
            if (t is 20 or 21) b |= InputButtons.Melee;
            if (t > 21 && t <= 36) client.Cam += 10f * Deg;   // the mouse turns 150 degrees during the swing
            var input = client.Sample(s, t, 0f, 0f, b);
            s = Combat.StepPlayer(s, input, C, out var e);
            if (e.Fired && t > 21)
            {
                var (yaw, _) = Combat.ShotAim(input, C, s);
                worst = MathF.Max(worst, MathF.Abs(MeleeOrbitSteer.Wrap(yaw - client.Cam)) / Deg);
            }
        }
        output.WriteLine($"orbit {orbit}: worst shot {worst:F1} degrees off the crosshair");
        Assert.InRange(worst, 0f, 5f);
    }

    [Fact]
    public void A_queued_combo_strike_goes_the_way_its_press_looked()
    {
        float dur = 1f, hit = 0.2f;
        var variant = new MeleeVariant(0f, Enumerable.Range(0, 61).Select(i => { float tt = dur * i / 60f; return 2f * Math.Clamp(tt / hit, 0f, 1f); }).ToArray());
        var swing = C.Classes[0].Melee[0] with { Reach = new MeleeReach(0f, 0.1f, 0f, hit, 3.5f, 1f, [variant]) };
        var chain = Armed(C with { Classes = [.. C.Classes.Select(k => k with { Melee = [swing, swing] })] });
        var s = Fresh(chain);
        var client = new Client(true, chain);
        uint t = 1;
        s = Combat.StepPlayer(s, client.Sample(s, t++, 0f, 0f, InputButtons.Melee), chain, out _);
        s = Combat.StepPlayer(s, client.Sample(s, t++, 0f, 0f, InputButtons.None), chain, out _);
        Assert.True(client.Active);
        for (int i = 0; i < 6; i++) { client.Cam += 20f * Deg; s = Combat.StepPlayer(s, client.Sample(s, t++, 0f, 0f, InputButtons.None), chain, out _); }
        float pressCam = client.Cam;
        Assert.Equal(1, s.MeleeStep);
        s = Combat.StepPlayer(s, client.Sample(s, t++, 0f, 0f, InputButtons.Melee), chain, out _);   // queued, no key held
        float atStart = float.NaN;
        for (int i = 0; i < 40 && float.IsNaN(atStart); i++)
        {
            client.Cam += 5f * Deg;   // the camera keeps turning while the strike waits
            int before = s.MeleeStep;
            s = Combat.StepPlayer(s, client.Sample(s, t++, 0f, 0f, InputButtons.None), chain, out _);
            if (before == 1 && s.MeleeStep == 2) atStart = s.Yaw;
        }
        Assert.False(float.IsNaN(atStart));
        output.WriteLine($"strike 2 starts at {atStart / Deg:F1}, the press looked at {pressCam / Deg:F1}");
        Assert.InRange(MathF.Abs(MeleeOrbitSteer.Wrap(atStart - pressCam)) / Deg, 0f, 2f);
    }

    [Fact]
    public void A_buffered_dash_goes_the_way_of_the_keys_as_with_the_locked_camera()
    {
        var k = Armed(JumpPackDashTests.Config with { InputBufferWindow = 0.3f });
        int orbitSeconds = 0;
        foreach (int tapAt in new[] { 14, 16, 18, 20, 22, 24, 26, 30 })
            foreach (bool orbit in new[] { false, true })
            {
                var client = new Client(orbit, k);
                var (s, t) = StrikeAndSettle(client, Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, k), k, 70);
                Assert.True(client.Active);
                for (int i = 0; i < 15; i++) s = Combat.StepPlayer(s, client.Sample(s, t++, 1f, 0f, InputButtons.None), k, out _);   // D held: to the camera's right (+X at yaw 0)
                int dashes = 0, sinceFirst = -1; bool wasEvading = false; Vector3 from = default; var moves = new List<Vector3>();
                for (int i = 0; i < 160; i++)
                {
                    var b = InputButtons.None;
                    if (i is 0 or 1) b |= InputButtons.Jump;
                    if (sinceFirst >= tapAt && sinceFirst <= tapAt + 1) b |= InputButtons.Jump;   // a second tap, buffered when the first dash still holds the player
                    s = Combat.StepPlayer(s, client.Sample(s, t++, 1f, 0f, b), k, out _);
                    bool evading = s.EvadeTicks > 0 && !s.EvadeLanded;
                    if (evading && !wasEvading) { dashes++; from = s.Position; if (dashes == 1) sinceFirst = 0; }
                    if (!evading && wasEvading) moves.Add(s.Position - from);
                    wasEvading = evading;
                    if (sinceFirst >= 0) sinceFirst++;
                }
                output.WriteLine($"orbit {orbit} tap {tapAt}: {string.Join("; ", moves.Select(m => $"x {m.X:F1} y {m.Y:F1}"))}");
                if (orbit && moves.Count >= 2) orbitSeconds++;
                foreach (var m in moves) Assert.True(m.X > 1f && MathF.Abs(m.Y) < 0.5f * m.X, $"orbit {orbit} tap {tapAt}: a dash moved x {m.X:F1} y {m.Y:F1}");
            }
        Assert.True(orbitSeconds > 0, "no second (buffered) dash happened in melee mode");
    }

    [Fact]
    public void A_steered_slam_follows_the_mouse()
    {
        var slam = C.Classes[0].Melee[0] with { SteerFrom = 0f, SteerTo = 1f, SteerRate = 300f };
        var cfg = Armed(C with { Classes = [.. C.Classes.Select(k => k with { Melee = [slam] })] });
        var s = Fresh(cfg);
        var client = new Client(true, cfg);
        uint t = 1;
        s = Combat.StepPlayer(s, client.Sample(s, t++, 0f, 0f, InputButtons.Melee), cfg, out _);
        Assert.True(MeleeOrbitClient.SteeredStrike(cfg, s));
        client.Cam = 60f * Deg;
        var sent = client.Sample(s, t, 0f, 0f, InputButtons.None);
        Assert.Equal(client.Cam, sent.Yaw, 4);   // the camera's yaw: the slam turns toward the view
    }

    [Fact]
    public void Running_where_the_keys_point_matches_on_the_server_and_in_the_prediction()
    {
        var client = new Client(true, C);
        var (server, t) = StrikeAndSettle(client, Fresh(C), C);
        Assert.True(client.Active);
        var predicted = server;
        for (int i = 0; i < 120; i++)
        {
            client.Cam += 90f * Deg * C.TickSeconds;
            var input = client.Sample(predicted, t++, i >= 60 ? 1f : 0f, i >= 60 ? 0f : 1f, InputButtons.None);
            server = Combat.StepPlayer(server, input, C, out _);
            predicted = Combat.StepPlayer(predicted, input, C, out _);
            Assert.Equal(server.Position, predicted.Position);
            Assert.Equal(server.Yaw, predicted.Yaw);
        }
        // D held for 2 s: the body faces the camera's right and runs that way.
        Assert.InRange(MathF.Abs(MeleeOrbitSteer.Wrap(server.Yaw - (client.Cam - MathF.PI / 2f))) / Deg, 0f, 4f);
        var v = new Vector2(server.Velocity.X, server.Velocity.Y);
        Assert.InRange(MathF.Abs(MeleeOrbitSteer.Wrap(MathF.Atan2(-v.X, v.Y) - server.Yaw)) / Deg, 0f, 10f);
    }

    [Fact]
    public void The_body_frame_stick_keeps_the_world_direction()
    {
        var (x, y) = MeleeOrbitClient.BodyFrameStick(cameraYaw: MathF.PI / 2f, bodyYaw: 0f, moveX: -1f, moveY: 0f);   // A with the camera 90 degrees left: straight back
        Assert.Equal(0f, x, 4);
        Assert.Equal(-1f, y, 4);
        (x, y) = MeleeOrbitClient.BodyFrameStick(0f, 0f, 0.6f, 0.8f);
        Assert.Equal((0.6f, 0.8f), (MathF.Round(x, 4), MathF.Round(y, 4)));
    }
}
