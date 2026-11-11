using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmNet;
using SmSim;
using V3 = System.Numerics.Vector3;
using V2 = System.Numerics.Vector2;

namespace SmClient;

/// <summary>
/// bot-grenade-safety (owner, 2026-11-11; Classic PvP bots; rules engine-free in <see cref="BotGrenadeSafety"/>, **ours**): the bots no longer blow themselves up with their own
/// grenades. The throw needs the target and the predicted landing point beyond the item's blast radius + margin (3D) and aims at that landing point; while the bot's own grenade
/// (or waiting launcher round) is out it does not jump, pound, charge, melee or walk into its blast, backing out when inside; remote rounds are not set off with the bot inside
/// their reach; no throw at a target in the air. The own grenade is the first new thrown projectile the snapshots show after the bot's own release (snapshots carry no owner);
/// while it flies (a frag's fuse starts on the ground) its predicted landing point stands for it, once down (resting, stuck or slow) its own place. <c>--old-bot-throws</c> restores the old throws (6 m horizontal minimum, the aim 8 m short of the target) and drops every hold.
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
        public V3 SamplePosition;
        public double SampleAt = double.NegativeInfinity;
        public float Reach, Expires, Speed;
        public double ReleasedAt;
        public HashSet<int> KnownAtRelease = new();
    }

    /// <summary>A claimed grenade that moved less than <see cref="DownSampleMetres"/> in <see cref="DownSampleSeconds"/> is down (under 10 m/s; the throw leaves at 47.5 m/s; ours).</summary>
    const double DownSampleSeconds = 0.15;
    const float DownSampleMetres = 1.5f;

    readonly List<OwnGrenade> _ownGrenades = new();
    readonly List<BotGrenadeSafety.Hazard> _hazards = new();
    /// <summary>The landing point the bot's throw under way aims at (null: no throw planned, or the old aim).</summary>
    V3? _botThrowAim;
    double _botSafetyLogAt = double.NegativeInfinity;
    BotGrenadeSafety.HoldKind _lastHold;
    /// <summary>Whether this tick's bot input came from added content's own bot (Musou Mode): the hold leaves it alone.</summary>
    bool _botExtrasTick;
    int _throwsRefused, _holds, _backOuts, _jumpsHeld, _poundsHeld, _chargesHeld, _meleeHeld, _detonationsHeld, _ownClaimed;

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

    /// <summary>Counts and logs a held action at most once a second per kind.</summary>
    void BotNoteHeld(ref int count, ref double lastAt, string text)
    {
        if (_connectedFor - lastAt < 1.0) return;
        count++; lastAt = _connectedFor;
        BotSafetyLogNow(text);
    }

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
        BotNoteHeld(ref _chargesHeld, ref _botLastChargeHold, $"no charge at P{target.PlayerId} (its own grenade's blast on the way)");
        return true;
    }

    /// <summary>Whether a melee swing (and its lunge) at <paramref name="target"/> would go into the bot's own live blast (logged when it is).</summary>
    bool BotMeleeIntoOwnBlast(PlayerState me, PlayerState target)
    {
        if (_hazards.Count == 0 || !GrenadeSafetyOn || (!InOwnBlast(target) && !InOwnBlast(me))) return false;
        BotNoteHeld(ref _meleeHeld, ref _botLastMeleeHold, $"no melee at P{target.PlayerId} (inside its own grenade's blast)");
        return true;
    }

    /// <summary>Whether a jump at <paramref name="target"/> would land in the bot's own live blast: the target's feet or chest inside it (logged when it is).</summary>
    bool BotJumpIntoOwnBlast(PlayerState target)
    {
        if (_hazards.Count == 0 || !GrenadeSafetyOn) return false;
        if (!InOwnBlast(target) && !BotGrenadeSafety.Inside(_hazards, target.Position)) return false;
        BotNoteHeld(ref _jumpsHeld, ref _botLastJumpHold, $"no jump at P{target.PlayerId} (its own grenade's blast at the landing)");
        return true;
    }

    /// <summary>Whether a ground pound onto <paramref name="target"/> would land in the bot's own live blast (logged when it is).</summary>
    bool BotPoundIntoOwnBlast(PlayerState target)
    {
        if (_hazards.Count == 0 || !GrenadeSafetyOn) return false;
        if (!InOwnBlast(target) && !BotGrenadeSafety.Inside(_hazards, target.Position)) return false;
        BotNoteHeld(ref _poundsHeld, ref _botLastPoundHold, $"no pound onto P{target.PlayerId} (its own grenade's blast there)");
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

    /// <summary>The distance (m) from the bot's chest to the nearest waiting round of <paramref name="weapon"/> inside its blast reach (a detonation now would hit the bot), or null.</summary>
    float? BotDetonationHeld(PlayerState me, WeaponSpec weapon)
    {
        if (!GrenadeSafetyOn || _client is null || BotGrenadeSafety.SafetyDistance(Shots.ProjectileOf(weapon, 0)) is not (var safety and > 0f)) return null;
        int index = Loadouts.WeaponIndex(_config!, me, me.Slot);
        var chest = BotChest(me);
        var rounds = _client.NewestProjectiles().Where(q => (q.Flags & ProjectileFlags.Thrown) == 0 && q.Weapon == index && q.Stage == 0).Select(q => q.Position).ToList();
        if (BotGrenadeSafety.MayDetonate(rounds, chest, safety)) return null;
        return rounds.Min(r => V3.Distance(r, chest));
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
        GD.Print($"t {_connectedFor:F1} s: id {_client?.PlayerId} grenade safety: {text} (refused throws {_throwsRefused}, holds {_holds}, back-outs {_backOuts}, jumps held {_jumpsHeld}, " +
                 $"pounds held {_poundsHeld}, charges held {_chargesHeld}, melee held {_meleeHeld}, detonations held {_detonationsHeld}, own grenades claimed {_ownClaimed})");
    }

    /// <summary>From <see cref="WatchThrowAndCone"/>: the own predicted release of a thrown item starts tracking its grenade (a damaging item only, Classic PvP bots).</summary>
    void BotNoteOwnRelease(PlayerState me)
    {
        if (!GrenadeSafetyOn || _client is null || Loadouts.Equipment(_config!, me) is not { } item) return;
        float safety = BotGrenadeSafety.SafetyDistance(item, _config!.ThrownBlastScale(item));
        if (safety <= 0f) return;
        var g = new OwnGrenade { Reach = safety, Speed = item.Projectile.Speed, ReleasedAt = _connectedFor, ReleasedFrom = me.Position, Landing = _botThrowAim ?? me.Position, HasLanding = _botThrowAim is not null, Expires = BotGrenadeSafety.MaxHazardSeconds };
        g.Centre = g.Landing;
        foreach (var q in _client.NewestProjectiles()) if ((q.Flags & ProjectileFlags.Thrown) != 0) g.KnownAtRelease.Add(q.Id);
        _ownGrenades.Add(g);
        GD.Print(g.HasLanding
            ? $"t {_connectedFor:F1} s: id {_client.PlayerId} own grenade out: landing predicted at {g.Landing.X:F1},{g.Landing.Y:F1},{g.Landing.Z:F1} ({V3.Distance(BotChest(me), g.Landing):F1} m from the chest; safety {safety:F1} m)"
            : $"t {_connectedFor:F1} s: id {_client.PlayerId} own grenade out: no planned landing (safety {safety:F1} m)");
        _botThrowAim = null;
    }

    /// <summary>
    /// Updates the bot's own grenades from the newest snapshot (claims a new thrown projectile after a release, follows it until it is gone) and builds <see cref="_hazards"/>:
    /// those grenades and, with rounds waiting (<see cref="PlayerState.ProjectilesOut"/>), every waiting round of the remote-detonated weapon the bot carries.
    /// </summary>
    IReadOnlyList<BotGrenadeSafety.Hazard> BotHazards(PlayerState me)
    {
        _hazards.Clear();
        if (!GrenadeSafetyOn || _client is null) { _ownGrenades.Clear(); return _hazards; }
        var live = _client.NewestProjectiles().ToList();
        for (int i = _ownGrenades.Count - 1; i >= 0; i--)
        {
            var g = _ownGrenades[i];
            double age = _connectedFor - g.ReleasedAt;
            if (g.Id < 0)
            {
                // A new thrown projectile since the release that no other own grenade holds and that could have come from the bot's hand by now (BotGrenadeSafety.Claimable:
                // an enemy's grenade landing beside the bot is not taken for its own), nearest to where the bot let go.
                ProjectileView? best = null; float bestDistance = float.MaxValue;
                foreach (var q in live)
                {
                    if ((q.Flags & ProjectileFlags.Thrown) == 0 || g.KnownAtRelease.Contains(q.Id) || _ownGrenades.Any(o => o.Id == q.Id)) continue;
                    float d = V3.Distance(q.Position, g.ReleasedFrom);
                    if (BotGrenadeSafety.Claimable(d, (float)age, g.Speed) && d < bestDistance) { bestDistance = d; best = q; }
                }
                if (best is { } claimed)
                {
                    g.Id = claimed.Id; g.Centre = claimed.Position; _ownClaimed++;
                    g.SamplePosition = claimed.Position; g.SampleAt = _connectedFor;
                    GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} own grenade #{g.Id} seen {age:F2} s after the release at {g.Centre.X:F1},{g.Centre.Y:F1},{g.Centre.Z:F1}");
                }
                else if (age > BotGrenadeSafety.UnseenSeconds + 1.5) { _ownGrenades.RemoveAt(i); continue; }   // never seen: the predicted landing stood for it through its fuse
            }
            else
            {
                bool found = false;
                foreach (var q in live)
                {
                    if (q.Id != g.Id) continue;
                    g.Centre = q.Position; found = true;
                    if ((q.Flags & (ProjectileFlags.Stuck | ProjectileFlags.Attached)) != 0) g.Down = true;
                    else if (_connectedFor - g.SampleAt >= DownSampleSeconds)
                    {
                        if (V3.Distance(q.Position, g.SamplePosition) < DownSampleMetres) g.Down = true;   // slow: past its bounce, or dropped off a wall
                        g.SamplePosition = q.Position; g.SampleAt = _connectedFor;
                    }
                    break;
                }
                if (!found)   // detonated (or gone)
                {
                    GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} own grenade #{g.Id} gone {age:F2} s after the release at {g.Centre.X:F1},{g.Centre.Y:F1},{g.Centre.Z:F1}: " +
                             $"{V3.Distance(g.Centre, BotChest(me)):F1} m from the chest ({(me.Alive ? "alive" : "dead")}), {V3.Distance(g.Centre, g.Landing):F1} m from the planned landing");
                    _ownGrenades.RemoveAt(i); continue;
                }
            }
            if (age > g.Expires) { _ownGrenades.RemoveAt(i); continue; }
            // In flight a frag cannot go off (its fuse starts on the ground): its planned landing stands for it; once down, where it is. Unseen and unplanned: no place yet.
            if (g.Id >= 0 && g.Down) _hazards.Add(new BotGrenadeSafety.Hazard(g.Centre, g.Reach));
            else if (g.HasLanding) _hazards.Add(new BotGrenadeSafety.Hazard(g.Landing, g.Reach));
        }
        if (me.ProjectilesOut > 0)
            foreach (var slot in new[] { WeaponSlot.Ranged, WeaponSlot.Pistol })
                if (Loadouts.Weapon(_config!, me, slot) is { } w && Shots.RemoteDetonated(w) && BotGrenadeSafety.SafetyDistance(Shots.ProjectileOf(w, 0)) is var reach and > 0f)
                {
                    int index = Loadouts.WeaponIndex(_config!, me, slot);
                    foreach (var q in live) if ((q.Flags & ProjectileFlags.Thrown) == 0 && q.Weapon == index && q.Stage == 0) _hazards.Add(new BotGrenadeSafety.Hazard(q.Position, reach));
                }
        return _hazards;
    }

    /// <summary>Whether <paramref name="s"/>'s chest is inside one of the bot's hazards (always false without them).</summary>
    bool InOwnBlast(PlayerState s) => _hazards.Count > 0 && BotGrenadeSafety.Inside(_hazards, BotChest(s));

    /// <summary>
    /// The hold on the bot's final input (Classic PvP; on the ground, not during added content's own bot, a jump or a charge): <see cref="BotGrenadeSafety.Hold"/> with the hazards
    /// of this tick. A dropped jump press also ends the hop's hold, so it does not come back as a late press.
    /// </summary>
    PlayerInput BotGrenadeHold(PlayerInput input)
    {
        if (!GrenadeSafetyOn || _botExtrasTick || _client is null || _hazards.Count == 0) { _lastHold = BotGrenadeSafety.HoldKind.None; return input; }
        var me = _client.Predicted;
        if (!me.Alive || !me.Grounded || me.Jump != JumpPhase.None || me.Rush != RushPhase.None) { _lastHold = BotGrenadeSafety.HoldKind.None; return input; }
        var held = BotGrenadeSafety.Hold(input, me.Position, BotChest(me), _hazards, out var kind);
        if (kind != BotGrenadeSafety.HoldKind.None && (input.Buttons & InputButtons.Jump) != 0 && (held.Buttons & InputButtons.Jump) == 0) _botJumpHolding = false;
        if (kind != _lastHold && kind != BotGrenadeSafety.HoldKind.None)
        {
            if (kind == BotGrenadeSafety.HoldKind.Held) _holds++; else _backOuts++;
            var h = _hazards.OrderBy(z => V3.Distance(z.Centre, me.Position)).First();
            GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} grenade hold: {(kind == BotGrenadeSafety.HoldKind.Held ? "holds (the move would enter its own blast)" : "backs out of its own blast")} " +
                     $"at {h.Centre.X:F1},{h.Centre.Y:F1},{h.Centre.Z:F1}, {V3.Distance(h.Centre, BotChest(me)):F1} m from the chest (reach {h.Reach:F1} m); holds {_holds}, back-outs {_backOuts}");
        }
        _lastHold = kind;
        return held;
    }
}
