using SmFormats.Havok;

namespace SmContent;

/// <summary>
/// One locomotion clip with the pitch variants the aim blend uses (Ork O7a): the level clip, and the clips that aim down and up when the rig has them, each with
/// the aim pitch it stands for (degrees, negative down; the graphs' <c>FLT_Aim_Vertical_Deg</c> blend entries).
/// </summary>
public sealed record OrkPitchClip(string Level, string? Down, float DownDegrees, string? Up, float UpDegrees)
{
    /// <summary>The weights of (down, level, up) for an aim pitch in degrees: linear between the level clip and the neighbouring variant, level only where the rig has no variant.</summary>
    public (float Down, float Level, float Up) WeightsAt(float pitchDegrees)
    {
        if (!float.IsFinite(pitchDegrees)) return (0f, 1f, 0f);
        if (pitchDegrees >= 0f) { float t = Up is null || UpDegrees < 1f ? 0f : Math.Clamp(pitchDegrees / UpDegrees, 0f, 1f); return (0f, 1f - t, t); }
        float d = Down is null || DownDegrees > -1f ? 0f : Math.Clamp(pitchDegrees / DownDegrees, 0f, 1f);
        return (d, 1f - d, 0f);
    }
}

/// <summary>A movement direction of the Ork's relaxed (always ready) run: its angle against the facing (0 forward, +90 right, ±180 back) and its walk and run clips.</summary>
public sealed record OrkMoveDirection(float AngleDegrees, OrkPitchClip Walk, OrkPitchClip Run);

/// <summary>
/// The locomotion of one weapon family of the Ork: the four directions of the run (<c>BOY_2HR_NAV_*</c> for the shoota; the slugga uses the 1HM set, level only), and the
/// standing aim: the idle clip with the additive pitch offsets (<see cref="IdleDown"/> / <see cref="IdleUp"/>, laid over the idle by pitch, **ours**).
/// </summary>
public sealed record OrkMoveSet(HipFireWeaponClass Family, IReadOnlyList<OrkMoveDirection> Directions, string Idle, string? IdleDown, string? IdleUp);

/// <summary>
/// What the networked Ork plays on the ground (Ork O7a; docs/ork-faction-design.md section 5): both families' move sets, the one sprint clip, the grenade toss, and the
/// clips whose gait (<see cref="ClipGait"/>) the blend space needs. Engine-free: built from the Ork rig's hip-fire and locomotion data, so a test can feed it
/// synthetic data. The choices (which families, the idle for the pistol, the sprint for every weapon) are the design's, **ours** where the graphs give none.
/// </summary>
public sealed record OrkLocomotionPlan(OrkMoveSet? Gun, OrkMoveSet? Pistol, string? Sprint, float SprintBlend, string? Toss, IReadOnlyList<string> Notes)
{
    /// <summary>The move set for the weapon in hand: the pistol's for a pistol slot, else the gun's (either falls back to the other).</summary>
    public OrkMoveSet? For(bool pistol) => pistol ? Pistol ?? Gun : Gun ?? Pistol;

    /// <summary>Every looping clip the blend space measures: the walk and run levels of both sets, and the sprint.</summary>
    public IEnumerable<string> GaitClips()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in new[] { Gun, Pistol })
            foreach (var d in set?.Directions ?? [])
            {
                names.Add(d.Walk.Level);
                names.Add(d.Run.Level);
            }
        if (Sprint is not null) names.Add(Sprint);
        return names;
    }

    /// <summary>
    /// The plan from the Ork rig's data. <paramref name="inRig"/> says whether a clip is in the rig. A direction without a walk and a run clip is left out of its set (a set with fewer
    /// than three directions is dropped, with a note); the idle is the clip table's standing aim (<c>rifle:wos</c> / <c>pistol:wos</c>), else its first idle.
    /// </summary>
    public static OrkLocomotionPlan Build(HipFireAnimations hip, LocomotionAnimations loco, RigClipTable table, Func<string, bool> inRig)
    {
        var notes = new List<string>();
        OrkMoveSet? Set(HipFireWeaponClass family, string stanceKey)
        {
            if (hip.For(family) is not { } source) { notes.Add($"no {family} hip-fire data on the Ork rig; no move set"); return null; }
            var directions = new List<OrkMoveDirection>();
            foreach (var d in source.Directions)
            {
                var run = d.Run is { } r ? Pitch(r, inRig) : null;
                var walk = (d.Walk is { } w ? Pitch(w, inRig) : null) ?? run;
                if (run is null || walk is null) { notes.Add($"{family} {d.Label}: no usable walk and run clips; the direction is left out"); continue; }
                directions.Add(new OrkMoveDirection(d.AngleDegrees, walk, run));
            }
            if (directions.Count < 3) { notes.Add($"{family}: {directions.Count} usable directions; no move set"); return null; }
            string? idle = null, down = null, up = null;
            if (table.AimStances.TryGetValue(stanceKey, out var stance))
            {
                idle = stance.Level.FirstOrDefault(inRig);
                if (stance.AdditivePitch) { down = stance.Down.FirstOrDefault(inRig); up = stance.Up.FirstOrDefault(inRig); }
            }
            idle ??= table.IdleCandidates.FirstOrDefault(inRig);
            if (idle is null) { notes.Add($"{family}: no idle clip in the rig; no move set"); return null; }
            return new OrkMoveSet(family, directions.OrderBy(x => x.AngleDegrees).ToList(), idle, down, up);
        }
        var gun = Set(HipFireWeaponClass.Rifle, "rifle:wos");
        var pistol = Set(HipFireWeaponClass.Pistol, "pistol:wos");
        string? sprint = null;
        float sprintBlend = LocomotionAnimations.DefaultEnterBlend;
        if (loco.SprintFor(SprintFamily.Rifle) is { Clips.Count: > 0 } set && set.Clips.MinBy(c => MathF.Abs(c.AngleDegrees)) is { } forward && inRig(forward.Clip)) { sprint = forward.Clip; sprintBlend = set.EnterBlend; }
        else notes.Add("no sprint clip for the Ork; it runs while sprinting");
        string? toss = table.TossClip is { } t && inRig(t) ? t : null;
        if (toss is null) notes.Add("no toss clip for the Ork; throws show the movement pose");
        return new OrkLocomotionPlan(gun, pistol, sprint, sprintBlend, toss, notes);
    }

    /// <summary>A band's clips as a level clip with its down and up variants (the entries nearest the pitch extremes); null when the level clip is not in the rig.</summary>
    static OrkPitchClip? Pitch(AimSpeedBand band, Func<string, bool> inRig)
    {
        if (band.Clips.Count == 0) return null;
        var all = band.Clips.OrderBy(c => c.PitchDegrees).ToList();
        var level = all.MinBy(c => MathF.Abs(c.PitchDegrees))!;   // the level clip is the one nearest zero pitch; without it in the rig there is no band (a variant must not stand in for it)
        if (!inRig(level.Clip)) return null;
        var low = all[0]; var high = all[^1];
        bool hasLow = low.PitchDegrees < -1f && inRig(low.Clip), hasHigh = high.PitchDegrees > 1f && inRig(high.Clip);
        return new OrkPitchClip(level.Clip, hasLow ? low.Clip : null, hasLow ? low.PitchDegrees : 0f, hasHigh ? high.Clip : null, hasHigh ? high.PitchDegrees : 0f);
    }
}

/// <summary>The Ork locomotion data the client loads in one background task: the plan and the measured gaits of its clips.</summary>
public sealed record OrkLocomotionBundle(OrkLocomotionPlan Plan, GaitLibrary Gaits)
{
    /// <summary>The Ork's actions (reactions, reload, recoil, toss, death clips; Ork O7b / O7c), read from the same rig file; null when they could not be read.</summary>
    public OrkActionPlan? Actions { get; init; }
    /// <summary>Why <see cref="Actions"/> is null.</summary>
    public string? ActionsFailure { get; init; }
    /// <summary>What the worker spent (ms): the rig read, plan and gaits; the action plan; decoding the action clips (a measurement for the spec).</summary>
    public double LocomotionMilliseconds { get; init; }
    public double ActionsMilliseconds { get; init; }
    public double DecodeMilliseconds { get; init; }
    /// <summary>The action clips (<see cref="OrkActionPlan.Clips"/> and <see cref="OrkActions.SimClips"/>) decoded on the worker, by name.</summary>
    public IReadOnlyDictionary<string, HavokBoundAnimation> ActionAnimations { get; init; } = new Dictionary<string, HavokBoundAnimation>();
}

/// <summary>Reads the Ork's locomotion (Ork O7a). Read-only.</summary>
public static class OrkLocomotion
{
    /// <summary>Reads the Ork rig from the install, builds the plan and measures the gait of each of its clips. The gait part is optional (<see cref="GaitLibrary.Guard"/>).</summary>
    public static OrkLocomotionBundle Load(string gameDir)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var profile = RigProfiles.OrkBoy;
        var character = profile.LoadCharacter(gameDir);
        var names = new HashSet<string>(character.AnimationNames, StringComparer.OrdinalIgnoreCase);
        var hip = HipFireAnimations.From(character, profile);
        var loco = LocomotionAnimations.From(character, profile);
        var plan = OrkLocomotionPlan.Build(hip, loco, profile.Clips, names.Contains);
        var gaits = GaitLibrary.Guard(() => GaitLibrary.From(character, plan.GaitClips(), profile.Entry, profile.Feet));
        double locomotionMs = watch.Elapsed.TotalMilliseconds;   // the rig read, the plan and the gaits
        OrkActionPlan? actions = null;
        string? failure = null;
        try { actions = OrkActions.From(character, profile, hip); }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or ArgumentException or IndexOutOfRangeException or NotSupportedException)
        { failure = $"{e.GetType().Name}: {e.Message}"; }
        double actionsMs = watch.Elapsed.TotalMilliseconds - locomotionMs;
        // The action clips are decoded here too, off the main thread (the client's rig cache takes them: its first reaction or swing is not a decode hitch).
        var decoded = new Dictionary<string, HavokBoundAnimation>(StringComparer.OrdinalIgnoreCase);
        foreach (string clip in (actions?.Clips() ?? []).Concat(OrkActions.SimClips).Where(names.Contains).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { decoded[clip] = character.LoadAnimation(clip); }
            catch (Exception e) when (e is KeyNotFoundException or NotSupportedException or InvalidDataException) { /* the client decodes it itself and notes the failure */ }
        }
        return new OrkLocomotionBundle(plan, gaits)
        {
            Actions = actions, ActionsFailure = failure, ActionAnimations = decoded,
            LocomotionMilliseconds = locomotionMs, ActionsMilliseconds = actionsMs, DecodeMilliseconds = watch.Elapsed.TotalMilliseconds - locomotionMs - actionsMs,
        };
    }
}

/// <summary>
/// The Ork's look (Ork O7a; docs/ork-faction-design.md section 9, question 7): the wire carries 0..4 or <see cref="SmSim.Races.OrkLookAutomatic"/>; automatic is picked by player
/// id so every client draws the same one, from the first <see cref="AutomaticLooks"/> looks (the armoured boy is a choice, not a draw; **ours**).
/// </summary>
public static class OrkLookRules
{
    /// <summary>The look names in wire order (<c>boy_ranged</c> is 0).</summary>
    public static readonly IReadOnlyList<string> Names = ["boy_ranged", "boy_melee", "boy_ranged_pro", "boy_melee_pro", "boy_ardboy"];

    /// <summary>The character's look key of each look (<c>ork:boy_ranged</c> ...), made once so a frame does not build strings.</summary>
    public static readonly IReadOnlyList<string> Keys = [.. Names.Select(n => "ork:" + n)];

    /// <summary>How many of the looks automatic chooses from (the first four).</summary>
    public const int AutomaticLooks = 4;

    /// <summary>The look index to draw: the wire's own when it names one, else by player id (a negative or zero id counts as 0).</summary>
    public static int Resolve(byte wireLook, int playerId) =>
        wireLook < Names.Count ? wireLook : (int)((uint)Math.Max(playerId, 0) % AutomaticLooks);

    /// <summary>The look name to draw for a wire look and player id.</summary>
    public static string NameOf(byte wireLook, int playerId) => Names[Resolve(wireLook, playerId)];
}
