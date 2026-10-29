using System.Net;
using SmContent;
using SmNet;
using SmServer;
using SmSim;

// smserver: headless dedicated server (UDP). Usage: smserver [--port 27015] [--game-dir <dir>] [--map <name>|none]
//   [--mode annihilation|seize|ffa] [--score-limit <points>] [--time-limit <minutes, 0 none>] [--friendly-fire on|off] [--countdown <seconds, 0 none>]
// The content fingerprint comes from the server's own install, so clients with a different install are refused.
// --map picks a multiplayer map (default mp_tdm_09, Manufactorum, the alpha map, when installed; "none" for the flat placeholder ground).
// --mode: Annihilation (team deathmatch, the default), seize (Seize Ground: control points from the map, M16) or ffa (no
// teams, as before M15). The limits and friendly fire are host options in the original; their defaults here are provisional
// (spec/behavior.md, M15 and M16).
// Flags without a value (protocol 22): --no-custom-emblems (no emblem is pulled, kept or served; everyone sees the fallback badge).
// Hardening (server/hardening): --max-per-ip, --join-interval, --idle-kick, --ban-file; unknown flags and flags without their value
// are refused with exit code 2 (ServerCommandLine, GameServer.CommandLine.cs); --help lists every option.
if (!ServerCommandLine.TryParse(args, out var cli, out var cliError))
{
    Console.Error.WriteLine($"smserver: {cliError}");
    Console.Error.WriteLine("smserver --help lists the options.");
    return ServerCommandLine.UsageError;
}
if (cli.Help) { Console.WriteLine(ServerCommandLine.Usage); return 0; }
bool customEmblems = cli.CustomEmblems;
var unlocks = cli.Unlocks;
int port = cli.Port;
// --bind 127.0.0.1 keeps a test server on this machine only (no firewall prompt for a new server copy); the default
// listens on every interface, as a dedicated server must.
var bind = cli.Bind;
string? gameDir = cli.GameDir;
string? mapName = cli.Map;
// --pickup-test <seconds> (test aid, fidelity gap 1): every that many seconds one drop of each type lies 2.5 m in front of every living
// player (beside the drops of real deaths), for screenshots and pickup checks.
float pickupTest = cli.PickupTestSeconds;
var rules = new MatchRules { CountdownTime = cli.CountdownSeconds ?? MatchRules.DefaultCountdown };   // match flow (ours): "MATCH BEGINS" before play
if (cli.TimeLimitMinutes is { } minutes) rules = rules with { TimeLimit = minutes * 60f };
if (cli.FriendlyFire is { } friendlyFire) rules = rules with { FriendlyFire = friendlyFire };
bool teams = cli.Teams;
var mode = cli.Mode;
int? scoreLimit = cli.ScoreLimit;

rules = rules with { Mode = mode, ScoreLimit = scoreLimit ?? MatchRules.For(mode).ScoreLimit, HonorSideRequests = cli.HonorSideRequests };
if (cli.HonorSideRequests) Console.WriteLine("--honor-side-requests: a joining client's requested side is kept up to the per-side limit; balancing applies only to joins that name no side (test aid, ours)");

string install;
try { install = GameInstall.Locate(gameDir); }
catch (DirectoryNotFoundException)
{
    // Exit code 1: no game install found (one line; GameInstall.NotFoundMessage names every way to point at it).
    Console.Error.WriteLine($"smserver: game not found{(gameDir is null ? "" : $" at --game-dir '{gameDir}'")}. {GameInstall.NotFoundMessage}");
    return 1;
}
ulong fingerprint = ContentFingerprint.Compute(install);
// Movement tuning comes from the install's multiplayer player blueprints; clients load the same, and the config hash
// in the handshake refuses any client whose values differ.
// --content (docs/added-content.md): the added content this server runs; the faithful profile is SM1 alone. The flags are in the config hash,
// so a client with another profile is refused (ConfigMismatch, naming this server's profile).
var contentExtras = cli.Content;
Console.WriteLine($"content: {ContentProfiles.Describe(contentExtras)}{(contentExtras == ContentExtras.None ? " (SM1 multiplayer alone: no added content)" : "")}; " +
    $"build {(ContentExtras.Compiled ? "with" : "without")} added content; clients need --content {ContentProfiles.Name(contentExtras)}");
if (cli.OrkData && !contentExtras.HasFlag(ContentExtras.Orks)) Console.WriteLine("--ork-data: ignored, this profile has no Ork");
// Ork O5: the Ork Boy plays (class 6) when the profile has it, in the owner's Tactical parity unless --ork-data (test aid; clients need the same flag).
var tuning = GameplayTuning.LoadForMatch(install, contentExtras, cli.OrkData);
if (!contentExtras.HasFlag(ContentExtras.Orks))
    Console.WriteLine("ork: not in this profile (no Ork class; an Ork request falls back to the default class)");
else if (tuning.Config.Classes.Any(c => c.Race == Race.Orks))
    Console.WriteLine($"ork: the Ork Boy plays, {GameplayTuning.MatchOrk(cli.OrkData)} balance{(cli.OrkData ? " (--ork-data: the data's values, a test aid; clients need --ork-data too)" : " (the owner's Tactical parity)")}; it joins the side it asks for under the balance rule");
else
{
    // O5 review: a failed Ork read leaves the six classes (MpClasses.WithOrk) but must not pass unnoticed.
    string why = string.Join(" | ", tuning.Notes.Where(n => n.Contains("ork:", StringComparison.Ordinal)));
    Console.WriteLine("**************************************************************************************************");
    Console.WriteLine($"WARNING: the Ork Boy could not be loaded; this server runs WITHOUT the Ork ({(why.Length > 0 ? why : "no note")}).");
    Console.WriteLine("WARNING: clients whose install loads the Ork have another config hash and are refused (ConfigMismatch).");
    Console.WriteLine("**************************************************************************************************");
}
var config = tuning.Config;
Console.WriteLine($"tuning from {tuning.Source}: run {config.RunSpeed}, strafe {config.StrafeSpeed}, back {config.BackSpeed}, sprint {config.SprintSpeed} m/s; placeholders: {string.Join(", ", tuning.Placeholders)}");
foreach (var note in tuning.Notes) Console.WriteLine("note: " + note);
// Fidelity gap 1: what a death drops (weights, reach, lifetime, amounts per weapon and item).
foreach (var t in config.Pickups?.Types ?? [])
    Console.WriteLine($"drop {t.Blueprint} ({t.Kind}): weight {t.Weight} of {config.Pickups!.TotalWeight}, reach {t.Radius} m, lifetime {t.Lifetime} s; " +
        string.Join(", ", config.Weapons.Select((w, i) => (w.Name, A: t.WeaponAmount(i))).Concat(config.Thrown.Select((x, i) => (x.Name, A: t.ThrownAmount(i)))).Where(x => x.A > 0).Select(x => $"{x.Name} +{x.A}")));
Console.WriteLine($"melee {tuning.Melee?.Weapon ?? "placeholder"}: reach {config.MeleeRange} m, cone ±{config.MeleeConeDegrees}°, vertical ±{config.MeleeVerticalReach} m");
for (int i = 0; i < config.MeleeSteps.Count; i++)
{
    var m = config.MeleeSteps[i];
    string clip = tuning.Melee is { } mt && i < mt.Animations.Count ? $" ({mt.Animations[i]})" : "";
    Console.WriteLine($"  step {i + 1}{clip}: {m.Duration:F3} s, hit {m.HitTime:F3}, combo {m.ComboOpen:F3}-{m.ComboClose:F3}, ranged {m.RangedAt:F3}, move {m.MoveAt:F3}; damage {m.Damage}/{m.SecondaryDamage}, lunge {m.Lunge} m");
}
// Classes and their loadouts (SmContent.MpClasses); without them the config's single default class is used.
var classes = config.Classes;
int defaultClass = Math.Max(0, classes.Select(c => c.Name).ToList().IndexOf("sm - tactical"));
Console.WriteLine($"classes: {classes.Count} ({(tuning.Classes is null ? "placeholder default" : "from the install")}), default {classes[defaultClass].Name}; weapon switch {config.WeaponSwitchTime} s; weapon held up {config.ReadyHold} s after a shot (M32: the owner's rough estimate, unverified)");
for (int i = 0; i < classes.Count; i++)
{
    var c = classes[i];
    string Weapon(int index) => index >= 0 && index < config.Weapons.Count ? config.Weapons[index].Name : "none";
    var info = tuning.Classes?.Classes.FirstOrDefault(k => k.Key == c.Name);
    string role = info is null ? "" : $" [{info.Race}, {info.Role}]";
    Console.WriteLine($"  {i}: {c.Name}{role}: primary {Weapon(c.Primary)}, pistol {Weapon(c.Pistol)}, melee {(c.Melee.Count == 0 ? "none" : $"{c.Melee.Count} steps")}");
    string Choices(WeaponSlot slot) => string.Join(", ", Loadouts.Choices(config, i, slot).Select((w, k) => $"{k} {Weapon(w)}"));
    Console.WriteLine($"     choices: primary [{Choices(WeaponSlot.Ranged)}], pistol [{Choices(WeaponSlot.Pistol)}], melee [{string.Join(", ", c.MeleeChoices.Select((m, k) => $"{k} {m.Wargear} ({m.Steps.Count} steps)"))}], " +
        $"equipment [{string.Join(", ", c.EquipmentChoices.Select((t, k) => $"{k} {config.Thrown[t].Name}"))}]");
}
Console.WriteLine($"throw: release {config.ThrowRelease:F3} s, ends {config.ThrowDuration:F3} s ({(tuning.Classes?.Throw is { } tt ? $"from {tt.Animation}" : "placeholder")})");
// feel/locks-sprint-stop: the owner's shorter action locks (an estimate for review) and the sprint stop.
Console.WriteLine($"action locks: move / jump / evade gates x {config.ActionLockScale:0.##} (estimate, owner's request); sprint stop: " +
    (SprintStop.On(config) ? $"{config.SprintStopTime:F3} s, {SprintStop.Distance(config.SprintSpeed, config):F2} m from {config.SprintSpeed:0.##} m/s (above {SprintStop.Threshold(config):0.##} m/s)" : "off"));
foreach (var t in config.Thrown)
{
    Console.WriteLine($"  thrown {t.Name}{(t.PlayerName is { } shown ? $" (shown as \"{shown}\")" : "")}: {t.SpawnCount} at spawn (initial {t.InitialCount}) of {t.Count}, cooldown {t.Cooldown} s; {(t.Stim is { } st ? StimText(st) : Describe(t.Projectile, config.BlindViewHalfAngle))}");
    if (t.Stim is not null)
        foreach (var v in t.Variants) Console.WriteLine($"    with {string.Join(" + ", v.Upgrades)}: {v.Spec.Count} carried; {(v.Spec.Stim is { } vs ? StimText(vs) : "no stim")}");
    if (t.Projectile.Rigid is { } r)
        Console.WriteLine($"    rigid: ground ≤{r.GroundAngle}° (keeps h {r.GroundHorizontalReduction} v {r.GroundVerticalReduction}), entity keeps h {r.EntityHorizontalReduction} v {r.EntityVerticalReduction}, " +
            $"wall keeps [{string.Join(" ", r.WallHorizontalReduction.Select(q => $"{q.Distance}° from the surface:{q.Value}"))}], no collision for {r.DelayBeforeCollision} s, stop at max bounce {t.Projectile.StopAtMaxBounce}; ground fuse {t.Projectile.GroundLifetime} s");
}
for (int i = 0; i < config.JumpPacks.Count; i++)
{
    var j = config.JumpPacks[i];
    string users = string.Join(", ", classes.Where(c => c.JumpPack == i).Select(c => c.Name));
    Console.WriteLine($"  jump pack {j.Name} ({users}): height by hold [{string.Join(" ", j.HeightMap.Select(q => $"{q.Distance}s:{q.Value}m"))}], " +
        $"cost by hold [{string.Join(" ", j.PowerCost.Select(q => $"{q.Distance}s:{q.Value}"))}] of {j.MaxPower} power (+{j.RechargeRate}/s on the ground); " +
        $"gravity rise {j.JumpingGravity}, hang {j.HangGravity} for {j.HangTime} s, fall {j.FallingGravity}; forward {j.ForwardSpeed} m/s, air gain {j.MoveGain}/{j.DriftGain}");
    Console.WriteLine($"    timing: lift-off {j.TakeOffDelay:F3} s; landing move/jump locks {JumpPack.MoveLock(j, true, config):F3}/{JumpPack.JumpLock(j, true, config):F3} s (moving), " +
        $"{JumpPack.MoveLock(j, false, config):F3}/{JumpPack.JumpLock(j, false, config):F3} s (idle; × the lock scale {config.ActionLockScale}: data {j.LandMoveLock:F3}/{j.LandJumpLock:F3}, {j.LandIdleMoveLock:F3}/{j.LandIdleJumpLock:F3}); pound recovery {j.PoundRecovery:F3} s");
    if (j.HoldTiers) Console.WriteLine($"    three jumps by the hold (feel/jump-pack-2, owner's decision): with a dash, Space let go by the dash's decision tick = the dash, let go before the hold reaches {j.MaxHold:F3} s = the small jump (one apex), held to it = the full jump");
    else if (j.ReleaseCut) Console.WriteLine($"    middle jump (M45): letting go of Space while rising cuts the rise after {j.ReleaseCutDelay:F3} s (decay {j.ReleaseCutDecay:F3} s)");
    string Blast(SplashSpec? s) => s is null ? "none" : $"r {s.Radius} m: {s.Damage.Damage} + {s.Damage.ShieldDamage} shield, fall-off [{string.Join(" ", s.FallOff.Select(q => $"{q.Distance}:{q.Value}"))}], LOS {s.LineOfSight}, ignore self {s.IgnoreSelf}, {s.TargetFilter}";
    Console.WriteLine($"    ground pound {(j.PoundEnabled ? "on melee in flight" : "off")}: after {j.PoundDelay} s, speed {j.PoundSpeed} m/s within {j.PoundLimitAngle}° of down, gravity {j.PoundGravity}; blast {Blast(j.Pound)}" +
        (j.PoundReactions.Count > 0 ? $"; reactions {string.Join(", ", j.PoundReactions.Select(r => $"{r.Reaction} from {r.Intensity}"))}" : ""));
    foreach (var l in j.Landings) Console.WriteLine($"    landing from a drop of {l.MinDrop} m: blast {Blast(l.Blast)}");
}
foreach (var w in config.Weapons)
{
    Console.WriteLine($"  weapon {w.Name}: {w.Slot}, {w.Trigger}, damage {w.Damage}, cooldown {w.ShotCooldown} s, clip {w.ClipSize}/{w.ReserveSize}, reload {w.ReloadTime} s, range {w.MaxRange} m, fall-off {w.FallOffStart}-{w.FallOffEnd} m to x{w.FallOffEndMultiplier}, head x{w.HeadMultiplier}, body x{w.BodyMultiplier}, shield head x{w.ShieldHeadMultiplier}, body x{w.ShieldBodyMultiplier}");
    Console.WriteLine($"    raise (M31): {(w.AlwaysReady ? "always ready, never waits" : w.RaiseDelay > 0f ? $"{w.RaiseDelay:F3} s ({Combat.RaiseTicksOf(w, config)} ticks) when down" : "no data, never waits")}");
    if (w.Pierce is { } pierce)
        Console.WriteLine($"    pierces props (M39): {Pierce(pierce)}");
    foreach (var s in w.Charge?.Stages.Where(s => s.Shot?.Pierce is not null) ?? [])
        Console.WriteLine($"    charged {s.Name} pierces props (M39): {Pierce(s.Shot!.Pierce!)}");
    if (w.Heat is { } heat)
        Console.WriteLine($"    heat: {heat.PerShot}/shot, overheat {heat.Overheat}, high {heat.HighHeat}, recovery {heat.Recovery}, cool {heat.CoolRate}/s after {heat.CoolDelay} s ({heat.CoolDelayAfterOverheat} s after an overheat)");
    if (w.Projectile is { } basic)
        Console.WriteLine($"    projectile: {Describe(basic)}; {w.AmmoPerShot} ammo per shot{(Shots.RemoteDetonated(w) ? $"; remote detonation (reload), delays [{string.Join(", ", w.DetonationDelays)}] s" : "")}");
    if (w.Cone is { } cone)
        Console.WriteLine($"    cone: length {cone.Length} m, angle {cone.Angle}° (half {cone.Angle / 2}°), apex {cone.StartLength} m behind; {cone.Damage.Damage} + {cone.Damage.ShieldDamage} shield; " +
            $"fall-off [{string.Join(" ", cone.FallOff.Select(q => $"{q.Distance}:{q.Value}"))}], coverage [{string.Join(" ", cone.CoverageFallOff.Select(q => $"{q.Distance}:{q.Value}"))}]");
    if (w.Recharge is { } recharge)
        Console.WriteLine($"    recharging clip: {recharge.PerSecond}/s after {recharge.Delay} s ({recharge.RoundTime} s per round)");
    if (w.Charge is { } charge)
    {
        Console.WriteLine($"    charge on Fire, held (tap: the basic shot; {charge.Ability}): needs {charge.AmmoToStart} ammo, min time x{charge.MinTimeMultiplier}, reload {(charge.ReloadCancels ? "cancels" : "ignored")}");
        for (int k = 0; k < charge.Stages.Count; k++)
        {
            var st = charge.Stages[k];
            string shot = st.Shot is not { } x ? "no shot"
                : x.Delivery == DeliveryKind.Raycast ? $"ray {x.Damage?.Damage} (range {x.MaxRange} m)"
                : $"projectile {Describe(x.Projectile!)}";
            if (st.Shot is { } y) shot += $", {y.AmmoCost} ammo, +{y.HeatPerShot} heat, delay {y.FireDelay} s, cooldown {y.ShotCooldown} s";
            Console.WriteLine($"      stage {k + 1} {st.Name} from {st.MinTime} s: +{st.HeatPerSecond} heat/s, move x{st.MoveSpeedMultiplier}; {shot}");
        }
    }
}

static string Pierce(PierceSpec p) =>
    $"{p.Points} points, {p.PropCost} per prop" + (p.Exit is { } e
        ? $"; past each: cone {e.Length} m, {e.Angle}°, {e.Damage.Damage} damage (head x{e.Damage.HeadMultiplier}, body x{e.Damage.BodyMultiplier}){(p.ExitHitsAllies ? ", allies too" : "")}"
        : "; nothing is hit past a prop");

// perks/inert-2: combat stims in the startup listing.
static string StimText(StimSpec s) =>
    $"combat stims (nothing thrown): use {s.UseRelease:F3} s to the effect, {s.UseDuration:F3} s in all; lasts {s.Lifetime} s; health-only damage taken ranged x{s.ReceivedRanged}, melee x{s.ReceivedMelee}, " +
    $"explosive x{s.ReceivedExplosive}; melee damage dealt x{s.MeleeDamage}; {(s.ReactionImmune ? "no hit reactions" : "reactions as usual")}";

static string Describe(ProjectileSpec p, float blindView = 180f)
{
    string blast = p.AllSplashes.Any()
        ? string.Join("; ", p.AllSplashes.Select(s => s.BlindOnly
            ? $"blinding blast r {s.Radius} m: blind {s.Blindness} s (line of sight to the eye; " + (blindView < 180f ? $"only players facing it, within {blindView}° of their view: owner's change from SM1, which blinds by distance" : "whatever the facing: SM1's rule") + "; no damage, no reaction)"
            : $"blast r {s.Radius} m: {s.Damage.Damage} + {s.Damage.ShieldDamage} shield{(s.Lingers ? $" every {s.Period} s for {s.Lifetime} s" : "")}"))
        : "no blast";
    string direct = p.DetonateDamage is { } d ? $", direct {d.Damage} on detonation" : "";
    return $"{p.Blueprint.Split('\\', '/')[^1]} speed {p.Speed} m/s, lifetime {p.Lifetime} s, {(p.DetonateOnCollision ? "detonates on impact" : p.Sticky ? "sticky" : "rests")}, " +
        $"{p.MaxBounces} bounces{(p.MaxActive > 0 ? $", max {p.MaxActive} active" : "")}; {blast}{direct}";
}
var available = MapContent.Available(install);
mapName ??= available.Contains("mp_tdm_09") ? "mp_tdm_09" : "none";
GameMap? map = null;
IReadOnlyList<MapBlocker> mapBlockers = [];
if (mapName != "none")
{
    if (!available.Contains(mapName)) { Console.Error.WriteLine($"unknown map '{mapName}'; installed: {string.Join(", ", available)}"); return 1; }
    var content = MapContent.Load(install, mapName);
    mapBlockers = content.Blockers;
    var collision = content.ToCollisionWorld();
    map = new GameMap(mapName, collision, content.SpawnPoints(), content.Hash, content.SimControlPoints(), content.SimStartAreas(), content.SimBlockers());
    Console.WriteLine($"map {mapName} ({content.Region}): {collision.Character.TriangleCount} character and {collision.Weapons.TriangleCount} weapon triangles, {content.Spawns.Count} spawns; map hash {content.Hash:X16}");
    Console.WriteLine($"  characters collide with layers {string.Join(" + ", MapContent.CharacterLayers)}, shots with layer {MapContent.WeaponLayer}");
    if (content.Props.Count > 0)
        Console.WriteLine($"  props (solid for characters and shots; pierced by {string.Join(", ", config.Weapons.Where(w => w.Pierce is not null || w.Charge?.Stages.Any(s => s.Shot?.Pierce is not null) == true).Select(w => w.Name).DefaultIfEmpty("no weapon"))}): " +
            string.Join(", ", content.Props.GroupBy(p => p.Definition).Select(g => $"{g.Count()} {g.Key}")));
    foreach (var k in content.KillRegions)
        Console.WriteLine($"  kill region {k.Name}: centre {k.Position.X:0.##},{k.Position.Y:0.##},{k.Position.Z:0.##}, box {k.Scale.X:0.##} x {k.Scale.Y:0.##} x {k.Scale.Z:0.##} m, turned {k.Yaw * 180f / MathF.PI:0.#} deg");
    for (int i = 0; i < content.ControlPoints.Count; i++)
    {
        var p = content.ControlPoints[i];
        Console.WriteLine($"  point {Letter(i)} = {p.Name}: centre {p.Position.X:0.##},{p.Position.Y:0.##},{p.Position.Z:0.##}, radius {p.Scale.X / 2f:0.##} m, height {p.Scale.Z:0.##} m (marker scale {p.Scale.X:0.##} x {p.Scale.Y:0.##} x {p.Scale.Z:0.##})");
    }
    foreach (var a in content.StartAreas)
        Console.WriteLine($"  start area {a.Side} = {a.Name}: centre {a.Position.X:0.##},{a.Position.Y:0.##},{a.Position.Z:0.##}, {(a.Shape == 3 ? "cylinder" : "box")} {a.Scale.X:0.##} x {a.Scale.Y:0.##} x {a.Scale.Z:0.##} m, facing {a.Yaw * 180f / MathF.PI:0.#} deg");
    foreach (var b in content.Blockers)
        Console.WriteLine($"  spawn-room blocker {b.Side} = {b.Name}: centre {b.Position.X:0.##},{b.Position.Y:0.##},{b.Position.Z:0.##}, box {b.Scale.X:0.##} x {b.Scale.Y:0.##} x {b.Scale.Z:0.##} m, turned {b.Yaw * 180f / MathF.PI:0.#} deg; " +
            $"solid for {(b.Side == Team.SpaceMarines ? Team.Chaos : Team.SpaceMarines)}, shots pass");
    foreach (var note in content.Notes) Console.WriteLine("note: " + note);
}
if (mode == GameMode.SeizeGround && (map?.ControlPoints?.Count ?? 0) == 0)
{
    Console.Error.WriteLine($"Seize Ground needs a map with control points; '{mapName}' has none");
    return 1;
}
// Annihilation: the sides and the scripts' rule names from the install; limits and friendly fire provisional host options.
var modes = GameModes.Load(install);
Console.WriteLine($"teams: {string.Join(", ", modes.Teams.Select(t => $"{t.Key} = {t.Side} ({t.Blueprint.Split('\\', '/')[^1]})"))}; " +
    $"class sides: {string.Join(", ", classes.Select(c => $"{c.Name} {c.Side}"))}");
Console.WriteLine($"deathmatch script {GameModes.ObjectivesScript}: {modes.ScriptMarkersFound.Count}/{GameModes.ScriptMarkers.Count} rule names found ({string.Join(", ", modes.ScriptMarkersFound)})");
Console.WriteLine($"Seize Ground script names: {modes.SeizeGroundMarkersFound.Count}/{GameModes.SeizeGroundMarkers.Count} found");
foreach (var note in modes.Notes) Console.WriteLine("note: " + note);
MatchRules? match = teams ? rules : null;
Console.WriteLine(match is null
    ? "mode: free-for-all (no teams, no score limit)"
    : match.Mode == GameMode.SeizeGround
    ? $"mode: Seize Ground (domination): a neutral point is owned after {match.CaptureTime} s with only one side on it, the other side's point turns neutral after {match.NeutraliseTime} s " +
      $"(domination captureDuration / decapDuration), contested points freeze; each owned point scores {match.PointScore} every {match.ScoreInterval} s (scoreSpeed; interval provisional); deaths do not score; " +
      $"score limit {match.ScoreLimit} (provisional), time limit {(match.TimeLimit > 0f ? $"{match.TimeLimit / 60f:0.##} min" : "none")} (provisional), friendly fire {(match.FriendlyFire ? "on" : "off")}, " +
      $"respawn {config.RespawnTime} s (placeholder) in the side's start area, restart {match.RestartDelay} s after the end (ours)"
    : $"mode: Annihilation (team deathmatch): each death scores 1 for the other side (suicides and team kills too); score limit {match.ScoreLimit} (provisional), " +
      $"time limit {(match.TimeLimit > 0f ? $"{match.TimeLimit / 60f:0.##} min" : "none")} (provisional), friendly fire {(match.FriendlyFire ? "on" : "off")} (provisional default off), " +
      $"respawn {config.RespawnTime} s (placeholder), restart {match.RestartDelay} s after the end (ours), at most {match.MaxPerSide} per side, warm-up until both sides have a player, then a {(match.CountdownTime > 0f ? $"{match.CountdownTime:0.##} s" : "no")} match-begins countdown without attacks (ours); " +
      $"spawns: {(map is null ? "placeholder circle" : SpawnsPerSide(map))}");
UdpServerTransport transport;
try { transport = new UdpServerTransport(new IPEndPoint(bind, port)); }
catch (System.Net.Sockets.SocketException e) when (e.SocketErrorCode is System.Net.Sockets.SocketError.AddressAlreadyInUse or System.Net.Sockets.SocketError.AccessDenied)
{
    // Exit code 3: the port is taken (another server, on this address or every interface); nothing was started.
    Console.Error.WriteLine($"smserver: UDP port {port} on {bind} is already in use ({e.SocketErrorCode}); stop the other server or pick another --port");
    return 3;
}
catch (System.Net.Sockets.SocketException e) when (e.SocketErrorCode == System.Net.Sockets.SocketError.AddressNotAvailable)
{
    Console.Error.WriteLine($"smserver: cannot listen on {bind}: no network interface of this machine has that address; use one of its addresses, 127.0.0.1, or leave --bind out");
    return 3;
}
using var listening = transport;
if (!bind.Equals(IPAddress.Any)) Console.WriteLine($"listening on {bind} only (--bind)");
var clock = new SystemClock();
// Custom appearances (protocol 22): the catalogue's ranges, read without the locale or a blueprint scan.
IAppearanceRules? appearanceRules = null;
try
{
    var catalogue = CustomizationCatalog.LoadLimits(install);
    var marine = catalogue.LimitsFor(CustomizationCatalog.SpaceMarine);
    var chaosLimits = catalogue.LimitsFor(CustomizationCatalog.Chaos);
    appearanceRules = new CatalogAppearanceRules(marine, chaosLimits);
    string Sizes(AppearanceLimits l) => $"{l.PredefinedSets} sets, pieces per slot [{string.Join(" ", l.PiecesPerSlot)}], {l.Palette} colours, {l.Badges} badges";
    Console.WriteLine($"appearances: Space Marines {Sizes(marine)}; Chaos {Sizes(chaosLimits)}; Orks {Races.OrkLooks} looks (no armour, colour or emblem); custom emblems {(customEmblems ? "on" : "off (--no-custom-emblems)")}; unlocks {unlocks.ToString().ToLowerInvariant()}");
}
catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
{
    Console.WriteLine($"appearances: catalogue unreadable ({e.Message}); codes are checked for their structure only");
}
string? buildHint = ServerCommandLine.BuildHint(install);
var server = new GameServer(config, fingerprint, transport, clock, new ServerOptions
{
    DefaultClass = defaultClass, Match = match, AppearanceRules = appearanceRules, CustomEmblems = customEmblems, Unlocks = unlocks,
    MaxPlayersPerAddress = cli.MaxPerIp, JoinInterval = cli.JoinInterval, IdleKick = cli.IdleKick, BanFile = cli.BanFile, BuildHint = buildHint, LimitLoopback = cli.LimitLoopback,
    BotDamageScale = cli.BotDamageScale,   // test aid (fix/hitboxes-grenades-bots)
    NameSeed = cli.NameSeed,               // names/bot-lore
}, map: map);
server.Log += line => Console.WriteLine("note: " + line);
Console.WriteLine($"names: test bots are named from per-race pools (ours), seed {server.NameSeed}{(cli.NameSeed is null ? " (random; --name-seed fixes it)" : " (--name-seed)")}; other players show as P<id>");
if (cli.BotDamageScale != 1f) Console.WriteLine($"--bot-damage-scale {cli.BotDamageScale:0.###}: every hit a test bot (client --bot) deals is scaled by it (test aid, not the original)");
// Joins and leaves with addresses, refusals, bans, idle kicks, caught faults, skipped ticks (UTC time for the operator).
server.OperatorLog += line => Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}] {line}");
Console.WriteLine($"guards (ours): {(cli.MaxPerIp > 0 ? $"at most {cli.MaxPerIp} players per IP address" : "no per-address cap")}, " +
    $"{(cli.JoinInterval > TimeSpan.Zero ? $"one join per {cli.JoinInterval.TotalSeconds:0.#} s per address" : "no join rate limit")}, " +
    $"{(cli.IdleKick > TimeSpan.Zero ? $"idle kick after {cli.IdleKick.TotalSeconds:0.#} s" : "no idle kick")}, " +
    $"{(cli.BanFile is { } banFile ? $"bans from {banFile}" : "no ban file")}; loopback {(cli.LimitLoopback ? "limited too (--limit-loopback)" : "exempt from the per-address limits and the idle kick")}; refusals name {buildHint ?? "the content fingerprint only (no Steam manifest found)"}");
if (match is not null)
{
    var middle = server.World.MapMiddle;
    for (int i = 0; i < server.World.StartAreas.Count; i++)
    {
        // Every spawn of this side (first, respawn, round start) is at one of these spots, facing as listed (yaw: 0 = +Y,
        // degrees): out of the spawn room toward its exit (M22). "clear": metres of open view along the facing at eye height.
        var spots = server.World.StartAreaSpots(i);
        var facing = server.World.StartAreaFacings(i);
        string exit = facing.Exit is { } e ? $"exit {e.X:0.#},{e.Y:0.#},{e.Z:0.##}" : "no exit";
        Console.WriteLine($"  {server.World.StartAreas[i].Side} spawn spots: {spots.Count}, ground z {spots.Min(q => q.Z):0.##}..{spots.Max(q => q.Z):0.##}; facing from {facing.Source} ({facing.Note}), {exit}");
        var clear = spots.Select((q, k) => ClearView(map, q, facing.Yaws[k], config.EyeHeight)).ToList();
        Console.WriteLine($"    [{string.Join(" ", spots.Select((q, k) => $"{q.X:0.#},{q.Y:0.#},{q.Z:0.##} facing {facing.Yaws[k] * 180f / MathF.PI:0.#} clear {clear[k]:0.#}"))}]");
        if (middle is { } m)
        {
            var toMiddle = spots.Select(q => MathF.Atan2(-(m.X - q.X), m.Y - q.Y)).ToList();
            var oldClear = spots.Select((q, k) => ClearView(map, q, toMiddle[k], config.EyeHeight)).ToList();
            Console.WriteLine($"    clear view {clear.Min():0.#}..{clear.Max():0.#} m; the old facing at point B ({m.X:0.#},{m.Y:0.#}) was {toMiddle.Min() * 180f / MathF.PI:0.#}..{toMiddle.Max() * 180f / MathF.PI:0.#} deg, clear {oldClear.Min():0.#}..{oldClear.Max():0.#} m");
        }
    }
}
// A spawn inside a kill volume would kill and respawn a player there every tick: warn at start-up.
if (map is not null && map.Collision.KillVolumes.Count > 0)
{
    var places = server.World.Spawns.Select((s, i) => ($"spawn {i + 1}", s.Position))
        .Concat(Enumerable.Range(0, server.World.StartAreas.Count).SelectMany(i => server.World.StartAreaSpots(i).Select(q => ($"{server.World.StartAreas[i].Side} start spot", q))))
        .Concat((map.ControlPoints ?? []).Select((p, i) => ($"point {Letter(i)}", p.Center)));
    int bad = 0;
    foreach (var (what, at) in places)
        if (map.Collision.KillVolumes.Any(k => k.Contains(at) || k.Contains(at + new System.Numerics.Vector3(0, 0, 1f))))
        { Console.Error.WriteLine($"warning: {what} at {at.X:0.#},{at.Y:0.#},{at.Z:0.##} is inside a kill volume"); bad++; }
    Console.WriteLine($"  kill volumes: {map.Collision.KillVolumes.Count}; spawns, start spots and points inside one: {bad}");
}
server.PlayerJoined += p => Console.WriteLine($"{server.Describe(p.PlayerId)} joined{(p.Team != Team.None ? $" the {p.Team}" : "")}: {Loadouts.Describe(config, p)}");
var lastState = MatchState.None;
// Spawn-room blockers: a line when a player first presses against the other side's blocker (and again after leaving it), and a
// warning when one gets inside it (it should not; once per stay, like the contact line).
var blockerNames = mapBlockers.Select(b => (Name: b.Name, Box: b.ToSim())).ToArray();
var pressing = new HashSet<(int Player, int Blocker)>();
var insideBox = new HashSet<(int Player, int Blocker)>();
server.Stepped += world =>
{
    // --pickup-test: test drops in front of every living player (the server's own SpawnPickup; the ground under the point as for a death).
    if (pickupTest > 0f && world.Config.Pickups is { } drops && world.Tick % (uint)Math.Max(1, MathF.Round(pickupTest * world.Config.TickRate)) == 0)
        foreach (var p in world.Players.Values.Where(p => p.Alive).ToList())
            for (int t = 0; t < drops.Types.Count; t++)
            {
                float side = (t - (drops.Types.Count - 1) / 2f) * 1.2f, yaw = p.Yaw;
                var at = p.Position + new System.Numerics.Vector3(-MathF.Sin(yaw) * 2.5f + MathF.Cos(yaw) * side, MathF.Cos(yaw) * 2.5f + MathF.Sin(yaw) * side, 0f);
                var ground = map?.Collision.Character.Raycast(at + new System.Numerics.Vector3(0, 0, 1.5f), new System.Numerics.Vector3(0, 0, -1), 4f)?.Point ?? at;
                world.SpawnPickup(t, ground, 0);
            }
    for (int k = 0; k < blockerNames.Length; k++)
    {
        var (name, box) = blockerNames[k];
        float bottom = box.Center.Z - box.Size.Z / 2f, top = box.Center.Z + box.Size.Z / 2f;
        foreach (var p in world.Players.Values)
        {
            bool spans = p.Position.Z < top && p.Position.Z + world.Config.CapsuleHeight > bottom;
            bool against = p.Alive && box.Blocks(p.Team) && spans && box.HorizontalDistance(p.Position) <= world.Config.CapsuleRadius + 0.05f;
            if (against && pressing.Add((p.PlayerId, k)))
                Console.WriteLine($"tick {world.Tick}: player {p.PlayerId} ({p.Team}) held at {name} at {p.Position.X:F2},{p.Position.Y:F2},{p.Position.Z:F2} " +
                    $"({box.HorizontalDistance(p.Position):F3} m from the box, capsule radius {world.Config.CapsuleRadius})");
            else if (!against) pressing.Remove((p.PlayerId, k));
            bool inside = p.Alive && box.Blocks(p.Team) && box.Contains(p.Position + new System.Numerics.Vector3(0, 0, world.Config.CapsuleHeight / 2f));
            if (inside && insideBox.Add((p.PlayerId, k)))   // once per stay inside
                Console.Error.WriteLine($"warning: tick {world.Tick}: player {p.PlayerId} ({p.Team}) is inside {name} at {p.Position.X:F2},{p.Position.Y:F2},{p.Position.Z:F2}");
            else if (!inside) insideBox.Remove((p.PlayerId, k));
        }
    }
    foreach (var k in world.LastKills)
    {
        string how = k.Cause switch
        {
            KillCause.Weapon when k.Index >= 0 && k.Index < world.Config.Weapons.Count => world.Config.Weapons[k.Index].Name,
            KillCause.Thrown when k.Index >= 0 && k.Index < world.Config.Thrown.Count => world.Config.Thrown[k.Index].Name,
            KillCause.Melee => "melee",
            KillCause.JumpPack => "jump pack",
            KillCause.Perk when k.Index >= 0 && k.Index < world.Config.Perks.Count => Perks.NameOf(world.Config.Perks[k.Index]),
            KillCause.World => "the level",
            _ => k.Cause.ToString(),
        };
        string who = k.Killer == 0 ? "" : k.Killer == k.Victim ? " (suicide)" : $" by {server.Describe(k.Killer)}";
        Console.WriteLine($"tick {k.Tick}: {server.Describe(k.Victim)} killed{who} with {how}; score marines {world.ScoreOf(Team.SpaceMarines)}, chaos {world.ScoreOf(Team.Chaos)}");
    }
    foreach (var c in world.LastCaptures)
        Console.WriteLine($"tick {c.Tick}: point {Letter(c.Point)} ({world.ControlPoints[c.Point].Name}) {(c.Kind == CaptureKind.Captured ? "captured" : "neutralised")} by {c.Side}; " +
            $"owned: marines {world.OwnedBy(Team.SpaceMarines)}, chaos {world.OwnedBy(Team.Chaos)}; score marines {world.ScoreOf(Team.SpaceMarines)}, chaos {world.ScoreOf(Team.Chaos)}");
    if (world.State != lastState)
    {
        Console.WriteLine($"tick {world.Tick}: match {world.State}" + (world.State == MatchState.Ended
            ? $", winner {(world.Winner == Team.None ? "none (draw)" : world.Winner.ToString())}, marines {world.ScoreOf(Team.SpaceMarines)} - chaos {world.ScoreOf(Team.Chaos)}" : ""));
        lastState = world.State;
    }
    foreach (var shot in world.LastShots.Where(x => x.Stage > 0))
    {
        var weapon = world.Config.Weapons[shot.Weapon];
        Console.WriteLine($"tick {shot.Tick}: player {shot.Shooter} fired {weapon.Name} charged ({weapon.Charge!.Stages[shot.Stage - 1].Name}){(shot.Projectile > 0 ? $", projectile #{shot.Projectile}" : "")}");
    }
    // Jump-pack lines only for pound impacts, sword-slash landings (protocol 30) and landings that hit someone (lift-offs, dives and plain landings are not logged).
    foreach (var j in world.LastJumps.Where(x => x.Kind is JumpEventKind.Impact or JumpEventKind.SlashLand || (x.Kind == JumpEventKind.Land && x.Victims > 0)))
    {
        var who = world.Players.GetValueOrDefault(j.Player);
        string what = j.Kind switch
        {
            JumpEventKind.Launch => $"lifted off ({who.JumpPower:F0} power left)",
            JumpEventKind.Dive => who.JumpSlash ? "started a sword slash" : "started a ground pound",
            JumpEventKind.Land => $"landed after a {j.Drop:F1} m drop",
            JumpEventKind.SlashLand => $"sword slash landed after a {j.Drop:F1} m drop (its cone hits {Loadouts.JumpPack(world.Config, who)?.Slash?.HitTime ?? 0f:F3} s later; the regular landing's blast)",
            _ => $"ground pound impact after a {j.Drop:F1} m drop",
        };
        string dealt = j.Kind is JumpEventKind.Land or JumpEventKind.Impact or JumpEventKind.SlashLand
            ? $": {j.Victims} hit, {j.ShieldDamage:F0} shield + {j.HealthDamage:F0} health damage, {j.Kills} kills" : "";
        Console.WriteLine($"tick {j.Tick}: player {j.Player} {what} at {j.Position.X:F1},{j.Position.Y:F1},{j.Position.Z:F1}{dealt}");
    }
    foreach (var g in world.LastBlindings.GroupBy(b => b.Projectile))
        Console.WriteLine($"tick {world.Tick}: blind grenade #{g.Key} of player {g.First().Source} blinded {string.Join(", ", g.GroupBy(b => b.Target).Select(t => t.Last()).Select(b => $"{b.Target} at {b.Distance:F1} m ({b.Ticks * world.Config.TickSeconds:F1} s)"))}");
    foreach (var t in world.LastThrows)
        Console.WriteLine($"tick {t.Tick}: player {t.Thrower} threw {world.Config.Thrown[t.Item].Name} (#{t.Projectile}), {world.Players.GetValueOrDefault(t.Thrower).Grenades} left");
    foreach (var g in world.LastHits.Where(h => h.Cone).GroupBy(h => h.Shooter))
        Console.WriteLine($"tick {world.Tick}: player {g.Key} cone hit {string.Join(", ", g.Select(h => $"{h.Target} at {h.Distance:F1} m ({h.ShieldDamage:F0} shield + {h.HealthDamage:F0} health{(h.Killed ? ", killed" : "")})"))}");
    foreach (var d in world.LastDetonations)
        Console.WriteLine($"tick {d.Tick}: projectile #{d.Projectile} ({(d.Thrown ? world.Config.Thrown[d.Weapon].Name : world.Config.Weapons[d.Weapon].Name)}{(d.Stage > 0 ? " charged" : "")}) of player {d.Owner} detonated at " +

            $"{d.Position.X:F1},{d.Position.Y:F1},{d.Position.Z:F1}: {d.Victims} hit, {d.ShieldDamage:F0} shield + {d.HealthDamage:F0} health damage, {d.Kills} kills");
};
Console.WriteLine($"smserver on UDP {port}; protocol {Protocol.Version}; content {fingerprint:X16}; config {config.Hash():X16}; {config.TickRate} Hz");
Console.CancelKeyPress += (_, e) => { e.Cancel = true; Environment.Exit(0); };
var lastReport = TimeSpan.Zero;
// GameServer.Update isolates packet handlers and ticks itself (hardening M3); a fault anywhere else in the loop is logged and the
// loop goes on. A crash of the process is for a restart supervisor (docs/dedicated-server.md).
while (true)
{
    try
    {
        server.Update();
        if (server.FatalError is { } fatal)
        {
            // Exit code 4: the simulation keeps failing (ServerOptions.MaxConsecutiveTickFaults); a supervisor restarts the server.
            Console.Error.WriteLine($"[{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}] FATAL: {fatal}; exiting with code 4 for a restart");
            return 4;
        }
        if (clock.Now - lastReport > TimeSpan.FromSeconds(10))
        {
            var w = server.World;
            Console.WriteLine($"tick {w.Tick}, players {server.Players.Count}, rejected {server.Rejected}" + (w.Rules is null ? "" : $"; {w.State}, marines {w.ScoreOf(Team.SpaceMarines)} - chaos {w.ScoreOf(Team.Chaos)}") +
                (w.Rules?.Mode == GameMode.SeizeGround ? $"; points {string.Join(" ", w.Points.Select((p, i) => $"{Letter(i)}:{(p.Owner == Team.None ? "-" : p.Owner == Team.SpaceMarines ? "SM" : "CSM")}"))}" : "") +
                (server.Faults + server.SkippedTicks + server.IdleKicked + server.JoinsDeferred + server.BannedPackets > 0
                    ? $"; faults {server.Faults}, skipped ticks {server.SkippedTicks}, idle kicks {server.IdleKicked}, joins deferred {server.JoinsDeferred}, banned packets {server.BannedPackets}" : ""));
            lastReport = clock.Now;
        }
    }
    catch (Exception e) when (e is not OutOfMemoryException)
    {
        Console.Error.WriteLine($"[{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}] server loop error: {e}");
        Thread.Sleep(100);   // a fault that repeats every pass must not flood the log at full speed
    }
    Thread.Sleep(1);
}

// Metres of open view from a spot along a facing, at eye height, against the character collision (up to 100 m).
static float ClearView(GameMap? map, System.Numerics.Vector3 spot, float yaw, float eye)
{
    if (map is null) return float.PositiveInfinity;
    var dir = new System.Numerics.Vector3(-MathF.Sin(yaw), MathF.Cos(yaw), 0f);
    return map.Collision.Character.Raycast(spot + new System.Numerics.Vector3(0, 0, eye), dir, 100f)?.Distance ?? 100f;
}

// Per side (M22): a side with a start area spawns there (World.TryStartAreaSpawn); a side without one uses the shared markers.
static string SpawnsPerSide(GameMap map) => string.Join("; ", new[] { Team.SpaceMarines, Team.Chaos }.Select(side =>
    (map.StartAreas ?? []).Any(a => a.Side == side)
        ? $"{side} in its start area (spawn room), facing out of the room toward its exit (M22)"
        : $"{side} at the {map.Spawns.Count} shared tdm markers, farthest from enemies (provisional; no start area)"));

static string Letter(int index) => ((char)('A' + index)).ToString();
