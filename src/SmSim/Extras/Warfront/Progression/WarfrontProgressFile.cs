using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Squad;

namespace SmSim.Extras.Warfront.Progression;

/// <summary>One retainer a player owns beyond the first (his +1 Retainer cards): class and name only.</summary>
public sealed class WarfrontExtraRetainer
{
    [JsonPropertyName("className")] public string ClassName { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

/// <summary>How a progress file's text parsed: <see cref="Newer"/> is a file of a newer build (never touched), <see cref="Damaged"/> anything unreadable.</summary>
public enum WarfrontProgressParse { Ok, Newer, Damaged }

/// <summary>
/// A player's persistent Warfront progression, stored on his own PC (<c>progress.json</c>, record version <see cref="CurrentVersion"/>; owner 2026-11-22) and mirrored by the host: XP (the level is derived), picks taken
/// and the stat cards (the retainers beyond the first are an empty list since version 2: owner 2026-11-23, card 2). Pure data and pure rules: <see cref="Sanitize"/> (the host's sanity checks), <see cref="Compare"/>/<see cref="Better"/> (which copy wins) and the JSON
/// helpers the client and the tests share; no file IO. The same type is the wire's payload (<c>WarfrontProgressSaveWire</c>), where <see cref="Key"/>, <see cref="Version"/> and <see cref="ResetAt"/> are not carried.
/// </summary>
public sealed class WarfrontProgressFile
{
    /// <summary>2 (owner 2026-11-23, card 2): Retainer cards are retired and dropped with their extras, a pick taken may hold no card (a version 1 file loads and converts through <see cref="Sanitize"/>; an older build
    /// reads a version 2 file as newer and leaves it alone). 1: the first record (codebase fix 1).</summary>
    public const int CurrentVersion = 2;
    /// <summary>Owner 2026-11-22: the highest level a stored progression can hold.</summary>
    public const int MaxLevel = 50;
    /// <summary>The most XP a stored progression holds: the last XP of level <see cref="MaxLevel"/>.</summary>
    public static long MaxXp => WarfrontCampaign.XpForLevel(MaxLevel + 1) - 1;
    /// <summary>The most picks a stored progression holds (one per level above 1).</summary>
    public const int MaxCards = MaxLevel - 1;
    /// <summary>Retainers beyond the first: the roster holds <see cref="SquadRules.MaxPerPlayer"/> in all.</summary>
    public const int MaxExtras = SquadRules.MaxPerPlayer - 1;
    /// <summary>UTF-8 byte caps of the retainer strings (they bound the wire payload; ours).</summary>
    public const int MaxClassBytes = 16, MaxNameBytes = 24;

    [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
    /// <summary>The campaign key as hex (the client.json key), empty on the wire.</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("xp")] public long Xp { get; set; }
    [JsonPropertyName("picksTaken")] public int PicksTaken { get; set; }
    [JsonPropertyName("cards")] public List<int> Cards { get; set; } = new();
    [JsonPropertyName("extraRetainers")] public List<WarfrontExtraRetainer> ExtraRetainers { get; set; } = new();
    /// <summary>UTC time (round-trip text) of the last RESET PROGRESSION, null for a file never reset.</summary>
    [JsonPropertyName("resetAt")] public string? ResetAt { get; set; }

    /// <summary>The level of <see cref="Xp"/>, at most <see cref="MaxLevel"/>.</summary>
    [JsonIgnore] public int Level => LevelForXp(Xp);

    /// <summary>The level of <paramref name="xp"/> on the 1500 + 500 per level curve, never above <see cref="MaxLevel"/>.</summary>
    public static int LevelForXp(long xp) => xp <= 0 ? 1 : Math.Min(MaxLevel, WarfrontCampaign.LevelForXp(Math.Min(xp, MaxXp)));

    public WarfrontProgressFile Clone() => new()
    {
        Version = Version, Key = Key, Xp = Xp, PicksTaken = PicksTaken, ResetAt = ResetAt, Cards = new(Cards),
        ExtraRetainers = ExtraRetainers.Select(r => new WarfrontExtraRetainer { ClassName = r.ClassName, Name = r.Name }).ToList(),
    };

    /// <summary>A fresh level 1 progression (what a reset writes).</summary>
    public static WarfrontProgressFile Fresh(string key, string? resetAt = null) => new() { Key = key, ResetAt = resetAt };

    // ---- the host's sanity checks ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A sanitized copy (never throws, idempotent): XP clamped to 0..<see cref="MaxXp"/>; unknown card ids dropped; picks taken at most the level minus 1, and the card list cut to one card per pick taken.
    /// Since record version 2 (owner 2026-11-23, card 2) the retired +1 Retainer cards are dropped while their picks stay taken (so <see cref="PicksTaken"/> may exceed the card count: no pick is given back), and
    /// <see cref="ExtraRetainers"/> is always empty (every player has exactly two retainers; none is earned). A version 1 file converts by this alone. Each repair is a line in <paramref name="notes"/> (for the host's log).
    /// </summary>
    public static WarfrontProgressFile Sanitize(WarfrontProgressFile? f, List<string>? notes = null)
    {
        f ??= new WarfrontProgressFile();
        var o = new WarfrontProgressFile { Version = CurrentVersion, Key = f.Key ?? "", ResetAt = f.ResetAt };
        o.Xp = Math.Clamp(f.Xp, 0L, MaxXp);
        if (o.Xp != f.Xp) notes?.Add($"xp {f.Xp} clamped to {o.Xp}");
        var known = (f.Cards ?? new List<int>()).Where(IsKnownCard).ToList();
        if (known.Count != (f.Cards?.Count ?? 0)) notes?.Add("unknown card ids dropped");
        int picks = Math.Clamp(f.PicksTaken, 0, Math.Min(o.Level - 1, MaxCards));
        if (picks != f.PicksTaken) notes?.Add($"picksTaken {f.PicksTaken} reduced to {picks} (level {o.Level})");
        // the cards in pick order, at most one per pick taken; a retired +1 Retainer card (owner 2026-11-23, card 2) is dropped and its pick stays taken (no pick comes back, nothing else changes)
        int held = 0, retired = 0;
        var cards = new List<int>();
        foreach (var c in known)
        {
            if (held >= picks) break;
            held++;
            if (c == (int)WarfrontCard.Retainer) { retired++; continue; }
            cards.Add(c);
        }
        if (retired > 0) notes?.Add($"{retired} retired Retainer card(s) dropped (every player has {SquadRules.RetainersPerPlayer} retainers)");
        if (known.Count > held) notes?.Add("surplus cards dropped");
        o.PicksTaken = picks;
        o.Cards = cards;
        // no retainer is earned any more: every player has exactly two, named by the run (the extras of an older file are dropped with its Retainer cards)
        if ((f.ExtraRetainers?.Count ?? 0) > 0) notes?.Add($"{f.ExtraRetainers!.Count} extra retainer(s) dropped (no Retainer cards)");
        return o;
    }

    public static bool IsKnownCard(int c) => c >= (int)WarfrontCard.AttackSpeed && c <= (int)WarfrontCard.Retainer;

    /// <summary>Trimmed, control characters removed, cut to <paramref name="maxBytes"/> UTF-8 bytes on a whole character (and to <see cref="SquadRules.NameMax"/> characters).</summary>
    public static string CleanText(string? s, int maxBytes)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder();
        int bytes = 0, chars = 0;
        foreach (var r in s.Trim().EnumerateRunes())
        {
            if (Rune.IsControl(r)) continue;
            int b = r.Utf8SequenceLength;
            if (bytes + b > maxBytes || chars + r.Utf16SequenceLength > SquadRules.NameMax) break;
            sb.Append(r.ToString()); bytes += b; chars += r.Utf16SequenceLength;
        }
        return sb.ToString().Trim();
    }

    // ---- which copy wins -------------------------------------------------------------------------------------------------------------------

    /// <summary>The ordering of two copies: higher XP, then more picks taken, then more extra retainers; 0 on a full tie. Positive when <paramref name="a"/> is better.</summary>
    public static int Compare(WarfrontProgressFile a, WarfrontProgressFile b)
    {
        int c = a.Xp.CompareTo(b.Xp);
        if (c != 0) return c;
        c = a.PicksTaken.CompareTo(b.PicksTaken);
        if (c != 0) return c;
        return a.ExtraRetainers.Count.CompareTo(b.ExtraRetainers.Count);
    }

    /// <summary>The winner of two copies, whole (its cards and retainers come with it). A full tie goes to <paramref name="host"/>.</summary>
    public static WarfrontProgressFile Better(WarfrontProgressFile host, WarfrontProgressFile player) => Compare(player, host) > 0 ? player : host;

    /// <summary>The client's rule: it writes the host's copy only when that copy is at least as good as its own (it never lowers its own file).</summary>
    public static bool ClientMayWrite(WarfrontProgressFile host, WarfrontProgressFile own) => Compare(host, own) >= 0;

    // ---- JSON ------------------------------------------------------------------------------------------------------------------------------

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Parses file text. <see cref="WarfrontProgressParse.Newer"/> for a version above <see cref="CurrentVersion"/> (the file is left alone by every caller), <see cref="WarfrontProgressParse.Damaged"/> for anything else unreadable. Never throws; a parsed file is not yet sanitized.</summary>
    public static WarfrontProgressParse TryParse(string? json, out WarfrontProgressFile? file)
    {
        file = null;
        if (string.IsNullOrWhiteSpace(json)) return WarfrontProgressParse.Damaged;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int ver))
                return WarfrontProgressParse.Damaged;
            if (ver > CurrentVersion) return WarfrontProgressParse.Newer;
            if (ver < 1) return WarfrontProgressParse.Damaged;
            var f = doc.RootElement.Deserialize<WarfrontProgressFile>(Options);
            if (f is null) return WarfrontProgressParse.Damaged;
            f.Cards ??= new(); f.ExtraRetainers ??= new(); f.Key ??= "";
            file = f;
            return WarfrontProgressParse.Ok;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or NotSupportedException) { return WarfrontProgressParse.Damaged; }
    }
}
