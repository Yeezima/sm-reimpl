using SmFormats.Baf;
using SmFormats.Obpk;

namespace SmContent;

/// <summary>
/// fx/impact-pass: the melee weapons' own effects the client plays (client presentation only; the simulation reads the blasts in <see cref="MeleeTuning"/>).
/// Evidence (Verified-local, <c>smtool baf-dump wargear.oppc --grep fx_name</c> on the MP melee wargear):
/// <list type="bullet">
/// <item><b>Blast strikes.</b> Each <c>action_data/triggered_actions/displace_*</c> action (footstomp, quickstomp, ax_slam, hammerslam) holds
/// <c>actions_by_upgrade/actions/entry</c>s: the default entry (no required upgrade; the knife's foot stomp: radius 7, <c>interrupt_stomp_dmg</c> at <c>fx_l_foot</c>, scale 1)
/// and the Furious Stomp entry (radius 10, <c>knockback_stomp_dmg_furied</c> at scale 0.85 plus the default's). The hammer's and the maul's slam plays <c>thunderhammer_dmg_displace</c>
/// (<c>_heavy</c> for the upgrade) at the hammer's <c>fx_swing</c>, scale 1.5. Each entry's <c>area_of_effect/shape/radius</c> and <c>do_damage/damage_general</c> are the
/// blast's (the same values <see cref="MeleeTuning"/> gives the simulation), so a step's blast is matched to its entry by them (<see cref="Match"/>).</item>
/// <item><b>Deployed effects.</b> <c>action_data/while_deployed_actions</c> <c>play_fx</c> commands other than the Chaos eye glow (<see cref="EyeGlowData"/>): the power sword's
/// <c>powersword_idle</c> at <c>fx_powersword_swing</c> (both races: the Chaos sword binds the same blue effect; the data has no Chaos colour for it), the hammer's, the axe's.</item>
/// </list>
/// No engine types; read once in the background.
/// </summary>
public sealed class MeleeEffects
{
    /// <summary>One entry of a blast strike's action: the blast's radius and damage, the upgrades it requires (none: the default) and the effects it plays.</summary>
    public sealed record StompEntry(float Radius, float Damage, IReadOnlyList<string> RequiredUpgrades, IReadOnlyList<FxPlay> Plays)
    {
        public bool IsDefault => RequiredUpgrades.Count == 0;
    }

    /// <summary>A blast strike's triggered action (<c>displace_footstomp</c> ...) and its entries in file order.</summary>
    public sealed record StompAction(string Action, IReadOnlyList<StompEntry> Entries);

    readonly Dictionary<string, IReadOnlyList<StompAction>> _stomps;
    readonly Dictionary<string, IReadOnlyList<FxPlay>> _deployed;

    MeleeEffects(Dictionary<string, IReadOnlyList<StompAction>> stomps, Dictionary<string, IReadOnlyList<FxPlay>> deployed) { _stomps = stomps; _deployed = deployed; }

    /// <summary>Wargear files read.</summary>
    public IReadOnlyCollection<string> Wargear => _stomps.Keys;

    /// <summary>The blast strikes of a wargear file (empty when it has none).</summary>
    public IReadOnlyList<StompAction> Stomps(string? wargear) => wargear is not null && _stomps.TryGetValue(wargear, out var s) ? s : [];

    /// <summary>The effects a wargear file plays while it is deployed (in hand), the eye glow left out; empty when none.</summary>
    public IReadOnlyList<FxPlay> Deployed(string? wargear) => wargear is not null && _deployed.TryGetValue(wargear, out var d) ? d : [];

    /// <summary>Every effect name any of them plays (for the background compile).</summary>
    public IEnumerable<string> EffectNames => _stomps.Values.SelectMany(a => a).SelectMany(a => a.Entries).SelectMany(e => e.Plays)
        .Concat(_deployed.Values.SelectMany(p => p)).Select(p => p.Name).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal);

    /// <summary>
    /// The entry of a wargear's blast strikes whose blast is (<paramref name="radius"/>, <paramref name="damage"/>): the one with plays whose radius and damage are nearest
    /// (radius first; a tie keeps the default entry, then file order). Null when the wargear has no blast strike with plays.
    /// </summary>
    public StompEntry? Match(string? wargear, float radius, float damage) => Match(Stomps(wargear), radius, damage);

    /// <inheritdoc cref="Match(string?, float, float)"/>
    public static StompEntry? Match(IEnumerable<StompAction> actions, float radius, float damage)
    {
        StompEntry? best = null; float bestScore = float.MaxValue;
        foreach (var entry in actions.SelectMany(a => a.Entries))
        {
            if (entry.Plays.Count == 0) continue;
            float score = MathF.Abs(entry.Radius - radius) * 1000f + MathF.Abs(entry.Damage - damage) + (entry.IsDefault ? 0f : 0.01f);
            if (score < bestScore) { best = entry; bestScore = score; }
        }
        return best;
    }

    /// <summary>From already-read wargear files (testable without an install).</summary>
    public static MeleeEffects Build(IReadOnlyDictionary<string, BafDocument> wargear)
    {
        var stomps = new Dictionary<string, IReadOnlyList<StompAction>>(StringComparer.OrdinalIgnoreCase);
        var deployed = new Dictionary<string, IReadOnlyList<FxPlay>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, doc) in wargear)
        {
            stomps[name] = ReadStomps(doc.Root);
            var idle = FxBinding.Collect(doc.Root)
                .Where(b => b.IsDefault && b.Play is { Name.Length: > 0 } && b.Path.Contains("while_deployed_actions/", StringComparison.Ordinal) && !EyeGlowData.IsEyeGlow(b))
                .Select(b => b.Play!).ToList();
            if (idle.Count > 0) deployed[name] = idle;
        }
        return new MeleeEffects(stomps, deployed);
    }

    static List<StompAction> ReadStomps(BafTable root)
    {
        var found = new List<StompAction>();
        if (root.Table("action_data/triggered_actions") is not { } triggered) return found;
        foreach (var e in triggered.Entries)
        {
            if (e.Type != BafType.Table || !e.Name.StartsWith("displace_", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith("_coop", StringComparison.OrdinalIgnoreCase)) continue;
            if (e.AsTable().Table("commands") is not { } commands) continue;
            // Plays outside the upgrade entries go with every entry.
            var common = FxBinding.Collect(commands).Where(b => b.Play is { Name.Length: > 0 } && !b.Path.Contains("actions_by_upgrade", StringComparison.Ordinal)).Select(b => b.Play!).ToList();
            var entries = new List<StompEntry>();
            if (commands.Table("actions_by_upgrade/actions") is { } byUpgrade)
            {
                foreach (var entry in byUpgrade.Entries)
                {
                    if (entry.Type != BafType.Table) continue;
                    var t = entry.AsTable();
                    var required = t.Table("required_upgrade_name/required_list")?.Entries.Where(x => x.Type == BafType.String).Select(x => x.AsString()).ToList() ?? [];
                    var cmds = t.Table("action/commands");
                    var plays = cmds is not { } c ? [] : FxBinding.Collect(c).Where(b => b.Play is { Name.Length: > 0 }).Select(b => b.Play!).ToList();
                    entries.Add(new StompEntry(RadiusOf(cmds), DamageOf(cmds), required, [.. plays, .. common]));
                }
            }
            else entries.Add(new StompEntry(RadiusOf(commands), DamageOf(commands), [], common));
            found.Add(new StompAction(e.Name.ToLowerInvariant(), entries));
        }
        return found;
    }

    static float RadiusOf(BafTable? commands) => commands?.Table("area_of_effect/shape")?.Float("radius") ?? 0f;
    static float DamageOf(BafTable? commands) => commands?.Table("area_of_effect/hit_actions/commands/do_damage")?.Float("damage_general") ?? 0f;

    /// <summary>Reads the named wargear files of the install (read-only).</summary>
    /// <exception cref="FileNotFoundException">The wargear package is missing.</exception>
    public static MeleeEffects Load(string gameDir, IEnumerable<string> wargearNames)
    {
        var wanted = wargearNames.Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var docs = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count > 0)
            using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.WargearPackage)))
                archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && wanted.Contains(e.FileName), (e, d) => docs.TryAdd(e.FileName, BafDocument.Read(d)));
        return Build(docs);
    }
}
