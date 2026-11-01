using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using SmNet;
using SmNet.Extras.Musou;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Musou Mode's big frag on the client (pass 2, item 21) and the test aid of the melee milestone (M5). A frag's blast is the core's detonation, at the original effect's size; each record of tag 11 kind
/// <see cref="TfwFireKind.Frag"/> adds the same original effect in two rings out to the blast's real reach, a beat apart, so it reads as one blast the size of its radius (no new asset, no new hook).
/// <c>--tfw-test-melee [--tfw-test dir]</c>: a scripted player on a real server sprints into the crowd, throws, swings and fires, through the real actions, and checks what comes back.
/// </summary>
public partial class NetworkView
{
    const int FragInnerRing = 6, FragOuterRing = 10;
    const float FragInnerAt = 0.4f, FragOuterAt = 0.8f, FragInnerDelay = 0.07f, FragOuterDelay = 0.16f;

    readonly Dictionary<(uint Tick, ushort Projectile), double> _tfwFragPlayed = new();
    readonly List<(double At, ProjectileView View)> _tfwFragRing = new();
    int _tfwFragsShown;

    /// <summary>Tag 11's frag records (this frame's sections: <c>TfwClientState.NewFire</c> is empty by now) and the rings' effects that are due. Each (tick, projectile) once: the server repeats a record.</summary>
    void TfwFragFrame()
    {
        foreach (var section in _tfwSections)
        {
            if (section.Tag != TfwWire.FireTag || TfwWire.ReadFire(section.Payload) is not { } records) continue;
            foreach (var fire in records)
            {
                if (fire.Kind != TfwFireKind.Frag || !_tfwFragPlayed.TryAdd((fire.Tick, fire.Subject), _connectedFor)) continue;
                _tfwFragsShown++;
                GD.Print($"t {_connectedFor:F1} s: war: frag blast at ({fire.At.X:F1}, {fire.At.Y:F1}, {fire.At.Z:F1}), radius {fire.To.Y:F1}");
                if (Lean || DisplayServer.GetName() == "headless") continue;
                TfwFragRing(fire, FragInnerRing, FragInnerAt, FragInnerDelay);
                TfwFragRing(fire, FragOuterRing, FragOuterAt, FragOuterDelay);
            }
        }
        for (int i = _tfwFragRing.Count - 1; i >= 0; i--)
        {
            if (_tfwFragRing[i].At > _connectedFor) continue;
            FxDetonation(_tfwFragRing[i].View);
            _tfwFragRing.RemoveAt(i);
        }
        if (_tfwFragPlayed.Count > 32)
            foreach (var key in new List<(uint, ushort)>(_tfwFragPlayed.Keys)) if (_connectedFor - _tfwFragPlayed[key] > 10.0) _tfwFragPlayed.Remove(key);
    }

    /// <summary>One ring: each effect on the ground under its place (none over a drop or inside a wall's far side: that one is left out).</summary>
    void TfwFragRing(in TfwFireEvent fire, int count, float share, float delay)
    {
        float radius = fire.To.Y * share;
        for (int k = 0; k < count; k++)
        {
            float angle = (k + (uint)fire.Subject % 7 * 0.13f) * MathF.Tau / count;
            var at = fire.At + new Num.Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0f);
            if (_client?.Collision is not { } world || world.Weapons.Raycast(at + new Num.Vector3(0f, 0f, 2.5f), new Num.Vector3(0f, 0f, -1f), 6f) is not { } floor) continue;
            at.Z = at.Z + 2.5f - floor.Distance;
            _tfwFragRing.Add((_connectedFor + delay + 0.02 * k, new ProjectileView(0, (int)fire.To.X, 0, at, ProjectileFlags.Thrown)));
        }
    }

    // ---------------------------------------------------------------- the test aid

    InputButtons _tfwTestButtons;

    /// <summary>The scripted player's held buttons: on the private desktop the mouse is never captured, and the sample ignores the buttons that need it.</summary>
    void TfwTestInput(ref PlayerInput input)
    {
        if (_tfwTestButtons != InputButtons.None) input = input with { Buttons = input.Buttons | _tfwTestButtons };
    }

    void TfwAction(GameAction action, bool down)
    {
        var button = ControlActions.Info(action).Buttons;
        _tfwTestButtons = down ? _tfwTestButtons | button : _tfwTestButtons & ~button;
    }

    async Task TfwActionTap(GameAction action) { TfwAction(action, true); await TfwFrames(4); TfwAction(action, false); await TfwFrames(4); }

    /// <summary>Faces the middle of the crowd within <paramref name="within"/> metres; false with nobody there.</summary>
    bool TfwFaceCrowd(float within)
    {
        var me = _client!.Predicted.Position;
        if (_musouState.MiddleNear(me, within) is not { } look) return false;
        _yaw = MathF.Atan2(-(look.X - me.X), look.Y - me.Y);
        return true;
    }

    int TfwKnockedShown()
    {
        int n = 0;
        foreach (var e in _tfwMeleeShown) if (e.State == MusouEnemyState.Knocked) n++;
        return n;
    }

    IReadOnlyList<MusouShown> _tfwMeleeShown = [];

    /// <summary>The script (see the class summary). The server runs with <c>SM_TFW_KEEP_HUMAN=1</c> and <c>SM_TFW_FIGHT_LOG=&lt;s&gt;</c>, whose TFWFIGHT lines carry the counters; these checks are what a client sees.</summary>
    async Task TfwTestMelee()
    {
        var client = _client!;
        if (!await TfwTestDeploy(TfwLane.B)) return;
        await TfwWait(1.0);
        var point = _tfwState.Objectives[0].Position;
        await TfwPathTo(point, "walked towards the Orks", 45.0, () => _musouState.MiddleNear(client.Predicted.Position, 22f) is not null);
        TfwCheck("a crowd is near", TfwFaceCrowd(30f));
        await TfwShot("melee-00-crowd");

        // Item 21: one throw, three frags, from outside the crowd.
        TfwFaceCrowd(30f);
        int carried = client.Predicted.Grenades;
        TfwCheck("the player carries grenades", carried > 0, carried.ToString());
        int before = _tfwFragsShown, flying = 0;
        await TfwActionTap(GameAction.Grenade);
        double fuse = _connectedFor + 6.0;
        while (_connectedFor < fuse && _tfwFragsShown < before + 3)
        {
            int thrown = 0;
            foreach (var p in client.Projectiles()) if (p.Thrown) thrown++;
            flying = Math.Max(flying, thrown);
            await TfwFrames();
        }
        await TfwShot("melee-20-frag");
        await TfwWait(0.15);
        await TfwShot("melee-21-frag");
        await TfwWait(0.3);
        await TfwShot("melee-22-frag");
        await TfwWait(0.6);
        await TfwShot("melee-23-frag");
        TfwCheck("one throw makes three grenades", flying >= 3, flying.ToString());
        TfwCheck("three big blasts came back", _tfwFragsShown >= before + 3, (_tfwFragsShown - before).ToString());
        TfwCheck("one throw spends one grenade", client.Predicted.Grenades == carried - 1, $"{carried} to {client.Predicted.Grenades}");

        // Item 7: sprint into them, round the walls, and film the falls near by.
        int fell = 0, filmed = 0;
        TfwAction(GameAction.Sprint, true);
        for (int run = 0; run < 5 && (fell == 0 || filmed < 4); run++)
        {
            double until = _connectedFor + 7.0;
            var goal = _musouState.MiddleNear(client.Predicted.Position, 30f) ?? point;
            var sprint = TfwPathTo(goal, $"sprinted into the crowd ({run + 1})", 8.0, () =>
            {
                fell = Math.Max(fell, TfwKnockedShown());
                return _connectedFor >= until;
            });
            while (!sprint.IsCompleted)
            {
                var me = client.Predicted.Position;
                foreach (var e in _tfwMeleeShown)
                    if (e.State == MusouEnemyState.Knocked && e.StateSeconds > 0.4f && Num.Vector3.Distance(e.Position, me) < 7f && filmed < 8 && _connectedFor >= _tfwFilmAt)
                    {
                        _yaw = MathF.Atan2(-(e.Position.X - me.X), e.Position.Y - me.Y);
                        await TfwFrames();
                        await TfwShot($"melee-10-fall-{filmed++}");
                        _tfwFilmAt = _connectedFor + 0.4;
                        break;
                    }
                await TfwFrames();
            }
            await sprint;
        }
        TfwAction(GameAction.Sprint, false);
        TfwCheck("sprinting into the crowd throws Orks down", fell > 0, "none seen in Knocked");
        GD.Print($"TFWMELEE most Orks seen down at once: {fell}");

        // Item 22: swings in the crowd.
        for (int k = 0; k < 12; k++)
        {
            TfwFaceCrowd(30f);
            TfwKey(Key.W, true);
            await TfwWait(0.5);
            TfwKey(Key.W, false);
            TfwFaceCrowd(8f);
            await TfwActionTap(k % 2 == 0 ? GameAction.Melee : GameAction.StunStrike);
            if (k % 3 == 0) await TfwShot($"melee-30-cleave-{k / 3}");
            await TfwWait(0.4);
        }
        GD.Print($"TFWMELEE own most hit in one tick by melee: {_tfwBestCleave}");
        TfwCheck("a swing struck enemies", _tfwBestCleave > 0);

        // Item 24: shots along the crowd (the count starts here: a frag's hits share the markers).
        _tfwBestPierce = 0;
        TfwFaceCrowd(30f);
        TfwAction(GameAction.Fire, true);
        await TfwWait(3.0);
        TfwAction(GameAction.Fire, false);
        await TfwShot("melee-40-shots");
        GD.Print($"TFWMELEE own most enemies hit by one shot: {_tfwBestPierce}");
        TfwCheck("the run is still on", TfwNow.Phase != TfwRunPhase.Won);
    }

    int _tfwBestCleave, _tfwBestPierce;
    double _tfwFilmAt;

    /// <summary>The test aid's count of the hits in this frame's events, by tick: melee hits (marker 3) and shot hits (markers 1 and 2) of the own player.</summary>
    void TfwMeleeCount(IReadOnlyList<MusouShown> shown)
    {
        if (!CmdFlag("tfw-test-melee")) return;
        _tfwMeleeShown = shown;
        int own = _client?.PlayerId ?? 0;
        foreach (var e in _musouState.NewEvents)
        {
            if (e.Kind != MusouEventKind.Hit || e.Player != own) continue;
            var key = (e.Tick, e.Value == 3);
            _tfwHitsByTick[key] = _tfwHitsByTick.GetValueOrDefault(key) + 1;
            if (e.Value == 3) _tfwBestCleave = Math.Max(_tfwBestCleave, _tfwHitsByTick[key]);
            else _tfwBestPierce = Math.Max(_tfwBestPierce, _tfwHitsByTick[key]);
        }
    }

    readonly Dictionary<(uint, bool), int> _tfwHitsByTick = new();
}
