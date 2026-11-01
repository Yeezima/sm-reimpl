using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmSim;

/// <summary>
/// Fire support in the World (Musou pass 2, M8; design section E, the owner's orbital support): a called strike's validation, its barrage and damage, and the Thunderhawk's path and guns (paused: <see cref="TfwFireRules.RunsEnabled"/>). Runs from <see cref="TfwStep"/> after the fight, only with a battle
/// on and the match in play; the enemies' deaths carry <see cref="MusouDeathKind.Barrage"/> or <see cref="MusouDeathKind.Strafe"/> and no player, so they score nothing and the kills are credited by
/// the next <see cref="TfwCreditKills"/>. Rules: a shell kills every enemy and Guardsman in the sphere of <see cref="TfwFireRules.BlastRadius"/> round its impact, bosses too, and hurts
/// players and AI Marines (never spared: friendly fire is the risk) falling off to that radius. Line of sight is ignored: heavy shell fire. A run hurts enemies only.
/// </summary>
public sealed partial class World
{
    readonly List<TfwShell> _fireLanding = new();
    readonly List<int> _fireIds = new();
    readonly ushort[] _fireNear = new ushort[MusouSim.Capacity];
    readonly MusouEnemy[] _fireAll = new MusouEnemy[MusouSim.Capacity];
    int _fireAllTick = -1;

    /// <summary>One tick of fire support.</summary>
    void TfwFireStep()
    {
        var run = _tfw!;
        var fire = run.Fire;
        if (run.Battle is not { } battle || _state != MatchState.Playing) { fire.Stop(); return; }
        int rate = Math.Max(1, Config.TickRate);
        fire.Follow(battle, Tick, rate);
        if (!fire.NoSchedule)
        {
            if (TfwFireRules.RunsEnabled && Tick >= fire.NextRunTick && fire.Run is null) TfwBeginRun(TfwAim(2), TfwFireHeading(2), rate);
        }
        fire.SendDue(Tick, rate);
        _fireLanding.Clear();
        fire.Landing(Tick, _fireLanding);
        foreach (var shell in _fireLanding) TfwShellLands(shell.At);
        TfwRunStep(rate);
    }

    /// <summary>
    /// A player's call for an orbital strike on <paramref name="point"/> (the command Orbital; the client only asks): refused, with nothing changed, unless a battle is on, the player is in it and alive,
    /// and the point lies on the battlefield; no cooldown (the owner, 2026-11-01), and a barrage under way stops nothing. The walking barrage then starts at once, centred on the point, across the caller's view. Whether it was accepted.
    /// </summary>
    public bool TfwOrbital(int player, Vector3 point)
    {
        if (_tfw is not { } run || run.Battle is null || _state != MatchState.Playing || !run.InField.Contains(player)) return false;
        if (!_players.TryGetValue(player, out var p) || !p.Alive) return false;
        var box = run.Field.Bounds;
        if (!float.IsFinite(point.X + point.Y + point.Z) || point.X < box.Min.X || point.X > box.Max.X || point.Y < box.Min.Y || point.Y > box.Max.Y) return false;
        int rate = Math.Max(1, Config.TickRate);
        point = run.Field.Clamp(point);
        point.Z = TfwGroundBelow(point, point.Z);
        TfwBarrage(point, TfwFireTestHeading(player, point), rate);
        return true;
    }

    float TfwFireHeading(ulong purpose) => _tfw!.Fire.Roll(Tick, 10 + purpose) * MathF.Tau;

    /// <summary>A place to aim at: most times a living enemy at random (more of them where they are thick), else a random place of the battlefield; on the ground there.</summary>
    Vector3 TfwAim(ulong purpose)
    {
        var fire = _tfw!.Fire;
        var musou = _musou!;
        if (fire.Roll(Tick, 20 + purpose) < TfwFireRules.AimAtMass && musou.Alive > 0)
        {
            int n = _fireAllTick == (int)Tick ? _fireAllCount : musou.CopyTo(_fireAll);
            _fireAllCount = n; _fireAllTick = (int)Tick;
            int start = (int)(fire.Roll(Tick, 30 + purpose) * n);
            for (int k = 0; k < n; k++)
            {
                var e = _fireAll[(start + k) % n];
                if (e.Alive) return e.Position;
            }
        }
        var box = _tfw.Field.Bounds;
        var at = new Vector3(box.Min.X + (box.Max.X - box.Min.X) * fire.Roll(Tick, 40 + purpose), box.Min.Y + (box.Max.Y - box.Min.Y) * fire.Roll(Tick, 50 + purpose), 0f);
        return at with { Z = TfwGroundAt(at, box.Max.Z) };
    }

    int _fireAllCount;

    /// <summary>The top surface under <paramref name="at"/> (shots' collision, a shell or a round strikes what is highest); without one <paramref name="fallback"/>.</summary>
    float TfwGroundAt(Vector3 at, float fallback)
    {
        if (Collision is null) return fallback;
        float top = Collision.Weapons.Max.Z + 1f;
        return Collision.Weapons.Raycast(new Vector3(at.X, at.Y, top), -Vector3.UnitZ, top - Collision.Weapons.Min.Z + 1f) is { } hit ? hit.Point.Z : fallback;
    }

    /// <summary>The surface at or under <paramref name="at"/> seen from just above <paramref name="z"/>: a strike lands where it was aimed, not on a gantry or roof far overhead; with none, <paramref name="z"/>.</summary>
    float TfwGroundBelow(Vector3 at, float z)
    {
        if (Collision is null) return z;
        float top = z + TfwFireRules.GroundLookUp;
        return Collision.Weapons.Raycast(new Vector3(at.X, at.Y, top), -Vector3.UnitZ, top - Collision.Weapons.Min.Z + 1f) is { } hit ? hit.Point.Z : z;
    }

    /// <summary>A barrage walks along a line through <paramref name="aim"/> on <paramref name="heading"/> (radians); the shells are laid at once and the records go out as each one's telegraph falls due.</summary>
    void TfwBarrage(Vector3 aim, float heading, int rate)
    {
        var fire = _tfw!.Fire;
        uint t = Tick;
        int shells = TfwFireRules.ShellsMin + (int)(fire.Roll(t, 60) * (TfwFireRules.ShellsMax - TfwFireRules.ShellsMin + 1));
        float length = TfwFireRules.LineMin + fire.Roll(t, 61) * (TfwFireRules.LineMax - TfwFireRules.LineMin);
        var way = new Vector3(MathF.Cos(heading), MathF.Sin(heading), 0f);
        var box = _tfw.Field.Bounds;
        uint first = t + (uint)MathF.Round(TfwFireRules.ShellDelay * rate);
        for (int k = 0; k < shells; k++)
        {
            float along = length * (shells == 1 ? 0f : k / (float)(shells - 1) - 0.5f);
            var scatter = new Vector3(fire.Roll(t, 62, (ulong)k) * 2f - 1f, fire.Roll(t, 63, (ulong)k) * 2f - 1f, 0f) * TfwFireRules.Scatter;
            var at = aim + way * along + scatter;
            at = new Vector3(Math.Clamp(at.X, box.Min.X, box.Max.X), Math.Clamp(at.Y, box.Min.Y, box.Max.Y), 0f);
            at.Z = TfwGroundBelow(at, aim.Z);
            fire.AddShell(new TfwShell(first + (uint)MathF.Round(k * TfwFireRules.ShellStep * rate), (ushort)k, at, false));
        }
        fire.Started(t);
    }

    /// <summary>A shell lands: see the class summary.</summary>
    void TfwShellLands(Vector3 at)
    {
        var fire = _tfw!.Fire;
        var musou = _musou!;
        MusouBeginTick();
        int n = musou.Query(at, TfwFireRules.BlastRadius, _fireNear, TfwFireRules.BlastRadius);
        for (int k = 0; k < n; k++)
        {
            if (!musou.TryGet(_fireNear[k], out var e)) continue;
            if (Vector3.Distance(MusouSim.Chest(e.Position, musou.Body(e.Type).Height), at) > TfwFireRules.BlastRadius && Vector3.Distance(e.Position, at) > TfwFireRules.BlastRadius) continue;
            var away = new Vector2(e.Position.X - at.X, e.Position.Y - at.Y);
            float distance = away.Length();
            var push = distance > 1e-3f ? away / distance : new Vector2(-MathF.Sin(e.Yaw), MathF.Cos(e.Yaw));
            musou.Damage(e.Id, 0, MathF.Max(e.Health, 1f), new Vector3(push, 0.8f), out bool killed, marker: 0, cause: MusouDeathKind.Barrage);
            if (killed) fire.ShellKills++;
        }
        _fireIds.Clear();
        foreach (int id in _players.Keys) _fireIds.Add(id);
        _fireIds.Sort();
        foreach (int id in _fireIds)
        {
            var p = _players[id];
            if (!p.Alive) continue;
            float distance = Vector3.Distance(p.Position, at);
            if (distance >= TfwFireRules.BlastRadius) continue;
            MusouHit(new MusouAttack(0, id, TfwFireRules.PlayerDamage * Falloff(distance), Melee: false, Staggers: distance <= TfwFireRules.BlastRadius / 2f, at));
        }
        TfwGuards?.KillWithin(at, TfwFireRules.BlastRadius);
    }

    static float Falloff(float distance) => Math.Clamp(1f - distance / TfwFireRules.BlastRadius, 0f, 1f);

    /// <summary>A run begins, over <paramref name="aim"/> on <paramref name="heading"/>: at <see cref="TfwFireRules.RunHeight"/> over the aim's ground and clear of everything under the path.</summary>
    void TfwBeginRun(Vector3 aim, float heading, int rate)
    {
        var fire = _tfw!.Fire;
        var way = new Vector3(MathF.Cos(heading), MathF.Sin(heading), 0f);
        float height = aim.Z + TfwFireRules.RunHeight;
        if (Collision is not null)
        {
            int samples = (int)(2f * TfwFireRules.RunReach / TfwFireRules.RunSample);
            for (int k = 0; k <= samples; k++)
            {
                var at = aim + way * (k * TfwFireRules.RunSample - TfwFireRules.RunReach);
                height = MathF.Max(height, TfwGroundAt(at, float.NegativeInfinity) + TfwFireRules.RunClearance);
            }
        }
        var flat = new Vector3(aim.X, aim.Y, height);
        fire.BeginRun(Tick, flat - way * TfwFireRules.RunReach, flat + way * TfwFireRules.RunReach, rate);
    }

    /// <summary>The aircraft moves on and its guns fire: every <see cref="TfwFireRules.VolleySeconds"/> each side gun takes the enemies on its side within the corridor.</summary>
    void TfwRunStep(int rate)
    {
        var fire = _tfw!.Fire;
        if (fire.Run is not { } run) return;
        float flown = (Tick - run.Start) / (float)rate * TfwFireRules.RunSpeed;
        if (flown >= run.Length) { fire.EndRun(); return; }
        uint every = Math.Max(1u, (uint)MathF.Round(TfwFireRules.VolleySeconds * rate));
        if ((Tick - run.Start) % every != 0) return;
        var musou = _musou!;
        var at = run.From + run.Direction * flown;
        var middle = at + run.Direction * ((TfwFireRules.GunsAhead - TfwFireRules.GunsBehind) / 2f);
        float reach = MathF.Sqrt(TfwFireRules.Corridor * TfwFireRules.Corridor + MathF.Pow((TfwFireRules.GunsAhead + TfwFireRules.GunsBehind) / 2f, 2f));
        int n = musou.Query(middle with { Z = 0f }, reach, _fireNear, verticalReach: 4096f);
        if (n == 0) return;
        MusouBeginTick();
        for (int gun = 0; gun < 2; gun++)
        {
            float sign = gun == 0 ? 1f : -1f;
            var muzzle = at + run.Side * (sign * TfwFireRules.GunSide) - Vector3.UnitZ * TfwFireRules.GunBelow;
            for (int round = 0; round < TfwFireRules.RoundsPerVolley; round++)
                TfwRound(run, at, muzzle, sign, n, (ulong)(gun * 16 + round));
        }
    }

    /// <summary>One round: an enemy of the gun's side with a clear line, by hash; a roll says whether it hits.</summary>
    void TfwRound(in TfwStrafe run, Vector3 at, Vector3 muzzle, float sign, int near, ulong index)
    {
        var fire = _tfw!.Fire;
        var musou = _musou!;
        int start = (int)(fire.Roll(Tick, 70, index) * near);
        int tried = 0;
        for (int k = 0; k < near && tried < TfwFireRules.RoundTries; k++)
        {
            if (!musou.TryGet(_fireNear[(start + k) % near], out var e) || !e.Alive) continue;
            var off = e.Position - at;
            float along = off.X * run.Direction.X + off.Y * run.Direction.Y;
            float lateral = (off.X * run.Side.X + off.Y * run.Side.Y) * sign;
            if (lateral < 0f || lateral > TfwFireRules.Corridor || along < -TfwFireRules.GunsBehind || along > TfwFireRules.GunsAhead) continue;
            tried++;
            var chest = MusouSim.Chest(e.Position, musou.Body(e.Type).Height);
            if (Blocked(muzzle, chest)) continue;
            bool hit = fire.Roll(Tick, 71, index) < TfwFireRules.RoundHitChance;
            if (hit)
            {
                musou.Damage(e.Id, 0, TfwFireRules.RoundDamage, Vector3.Normalize(chest - muzzle), out bool killed, marker: 0, cause: MusouDeathKind.Strafe);
                if (killed) fire.RoundKills++;
                fire.Round(Tick, muzzle, chest, true);
            }
            else
            {
                var miss = e.Position + new Vector3(fire.Roll(Tick, 72, index) * 4f - 2f, fire.Roll(Tick, 73, index) * 4f - 2f, 0f);
                fire.Round(Tick, muzzle, miss, false);
            }
            return;
        }
    }

    /// <summary>Test aid: one shell lands at <paramref name="at"/> now.</summary>
    public void TfwShellForTests(Vector3 at) => TfwShellLands(at);

    /// <summary>The heading across <paramref name="player"/>'s view of <paramref name="at"/>, so a barrage walks across the screen and not onto its caller.</summary>
    internal float TfwFireTestHeading(int player, Vector3 at)
    {
        if (!_players.TryGetValue(player, out var p)) return 0f;
        var to = at - p.Position;
        return MathF.Atan2(to.Y, to.X) + MathF.PI / 2f;
    }

    /// <summary>Test aid (<c>SM_TFW_FIRE_TEST</c>, <see cref="TfwCommand.FireTest"/>, and tests): a barrage and/or a run now through <paramref name="aim"/> on <paramref name="heading"/>. False without a battle.</summary>
    public bool TfwFireForTests(bool barrage, bool strafe, Vector3 aim, float heading)
    {
        if (_tfw?.Battle is null) return false;
        int rate = Math.Max(1, Config.TickRate);
        aim.Z = TfwGroundAt(aim, aim.Z);
        if (barrage) TfwBarrage(aim, heading, rate);
        if (strafe && _tfw.Fire.Run is null) TfwBeginRun(aim, heading, rate);
        return true;
    }
}
