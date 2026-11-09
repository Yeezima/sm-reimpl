using System.Numerics;
using SmFormats.Dcm;

namespace SmContent.Extras.Musou;

/// <summary>
/// Musou Mode's Warboss on his own rig (owner, 2026-11-06: "the final boss should use a warboss model"): the campaign's <c>warboss_regular</c> body on <c>havok_warboss_regular__hkxx</c> and
/// the head of the campaign's <c>cinematic_warboss</c>, read from the campaign set <c>ebp_sets__sequence2_core_units</c> of the player's install (a singleplayer dependency,
/// docs/discovery.md "Warboss model"). The rig, meshes and clips are original assets; which clip plays for the crowd's run, idle, attack, stagger and death is **ours** (picked by name; the
/// graphs are not read), and so is the head's binding (<see cref="HeadBone"/>).
/// </summary>
public static class WarbossRig
{
    public const string Archive = "data/chunks/ebp_sets__sequence2_core_units.oppc", Entry = "havok_warboss_regular__hkxx", BodyVisual = "warboss_regular";

    /// <summary>
    /// The head: the body mesh has none (one block, <c>GEO_WarBoss_Body</c>), and the install has no <c>warboss_head_regular</c> mesh (only its textures and material). The only Warboss head
    /// mesh is <c>cinematic_warboss</c> (blocks <c>GEO_WarBoss_Head_LODH</c> and <c>GEO_WarBoss_Gob_LODH</c>, material <c>WarBoss_Head_regular</c>), skinned to the cutscene's face rig
    /// (<c>RIG_headX</c>, <c>RIG_jawX</c>, brows, lips, cheeks, tongue; 41 bones the gameplay rig lacks) in the same model space as the body (its <c>RIG_NeckA</c>, <c>RIG_SpineD</c> and
    /// <c>RIG_headX</c> binds sit where the gameplay rig's <c>RIG_Neck</c>, <c>RIG_Chest</c> and <c>RIG_Head</c> stand, *supported by evidence*: the decoded binds, 2026-11-06).
    /// </summary>
    public const string Head = "cinematic_warboss";

    /// <summary>
    /// The gameplay rig's bone a face-rig bone of the head follows (**ours**): the neck and spine bones their twins, the jaw, chin, lower lips and tongue the jaw, everything else (the head,
    /// brows, eyes, cheeks, ears, upper lips, the bobbles) the head. The face is posed as modelled and never animates (the gameplay clips carry no face). A bone the rig has keeps its own name.
    /// </summary>
    public static string HeadBone(string bone, IReadOnlySet<string> rigBones)
    {
        if (rigBones.Contains(bone)) return bone;
        string b = bone.ToLowerInvariant();
        if (b.Contains("neck")) return "RIG_Neck";
        if (b.Contains("spine")) return "RIG_Chest";
        if (b.Contains("jaw") || b.Contains("chin") || b.Contains("lowerlip") || b.Contains("tongue")) return "RIG_Jaw";
        return "RIG_Head";
    }

    /// <summary>
    /// The head's mesh with every skinned block's bones bound to the gameplay rig (<see cref="HeadBone"/>): a face bone takes its target's inverse bind from the rig's reference pose
    /// (<paramref name="referenceModel"/>, model space, row-vector), so the head stays where it was modelled at that pose and follows its target bone after. Targets the rig lacks are left
    /// as they were. Nothing is written: the blocks are changed in memory only.
    /// </summary>
    public static DcmMesh BindHead(DcmMesh mesh, IReadOnlyList<string> boneNames, IReadOnlyList<Matrix4x4> referenceModel, out int rebound)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < boneNames.Count; i++) index.TryAdd(boneNames[i], i);
        var set = new HashSet<string>(boneNames, StringComparer.Ordinal);
        int count = 0;
        var blocks = new List<DcmBlock>(mesh.Blocks.Count);
        foreach (var block in mesh.Blocks)
        {
            if (block.Skin is not { } skin) { blocks.Add(block); continue; }
            var bones = new List<DcmBone>(skin.Bones.Count);
            foreach (var bone in skin.Bones)
            {
                string target = HeadBone(bone.Name, set);
                if (target == bone.Name || !index.TryGetValue(target, out int t) || !Matrix4x4.Invert(referenceModel[t], out var inverse)) { bones.Add(bone); continue; }
                bones.Add(bone with { Name = target, InverseBind = inverse });
                count++;
            }
            blocks.Add(block with { Skin = skin with { Bones = bones } });
        }
        rebound = count;
        return DcmMesh.FromBlocks(blocks);
    }

    /// <summary>
    /// The clips by the crowd's five jobs: candidates in order, then a part of a name to fall back on. The rig's 48 clips (<c>smtool graph-dump --animations --archive
    /// data/chunks/ebp_sets__sequence2_core_units.oppc --entry havok_warboss_regular__hkxx</c>) have walks but no run, no standing idle and no death of the usual names (his campaign fight is
    /// scripted), so the picks are **ours**: the forward walk for the run (played at the crowd's rate), the floor idle, the shove for a strike (the slam and charge too: the crowd has one attack
    /// clip), the charge's wall-hit exit for a stagger (he never staggers) and the explosion reaction for the death.
    /// </summary>
    public static readonly (string[] Names, string Contains) Run = (["WB_Nav_Walk_F"], "Walk_F"),
        Idle = (["WB_Nav_Floor_Idle", "WB_Nav_Death_Idle"], "Idle"),
        Attack = (["WB_Att_Shove", "WB_Att_Ground_Pound"], "_Att_"),
        Stagger = (["WB_Att_Charge_wall_hit_exit", "WB_Get_Up"], "_REC_"),
        Death = (["WB_Get_Up"], "Get_Up"),
        /// <summary>The death before 2026-11-09 (<c>--musou-old-big-orks</c>): the blast reaction, which stands him up again.</summary>
        OldDeath = (["WB_REC_Explode"], "Death");

    /// <summary>
    /// The death clip is played backwards (musou-big-ork-deaths, the owner 2026-11-09: "the elite and boss orks and the warboss need to have death animations"; ours): the rig's only clip that ends with him
    /// lying is the get-up, which starts on his back; run in reverse he drops to his knees and falls on his back, then lies at its first frame.
    /// </summary>
    public const bool DeathReversed = true;

    /// <summary>The rig as a profile: only the data sits here; no marine or Ork graph map fits it, so nothing may read those through this profile.</summary>
    public static readonly RigProfile Profile = new("warboss", Archive, Entry, BodyVisual);
}
