using System;
using Godot;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Musou half of the test aid <c>--bolt-smoke-scene</c> (NetworkView.BoltSmokeScene.cs; fx/bolt-smoke proof, 2026-11-15): 8 m ahead of the player, left to right, three body hits of the chosen weapon on an Ork (the real
/// <c>OrkBloodHit</c>), the kills of Orks by that weapon's shot (a body kill and an arm kill, the real <c>OrkBloodDeath</c> through the zone the aid forces) and by its bolt blast (the frag detonation at the bolt blast's drawn size
/// and the blast's kill). Presentation only; nothing is sent.
/// </summary>
public partial class NetworkView
{
    partial void ExtrasBoltSmokeScene(in BoltScene s, string gun, ref bool played)
    {
        if (!MusouActive || _config is null) return;
        played = true;
        var from = ToGodot(s.Eye);
        Vector3 Chest(Num.Vector3 at) => ToGodot(at + new Num.Vector3(0f, 0f, 1.2f));
        // 1. three hits on one Ork, 2.5 m to the left
        var hitAt = s.Target - s.Right * 1.8f;
        for (int k = 0; k < 3; k++)
        {
            int n = k;
            BoltSceneAt(n * 0.12, () =>
            {
                var end = Chest(hitAt) + new Vector3(0f, 0.1f * n, 0f);
                OrkBloodHit(null, n == 2, false, end, (end - from).Normalized(), hitAt, 0);
            });
        }
        // 2. two shot kills: the body (whole-body burst) and an arm (a dismemberment)
        Kill(s, s.Target - s.Right * 0.6f, "body", MusouDeathKind.Shot, 0.30, 11);
        Kill(s, s.Target + s.Right * 0.6f, "left_arm", MusouDeathKind.Shot, 0.55, 12);
        // 3. the bolt blast, 2.5 m to the right: the frag detonation, and its kill
        int frag = -1;
        for (int i = 0; i < _config.Thrown.Count && frag < 0; i++) if (SmSim.Extras.Musou.Campaign.TfwCombatRules.IsFrag(_config.Thrown[i])) frag = i;
        var blastAt = s.Target + s.Right * 1.8f;
        if (frag >= 0)
            BoltSceneAt(0.42, () => FxDetonation(new SmNet.ProjectileView(0, frag, 0, blastAt, SmNet.ProjectileFlags.Thrown), BoltBlastFxScale * BoltBlastSizeOf(1.25f), BlastDustShare(1.25f), flashOnly: BlastFlashOnly(1.25f)));
        Kill(s, blastAt, "body", MusouDeathKind.BoltBlast, 0.42, 13);
    }

    void Kill(in BoltScene s, Num.Vector3 at, string zone, MusouDeathKind cause, double when, ushort id)
    {
        var push = s.Fwd; var eye = new Vector3(s.Eye.X, s.Eye.Y, s.Eye.Z);   // the camera in game space, as the deaths path gives it
        BoltSceneAt(when, () =>
        {
            _bsZone = zone;
            var death = new MusouDeathShown(id, MusouEnemyType.Boy, at, 0f, push, cause, 0, (uint)id);
            OrkBloodDeath(death, eye);
            _bsZone = null;
        });
    }
}

public partial class NetworkView
{
    partial void ExtrasBoltSceneReady(ref bool ready) { if (MusouActive && OrkBloodData() is null) ready = false; }
}
