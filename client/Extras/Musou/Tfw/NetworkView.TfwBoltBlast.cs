using System;
using System.Collections.Generic;
using Godot;
using SmNet;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Musou Mode's bolt blasts on the client (the owner, 2026-11-07: every bolt weapon's shot blows up at each enemy it goes through). Each record of tag 11 kind <see cref="TfwFireKind.BoltBlast"/> plays
/// the original frag detonation (its effect and its sound, the thrown item the record names) once at the blast's point; no new asset. A heavy bolter in a crowd sets off dozens a second, so the
/// client thins what it draws (ours): a blast within <see cref="BoltBlastMergeMetres"/> of one drawn in the last <see cref="BoltBlastMergeSeconds"/> is left out (the damage is the server's either way),
/// at most <see cref="BoltBlastsPerFrame"/> effects and <see cref="BoltBlastSoundsPerFrame"/> sounds a frame, and each blast shakes the camera a quarter of a frag's. The effect plays at
/// <see cref="BoltBlastFxScale"/> of the frag's size (owner, 2026-11-08: "make the vfx 60% smaller").
/// <para>
/// musou/bolt-blast-sound (owner, 2026-11-10: "if the sound is triggered so often by the bolter explosions behaving like grenade explosions - disable that sound effect for bolter explosions and leave it
/// for regular grenades"): a bolt blast no longer posts the frag's detonation sound (measured: up to 2 a frame, the largest single source of the heavy-combat roar and of the voices stolen at the cap;
/// musou/sound-ab). Its effect, shake and damage stay; a thrown grenade's detonation sounds as before, and so do the bolter's own shot, impact and first-hit smack and blast. Restore switch:
/// <c>--musou-old-bolt-blast-sound</c> (the blasts post the frag's detonation sound again, as before).
/// </para>
/// </summary>
public partial class NetworkView
{
    const int BoltBlastsPerFrame = 6, BoltBlastSoundsPerFrame = 2;
    /// <summary><c>--musou-old-bolt-blast-sound</c>: each bolt blast posts the frag's detonation sound again (the behaviour before musou/bolt-blast-sound).</summary>
    static readonly bool OldBoltBlastSound = CmdFlag("musou-old-bolt-blast-sound");
    /// <summary>Bolt blasts that passed the thinning and would sound under the old rule (posted or not): the test aid's count (musou/sound-ab).</summary>
    internal int BoltBlastSoundable { get; private set; }
    const float BoltBlastMergeMetres = 1.0f, BoltBlastMergeSeconds = 0.12f, BoltBlastShake = 0.25f;
    /// <summary>The bolt blast's effect against the frag detonation it reuses: 60 % smaller (owner, 2026-11-08; until then 1).</summary>
    internal const float BoltBlastFxScale = 0.4f;

    readonly Dictionary<(uint Tick, ushort Count), double> _tfwBoltPlayed = new();
    readonly List<(double At, Num.Vector3 Where)> _tfwBoltDrawn = new();
    int _tfwBoltBlastsShown, _tfwBoltBlastsDrawn;

    /// <summary>Tag 11's bolt blast records of this frame's sections (as <see cref="TfwFragFrame"/>), each (tick, count) once.</summary>
    void TfwBoltBlastFrame()
    {
        int drawn = 0, sounded = 0;
        _tfwBoltDrawn.RemoveAll(d => _connectedFor - d.At > BoltBlastMergeSeconds);
        foreach (var section in _tfwSections)
        {
            if (section.Tag != TfwWire.FireTag || TfwWire.ReadFire(section.Payload) is not { } records) continue;
            foreach (var fire in records)
            {
                if (fire.Kind != TfwFireKind.BoltBlast || !_tfwBoltPlayed.TryAdd((fire.Tick, fire.Subject), _connectedFor)) continue;
                _tfwBoltBlastsShown++;
                if (_bot || CmdFlag("tfw-test-melee")) GD.Print($"t {_connectedFor:F1} s: war: bolt blast at ({fire.At.X:F1}, {fire.At.Y:F1}, {fire.At.Z:F1}), radius {fire.To.Y:F1}");
                if (drawn >= BoltBlastsPerFrame || fire.To.X < 0f || Near(fire.At)) continue;
                var view = new ProjectileView(0, (int)fire.To.X, 0, fire.At, ProjectileFlags.Thrown);
                if (sounded < BoltBlastSoundsPerFrame)
                {
                    BoltBlastSoundable++;
                    if (OldBoltBlastSound && !MusouSoundIsOff(SmContent.MusouSoundOff.BoltBlast)) SoundDetonation(view, "bolt blast");   // musou/bolt-blast-sound: only with the restore switch (musou/sound-ab: boltblast off)
                    sounded++;
                }
                if (Lean || DisplayServer.GetName() == "headless") { drawn++; _tfwBoltDrawn.Add((_connectedFor, fire.At)); continue; }
                var shake = _shake;
                if (FxDetonation(view, BoltBlastFxScale)) _tfwBoltBlastsDrawn++;
                _shake = shake + (_shake - shake) * BoltBlastShake;
                drawn++;
                _tfwBoltDrawn.Add((_connectedFor, fire.At));
            }
        }
        if (_tfwBoltPlayed.Count > 512)
            foreach (var key in new List<(uint, ushort)>(_tfwBoltPlayed.Keys)) if (_connectedFor - _tfwBoltPlayed[key] > 5.0) _tfwBoltPlayed.Remove(key);

        bool Near(Num.Vector3 at)
        {
            foreach (var d in _tfwBoltDrawn) if (Num.Vector3.DistanceSquared(d.Where, at) < BoltBlastMergeMetres * BoltBlastMergeMetres) return true;
            return false;
        }
    }
}
