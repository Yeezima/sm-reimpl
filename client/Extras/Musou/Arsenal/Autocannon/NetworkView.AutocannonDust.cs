using System;
using Godot;
using SmSim.Extras.Musou.Arsenal;
using SmContent.Extras.Musou.Arsenal;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// musou/autocannon-dust (client presentation only, Musou Mode only; no simulation, wire, protocol or config-hash change). The owner, 2026-11-12: "for the autocannon in musou mode - make it or the aoe
/// explosion it causes on hit kick up 50% less dust. what should happen also is chunks of debris similar to the ground pound effect should fly out as a reaction to the explosive force of the high
/// explosive autocannon round detonating."
/// <list type="bullet">
/// <item><b>Dust:</b> the round's world and body hit (<c>autocannon_dmg</c>, through <see cref="ExtrasImpactDustShare"/>) draw <see cref="DustShare"/> (half) of the dust and debris sprites they drew, and its 2.5 m blast (the frag detonation, <see cref="TfwBoltBlastFrame"/>) draws <see cref="BlastDust"/> (none; owner 2026-11-13) (<see cref="FxEffect.DustShare"/>: the share of the particles of each spray throwing alpha-blended sprites, that share of the alpha of those outside a
/// spray; a dropped particle loses only its alpha sprites, its additive flash and fire stay); sizes and timings are the data's, flashes, fire, decals and lights are untouched. Before: every particle at 1.</item>
/// <item><b>Debris:</b> each drawn blast also throws chunks of the ground it stands over (the pound's <see cref="GroundShards"/>: the same physics chunks, flat slabs, tints and bounce) on a node of their own
/// (<see cref="ShardCap"/> live at most, so a pound's 120 are never eaten), <see cref="ShardMin"/> to <see cref="ShardMax"/> at the 2.5 m blast (scaled by radius over <see cref="SmSim.AutocannonRules.BlastRadius"/>),
/// at <see cref="ShardSpeed"/> of the pound's speeds, <see cref="ShardSize"/> of its sizes and <see cref="ShardSpread"/> of its spread, from the wall within a metre of the blast or the ground under it;
/// at most <see cref="ShardBurstsPerFrame"/> blasts a frame, only within <see cref="ShardRange"/> m of the camera. **Ours**: the numbers are estimates for a blast a fifth of a pound's radius.</item>
/// </list>
/// Restore switch <c>--musou-old-autocannon-fx</c> (a presentation test aid): the full dust and no chunks, as before. <c>--no-shards</c> also leaves the chunks out.
/// </summary>
public partial class NetworkView
{
    /// <summary>The share of the autocannon's dust and debris sprites that is drawn (owner 2026-11-12: 50 % less; ours is a rounding of "50%").</summary>
    internal const float DustShare = 0f;   // owner 2026-11-15: "no dust effect triggered as a result of an autocannon shot. leave only the bright yellow explosive flash" (0.5 from 2026-11-12 to 2026-11-15)
    /// <summary>The share of the autocannon's 2.5 m blast's dust and debris sprites that is drawn: none (owner 2026-11-13: "remove the dust from the autocannon aoe blast"; the flash, fire, light and decals are additive or not sprites of dust and stay; the hit's own effect keeps <see cref="DustShare"/>).</summary>
    internal const float BlastDust = 0f;
    /// <summary>The share of a bolt weapon's blast (the frag's detonation at the bolt blast's size) whose dust, smoke and debris sprites are drawn: none (owner 2026-11-15: "no black smoke clouds ... when shooting bolt weapons in musou mode"; its additive flash, fire, light and decal stay). <c>--musou-old-bolt-smoke</c> draws them as before.</summary>
    internal const float BoltBlastDust = 0f;
    /// <summary>Whether a drawn autocannon blast also throws chunks of the ground (owner 2026-11-15: "leave only the bright yellow explosive flash": none; <c>--musou-old-autocannon-fx</c> restores the chunks with the dust).</summary>
    internal const bool BlastChunks = false;
    /// <summary><c>--musou-old-bolt-smoke</c>: the bolt blasts draw the frag detonation's smoke and debris sprites again (the behaviour before musou/bolt-smoke).</summary>
    static readonly bool OldBoltSmoke = CmdFlag("musou-old-bolt-smoke") || CmdFlag("old-bolt-smoke");
    /// <summary>The chunks of a blast at the autocannon's blast radius (ours), at most live at once, the most blasts that throw a frame, and the camera distance beyond which none are thrown (m).</summary>
    internal const int ShardMin = 5, ShardMax = 9, ShardCap = 36, ShardBurstsPerFrame = 2;
    internal const float ShardRange = 45f;
    /// <summary>The chunks' launch speeds, sizes and spread against the ground pound's (ours).</summary>
    internal const float ShardSpeed = 0.55f, ShardSize = 0.7f, ShardSpread = 0.6f;

    /// <summary><c>--musou-old-autocannon-fx</c>: the autocannon's hit and blast draw all their dust and throw no chunks (the behaviour before musou/autocannon-dust).</summary>
    static readonly bool OldAutocannonDust = CmdFlag("musou-old-autocannon-fx");

    GroundShards? _blastShards;
    int _blastShardBursts, _blastShardsThrown;
    /// <summary>The test aid's counts: blasts that threw chunks and the chunks they threw.</summary>
    internal (int Bursts, int Chunks) BlastShardCounts => (_blastShardBursts, _blastShardsThrown);

    partial void ExtrasImpactDustShare(string wargear, ref float share)
    {
        if (OldAutocannonDust || !MusouActive || !(ArsenalRules.IsAutocannon(wargear) || string.Equals(wargear, Arsenal.AutocannonTwinFile, StringComparison.OrdinalIgnoreCase))) return;
        share = DustShare;
    }

    /// <summary>
    /// musou/autocannon-flash (owner 2026-11-15: "autocannon on hit effects in musou mode should trigger where the shot hits the terrain even when no enemy target is hit"): a round that struck the
    /// level with no enemy plays its blast's bright flash there (the frag detonation at the autocannon blast's drawn size, no dust: <see cref="BlastDust"/>); the server's blast, which does the area
    /// damage, still goes off only at an enemy the round struck, so a terrain hit is the picture alone. Restore switch <c>--musou-old-autocannon-fx</c>.
    /// </summary>
    partial void ExtrasTerrainBlast(string wargear, Vector3 end, Vector3 normal)
    {
        if (OldAutocannonDust || !MusouActive || _config is null || !(ArsenalRules.IsAutocannon(wargear) || string.Equals(wargear, Arsenal.AutocannonTwinFile, StringComparison.OrdinalIgnoreCase))) return;
        int frag = -1;
        for (int i = 0; i < _config.Thrown.Count && frag < 0; i++) if (SmSim.Extras.Musou.Campaign.TfwCombatRules.IsFrag(_config.Thrown[i])) frag = i;
        if (frag < 0) return;
        float radius = SmSim.AutocannonRules.BlastRadius * SmSim.Extras.Musou.MusouCombatRules.AutocannonAoeScale;
        FxDetonation(new SmNet.ProjectileView(0, frag, 0, ToGame(end), SmNet.ProjectileFlags.Thrown), BoltBlastFxScale * BoltBlastSizeOf(radius), BlastDustShare(radius), flashOnly: true);
    }

    /// <summary>The dust share a blast of <paramref name="radius"/> metres is drawn with: <see cref="BlastDust"/> for the autocannon's (a radius above the bolt blast's), 1 for every other blast.</summary>
    /// <summary>musou/autocannon-flash: whether a blast of <paramref name="radius"/> metres draws only its bright flash (the autocannon's, not the bolt blast's; <c>--musou-old-autocannon-fx</c> restores the whole effect).</summary>
    bool BlastFlashOnly(float radius) => !OldAutocannonDust && SmSim.AutocannonRules.LosesDust(radius);
    float BlastDustShare(float radius) => BlastDustShareOf(radius, OldAutocannonDust, OldBoltSmoke);
    /// <summary>The pure radius-to-share mapping (tested): <see cref="BlastDust"/> above the bolt blast's radius unless restored, else 1.</summary>
    internal static float BlastDustShareOf(float radius, bool restored, bool boltRestored = false) => restored && boltRestored ? 1f : !restored && SmSim.AutocannonRules.LosesDust(radius) ? BlastDust : boltRestored || SmSim.AutocannonRules.LosesDust(radius) ? 1f : BoltBlastDust;

    /// <summary>The chunks of one autocannon blast at <paramref name="at"/> (game space, blast radius <paramref name="radius"/> m); the number thrown, 0 when none.</summary>
    int BlastDebris(Num.Vector3 at, float radius, int seed, ref int burstsThisFrame)
    {
        if (OldAutocannonDust || NoShards || burstsThisFrame >= ShardBurstsPerFrame || _client?.Collision is not { } world || _catalogue is null) return 0;
        if (_camera is { } cam && cam.GlobalPosition.DistanceTo(ToGodot(at)) > ShardRange) return 0;
        // The surface the blast stands on: a wall or floor within a metre (as the detonation's decals), else the ground up to 3 m below it.
        var (point, normalOrNull, surface) = DetonationSurface(at);
        var normal = normalOrNull ?? Vector3.Up;
        if (normalOrNull is null)
        {
            if (world.Weapons.Raycast(at, -Num.Vector3.UnitZ, 3f) is not { } down) return 0;
            var n = down.Normal.Z < 0f ? -down.Normal : down.Normal;
            point = ToGodot(down.Point); normal = DirToGodot(n).Normalized(); surface = world.Weapons.MaterialOf(down.Triangle);
        }
        if (_blastShards is not { } shards || !IsInstanceValid(shards))
        {
            if (_ragdollWorld is null || !IsInstanceValid(_ragdollWorld)) EnsureRagdollWorld();
            _blastShards = shards = new GroundShards { Name = "BlastShards", Cap = ShardCap };
            AddChild(shards);
        }
        float k = Math.Clamp(radius / SmSim.AutocannonRules.BlastRadius, 0.5f, 1.5f);
        int thrown = shards.Spawn(point, normal, _catalogue.SurfaceName(surface), seed, ((int)MathF.Round(ShardMin * k), (int)MathF.Round(ShardMax * k)), ShardSpeed, null, ShardSize, ShardSpread);
        burstsThisFrame++; _blastShardBursts++; _blastShardsThrown += thrown;
        return thrown;
    }
}
