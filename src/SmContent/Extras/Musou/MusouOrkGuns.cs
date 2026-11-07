using SmFormats.Baf;
using SmFormats.Obpk;
using SmSim.Extras.Musou;

namespace SmContent.Extras.Musou;

/// <summary>A Musou enemy type that carries a gun: the wargear file its model, muzzle flash, tracer, impacts and fire sound come from.</summary>
public sealed record MusouGun(MusouEnemyType Type, string Wargear);

/// <summary>What a shot was given this frame (<see cref="MusouGunFxPlanner"/>).</summary>
[Flags]
public enum MusouGunFx : byte { None = 0, Flash = 1, Tracer = 2, Impact = 4 }

/// <summary>One round the simulation reported (a <c>Tracer</c> event): who fired, when, how far the shooter is from the camera (m), and whether it struck a player.</summary>
public readonly record struct MusouGunShot(ushort Enemy, uint Tick, float Distance, bool HitPlayer);

/// <summary>
/// The guns of Musou Mode's ranged Orks (added content; the owner, 2026-11-06: "the ranged attack orks don't have weapon models in their hand, and their shots are invisible ... flesh that out.
/// musou mode only."). The Shoota Boy carries the Ork boy's shoota, <c>ork_coop_boy_2hand_shoota_rapid</c>, the same wargear and model as the playable Ork (<see cref="OrkWeaponMounts.Shoota"/>):
/// the model on the wargear's wield mount, and on each round the wargear's own effects (<c>fire_fx</c>: <c>shoota_muzzle</c> at <c>fx_muzzle</c>; <c>hit_fx</c>: <c>shoota_tracer</c> to the hit point and
/// the container <c>shoota_coop</c> by surface) and sound (<c>play_or_shoota_fire</c>, posted by the crowd's sounds). **Supported by evidence**: the names are the data's (read at run time).
/// <para>
/// **Ours** (spec "Musou Ork guns", refine register): the data plays the tracer on every round; a crowd draws it on every <see cref="TracerEvery"/>th round of each Ork (the owner: "occasional tracer"),
/// the distances beyond which nothing is drawn, the rates and bursts of each kind of effect (token buckets over the whole crowd, nearest shots first), how far a miss flies on to find a wall, and how long
/// a firing Ork holds his aiming stance.
/// </para>
/// </summary>
public static class MusouOrkGunRules
{
    /// <summary>The ranged Orks and their guns (the Chaos Marine's bolter is not an Ork weapon: not drawn here).</summary>
    public static readonly IReadOnlyList<MusouGun> Guns = [new(MusouEnemyType.ShootaBoy, OrkWeaponMounts.Shoota), new(MusouEnemyType.RokkitBoy, RokkitLauncha),
        new(MusouEnemyType.BigShootaNob, OrkWeaponMounts.Shoota)];   // expansion S3: PLACEHOLDER, the Big Shoota Nob fires with the Shoota's model and effects (his own big shoota wargear not yet read)

    /// <summary>Expansion S2: the Rokkit Boy's launcher, the co-op <c>ork_coop_boy_2hand_shoota_pro</c>'s wargear (docs/discovery.md "Exterminatus Ork roster"). His rocket's effects are its own (the client's S2 cues), not a tracer.</summary>
    public const string RokkitLauncha = "ork_coop_boy_rokkit_launcha";

    /// <summary>
    /// Guns whose wargear gives no wield mount (owner 2026-11-08: "make the rokkit boy hold it by a placement we choose"): the handle node of the model put on the rig bone of the same name.
    /// The launcha's file leaves its wield and stow nodes empty (so it reads as worn), but its model (<c>boy_pro_rocketlauncher</c>) has a <c>RIG_Prop_M_Chest</c> locator at the very
    /// spot the Shoota's has ((-0.479, 0.477, 0.550) in both; <c>smtool weapon-parts ork_coop_boy_rokkit_launcha --rig ork --wargear</c>), and the Shoota is held by that node on that bone.
    /// So the launcha is held as the Shoota is: **ours**, an interpretation the model's locator supports.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> OwnMounts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [RokkitLauncha] = "RIG_Prop_M_Chest" };

    /// <summary>The gun a type carries, or null.</summary>
    public static string? WargearOf(MusouEnemyType type)
    {
        foreach (var g in Guns) if (g.Type == type) return g.Wargear;
        return null;
    }

    /// <summary>A round in this many of each Ork's rounds shows its tracer (ours: the owner's "occasional"; a burst is 3 rounds, so about one a burst).</summary>
    public const int TracerEvery = 3;
    /// <summary>Metres from the camera within which a round shows its muzzle flash, its tracer and its impact (ours).</summary>
    public const float FlashMetres = 45f, TracerMetres = 70f, ImpactMetres = 35f;
    /// <summary>Effects a second over the whole crowd and the most at once (token buckets, ours; the effect player keeps at most 160 effects).</summary>
    public const float FlashesPerSecond = 40f, FlashBurst = 8f, TracersPerSecond = 16f, TracerBurst = 4f, ImpactsPerSecond = 14f, ImpactBurst = 4f;
    /// <summary>Metres a miss flies on past the simulation's end point (beside the target) to find the wall or floor it strikes (ours); none within: no impact.</summary>
    public const float MissReach = 30f;
    /// <summary>Seconds a Shoota Boy holds his two-handed aiming stance after a round (ours: the simulation's shooter stands still through a burst, but its state is still "seek").</summary>
    public const float AimHoldSeconds = 0.6f;
    /// <summary>The muzzle flash's scale (the data's play_fx has none: 1).</summary>
    public const float FlashScale = 1f;

    /// <summary>Whether an Ork's <paramref name="round"/>th round (0 first) shows its tracer: every <see cref="TracerEvery"/>th, from a start of his own so a crowd does not flash its tracers together.</summary>
    public static bool TracerRound(ushort enemy, int round) => (round + Phase(enemy)) % TracerEvery == 0;

    static int Phase(ushort enemy) => (int)((enemy * 2654435761u) >> 16) % TracerEvery;
}

/// <summary>
/// Which effects the crowd's rounds get, frame by frame (engine-free, so the caps are tested). <see cref="Refill"/> each frame, then <see cref="Plan"/> with the frame's rounds: the nearest first, each
/// gets its flash, tracer (on its tracer rounds) and impact while within the distance and while the kind's bucket holds a token. Every round counts toward its Ork's tracer cadence, drawn or not.
/// </summary>
public sealed class MusouGunFxPlanner
{
    float _flashes = MusouOrkGunRules.FlashBurst, _tracers = MusouOrkGunRules.TracerBurst, _impacts = MusouOrkGunRules.ImpactBurst;
    readonly Dictionary<ushort, int> _rounds = new();
    readonly List<int> _order = new();

    /// <summary>Rounds seen, and effects given and held back (by the rate, or too far for every kind).</summary>
    public long Rounds { get; private set; }
    public long Flashes { get; private set; }
    public long Tracers { get; private set; }
    public long Impacts { get; private set; }
    public long OverRate { get; private set; }
    public long TooFar { get; private set; }

    public void Refill(float seconds)
    {
        if (!(seconds > 0f)) return;
        _flashes = MathF.Min(MusouOrkGunRules.FlashBurst, _flashes + MusouOrkGunRules.FlashesPerSecond * seconds);
        _tracers = MathF.Min(MusouOrkGunRules.TracerBurst, _tracers + MusouOrkGunRules.TracersPerSecond * seconds);
        _impacts = MathF.Min(MusouOrkGunRules.ImpactBurst, _impacts + MusouOrkGunRules.ImpactsPerSecond * seconds);
    }

    /// <summary>The effects of each of <paramref name="shots"/> (same order) into <paramref name="result"/>.</summary>
    public void Plan(IReadOnlyList<MusouGunShot> shots, MusouGunFx[] result)
    {
        if (result.Length < shots.Count) throw new ArgumentException("result is shorter than shots", nameof(result));
        // The tracer cadence in the simulation's order (by tick), before the nearest-first pass.
        _order.Clear();
        for (int i = 0; i < shots.Count; i++) _order.Add(i);
        _order.Sort((a, b) => shots[a].Tick != shots[b].Tick ? shots[a].Tick.CompareTo(shots[b].Tick) : a.CompareTo(b));
        foreach (int i in _order)
        {
            var s = shots[i];
            _rounds.TryGetValue(s.Enemy, out int round);
            _rounds[s.Enemy] = round + 1;
            result[i] = MusouOrkGunRules.TracerRound(s.Enemy, round) ? MusouGunFx.Tracer : MusouGunFx.None;   // a mark for the pass below
            Rounds++;
        }
        if (_rounds.Count > 8192) _rounds.Clear();   // ids are reused across waves; the cadence only needs to look varied
        _order.Sort((a, b) => shots[a].Distance != shots[b].Distance ? shots[a].Distance.CompareTo(shots[b].Distance) : a.CompareTo(b));
        foreach (int i in _order)
        {
            var s = shots[i];
            bool tracerRound = result[i] == MusouGunFx.Tracer;
            var fx = MusouGunFx.None;
            if (!(s.Distance <= MusouOrkGunRules.TracerMetres)) { TooFar++; result[i] = fx; continue; }
            bool held = false;
            if (s.Distance <= MusouOrkGunRules.FlashMetres) { if (Take(ref _flashes)) { fx |= MusouGunFx.Flash; Flashes++; } else held = true; }
            if (tracerRound) { if (Take(ref _tracers)) { fx |= MusouGunFx.Tracer; Tracers++; } else held = true; }
            if (s.Distance <= MusouOrkGunRules.ImpactMetres) { if (Take(ref _impacts)) { fx |= MusouGunFx.Impact; Impacts++; } else held = true; }
            if (held) OverRate++;
            result[i] = fx;
        }
    }

    static bool Take(ref float tokens)
    {
        if (tokens < 1f) return false;
        tokens -= 1f;
        return true;
    }
}

/// <summary>
/// The effects of the Musou guns from the install: an <see cref="EffectCatalogue"/> of just the <see cref="MusouOrkGunRules.Guns"/>' wargear (the main catalogue holds the multiplayer classes' only), with its
/// impact containers, the surface order and the player blueprint's surface, as <see cref="EffectCatalogue.Load"/> reads them. Read-only; the install is opened for reading only.
/// </summary>
public static class MusouOrkGunEffects
{
    /// <exception cref="FileNotFoundException">An archive is missing.</exception>
    public static EffectCatalogue Load(string gameDir, List<string> notes)
    {
        var names = MusouOrkGunRules.Guns.Select(g => g.Wargear).ToHashSet(StringComparer.OrdinalIgnoreCase);
        BafDocument? global = null;
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, MpClasses.GlobalPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName == "global", (_, d) => global = BafDocument.Read(d));
        var wargear = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.WargearPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && names.Contains(e.FileName), (e, d) => wargear[e.FileName] = BafDocument.Read(d));
        foreach (var n in names) if (!wargear.ContainsKey(n)) notes.Add($"musou guns: wargear '{n}' not in the install");
        BafDocument? player = null;
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.BlueprintPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName.Equals(GameplayTuning.MarineBlueprint, StringComparison.OrdinalIgnoreCase), (_, d) => player ??= BafDocument.Read(d));
        var containerNames = wargear.Values.SelectMany(d => EffectCatalogue.Strings(d.Root, "effect_container")).Select(p => p.Split('\\', '/')[^1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var containers = new Dictionary<string, List<BafDocument>>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, EffectCatalogue.MaterialEffectPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && containerNames.Contains(e.FileName), (e, d) =>
            {
                if (!containers.TryGetValue(e.FileName, out var list)) containers[e.FileName] = list = [];
                list.Add(BafDocument.Read(d));
            });
        return EffectCatalogue.Build(wargear, null, containers.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<BafDocument>)kv.Value, StringComparer.OrdinalIgnoreCase), global, player, notes);
    }

    /// <summary>Every effect a gun of <paramref name="catalogue"/> can play: its fire effects, its tracer, and each effect its impact container lists.</summary>
    public static IReadOnlyCollection<string> EffectNames(EffectCatalogue catalogue)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in MusouOrkGunRules.Guns)
        {
            if (catalogue.Weapon(g.Wargear) is not { } w) continue;
            foreach (var p in w.Fire) if (p.Name.Length > 0) names.Add(p.Name);
            if (w.Tracer is { Name.Length: > 0 } t) names.Add(t.Name);
            if (catalogue.Container(w.HitMaterial) is { } table)
                foreach (var surface in table.Effects.Values) foreach (var plays in surface.Values) foreach (var p in plays) if (p.Name.Length > 0) names.Add(p.Name);
        }
        return names;
    }
}
