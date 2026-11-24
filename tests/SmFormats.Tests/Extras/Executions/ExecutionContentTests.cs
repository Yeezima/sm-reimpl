using System.Numerics;
using SmContent;
using SmContent.Extras.Executions;
using SmFormats.Havok;
using SmSim;
using SmSim.Extras.Executions;
using Xunit.Abstractions;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests.Extras.Executions;

/// <summary>
/// Executions X2: the clip reader (<see cref="ExecutionClipReader"/>), the posed hit volumes (<see cref="ExecutionHitVolumes"/>) on synthetic Havok files, and an
/// install-only load of real paired clips (skipped without an install; nothing of the install is kept or written).
/// </summary>
public class ExecutionContentTests(ITestOutputHelper output)
{
    static readonly Bone[] Bones = [new("RIG_Root", -1, Vector3.Zero, Quaternion.Identity), new("RIG_Head", 0, new Vector3(0f, 0f, 1.7f), Quaternion.Identity)];

    static HavokCharacterFile Rig() => new(Build(Bones,
    [
        // Extracted motion: 1 m forward over 2 s.
        new Clip("SM_SYNC_Attack", 2f, 61, [new(Vector3.Zero, Quaternion.Identity), new(new Vector3(0f, 0f, 1.7f), Quaternion.Identity)],
            [new(0, 0, 0, 0), new(0, 0.5f, 0, 0), new(0, 1f, 0, 0.25f)]),
        // No extracted motion: the root bone moves 0 .. 0.8 m along X.
        new Clip("SM_SYNC_Victim", 1.5f, 46, [new(Vector3.Zero, Quaternion.Identity, (0f, 0.8f)), new(new Vector3(0f, 0f, 1.7f), Quaternion.Identity)]),
        new Clip("SM_Idle", 1f, 2, [new(Vector3.Zero, Quaternion.Identity), new(new Vector3(0f, 0f, 1.7f), Quaternion.Identity)]),
    ]));

    static IReadOnlyList<BehaviorGraph> Graphs() => HavokBehavior.Read(new HavokPackfile(BuildBehavior(
    [
        new Graph("SM_Base_MP", ["SYNC_Contact", "ACTN_Kill_Victim", "BRANCH_End", "SYNC_Left_Foot"],
        [
            new Sequencer("Sync Kill Chainsword", ["SM_SYNC_Attack"],
                [new Tag("Event", 0.25f, 0.25f, 0), new Tag("Event", 0.6f, 0.6f, 1), new Tag("Branch", 0.9f, 1f, 2), new Tag("Event", 0.1f, 0.1f, 3)], PlaybackSpeed: 2f),
            new Sequencer("Other", ["SM_Idle"], [new Tag("Event", 0.5f, 0.5f, 0)]),
        ]),
    ])));

    [Fact]
    public void The_reader_gets_length_root_path_and_sequencer_events()
    {
        var rig = Rig();
        var a = ExecutionClipReader.Read(rig, "SM_SYNC_Attack", Graphs());
        Assert.Equal((2f, 61, "extracted motion"), (a.Seconds, a.Frames, a.RootSource));
        Assert.Equal(new Vector4(0, 1f, 0, 0.25f), a.Root[^1]);
        var use = Assert.Single(a.Uses);
        Assert.Equal(("SM_Base_MP", "Sync Kill Chainsword", 2f, 1f), (use.Graph, use.Sequencer, use.PlaybackSpeed, use.Seconds));   // 2 s at speed 2
        Assert.Equal(["SYNC_Left_Foot", "SYNC_Contact", "ACTN_Kill_Victim", "BRANCH_End"], use.Events.Select(e => e.Name));
        Assert.Equal(0.25f, use.Events[1].Start, 4);
        Assert.Equal(0.6f, use.Events[2].Start, 4);
        Assert.Equal("Branch", use.Events[3].Tag);

        var v = ExecutionClipReader.Read(rig, "SM_SYNC_Victim", Graphs());
        Assert.Equal("root bone", v.RootSource);
        Assert.Equal(46, v.Root.Count);
        Assert.Equal(Vector4.Zero, v.Root[0]);
        Assert.Equal(0.8f, v.Root[^1].X, 2);
        Assert.Empty(v.Uses);
        Assert.Throws<KeyNotFoundException>(() => ExecutionClipReader.Read(rig, "Missing", Graphs()));
    }

    [Fact]
    public void Guess_reads_names_and_fill_takes_lengths_and_paths()
    {
        Assert.Equal(ExecutionEventKind.Kill, ExecutionClipReader.Guess("ACTN_Kill_Victim"));
        Assert.Equal(ExecutionEventKind.Contact, ExecutionClipReader.Guess("ACTN_Hit_Primary_Target"));
        Assert.Equal(ExecutionEventKind.Sync, ExecutionClipReader.Guess("SYNC_Contact"));
        Assert.Equal(ExecutionEventKind.Other, ExecutionClipReader.Guess("SYNC_Left_Foot"));
        Assert.Equal(ExecutionEventKind.End, ExecutionClipReader.Guess("BRANCH_End"));
        Assert.Equal(ExecutionEventKind.Other, ExecutionClipReader.Guess("Input_Window_Open"));
        var rig = Rig();
        var a = ExecutionClipReader.Read(rig, "SM_SYNC_Attack", Graphs());
        var v = ExecutionClipReader.Read(rig, "SM_SYNC_Victim", Graphs());
        var row = new ExecutionRow("cs", "*", ExecutionWeapons.Chainsword, ExecutionTargets.ChaosMarine, a.Clip, v.Clip, 0f, 0f, [], new Vector3(0, 1.2f, 0), MathF.PI, "SM_Base_MP", "Sync Kill Chainsword");
        Assert.False(row.IsValid);
        var filled = ExecutionClipReader.Fill(row, a, v, [(a with { Clip = "SM_SYNC_Attack_3.5m" }, null), (v, 7f)]);
        Assert.True(filled.IsValid);
        Assert.Equal((1f, 1.5f), (filled.AttackerSeconds, filled.VictimSeconds));   // the attacker's sequencer plays it at speed 2
        Assert.Equal(a.Root, filled.AttackerRoot);
        Assert.Equal(v.Root, filled.VictimRoot);
        Assert.Equal([3.5f, 7f], filled.AttackerVariants.Select(x => x.Distance));   // from the name, else as given
        Assert.Equal((3.5f, 7f), filled.DistanceBand);
        Assert.Equal(ExecutionEventKind.Kill, ExecutionClipReader.Guess("BRANCH_Boy_Dead"));
        Assert.Equal(0f, ExecutionClipReader.Yaw(Quaternion.Identity), 5);
        Assert.Equal(MathF.PI / 2f, ExecutionClipReader.Yaw(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f)), 4);
    }

    static PosedHitVolumes Posed(HavokCharacterFile character)
    {
        var body = new RagdollModelBody("Ragdoll_RIG_Head", "RIG_Root", -1, Vector3.Zero, Quaternion.Identity, [], null!);
        var ragdoll = new RagdollModel([body], [], [], [], [], ["RIG_Root", "RIG_Head"]);
        var notes = new List<string>();
        return PosedHitVolumes.From([HitVolume.Sphere("Head", HitZone.Head, new Vector3(0f, 0f, 1.7f), 0.2f)], ragdoll, character, notes)
            ?? throw new InvalidOperationException(string.Join("; ", notes));
    }

    [Fact]
    public void Hit_volumes_follow_the_clip_tick_by_tick()
    {
        var rig = Posed(Rig());
        var notes = new List<string>();
        var poses = ExecutionHitVolumes.Build(rig, ["SM_SYNC_Victim", "sm_sync_victim", "Missing"], notes);
        var p = Assert.Single(poses);
        Assert.True(p.IsValid);
        Assert.Equal(46, p.Sets.Count);   // 1.5 s at 30 Hz, both ends
        Assert.Equal(0f, p.At(0f)![0].A.X, 3);
        Assert.Equal(0.8f, p.At(1.5f)![0].A.X, 2);
        Assert.Equal(0.4f, p.At(0.75f)![0].A.X, 1);
        Assert.Same(p.At(99f), p.Sets[^1]);
        Assert.Single(notes);   // the missing clip
        Assert.Contains("Missing", notes[0]);
    }

    // ---------------------------------------------------------------- install only

    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (reads the player's install; never in CI)";
        }
    }

    /// <summary>The rigs that hold the paired clips (docs/executions-coverage.md: the campaign player rig in the shared player set and the victims' rigs).</summary>
    static readonly RigProfile[] PairRigs =
    [
        new("sm_campaign", "data/chunks/ebp_sets__player_set.oppc", "havok_spacemarine__hkxx", ""),
        new("ork_boy", "data/chunks/ebp_sets__ork_core_units.oppc", "havok_boy__hkxx", ""),
        new("gretchin", "data/chunks/ebp_sets__ork_core_units.oppc", "havok_gretchin__hkxx", ""),
        new("chaos_marine", "data/chunks/ebp_sets__chaos_core_units.oppc", "havok_chaosspacemarine__hkxx", ""),
    ];

    [InstallFact]
    public void Real_paired_clips_read_from_the_install()
    {
        int read = 0;
        var byName = new Dictionary<string, ExecutionClip>(StringComparer.OrdinalIgnoreCase);
        HavokCharacterFile? campaign = null;
        foreach (var profile in PairRigs)
        {
            var character = profile.LoadCharacter(Game.Value!);
            if (profile.Name == "sm_campaign") campaign = character;
            var graphs = HavokBehavior.Read(character.Pack);
            var clips = character.AnimationNames.Where(n => n.Contains("_SYN_", StringComparison.OrdinalIgnoreCase) || n.Contains("_SYNC_", StringComparison.OrdinalIgnoreCase)
                || n.Contains("CK_XATK_HVY", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList();
            output.WriteLine($"{profile.Name}: {character.AnimationNames.Count} clips, {clips.Count} paired-looking");
            foreach (var name in clips)
            {
                ExecutionClip clip;
                try { clip = ExecutionClipReader.Read(character, name, graphs); }
                catch (Exception e) when (e is NotSupportedException or InvalidDataException) { output.WriteLine($"  {name}: not read ({e.Message})"); continue; }
                Assert.True(clip.Seconds > 0f && float.IsFinite(clip.Seconds), name);
                Assert.All(clip.Root, r => Assert.True(float.IsFinite(r.X) && float.IsFinite(r.Y) && float.IsFinite(r.Z) && float.IsFinite(r.W)));
                var end = clip.Root.Count > 0 ? clip.Root[^1] : Vector4.Zero;
                output.WriteLine($"  {name}: {clip.Seconds:F3} s, {clip.Frames} frames, root {clip.RootSource} end ({end.X:F2}, {end.Y:F2}, {end.Z:F2}, {end.W:F2}); " +
                    $"{clip.Uses.Count} sequencer(s){(clip.Uses.Count > 0 ? $": {clip.Uses[0].Graph}/{clip.Uses[0].Sequencer} [{string.Join(", ", clip.Uses[0].Events.Select(e => $"{e.Name}@{e.Start:F2}"))}]" : "")}");
                byName[name] = clip;
                read++;
            }
        }
        Assert.True(read > 0, "no paired-looking clip read from the install");
        // The inventory's Ork Slam (O3): both halves 3.667 s, the attacker's 3.5 m variant carried far forward, a blendable 1 / 3.5 / 7 m set.
        Assert.Equal(3.667f, byName["SM_SYN_ATT_F_ORKSLAM_3.5M"].Seconds, 2);
        Assert.Equal(3.667f, byName["BOY_SYN_ATT_F_ORKSLAM_3.5M"].Seconds, 2);
        Assert.True(byName["SM_SYN_ATT_F_ORKSLAM_3.5M"].Root[^1].Y > 5f, "the slam's root path");
        var row = ExecutionClipReader.Fill(
            new ExecutionRow("O3", "*", ExecutionWeapons.Chainsword, ExecutionTargets.OrkBoy, "SM_SYN_ATT_F_ORKSLAM_3.5M", "BOY_SYN_ATT_F_ORKSLAM_3.5M", 0f, 0f, [],
                new Vector3(0.07f, 3.50f, 0f), MathF.PI, "SM_MEL_ChainSword", ""),
            byName["SM_SYN_ATT_F_ORKSLAM_3.5M"], byName["BOY_SYN_ATT_F_ORKSLAM_3.5M"],
            [(byName["SM_SYN_ATT_F_ORKSLAM_1M"], null), (byName["SM_SYN_ATT_F_ORKSLAM_3.5M"], null), (byName["SM_SYN_ATT_F_ORKSLAM_7M"], null)]);
        Assert.True(row.IsValid);
        Assert.Equal((1f, 7f), row.DistanceBand);
        output.WriteLine($"O3 blended at 2 m: root end {row.AttackerRootAt(2f)[^1]}");
        // The MP marine's hit volumes posed by a campaign clip (the same 50-bone skeleton: inventory 1.4).
        var notes = new List<string>();
        var rest = HitVolumeTuning.Load(Game.Value!, RigProfiles.Marine, notes);
        var ragdoll = RagdollModel.Load(Game.Value!, RigProfiles.Marine, notes);
        Assert.NotNull(rest); Assert.NotNull(ragdoll);
        var posed = PosedHitVolumes.From(rest!, ragdoll!, campaign!, notes);
        Assert.True(posed is not null, string.Join("; ", notes));
        var pose = Assert.Single(ExecutionHitVolumes.Build(posed!, ["SM_SYN_ATT_F_ORKSLAM_3.5M"], notes));
        Assert.True(pose.IsValid);
        output.WriteLine($"O3 posed hit volumes: {pose.Sets.Count} sets of {pose.Sets[0].Count}; notes: {string.Join("; ", notes)}");
    }
}
