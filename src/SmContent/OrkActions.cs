using SmFormats.Havok;
using SmSim;

namespace SmContent;

/// <summary>
/// The Ork Boy's actions on screen (Ork O7b / O7c; docs/ork-faction-design.md section 5): the clips and timings the networked client poses when an Ork shoots,
/// reloads, throws, is hit, dodges, swings the choppa, charges and dies, read from the boy's own behaviour graphs. The simulation runs the marine's timings for the
/// reactions and the throw (design section 1.3, parity) and the Ork's own for the choppa, the charge and the hop (O4); this record holds what the client needs to
/// lay the boy's clips over those times (the rules are <see cref="OrkActionRules"/>). Engine-free, so a test can feed it synthetic data.
/// </summary>
/// <param name="Reactions">The Ork's own reaction clips by kind (<see cref="ReactionTuning.OrkRig"/>), timing from <c>BOY_REACTIONS_Coop</c>; empty when the graph or a clip is missing.</param>
/// <param name="Reload">The boy's reload clip (<c>BOY_2HR_RNG_Reload_S_01</c>) for the gun and the pistol, stretched to the weapon's reload time (**ours**: the clip is 4.1 s, the shoota reloads in 2.0 s).</param>
/// <param name="GunRecoil">The shoota's per-shot additive recoil (<c>BOY_2HR_RNG_Fire_S_01</c>, from <c>BOY_2HR_Shoot</c>).</param>
/// <param name="PistolRecoil">The slugga's (the boy has no pistol set: the same 2HR clip, **ours**).</param>
/// <param name="Throw">The stikkbomb toss's clip and its release and end moments.</param>
/// <param name="Deaths">The death clips with the moment the original hands the body to its powered ragdoll.</param>
/// <param name="UpperBodyMask">The bone mask that lays an action clip over the upper body and leaves the legs to the locomotion (the reload layer's mask).</param>
public sealed record OrkActionPlan(IReadOnlyList<ReactionSpec> Reactions, ReloadAnimations? Reload, RecoilClip? GunRecoil, RecoilClip? PistolRecoil, OrkThrowClip? Throw,
    IReadOnlyList<OrkDeathClip> Deaths, IReadOnlyDictionary<string, float>? UpperBodyMask, IReadOnlyList<string> Notes)
{
    /// <summary>The Ork's reaction clips of a kind, or null.</summary>
    public ReactionSpec? ReactionOf(ReactionKind kind)
    {
        for (int i = 0; i < Reactions.Count; i++) if (Reactions[i].Kind == kind) return Reactions[i];   // a loop, not LINQ: called per Ork and frame, no closure allocation
        return null;
    }

    /// <summary>The recoil clip of the weapon family in hand.</summary>
    public RecoilClip? RecoilFor(bool pistol) => pistol ? PistolRecoil ?? GunRecoil : GunRecoil ?? PistolRecoil;

    /// <summary>The death clip of a kind, or the first one there is.</summary>
    public OrkDeathClip? DeathOf(OrkDeathKind kind)
    {
        for (int i = 0; i < Deaths.Count; i++) if (Deaths[i].Kind == kind) return Deaths[i];
        return Deaths.Count > 0 ? Deaths[0] : null;
    }

    /// <summary>Every clip the plan names, for a check against the rig.</summary>
    public IEnumerable<string> Clips()
    {
        foreach (var r in Reactions) foreach (var c in r.Clips) yield return c.Animation;
        if (Reload is not null) foreach (var c in Reload.Clips) yield return c.Clip;
        if (GunRecoil is not null) yield return GunRecoil.Clip;
        if (PistolRecoil is not null) yield return PistolRecoil.Clip;
        if (Throw is not null) yield return Throw.Clip;
        foreach (var d in Deaths) yield return d.Clip;
    }
}

/// <summary>The stikkbomb toss: the clip (<c>BOY_2HR_RNG_GrenadeToss</c>), its length, the moment the bomb leaves the hand (<c>Ability_Ranged_Fire</c>) and the moment the throw is over for a mover (<c>BRANCH_Run</c>, else <c>BRANCH_GrenadeToss_End</c>), in clip seconds.</summary>
public sealed record OrkThrowClip(string Clip, float Length, float ReleaseAt, float EndAt);

/// <summary>Which death an Ork dies (the graph's death states, picked by the hit's side; the choice rules are <see cref="OrkActionRules.DeathKindOf"/>).</summary>
public enum OrkDeathKind : byte
{
    /// <summary>Shot from the front: falls backwards (<c>BOY_2HR_DIE_B</c>, the graph's "Death Back").</summary>
    Backward,
    /// <summary>Shot from behind: falls forward (<c>BOY_2HR_DIE_Fall_Forward_01</c>, "Death Front State").</summary>
    Forward,
    /// <summary>A melee kill from the left front or the right back (<c>BOY_1HM_DIE_Basic_02</c>: "Death Left" and "Melee Death Back Right").</summary>
    MeleeA,
    /// <summary>A melee kill from the left back or the right front (<c>BOY_1HM_DIE_Basic_03</c>: "Death Left Back" and "Death Right").</summary>
    MeleeB,
}

/// <summary>A death clip: its length and the moment (clip seconds) the original's <c>Pre-Powered Ragdoll</c> modifier window ends and the powered ragdoll takes the body over.</summary>
public sealed record OrkDeathClip(OrkDeathKind Kind, string Clip, float Length, float RagdollAt);

/// <summary>
/// The engine-free rules of the Ork's actions (Ork O7b / O7c): how the boy's clips are laid over the times the simulation runs. All of them are **ours** where they
/// stretch a clip to the marine timings the simulation keeps (design section 5: "stretched to the sim's marine timings"); the clip choices are the graphs'.
/// </summary>
public static class OrkActionRules
{
    /// <summary>
    /// The Ork's reaction clip time for the simulation's time: the whole of the clip up to its <c>BRANCH_Reaction_End</c> (<paramref name="orkEnd"/>) is played over the
    /// marine clip's (<paramref name="simEnd"/>), the time the simulation holds the player. Clamped to the clip. An unusable end plays the clip at its own speed.
    /// </summary>
    public static float ReactionTime(float simTime, float simEnd, float orkEnd, float orkLength)
    {
        if (!float.IsFinite(simTime) || simTime < 0f) simTime = 0f;
        float scaled = simEnd > 1e-4f && orkEnd > 1e-4f && float.IsFinite(simEnd) && float.IsFinite(orkEnd) ? simTime / simEnd * orkEnd : simTime;
        return Math.Clamp(scaled, 0f, MathF.Max(0f, orkLength));
    }

    /// <summary>The clip seconds that pass in one simulation second for a clip the simulation plays over <paramref name="simDuration"/> (a sequencer's playback speed: the Ork's right hop plays at 1.5); 1 when either is unusable.</summary>
    public static float ClipRate(float clipLength, float simDuration) =>
        clipLength > 1e-3f && simDuration > 1e-3f && float.IsFinite(clipLength) && float.IsFinite(simDuration) ? clipLength / simDuration : 1f;

    /// <summary>
    /// The toss clip time for the simulation's time into the throw: piecewise linear, the simulation's release (<paramref name="simRelease"/>) on the clip's
    /// (<paramref name="clipRelease"/>) and the simulation's end (<paramref name="simEnd"/>) on the clip's, so the stikkbomb leaves the hand in the clip when the
    /// simulation throws it. Past the end the clip goes on at the last segment's rate; clamped to <paramref name="clipLength"/>.
    /// </summary>
    public static float ThrowClipTime(float simTime, float simRelease, float simEnd, float clipRelease, float clipEnd, float clipLength)
    {
        if (!float.IsFinite(simTime) || simTime < 0f) simTime = 0f;
        float t;
        if (simRelease > 1e-4f && simTime < simRelease) t = simTime / simRelease * clipRelease;
        else if (simEnd > simRelease + 1e-4f) t = clipRelease + (simTime - simRelease) / (simEnd - simRelease) * (clipEnd - clipRelease);
        else t = simTime;
        return Math.Clamp(t, 0f, MathF.Max(0f, clipLength));
    }

    /// <summary>
    /// The death an Ork dies. <paramref name="melee"/>: a melee kill (the "Melee Death" states, left or right by the hit's side, front or back by its direction); otherwise a
    /// shot, blast or fall: forward when the push runs along the Ork's facing (hit from behind), else backward. <paramref name="pushAlongFacing"/> and
    /// <paramref name="pushAlongRight"/> are the killing blow's direction on the Ork's facing and right axes (<paramref name="hasDirection"/> false: none known, shown as a hit from the
    /// front). The wedge test is the graph's (-90 to 90 degrees); the left / right split of the melee deaths is **ours**.
    /// </summary>
    public static OrkDeathKind DeathKindOf(bool melee, bool hasDirection, float pushAlongFacing, float pushAlongRight)
    {
        bool fromBehind = hasDirection && pushAlongFacing > 0f;
        if (!melee) return fromBehind ? OrkDeathKind.Forward : OrkDeathKind.Backward;
        bool fromLeft = hasDirection && pushAlongRight > 0f;   // pushed to the right: struck from the left
        // left front -> A (Death Left), left back -> B (Death Left Back), right front -> B (Death Right), right back -> A (Melee Death Back Right)
        return fromLeft == fromBehind ? OrkDeathKind.MeleeB : OrkDeathKind.MeleeA;
    }

    /// <summary>How long the death clip plays before the ragdoll takes the body, seconds of real time: the window, never past the clip.</summary>
    public static float DeathHold(OrkDeathClip clip) => Math.Clamp(clip.RagdollAt, 0f, MathF.Max(0f, clip.Length));

    /// <summary>
    /// The seconds into a collision-stop the charge's clip is posed at: the Ork has no collision clip, so the stop plays the charge's own clip on from the moment its hit lands
    /// (<paramref name="contactUntil"/>, <see cref="RushSpec.ContactUntil"/>), clamped to the clip (**ours**, spec "The Ork's charge").
    /// </summary>
    public static float RushStopClipTime(float contactUntil, float stopTime, float clipLength) => Math.Clamp(contactUntil + MathF.Max(0f, stopTime), 0f, MathF.Max(0f, clipLength));

    /// <summary>
    /// The charge clip's time for the simulation's rush state: in the rush, the simulation's time (clamped to <paramref name="moveClipLength"/>, the rush move's clip length) at the rate the
    /// clip plays at over it (1 when the move is the clip); in the collision stop, <see cref="RushStopClipTime"/>, which is already in clip seconds and must not be scaled again (the stop move is only
    /// the stop's own 0.333 s long); 0 otherwise.
    /// </summary>
    public static float RushClipTime(RushPhase phase, float contactUntil, float simTime, float moveClipLength, float clipLength) => phase switch
    {
        RushPhase.Rush => Math.Min(Math.Clamp(float.IsFinite(simTime) ? simTime : 0f, 0f, MathF.Max(0f, moveClipLength)) * ClipRate(clipLength, moveClipLength), MathF.Max(0f, clipLength)),
        RushPhase.Collision => RushStopClipTime(contactUntil, float.IsFinite(simTime) ? simTime : 0f, clipLength),
        _ => 0f,
    };
}

/// <summary>Reads the Ork's action plan (Ork O7b / O7c). Read-only.</summary>
public static class OrkActions
{
    /// <summary>The graph variable the reload clip's duration is bound to on the marine; the Ork's clip is stretched to it too (**ours**).</summary>
    public const string EnforcedReloadDuration = "FLT_WeaponReloadTime";

    /// <summary>The death clips the plan looks for, in <see cref="OrkDeathKind"/> order (the Base_RANGED_Coop "Death" states).</summary>
    public static readonly IReadOnlyList<(OrkDeathKind Kind, string Clip)> DeathClips =
    [
        (OrkDeathKind.Backward, "BOY_2HR_DIE_B"),
        (OrkDeathKind.Forward, "BOY_2HR_DIE_Fall_Forward_01"),
        (OrkDeathKind.MeleeA, "BOY_1HM_DIE_Basic_02"),
        (OrkDeathKind.MeleeB, "BOY_1HM_DIE_Basic_03"),
    ];

    /// <summary>
    /// The clips the simulation's own Ork data name (the choppa's 3.5 m combo and heavy strike, the charge, the side hops): decoded with the plan's, so their first use is not a
    /// decode on the frame (a hint only; the client takes the names from the configuration).
    /// </summary>
    public static readonly IReadOnlyList<string> SimClips =
    [
        "BOY_1HM_MEL_Cmbo_01_Atk01_3.5m", "BOY_1HM_MEL_Cmbo_01_Atk02_3.5m", "BOY_1HM_MEL_Cmbo_01_Hvy01_3.5m", OrkClass.RushClip,
        "BOY_2HR_NAV_Evade4m_L_01", "BOY_2HR_NAV_Evade4m_R_01",
    ];

    /// <summary>The window modifier whose end hands the body to the powered ragdoll (the sequencers' <c>Pre-Powered Ragdoll</c> tags).</summary>
    public const string PreRagdollTag = "Pre-Powered Ragdoll";

    /// <summary>Reads the Ork rig from the install and builds the plan.</summary>
    public static OrkActionPlan Load(string gameDir)
    {
        var profile = RigProfiles.OrkBoy;
        var character = profile.LoadCharacter(gameDir);
        return From(character, profile, HipFireAnimations.From(character, profile));
    }

    /// <summary>The plan from the Ork rig's file (already read) and its hip-fire data.</summary>
    public static OrkActionPlan From(HavokCharacterFile character, RigProfile profile, HipFireAnimations hip)
    {
        var graphs = HavokBehavior.Read(character.Pack);
        Func<string, float> length = name => character.LoadAnimation(name).Animation.Duration;
        var reload = TryReload(character, profile);
        return From(graphs, length, profile, hip, reload);
    }

    /// <summary>The plan from already-read graphs (testable without an install); <paramref name="reload"/> is read by the caller (it needs the detail reader).</summary>
    public static OrkActionPlan From(IReadOnlyList<BehaviorGraph> graphs, Func<string, float> animationLength, RigProfile profile, HipFireAnimations? hip, ReloadAnimations? reload)
    {
        var notes = new List<string>();
        var reactions = ReactionTuning.Specs(profile.Reactions, graphs, animationLength, notes) ?? [];
        var stretched = reload is null ? null : new ReloadAnimations(reload.Clips.Select(c => c.EnforcedDuration is null ? c with { EnforcedDuration = EnforcedReloadDuration } : c).ToList());
        if (reload is not null && reload.Clips.Any(c => c.EnforcedDuration is null))
            notes.Add("reload: the boy's clip has no enforced duration; it is played over the weapon's reload time (ours)");
        var gun = hip?.For(HipFireWeaponClass.Rifle)?.Recoil;
        var pistol = hip?.For(HipFireWeaponClass.Pistol)?.Recoil;
        if (gun is null) notes.Add("recoil: no shoota recoil clip; shots show no recoil");
        var toss = Throw(graphs, profile.Clips.TossClip, animationLength, notes);
        var deaths = Deaths(graphs, animationLength, notes, profile.BaseGraph);
        var mask = hip?.For(HipFireWeaponClass.Rifle)?.UpperBodyMask ?? hip?.Sets.Select(s => s.UpperBodyMask).FirstOrDefault(m => m is not null);
        if (mask is null) notes.Add("no upper-body bone mask: action clips keep the whole body");
        return new OrkActionPlan(reactions, stretched, gun, pistol, toss, deaths, mask, notes);
    }

    static ReloadAnimations? TryReload(HavokCharacterFile character, RigProfile profile)
    {
        try { return ReloadAnimations.From(character, profile); }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or ArgumentException or IndexOutOfRangeException or NotSupportedException)
        { return null; }
    }

    /// <summary>The toss clip with its release (<c>Ability_Ranged_Fire</c>) and end (<c>BRANCH_Run</c>, the earliest moment a mover leaves the throw; else <c>BRANCH_GrenadeToss_End</c>).</summary>
    public static OrkThrowClip? Throw(IReadOnlyList<BehaviorGraph> graphs, string? clip, Func<string, float> animationLength, List<string> notes)
    {
        if (clip is null) { notes.Add("throw: the rig names no toss clip"); return null; }
        var seq = graphs.SelectMany(g => g.Sequencers).FirstOrDefault(s => s.Animations.Count == 1 && s.Animations[0].Equals(clip, StringComparison.OrdinalIgnoreCase)
                                                                           && s.Tags.Any(t => t.Name == "Ability_Ranged_Fire"));
        if (seq is null) { notes.Add($"throw: no sequencer plays {clip} with a release event"); return null; }
        float speed = seq.PlaybackSpeeds.Count > 0 && float.IsFinite(seq.PlaybackSpeeds[0]) && seq.PlaybackSpeeds[0] > 0f ? seq.PlaybackSpeeds[0] : 1f;
        float len;
        try { len = animationLength(clip); }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or ArgumentException or NotSupportedException) { notes.Add($"throw: {clip}: {e.Message}"); return null; }
        if (!float.IsFinite(len) || len <= 0f) { notes.Add($"throw: {clip} has no usable length"); return null; }
        float release = seq.Tags.Where(t => t.Name == "Ability_Ranged_Fire").Min(t => t.Start) * len;
        float? run = seq.Tags.Where(t => t.Kind == "Branch" && t.Name == "BRANCH_Run").Select(t => (float?)t.Start).Min();
        float? end = seq.Tags.Where(t => t.Kind == "Branch" && t.Name == "BRANCH_GrenadeToss_End").Select(t => (float?)t.Start).Min();
        float endAt = (run ?? end ?? 1f) * len;
        if (endAt <= release + 1e-3f) endAt = len;
        if (speed != 1f) notes.Add($"throw: {clip} plays at speed {speed}; times are in clip seconds");
        return new OrkThrowClip(clip, len, release, endAt);
    }

    /// <summary>The death clips: each one's length and the end of the window of its <see cref="PreRagdollTag"/> modifier (else 0.4 s, ours), the graphs' hand-over to the powered ragdoll.</summary>
    public static IReadOnlyList<OrkDeathClip> Deaths(IReadOnlyList<BehaviorGraph> graphs, Func<string, float> animationLength, List<string> notes, string? graph = null)
    {
        var list = new List<OrkDeathClip>();
        foreach (var (kind, clip) in DeathClips)
        {
            float len;
            try { len = animationLength(clip); }
            catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or ArgumentException or NotSupportedException) { notes.Add($"death {clip}: {e.Message}"); continue; }
            if (!float.IsFinite(len) || len <= 0f) { notes.Add($"death {clip} has no usable length"); continue; }
            // The death states live in the ranged base graph; the melee graph's copies of the clips carry other windows.
            float? Window(IEnumerable<BehaviorGraph> from) => from.SelectMany(g => g.Sequencers)
                .Where(s => s.Animations.Count == 1 && s.Animations[0].Equals(clip, StringComparison.OrdinalIgnoreCase))
                .SelectMany(s => s.Tags.Where(t => t.Kind == "Modifier" && t.Name is { } n && n.StartsWith(PreRagdollTag, StringComparison.Ordinal) && float.IsFinite(t.End)))
                .Select(t => (float?)Math.Clamp(t.End, 0f, 1f)).Max();
            float? end = (graph is null ? null : Window(graphs.Where(g => g.Name.Equals(graph, StringComparison.OrdinalIgnoreCase)))) ?? Window(graphs);
            if (end is null) notes.Add($"death {clip}: no {PreRagdollTag} window; the ragdoll takes over after 0.4 s (ours)");
            list.Add(new OrkDeathClip(kind, clip, len, Math.Min(len, (end ?? 0.4f / len) * len)));
        }
        return list;
    }
}
