using System.Globalization;
using System.Text;
using SmContent;
using SmContent.Extras.Musou;

namespace SmTool;

/// <summary>
/// <c>smtool spear-rig-dump [--out dir] [--time s]</c> (authoring aid, musou/spear-authored 2026-11-12): writes the install's marine rig and the spear's base stance (the hammer's two-handed idle,
/// <see cref="ClawSpearClips.SpearIdle"/>, at <c>--time</c>, default 0; a bone the clip has no track for at its rest, as the overlay lays it) as <c>rig.json</c>, for Blender authoring
/// (<c>tools/blender/spear_hew_author.py</c>). **Private**: the file holds the install's skeleton data, so it goes to the private authoring workspace (default
/// <c>%LOCALAPPDATA%\sm-reimpl\spear-authoring</c>) and is refused inside any source checkout or the install (<see cref="PrivateOutputGuard"/>); nothing of it is ever committed.
/// Units: metres; skeleton space (Z up, the rig's own axes); rotations as quaternions x, y, z, w; local transforms (translation, rotation, scale) as the Havok pose stores them.
/// </summary>
static class SpearRigDumpCommand
{
    public static int Run(string[] rest)
    {
        string outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sm-reimpl", "spear-authoring");
        float time = 0f;
        for (int i = 0; i < rest.Length; i++)
        {
            if (rest[i] == "--out" && i + 1 < rest.Length) outDir = Path.GetFullPath(rest[++i]);
            else if (rest[i] == "--time" && i + 1 < rest.Length) time = float.Parse(rest[++i], CultureInfo.InvariantCulture);
            else { Console.Error.WriteLine($"spear-rig-dump: unknown argument \"{rest[i]}\" (usage: spear-rig-dump [--out dir] [--time s])"); return 2; }
        }
        var game = GameInstall.TryLocate();
        if (game is null) { Console.Error.WriteLine("spear-rig-dump: the Space Marine install was not found"); return 1; }
        if (PrivateOutputGuard.Refuses(outDir, game.Dir) is { } why)
        { Console.Error.WriteLine($"spear-rig-dump: the output holds the install's skeleton data and is refused: {why} (use the private authoring folder)"); return 2; }
        var rig = CharacterModel.LoadMarineRig(game.Dir);
        var sk = rig.Skeleton;
        var idle = rig.LoadAnimation(ClawSpearClips.SpearIdle);
        int n = sk.BoneNames.Count;
        var sample = idle.Animation.Sample(time);
        var tracked = new bool[n];
        var idlePose = new (System.Numerics.Vector3 T, System.Numerics.Quaternion R, System.Numerics.Vector3 S)[n];
        for (int b = 0; b < n; b++) { var r = sk.ReferencePose[b]; idlePose[b] = (r.Translation, System.Numerics.Quaternion.Normalize(r.Rotation), r.Scale); }
        for (int t = 0; t < sample.Length; t++)
        {
            int b = idle.TrackToBone[t];
            if ((uint)b >= (uint)n) continue;
            idlePose[b] = (sample[t].Translation, System.Numerics.Quaternion.Normalize(sample[t].Rotation), sample[t].Scale);
            tracked[b] = true;
        }
        var sb = new StringBuilder();
        string F(float v) => v.ToString("0.#######", CultureInfo.InvariantCulture);
        sb.Append("{\n  \"format\": \"sm-spear-rig-dump/1\",\n  \"units\": \"metres\",\n  \"space\": \"skeleton space (Z up), local transforms as stored by the Havok pose; rotations x,y,z,w\",\n");
        sb.Append($"  \"stance_clip\": \"{ClawSpearClips.SpearIdle}\",\n  \"stance_time\": {F(time)},\n  \"grip_sockets\": [\"{SpearHold.RightGripBone}\", \"{SpearHold.LeftGripBone}\"],\n  \"bones\": [\n");
        for (int b = 0; b < n; b++)
        {
            var r = sk.ReferencePose[b]; var p = idlePose[b];
            sb.Append($"    {{\"name\": \"{sk.BoneNames[b]}\", \"parent\": {sk.Parents[b]}, \"tracked\": {(tracked[b] ? "true" : "false")}, ");
            sb.Append($"\"bind\": [{F(r.Translation.X)}, {F(r.Translation.Y)}, {F(r.Translation.Z)}, {F(r.Rotation.X)}, {F(r.Rotation.Y)}, {F(r.Rotation.Z)}, {F(r.Rotation.W)}, {F(r.Scale.X)}, {F(r.Scale.Y)}, {F(r.Scale.Z)}], ");
            sb.Append($"\"stance\": [{F(p.T.X)}, {F(p.T.Y)}, {F(p.T.Z)}, {F(p.R.X)}, {F(p.R.Y)}, {F(p.R.Z)}, {F(p.R.W)}, {F(p.S.X)}, {F(p.S.Y)}, {F(p.S.Z)}]}}{(b + 1 < n ? "," : "")}\n");
        }
        sb.Append("  ]\n}\n");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "rig.json"), sb.ToString());
        Console.WriteLine($"spear-rig-dump: {n} bones, stance {ClawSpearClips.SpearIdle} at {time:0.###} s -> rig.json (private; not for the repository)");
        return 0;
    }
}
