using System;
using System.Collections.Generic;
using Godot;
using SmNet;
using SmSim;
using V3 = System.Numerics.Vector3;
using V2 = System.Numerics.Vector2;

namespace SmClient;

/// <summary>
/// bot-grenade-safety (owner, 2026-11-11; Classic PvP bots; rules engine-free in <see cref="BotGrenadeSafety"/>, **ours**): the bots no longer blow themselves up with their own
/// grenades. The throw needs the target and the predicted landing point beyond the item's blast radius + margin (3D) and aims at that landing point (a throw under way keeps the
/// farther of the press's and the current landing point); while the bot's own grenade (or waiting launcher round) is out it does not jump, pound, charge, melee or walk into its
/// blast, backing out when inside; remote rounds are not set off with the bot inside their reach; no throw at a target in the air. The own grenade is the first new thrown projectile
/// the snapshots show after the bot's own release (snapshots carry no owner); while it flies (a frag's fuse starts on the ground) its predicted landing point stands for it (and its
/// own place once it has left the bot's reach: a bounce back off a wall), once down (resting, stuck or slow) its own place. No deadlock: a hazard lasts at most
/// <see cref="BotGrenadeSafety.MaxHazardSeconds"/> s, and a back-out that makes no progress for <see cref="BotGrenadeSafety.GiveUpSeconds"/> s gives up (the blasts that hold the bot
/// are left out until it is out of them). A splash weapon held at close range for <see cref="BotGrenadeSafety.PistolAfterSeconds"/> s gives way to a loaded pistol.
/// <c>--old-bot-throws</c> restores the old throws (6 m horizontal minimum, the aim 8 m short of the target) and drops every hold.
/// </summary>
public partial class NetworkView
{
    bool? _oldBotThrows;
    /// <summary><c>--old-bot-throws</c>: the throws, aim and detonations as before bot-grenade-safety, no holds.</summary>
    bool OldBotThrows => _oldBotThrows ??= CmdFlag(BotGrenadeSafety.RestoreFlag);

    /// <summary>Whether this bot plays by the grenade safety rules (a Classic PvP bot without <c>--old-bot-throws</c>).</summary>
    bool GrenadeSafetyOn => _bot && _config is not null && BotGrenadeSafety.Applies(MatchExtras, OldBotThrows);

    /// <summary>One of the bot's own live thrown grenades: its snapshot id once claimed (-1 before), where it is, its reach and when it was released.</summary>
    sealed class OwnGrenade
    {
        public int Id = -1;
        public V3 Centre, Landing, ReleasedFrom;
        /// <summary>Whether the throw's predicted landing point is known (it stands for the grenade while it flies).</summary>
        public bool HasLanding;
        /// <summary>Whether the grenade is down (resting, stuck to a player, or slower than about 10 m/s): from then on it stands where it is; in flight it cannot go off yet.</summary>
        public bool Down;
        /// <summary>Whether the claimed grenade has been outside the bot's reach since the release: in flight its own place counts from then on (a bounce back off a wall).</summary>
        public bool Cleared;
        public V3 SamplePosition;
        public double SampleAt = double.NegativeInfinity;
        public float Reach, Speed;
        public double ReleasedAt;
        public HashSet<int> KnownAtRelease = new();
    }

    readonly List<OwnGrenade> _ownGrenades = new();
    readonly List<BotGrenadeSafety.Hazard> _hazards = new();
    /// <summary>The newest snapshot's projectiles, copied once per bot tick when something is out (reused: no list per tick).</summary>
    readonly List<ProjectileView> _botLive = new();
    /// <summary>Waiting rounds of the bot's own weapons by snapshot id: when first seen, the <see cref="_hazardStamp"/> of the last tick that saw them and whether their cap was logged.</summary>
    readonly Dictionary<int, (double FirstSeen, int Stamp, bool Expired)> _roundSeen = new();
    readonly List<int> _roundStale = new();
    int _hazardStamp;
    /// <summary>Weapon index: whether it can leave a round waiting with a blast to come (<see cref="BotGrenadeSafety.LeavesWaitingRounds"/>; the early-out's test).</summary>
    readonly Dictionary<int, bool> _waitingRoundWeapon = new();
    static readonly WeaponSlot[] HazardSlots = [WeaponSlot.Ranged, WeaponSlot.Second, WeaponSlot.Pistol];
    /// <summary>The landing point the bot's throw under way aims at (null: no throw planned, or the old aim).</summary>
    V3? _botThrowAim;
    /// <summary>The landing point checked at the throw's press (safe): a throw under way never aims nearer than it (<see cref="BotGrenadeSafety.FartherLanding"/>).</summary>
    V3? _botThrowPressAim;
    double _botSafetyLogAt = double.NegativeInfinity;
    BotGrenadeSafety.HoldKind _lastHold;
    /// <summary>The back-out under way (null: none) and whether the bot gave one up (cornered): the blasts holding it are then left out until it is out of them.</summary>
    BotGrenadeSafety.BackOutProgress? _botBackOut;
    bool _botGaveUp;
    /// <summary>Whether this tick's bot input came from added content's own bot (Musou Mode): the hold leaves it alone.</summary>
    bool _botExtrasTick;
    int _throwsRefused, _holds, _backOuts, _jumpsHeld, _poundsHeld, _chargesHeld, _meleeHeld, _detonationsHeld, _ownClaimed, _giveUps, _hazardsExpired, _splashPistols, _takeOffsHeld;

    /// <summary>The bot's chest (game space): its feet plus the class's hit-volume centre height, as a blast measures it.</summary>
    V3 BotChest(PlayerState s) => s.Position + new V3(0, 0, HitVolumes.CentreHeight(_config!.For(s.Class)));

    double _botLastDetonationHold = double.NegativeInfinity, _botLastJumpHold = double.NegativeInfinity, _botLastPoundHold = double.NegativeInfinity,
        _botLastChargeHold = double.NegativeInfinity, _botLastMeleeHold = double.NegativeInfinity;

    /// <summary>The bot's item and its safety distance when the throw rule applies (a Classic PvP bot, a damaging item), else null (the old rule).</summary>
    (ThrownSpec Item, float Safety)? BotThrowSafety(PlayerState me)
    {
        if (!GrenadeSafetyOn || Loadouts.Equipment(_config!, me) is not { } item) return null;
        float safety = BotGrenadeSafety.SafetyDistance(item, _config!.ThrownBlastScale(item));
        return safety > 0f ? (item, safety) : null;
    }

    /// <summary>The landing point a throw at <paramref name="target"/> aims at (pressed now or under way), or null for the old aim (no safety rule for the item).</summary>
    V3? BotThrowLanding(PlayerState me, PlayerState target)
    {
        if (StimOf(me) is not null || BotThrowSafety(me) is not { } safe) return null;
        BotThrowCheck(me, target, safe.Item, safe.Safety, out var landing);
        return landing;
    }

    /// <summary>
    /// The aim of a throw pressed now or under way (Classic PvP, a damaging item): the landing point now, or the press's when that is farther from the bot's chest (the target came
    /// closer since the press: the aim does not follow it in). Null for the old aim.
    /// </summary>
    V3? BotThrowAimPoint(PlayerState me, PlayerState target)
    {
        if (BotThrowLanding(me, target) is not { } now) return null;
        return _botThrowPressAim is { } pressed ? BotGrenadeSafety.FartherLanding(BotChest(me), pressed, now) : now;
    }

    /// <summary>Counts and logs a held action at most once a second per kind.</summary>
    void BotNoteHeld(ref int count, ref double lastAt, string text)
    {
        if (!BotHoldLogDue(lastAt)) return;
        count++; lastAt = _connectedFor;
        BotSafetyLogNow(text);
    }

    /// <summary>Whether a held action of a kind last noted at <paramref name="lastAt"/> is due to be counted and logged (callers test it before formatting the text: no per-tick string).</summary>
    bool BotHoldLogDue(double lastAt) => _connectedFor - lastAt >= 1.0;

    void BotSafetyLogNow(string text)
    {
        _botSafetyLogAt = double.NegativeInfinity;
        BotSafetyLog(text);
    }

    /// <summary>Whether a charge at <paramref name="target"/> would end in, or run through, the bot's own live blast (logged when it is).</summary>
    bool BotChargeIntoOwnBlast(PlayerState me, PlayerState target)
    {
        if (_hazards.Count == 0 || !GrenadeSafetyOn || Loadouts.Rush(_config!, me) is null) return false;
        if (!InOwnBlast(target) && !BotGrenadeSafety.PathCrosses(_hazards, BotChest(me), BotChest(target))) return false;
        if (BotHoldLogDue(_botLastChargeHold)) BotNoteHeld(ref _chargesHeld, ref _botLastChargeHold, $"no charge at P{target.PlayerId} (its own grenade's blast on the way)");
        return true;
    }

    /// <summary>Whether a melee swing (and its lunge) at <paramref name="target"/> would go into the bot's own live blast (logged when it is).</summary>
    bool BotMeleeIntoOwnBlast(PlayerState me, PlayerState target)
    {
        if (_hazards.Count == 0 || !GrenadeSafetyOn || (!InOwnBlast(target) && !InOwnBlast(me))) return false;
        if (BotHoldLogDue(_botLastMeleeHold)) BotNoteHeld(ref _meleeHeld, ref _botLastMeleeHold, $"no melee at P{target.PlayerId} (inside its own grenade's blast)");
        return true;
    }

    /// <summary>Whether a jump at <paramref name="target"/> would land in the bot's own live blast: the target's feet or chest inside it (logged when it is).</summary>
    bool BotJumpIntoOwnBlast(PlayerState target)
    {
        if (_hazards.Count == 0 || !GrenadeSafetyOn) return false;
        if (!InOwnBlast(target) && !BotGrenadeSafety.Inside(_hazards, target.Position)) return false;
        if (BotHoldLogDue(_botLastJumpHold)) BotNoteHeld(ref _jumpsHeld, ref _botLastJumpHold, $"no jump at P{target.PlayerId} (its own grenade's blast at the landing)");
        return true;
    }

    /// <summary>Whether a ground pound onto <paramref name="target"/> would land in the bot's own live blast (logged when it is).</summary>
    bool BotPoundIntoOwnBlast(PlayerState target)
    {
        if (_hazards.Count == 0 || !GrenadeSafetyOn) return false;
        if (!InOwnBlast(target) && !BotGrenadeSafety.Inside(_hazards, target.Position)) return false;
        if (BotHoldLogDue(_botLastPoundHold)) BotNoteHeld(ref _poundsHeld, ref _botLastPoundHold, $"no pound onto P{target.PlayerId} (its own grenade's blast there)");
        return true;
    }

    /// <summary>
    /// In a jump: the stick that keeps the bot out of its own live blast, or null to steer as before. A target inside the blast is not steered at (the stick centred); a flight
    /// whose ground point (the bot's feet at the hazard's height) is inside one steers straight away from it.
    /// </summary>
    (float X, float Y)? BotAirStick(PlayerState me, PlayerState target)
    {
        if (_hazards.Count == 0 || !GrenadeSafetyOn) return null;
        foreach (var h in _hazards)
        {
            var ground = new V3(me.Position.X, me.Position.Y, h.Centre.Z);
            if (V3.Distance(ground, h.Centre) >= h.Reach) continue;
            var away = BotGrenadeSafety.Away(_hazards, ground, -BotGrenadeSafety.Forward(_yaw));
            return (V2.Dot(away, BotGrenadeSafety.Right(_yaw)), V2.Dot(away, BotGrenadeSafety.Forward(_yaw)));
        }
        return InOwnBlast(target) || BotGrenadeSafety.Inside(_hazards, target.Position) ? (0f, 0f) : null;
    }

    double _botLastSplashHold = double.NegativeInfinity;
    int _splashHeld;
    /// <summary>The run of held splash shots of the weapon in hand (<see cref="BotGrenadeSafety.NoteHeld"/>; null: none).</summary>
    BotGrenadeSafety.HeldRun? _botSplashRun;
    /// <summary>Whether the pistol in hand was taken for a held splash shot (<see cref="BotSplashSwitch"/>): it goes back to the splash weapon when the range allows.</summary>
    bool _botSplashPistol;

    /// <summary>
    /// Where shot <paramref name="stage"/> of <paramref name="weapon"/> would burst on a shot at <paramref name="target"/>: the distance (m) from the bot's chest to the first level
    /// contact on the line from the eye to the target's chest, else to the target's chest, or, when nearer, to where the shot meets a target coming on
    /// (<see cref="BotGrenadeSafety.MeetingPoint"/>: a jump-pack flier at the bot met a plasma ball 6.5 m from the heavy's chest in a 2026-11-11 run); and the shot's safety distance
    /// (its projectile's damaging blast radius + margin). Null for shots without a blast, without a target, and outside Classic PvP.
    /// </summary>
    (float Distance, float Safety)? BotSplashBurst(PlayerState me, WeaponSpec weapon, int stage, PlayerState? target)
    {
        var projectile = Shots.ProjectileOf(weapon, stage);
        if (!GrenadeSafetyOn || target is not { } t || projectile is null || BotGrenadeSafety.SafetyDistance(projectile) is not (var safety and > 0f)) return null;
        var eye = me.Position + new V3(0, 0, _config!.EyeHeight);
        var ownChest = BotChest(me);
        var chest = BotChest(t);
        float distance = V3.Distance(ownChest, BotBurstPoint(eye, chest));
        var ahead = BotGrenadeSafety.MeetingPoint(eye, chest, t.Velocity, projectile.Speed, (float)(_client?.CurrentInterpolationDelay.TotalSeconds ?? 0.0));
        if (V3.Distance(ownChest, ahead) < distance) distance = MathF.Min(distance, V3.Distance(ownChest, BotBurstPoint(eye, ahead)));   // the level can only bring it nearer
        return (distance, safety);
    }

    /// <summary>Where a shot from <paramref name="eye"/> at <paramref name="point"/> stops: the first level contact on the line, else the point.</summary>
    V3 BotBurstPoint(V3 eye, V3 point)
    {
        if (_client?.Collision is not { } world) return point;
        var line = point - eye;
        float length = line.Length();
        return length > 0.3f && world.Weapons.Raycast(eye, line / length, length) is { } hit ? hit.Point : point;
    }

    /// <summary>
    /// Whether shot <paramref name="stage"/> of <paramref name="weapon"/> would burst within its own blast reach of the bot (<see cref="BotSplashBurst"/> nearer than the safety
    /// distance; <see cref="BotGrenadeSafety.BurstsTooClose"/>). False for shots without a blast, without a target, and outside Classic PvP. With <paramref name="note"/> (a shot
    /// the bot then withholds) it goes on the held run (<see cref="BotSplashSwitch"/>) and is counted and logged (at most once a second); without, it is only asked about.
    /// </summary>
    bool BotSplashShotHeld(PlayerState me, WeaponSpec weapon, int stage, PlayerState? target, bool note = true)
    {
        if (BotSplashBurst(me, weapon, stage, target) is not { } burst || burst.Distance >= burst.Safety) return false;
        if (note) _botSplashRun = BotGrenadeSafety.NoteHeld(_botSplashRun, _connectedFor);
        if (note && _connectedFor - _botLastSplashHold >= 1.0)
        {
            _splashHeld++; _botLastSplashHold = _connectedFor;
            BotSafetyLogNow($"no {WeaponLabel(weapon)} {(stage > 0 ? "charged " : "")}shot at P{target?.PlayerId}: it would burst {burst.Distance:F1} m from the chest (safety {burst.Safety:F1} m; splash shots held {_splashHeld})");
        }
        return true;
    }

    /// <summary>
    /// The pistol for a splash weapon at close range (Classic PvP; called before <see cref="BotWantsSwitch"/>): true to press Switch now, false to stay with the weapon in hand
    /// whatever <see cref="BotWantsSwitch"/> says, null to leave it to that. On the primary (not overheated; the toggle goes to the pistol): its splash shot held for
    /// <see cref="BotGrenadeSafety.PistolAfterSeconds"/> s and the pistol loaded (<see cref="BotGrenadeSafety.TakePistol"/>) takes the pistol. On a pistol taken that way: back to the
    /// primary (with ammunition) once its burst point at the target is <see cref="BotGrenadeSafety.PistolBackMargin"/> m beyond its safety distance
    /// (<see cref="BotGrenadeSafety.BackToSplash"/>); until then it stays (an empty pistol reloads itself); a pistol out of ammunition leaves it to the old rule.
    /// </summary>
    bool? BotSplashSwitch(PlayerState me, PlayerState? target)
    {
        var c = _config!;
        if (!GrenadeSafetyOn) { _botSplashPistol = false; return null; }
        if (me.MeleeStep > 0 || me.SwitchLeft > 0f || me.Charging || me.PendingShot > 0) return null;
        if (me.Slot == WeaponSlot.Ranged)
        {
            _botSplashPistol = false;
            if (me.Overheated || Loadouts.ToggleSlot(c, me) != WeaponSlot.Pistol) return null;
            double heldFor = BotGrenadeSafety.HeldFor(_botSplashRun, _connectedFor);
            if (!BotGrenadeSafety.TakePistol(heldFor, me.OtherClip)) return null;
            _botSplashPistol = true; _botSplashRun = null; _splashPistols++;
            BotSafetyLogNow($"takes the pistol: its splash shot held {heldFor:F1} s at close range (pistols taken {_splashPistols})");
            return true;
        }
        if (me.Slot != WeaponSlot.Pistol || !_botSplashPistol) return null;
        if (me.Clip + me.Reserve <= 0 || me.OtherClip + me.OtherReserve <= 0 || Loadouts.ToggleSlot(c, me) != WeaponSlot.Ranged || Loadouts.Holstered(c, me) is not { } primary)
        {
            _botSplashPistol = false;   // out of pistol rounds, or nothing to go back to: the old rule
            return null;
        }
        if (target is null) return false;
        if (BotSplashBurst(me, primary, 0, target) is { } burst && !BotGrenadeSafety.BackToSplash(burst.Distance, burst.Safety)) return false;
        _botSplashPistol = false;
        BotSafetyLogNow($"back to the {WeaponLabel(primary)}: P{target?.PlayerId} far enough for its shot");
        return true;
    }

    /// <summary>The distance (m) from the bot's chest to the nearest waiting round of <paramref name="weapon"/> inside its blast reach (a detonation now would hit the bot), or null.</summary>
    float? BotDetonationHeld(PlayerState me, WeaponSpec weapon)
    {
        if (!GrenadeSafetyOn || _client is null || BotGrenadeSafety.SafetyDistance(Shots.ProjectileOf(weapon, 0)) is not (var safety and > 0f)) return null;
        int index = Loadouts.WeaponIndex(_config!, me, me.Slot);
        var chest = BotChest(me);
        float? nearest = null;
        foreach (var q in _client.NewestProjectiles())
        {
            if ((q.Flags & ProjectileFlags.Thrown) != 0 || q.Weapon != index || q.Stage != 0 || !BotGrenadeSafety.InReach(q.Position, chest, safety)) continue;
            float d = V3.Distance(q.Position, chest);
            if (nearest is null || d < nearest) nearest = d;
        }
        return nearest;
    }

    /// <summary>
    /// The throw decision for a damaging item (Classic PvP bots): the verdict and, when it is a throw, the landing point the aim goes to (<see cref="_botThrowAim"/>). The landing is
    /// the target's feet moved by its horizontal velocity over the release delay and the flight, or the first level contact on the aimed line when the level is in the way.
    /// </summary>
    BotGrenadeSafety.ThrowVerdict BotThrowCheck(PlayerState me, PlayerState target, ThrownSpec item, float safety, out V3 landing)
    {
        var c = _config!;
        var eye = me.Position + new V3(0, 0, c.EyeHeight);
        float release = MathF.Max(0f, c.ThrowRelease - (me.Throwing ? me.ThrowTime : 0f));
        float flight = BotGrenadeSafety.FlightSeconds(V3.Distance(eye, target.Position), item.Projectile.Speed);
        landing = BotGrenadeSafety.PredictLanding(target.Position, target.Velocity, release + flight);
        // The level on the aimed line (a ledge edge below a target above, a rail): the grenade stops there.
        if (_client?.Collision is { } world)
        {
            var line = landing - eye;
            float length = line.Length();
            if (length > 0.3f && world.Weapons.Raycast(eye, line / length, length - 0.2f) is { } hit) landing = hit.Point;
        }
        return BotGrenadeSafety.CheckThrow(BotChest(me), BotChest(target), landing, safety, _botThrowMax, BotGrenadeSafety.Airborne(target.Jump, target.Velocity),
            !me.Grounded || BotGrenadeSafety.Airborne(me.Jump, me.Velocity));
    }

    /// <summary>Aims a throw under way at its landing point (yaw and pitch from the eye; a grenade flies straight for its first metres and lands about where it is aimed).</summary>
    void BotAimThrow(PlayerState me, V3 landing)
    {
        var eye = me.Position + new V3(0, 0, _config!.EyeHeight);
        float dx = landing.X - eye.X, dy = landing.Y - eye.Y;
        _yaw = MathF.Atan2(-dx, dy);
        _pitch = Math.Clamp(MathF.Atan2(landing.Z - eye.Z, MathF.Max(1f, MathF.Sqrt(dx * dx + dy * dy))), -MaxPitchDown, MaxPitchUp);
    }

    /// <summary>A throw logged at most once every 2 s (refusals), with the bot's counts.</summary>
    void BotSafetyLog(string text)
    {
        if (_connectedFor - _botSafetyLogAt < 2.0) return;
        _botSafetyLogAt = _connectedFor;
        GD.Print($"t {_connectedFor:F1} s: id {_client?.PlayerId} grenade safety: {text} (refused throws {_throwsRefused}, holds {_holds}, back-outs {_backOuts}, give-ups {_giveUps}, jumps held {_jumpsHeld}, " +
                 $"pounds held {_poundsHeld}, charges held {_chargesHeld}, melee held {_meleeHeld}, detonations held {_detonationsHeld}, take-offs held {_takeOffsHeld}, own grenades claimed {_ownClaimed}, " +
                 $"hazards expired {_hazardsExpired}, pistols for splash {_splashPistols})");
    }

    /// <summary>From <see cref="WatchThrowAndCone"/>: the own predicted release of a thrown item starts tracking its grenade (a damaging item only, Classic PvP bots).</summary>
    void BotNoteOwnRelease(PlayerState me)
    {
        if (!GrenadeSafetyOn || _client is null || Loadouts.Equipment(_config!, me) is not { } item) return;
        float safety = BotGrenadeSafety.SafetyDistance(item, _config!.ThrownBlastScale(item));
        if (safety <= 0f) return;
        var g = new OwnGrenade { Reach = safety, Speed = item.Projectile.Speed, ReleasedAt = _connectedFor, ReleasedFrom = me.Position, Landing = _botThrowAim ?? me.Position, HasLanding = _botThrowAim is not null };
        g.Centre = g.Landing;
        foreach (var q in _client.NewestProjectiles()) if ((q.Flags & ProjectileFlags.Thrown) != 0) g.KnownAtRelease.Add(q.Id);
        _ownGrenades.Add(g);
        GD.Print(g.HasLanding
            ? $"t {_connectedFor:F1} s: id {_client.PlayerId} own grenade out: landing predicted at {g.Landing.X:F1},{g.Landing.Y:F1},{g.Landing.Z:F1} ({V3.Distance(BotChest(me), g.Landing):F1} m from the chest; safety {safety:F1} m)"
            : $"t {_connectedFor:F1} s: id {_client.PlayerId} own grenade out: no planned landing (safety {safety:F1} m)");
        _botThrowAim = null; _botThrowPressAim = null;
    }

    /// <summary>Whether one of the bot's own tracked grenades already holds snapshot id <paramref name="id"/>.</summary>
    bool BotOwnHolds(int id)
    {
        foreach (var o in _ownGrenades) if (o.Id == id) return true;
        return false;
    }

    /// <summary>Whether a weapon the bot carries can leave a round waiting with a blast to come (the early-out of <see cref="BotHazards"/>; cached per weapon index).</summary>
    bool BotCanLeaveRounds(PlayerState me)
    {
        foreach (var slot in HazardSlots)
        {
            int index = Loadouts.WeaponIndex(_config!, me, slot);
            if (index < 0 || index >= _config!.Weapons.Count) continue;
            if (!_waitingRoundWeapon.TryGetValue(index, out bool leaves)) _waitingRoundWeapon[index] = leaves = BotGrenadeSafety.LeavesWaitingRounds(_config.Weapons[index]);
            if (leaves) return true;
        }
        return false;
    }

    /// <summary>Nothing out: the waiting rounds, the back-out and a give-up are forgotten.</summary>
    void BotForgetRounds()
    {
        if (_roundSeen.Count > 0) _roundSeen.Clear();
        _botBackOut = null; _botGaveUp = false;
    }

    /// <summary>
    /// Updates the bot's own grenades from the newest snapshot (claims a new thrown projectile after a release, follows it until it is gone) and builds <see cref="_hazards"/>:
    /// those grenades (<see cref="BotGrenadeSafety.TrackOwn"/>) and, with rounds waiting, every waiting round of the bot's own weapons for <see cref="BotGrenadeSafety.MaxHazardSeconds"/>
    /// s from when it is first seen (<see cref="BotGrenadeSafety.RoundIsHazard"/>). After a back-out given up, the blasts that hold the bot are left out until it is out of them.
    /// Nothing out (no own grenade, no remote round, no weapon that leaves waiting rounds): no snapshot scan.
    /// </summary>
    IReadOnlyList<BotGrenadeSafety.Hazard> BotHazards(PlayerState me)
    {
        _hazards.Clear();
        if (!GrenadeSafetyOn || _client is null) { _ownGrenades.Clear(); BotForgetRounds(); return _hazards; }
        if (_ownGrenades.Count == 0 && me.ProjectilesOut <= 0 && !BotCanLeaveRounds(me)) { BotForgetRounds(); return _hazards; }
        _botLive.Clear();
        foreach (var q in _client.NewestProjectiles()) _botLive.Add(q);
        var chest = BotChest(me);
        for (int i = _ownGrenades.Count - 1; i >= 0; i--)
        {
            var g = _ownGrenades[i];
            double age = _connectedFor - g.ReleasedAt;
            bool seen = false;
            if (g.Id < 0)
            {
                // A new thrown projectile since the release that no other own grenade holds and that could have come from the bot's hand by now (BotGrenadeSafety.Claimable:
                // an enemy's grenade landing beside the bot is not taken for its own), nearest to where the bot let go.
                ProjectileView? best = null; float bestDistance = float.MaxValue;
                foreach (var q in _botLive)
                {
                    if ((q.Flags & ProjectileFlags.Thrown) == 0 || g.KnownAtRelease.Contains(q.Id) || BotOwnHolds(q.Id)) continue;
                    float d = V3.Distance(q.Position, g.ReleasedFrom);
                    if (BotGrenadeSafety.Claimable(d, (float)age, g.Speed) && d < bestDistance) { bestDistance = d; best = q; }
                }
                if (best is { } claimed)
                {
                    g.Id = claimed.Id; g.Centre = claimed.Position; _ownClaimed++; seen = true;
                    g.SamplePosition = claimed.Position; g.SampleAt = _connectedFor;
                    GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} own grenade #{g.Id} seen {age:F2} s after the release at {g.Centre.X:F1},{g.Centre.Y:F1},{g.Centre.Z:F1}");
                }
            }
            else
            {
                foreach (var q in _botLive)
                {
                    if (q.Id != g.Id) continue;
                    g.Centre = q.Position; seen = true;
                    bool stuck = (q.Flags & (ProjectileFlags.Stuck | ProjectileFlags.Attached)) != 0;
                    double over = _connectedFor - g.SampleAt;
                    if (stuck || over >= BotGrenadeSafety.DownSampleSeconds)
                    {
                        if (BotGrenadeSafety.IsDown(stuck, V3.Distance(q.Position, g.SamplePosition), over)) g.Down = true;   // slow: past its bounce, or dropped off a wall
                        g.SamplePosition = q.Position; g.SampleAt = _connectedFor;
                    }
                    break;
                }
            }
            if (g.Id >= 0 && seen && V3.Distance(g.Centre, chest) >= g.Reach) g.Cleared = true;
            var track = BotGrenadeSafety.TrackOwn((float)age, g.Id >= 0, seen, g.Down, g.HasLanding, g.Cleared);
            if ((track & BotGrenadeSafety.OwnHazard.Drop) != 0)
            {
                if (g.Id >= 0 && !seen)   // detonated (or gone)
                    GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} own grenade #{g.Id} gone {age:F2} s after the release at {g.Centre.X:F1},{g.Centre.Y:F1},{g.Centre.Z:F1}: " +
                             $"{V3.Distance(g.Centre, chest):F1} m from the chest ({(me.Alive ? "alive" : "dead")}), {V3.Distance(g.Centre, g.Landing):F1} m from the planned landing");
                else if (age > BotGrenadeSafety.MaxHazardSeconds) { _hazardsExpired++; BotSafetyLogNow($"own grenade #{g.Id} no longer a hazard ({age:F1} s out)"); }
                _ownGrenades.RemoveAt(i); continue;
            }
            if ((track & BotGrenadeSafety.OwnHazard.Landing) != 0) _hazards.Add(new BotGrenadeSafety.Hazard(g.Landing, g.Reach));
            if ((track & BotGrenadeSafety.OwnHazard.Grenade) != 0) _hazards.Add(new BotGrenadeSafety.Hazard(g.Centre, g.Reach));
        }
        // Rounds of the bot's own weapons that wait with a blast to come: the launcher's (while it has rounds out) and a sticky shot resting or stuck to a player (the plasma
        // pistol's charged ball), each for MaxHazardSeconds from when it is first seen. Snapshots carry no owner: an ally's of the same weapon counts too (the bot only keeps clear of it).
        _hazardStamp++;
        int doneA = -1, doneB = -1;
        foreach (var slot in HazardSlots)
        {
            if (Loadouts.Weapon(_config!, me, slot) is not { } w) continue;
            int index = Loadouts.WeaponIndex(_config!, me, slot);
            if (index == doneA || index == doneB) continue;   // the same weapon in two slots
            if (doneA < 0) doneA = index; else doneB = index;
            bool remote = Shots.RemoteDetonated(w) && me.ProjectilesOut > 0;
            foreach (var q in _botLive)
            {
                if ((q.Flags & ProjectileFlags.Thrown) != 0 || q.Weapon != index) continue;
                bool waiting = (remote && q.Stage == 0) || (q.Flags & (ProjectileFlags.Stuck | ProjectileFlags.Attached)) != 0;
                if (!waiting || BotGrenadeSafety.SafetyDistance(Shots.ProjectileOf(w, q.Stage)) is not (var reach and > 0f)) continue;
                (double FirstSeen, int Stamp, bool Expired) entry = _roundSeen.TryGetValue(q.Id, out var known) ? (known.FirstSeen, _hazardStamp, known.Expired) : (_connectedFor, _hazardStamp, false);
                if (BotGrenadeSafety.RoundIsHazard(entry.FirstSeen, _connectedFor)) _hazards.Add(new BotGrenadeSafety.Hazard(q.Position, reach));
                else if (!entry.Expired)
                {
                    entry.Expired = true; _hazardsExpired++;
                    BotSafetyLogNow($"waiting round #{q.Id} of {WeaponLabel(w)} no longer a hazard ({_connectedFor - entry.FirstSeen:F1} s out)");
                }
                _roundSeen[q.Id] = entry;
            }
        }
        if (_roundSeen.Count > 0)
        {
            _roundStale.Clear();
            foreach (var (id, e) in _roundSeen) if (e.Stamp != _hazardStamp) _roundStale.Add(id);
            foreach (var id in _roundStale) _roundSeen.Remove(id);
        }
        // A back-out given up (cornered): the blasts that hold the bot are left out (normal behaviour) until it is out of all of them; the others still count.
        if (_botGaveUp)
        {
            if (BotGrenadeSafety.StillGivenUp(_botGaveUp, _hazards, chest))
            {
                for (int i = _hazards.Count - 1; i >= 0; i--) if (V3.DistanceSquared(_hazards[i].Centre, chest) < _hazards[i].Reach * _hazards[i].Reach) _hazards.RemoveAt(i);
            }
            else
            {
                _botGaveUp = false;
                BotSafetyLogNow("out of the blasts it gave up on: the holds apply again");
            }
        }
        if (_hazards.Count == 0) _botBackOut = null;
        return _hazards;
    }

    /// <summary>Whether <paramref name="s"/>'s chest is inside one of the bot's hazards (always false without them).</summary>
    bool InOwnBlast(PlayerState s) => _hazards.Count > 0 && BotGrenadeSafety.Inside(_hazards, BotChest(s));

    /// <summary>
    /// The hold on the bot's final input (Classic PvP; on the ground, not during added content's own bot, a jump or a charge): <see cref="BotGrenadeSafety.Hold"/> with the hazards
    /// of this tick. A dropped jump press also ends the hop's hold, so it does not come back as a late press, and a navigation take-off it drops is handed back to the agent without a
    /// miss (<see cref="NavAgent.CancelTakeOff"/>: the link is tried again once the way is clear, not dropped). A back-out that makes no progress for
    /// <see cref="BotGrenadeSafety.GiveUpSeconds"/> s (cornered) gives up: from this tick the input is the bot's own, and the blasts holding it are left out until it is out of them.
    /// </summary>
    PlayerInput BotGrenadeHold(PlayerInput input)
    {
        if (!GrenadeSafetyOn || _botExtrasTick || _client is null || _hazards.Count == 0) { _lastHold = BotGrenadeSafety.HoldKind.None; return input; }
        var me = _client.Predicted;
        if (!me.Alive || !me.Grounded || me.Jump != JumpPhase.None || me.Rush != RushPhase.None) { _lastHold = BotGrenadeSafety.HoldKind.None; return input; }
        var chest = BotChest(me);
        var held = BotGrenadeSafety.Hold(input, me.Position, chest, _hazards, out var kind);
        if (kind == BotGrenadeSafety.HoldKind.BackedOut)
        {
            _botBackOut = BotGrenadeSafety.StepBackOut(_botBackOut, BotGrenadeSafety.DistanceInside(_hazards, chest) ?? 0f, _connectedFor, out bool giveUp);
            if (giveUp)
            {
                _botGaveUp = true; _botBackOut = null; _giveUps++; _lastHold = BotGrenadeSafety.HoldKind.None;
                BotSafetyLogNow($"gives up backing out (no farther from the blast in {BotGrenadeSafety.GiveUpSeconds:F1} s): back to its own moves until it is out");
                return input;
            }
        }
        else _botBackOut = null;
        if (kind != BotGrenadeSafety.HoldKind.None && (input.Buttons & InputButtons.Jump) != 0 && (held.Buttons & InputButtons.Jump) == 0)
        {
            _botJumpHolding = false;
            if (_navAgent is { JumpInProgress: true } agent && agent.CancelTakeOff(me.Jump, me.Grounded, me.Position, _connectedFor)) _takeOffsHeld++;
        }
        if (kind != _lastHold && kind != BotGrenadeSafety.HoldKind.None)
        {
            if (kind == BotGrenadeSafety.HoldKind.Held) _holds++; else _backOuts++;
            var h = _hazards[0];
            foreach (var z in _hazards) if (V3.DistanceSquared(z.Centre, me.Position) < V3.DistanceSquared(h.Centre, me.Position)) h = z;
            GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} grenade hold: {(kind == BotGrenadeSafety.HoldKind.Held ? "holds (the move would enter its own blast)" : "backs out of its own blast")} " +
                     $"at {h.Centre.X:F1},{h.Centre.Y:F1},{h.Centre.Z:F1}, {V3.Distance(h.Centre, chest):F1} m from the chest (reach {h.Reach:F1} m); holds {_holds}, back-outs {_backOuts}");
        }
        _lastHold = kind;
        return held;
    }
}
