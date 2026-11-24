using System.Numerics;
using SmSim.Extras.Musou;

namespace SmSim.Extras.Executions;

// Executions X2 (brief 2026-11-24, owner approval and answers 2026-11-25; design state/design-executions-1125.md "X2"; inventory docs/executions-coverage.md):
// the engine-free catalogue rows the simulation reads, the target kinds as data, the weapon families, the Warfront target choice and the Classic clip choice.
// Nothing here is wired: X1 adds the flag and the hooks, X3 the state machine. X7 fills the rows from the inventory; SmContent.Extras.Executions.ExecutionCatalogue
// holds and checks them.

/// <summary>What a timed point of an execution means: the pair's sync point, the contact (hit), the kill frame, the end of the move, or something else the graph names.</summary>
public enum ExecutionEventKind : byte { Other = 0, Sync = 1, Contact = 2, Kill = 3, End = 4 }

/// <summary>A timed point of an execution: its kind, seconds from the attacker clip's start, and the graph's event name (null for ours).</summary>
public readonly record struct ExecutionEvent(ExecutionEventKind Kind, float Seconds, string? Name = null);

/// <summary>
/// One of an attacker move's distance variants (the original blends them on <c>FLT_MeleeTargetDistance</c>; inventory 1.7): its clip, the distance it is authored
/// for (metres: 1, 3.5, 7; the arm break 2, 4, 7) and its root motion (X right, Y forward, Z up in metres from the start, W the turn in radians; evenly spaced
/// over the clip; empty: none).
/// </summary>
public sealed record ExecutionVariant(string Clip, float Distance, IReadOnlyList<Vector4> Root);

/// <summary>
/// One execution move. Who performs it: <see cref="AttackerClass"/> (a <see cref="ClassLoadout.Name"/>, "*" any) with a weapon of <see cref="Weapon"/>'s family
/// (<see cref="ExecutionWeapons"/>). On what: <see cref="Target"/>, a target kind key (<see cref="ExecutionTargets"/>: data, so a roster maps onto it).
/// The clips: the attacker's reference clip (<see cref="AttackerClip"/>, the variant the victim's half is authored against, 3.5 m in the original) and the
/// victim's, their lengths (seconds, after the sequencer's playback speed) and <see cref="Events"/>. Where the victim stands at the start:
/// <see cref="Offset"/> (metres in the attacker's frame for the reference clip; X right, Y forward, Z up) and how it faces (<see cref="RelativeYaw"/>: radians,
/// victim yaw minus attacker yaw; π faces each other). The source: <see cref="Graph"/> and <see cref="Sequencer"/>.
/// </summary>
public sealed record ExecutionRow(string Id, string AttackerClass, string Weapon, string Target,
    string AttackerClip, string VictimClip, float AttackerSeconds, float VictimSeconds, IReadOnlyList<ExecutionEvent> Events,
    Vector3 Offset, float RelativeYaw, string Graph, string Sequencer)
{
    /// <summary>The attacker's distance variants (any order; <see cref="DistanceBand"/>); empty: the reference clip alone, at the offset's forward distance.</summary>
    public IReadOnlyList<ExecutionVariant> AttackerVariants { get; init; } = [];
    /// <summary>The reference clip's root motion (as <see cref="ExecutionVariant.Root"/>); used when there are no variants.</summary>
    public IReadOnlyList<Vector4> AttackerRoot { get; init; } = [];
    /// <summary>The victim clip's root motion (in its own start frame, as <see cref="ExecutionVariant.Root"/>).</summary>
    public IReadOnlyList<Vector4> VictimRoot { get; init; } = [];
    /// <summary>The original's conduit priority for the weapon (higher first in the fallback order; 0 unknown).</summary>
    public int Priority { get; init; }
    /// <summary>A note on the row's provenance (e.g. "ours: races swapped"); not played.</summary>
    public string? Note { get; init; }

    /// <summary>The first event of <paramref name="kind"/> (seconds), or null.</summary>
    public float? At(ExecutionEventKind kind)
    {
        float? best = null;
        foreach (var e in Events) if (e.Kind == kind && (best is null || e.Seconds < best)) best = e.Seconds;
        return best;
    }

    /// <summary>The move's length: its end event, else the longer clip (seconds).</summary>
    public float Seconds => At(ExecutionEventKind.End) ?? MathF.Max(AttackerSeconds, VictimSeconds);

    /// <summary>The kill frame: its kill event, else its contact or sync event, else the end (seconds).</summary>
    public float KillSeconds => At(ExecutionEventKind.Kill) ?? At(ExecutionEventKind.Contact) ?? At(ExecutionEventKind.Sync) ?? Seconds;

    /// <summary>The forward distances the attacker's blend covers (metres): its shortest and longest variant, else the offset's forward distance alone.</summary>
    public (float Min, float Max) DistanceBand
    {
        get
        {
            if (AttackerVariants.Count == 0) return (Offset.Y, Offset.Y);
            float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            foreach (var v in AttackerVariants) { lo = MathF.Min(lo, v.Distance); hi = MathF.Max(hi, v.Distance); }
            return (lo, hi);
        }
    }

    /// <summary>Whether the row can be played: names, finite positive lengths, finite offset, distances and paths, events inside the move.</summary>
    public bool IsValid
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(AttackerClass) || string.IsNullOrWhiteSpace(Weapon) || string.IsNullOrWhiteSpace(Target)) return false;
            if (string.IsNullOrWhiteSpace(AttackerClip) || string.IsNullOrWhiteSpace(VictimClip)) return false;
            if (!(AttackerSeconds > 0f) || !(VictimSeconds > 0f) || !float.IsFinite(AttackerSeconds) || !float.IsFinite(VictimSeconds)) return false;
            if (!Finite(Offset) || !float.IsFinite(RelativeYaw) || !(Offset.Y > 0f)) return false;
            if (Events is null || AttackerRoot is null || VictimRoot is null || AttackerVariants is null) return false;
            float length = MathF.Max(AttackerSeconds, VictimSeconds);
            foreach (var e in Events) if (!float.IsFinite(e.Seconds) || e.Seconds < 0f || e.Seconds > length + 1e-3f) return false;
            foreach (var p in AttackerRoot) if (!Finite(p)) return false;
            foreach (var p in VictimRoot) if (!Finite(p)) return false;
            foreach (var v in AttackerVariants)
            {
                if (v is null || string.IsNullOrWhiteSpace(v.Clip) || !float.IsFinite(v.Distance) || !(v.Distance > 0f) || v.Root is null) return false;
                foreach (var p in v.Root) if (!Finite(p)) return false;
            }
            return true;
        }
    }

    /// <summary>Whether this row is played by <paramref name="attackerClass"/> with a weapon of <paramref name="family"/> (case aside; "*" matches any).</summary>
    public bool PlayedBy(string attackerClass, string family) => Matches(AttackerClass, attackerClass) && Matches(Weapon, family);

    static bool Matches(string row, string asked) => row == "*" || string.Equals(row, asked, StringComparison.OrdinalIgnoreCase);
    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    static bool Finite(Vector4 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W);

    /// <summary>A root path's sample at <paramref name="fraction"/> (0..1) of its clip (linear between samples, clamped); zero without samples.</summary>
    public static Vector4 RootAt(IReadOnlyList<Vector4> root, float fraction)
    {
        if (root.Count == 0) return Vector4.Zero;
        if (root.Count == 1) return root[0];
        float f = Math.Clamp(float.IsFinite(fraction) ? fraction : 0f, 0f, 1f) * (root.Count - 1);
        int i = Math.Min((int)f, root.Count - 2);
        return Vector4.Lerp(root[i], root[i + 1], f - i);
    }

    /// <summary>
    /// The attacker's blend at forward distance <paramref name="distance"/> (clamped to <see cref="DistanceBand"/>): the two variants either side (indices into
    /// <see cref="AttackerVariants"/>, the same one twice at an end or an exact match; -1, -1 without variants) and the weight of the further one.
    /// </summary>
    public (int Near, int Far, float Weight) Blend(float distance)
    {
        if (AttackerVariants.Count == 0) return (-1, -1, 0f);
        int near = -1, far = -1;
        for (int i = 0; i < AttackerVariants.Count; i++)
        {
            float d = AttackerVariants[i].Distance;
            if (d <= distance && (near < 0 || d > AttackerVariants[near].Distance)) near = i;
            if (d >= distance && (far < 0 || d < AttackerVariants[far].Distance)) far = i;
        }
        if (near < 0) return (far, far, 0f);
        if (far < 0) return (near, near, 0f);
        float a = AttackerVariants[near].Distance, b = AttackerVariants[far].Distance;
        return (near, far, b - a > 1e-6f ? Math.Clamp((distance - a) / (b - a), 0f, 1f) : 0f);
    }

    /// <summary>The attacker's root path blended at <paramref name="distance"/> (<see cref="Blend"/>; time fractions matched), or <see cref="AttackerRoot"/> without variants.</summary>
    public IReadOnlyList<Vector4> AttackerRootAt(float distance)
    {
        var (near, far, w) = Blend(distance);
        if (near < 0) return AttackerRoot;
        var a = AttackerVariants[near].Root;
        var b = AttackerVariants[far].Root;
        if (near == far || w <= 0f) return a;
        if (w >= 1f) return b;
        int n = Math.Max(a.Count, b.Count);
        if (n == 0) return [];
        var path = new Vector4[n];
        for (int i = 0; i < n; i++)
        {
            float f = n == 1 ? 0f : i / (float)(n - 1);
            path[i] = Vector4.Lerp(RootAt(a, f), RootAt(b, f), w);
        }
        return path;
    }
}

/// <summary>
/// The target kinds (owner answers 2026-11-25), as data: each row names one kind; a roster maps its enemy names onto kinds (<see cref="Warfront"/> for the Orks,
/// <see cref="ChaosRun"/> for the coming Chaos Warfront) and <see cref="Never"/> lists who is never executed whatever a map says. Classic victims are players:
/// <see cref="Classic"/>.
/// </summary>
public static class ExecutionTargets
{
    /// <summary>The kinds the inventory has victim clips for (docs/executions-coverage.md section 5).</summary>
    public const string OrkBoy = "ork_boy", Gretchin = "gretchin", ChaosMarine = "chaos_marine", Renegade = "renegade", Bloodletter = "bloodletter";
    /// <summary>A loyalist Space Marine victim: no original clip exists (inventory 4.1); Classic plays the Chaos Marine rows with the races swapped (<see cref="Classic"/>).</summary>
    public const string SpaceMarine = "space_marine";

    /// <summary>
    /// The Warfront Ork roster (<see cref="MusouEnemyType"/> names) onto kinds: the Boy, Shoota Boy, 'Ard Boy and Rokkit Boy on the Boy's rows (the campaign boy's
    /// melee, melee-pro and ranged graphs all carry the sync states; the Rokkit Boy uses the boy rig: interpretation), the grounded Stormboy too (owner), the
    /// Gretchin on its own. The Scrap Mek maps to its own kind, which has no rows (no original clip): never chosen until one exists.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Warfront = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [nameof(MusouEnemyType.Boy)] = OrkBoy, [nameof(MusouEnemyType.ShootaBoy)] = OrkBoy, [nameof(MusouEnemyType.ArdBoy)] = OrkBoy,
        [nameof(MusouEnemyType.RokkitBoy)] = OrkBoy, [nameof(MusouEnemyType.Stormboy)] = OrkBoy,
        [nameof(MusouEnemyType.Gretchin)] = Gretchin, [nameof(MusouEnemyType.ScrapMek)] = "scrap_mek",
    };

    /// <summary>
    /// The Chaos run's roster (owner 2026-11-25; names for the roster to come): Traitor Guard and Cultists on the renegade guardsman's rows, the Chaos Tactical,
    /// Havoc and Champion marines on the Chaos Marine's (C1 to C3), the Bloodletter on its own; never the Chosen or the Lord (<see cref="Never"/>).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ChaosRun = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["traitor_guard"] = Renegade, ["cultist"] = Renegade,
        ["chaos_tactical"] = ChaosMarine, ["chaos_havoc"] = ChaosMarine, ["chaos_champion"] = ChaosMarine,
        ["bloodletter"] = Bloodletter,
    };

    /// <summary>Never executed, whatever a map says: the Bomb Squig (owner), the Chaos run's Chosen and Lord (owner); elites and bosses are excluded by <see cref="Of(MusouEnemyType)"/>.</summary>
    public static readonly IReadOnlySet<string> Never = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { nameof(MusouEnemyType.BombSquig), "chosen", "chaos_lord" };

    /// <summary>The kind <paramref name="rosterName"/> maps to in <paramref name="map"/>, or null (unmapped or in <see cref="Never"/>).</summary>
    public static string? Of(string rosterName, IReadOnlyDictionary<string, string> map) =>
        Never.Contains(rosterName) ? null : map.TryGetValue(rosterName, out var kind) ? kind : null;

    /// <summary>A Warfront Ork's kind, or null: never an elite (<see cref="MusouPowerupRules.Elite"/>), a boss (<see cref="MusouPowerupRules.Boss"/>) or one in <see cref="Never"/>.</summary>
    public static string? Of(MusouEnemyType type) =>
        MusouPowerupRules.Elite(type) || MusouPowerupRules.Boss(type) ? null : Of(type.ToString(), Warfront);

    /// <summary>
    /// Whether Classic plays the Chaos Marine rows with the races swapped on a loyalist victim (owner 2026-11-25, "Yes, swap races"; <b>ours</b>: the original has
    /// only the loyalist killing a Chaos Marine; both PvP races share one skeleton, so the killer plays the attacker half and the loyalist the victim half).
    /// </summary>
    public const bool ClassicRaceSwap = true;

    /// <summary>
    /// Classic: the kinds a victim on <paramref name="side"/> takes rows of, in try order: a Chaos victim the Chaos Marine's; a loyalist (or sideless) victim the
    /// loyalist's own (none exist today), then the Chaos Marine's with the races swapped (<see cref="ClassicRaceSwap"/>).
    /// </summary>
    public static IReadOnlyList<(string Kind, bool RaceSwapped)> Classic(Team side) =>
        side == Team.Chaos ? [(ChaosMarine, false)] : ClassicRaceSwap ? [(SpaceMarine, false), (ChaosMarine, true)] : [(SpaceMarine, false)];
}

/// <summary>
/// The weapon families the rows are keyed by (inventory 3.1 and 3.2, owner 2026-11-25): a weapon key (any of the game's spellings: <c>sm_mp_melee_power_axe</c>,
/// <c>mp_melee_chain_axe</c>, <c>powersword</c> …) maps to the first <see cref="Aliases"/> entry its compact form (lower case, no '_', '-' or ' ') contains.
/// </summary>
public static class ExecutionWeapons
{
    public const string Chainsword = "chainsword", PowerAxe = "power_axe", ThunderHammer = "thunder_hammer", CombatKnife = "combat_knife";

    /// <summary>
    /// In match order. The power sword plays the chainsword's moves in every mode with executions (owner override of "no lookalike substitutes", this weapon
    /// only); the chain axe is the power axe's Chaos twin (same graph since the original MP); the daemon maul the hammer's. Anything else (the kick, the claw,
    /// the spear, guns) has no family and no execution.
    /// </summary>
    public static readonly IReadOnlyList<(string Contains, string Family)> Aliases =
    [
        ("powersword", Chainsword), ("chainsword", Chainsword),
        ("chainaxe", PowerAxe), ("poweraxe", PowerAxe),
        ("thunderhammer", ThunderHammer), ("daemonmaul", ThunderHammer), ("demonmaul", ThunderHammer),
        ("combatknife", CombatKnife),
    ];

    /// <summary>The family of <paramref name="weaponKey"/>, or null (no execution).</summary>
    public static string? FamilyOf(string? weaponKey)
    {
        if (string.IsNullOrWhiteSpace(weaponKey)) return null;
        var compact = new string(weaponKey.Where(ch => ch is not ('_' or '-' or ' ')).Select(char.ToLowerInvariant).ToArray());
        foreach (var (part, family) in Aliases) if (compact.Contains(part, StringComparison.Ordinal)) return family;
        return null;
    }
}

/// <summary>
/// One posed clip's hit volumes (built by <c>SmContent.Extras.Executions.ExecutionHitVolumes</c> as <see cref="FlightVolumes"/> is for flight):
/// <see cref="Sets"/>[k] is the clip at k × <see cref="StepSeconds"/>, each in the order and character frame of <see cref="SimConfig.HitVolumes"/>.
/// </summary>
public sealed record ExecutionPose(string Clip, float StepSeconds, IReadOnlyList<IReadOnlyList<HitVolume>> Sets)
{
    /// <summary>The set nearest <paramref name="seconds"/> into the clip (clamped), or null without sets.</summary>
    public IReadOnlyList<HitVolume>? At(float seconds) =>
        Sets.Count == 0 || !(StepSeconds > 0f) ? null : Sets[Math.Clamp((int)MathF.Round((float.IsFinite(seconds) ? seconds : 0f) / StepSeconds), 0, Sets.Count - 1)];

    /// <summary>Whether there is a set, each valid with a head (as <see cref="FlightVolumes.IsValid"/> asks).</summary>
    public bool IsValid => StepSeconds > 0f && Sets is { Count: > 0 } && Sets.All(s => s is { Count: > 0 and <= 64 } && s.All(v => v is not null && v.IsValid) && s.Any(v => v.Zone == HitZone.Head));
}

/// <summary>
/// Executions X2: which Ork a Warfront executor takes and which rows a finisher may play. Deterministic: the same inputs give the same answer, with no dictionary
/// order, clock or unseeded random number involved (ties end on the lower id, rows keep the catalogue's order, the pick among valid moves is seeded:
/// <see cref="Seed"/>).
/// </summary>
public static class ExecutionChoice
{
    /// <summary>Raised when the rules below change (for X1's hashed Executions block).</summary>
    public const int RulesVersion = 1;

    /// <summary>Warfront: furthest a target's feet may be from the executor's (metres, horizontal; ours, the design's "about 2.5 m").</summary>
    public const float WarfrontRange = 2.5f;
    /// <summary>Warfront: candidates rank by distance in bands of this many metres, then by angle in bands of <see cref="AngleBand"/> degrees, then by id (the melee lock's defaults, <see cref="MeleeLockSpec"/>).</summary>
    public const float DistanceBand = 1f, AngleBand = 6f;

    /// <summary>Whether a Warfront Ork of <paramref name="type"/> may ever be executed (a mapped kind, not an elite, a boss or the Bomb Squig); rows decide the rest.</summary>
    public static bool EligibleType(MusouEnemyType type) => ExecutionTargets.Of(type) is not null;

    /// <summary>
    /// Whether <paramref name="e"/> may be executed now: alive and in play (not spawning, dying or in a Stormboy's hop), of an eligible type, and with at least one
    /// valid row in <paramref name="rows"/> for its kind (an Ork with nothing to play is never chosen; the coverage matrix lists such gaps).
    /// </summary>
    public static bool Eligible(in MusouEnemy e, IReadOnlyList<ExecutionRow> rows)
    {
        if (!e.Alive || e.State is MusouEnemyState.Spawning or MusouEnemyState.Airborne) return false;
        if (ExecutionTargets.Of(e.Type) is not { } kind) return false;
        foreach (var r in rows) if (r.IsValid && string.Equals(r.Target, kind, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Warfront: the Orks <paramref name="executor"/> could take, best first (ids): eligible (<see cref="Eligible"/>), not in <paramref name="reserved"/>, within
    /// <see cref="WarfrontRange"/> (horizontal) and the melee lock's vertical reach, seen and not behind a spawn-room blocker (<see cref="MeleeLock.Sees"/>; the Ork
    /// stands in through <see cref="StandIn"/>), and with a row the executor's class and weapon play on its kind; ranked by distance band, then angle band (from
    /// <paramref name="facingYaw"/>, any direction), then id. The caller aligns them in this order (<see cref="ExecutionAlign.Align"/>) and takes the first that fits.
    /// </summary>
    public static IReadOnlyList<ushort> RankOrks(in PlayerState executor, float facingYaw, string attackerClass, string weaponKey, IReadOnlyList<MusouEnemy> enemies,
        IReadOnlyList<ExecutionRow> rows, SimConfig c, CollisionWorld? world, IReadOnlySet<ushort>? reserved = null)
    {
        var ranked = new List<(int Band, int Angle, ushort Id)>();
        if (ExecutionWeapons.FamilyOf(weaponKey) is not { } family) return [];
        var forward = MeleeLock.Forward(facingYaw);
        float vertical = c.MeleeLock.Vertical;
        foreach (var e in enemies)
        {
            if (e.Id == 0 || reserved is not null && reserved.Contains(e.Id) || !Eligible(e, rows)) continue;
            if (Rows(rows, attackerClass, family, ExecutionTargets.Of(e.Type)!).Count == 0) continue;
            var flat = new Vector2(e.Position.X - executor.Position.X, e.Position.Y - executor.Position.Y);
            float distance = flat.Length();
            if (!float.IsFinite(distance) || distance > WarfrontRange || MathF.Abs(e.Position.Z - executor.Position.Z) > vertical) continue;
            float angle = distance < 1e-4f ? 0f : MathF.Acos(Math.Clamp(Vector2.Dot(flat / distance, forward), -1f, 1f)) * 180f / MathF.PI;
            if (!MeleeLock.Sees(executor, StandIn(executor, e), c, world)) continue;
            ranked.Add(((int)(distance / DistanceBand), (int)(angle / AngleBand), e.Id));
        }
        ranked.Sort((a, b) => a.Band != b.Band ? a.Band.CompareTo(b.Band) : a.Angle != b.Angle ? a.Angle.CompareTo(b.Angle) : a.Id.CompareTo(b.Id));
        var ids = new List<ushort>(ranked.Count);
        foreach (var r in ranked) if (!ids.Contains(r.Id)) ids.Add(r.Id);
        return ids;
    }

    /// <summary>The best of <see cref="RankOrks"/> (the target the HUD prompt shows), or 0.</summary>
    public static ushort ChooseOrk(in PlayerState executor, float facingYaw, string attackerClass, string weaponKey, IReadOnlyList<MusouEnemy> enemies,
        IReadOnlyList<ExecutionRow> rows, SimConfig c, CollisionWorld? world, IReadOnlySet<ushort>? reserved = null) =>
        RankOrks(executor, facingYaw, attackerClass, weaponKey, enemies, rows, c, world, reserved) is { Count: > 0 } r ? r[0] : (ushort)0;

    /// <summary>An Ork as a player state for the level checks (<see cref="MeleeLock.Sees"/>, <see cref="ExecutionAlign"/>): its place and yaw, alive, grounded, no side, the executor's class.</summary>
    public static PlayerState StandIn(in PlayerState executor, in MusouEnemy e) =>
        new(0, e.Position, Vector3.Zero, e.Yaw, true) { Alive = true, Class = executor.Class, Team = Team.None };

    /// <summary>
    /// The valid rows <paramref name="attackerClass"/> with a weapon of <paramref name="family"/> (<see cref="ExecutionWeapons.FamilyOf"/>) plays on a victim of
    /// <paramref name="kind"/>: rows with fewer "*" keys (class, weapon) first, then by <see cref="ExecutionRow.Priority"/> (higher first), then catalogue order.
    /// The variants <see cref="ExecutionAlign.Align"/> checks.
    /// </summary>
    public static IReadOnlyList<ExecutionRow> Rows(IReadOnlyList<ExecutionRow> rows, string attackerClass, string family, string kind)
    {
        var list = new List<(int Wild, int Priority, int Index, ExecutionRow Row)>();
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r is null || !r.IsValid || !string.Equals(r.Target, kind, StringComparison.OrdinalIgnoreCase) || !r.PlayedBy(attackerClass, family)) continue;
            list.Add(((r.AttackerClass == "*" ? 1 : 0) + (r.Weapon == "*" ? 1 : 0), r.Priority, i, r));
        }
        list.Sort((a, b) => a.Wild != b.Wild ? a.Wild.CompareTo(b.Wild) : a.Priority != b.Priority ? b.Priority.CompareTo(a.Priority) : a.Index.CompareTo(b.Index));
        return list.Select(x => x.Row).ToArray();
    }

    /// <summary>
    /// The seed of the pick among moves that pass the terrain check (the inventory's recommendation of a random pick, made deterministic so the server and every
    /// client agree; <b>ours</b>): a mix of the tick the execution starts on and the executor's and target's ids.
    /// </summary>
    public static ulong Seed(uint tick, int executor, int target)
    {
        ulong h = 0x9E3779B97F4A7C15UL ^ tick;
        h = Mix(h ^ ((ulong)(uint)executor << 32));
        h = Mix(h ^ (uint)target);
        return Mix(h);

        static ulong Mix(ulong z)
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>
    /// Warfront: the move <paramref name="executor"/> plays on Ork <paramref name="e"/> with a weapon <paramref name="weaponKey"/>: a seeded pick (<paramref name="seed"/>,
    /// <see cref="Seed"/>) among the rows for its kind that align (<see cref="ExecutionAlign.Align"/>); a failed result (nothing happens) when none does.
    /// </summary>
    public static ExecutionAlignResult ChooseWarfront(in PlayerState executor, string attackerClass, string weaponKey, in MusouEnemy e,
        IReadOnlyList<ExecutionRow> rows, SimConfig c, CollisionWorld? world, ulong? seed)
    {
        var family = ExecutionWeapons.FamilyOf(weaponKey);
        var kind = ExecutionTargets.Of(e.Type);
        var variants = family is null || kind is null ? [] : Rows(rows, attackerClass, family, kind);
        return ExecutionAlign.Align(executor, StandIn(executor, e), variants, c, world, seed);
    }

    /// <summary>
    /// Classic: the finisher <paramref name="attacker"/> plays on the corpse of <paramref name="victim"/> with a weapon <paramref name="weaponKey"/>: for each kind the
    /// victim takes (<see cref="ExecutionTargets.Classic"/>, in order) a seeded pick among the rows that align; <see cref="ExecutionAlignResult.RaceSwapped"/> when
    /// the Chaos Marine rows play on a loyalist. A failed result (the normal kill stands) when no row fits.
    /// </summary>
    public static ExecutionAlignResult ChooseClassic(in PlayerState attacker, string attackerClass, string weaponKey, in PlayerState victim,
        IReadOnlyList<ExecutionRow> rows, SimConfig c, CollisionWorld? world, ulong? seed)
    {
        ExecutionAlignResult? first = null;
        if (ExecutionWeapons.FamilyOf(weaponKey) is { } family)
            foreach (var (kind, swapped) in ExecutionTargets.Classic(victim.Team))
            {
                var variants = Rows(rows, attackerClass, family, kind);
                if (variants.Count == 0) continue;
                var r = ExecutionAlign.Align(attacker, victim, variants, c, world, seed);
                if (r.Ok) return r with { RaceSwapped = swapped };
                first ??= r;
            }
        return first ?? ExecutionAlign.Align(attacker, victim, [], c, world, seed);
    }
}
