using System;
using System.Numerics;
using SmSim.Extras.Musou;

namespace SmNet.Extras.Musou;

/// <summary>
/// Musou Mode, Ork hit reactions (owner 2026-11-13, "reference v3"; decision file decisions/2026-11-13-musou-ork-hit-reactions.md in the coordination repository). Client presentation only:
/// no simulation, wire, protocol or hash change. A hit Ork is drawn flinching away from the hit (torso recoil, pelvis shift, knee compression, feet planted) over whatever it is doing; its
/// facing, path and attack never change. Every hit re-kicks to the same size (no growth). The envelope is measured from the owner's reference clip (24 fps, frames 25 to 49 of the standing
/// sequence: the reaction reaches full size within one frame (42 ms), holds about 80 ms and is gone in about 330 ms; the excess over the idle sway read 8.4, 8.4, 7.5, 6.0, 4.5, 3.1, 2.2, 1.5,
/// 1.2 at 0, 42, 83, 125, 167, 208, 250, 292, 333 ms). Every number here is ours (kind: ours; confidence: low until a playtest); strengths follow the owner's typed table.
/// </summary>
public static class MusouOrkFlinchRules
{
    /// <summary>The reaction's rise to full size, its hold at full size, and its fall to nothing (s): measured from the reference (rise 1 frame = 42 ms, hold to about 83 ms, gone by about 330 ms).</summary>
    public const float Rise = 0.04f, Hold = 0.04f, Fall = 0.27f;
    /// <summary>The fall's curve: (1 - u) to this power, u the share of the fall elapsed; fits the reference's 0.68, 0.49, 0.32, 0.19, 0.10 at 125 to 292 ms within 0.1.</summary>
    public const float FallPower = 1.5f;
    /// <summary>Total seconds a reaction lasts after its kick.</summary>
    public const float Seconds = Rise + Hold + Fall;

    /// <summary>The most a flinch may be scaled up to (a charge's 2.5 and a hammer's step, a close blast): the pose never goes past this many base sizes. 3 while the base was half its size (a 57-degree torso); at the doubled base 2 keeps the deepest tip at 76 degrees and the legs in reach (owner 2026-11-13: "keep the scaling stagger strength" = every multiplier kept, only the top clipped).</summary>
    public const float MaxStrength = 2f;

    // ---- the pose at strength 1 (a Boy shot by the bolt pistol), the reference proxy translated onto the Ork rigs (ours)
    /// <summary>The pelvis and the torso move this far along the hit's direction (m) and the pelvis drops this far (m): the knees bend to keep the feet where they were.</summary>
    public const float PelvisBack = 0.14f, PelvisDrop = 0.09f;
    /// <summary>The torso's recoil: degrees tipped back at the three spine bones, the neck and the head (their sum 38, twice the first version's 19 at the owner's word of 2026-11-13, "increase the magnitude of all ork reactions by 2x of current"; the first pass at 11 measured about half the reference's visible motion: summed pixel change per figure area 9 against 15 to 26).</summary>
    public const float Spine1 = 10.0f, Spine2 = 9.0f, Spine3 = 9.0f, Neck = 4.0f, Head = 6.0f;

    // ---- the far crowd (the baked tier): the whole instance leans about its feet and shifts
    /// <summary>The lean (degrees) and the shift along the hit (m) of a far Ork at strength 1.</summary>
    public const float FarLean = 14f, FarShift = 0.12f;
    /// <summary>Within this far from the camera (m) a skeletal Ork flinches fully and a baked one jolts; past <see cref="FarMetres"/> nothing is drawn (ours).</summary>
    public const float FarMetres = 70f;

    /// <summary>Test aid only (<c>--flinch-scale</c> on the crowd test stage): every pose and lean is drawn at this share (0.5 draws the first version's magnitude for a side-by-side). 1 in the game.</summary>
    public static float TestScale = 1f;

    /// <summary>The envelope at <paramref name="age"/> seconds after a kick: 0 before it, rising to 1 over <see cref="Rise"/>, 1 over <see cref="Hold"/>, then falling to 0 over <see cref="Fall"/>.</summary>
    public static float Envelope(float age)
    {
        if (!(age > 0f)) return 0f;
        if (age < Rise) { float u = age / Rise; return u * u * (3f - 2f * u); }
        if (age < Rise + Hold) return 1f;
        float f = (age - Rise - Hold) / Fall;
        return f >= 1f ? 0f : MathF.Pow(1f - f, FallPower);
    }

    // ---- strength: the owner's typed table (2026-11-13)
    /// <summary>The strength of a shot of this weapon (the weapon's file name): bolt pistol, bolter, plasma pistol 1; heavy bolter, plasma gun 1.3; stalker bolter, melta, lascannon 1.5; autocannon 1.8.</summary>
    public static float ShotStrength(string? weapon)
    {
        if (weapon is not { Length: > 0 } w) return 1f;
        bool Has(string part) => w.Contains(part, StringComparison.OrdinalIgnoreCase);
        if (Has("auto_cannon") || Has("autocannon")) return 1.8f;
        if (Has("sniper_rifle") || Has("stalker") || Has("melta") || Has("las_cannon") || Has("lascannon")) return 1.5f;
        if (Has("heavy_bolter") || Has("plasma_gun") || Has("plasma_cannon")) return 1.3f;   // the plasma cannon's shot is a blast (the lead's assumption), so its blast uses BlastStrength; a hit not seen as a blast reads as 1.3 (ours)
        return 1f;
    }

    /// <summary>Melee: light 1.3, heavy 2 (heavy strikes and ground pounds), charge 2.5; a hammer, mace or axe a step above the swords (x <see cref="HeavyWeaponStep"/>, ours).</summary>
    public static float MeleeStrength(string? wargear, bool heavy, bool charge)
    {
        float s = charge ? 2.5f : heavy ? 2f : 1.3f;
        if (wargear is { Length: > 0 } w && (w.Contains("hammer", StringComparison.OrdinalIgnoreCase) || w.Contains("mace", StringComparison.OrdinalIgnoreCase) || w.Contains("maul", StringComparison.OrdinalIgnoreCase)
            || w.Contains("axe", StringComparison.OrdinalIgnoreCase))) s *= HeavyWeaponStep;
        return s;
    }
    /// <summary>The factor a hammer or axe adds over the swords (ours: the owner said "one step stronger").</summary>
    public const float HeavyWeaponStep = 1.25f;

    /// <summary>An explosive's strength on a survivor: 2.5 beside the blast, fading linearly to 1 at <paramref name="radius"/> (0 or less: 1).</summary>
    public static float BlastStrength(float distance, float radius)
    {
        if (!(radius > 0f)) return 1f;
        float u = Math.Clamp(distance / radius, 0f, 1f);
        return 2.5f + (1f - 2.5f) * u;
    }

    /// <summary>The size scaling by kind: Boyz, Shootas, Gretchin and the rest of the rank and file 1; 'Ard Boyz and Nobs smaller; Boss Orks and the Warboss a small visual flinch (ours).</summary>
    public static float SizeScale(MusouEnemyType type) => type switch
    {
        MusouEnemyType.Warboss => WarbossScale,
        MusouEnemyType.BossBoy or MusouEnemyType.BossShootaBoy or MusouEnemyType.BossArdBoy or MusouEnemyType.BossNob => BossScale,
        MusouEnemyType.Nob or MusouEnemyType.BigShootaNob or MusouEnemyType.ChaosMarine => NobScale,
        MusouEnemyType.ArdBoy => ArdScale,
        _ => 1f,
    };
    public const float ArdScale = 0.7f, NobScale = 0.5f, BossScale = 0.2f, WarbossScale = 0.12f;

    /// <summary>The kick's size for a hit: the strength by the weapon times the Ork's size, never past <see cref="MaxStrength"/>.</summary>
    public static float Amplitude(float strength, MusouEnemyType type) => Math.Min(MaxStrength, Math.Max(0f, strength) * SizeScale(type));

    /// <summary>The scale of the pose by distance from the camera: whether near (skeletal) or far (baked) is the crowd's tier, this is only the cut-off; 0 past <see cref="FarMetres"/>.</summary>
    public static float DistanceScale(float distance) => distance <= FarMetres ? 1f : 0f;

    /// <summary>Whether this kind flinches at all: every Ork but the Bomb Squig, the Scrap Mek and the Weirdboy (their hits draw no reaction, at any distance). Those that react and have the Boy rig's bones take the bone pose; the rest (the Warboss, the Stormboy ...) the whole-body lean.</summary>
    public static bool Reacts(MusouEnemyType type) => type is not (MusouEnemyType.BombSquig or MusouEnemyType.ScrapMek or MusouEnemyType.Weirdboy);

    /// <summary>Whether this Ork, at this distance from the camera, is drawn flinching: it reacts and is within <see cref="FarMetres"/>. The push-back (the combat-feel slide) is left out only for these.</summary>
    public static bool Flinches(MusouEnemyType type, float distance) => Reacts(type) && DistanceScale(distance) > 0f;

    /// <summary>The far tier's lean angle (radians) and shift (m) for a flinch of <paramref name="size"/> reference sizes.</summary>
    public static float FarLeanRadians(float size) => FarLean * MathF.PI / 180f * size;
    public static float FarShiftMetres(float size) => FarShift * size;
}

/// <summary>
/// One Ork's flinch: the displacement (the unit direction away from the hit times the size, in reference sizes) the pose is drawn at. A new kick takes the displacement from where it
/// is now to the new kick's full size over the rise, so a burst keeps re-kicking to the same size and never grows (owner: "No growth"). Horizontal only.
/// </summary>
public struct MusouOrkFlinchTrack
{
    Vector2 _from, _to;
    float _age;
    bool _active;

    public readonly bool Active => _active;

    /// <summary>A hit: the displacement goes to <paramref name="direction"/> (the unit direction away from the hit) times <paramref name="size"/> over the rise; the envelope restarts.</summary>
    public void Kick(Vector2 direction, float size)
    {
        if (!(size > 0f) || direction.LengthSquared() < 1e-8f) return;
        _from = Current();
        _to = Vector2.Normalize(direction) * size;
        _age = 0f;
        _active = true;
    }

    /// <summary>Advances the clock by <paramref name="delta"/> seconds; false once the reaction is over.</summary>
    public bool Step(float delta)
    {
        if (!_active) return false;
        _age += delta;
        if (_age >= MusouOrkFlinchRules.Seconds) { _active = false; _from = _to = default; return false; }
        return true;
    }

    /// <summary>The displacement now: from the old one to the new kick's over the rise, then the kick's, fading by the envelope after the hold.</summary>
    public readonly Vector2 Current()
    {
        if (!_active) return default;
        if (_age < MusouOrkFlinchRules.Rise) return Vector2.Lerp(_from, _to, MusouOrkFlinchRules.Envelope(_age));
        return _to * MusouOrkFlinchRules.Envelope(_age);
    }
}
