using System.Globalization;
using System.Numerics;
using SmContent;
using SmSim;

namespace SmTool;

/// <summary>
/// <c>smtool ork-balance &lt;game-dir|auto&gt; [--mode parity|data|both]</c> (Ork O4): the Ork Boy's balance rows (data, Tactical
/// reference, rule, factor, result) in Data mode (the data's own values) and Parity mode (docs/ork-faction-design.md section 2), the
/// sustained-damage checks, the hit volumes' projected areas against the marine's (open question 2) and the config hashes without the
/// Ork and with it in each mode. Read-only.
/// </summary>
static class OrkCommands
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// <c>smtool ork-actions &lt;game-dir|auto&gt;</c> (Ork O7b / O7c): the clips and timings the networked client poses for the Ork's actions (<see cref="OrkActions"/>):
    /// reactions with their branch times, the reload clip, the recoil, the toss release, the death clips and their ragdoll hand-over, and the choppa / charge / hop clips
    /// with the rate they play at against the simulation's duration. Read-only.
    /// </summary>
    public static int Actions(string gameDir)
    {
        if (gameDir == "auto") gameDir = GameInstall.Locate();
        var profile = RigProfiles.OrkBoy;
        var character = profile.LoadCharacter(gameDir);
        var hip = HipFireAnimations.From(character, profile);
        var plan = OrkActions.From(character, profile, hip);
        float Len(string clip) => character.LoadAnimation(clip).Animation.Duration;
        Console.WriteLine("== Ork actions (the boy's own clips; the simulation keeps the marine timings for reactions and the throw)");
        foreach (var r in plan.Reactions)
            foreach (var (label, c) in new[] { ("front", r.Front), ("back", r.Back) })
                Console.WriteLine($"reaction {r.Kind,-9} {label,-5} {c.Animation,-32} {c.Duration,5:0.000} s  end {c.EndAt,5:0.000}  run {c.RunAt,5:0.000}  evade {c.EvadeAt,5:0.000}  melee {c.MeleeAt,5:0.000}  hitreact {c.ReactAt,5:0.000}");
        if (plan.Reload is { } reload)
            foreach (var c in reload.Clips)
                Console.WriteLine($"reload {c.Family,-10} {c.Clip} {c.ClipLength:0.00} s, stretched to the reload time: {c.EnforcedDuration ?? "no"}; magazine out {c.MagazineOut?.ToString("0.000") ?? "-"} in {c.MagazineIn?.ToString("0.000") ?? "-"}, blend in {c.BlendIn?.ToString("0.000") ?? "-"} out {c.BlendOut?.ToString("0.000") ?? "-"}, mask {(c.BoneMask is null ? "none" : $"{c.BoneMask.Count(kv => kv.Value > 0f)}/{c.BoneMask.Count} bones")}");
        Console.WriteLine($"recoil gun {plan.GunRecoil?.Clip ?? "none"} ({plan.GunRecoil?.Length:0.00} s, additive {plan.GunRecoil?.Additive}), pistol {plan.PistolRecoil?.Clip ?? "none"}");
        if (plan.Throw is { } t) Console.WriteLine($"throw {t.Clip} {t.Length:0.000} s: release {t.ReleaseAt:0.000} s, end {t.EndAt:0.000} s");
        foreach (var d in plan.Deaths) Console.WriteLine($"death {d.Kind,-9} {d.Clip,-30} {d.Length:0.000} s, ragdoll from {d.RagdollAt:0.000} s");
        Console.WriteLine($"upper-body mask: {(plan.UpperBodyMask is null ? "none" : $"{plan.UpperBodyMask.Count(kv => kv.Value > 0f)} of {plan.UpperBodyMask.Count} bones")}");
        var tuning = GameplayTuning.Load(gameDir, OrkBalanceMode.Parity);
        if (tuning.Classes?.Ork is { } ork)
        {
            var cfg = tuning.Config;
            var choppa = ork.Choppa;
            for (int i = 0; i < choppa.Steps.Count; i++)
            {
                string clip = tuning.Classes.MeleeAnimationsByWeapon[OrkClass.Choppa][i];
                Console.WriteLine($"choppa light {i + 1}: {clip} {Len(clip):0.000} s over the step's {choppa.Steps[i].Duration:0.000} s (rate {OrkActionRules.ClipRate(Len(clip), choppa.Steps[i].Duration):0.00}), hit {choppa.Steps[i].HitTime:0.000}, combo {choppa.Steps[i].ComboOpen:0.000}-{choppa.Steps[i].ComboClose:0.000}, moves again {choppa.Steps[i].MoveAt:0.000}");
            }
            for (int i = 0; i < choppa.Heavy.Count; i++)
            {
                string clip = tuning.Classes.MeleeHeavyAnimationsByWeapon[OrkClass.Choppa][i];
                Console.WriteLine($"choppa heavy {i + 1}: {clip} {Len(clip):0.000} s over the step's {choppa.Heavy[i].Duration:0.000} s (rate {OrkActionRules.ClipRate(Len(clip), choppa.Heavy[i].Duration):0.00}), hit {choppa.Heavy[i].HitTime:0.000}, moves again {choppa.Heavy[i].MoveAt:0.000}");
            }
            if (choppa.Rush is { } rush)
                Console.WriteLine($"charge {rush.Rush.Animation} {Len(rush.Rush.Animation):0.000} s (sim {rush.Rush.ClipLength:0.000} s, end {rush.Rush.End:0.000}, control back {rush.Rush.MoveAt:0.000}); contact until {rush.ContactUntil:0.000}; collision stop {rush.Collision.ClipLength:0.000} s; hyper armour {rush.HyperArmourUntil:0.000}");
            if (ork.Evade is { } evade)
                foreach (var (label, m) in new[] { ("left", evade.Left), ("right", evade.Right) })
                    Console.WriteLine($"hop {label}: {m.Animation} {Len(m.Animation):0.000} s over the sim's {m.Duration:0.000} s (rate {OrkActionRules.ClipRate(Len(m.Animation), m.Duration):0.00}), {m.Distance.X:0.00} m, peak {m.PeakHeight:0.00} m, control back {m.MoveAt:0.000}");
            Console.WriteLine($"simulation (marine) timings: throw release {cfg.ThrowRelease:0.000} s, end {cfg.ThrowDuration:0.000} s");
            foreach (var kind in new[] { ReactionKind.Interrupt, ReactionKind.Stun, ReactionKind.Stagger })
                if (Reaction.SpecOf(cfg, kind) is { } spec)
                    Console.WriteLine($"  sim reaction {kind,-9}: {spec.Front.Animation} end {spec.Front.EndAt:0.000} s / {spec.Back.Animation} end {spec.Back.EndAt:0.000} s");
        }
        foreach (var n in plan.Notes) Console.WriteLine("note: " + n);
        return 0;
    }

    public static int Balance(string gameDir, string[] rest)
    {
        if (gameDir == "auto") gameDir = GameInstall.Locate();
        string mode = rest.SkipWhile(a => a != "--mode").Skip(1).FirstOrDefault() ?? "both";
        var modes = mode switch
        {
            "parity" => new[] { OrkBalanceMode.Parity },
            "data" => [OrkBalanceMode.Data],
            "both" => [OrkBalanceMode.Data, OrkBalanceMode.Parity],
            _ => null,
        };
        if (modes is null) { Console.Error.WriteLine("--mode parity|data|both"); return 2; }
        var plain = GameplayTuning.Load(gameDir);
        Console.WriteLine($"config hash without the Ork: {plain.Config.Hash():X16} ({plain.Config.Classes.Count} classes)");
        foreach (var m in modes)
        {
            var tuning = GameplayTuning.Load(gameDir, m);
            var c = tuning.Config;
            Console.WriteLine();
            Console.WriteLine($"== {m} mode: config hash {c.Hash():X16} ({c.Classes.Count} classes)");
            if (tuning.Classes?.Ork is not { } build) { Console.WriteLine("no Ork:"); Notes(tuning); continue; }
            Console.WriteLine($"{"stat",-36} {"data",9} {"reference",10} {"rule",-15} {"factor",8} {"result",9} unit      note");
            foreach (var r in build.Rows)
                Console.WriteLine($"{r.Stat,-36} {F(r.Data),9} {F(r.Reference),10} {r.Rule,-15} {F(r.Factor),8} {F(r.Result),9} {r.Unit,-9} {r.Note}");
            int ork = c.Classes.Count - 1;
            var body = c.For(ork);
            Console.WriteLine($"class {ork} {c.Classes[ork].Name}: side {c.Classes[ork].Side}, body {build.Body.Name}; health {body.HealthMax} + shield {body.ShieldMax}; " +
                $"run {body.RunSpeed} strafe {body.StrafeSpeed} back {body.BackSpeed} sprint {body.SprintSpeed} m/s");
            Console.WriteLine($"perks: {string.Join(", ", c.Classes[ork].PerkChoices.Select(i => c.Perks[i].Name))} (default {(c.Classes[ork].DefaultPerk is var d and >= 0 ? c.Perks[d].Name : "none")})");
            foreach (var (label, spec) in new[] { ("shoota", build.Shoota), ("slugga", build.Slugga) })
                Console.WriteLine($"{label}: {spec.Damage:0.###} per shot every {OrkBalance.EffectiveCooldown(spec.ShotCooldown, c.TickSeconds):0.####} s, clip {spec.ClipSize}, reserve {spec.ReserveSize}, " +
                    $"reload {spec.ReloadTime:0.###} s, range {spec.MaxRange} m; burst {OrkBalance.Burst(spec, c.TickSeconds):0.#} / sustained {OrkBalance.Sustained(spec, c.TickSeconds):0.#} damage/s");
            Console.WriteLine($"choppa: light {string.Join(" / ", build.Choppa.Steps.Select(s => $"{s.Damage:0.#} ({s.SecondaryDamage:0.#}) {s.PrimaryReaction.Kind}"))}; " +
                $"heavy {string.Join(" / ", build.Choppa.Heavy.Select(s => $"{s.Damage:0.#} {s.PrimaryReaction.Kind}"))}; " +
                (build.Choppa.Rush is { } rush ? $"charge {rush.Primary.Damage:0.#} + {rush.Primary.ShieldDamage:0.#} shield {rush.PrimaryReaction.Kind}, {rush.Rush.Distance():0.0} m" : "no charge") +
                $"; {build.Choppa.Variants.Count} perk variant(s)");
            if (build.Stikk is { } stikk)
                Console.WriteLine($"stikkbomb: {stikk.SpawnCount} of {stikk.Count}, cooldown {stikk.Cooldown} s, blast r {stikk.Projectile.Splash?.Radius} m {stikk.Projectile.Splash?.Damage.Damage} + {stikk.Projectile.Splash?.Damage.ShieldDamage} shield-only; " +
                    $"speed {stikk.Projectile.Speed}, fuse {stikk.Projectile.GroundLifetime} s; {stikk.Variants.Count} perk variant(s)");
            if (build.Evade is { } e)
                Console.WriteLine($"dodge hop (side only {e.SideOnly}): left {e.Left.Distance.X:0.00} m in {e.Left.Duration:0.###} s (peak {e.Left.PeakHeight:0.00} m), right {e.Right.Distance.X:0.00} m in {e.Right.Duration:0.###} s " +
                    $"(peak {e.Right.PeakHeight:0.00} m); control back at {e.Left.MoveAt:0.###} s; spacing {e.MinInterval} s; hyper armour {e.HyperArmourTime:0.###} s");
            if (m == modes[^1]) { Areas(plain.Config, body); Rigs(gameDir, body); }
            Notes(tuning);
        }
        return 0;
    }

    static void Notes(GameplayTuning tuning)
    {
        foreach (var n in tuning.Notes.Where(n => n.Contains("ork:", StringComparison.Ordinal))) Console.WriteLine("note: " + n);
    }

    /// <summary>Projected areas of the hit volumes from the front and the side (2 cm grid), the Ork's against the marine's.</summary>
    static void Areas(SimConfig marine, SimConfig ork)
    {
        static float Area(IReadOnlyList<HitVolume> v, bool side)
        {
            const float step = 0.02f;
            int hits = 0;
            for (float x = -1.5f; x <= 1.5f; x += step)
                for (float z = 0f; z <= 3f; z += step)
                {
                    var origin = side ? new Vector3(-5f, x, z) : new Vector3(x, -5f, z);
                    if (HitVolumes.Raycast(v, origin, side ? Vector3.UnitX : Vector3.UnitY, Vector3.Zero, 0f, 10f).Zone != HitZone.None) hits++;
                }
            return hits * step * step;
        }
        var a = HitVolumes.Of(marine);
        var b = HitVolumes.Of(ork);
        float front = Area(b, false) / Area(a, false), side = Area(b, true) / Area(a, true);
        var (ab, at) = HitVolumes.Extent(a);
        var (bb, bt) = HitVolumes.Extent(b);
        Console.WriteLine($"hit volumes: ork {b.Count} ({b.Count(v => v.Zone == HitZone.Head)} head), {bb:0.00}-{bt:0.00} m; marine {a.Count}, {ab:0.00}-{at:0.00} m; " +
            $"projected area ork/marine front {front:P0}, side {side:P0} (open question 2: revisit below 85 %)");
    }

    /// <summary>The campaign boy's rig (<c>ork_core_units</c>), compared with the Exterminatus boy's whose ragdoll the Ork uses (Ork O4b).</summary>
    static readonly RigProfile CampaignBoy = new("ork-campaign", "data/chunks/ebp_sets__ork_core_units.oppc", "havok_boy__hkxx", "boy_melee");

    /// <summary>The hit volumes of the Exterminatus and campaign boy rigs' ragdolls side by side, and which one the config uses.</summary>
    static void Rigs(string gameDir, SimConfig ork)
    {
        IReadOnlyList<HitVolume>? Of(RigProfile p)
        {
            var notes = new List<string>();
            try { return HitVolumeTuning.Load(gameDir, p, notes); }
            catch (Exception e) when (e is IOException or InvalidDataException or KeyNotFoundException or ArgumentException) { Console.WriteLine($"{p.Entry}: {e.Message}"); return null; }
        }
        var coop = Of(RigProfiles.OrkBoy);
        var campaign = Of(CampaignBoy);
        foreach (var (label, p, v) in new[] { ("exterminatus", RigProfiles.OrkBoy, coop), ("campaign", CampaignBoy, campaign) })
        {
            if (v is null) { Console.WriteLine($"{label} {p.Entry} ({p.Archive}): no usable ragdoll"); continue; }
            var (lo, hi) = HitVolumes.Extent(v);
            Console.WriteLine($"{label} {p.Entry} ({p.Archive}): {v.Count} volumes ({v.Count(x => x.Zone == HitZone.Head)} head), {lo:0.000}-{hi:0.000} m: " +
                string.Join(", ", v.Select(x => $"{x.Name} {x.Kind}")));
        }
        if (coop is not null && campaign is not null)
        {
            static string Key(HitVolume x) => string.Join(",", new[] { x.A.X, x.A.Y, x.A.Z, x.B.X, x.B.Y, x.B.Z, x.Radius }.Select(f => f.ToString("0.000", Inv)));
            int same = coop.Count(c => campaign.Any(k => k.Name == c.Name && k.Kind == c.Kind && Key(k) == Key(c)));
            Console.WriteLine($"exterminatus vs campaign: {same} of {coop.Count} volumes identical (name, kind, ends, radius to the millimetre); campaign {campaign.Count}");
        }
        bool usesCoop = coop is not null && ork.HitVolumes is { } used && used.Count == coop.Count && used.Zip(coop).All(z => z.First.Name == z.Second.Name && z.First.A == z.Second.A && z.First.B == z.Second.B);
        Console.WriteLine($"the Ork class uses: {(usesCoop ? $"the exterminatus boy's ({RigProfiles.OrkBoy.Entry})" : "other volumes")}");
    }

    static float Distance(this RushMove m) => m.Path.Count > 0 ? m.Path[^1].Length() : 0f;

    static string F(float v) => float.IsNaN(v) ? "-" : v.ToString("0.####", Inv);
}
