using System.Numerics;
using SmSim.Extras.Executions;

namespace SmContent.Extras.Executions;

/// <summary>One decapitating row: the row's id before its weapon (<c>C1</c>), the victim kind, the victim tag that marks the cut and the cut's time when the sheet lacks the tag (seconds).</summary>
public sealed record ExecutionDecapRow(string RowBase, string Target, string CutTag, float FallbackSeconds);

/// <summary>What the cut does to a victim's body (<see cref="ExecutionDecapRules.BodyFor"/>).</summary>
public enum ExecutionDecapBody : byte
{
    /// <summary>Left as today (the geyser and blood only): no way to make it look right.</summary>
    None = 0,
    /// <summary>An Ork Boy mesh with the install's headless body (its <c>GEO_Wounded_Neck</c> block, original asset): the body swaps to it.</summary>
    OrkStump = 1,
    /// <summary>A marine: the head is its own armour piece (helmet or bare head), hidden; a neck cap of ours closes the collar (the armour has no stump geometry).</summary>
    HeadPieces = 2,
}

/// <summary>
/// Executions: the head cut (owner 2026-11-27, card "Decap": "Search, then build ours"; docs/executions-cues.md section 6). Everything here is ours except where named data:
/// <list type="bullet">
/// <item>The rows: C1 (every wield; the Chaos Marine's victim half, cut at its <c>ACTN_FX_CS_Sync_Blood_Geyser</c>, 4.300 s) and O5 (the Ork Boy kinds, at <c>ACTN_OrkBoy_Sync_HeadDecapHit</c>,
/// 1.867 s; the 0.500 s <c>ACTN_Ork_Decap_AxBackHit</c> is the first chop into the back). The time is the tag's own (supported by evidence); the reading that the head comes off there is ours.</item>
/// <item>Left as today: B1 (the Bloodletter's <c>HeadSawOff</c>: the data vanishes the daemon 17 ms later) and O7 (the hammer's head pop crushes; it does not sever).</item>
/// <item>The cut is one more cue on the run's list (<see cref="WithCut"/>), so the cue clock's rules hold for it: once per run on every client, skipped when first seen more than
/// <see cref="ExecutionCueTimeline.LateSkipSeconds"/> late, never after an interruption.</item>
/// <item>It draws only where execution gore draws (<see cref="Allowed"/>: Warfront, Classic with the Extended gore; never with <c>--no-execution-gore</c>).</item>
/// </list>
/// </summary>
public static class ExecutionDecapRules
{
    public static readonly IReadOnlyList<ExecutionDecapRow> Rows =
    [
        new("C1", ExecutionTargets.ChaosMarine, "ACTN_FX_CS_Sync_Blood_Geyser", 4.300f),
        new("O5", ExecutionTargets.OrkBoy, "ACTN_OrkBoy_Sync_HeadDecapHit", 1.867f),
    ];

    /// <summary>The Ork meshes whose object tree has a headless body (<see cref="StumpNode"/>; checked on the install 2026-11-27). <c>boy_ardboy</c> and the campaign <c>boy_stormboy</c> have none.</summary>
    public static readonly IReadOnlyList<string> OrkStumpMeshes = ["boy_melee", "boy_ranged", "boy_melee_pro", "boy_ranged_pro"];

    /// <summary>The object-tree node of the headless body (matched ignoring case; the data's dismemberment points name it <c>geo_wounded_neck</c>).</summary>
    public const string StumpNode = "wounded_neck";

    /// <summary>The bones a head hangs from, tried in order (the Ork and marine rigs' <c>RIG_Head</c>); the head is this bone and every bone under it.</summary>
    public static readonly IReadOnlyList<string> HeadBones = ["RIG_Head", "Bip01 Head"];

    /// <summary>A piece whose vertices are this share or more on the head bones is a head piece (a helmet, a bare head): hidden whole.</summary>
    public const float HeadPieceShare = 0.7f;

    /// <summary>The neck wound: the data's head point (<c>explode_head</c>: <c>body_dismemberment</c> x0.4 and <c>body_dismemberment_drip</c> x0.8 at <c>fx_gore_neck</c>), played at the neck.</summary>
    public static readonly IReadOnlyList<(string Effect, float Scale)> NeckWound = [("body_dismemberment", 0.4f), ("body_dismemberment_drip", 0.8f)];

    /// <summary>The toss (ours): metres a second along the blade's exit (away from the attacker, level), up, and the spin (radians a second).</summary>
    public const float TossAlong = 2.2f, TossUp = 2.4f, TossSpin = 7f;
    /// <summary>The loose head's mass (kg) and the radius of its collision sphere at scale 1 (metres; ours).</summary>
    public const float HeadMass = 6f, HeadRadius = 0.13f;
    /// <summary>A loose head goes this long after the cut at the latest (seconds, ours), if its corpse has not taken it before.</summary>
    public const float HeadMaxSeconds = 90f;

    /// <summary>The row's decap entry, or null for a row that does not cut a head.</summary>
    public static ExecutionDecapRow? RowFor(string rowId)
    {
        int dot = rowId.IndexOf('.');
        string head = dot < 0 ? rowId : rowId[..dot];
        foreach (var r in Rows) if (string.Equals(r.RowBase, head, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }

    /// <summary>The cut's time: the first cue of the row's own cut tag (the data's), else the row's fallback.</summary>
    public static float CutSeconds(ExecutionDecapRow row, IReadOnlyList<ExecutionCue> cues)
    {
        foreach (var c in cues) if (c.Kind != ExecutionCueKind.Decap && string.Equals(c.Tag, row.CutTag, StringComparison.OrdinalIgnoreCase)) return c.At;
        return row.FallbackSeconds;
    }

    /// <summary>The cue that cuts the head (ours).</summary>
    public static ExecutionCue CutCue(string rowId, ExecutionWield wield, ExecutionDecapRow row, float at) =>
        new(rowId, wield, ExecutionCueSide.Victim, ExecutionCueKind.Decap, ExecutionCuePlace.Victim, at, at, row.CutTag, "decap", "ours", Ours: true, Blood: true);

    /// <summary>The run's cue list with the cut put in its place by time (after the cues of the same instant); the list itself for a row that cuts nothing.</summary>
    public static IReadOnlyList<ExecutionCue> WithCut(string rowId, ExecutionWield wield, IReadOnlyList<ExecutionCue> cues)
    {
        if (RowFor(rowId) is not { } row) return cues;
        float at = CutSeconds(row, cues);
        var list = new List<ExecutionCue>(cues.Count + 1);
        bool added = false;
        foreach (var c in cues)
        {
            if (!added && c.At > at) { list.Add(CutCue(rowId, wield, row, at)); added = true; }
            list.Add(c);
        }
        if (!added) list.Add(CutCue(rowId, wield, row, at));
        return list;
    }

    /// <summary>Whether the cut draws: where execution gore draws (<see cref="ExecutionCueRules.BloodAllowed"/>).</summary>
    public static bool Allowed(SmSim.ContentExtras extras, bool off = false) => ExecutionCueRules.BloodAllowed(extras, off);

    /// <summary>
    /// What the cut does to a body: an Ork Boy mesh of <see cref="OrkStumpMeshes"/> swaps to its headless body; a marine victim (the Chaos Marine's rows) with a head piece hides it; anything
    /// else is left as today. <paramref name="mesh"/>: the Ork look's mesh name (null for a marine); <paramref name="headPieces"/>: the marine's pieces found on the head bones.
    /// </summary>
    public static ExecutionDecapBody BodyFor(ExecutionDecapRow row, string? mesh, int headPieces)
    {
        if (string.Equals(row.Target, ExecutionTargets.OrkBoy, StringComparison.OrdinalIgnoreCase))
            return mesh is not null && OrkStumpMeshes.Contains(mesh, StringComparer.OrdinalIgnoreCase) ? ExecutionDecapBody.OrkStump : ExecutionDecapBody.None;
        if (string.Equals(row.Target, ExecutionTargets.ChaosMarine, StringComparison.OrdinalIgnoreCase)) return headPieces > 0 ? ExecutionDecapBody.HeadPieces : ExecutionDecapBody.None;
        return ExecutionDecapBody.None;
    }

    /// <summary>The Ork look's mesh from its piece name (<c>ork_boy_melee</c>, <c>musou_boy_melee</c>); null for another.</summary>
    public static string? OrkMeshOf(string pieceName)
    {
        foreach (var prefix in new[] { "musou_", "ork_" })
            if (pieceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return pieceName[prefix.Length..];
        return null;
    }

    /// <summary>Whether a triangle goes with the head: two or more of its corners are on the head bones (a seam triangle stays on the body).</summary>
    public static bool HeadTriangle(bool a, bool b, bool c) => (a ? 1 : 0) + (b ? 1 : 0) + (c ? 1 : 0) >= 2;

    /// <summary>
    /// The toss (ours): along the blade's exit (<paramref name="away"/>, from the attacker toward the victim; any length, its vertical part dropped) and up, with a spin about the level axis
    /// across the exit, tilted by the run's <paramref name="seed"/> so heads do not all spin alike. Y up (the client's axes).
    /// </summary>
    public static (Vector3 Linear, Vector3 Angular) Toss(Vector3 away, uint seed)
    {
        var level = new Vector3(away.X, 0f, away.Z);
        level = level.LengthSquared() > 1e-6f ? Vector3.Normalize(level) : Vector3.UnitZ;
        var linear = level * TossAlong + Vector3.UnitY * TossUp;
        var across = Vector3.Cross(Vector3.UnitY, level);
        float tilt = ((seed * 2654435761u) >> 8) / (float)(1u << 24) * 0.6f - 0.3f;   // -0.3 .. 0.3
        var axis = Vector3.Normalize(across + level * tilt);
        return (linear, axis * TossSpin);
    }
}
