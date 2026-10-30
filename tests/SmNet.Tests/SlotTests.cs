using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// M22: weapon slot keys (1 = pistol, 2 = primary; the user's knowledge of the original). A slot press naming the holstered
/// weapon is a switch request exactly like a Switch press; naming the weapon in hand or an empty slot does nothing.
/// Press edges, precedence, blocking (by equivalence with Switch over random play), protocol 14 and prediction exactness.
/// Synthetic weapons, evade, jump pack and grenade only.
/// </summary>
public class SlotTests(ITestOutputHelper output)
{
    const int Tactical = 0, Assault = 1, Jumper = 2, NoPistol = 3;

    /// <summary>
    /// Tactical: rifle + pistol + knife + evade + frag; Assault: pistol only + jump pack; Jumper: rifle + pistol + jump pack;
    /// NoPistol: rifle only + evade. Weapons: 0 pistol, 1 rifle (EvadeTests).
    /// </summary>
    static readonly SimConfig Config = EvadeTests.Config with
    {
        Thrown = [GrenadeTests.Frag],
        Classes =
        [
            new ClassLoadout("tactical", 1, 0, SimConfig.PlaceholderMeleeSteps) { Evade = 0, EquipmentChoices = [0] },
            new ClassLoadout("assault", -1, 0, SimConfig.PlaceholderMeleeSteps) { JumpPack = 0, EquipmentChoices = [0] },
            new ClassLoadout("jumper", 1, 0, SimConfig.PlaceholderMeleeSteps) { JumpPack = 0, EquipmentChoices = [0] },
            new ClassLoadout("no pistol", 1, -1, SimConfig.PlaceholderMeleeSteps) { Evade = 0 },
        ],
    };

    const InputButtons Slots = InputButtons.SlotPistol | InputButtons.SlotPrimary;

    static PlayerState Fresh(int cls = Tactical, SimConfig? c = null) =>
        Combat.Respawned(Loadouts.Choose(c ?? Config, new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = cls }, -1, -1, -1, 0), c ?? Config);

    static SelfEvents Step(ref PlayerState s, InputButtons b, float mx = 0f, float my = 0f, SimConfig? c = null)
    {
        s = Combat.StepPlayer(s, new PlayerInput(0, mx, my, 0f, b), c ?? Config, out var e);
        return e;
    }

    static PlayerState After(PlayerState s, InputButtons b, out SelfEvents e, float mx = 0f, float my = 0f) =>
        Combat.StepPlayer(s, new PlayerInput(0, mx, my, 0f, b), Config, out e);

    /// <summary>The state without the switch-family held flags (the only thing a slot press and a Switch press leave differently).</summary>
    static PlayerState Unheld(PlayerState s) => s with { SwitchHeld = false, SlotPistolHeld = false, SlotPrimaryHeld = false };

    static InputButtons SlotFor(WeaponSlot slot) => slot == WeaponSlot.Pistol ? InputButtons.SlotPistol : InputButtons.SlotPrimary;

    // ---------------------------------------------------------------- selection

    [Fact]
    public void Slot_1_selects_the_pistol_and_slot_2_the_primary_like_a_switch()
    {
        Assert.Equal((512, 1024), ((int)InputButtons.SlotPistol, (int)InputButtons.SlotPrimary));
        var s = Fresh();
        Step(ref s, InputButtons.Fire);   // one rifle shot
        var viaSwitch = After(s, InputButtons.Switch, out var es);
        Step(ref s, InputButtons.SlotPistol);
        Assert.Equal(WeaponSlot.Pistol, s.Slot);
        Assert.Equal(Config.WeaponSwitchTime, s.SwitchLeft, 5);
        Assert.Equal((10, 50, 29, 270), (s.Clip, s.Reserve, s.OtherClip, s.OtherReserve));
        Assert.True(s.SlotPistolHeld);
        Assert.Equal(Unheld(viaSwitch), Unheld(s));
        Assert.False(es.Fired);

        // The switch runs its time; then 2 brings the rifle back with its own ammunition.
        while (s.SwitchLeft > 0f) Step(ref s, InputButtons.None);
        viaSwitch = After(s, InputButtons.Switch, out _);
        Step(ref s, InputButtons.SlotPrimary);
        Assert.Equal((WeaponSlot.Ranged, 29, 270, 10, 50), (s.Slot, s.Clip, s.Reserve, s.OtherClip, s.OtherReserve));
        Assert.Equal(Unheld(viaSwitch), Unheld(s));
        Assert.True(s.SwitchLeft > 0f);
    }

    [Fact]
    public void Selecting_the_slot_in_hand_does_nothing()
    {
        var s = Fresh();
        Step(ref s, InputButtons.Fire);
        for (int k = 0; k < 40; k++)
        {
            var b = k % 2 == 0 ? InputButtons.SlotPrimary : InputButtons.None;
            var plain = After(s, InputButtons.None, out var ep);
            Step(ref s, b);
            Assert.Equal(WeaponSlot.Ranged, s.Slot);
            Assert.Equal(0f, s.SwitchLeft);
            Assert.Equal(Unheld(plain), Unheld(s));
        }
        // With the pistol in hand, 1 is the no-op.
        var p = Fresh();
        Step(ref p, InputButtons.SlotPistol);
        while (p.SwitchLeft > 0f) Step(ref p, InputButtons.None);
        Assert.Equal(Unheld(After(p, InputButtons.None, out _)), Unheld(After(p, InputButtons.SlotPistol, out _)));
    }

    [Fact]
    public void A_slot_acts_on_a_press_edge_only_and_presses_during_a_switch_are_lost()
    {
        var s = Fresh();
        Step(ref s, InputButtons.SlotPistol);
        // 2 pressed during the switch: ignored (not buffered), like Switch.
        Step(ref s, InputButtons.None);
        Step(ref s, InputButtons.SlotPrimary);
        Assert.Equal(WeaponSlot.Pistol, s.Slot);
        // 2 held through the end of the switch: no switch without a new press.
        for (int k = 0; k < 40; k++) Step(ref s, InputButtons.SlotPrimary);
        Assert.Equal((WeaponSlot.Pistol, 0f), (s.Slot, s.SwitchLeft));
        Step(ref s, InputButtons.None);
        Step(ref s, InputButtons.SlotPrimary);
        Assert.Equal(WeaponSlot.Ranged, s.Slot);
        // Held 1 after a switch back: one press, one switch; the held flag follows the button.
        var t = Fresh();
        Step(ref t, InputButtons.SlotPistol);
        Assert.True(t.SlotPistolHeld);
        Step(ref t, InputButtons.SlotPistol);
        Assert.Equal(WeaponSlot.Pistol, t.Slot);
        Step(ref t, InputButtons.None);
        Assert.False(t.SlotPistolHeld || t.SlotPrimaryHeld);
    }

    [Fact]
    public void Both_slots_in_one_tick_select_the_pistol_and_a_slot_press_overrides_switch()
    {
        // Rifle in hand, 1 and 2 at once: the pistol (1 wins).
        var s = Fresh();
        Step(ref s, Slots);
        Assert.Equal(WeaponSlot.Pistol, s.Slot);
        Assert.True(s.SlotPistolHeld && s.SlotPrimaryHeld);
        // Pistol in hand, 1 and 2 at once: 1 names the weapon in hand, so nothing.
        while (s.SwitchLeft > 0f) Step(ref s, InputButtons.None);
        Step(ref s, InputButtons.None);
        Step(ref s, Slots);
        Assert.Equal((WeaponSlot.Pistol, 0f), (s.Slot, s.SwitchLeft));
        // Switch with the slot in hand: the slot press wins, nothing; Switch with the other slot: one switch.
        var t = Fresh();
        Step(ref t, InputButtons.Switch | InputButtons.SlotPrimary);
        Assert.Equal((WeaponSlot.Ranged, 0f), (t.Slot, t.SwitchLeft));
        var u = Fresh();
        Step(ref u, InputButtons.Switch | InputButtons.SlotPistol);
        Assert.Equal(WeaponSlot.Pistol, u.Slot);
        Assert.Equal(Unheld(After(Fresh(), InputButtons.Switch, out _)), Unheld(u));
        // A held Switch does not stop a slot press (separate edges).
        var v = Fresh();
        Step(ref v, InputButtons.Switch);
        while (v.SwitchLeft > 0f) Step(ref v, InputButtons.Switch);
        Step(ref v, InputButtons.Switch | InputButtons.SlotPrimary);
        Assert.Equal(WeaponSlot.Ranged, v.Slot);
    }

    [Fact]
    public void An_empty_slot_is_ignored()
    {
        // No pistol: 1 does nothing, 2 (in hand) does nothing.
        var n = Fresh(NoPistol);
        Assert.Equal(WeaponSlot.Ranged, n.Slot);
        foreach (var b in new[] { InputButtons.SlotPistol, InputButtons.None, InputButtons.SlotPrimary, InputButtons.None, Slots })
        {
            var plain = After(n, InputButtons.None, out _);
            Step(ref n, b);
            Assert.Equal((WeaponSlot.Ranged, 0f), (n.Slot, n.SwitchLeft));
            Assert.Equal(Unheld(plain), Unheld(n));
        }
        // No primary (pistol in hand): 2 does nothing.
        var a = Fresh(Assault);
        Assert.Equal(WeaponSlot.Pistol, a.Slot);
        Step(ref a, InputButtons.SlotPrimary);
        Assert.Equal((WeaponSlot.Pistol, 0f), (a.Slot, a.SwitchLeft));
        // An empty slot does not end an evade at its switch branch either (a Switch press there does, as before).
        var e = Fresh(NoPistol);
        Step(ref e, InputButtons.Jump);
        for (int k = 2; k <= 21; k++) Step(ref e, InputButtons.None);   // 21 ticks = 0.7 s = SwitchAt
        Assert.True(Evade.Evading(After(e, InputButtons.SlotPistol, out _)));
        Assert.False(Evade.Evading(After(e, InputButtons.Switch, out _)));
    }

    // ---------------------------------------------------------------- blocking

    [Fact]
    public void Slot_presses_are_lost_during_an_evade_before_its_switch_branch_and_end_it_after()
    {
        var s = Fresh();
        Step(ref s, InputButtons.Jump);
        for (int n = 2; n <= 21; n++)
        {
            Step(ref s, n % 2 == 0 ? InputButtons.SlotPistol : InputButtons.None);
            Assert.Equal((n, WeaponSlot.Ranged), (s.EvadeTicks, s.Slot));
        }
        // 0.7 s played: a press of 2 (in hand) still does nothing; a press of 1 ends the roll and switches.
        Assert.True(Evade.Evading(After(s, InputButtons.SlotPrimary, out _)));
        var viaSwitch = After(s, InputButtons.Switch, out _);
        Step(ref s, InputButtons.SlotPistol);
        Assert.Equal((0, WeaponSlot.Pistol), (s.EvadeTicks, s.Slot));
        Assert.Equal(Unheld(viaSwitch), Unheld(s));
    }

    [Fact]
    public void Slot_presses_are_ignored_during_melee_and_throws()
    {
        var m = Fresh();
        Step(ref m, InputButtons.Melee);
        Assert.Equal(1, m.MeleeStep);
        Step(ref m, InputButtons.None);
        Step(ref m, InputButtons.SlotPistol);
        while (m.MeleeStep > 0) Step(ref m, InputButtons.None);
        Assert.Equal(WeaponSlot.Ranged, m.Slot);

        var t = Fresh();
        Step(ref t, InputButtons.Grenade);
        Assert.True(t.Throwing);
        Step(ref t, InputButtons.None);
        Step(ref t, InputButtons.SlotPistol);
        while (t.Throwing) Step(ref t, InputButtons.None);
        Assert.Equal(WeaponSlot.Ranged, t.Slot);
    }

    /// <summary>
    /// The rule behind "blocked exactly when Switch is blocked": over random play (fire, alt fire, reload, melee, grenade, aim,
    /// jump: evades and jump-pack jumps and pounds), at every tick a press of the slot not in hand gives the same state and
    /// events as a Switch press, and a press of the slot in hand the same as no press (held flags aside).
    /// </summary>
    [Theory]
    [InlineData(Tactical, 1)]
    [InlineData(Tactical, 2)]
    [InlineData(Jumper, 3)]
    [InlineData(Jumper, 4)]
    [InlineData(Assault, 5)]
    [InlineData(NoPistol, 6)]
    public void A_slot_press_acts_exactly_like_a_switch_press_over_random_play(int cls, int seed)
    {
        var rng = new Random(seed);
        var s = Fresh(cls);
        InputButtons[] pool = [InputButtons.Fire, InputButtons.Reload, InputButtons.Melee, InputButtons.Grenade, InputButtons.Aim, InputButtons.Jump, InputButtons.Sprint];
        int switched = 0, blocked = 0, blockedEvading = 0, blockedBusy = 0;
        for (int t = 0; t < 4000; t++)
        {
            var b = InputButtons.None;
            foreach (var x in pool) if (rng.NextDouble() < (x is InputButtons.Fire or InputButtons.Sprint ? 0.4 : 0.06)) b |= x;
            float mx = (float)(rng.NextDouble() * 2 - 1), my = (float)(rng.NextDouble() * 2 - 1);
            // Every 30 ticks an occasional switch keeps both slots in play.
            bool doSwitch = t % 30 == 0;

            var viaSwitch = After(s, b | InputButtons.Switch, out var es, mx, my);
            var viaOther = After(s, b | SlotFor(Loadouts.Other(s.Slot)), out var eo, mx, my);
            var none = After(s, b, out var en, mx, my);
            var viaSame = After(s, b | SlotFor(s.Slot), out var ec, mx, my);
            bool hasOther = Loadouts.Holstered(Config, s) is not null;
            if (hasOther)
            {
                Assert.Equal(Unheld(viaSwitch), Unheld(viaOther));
                Assert.Equal(es, eo);
                if (viaSwitch.Slot != s.Slot) switched++;
                else
                {
                    blocked++;
                    if (Evade.Evading(s)) blockedEvading++;
                    if (s.MeleeStep > 0 || s.Throwing || s.SwitchLeft > 0f || s.Jump != JumpPhase.None) blockedBusy++;
                }
            }
            else
            {
                Assert.Equal(Unheld(none), Unheld(viaOther));   // the empty slot: nothing
                Assert.Equal(en, eo);
            }
            Assert.Equal(Unheld(none), Unheld(viaSame));
            Assert.Equal(en, ec);

            s = Unheld(doSwitch ? viaSwitch : none);   // released before the next tick, so its presses are edges
            if (!s.Alive) s = Combat.Respawned(s, Config);
        }
        output.WriteLine($"class {cls}: {switched} switches, {blocked} blocked ({blockedEvading} evading, {blockedBusy} melee/throw/switch/jump)");
        if (Loadouts.Holstered(Config, Fresh(cls)) is not null)
        {
            Assert.True(switched > 50 && blocked > 50 && blockedBusy > 20, $"too little variety: {switched} switched, {blocked} blocked");
            if (cls == Tactical) Assert.True(blockedEvading > 5, $"only {blockedEvading} blocked while evading");
        }
    }

    // ---------------------------------------------------------------- protocol

    [Fact]
    public void Protocol_14_carries_the_slot_buttons_and_their_held_flags()
    {
        Assert.Equal(48, Protocol.Version);
        var input = new PlayerInput(9, 0f, 1f, 0.5f, InputButtons.SlotPistol | InputButtons.SlotPrimary | InputButtons.Switch);
        Assert.Equal(input, Protocol.Quantize(input));
        Assert.Equal(input, Assert.IsType<InputBatch>(Protocol.Read(Protocol.Write(new InputBatch([input], 1, 2)))).Inputs[0]);
        Assert.Equal(InputButtons.Fire, Protocol.Quantize(input with { Buttons = InputButtons.Fire | (InputButtons)0x8000 }).Buttons);   // bits 14..15 unknown (11 is MeleeHeavy since protocol 17, 12 Interact since 24, 13 added content's ExtraAction since extras wire 2)

        foreach (var (pistolHeld, primaryHeld, grenadeHeld) in new[] { (true, false, false), (false, true, true), (true, true, true), (false, false, false) })
        {
            var me = new PlayerState(3, Vector3.One, Vector3.Zero, 0f, true)
                { Alive = true, SlotPistolHeld = pistolHeld, SlotPrimaryHeld = primaryHeld, GrenadeHeld = grenadeHeld, Grenades = 1, Equipment = 0 };
            var own = Protocol.Write(new Snapshot(1, 1, 1, [me], Self: 3));
            Assert.Equal(me, Assert.IsType<Snapshot>(Protocol.Read(own)).Players[0]);
            Assert.Equal((grenadeHeld ? 1 : 0) | (pistolHeld ? 2 : 0) | (primaryHeld ? 4 : 0), own[^35]);   // the grenade flag byte (protocol 21: one more ready-tick byte follows)
            own[^35] |= 0x38;
            Assert.Equal(BufferedAction.JumpLong, Assert.IsType<Snapshot>(Protocol.Read(own)).Players[0].Buffered);   // protocol 33: bits 3..5 are the buffered action; 7 the long Jump press since 38
            // Slim records do not carry them.
            Assert.False(Protocol.Slim(me) is { SlotPistolHeld: true } or { SlotPrimaryHeld: true });
        }
    }

    // ---------------------------------------------------------------- prediction

    [Theory]
    [InlineData(61)]
    [InlineData(62)]
    public void Prediction_matches_the_server_with_slot_presses_under_150ms_rtt_and_5pct_loss(int seed)
    {
        var config = Config with
        {
            Weapons = [.. Config.Weapons.Select(w => w with { Damage = 0f, ReserveSize = 400 })],
            RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = Tactical });
        h.AddClient(nonce: 101, options: new ClientOptions { RequestedClass = Jumper });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = new[] { new Dictionary<uint, PlayerState>(), new Dictionary<uint, PlayerState>() };
        PlayerInput Input(int i, uint t)
        {
            var move = Harness.Scripted(i, t);
            var buttons = move.Buttons;
            uint phase = (t + (uint)i * 7) % 120;
            if (phase is 0 or 1) buttons |= InputButtons.SlotPistol;                      // held 2 ticks: one switch
            if (phase is 30) buttons |= InputButtons.SlotPistol;                           // the slot in hand: nothing
            if (phase is 40 or 41 or 42) buttons |= InputButtons.SlotPrimary;
            if (phase is 50) buttons |= InputButtons.SlotPistol;                           // during the switch: lost
            if (phase is 70) buttons |= InputButtons.SlotPistol | InputButtons.SlotPrimary; // both: the pistol
            if (phase is 95) buttons |= InputButtons.Switch | InputButtons.SlotPrimary;    // slot overrides the toggle
            if (phase is (>= 10 and < 30) or (>= 100 and < 115) && phase % 3 == 0) buttons |= InputButtons.Fire;
            if (phase is 60 or 61) buttons |= InputButtons.Melee;
            if (phase is 85) buttons |= InputButtons.Reload;
            return move with { Buttons = buttons, Pitch = 1.3f };
        }
        h.Run(TimeSpan.FromSeconds(20), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < 2; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });

        for (int i = 0; i < 2; i++)
        {
            var c = h.Clients[i];
            int id = c.PlayerId, compared = 0;
            var mismatches = new List<string>();
            uint from = truth.Keys.First() + 90;
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
                compared++;
                if (pred != real) mismatches.Add($"tick {tick}: predicted {pred}; server {real}");
            }
            var mine = truth.Values.Where(w => w.ContainsKey(id)).Select(w => w[id]).ToList();
            var pairs = mine.Zip(mine.Skip(1)).ToList();
            int toPistol = pairs.Count(p => p.First.Slot == WeaponSlot.Ranged && p.Second.Slot == WeaponSlot.Pistol);
            int toPrimary = pairs.Count(p => p.First.Slot == WeaponSlot.Pistol && p.Second.Slot == WeaponSlot.Ranged);
            output.WriteLine($"client {i}: compared {compared} ticks, {mismatches.Count} mismatches; {toPistol} to the pistol, {toPrimary} to the primary");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300, $"client {i}: only {compared} ticks compared");
            Assert.True(toPistol >= 4 && toPrimary >= 4, $"client {i}: too few slot switches");
            Assert.Empty(mismatches);
        }
    }
}
