using System.Text.RegularExpressions;
using SmSim;

namespace SmContent;

/// <summary>
/// fix/hitboxes (protocol 37): the server's jump hit volumes (<see cref="SimConfig.FlightHitVolumes"/>): every flight clip the client poses
/// (<c>SM_JMP_Zero[Lo]_&lt;F|B|L|R|U&gt;_Aim_&lt;U|F|D&gt;</c>, thunder-hammer variants left out, the shortest decodable name per pair, in the client's order:
/// <c>NetworkView.Jump.SetUpJump</c>) at <see cref="Steps"/> fractions of its length, and the take-off clip (<see cref="FlightPoseRules.TakeOffClip"/>) at
/// every tick until <see cref="FlightPoseRules.TakeOffBranch"/>, the rig's volumes carried by their bones (<see cref="PosedHitVolumes"/>). That the ragdoll
/// follows the animated pose for hits is an <b>interpretation</b> (the original's hit test is in its executable); the clips, bones and ragdoll are the data's
/// (<b>supported by evidence</b>); the fraction steps are ours.
/// </summary>
public static class FlightHitVolumes
{
    /// <summary>How many fractions of each flight clip are posed (0, 1/8, … 1; ours: the drawn pose is at most 1/16 of the clip from the nearest).</summary>
    public const int Steps = 9;

    static readonly Regex FlightClip = new(@"^SM_JMP_Zero(Lo)?_.*?([FBLRU])_Aim_([UFD])(_\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The jump poses from the rig, or null (with a note) when it has no flight clip or every one is unusable.</summary>
    public static FlightVolumes? Build(PosedHitVolumes rig, List<string> notes, float tickSeconds = 1f / 30f)
    {
        var poses = new List<FlightPose>();
        var taken = new HashSet<(bool, char, char)>();
        foreach (var name in rig.Character.AnimationNames.Where(n => !n.Contains("_TH_", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.Length))
        {
            var m = FlightClip.Match(name);
            if (!m.Success) continue;
            bool low = m.Groups[1].Success;
            char dir = char.ToUpperInvariant(m.Groups[2].Value[0]), aim = char.ToUpperInvariant(m.Groups[3].Value[0]);
            if (taken.Contains((low, dir, aim))) continue;
            // As the client: a clip that does not decode is left out and the pair stays open for a longer name.
            if (Sets(rig, name, Enumerable.Range(0, Steps).Select(i => i / (float)(Steps - 1)), seconds: false, notes) is not { } sets) continue;
            taken.Add((low, dir, aim));
            poses.Add(new FlightPose(name, dir, aim, low, sets));
        }
        if (poses.Count == 0) { notes.Add("flight hit volumes: no flight clip in the rig; the rest volumes stay in flight"); return null; }
        // The take-off clip as the client resolves it (exact name, else the shortest with it as a prefix followed by no letter), each tick to the branch.
        var names = rig.Character.AnimationNames;
        string? takeOff = names.FirstOrDefault(n => n.Equals(FlightPoseRules.TakeOffClip, StringComparison.OrdinalIgnoreCase))
            ?? names.Where(n => n.StartsWith(FlightPoseRules.TakeOffClip, StringComparison.OrdinalIgnoreCase) && n.Length > FlightPoseRules.TakeOffClip.Length
                                && !char.IsLetter(n[FlightPoseRules.TakeOffClip.Length])).OrderBy(n => n.Length).FirstOrDefault();
        int ticks = (int)MathF.Ceiling(FlightPoseRules.TakeOffBranch / tickSeconds - 1e-4f);
        var takeOffSets = takeOff is null ? null : Sets(rig, takeOff, Enumerable.Range(0, ticks + 1).Select(k => k * tickSeconds), seconds: true, notes);
        if (takeOffSets is null) notes.Add($"flight hit volumes: no usable take-off clip '{FlightPoseRules.TakeOffClip}'; the rest volumes during the take-off");
        var flight = new FlightVolumes(poses) { TakeOff = takeOffSets ?? [], TakeOffClip = takeOffSets is null ? null : takeOff };
        if (!flight.IsValid) { notes.Add("flight hit volumes: unusable; the rest volumes stay in flight"); return null; }
        return flight;
    }

    static List<IReadOnlyList<HitVolume>>? Sets(PosedHitVolumes rig, string clip, IEnumerable<float> times, bool seconds, List<string> notes)
    {
        var sets = new List<IReadOnlyList<HitVolume>>();
        try
        {
            foreach (float t in times) sets.Add(rig.Pose(rig.ClipPoseAt(clip, t, seconds)));   // the rest volumes' order (head first)
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or KeyNotFoundException or ArgumentException)
        {
            notes.Add($"flight hit volumes: '{clip}' not decoded ({e.Message}); left out");
            return null;
        }
        if (!sets.All(s => s.All(v => v.IsValid))) { notes.Add($"flight hit volumes: a pose of '{clip}' is unusable; left out"); return null; }
        return sets;
    }

    /// <summary>The jump poses from the install's marine rig (read-only), or null with the reason in <paramref name="notes"/>.</summary>
    public static FlightVolumes? Load(string gameDir, List<string> notes) =>
        PosedHitVolumes.Load(gameDir, notes) is { } rig ? Build(rig, notes) : null;

    /// <summary>As <see cref="Load(string, List{string})"/> for rest volumes already read (<see cref="HitVolumeTuning.Load(string, List{string})"/>).</summary>
    public static FlightVolumes? Load(string gameDir, IReadOnlyList<HitVolume> rest, List<string> notes, float tickSeconds = 1f / 30f) =>
        PosedHitVolumes.Load(gameDir, RigProfiles.Marine, notes, rest) is { } rig ? Build(rig, notes, tickSeconds) : null;
}
