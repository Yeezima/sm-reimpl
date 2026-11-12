using System.Numerics;
using SmNet;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// Headshot medal and scoreboard (owner 2026-11-07; protocol 64): the server marks a weapon kill on the head (<see cref="KillEvent.Headshot"/>), counts headshot kills and assists per
/// player, and the score follows from the counts (<see cref="ScoreRules"/>: 50 a kill, 50 more a headshot); the kill feed's cause code 6 and the scoreboard rows carry them.
/// Synthetic classes and weapons only (no game data).
/// </summary>
public class HeadshotScoreTests
{
    const int SmTac = 0, CsmTac = 1;

    /// <summary>A rifle without scatter or fall-off: 20 damage a shot, x1 on the head.</summary>
    static readonly WeaponSpec Rifle = new("test_rifle", WeaponSlot.Ranged, TriggerKind.SemiAuto, 20f, 0.2f, 30, 90, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    static readonly WeaponSpec Sidearm = new("test_sidearm", WeaponSlot.Pistol, TriggerKind.SemiAuto, 10f, 0.2f, 10, 40, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);

    static ClassLoadout Tac(string name, Team side) => new(name, 0, 1, SimConfig.PlaceholderMeleeSteps) { PrimaryChoices = [0], PistolChoices = [1], Side = side, Role = "tactical" };

    /// <summary>Two rifle hits kill (40 health, no armour); a quick respawn.</summary>
    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle, Sidearm],
        Classes = [Tac("sm - tactical", Team.SpaceMarines), Tac("csm - tactical", Team.Chaos)],
        HealthMax = 40f, ShieldMax = 0f, HealthRegenDelay = 1e6f, MaxRewindTicks = 40, RespawnTime = 0.5f,
    };

    static World Arena(params (int Class, Vector3 At)[] players)
    {
        var world = new World(Config, null, players.Select(p => new SpawnPoint(p.At, 0f)).ToArray()) { Rules = new MatchRules { TimeLimit = 0f } };
        for (int i = 0; i < players.Length; i++) world.Spawn(i + 1, players[i].Class, 0);
        for (int i = 0; i < players.Length; i++) Assert.Equal(players[i].At, world.Players[i + 1].Position);
        world.Step(_ => Idle);
        Assert.Equal(MatchState.Playing, world.State);
        return world;
    }

    static readonly PlayerInput Idle = new(0, 0, 0, MathF.PI, InputButtons.None);

    /// <summary>The pitch from the shooter's eye to a point <paramref name="height"/> over the feet of a player <paramref name="distance"/> metres away.</summary>
    static float PitchTo(float height, float distance) => MathF.Atan2(height - Config.EyeHeight, distance);
    static float HeadPitch(float distance) => PitchTo(Config.HeadHeight, distance);
    static float ChestPitch(float distance) => PitchTo((Config.BodyBottom + Config.BodyTop) / 2f, distance);

    /// <summary>Player <paramref name="shooter"/> fires (press edges) at the given yaw and pitch until <paramref name="target"/> dies; returns the hits seen.</summary>
    static List<HitEvent> ShootDead(World w, int shooter, int target, float yaw, float pitch)
    {
        var hits = new List<HitEvent>();
        for (int k = 0; k < 200 && w.Players[target].Alive; k++)
        {
            w.Step(id => id == shooter ? new PlayerInput(0, 0, 0, yaw, k % 2 == 0 ? InputButtons.Fire : InputButtons.None, pitch) : Idle);
            hits.AddRange(w.LastHits);
        }
        Assert.False(w.Players[target].Alive);
        return hits;
    }

    [Fact]
    public void A_kill_whose_killing_shot_hits_the_head_is_a_headshot_and_scores_fifty_more()
    {
        var w = Arena((SmTac, new Vector3(0, 0, 0)), (CsmTac, new Vector3(0, 5, 0)));
        var hits = ShootDead(w, 1, 2, 0f, HeadPitch(5f));
        Assert.All(hits, h => Assert.Equal(HitZone.Head, h.Zone));
        var kill = Assert.Single(w.LastKills);
        Assert.True(kill.Headshot);
        Assert.Equal(new PlayerScore(1, 0, Headshots: 1), w.Scores[1]);
        Assert.Equal(ScoreRules.KillScore + ScoreRules.HeadshotScore, w.Scores[1].Score);
        Assert.Equal(100, w.Scores[1].Score);   // the owner: +50 for the headshot (the data's 35 is not used)
        Assert.Equal(0, w.Scores[2].Score);
    }

    [Fact]
    public void A_body_kill_is_no_headshot_and_scores_the_kill_alone()
    {
        var w = Arena((SmTac, new Vector3(0, 0, 0)), (CsmTac, new Vector3(0, 5, 0)));
        var hits = ShootDead(w, 1, 2, 0f, ChestPitch(5f));
        Assert.All(hits, h => Assert.Equal(HitZone.Body, h.Zone));
        Assert.False(Assert.Single(w.LastKills).Headshot);
        Assert.Equal(new PlayerScore(1, 0), w.Scores[1]);
        Assert.Equal(50, w.Scores[1].Score);
    }

    [Fact]
    public void Only_the_killing_hit_decides_a_head_hit_then_a_body_kill_is_no_headshot()
    {
        var w = Arena((SmTac, new Vector3(0, 0, 0)), (CsmTac, new Vector3(0, 5, 0)));
        w.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0f, InputButtons.Fire, HeadPitch(5f)) : Idle);
        Assert.Equal(HitZone.Head, Assert.Single(w.LastHits).Zone);
        w.Step(_ => Idle);
        ShootDead(w, 1, 2, 0f, ChestPitch(5f));
        Assert.False(Assert.Single(w.LastKills).Headshot);
        Assert.Equal(0, w.Scores[1].Headshots);
    }

    [Fact]
    public void Headshot_rule_takes_only_a_weapons_shot_on_the_head()
    {
        var shot = new HitEvent(1, 1, 2, HitZone.Head, 5f, 0f, 40f, true, Weapon: 0);
        Assert.True(ScoreRules.IsHeadshotKill(shot));
        Assert.False(ScoreRules.IsHeadshotKill(shot with { Killed = false }));
        Assert.False(ScoreRules.IsHeadshotKill(shot with { Zone = HitZone.Body }));
        Assert.False(ScoreRules.IsHeadshotKill(shot with { Melee = true }));
        Assert.False(ScoreRules.IsHeadshotKill(shot with { ThrownItem = true }));
        Assert.False(ScoreRules.IsHeadshotKill(shot with { Landing = true }));
        Assert.False(ScoreRules.IsHeadshotKill(shot with { Splash = true }));
        Assert.False(ScoreRules.IsHeadshotKill(shot with { Cone = true }));
        Assert.False(ScoreRules.IsHeadshotKill(shot with { Perk = 0 }));
        Assert.False(ScoreRules.IsHeadshotKill(shot with { Weapon = -1 }));
    }

    /// <summary>Player 1 (Space Marines) at the origin facing player 2 (Chaos) 6 m up +Y, player 3 (Space Marines) at (3, 3) (the spawn rule gives each the point farthest from its enemies).</summary>
    static World Trio() => Arena((SmTac, new Vector3(0, 0, 0)), (CsmTac, new Vector3(0, 6, 0)), (SmTac, new Vector3(3, 3, 0)));
    static readonly float Yaw3 = MathF.Atan2(3f, 3f), Distance3 = MathF.Sqrt(18f);

    [Fact]
    public void A_teammate_who_dealt_a_quarter_of_the_damage_gets_an_assist_and_the_killer_does_not()
    {
        // Players 1 and 3 (Space Marines) both see player 2 (Chaos): 3 hits once (20 of 40), 1 finishes.
        var w = Trio();
        w.Step(id => id == 3 ? new PlayerInput(0, 0, 0, Yaw3, InputButtons.Fire, ChestPitch(Distance3)) : Idle);
        Assert.Equal((3, 2), (Assert.Single(w.LastHits).Shooter, w.LastHits[0].Target));
        w.Step(_ => Idle);
        ShootDead(w, 1, 2, 0f, ChestPitch(6f));
        Assert.Equal(1, Assert.Single(w.LastKills).Killer);
        Assert.Equal((1, 0), (w.Scores[1].Kills, w.Scores[1].Assists));
        Assert.Equal((0, 1), (w.Scores[3].Kills, w.Scores[3].Assists));
        Assert.Equal(0, w.Scores[3].Score);   // assists are counted, not scored (spec: what scores)
    }

    [Fact]
    public void Damage_from_an_earlier_life_earns_no_assist()
    {
        var w = Trio();
        ShootDead(w, 3, 2, Yaw3, ChestPitch(Distance3));   // 3 kills 2
        Assert.Equal(1, w.Scores[3].Kills);
        for (int k = 0; k < 60 && !w.Players[2].Alive; k++) w.Step(_ => Idle);
        Assert.True(w.Players[2].Alive);
        var at = w.Players[2].Position;
        float distance = Vector2.Distance(new Vector2(at.X, at.Y), Vector2.Zero);
        ShootDead(w, 1, 2, MathF.Atan2(-at.X, at.Y), ChestPitch(distance));
        Assert.Equal(0, w.Scores[3].Assists);
    }

    // ---------------------------------------------------------------- wire

    [Fact]
    public void The_kill_feed_carries_the_headshot_as_cause_code_six_and_only_for_a_weapon_kill()
    {
        Assert.Equal(70, Protocol.Version);
        KillView[] kills =
        [
            new(1, 1, 2, KillCause.Weapon, 3, Vector3.UnitX, Headshot: true),
            new(2, 1, 3, KillCause.Weapon, 3, Vector3.UnitX),
            new(3, 4, 5, KillCause.Melee, 0, Vector3.UnitY, Headshot: true),   // not a weapon kill: the flag does not travel
        ];
        var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, [], Match: new MatchView(MatchState.Playing, Team.None, 0, 0, 0), Kills: kills))));
        Assert.Equal(kills.Select(Protocol.Quantize), back.Kills!);
        Assert.Equal([true, false, false], back.Kills!.Select(k => k.Headshot));
        Assert.Equal([KillCause.Weapon, KillCause.Weapon, KillCause.Melee], back.Kills!.Select(k => k.Cause));
    }

    [Fact]
    public void Scoreboard_rows_carry_headshots_and_assists_and_the_score_follows()
    {
        var rows = new ScoreEntry[]
        {
            new(1, Team.SpaceMarines, 0, 7, 2, Name: "Brother Aurex", Headshots: 3, Assists: 4),
            new(2, Team.Chaos, 1, 1, 7, Headshots: 300, Assists: -2),   // clamped to 0..255 on the wire
        };
        var back = Assert.IsType<Scoreboard>(Protocol.Read(Protocol.Write(new Scoreboard(1, true, 41, 600f, false, rows))));
        Assert.Equal((3, 4, 7 * 50 + 3 * 50), (back.Players[0].Headshots, back.Players[0].Assists, back.Players[0].Score));
        Assert.Equal((255, 0), (back.Players[1].Headshots, back.Players[1].Assists));
    }

    [Fact]
    public void The_fullest_scoreboard_stays_within_one_datagram()
    {
        string name = new('W', PlayerNames.MaxLength);
        var rows = Enumerable.Range(1, Protocol.MaxPlayers).Select(i => new ScoreEntry(i, i % 2 == 0 ? Team.Chaos : Team.SpaceMarines, 0, 65535, 65535, 65535, 0, name, 999, true, 255, 255)).ToArray();
        var seize = new SeizeGroundInfo(10f, 5f, 1, 1f, [.. Enumerable.Range(0, Protocol.MaxSnapshotPoints).Select(k => new ControlPointInfo(new string('p', Protocol.MaxMapName), Vector3.One, 5f, 2f))]);
        var spectators = Enumerable.Range(100, Protocol.MaxSpectators).Select(i => new SpectatorEntry(i, name)).ToArray();
        var bytes = Protocol.Write(new Scoreboard(1, true, 41, 600f, false, rows, seize, spectators));
        Assert.InRange(bytes.Length, 1, 1200);
    }
}
