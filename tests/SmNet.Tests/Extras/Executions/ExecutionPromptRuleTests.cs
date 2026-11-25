using System.Numerics;
using SmClient;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Executions;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Executions;

/// <summary>
/// Executions X6: the HUD prompt's rules (client/Extras/Executions/ExecutionPromptRules.cs) and the camera's maths (ExecutionCameraMath.cs), engine-free: the 10 Hz throttle, the cheap refusals before
/// any choice, the cooldown, Chaos runs before X4b, the unbound hint, and the prompt's Ork being the press's.
/// </summary>
public class ExecutionPromptRuleTests
{
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);

    static SimConfig Config(string melee = "sm_mp_melee_chainsword") => new()
    {
        Classes = [new ClassLoadout("c", 0, -1, [Light]) { Side = Team.SpaceMarines, Role = "assault", MeleeChoices = [new MeleeCombo(melee, [Light])] }],
        Extras = ContentExtras.Executions | ContentExtras.Warfront,
    };

    static PlayerState Me(Vector3 at = default) => new(1, at, Vector3.Zero, 0f, true) { Alive = true, Team = Team.SpaceMarines, Class = 0 };

    static IReadOnlyList<ExecutionRow> Rows => [ExecutionTestKit.Row("O1.chainsword", ExecutionTargets.OrkBoy, gap: 1.3f)];

    static MusouShown Shown(ushort id, Vector3 at, MusouEnemyType type = MusouEnemyType.Boy, MusouEnemyState state = MusouEnemyState.Seek) => new(id, at, 0f, type, state, 0f);

    [Fact]
    public void ThrottleTenHz()
    {
        var throttle = new PromptThrottle();
        Assert.True(throttle.Due(0.0));
        Assert.False(throttle.Due(0.05));
        Assert.False(throttle.Due(0.099));
        Assert.True(throttle.Due(0.1));
        // 144 frames a second for a second: 10 recomputations (the first at 0 aside).
        var t = new PromptThrottle();
        int due = 0;
        for (int f = 1; f <= 144; f++) if (t.Due(f / 144.0)) due++;
        Assert.InRange(due, 9, 10);
        t.Reset();
        Assert.True(t.Due(1.0001));
    }

    [Fact]
    public void EarlyOutsSkipChoice()
    {
        var c = Config();
        int choices = 0;
        var near = new List<MusouEnemy>();
        var by = new List<Vector3>();
        // The view's flow: the cheap refusals first, the choice only when they all pass.
        void Frame(PlayerState me, bool inBattle, uint? lastDone, uint tick, IReadOnlyList<MusouShown> shown)
        {
            if (ExecutionPromptRules.Common(c, me, false, false, Rows) != ExecutionPromptHidden.None) return;
            if (ExecutionPromptRules.Warfront(c, me, inBattle, false, lastDone, tick, shown) != ExecutionPromptHidden.None) return;
            ExecutionPromptRules.Gather(me, shown, near);
            ExecutionPromptRules.WarfrontPick(me, 0f, c, Rows, null, near, by, null, ref choices);
        }
        var boy = Shown(5, new Vector3(0f, 1.3f, 0f));
        Frame(Me() with { Alive = false }, true, null, 100, [boy]);
        Frame(Me() with { Grounded = false }, true, null, 100, [boy]);
        Frame(Me() with { Rush = RushPhase.Rush }, true, null, 100, [boy]);
        Frame(Me() with { Reaction = ReactionKind.Stagger }, true, null, 100, [boy]);   // X3 refuses a press in an interrupt, stun or stagger
        Frame(Me(), false, null, 100, [boy]);                                 // the hub or the travel
        Frame(Me(), true, 90, 100, [boy]);                                    // on cooldown
        Frame(Me(), true, null, 100, [Shown(5, new Vector3(0f, 3.2f, 0f))]); // nobody within 3 m
        Frame(Me(), true, null, 100, [Shown(5, new Vector3(0f, 1.3f, 0f), state: MusouEnemyState.Dying)]);
        Assert.Equal(0, choices);
        Assert.Equal(ExecutionPromptHidden.NoFamily, ExecutionPromptRules.Common(Config("sm_mp_melee_kick"), Me(), false, false, Rows));
        Assert.Equal(ExecutionPromptHidden.NoRows, ExecutionPromptRules.Common(c, Me(), false, false, []));
        Assert.Equal(ExecutionPromptHidden.Executing, ExecutionPromptRules.Common(c, Me(), false, true, Rows));
        Assert.Equal(ExecutionPromptHidden.Puppet, ExecutionPromptRules.Common(c, Me() with { PlayerId = SmSim.Extras.Musou.Campaign.TfwContracts.PuppetIdFirst }, false, false, Rows));
        // An empty gather never calls the choice either.
        ExecutionPromptRules.WarfrontPick(Me(), 0f, c, Rows, null, [], by, null, ref choices);
        Assert.Equal(0, choices);
        // All passed: one choice.
        Frame(Me(), true, null, 100, [boy]);
        Assert.Equal(1, choices);
    }

    [Fact]
    public void CooldownHidesPrompt()
    {
        var c = Config();
        uint done = 1000, cooldown = (uint)ExecutionRules.CooldownTicks(c);
        Assert.Equal(300u, cooldown);   // 10 s at 30 Hz
        MusouShown[] boy = [Shown(5, new Vector3(0f, 1.3f, 0f))];
        Assert.Equal(ExecutionPromptHidden.Cooldown, ExecutionPromptRules.Warfront(c, Me(), true, false, done, done + cooldown - 3, boy));   // 9.9 s
        Assert.Equal(ExecutionPromptHidden.None, ExecutionPromptRules.Warfront(c, Me(), true, false, done, done + cooldown, boy));          // 10.0 s
        Assert.Equal(ExecutionPromptHidden.None, ExecutionPromptRules.Warfront(c, Me(), true, false, null, 5, boy));                       // never executed
    }

    [Fact]
    public void ChaosHidesBeforeX4b()
    {
        Assert.False(ExecutionPromptRules.ChaosKindsIn);
        Assert.Equal(ExecutionPromptHidden.ChaosRun, ExecutionPromptRules.Warfront(Config(), Me(), true, true, null, 5, [Shown(5, new Vector3(0f, 1.3f, 0f))]));
    }

    [Fact]
    public void OvershieldCoversArmourNotClothLeatherOrnamentsOrWeapons()
    {
        // The install's names (survey 2026-11-25): armour pieces, worn items and generic composite materials are covered.
        foreach (var name in new[] { "csm_armour_mp_raptor_01_legr", "csm_armour_mp_basic_torso", "csm_armour_mp_basic_shldrl", "sm_mk7aquila_helmet", "Worn_csm_jumpack_mp_basic_RIG_SpineD",
                     "Standard_2", "raptor_helmet_realtime_composite", "sm_ironhalo_backpack", "", null })
            Assert.False(OvershieldCoverage.Excluded(name), name);
        // Cloth, leather and ornaments by name are not.
        foreach (var name in new[] { "bt_tabard", "csm_loincloth_01", "sm_purity_seal_l", "csm_chains_torso", "leather_pouch_r", "Belt_Pouches", "sm_robe_lower", "Scroll_01" })
            Assert.True(OvershieldCoverage.Excluded(name), name);
        // The armour-pieces paint mask's cloth list (BakedArmourPaint.ClothWords on claude/armour-pieces-1125): every word is left out here too, so both features agree.
        foreach (var word in new[] { "cloth", "cape", "cloak", "tabard", "fur", "scroll", "parchment", "purity", "robe", "pelt" })
            Assert.Contains(word, OvershieldCoverage.ExcludedWords);
        Assert.True(OvershieldCoverage.Weapon("Weapon_melee_sm_mp_melee_chainsword"));
        Assert.False(OvershieldCoverage.Weapon("Worn_sm_jumppack"));
    }

    /// <summary>X6 re-review 7: the client's bystander reach is the server's <c>World.ExecutionsBystanderReach</c>.</summary>
    [Fact]
    public void BystanderReachIsTheServers()
    {
        var c = Config();
        var w = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f)]);
        Assert.Equal(w.ExecutionsBystanderReach, ExecutionPromptRules.BystanderReach(c));
    }

    /// <summary>
    /// X6 re-review 1 and 4: the press counter is synced every frame; only a new press on a frame after one that showed the Classic dot spends the offer. A press while the dot was hidden
    /// (refused before the offer on the server: rolling, staggered, airborne, no offer, during a move, before the window), a held key, or a counter reset (a new prediction) spends nothing.
    /// </summary>
    [Fact]
    public void OnlyANewPressAfterAShownDotSpendsTheOffer()
    {
        int seen = 0;
        Assert.False(ExecutionPromptRules.PressSpendsOffer(1, ref seen, shownLastFrame: false));   // pressed while rolling: no dot
        Assert.Equal(1, seen);
        Assert.False(ExecutionPromptRules.PressSpendsOffer(1, ref seen, shownLastFrame: true));    // the dot appears later: the old press is not new
        Assert.True(ExecutionPromptRules.PressSpendsOffer(2, ref seen, shownLastFrame: true));     // a press while it showed
        Assert.False(ExecutionPromptRules.PressSpendsOffer(2, ref seen, shownLastFrame: true));
        seen = 0;                                                                                    // reconnect: the new prediction counts from 0
        Assert.False(ExecutionPromptRules.PressSpendsOffer(0, ref seen, shownLastFrame: true));
        Assert.False(ExecutionPromptRules.PressSpendsOffer(1, ref seen, shownLastFrame: false));   // the window opens this frame (one-frame race): not spent
    }

    /// <summary>X6 review X6-8 (owner: every armour piece is covered): the name exclusion applies only to what is not an armour piece; a weapon is never covered.</summary>
    [Fact]
    public void ArmourPiecesAreCoveredWhateverTheirName()
    {
        foreach (var name in new[] { "sm_shldr_purity_seal_l", "csm_armour_chain_torso", "sm_trophy_helmet", "sm_fur_shldrr", "sm_belt_legs", "csm_cape_backpack", "Standard_2", null })
            Assert.True(OvershieldCoverage.Covered(name, armourPiece: true), name ?? "null");
        foreach (var name in new[] { "Worn_sm_purity_seal", "Worn_csm_chains_RIG_SpineD", "leather_pouch_r" })
            Assert.False(OvershieldCoverage.Covered(name, armourPiece: false), name);
        Assert.True(OvershieldCoverage.Covered("Worn_csm_jumpack_mp_basic_RIG_SpineD", armourPiece: false));
        Assert.False(OvershieldCoverage.Covered("Weapon_melee_sm_mp_melee_chainsword", armourPiece: true));
    }

    [Fact]
    public void OvershieldStrengthFollowsTheOwnersPicks()
    {
        // Owner 2026-11-25 (from the 3 x 3 grid): full 0.6, half 0.6, low (0.15 of the cap) 1.0; smooth between half and low, 1.0 below low.
        Assert.Equal(0.6f, OvershieldLook.StrengthAt(1f), 5);
        Assert.Equal(0.6f, OvershieldLook.StrengthAt(0.75f), 5);
        Assert.Equal(0.6f, OvershieldLook.StrengthAt(0.5f), 5);
        Assert.Equal(1.0f, OvershieldLook.StrengthAt(0.15f), 5);
        Assert.Equal(1.0f, OvershieldLook.StrengthAt(0.05f), 5);
        Assert.Equal(0.8f, OvershieldLook.StrengthAt(0.325f), 4);   // the midpoint of the smoothstep
        float last = 0f;
        for (float f = 0.5f; f >= 0.15f; f -= 0.01f) { float s = OvershieldLook.StrengthAt(f); Assert.True(s >= last - 1e-5f, $"{f}: {s} < {last}"); last = s; }   // rises as the overshield falls
        Assert.Equal(1.0f, OvershieldLook.StrengthAt(0f), 5);
        Assert.Equal(1.0f, OvershieldLook.StrengthAt(float.NaN), 5);   // not finite: read as empty (the shell is off at 0 anyway)
    }

    [Fact]
    public void FlinchNeverHidesButReactionsDo()
    {
        var c = Config();
        // A light hit's flinch (aim sway only) does not refuse a press on the server (X3, lead 40), so it does not hide the marker either.
        Assert.Equal(ExecutionPromptHidden.None, ExecutionPromptRules.Common(c, Me() with { FlinchTicks = 5 }, false, false, Rows));
        foreach (var kind in new[] { ReactionKind.Interrupt, ReactionKind.Stun, ReactionKind.Stagger })
            Assert.Equal(ExecutionPromptHidden.Reaction, ExecutionPromptRules.Common(c, Me() with { Reaction = kind, ReactionTicks = 4 }, false, false, Rows));
    }

    [Fact]
    public void SamePickAsServer()
    {
        var c = Config();
        var me = Me();
        // Three Boys in reach at different angles and distances, one Nob (an elite: never), one reserved.
        var shown = new[]
        {
            Shown(4, new Vector3(0.9f, 1.4f, 0f)), Shown(7, new Vector3(-0.2f, 1.25f, 0f)), Shown(9, new Vector3(0f, -1.6f, 0f)),
            Shown(11, new Vector3(0.1f, 1.2f, 0f), MusouEnemyType.Nob), Shown(12, new Vector3(0.05f, 1.22f, 0f)),
        };
        var near = new List<MusouEnemy>();
        ExecutionPromptRules.Gather(me, shown, near);
        Assert.Equal(5, near.Count);
        var reserved = new HashSet<ushort> { 12 };
        int choices = 0;
        ushort prompt = ExecutionPromptRules.WarfrontPick(me, me.Yaw, c, Rows, null, near, [], reserved, ref choices);
        // The press (X4: the same call with the seed of the press tick) takes the same Ork.
        var press = ExecutionChoice.ChooseAlignedOrk(me, me.Yaw, "c", "sm_mp_melee_chainsword", near, Rows, c, null, reserved, id => ExecutionChoice.Seed(4321, me.PlayerId, id), []);
        Assert.True(press.Result.Ok);
        Assert.NotEqual((ushort)0, prompt);
        Assert.Equal(press.Id, prompt);
        Assert.NotEqual((ushort)11, prompt);
        Assert.NotEqual((ushort)12, prompt);
    }

    [Fact]
    public void BystandersAreOtherPlayersAndPlayerSolidEnemiesOnly()
    {
        var c = Config();
        var into = new List<Vector3>();
        var others = new[] { Me(), Me(new Vector3(2f, 0f, 0f)) with { PlayerId = 2 }, Me(new Vector3(40f, 0f, 0f)) with { PlayerId = 3 }, Me(new Vector3(1f, 1f, 0f)) with { PlayerId = 4, Alive = false } };
        var shown = new[] { Shown(5, new Vector3(0f, 1.3f, 0f)), Shown(6, new Vector3(3f, 0f, 0f), MusouEnemyType.Warboss) };
        ExecutionPromptRules.Bystanders(c, Me(), others, shown, into);
        Assert.Equal([new Vector3(2f, 0f, 0f), new Vector3(3f, 0f, 0f)], into);   // the near living player and the Warboss; not the Boy, the far or dead player, or the executor
    }

    [Fact]
    public void ClassicOfferWindowAtViewTick()
    {
        var offer = new ExecutionOfferView(3, 100, 24);
        Assert.False(ExecutionPromptRules.ClassicOpen(offer, 99));
        Assert.True(ExecutionPromptRules.ClassicOpen(offer, 100));
        Assert.True(ExecutionPromptRules.ClassicOpen(offer, 124));
        Assert.False(ExecutionPromptRules.ClassicOpen(offer, 124.5));
        Assert.False(ExecutionPromptRules.ClassicOpen(null, 110));
    }

    [Fact]
    public void ExitViewTakesTheCameraHeading()
    {
        // The game's forward at yaw y is (-sin y, cos y); a positive pitch looks up.
        foreach (float yaw in new[] { 0f, 0.7f, -2.1f, 3f })
        {
            var forward = new Vector3(-MathF.Sin(yaw) * MathF.Cos(0.3f), MathF.Cos(yaw) * MathF.Cos(0.3f), -MathF.Sin(0.3f));
            var (y, p) = ExecutionCameraMath.ViewOf(forward, 9f);
            Assert.Equal(0f, MathF.IEEERemainder(y - yaw, MathF.Tau), 4);
            Assert.Equal(-0.3f, p, 4);
        }
        Assert.Equal(1.5f, ExecutionCameraMath.ViewOf(new Vector3(0f, 0f, -1f), 1.5f).Yaw);   // straight down: the yaw kept
        Assert.Equal(0f, ExecutionCameraMath.OvershieldFraction(100f, 100f));
        Assert.Equal(0.5f, ExecutionCameraMath.OvershieldFraction(150f, 100f), 4);
        Assert.Equal(1f, ExecutionCameraMath.OvershieldFraction(260f, 100f));
    }
}
