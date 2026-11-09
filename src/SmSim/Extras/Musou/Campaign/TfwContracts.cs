using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// Musou Mode (the war; <c>Tfw</c> in code means Musou Mode, docs/added-content.md): the rules' version. Hashed into the config inside Musou's block
/// (<c>SimConfig.Extras.cs</c>), so a war-only change raises this and touches neither <see cref="MusouSim.Version"/> nor <see cref="ContentExtras.WireVersion"/>.
/// The war's own numbers (kind ours; design sections C to F) join this class with the milestones that use them.
/// </summary>
public static class TfwRules
{
    public const uint Version = 58;   // 58: musou/stim-regen (owner, 2026-11-09): the combat stims' charges refill on the grenade rule (one back every 15 s up to the most). 57: musou/guard-vantage (owner, 2026-11-09): small Guard teams (3 to 6 men) go up to vantage points found from the level (GuardVantage) within 50 m of a friendly-captured point, else in front of the base, hold them, then rejoin their squad (TfwBattle.Vantage.cs; the lead renumbers) (renumbered 52 -> 57 at integration); 56: no Ork army aggregates and 80 % of the Orks (owner, 2026-11-09): an Ork army is real Orks alone (no rectangular impostor formations), the wave sizes, the armies' real cap, the garrisons, crowds, alive caps, trickle, patrols, Boss retinues and Big Shoota squads at 80 % (musou-ork-movement; to be renumbered at integration) (renumbered 53 -> 56 at integration); 55: musou/combat-rules (owner 2026-11-09): the players' cleave in the war reaches 1.5x further in a 120 degree half angle, up to 40 enemies a swing (renumbered at integration) (renumbered 53 -> 55 at integration); 54: every Musou Mode spawn (players, allies, Guardsmen, Orks) faces the map centre, the middle capture point (musou/spawn-facing, owner 2026-11-09) (renumbered 52 -> 54 at integration); 53: Ork movement (owner, 2026-11-09): the armies real within 300 m (aggregate past 330, the nearest made real first), the Stormboy squads run on between hops and spread round a fighter, the crowd's wider packs, 50 degree bearings, 10 degree wander, 0.4 flank share (musou-ork-movement; to be renumbered at integration) (renumbered 52 -> 53 at integration); 52: musou/combat-rules (owner 2026-11-09): every melee strike gives back 5 % armour, the stim's strike 25 % health + 20 % armour, the air slash's landing 25 % health + 10 % armour (renumbered at integration); 51: musou/boss-collision (owner, 2026-11-08): the big Orks solid to the players and lockable (MusouSolids; 44 to 48 are taken on integration/playtest-2026-11-08b) (renumbered 49 -> 51 at integration); 50: every plasma weapon's shot pierces all enemies on its path and burns each (3 s, 50 % of the hit in 0.5 s ticks, renewed not stacked), tag 11 kind Burn (owner, 2026-11-08) (musou/plasma-pierce-burn; the integration is at 48); 43:hotfix/bolt-blast-nerf (owner, 2026-11-08): the bolt blast's reach halved (2.5 to 1.25 m, its fall-off with it), a bolt blast kill dies as MusouDeathKind.BoltBlast (torn apart, never thrown), a fodder Ork it hurts falls where he stands (no throw), the rest stagger in place (renumbered 49 -> 50 at integration); 48: Orks face their target (MusouSim.Version 22; owner 2026-11-08) (ork-facing-dc3c3c; renumbered 44 -> 48 at integration); 47: musou/melee-feel (owner, 2026-11-08): the players' melee cleave reaches 1.5x as far (MusouCombatRules.CleaveRadiusScale) (melee-feel-b69f37; renumbered 44 -> 47 at integration); 46: musou/guard-spread (owner, 2026-11-08): the Guardsmen walk to a point in wide open order in three lanes, keep their way off the walls, take a detour when stuck, and split into three fire teams over 120 degrees round a point they hold (GuardRules.Spread) (guard-spread-2a9f42; renumbered 44 -> 46 at integration); 45: the grenade refill every 15 s (was 30; owner 2026-11-08: "vanilla grenades every 15s"), grenades back to vanilla (owner, 2026-11-08): one frag per throw with the data's blast and damage, the thrower not spared, the item's spawn count per life (musou/grenades-vanilla; the lead renumbers) (musou/grenades-vanilla; renumbered 44 -> 45 at integration); 44: claude/retainers-no-ammo-drops (owner 2026-11-08): each human's retainer and its orders back (Follow/Hold/Attack on Z/X/V, the revive, the 180 war-second recovery) beside the two roaming allied Marines (no lord or Defender), and no Ork KO leaves an ammunition or equipment box (TfwRewardRules.KillBoxes); 43: hotfix/bolt-blast-nerf (owner, 2026-11-08): the bolt blast's reach halved (2.5 to 1.25 m, its fall-off with it), a bolt blast kill dies as MusouDeathKind.BoltBlast (torn apart, never thrown), a fodder Ork it hurts falls where he stands (no throw), the rest stagger in place; 42: hotfix/musou-allies-two (owner 2026-11-08): two allied AI Marines in a battle whatever the number of players, roaming together (a leader and a follower) between the contested positions and the secured ones under attack, 30 s respawn at the landing zone; no retainers, lords or Defenders, the order keys do nothing; 41: hotfix (owner, 2026-11-08): on Ashfall every battle lands at the Imperial base in the south centre (the Space Marine start area, TfwBattlefield.BaseLanding), so the landing, a redeploy and a late join always put a human there; 40: the musou/perf-pass integration of the owner's picks (2026-11-08): the near balance is expansion S6's (the Guardsmen 0.875x, a Guardsman worth an Ork in the far clash; no near-only share), the remainder fights on in the far layer near a player (S6's rule: the real units a human can see take the aggregates' fire), waves march on the base when the players hold no point, a near army's aggregate routs by the army's own losses, a waiting wave creeps on after 2.5 s (S6), the route searches run side by side and are taken at their fixed tick; 39: expansion S6's answers (the Guardsmen 0.875x from 4.5x, even man for man with the Orks as in the far clash, owner 2026-11-08; musou/exp-s6, renumbered from 37); 38: S4 follow-up (owner, 2026-11-08): a scrap trap kills an AI Marine outright, the shield dome also stops the Guardsmen's fire, Ork blasts, the Orks' rockets and every player projectile from outside, the Guardsmen shoot built Rokkit turrets; 37: expansion S6, the battle flow: armies in a far layer (aggregates, real within 90 m of a human), Ork waves with committed destinations, Guard reinforcements and garrisons of 200, retreats and rallies, the far clash, six Defenders (puppet ids 65516 up), GuardSim 40 squads, tag 18 (musou/exp-s6); 36: expansion S4: five Scrap Meks in a battle and their contraptions (TfwBattle.Meks.cs, World.TfwMeks.cs, tag 17); 35: expansion S3: Stormboy squads in place of the hunter columns, a Big Shoota Nob and his squad at each position (TfwBattle.Stormboys.cs, TfwBattle.BigShootas.cs) (musou/exp-s3); 34: Rokkit Boyz in the crowds and Bomb Squigs dropped near the players in a battle (expansion S2, TfwBattle.Rokkits.cs, World.TfwSquigs.cs); 2: TF6, the war's sections on the wire and the commands with a place; 3: TF4, the war runs (the battle, the run, the extraction); 4: TF7b, tag 7 carries the own block (TfwOwnView); 5: the own block's deployed flag, and an extraction after a won battle never ends the run; 6: pass 2's contracts (tags 11 to 14, the commands ChangeClass and ChooseReward, Knocked, the deaths Barrage and Strafe); 7: battles on Manufactorum (M1: the map's points, drawn sites, tag 8 carries each radius and the sites); 8: M7 and M11 (the class change in the Stormbird: the command ChangeClass and its smoke on tag 11; the elites' bars on tag 13; a retainer wears its owner's look; 9: the hub is the Thunderhawk's interior, map version 4); 10: taken by the rewards (M6); 11: allied navigation and line of sight (M2: the Guardsmen and AI Marines go by the walking graph, shots need a sight, squads go for the nearest position not secured, all three live from the landing) and the Orks (M3: garrisons, the tide from each point, retaking, elites by the run's wins, the crowd; the owner's changes: spawning stops only with a full capture, 200 alive, no Guardsmen for now); 12: taken by the rewards; 13: M5, the shove and the fall (Knocked), three big frags, the 20-enemy cleave, piercing shots; 14: rewards, slots and scores merged with 13; 15: fire support (M8: tag 11 carries the shells, the commands FireTest and Orbital, the own block's cooldown); 16: orbital strikes have no cooldown (every B press calls one; the own block's byte stays, always 0); 17: a shell kills every enemy in a 12 m sphere; 18: a shell only scratches a boss; 19: the crowds, garrisons that never respawn and the stuck-Ork rule; 20: hotfix: the final boss in the courtyard and always large (BossScale 1.6), a steady trickle of Orks, the reward cards off; 23: a light swing is a random one of the chain, and a round that bursts on an enemy kills as a blast; 24: twice the Orks, crowds that go after a wait, elite squads, the melee heal, a boss of 3, no Ork damage at the range; 25: the orbital strike's 60 s cooldown in a battle (the owner, 2026-11-06), none at the Firing Range; the own block's byte carries it again; 26: packs (the Orks muster, then go as loose blobs that surround their target) and twice the Orks again (2026-11-06); 27: the roaming Boss Orks and their retinues, the commander is the Warboss (owner, 2026-11-06); 28: the Guardsmen back as defenders who never capture, a capture that goes on with Orks on the point, the Boss Orks cleave Guardsmen (owner, 2026-11-06) (musou/guard-formations); 29: with patrol and hunter columns from the existing Orks, and no Ork staggers or knocks back a player (TfwCombatRules.EnemiesImpede; owner, 2026-11-06) (musou/ork-patrols-nostun, merged); 30: endless ammunition, a grenade back every 30 s, the AI Marines take a tenth of every hit, the Guardsmen 4.5x health and damage (owner, 2026-11-06) (night/musou-rules); 31: the late batch's run flow (stream D, owner 2026-11-06): the commands Redeploy (a fallen human back at the landing zone for 1 supply) and ReturnToShip (the host's recall: survive 20 s, then the Stormbird), and the giant Ork wave from every side after the Warboss of a battle falls; 32: a bolt weapon's shot sets off a small blast at every enemy it strikes, tag 11 kind BoltBlast (owner, 2026-11-07) (musou/bolt-pierce-blasts; renumbered 31 -> 32 at integration); 33: the Weirdboyz come in a battle (TfwBattle.Weirdboyz.cs) (claude/musou-weirdboy; renumbered 31 -> 33 at integration)
}

/// <summary>The contract the war's milestones share (TF0, frozen): ids and how the war's actors are named across the simulation, the server and the client.</summary>
public static class TfwContracts
{
    /// <summary>
    /// The server-side AI Marines' player ids (real World players without a peer; design section E): 12 ids at the top of the 16-bit range (8 until expansion S6, which widened the range downwards for the
    /// six Defenders, so <see cref="PointTargetId"/> keeps its values). Since 2026-11-08 a battle holds the allied pair (<see cref="AllyRules"/>) and a retainer per human, six at most; the range stays as it was.
    /// </summary>
    public const int PuppetIdFirst = 65516, PuppetIdLast = 65527;
    /// <summary>How many puppet ids there are.</summary>
    public const int PuppetIds = PuppetIdLast - PuppetIdFirst + 1;

    /// <summary>Whether <paramref name="id"/> is one of the AI Marines' ids. Means nothing outside a war (<see cref="World.Tfw"/> null): a human may hold such an id there.</summary>
    public static bool IsPuppet(int id) => id is >= PuppetIdFirst and <= PuppetIdLast;

    /// <summary>The <see cref="MusouTarget.Id"/> of a secured position as the place the Orks go to retake (<see cref="MusouTarget.Goal"/>): over the AI Marines' ids, under 16 bits.</summary>
    public static int PointTargetId(int position) => PuppetIdLast + 3 + position;

    /// <summary>The <see cref="MusouTarget.Id"/> of Guard squad <paramref name="squad"/> (0-based): -2 and down, so it never meets a player's id or <see cref="FlowField"/>'s -1 for an unreached node.</summary>
    public static int GuardTargetId(int squad) => -(squad + 2);

    /// <summary>Whether a target or attack names a Guard squad (<see cref="MusouAttack.Player"/> below 0; the World routes it to <c>TfwGuardHit</c>).</summary>
    public static bool IsGuardTarget(int id) => id <= -2;

    /// <summary>The squad of a Guard target id (the inverse of <see cref="GuardTargetId"/>).</summary>
    public static int SquadOf(int targetId) => -targetId - 2;
}

/// <summary>The three fronts.</summary>
public enum TfwLane : byte { A = 0, B = 1, C = 2 }

/// <summary>Where the run stands (design sections C, D and F).</summary>
public enum TfwRunPhase : byte { Hub = 0, Travel = 1, Battle = 2, Pickup = 3, FinalAssault = 4, Won = 5, Lost = 6 }

/// <summary>A capture position in a battle (design section D): the chain goes Locked, Held, Clearing, Securing, Secured.</summary>
public enum TfwPositionState : byte { Locked = 0, Held = 1, Clearing = 2, Securing = 3, Secured = 4 }

/// <summary>A client's command (message kind 17, tag <c>TfwWire.CommandTag</c>; design section H). Deploy's argument is the lane; Hold and Attack carry a place (<see cref="TfwCommandRecord.Point"/>); ChooseReward's argument is the card (0 to <c>TfwWire.RewardCards</c> - 1) and <see cref="TfwCommandRecord.Offer"/> the offer it answers; SelectSlot's argument is the weapon key (1 to 5). Redeploy (a fallen human back at the landing zone for a supply) and ReturnToShip (the host's recall) take none (late batch 2026-11-06, TfwRun.Flow.cs).</summary>
public enum TfwCommand : byte { Deploy = 1, RequestPickup = 2, CancelPickup = 3, Follow = 4, Hold = 5, Attack = 6, ChangeClass = 7, ChooseReward = 8, SelectSlot = 9, Orbital = 10, FireTest = 11, RangeGo = 12, RangeBack = 13, Redeploy = 14, ReturnToShip = 15 }

/// <summary>One decoded command: its sequence (resent until tag 7 echoes it, idempotent by it), what it asks, its argument and, for Hold and Attack, the point of the sender's crosshair (metres; untrusted, within about 3 km on each axis). <see cref="Offer"/>: ChooseReward's offer sequence, so a delayed pick never lands on a newer offer.</summary>
public readonly record struct TfwCommandRecord(ushort Sequence, TfwCommand Command, byte Argument, Vector3 Point = default, ushort Offer = 0);

/// <summary>One front as the simulation holds it and tag 7 carries it (design section C): front -1 (HQ besieged) to 3 (stronghold), progress -1 to 1, the strengths, each position's owner bit, the gather time left, the lord's state.</summary>
public readonly record struct TfwLaneState(sbyte Front, float Progress, float Imperial, float Ork, byte OwnedMask, float GatherLeft, byte LordState);

/// <summary>The war as tag 7 carries it: the phase, the battle's lane, the war clock (seconds), the time scale, the reserves, the newest command sequence applied per the sender (filled per client), the three fronts, and what only this client's HUD needs (<see cref="TfwOwnView"/>, per client as the echo).</summary>
public readonly record struct TfwWarView(TfwRunPhase Phase, TfwLane BattleLane, float Clock, float TimeScale, byte Transport, byte Supplies, ushort CommandEcho,
    TfwLaneState A, TfwLaneState B, TfwLaneState C, TfwOwnView Own = default);

/// <summary>A player's retainer as the HUD shows it (TF7b; the allied pair's for a4a4b393 to the restore of 2026-11-08): none yet (in the Stormbird), up, reviving its player, or down and recovering.</summary>
public enum TfwRetainerState : byte { None = 0, Ready = 1, Reviving = 2, Recovering = 3 }

/// <summary>
/// What one client's HUD needs of the run beyond the fronts (TF7b, the end of tag 7): the seconds left, rounded up (so a countdown changes the record once a second) of the travel, the pickup, the
/// own revive window and the extraction's prompt; the revive's progress in tenths; whether the battle is won (a pickup is then free); the own retainer, its order and its recovery (war seconds, rounded; the allied pair is not on it; restored 2026-11-08), rounded
/// up); the battle's Guardsmen alive, squads in the field, squads in reserve and positions secured; and whether this player is in the run's field (<see cref="TfwRun.InField"/>: one who joined after the
/// landing is not, so the run does not hold its respawn); and the orbital strike's cooldown left, whole seconds rounded up (0: ready; contracts 25, the owner 2026-11-06: 60 s in a battle, never at the Firing Range).
/// </summary>
public readonly record struct TfwOwnView(byte TravelSeconds, byte PickupSeconds, byte ReviveSeconds, byte ExtractionSeconds, byte ReviveTenths, bool BattleWon,
    TfwRetainerState Retainer, MarineOrder Order, ushort RecoverySeconds, byte Guards, byte Squads, byte Reserves, byte Secured, bool Deployed = false, byte OrbitalSeconds = 0);

/// <summary>One capture position as tag 8 carries it (positions quantised over the Musou box).</summary>
public readonly record struct TfwObjective(byte Index, TfwPositionState State, float Progress, Vector3 Position, float Radius = 0f);

/// <summary>One Guardsman as tag 9 carries it (10-byte records, as the enemies').</summary>
public readonly record struct TfwGuard(ushort Id, Vector3 Position, float Yaw, byte State);

/// <summary>What a fire record reports (tag 11): a shell's landing, a Thunderhawk run, one round of its guns, the smoke of a class change, a frag's big blast, a bolt weapon's blast at an enemy it struck.</summary>
public enum TfwFireKind : byte { Shell = 1, Run = 2, Round = 3, Smoke = 4, Frag = 5, BoltBlast = 6,
    /// <summary>An enemy is burning from a plasma hit (owner, 2026-11-08): the tick, <see cref="TfwFireEvent.Subject"/> the enemy, <see cref="TfwFireEvent.At"/> where he stands. The client draws the flame for the burn's length and renews it on a later record.</summary>
    Burn = 7 }

/// <summary>
/// One record of tag 11; a client plays each (<see cref="Kind"/>, <see cref="Tick"/>, <see cref="Subject"/>) once. Shell: <see cref="Tick"/> the landing tick, <see cref="Subject"/> its number in the barrage,
/// <see cref="At"/> the impact. Run: the first tick, the run's number, the path from <see cref="At"/> to <see cref="To"/>. Round: the firing tick, a running count, muzzle to impact. Smoke: the first tick, the player,
/// <see cref="At"/> his feet. Frag: the tick it went off, the projectile, <see cref="At"/> the blast, <see cref="To"/> X the thrown item and Y the blast's radius. BoltBlast: the tick, a running count, <see cref="At"/> the blast,
/// <see cref="To"/> X the thrown item whose effect it plays (a frag; -1 none) and Y its radius.
/// </summary>
public readonly record struct TfwFireEvent(TfwFireKind Kind, uint Tick, ushort Subject, Vector3 At, Vector3 To = default);

/// <summary>One elite's health as tag 13 carries it: the enemy's id, 0 to 255 of its full health.</summary>
public readonly record struct TfwBar(ushort Enemy, byte Health);

/// <summary>How a map ended for the score (tag 12's history).</summary>
public enum TfwMapResult : byte { Won = 1, Left = 2, Lost = 3, Extracted = 4 }

/// <summary>This client's own score (tag 12): the map's points, the run's total, the map's KOs and its best chain.</summary>
public readonly record struct TfwScoreOwn(uint MapPoints, uint RunTotal, ushort Kos, ushort BestChain);

/// <summary>Another human's run total (tag 12).</summary>
public readonly record struct TfwScorePlayer(ushort Player, uint RunTotal);

/// <summary>One finished map of the run (tag 12's history).</summary>
public readonly record struct TfwMapScore(TfwLane Lane, byte Node, TfwMapResult Result, ushort Kos, ushort BestChain, uint Points);

/// <summary>One tag 12 section: the parts it carries (null: not in this section; a client keeps what it has). Each part has its own rate.</summary>
public readonly record struct TfwScoreUpdate(TfwScoreOwn? Own, IReadOnlyList<TfwScorePlayer>? Others = null, IReadOnlyList<TfwMapScore>? History = null);

/// <summary>What a reward card gives (tag 14); None: a card left empty when the class has too few choices.</summary>
public enum TfwRewardKind : byte { None = 0, Weapon = 1, Equipment = 2, Perk = 3 }

/// <summary>One reward card: its kind and the item (a <see cref="MusouDropItem"/>, an equipment or a perk index, by kind).</summary>
public readonly record struct TfwRewardCard(TfwRewardKind Kind, ushort Item);

/// <summary>The cards offered to one client after a win (tag 14); the newest <see cref="Sequence"/> stands.</summary>
public readonly record struct TfwRewardOffer(ushort Sequence, TfwRewardCard First, TfwRewardCard Second, TfwRewardCard Third);

/// <summary>
/// One client's extra primary slots (tag 15; owner's item 26): the weapon index in keys 3, 4 and 5 (-1: empty), and which key holds the weapon now in the player's primary and pistol fields
/// (0: its own, never an extra). The HUD draws the slots; the client needs the two so that keys 2 and 1 bring its own weapon back.
/// </summary>
public readonly record struct TfwArsenalView(short Slot3, short Slot4, short Slot5, byte LoadedPrimary, byte LoadedPistol)
{
    /// <summary>The empty slots.</summary>
    public static readonly TfwArsenalView Empty = new(-1, -1, -1, 0, 0);
    /// <summary>The weapon in key <paramref name="key"/> (3 to 5), or -1.</summary>
    public int Weapon(int key) => key switch { 3 => Slot3, 4 => Slot4, 5 => Slot5, _ => -1 };
}

/// <summary>
/// The war of one match (<see cref="World.Tfw"/>; set only by <see cref="World.StartTfw"/>). TF0 holds the contract alone: the command queue, the AI Marines' input source and
/// their names. The war itself (TF2's WarSim, TF3's Guardsmen, TF4's battle and run, TF5's Marines) grows here and in <c>World.Tfw.cs</c>.
/// </summary>
public sealed partial class TfwRun
{
    /// <summary>Commands waiting for the next tick (a client's flood is cut by the server's rate, this by the queue's size).</summary>
    public const int MaxQueuedCommands = 64;

    readonly Queue<(int Player, TfwCommandRecord Command)> _commands = new();

    /// <summary>Queues a command that arrived between ticks; false when the queue is full.</summary>
    public bool Enqueue(int player, TfwCommandRecord command)
    {
        if (_commands.Count >= MaxQueuedCommands) return false;
        _commands.Enqueue((player, command));
        return true;
    }

    /// <summary>The oldest queued command, if any.</summary>
    public bool TryDequeue(out int player, out TfwCommandRecord command)
    {
        bool any = _commands.TryDequeue(out var next);
        (player, command) = next;
        return any;
    }

    /// <summary>Commands waiting.</summary>
    public int QueuedCommands => _commands.Count;

    /// <summary>The AI Marines' inputs (TF5's MarineBrain): the input for a puppet this tick; null: it stands still.</summary>
    public Func<int, PlayerState, PlayerInput>? PuppetBrain { get; set; }

    /// <summary>An AI Marine's scoreboard name ("" for any other id; TF5's roster, MarineBrain.cs).</summary>
    public string PuppetName(int id) => TfwContracts.IsPuppet(id) ? MarineName(id) : "";
}
