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
                bool weather = false;
                WeatherEnvelope(section.Tag, section.Payload, ref weather);   // weather (client/Extras/Weather): tag 48
                if (weather) continue;
                bool warfront = false;
                WarfrontEnvelope(section.Tag, section.Payload, ref warfront);   // WARFRONT-HOOK
                if (warfront) continue;   // Warfront's tags (40 to 46, the squad's 32 to 34), on a Warfront server only
                bool execution = false;
                ExecutionsEnvelope(section.Tag, section.Payload, ref execution);   // EXECUTIONS-HOOK: tag 50 (ExecutionWire), on an executions server only
                if (execution) continue;
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
        WeatherFrame(me, delta);   // weather (client/Extras/Weather): the clock, the look through the lighting grade, rain
        WarfrontFrame(me, delta);   // WARFRONT-HOOK: the stage flow (commands, panels, rejoin; client/Extras/Warfront/Stage)
        SpearPoseFrame(me, others, delta);   // Musou's power spear: its clips between strikes, the fades and the spear on the hands (Musou/Arsenal/Spear/NetworkView.SpearPose.cs)
        if (_config is { } executionsConfig && executionsConfig.Extras.HasFlag(ContentExtras.Executions)) ExecutionsFrame(me, others, delta);   // EXECUTIONS-HOOK: the paired animation, camera, HUD prompt and shield shell (X5, X6)
    }

    // ---- Executions (docs/added-content.md section 13): X5 and X6 implement these in client/Extras/Executions/; each call above is marked // EXECUTIONS-HOOK. ----

    /// <summary>An envelope section: sets <paramref name="handled"/> for tag 50 (<c>ExecutionWire.Tag</c>) and queues it.</summary>
    partial void ExecutionsEnvelope(byte tag, byte[] payload, ref bool handled);
    /// <summary>Once a frame after the other features (only with the executions flag).</summary>
    partial void ExecutionsFrame(PlayerState me, IReadOnlyList<PlayerState> others, double delta);

    /// <summary>The own damage flash and HUD vignette: off while the own player is the Musou Dreadnought (owner 2026-11-15, "Turn flash off"; Musou Mode only).</summary>
    partial void ExtrasDamageFlashOff(PlayerState me, ref bool off) { if (IsDreadnoughtClass(me.Class)) off = true; }

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

    partial void ExtrasMapContent(string name, ref MapContent? map)
    {
        TfwAshfallContent(name, ref map);
        WarfrontMapContent(name, ref map);   // WARFRONT-HOOK: a lane slot's generated band (client/Extras/Warfront/Maps)
        MapmakerContent(name, ref map);
    }

    partial void ExtrasMapVisuals(string name, MapContent map, ref bool drawn)
    {
        TfwAshfallVisuals(name, ref drawn);
        WarfrontMapVisuals(name, ref drawn);   // WARFRONT-HOOK
        MapmakerVisuals(name, ref drawn);
    }

    // Warfront's hooks (slice W5b; implemented under client/Extras/Warfront/, each a no-op unless the match has the Warfront flag).
    partial void WarfrontCrowdPerf();   // WARFRONT-HOOK: the crowd's levels of detail (client/Extras/Warfront/NetworkView.WarfrontPerf.cs), once before the looks bake
    partial void WarfrontMapContent(string name, ref MapContent? map);
    partial void WarfrontMapVisuals(string name, ref bool drawn);
    partial void WarfrontEnvelope(byte tag, System.ReadOnlySpan<byte> payload, ref bool handled);
    partial void WarfrontFrame(PlayerState me, double dt);
    partial void WarfrontPickerBusy(ref bool busy);
    partial void WarfrontSpectateView(PlayerState me, ref (Godot.Vector3 Eye, Godot.Vector3 Target)? view);

    /// <summary>A panel of added content that takes the game's input (Warfront's card picker and squad panel).</summary>
    partial void ExtrasPickerBusy(ref bool busy) => WarfrontPickerBusy(ref busy);   // WARFRONT-HOOK

    /// <summary>The spectator camera of added content (Warfront: a fallen player's or a held joiner's watch).</summary>
    partial void ExtrasSpectateView(PlayerState me, ref (Godot.Vector3 Eye, Godot.Vector3 Target)? view) => WarfrontSpectateView(me, ref view);   // WARFRONT-HOOK

    partial void ExtrasMapFile(ref string file) { MapmakerMapFile(ref file); TfwMapFile(ref file); }

    partial void ExtrasMapCollision(string name, ref SmSim.CollisionWorld world, ref ulong hash) => TfwMapCollision(name, ref world, ref hash);

    partial void ExtrasMapCollisionVisuals() => TfwMapCollisionVisuals();
}
