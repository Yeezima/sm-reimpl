using System.Collections.Generic;
using SmNet;
using SmNet.Extras.LoyalistBarks;
using SmNet.Extras.Musou;
using SmNet.Extras.Musou.Campaign;
using SmContent;
using SmSim;

namespace SmClient;

/// <summary>
/// The networked view's frame hook for added content (docs/added-content.md): the one implementation of <see cref="ExtrasFrame"/>, a dispatcher. Once a frame it
/// drains the envelope queue (<c>GameClient.TakeExtras</c>) exactly once and hands each section to the feature that owns its tag (the loyalist barks: 2;
/// Musou: 3 to 6; Musou Mode: 7 to 15); a section of a tag nobody here owns (gore's 1, a newer build's) is counted and skipped. Then each feature's frame runs, in a fixed
/// order; each returns at once unless its flag is set in the match's config.
/// </summary>
public partial class NetworkView
{
    readonly List<ExtrasSection> _barkSections = new(), _musouSections = new(), _tfwSections = new();

    /// <summary>Envelope sections of a tag no feature of this build owns (skipped).</summary>
    public int ExtrasUnknownSections { get; private set; }

    partial void ExtrasFrame(PlayerState me, IReadOnlyList<PlayerState> others, double delta)
    {
        _barkSections.Clear();
        _musouSections.Clear();
        _tfwSections.Clear();
        if (_client is { Status: ClientStatus.Connected } client)
            foreach (var section in client.TakeExtras())
            {
                if (section.Tag == LoyalistShoutWire.Tag) _barkSections.Add(section);
                else if (MusouWire.Owns(section.Tag)) _musouSections.Add(section);
                else if (TfwWire.Owns(section.Tag)) _tfwSections.Add(section);
                else ExtrasUnknownSections++;
            }
        LoyalistBarksFrame(me, others, delta, _barkSections);
        MusouSoundAbFrame();   // musou/sound-ab: the A/B sound switches (Musou Mode only; every other mode puts them back as they were)
        MusouFrame(me, others, delta, _musouSections);
        Ph("tfw: guards, armies");   // musou/perf-pass: --frame-profile's phase
        TfwFrame(me, others, delta, _tfwSections);
        SpearPoseFrame(me, others, delta);   // Musou's power spear: its clips between strikes, the fades and the spear on the hands (Musou/Arsenal/Spear/NetworkView.SpearPose.cs)
    }

    /// <summary>The attack pose's drawn strike time: the Musou weight pass's retime (MW4), then Musou's power spear shows its strike's clip at the drawn time (both Musou Mode only).</summary>
    partial void ExtrasAttackTime(PlayerState state, bool own, ref float attackTime)
    {
        MusouPunchTime(state, own, ref attackTime);
        SpearStrikeShown(state, own, attackTime);
    }

    /// <summary>A map added content composes: Musou Mode's.</summary>
    partial void ExtrasMeleeTarget(PlayerState me, MeleeStep step, ref System.Numerics.Vector3? at, ref bool? armoured) => MusouMeleeTarget(me, step, ref at, ref armoured);

    partial void ExtrasShotMaterial(ShotView shot, int victim, ref bool? armoured) { MusouShotMaterial(shot, victim, ref armoured); MusouBolterOthersHit(shot, victim); }


    partial void ExtrasTrackEmitter(SoundEmitter emitter, bool own) => MusouTrackEmitter(emitter, own);

    partial void ExtrasFireLayers(WeaponSpec spec, SoundEmitter e, bool own) => MusouFireLayers(spec, e, own);

    partial void ExtrasOwnStrikeContact(PlayerState me, MeleeStep step) => MusouOwnStrikeContact(me, step);

    partial void ExtrasOwnShotEnd(WeaponSpec weapon, System.Numerics.Vector3 start, System.Numerics.Vector3 dir, ref float distance, ref bool enemy) => MusouOwnShotEnd(weapon, start, dir, ref distance, ref enemy);

    partial void ExtrasOwnShotContact(PlayerState me, WeaponSpec weapon, System.Numerics.Vector3 start, System.Numerics.Vector3 dir, System.Numerics.Vector3 end) => MusouOwnShotContact(me, weapon, start, dir, end);

    partial void ExtrasShakeTotal(float current, float amount, ref float total) => MusouShakeTotal(current, amount, ref total);

    partial void ExtrasMapContent(string name, ref MapContent? map) => TfwAshfallContent(name, ref map);

    partial void ExtrasMapVisuals(string name, MapContent map, ref bool drawn) => TfwAshfallVisuals(name, ref drawn);

    partial void ExtrasMapFile(ref string file) => TfwMapFile(ref file);

    partial void ExtrasMapCollision(string name, ref SmSim.CollisionWorld world, ref ulong hash) => TfwMapCollision(name, ref world, ref hash);
}
