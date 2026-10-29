using System.Runtime.CompilerServices;

namespace SmSim;

/// <summary>
/// A class's own body (Ork O4, docs/ork-faction-design.md section 1.3): its speeds, health, armour shield and regeneration, and hit
/// volumes, in place of the <see cref="SimConfig"/> fields of the same names (the marine's) for players of a class whose
/// <see cref="ClassLoadout.Body"/> names it. Everything else (capsule, step height, eye height, camera, reaction and throw timings)
/// stays the config's for every class (**ours**, parity). Hashed with the config (<see cref="SimConfig.Hash"/>).
/// </summary>
/// <param name="Name">A label (e.g. "ork - boy (parity)").</param>
public sealed record BodySpec(string Name,
    float RunSpeed, float StrafeSpeed, float BackSpeed, float SprintSpeed, float WalkSpeed,
    float AimRunSpeed, float AimStrafeSpeed, float AimBackSpeed,
    float HealthMax, float HealthRegenDelay, float HealthRegenRate,
    float ShieldMax, float ShieldRegenDelay, float ShieldRegenRate)
{
    /// <summary>The body's hit volumes (character frame, as <see cref="SimConfig.HitVolumes"/>); null: the config's.</summary>
    public IReadOnlyList<HitVolume>? HitVolumes { get; init; }

    /// <summary>The config's own body (its marine fields and hit volumes), as a spec (for references and tests).</summary>
    public static BodySpec Of(SimConfig c, string name = "marine") => new(name,
        c.RunSpeed, c.StrafeSpeed, c.BackSpeed, c.SprintSpeed, c.WalkSpeed, c.AimRunSpeed, c.AimStrafeSpeed, c.AimBackSpeed,
        c.HealthMax, c.HealthRegenDelay, c.HealthRegenRate, c.ShieldMax, c.ShieldRegenDelay, c.ShieldRegenRate)
    { HitVolumes = c.HitVolumes };

    /// <summary>Whether every value is usable: speeds finite and positive, health positive, shield, delays and rates finite and not negative, volumes valid with a head (at most 64).</summary>
    public bool IsValid =>
        Name is not null && new[] { RunSpeed, StrafeSpeed, BackSpeed, SprintSpeed, WalkSpeed, AimRunSpeed, AimStrafeSpeed, AimBackSpeed, HealthMax }.All(v => float.IsFinite(v) && v > 0f)
        && new[] { HealthRegenDelay, HealthRegenRate, ShieldMax, ShieldRegenDelay, ShieldRegenRate }.All(v => float.IsFinite(v) && v >= 0f)
        && (HitVolumes is null || (HitVolumes.Count is > 0 and <= 64 && HitVolumes.All(v => v is not null && v.IsValid) && HitVolumes.Any(v => v.Zone == HitZone.Head)));

    internal void Write(BinaryWriter w)
    {
        w.Write(Name);
        foreach (float f in new[] { RunSpeed, StrafeSpeed, BackSpeed, SprintSpeed, WalkSpeed, AimRunSpeed, AimStrafeSpeed, AimBackSpeed,
                     HealthMax, HealthRegenDelay, HealthRegenRate, ShieldMax, ShieldRegenDelay, ShieldRegenRate })
            w.Write(f);
        SimConfig.WriteVolumes(w, HitVolumes);
    }
}

public sealed partial record SimConfig
{
    /// <summary>
    /// Class bodies (Ork O4; <see cref="BodySpec"/>), indexed by <see cref="ClassLoadout.Body"/>; empty (every class so far): every player
    /// uses this config's own fields. Hashed only when not empty, so a config without class bodies hashes as before.
    /// </summary>
    public IReadOnlyList<BodySpec> Bodies { get; init; } = [];

    /// <summary>
    /// The config a player of class <paramref name="playerClass"/> moves, regenerates and is hit with: this config itself for a class
    /// without a body of its own (and always when <see cref="Bodies"/> is empty), else a copy with the class's <see cref="BodySpec"/>
    /// fields (its hit volumes, or this config's when the body has none). The copies are cached per config instance (never hashed or
    /// compared: <c>with</c> on a config starts a fresh cache) and calling <see cref="For"/> on a copy gives the same copies (and the base
    /// config for a class without a body).
    /// </summary>
    public SimConfig For(int playerClass)
    {
        var bodies = Bodies;
        if (bodies.Count == 0) return this;
        var classes = Classes;
        int body = playerClass >= 0 && playerClass < classes.Count ? classes[playerClass].Body : -1;
        if (body < 0 || body >= bodies.Count) return BodyCaches.TryGetValue(this, out var known) ? known.Based : this;   // a copy's own class-less answer is its base
        var cache = BodyCaches.GetValue(this, c => new BodyCache(c, c.Bodies.Count));
        return cache.Get(body);
    }

    static readonly ConditionalWeakTable<SimConfig, BodyCache> BodyCaches = new();

    /// <summary>The per-body copies of one config (shared with the copies, so <see cref="For"/> on a copy returns the base's copies).</summary>
    sealed class BodyCache(SimConfig based, int count)
    {
        readonly SimConfig?[] _copies = new SimConfig?[count];
        public SimConfig Based => based;

        public SimConfig Get(int body)
        {
            if (_copies[body] is { } done) return done;
            var b = based.Bodies[body];
            var copy = based with
            {
                RunSpeed = b.RunSpeed, StrafeSpeed = b.StrafeSpeed, BackSpeed = b.BackSpeed, SprintSpeed = b.SprintSpeed, WalkSpeed = b.WalkSpeed,
                AimRunSpeed = b.AimRunSpeed, AimStrafeSpeed = b.AimStrafeSpeed, AimBackSpeed = b.AimBackSpeed,
                HealthMax = b.HealthMax, HealthRegenDelay = b.HealthRegenDelay, HealthRegenRate = b.HealthRegenRate,
                ShieldMax = b.ShieldMax, ShieldRegenDelay = b.ShieldRegenDelay, ShieldRegenRate = b.ShieldRegenRate,
                HitVolumes = b.HitVolumes ?? based.HitVolumes,
            };
            BodyCaches.AddOrUpdate(copy, this);
            return _copies[body] = copy;
        }
    }
}

public sealed partial class World
{
    /// <summary>
    /// The height above a player's feet that blasts, melee, charges and the melta measure to (<see cref="HitVolumes.CentreHeight"/>) for
    /// its class's body (Ork O4: the Ork's own volumes; every marine and Chaos class: the config's, as before).
    /// </summary>
    float ChestOf(in PlayerState p) => HitVolumes.CentreHeight(Config.For(p.Class));
}
