using SmFormats.Havok;
using SmFormats.Obpk;
using SmSim;

namespace SmContent;

/// <summary>
/// One character rig and where its data sit (Ork O2, docs/ork-faction-design.md sections 0.6, 1.4, 1.5, 4, 5): the Havok
/// character packfile (<see cref="Archive"/> / <see cref="Entry"/>), the character visual whose object tree holds the stow markers
/// (<see cref="BodyVisual"/>), and the behaviour-graph names each content reader looks for (its maps). Every reader that used to
/// open the marine rig directly has an overload taking a profile; the marine overloads pass <see cref="RigProfiles.Marine"/>, whose
/// maps are the readers' own defaults, so their output is unchanged. Read-only; nothing is written.
/// </summary>
/// <param name="Name">A short label ("marine", "ork").</param>
/// <param name="Archive">The rig's archive, relative to the game folder (e.g. <c>data/chunks/ebp_sets__mp_versus_set.oppc</c>).</param>
/// <param name="Entry">The Havok character packfile in it (e.g. <c>havok_sm_base_mp__hkxx</c>).</param>
/// <param name="BodyVisual">The character visual (<c>renderable_ext/visual</c> of the player blueprint) whose <c>.O3d</c> is in <paramref name="Archive"/>.</param>
public sealed record RigProfile(string Name, string Archive, string Entry, string BodyVisual)
{
    /// <summary>The base graph holding the weapon state machines (hip fire's blends, the sprint entry blend).</summary>
    public string BaseGraph { get; init; } = HipFireAnimations.BaseGraph;
    /// <summary>Relaxed (or, without one, the always-ready) run per weapon family (<see cref="LocomotionAnimations"/>).</summary>
    public IReadOnlyList<LocomotionAnimations.RelaxedMap> Relaxed { get; init; } = LocomotionAnimations.DefaultRelaxed;
    /// <summary>Sprint loops (<see cref="LocomotionAnimations"/>).</summary>
    public IReadOnlyList<LocomotionAnimations.SprintMap> Sprints { get; init; } = LocomotionAnimations.DefaultSprints;
    /// <summary>Hip-fire families (<see cref="HipFireAnimations"/>).</summary>
    public IReadOnlyList<HipFireAnimations.ClassMap> HipFire { get; init; } = HipFireAnimations.DefaultMaps;
    /// <summary>Reload clips (<see cref="ReloadAnimations"/>).</summary>
    public IReadOnlyList<ReloadAnimations.Map> Reloads { get; init; } = ReloadAnimations.DefaultMaps;
    /// <summary>The evade's graph and clip sets (<see cref="EvadeTuning"/>).</summary>
    public EvadeTuning.RigMap Evade { get; init; } = EvadeTuning.MarineRig;
    /// <summary>The reaction graph and clip sets (<see cref="ReactionTuning"/>).</summary>
    public ReactionTuning.RigMap Reactions { get; init; } = ReactionTuning.MarineRig;
    /// <summary>Melee weapons whose combos the rig's graphs hold; the first is the default of <see cref="MeleeTuning.Load(string, RigProfile, MeleeTuning.WeaponMap?)"/>.</summary>
    public IReadOnlyList<MeleeTuning.WeaponMap> Melee { get; init; } = [MeleeTuning.CombatKnife];
    /// <summary>
    /// Wargear whose models <see cref="WeaponModels.Load(string, RigProfile, List{string}?, IEnumerable{string}?, IReadOnlyList{string}?)"/>
    /// reads when no names are given; empty: every base and startup wargear of the multiplayer classes (the marine).
    /// </summary>
    public IReadOnlyList<string> DefaultWargear { get; init; } = [];
    /// <summary>
    /// Mounts of wargear whose files name no handle or body nodes, by wargear name (case ignored; <see cref="WeaponModels.Build"/>). Empty (the marine):
    /// every weapon's wargear names its nodes. The Ork's: <see cref="OrkWeaponMounts.Table"/> (Ork O6).
    /// </summary>
    public IReadOnlyDictionary<string, RigWeaponMount> WeaponMounts { get; init; } = new Dictionary<string, RigWeaponMount>();
    /// <summary>The clip names the client picks by name (idle, aim stances, throw, recoil, default, death).</summary>
    public RigClipTable Clips { get; init; } = RigClipTable.Marine;
    /// <summary>
    /// Wargear → animation families on this rig (case ignored). Empty (the marine): the readers' name rules
    /// (<see cref="HipFireAnimations.FamilyOf"/>, <see cref="ReloadAnimations.FamilyOf"/>, <see cref="LocomotionAnimations.SprintFamilyOf"/>).
    /// </summary>
    public IReadOnlyDictionary<string, WeaponFamilies> WargearFamilies { get; init; } = new Dictionary<string, WeaponFamilies>();
    /// <summary>
    /// Loadout slot → animation families for wargear not in <see cref="WargearFamilies"/>; empty (the marine): the name rules. The Ork's slugga
    /// says slot "ranged" in its wargear, so the slot here is the loadout's (<see cref="WeaponSlot"/>), not the file's.
    /// </summary>
    public IReadOnlyDictionary<WeaponSlot, WeaponFamilies> SlotFamilies { get; init; } = new Dictionary<WeaponSlot, WeaponFamilies>();

    /// <summary>The families of the slot on this rig, or null when the rig has no slot table (the marine: use the name rules).</summary>
    public WeaponFamilies? FamilyForSlot(WeaponSlot slot) => SlotFamilies.TryGetValue(slot, out var f) ? f : null;

    /// <summary>
    /// The families of a weapon on this rig: its <see cref="WargearFamilies"/> entry, else its <paramref name="slot"/>'s
    /// (<see cref="FamilyForSlot"/>), else the marine name rules (so the marine profile gives exactly what the readers' <c>FamilyOf</c> give).
    /// </summary>
    public WeaponFamilies FamiliesOf(string wargear, WeaponSlot? slot = null) =>
        WargearFamilies.TryGetValue(wargear, out var f) ? f
        : slot is { } s && FamilyForSlot(s) is { } bySlot ? bySlot
        : new WeaponFamilies(HipFireAnimations.FamilyOf(wargear), ReloadAnimations.FamilyOf(wargear), LocomotionAnimations.SprintFamilyOf(wargear));

    /// <summary>The hip-fire family of a weapon on this rig (<see cref="FamiliesOf"/>).</summary>
    public HipFireWeaponClass HipFireFamilyOf(string wargear, WeaponSlot? slot = null) => FamiliesOf(wargear, slot).HipFire;
    /// <summary>The reload family of a weapon on this rig, null without a reload clip (<see cref="FamiliesOf"/>).</summary>
    public ReloadFamily? ReloadFamilyOf(string wargear, WeaponSlot? slot = null) => FamiliesOf(wargear, slot).Reload;
    /// <summary>The sprint family of a weapon on this rig (<see cref="FamiliesOf"/>).</summary>
    public SprintFamily SprintFamilyOf(string wargear, WeaponSlot? slot = null) => FamiliesOf(wargear, slot).Sprint;

    /// <summary>The toe and ankle bones the locomotion gaits are measured at (<see cref="ClipGait.Analyze"/>; Ork O7a).</summary>
    public FootBones Feet { get; init; } = FootBones.Marine;

    /// <summary>The rig archive's path in a game folder.</summary>
    public string ArchivePath(string gameDir) => Path.Combine(gameDir, Archive);

    /// <summary>The rig packfile's bytes (read-only; the last entry of that name, as the readers always took).</summary>
    /// <exception cref="FileNotFoundException">The entry is not in the archive.</exception>
    public byte[] ReadRig(string gameDir)
    {
        byte[]? rig = null;
        using (var archive = ObpkArchive.Open(ArchivePath(gameDir)))
            archive.ReadEntries(e => e.FileName == Entry, (_, d) => rig = d.ToArray());
        return rig ?? throw new FileNotFoundException($"'{Entry}' not found.");
    }

    /// <summary>The rig as a Havok character (skeleton, animations, behaviour graphs).</summary>
    public HavokCharacterFile LoadCharacter(string gameDir) => new(ReadRig(gameDir));
}

/// <summary>The bones a rig's feet touch the ground with (the contact points <see cref="ClipGait"/> measures strides at).</summary>
public sealed record FootBones(string LeftToe, string RightToe, string LeftAnkle, string RightAnkle)
{
    /// <summary>The Space Marine rig's (<c>RIG_L_Toe</c> ...).</summary>
    public static readonly FootBones Marine = new(ClipGait.LeftToe, ClipGait.RightToe, ClipGait.LeftAnkle, ClipGait.RightAnkle);
    /// <summary>The Ork boy's (<c>RIG_LLeg_Toe_1</c> ...; <c>smtool graph-dump --bones --rig ork</c>).</summary>
    public static readonly FootBones OrkBoy = new("RIG_LLeg_Toe_1", "RIG_RLeg_Toe_1", "RIG_LLeg_Foot", "RIG_RLeg_Foot");
}

/// <summary>A weapon's animation families on a rig: hip fire (movement and recoil), reload (null: no reload clip) and sprint.</summary>
public sealed record WeaponFamilies(HipFireWeaponClass HipFire, ReloadFamily? Reload, SprintFamily Sprint);

/// <summary>The rigs the alpha knows.</summary>
public static class RigProfiles
{
    // Declared first: static fields initialise in order and OrkBoy uses these.
    /// <summary>The Ork's gun (shoota) and pistol (slugga) families; one sprint for every weapon.</summary>
    static readonly WeaponFamilies OrkGun = new(HipFireWeaponClass.Rifle, ReloadFamily.Bolter, SprintFamily.Rifle),
        OrkPistol = new(HipFireWeaponClass.Pistol, ReloadFamily.BoltPistol, SprintFamily.Rifle);

    /// <summary>The multiplayer Space Marine rig, shared by both sides' armour (every reader's defaults).</summary>
    public static readonly RigProfile Marine = new("marine", CharacterModel.MarineRigArchive, CharacterModel.MarineRigEntry, WeaponModels.BodyVisual);

    /// <summary>The Ork rig's archive and packfile: the Exterminatus boy (<c>ork_coop_boy</c>; 51 bones, 220 clips, 25 graphs).</summary>
    public const string OrkArchive = "data/chunks/ebp_sets__mp_coop_set.oppc", OrkEntry = "havok_boy_coop__hkxx";

    /// <summary>
    /// The Ork Boy (Ork O2): graph names from <c>smtool graph-dump --rig ork</c> on the install. The boy has a ranged base graph
    /// (<c>Base_RANGED_Coop</c>: navigation, shoot, reload, grenade and evade states) and a melee one (<c>Base_MELEE_Coop</c>: the 1HM
    /// navigation with the sprint, the choppa combos). Its families stand in for the marine ones by slot (**ours**): <see cref="HipFireWeaponClass.Rifle"/>
    /// and <see cref="ReloadFamily.Bolter"/> = the gun (shoota), <see cref="HipFireWeaponClass.Pistol"/> and <see cref="ReloadFamily.BoltPistol"/> =
    /// the pistol (slugga); there is no heavy family.
    /// </summary>
    public static readonly RigProfile OrkBoy = new("ork", OrkArchive, OrkEntry, "boy_melee")
    {
        BaseGraph = "Base_RANGED_Coop",
        Relaxed =
        [
            // No relaxed stance: the 2HR aim run (four directions, walk at 2 and run at 6 units/s; the level-aim clip of each pitch blend).
            new(HipFireWeaponClass.Rifle, "BOY_NAV_RangedLOD", "High LOD Strafe") { PreferLevelPitch = true },
            // The pistol has no set of its own: the 1HM run of the melee graph (ours, design section 5).
            new(HipFireWeaponClass.Pistol, "BOY_NAV_Melee", "Move") { PreferLevelPitch = true },
        ],
        Sprints =
        [
            // One sprint clip for every weapon (no 2HR sprint): the melee graph's speed blend run 6 → sprint 9.5; the entry blend is the
            // navigation graph's own Move → Sprint transition.
            new(SprintFamily.Rifle, "BOY_NAV_Melee", "Sprint", "Behavior00", "Move", "Sprint") { ForwardOnly = true, TransitionGraph = "BOY_NAV_Melee" },
        ],
        HipFire =
        [
            new(HipFireWeaponClass.Rifle, "BOY_NAV_RangedLOD", "High LOD Strafe", "BOY_2HR_Shoot", "RANGED BEHAVIOR", "Ranged Shoot", "Ranged Navigation", "", null)
            { RecoilState = "Standing Shoot", ReloadBlend = "Moving Reload", ReloadBlendGraph = "BOY_2HR_Reload" },
            // ours: the 1HM movement with the 2HR additive recoil (no pistol set on the boy).
            new(HipFireWeaponClass.Pistol, "BOY_NAV_Melee", "Move", "BOY_2HR_Shoot", "RANGED BEHAVIOR", "Ranged Shoot", "Ranged Navigation", "", null)
            { RecoilState = "Standing Shoot", ReloadBlend = "Moving Reload", ReloadBlendGraph = "BOY_2HR_Reload" },
        ],
        Reloads =
        [
            // One reload clip for every gun (BOY_2HR_RNG_Reload_S_01); its mask sits in the reload graph's own "Moving Reload" blend.
            new(ReloadFamily.Bolter, "BOY_2HR_Reload", "Base_RANGED_Coop", "Ranged Reload", "Ranged Navigation")
            { FinishedBranch = "BRANCH_Reload_End", MaskInReloadGraph = true, MagazineOutWords = ["remove", "unload"] },
            new(ReloadFamily.BoltPistol, "BOY_2HR_Reload", "Base_RANGED_Coop", "Ranged Reload", "Ranged Navigation")
            { FinishedBranch = "BRANCH_Reload_End", MaskInReloadGraph = true, MagazineOutWords = ["remove", "unload"] },
        ],
        Evade = EvadeTuning.OrkRig,
        Reactions = ReactionTuning.OrkRig,
        Melee = [MeleeTuning.OrkChoppa, MeleeTuning.OrkChoppaSet2],
        DefaultWargear = ["slugga", "ork_coop_boy_2hand_shoota_rapid", "ork_coop_boy_choppa", "ork_coop_boy_stikkbomb"],
        Clips = RigClipTable.OrkBoy,
        Feet = FootBones.OrkBoy,
        WeaponMounts = OrkWeaponMounts.Table,
        // The boy's two guns by slot (ours, as the maps above): the marine name rules would make the slugga a rifle and give neither a reload.
        WargearFamilies = new Dictionary<string, WeaponFamilies>(StringComparer.OrdinalIgnoreCase)
        {
            ["slugga"] = OrkPistol,
            ["ork_coop_boy_2hand_shoota_rapid"] = OrkGun,
        },
        SlotFamilies = new Dictionary<WeaponSlot, WeaponFamilies> { [WeaponSlot.Ranged] = OrkGun, [WeaponSlot.Pistol] = OrkPistol },
    };

    /// <summary>A profile by name ("marine", "ork"; case ignored), or null.</summary>
    public static RigProfile? ByName(string? name) => name?.ToLowerInvariant() switch
    {
        "marine" or "sm" or "csm" => Marine,
        "ork" or "orkboy" or "boy" => OrkBoy,
        _ => null,
    };
}

/// <summary>A pitch triple of aim clips (down, level, up), each a list of candidates in order of preference (the first in the rig wins).</summary>
/// <param name="AdditivePitch">The down and up clips are additive offsets to lay over the level clip (the Ork's aim idles), not whole poses.</param>
public sealed record AimClipTriple(IReadOnlyList<string> Down, IReadOnlyList<string> Level, IReadOnlyList<string> Up, bool AdditivePitch = false);

/// <summary>
/// The clip names the client picks by name rather than reading from the graphs (Ork O2; design section 5). The marine table holds the
/// constants the client uses today (<c>NetworkView</c> idle and toss, <c>NetworkView.Aim</c> stances, <c>CharacterViewer.DefaultAnimation</c>,
/// <c>NetworkView.HipFire</c>'s grenade-launcher recoil, the customizer preview's idle). Empty lists and nulls: the rig has no such clip
/// (see <see cref="Unfilled"/>). The client switches to the table in O3/O7.
/// </summary>
public sealed record RigClipTable
{
    /// <summary>The standing idle, best first (<c>NetworkView.IdleCandidates</c>).</summary>
    public IReadOnlyList<string> IdleCandidates { get; init; } = [];
    /// <summary>The customizer preview's idle, best first (<c>PreviewStage.IdleCandidates</c>).</summary>
    public IReadOnlyList<string> PreviewIdleCandidates { get; init; } = [];
    /// <summary>The model viewer's default clip (<c>CharacterViewer.DefaultAnimation</c>).</summary>
    public string DefaultAnimation { get; init; } = "";
    /// <summary>The grenade toss when the class data name none (<c>NetworkView.DefaultTossAnimation</c>).</summary>
    public string? TossClip { get; init; }
    /// <summary>The rifle's recoil clip (the hip-fire graphs name it too; recorded for the client's fallbacks).</summary>
    public string? RecoilClip { get; init; }
    /// <summary>The grenade launcher's partial recoil overlay (<c>NetworkView.HipFire.GrenadeRecoilClip</c>).</summary>
    public string? GrenadeLauncherRecoilClip { get; init; }
    /// <summary>
    /// Aim stances by the client's keys: <c>&lt;family&gt;:wos</c> (standing) and <c>&lt;family&gt;:walk:&lt;dir&gt;</c> with the eight
    /// directions F, FR, R, BR, B, BL, L, FL; families rifle, pistol, heavy.
    /// </summary>
    public IReadOnlyDictionary<string, AimClipTriple> AimStances { get; init; } = new Dictionary<string, AimClipTriple>();
    /// <summary>Death clips before the ragdoll takes over (empty: the ragdoll at once, as the marine).</summary>
    public IReadOnlyList<string> DeathClips { get; init; } = [];
    /// <summary>Entries the rig cannot fill, with the reason (for reports and O3/O7).</summary>
    public IReadOnlyList<string> Unfilled { get; init; } = [];

    static readonly string[] Dirs8 = ["F", "FR", "R", "BR", "B", "BL", "L", "FL"];

    /// <summary>The marine's table: today's client constants.</summary>
    public static readonly RigClipTable Marine = new()
    {
        IdleCandidates = ["SM_NAV_IdleL_S_01", "SM_NAV_Idle_S_01", "SM_NAV_IdleR_S_01"],
        PreviewIdleCandidates = ["SM_NAV_IdleL_S_Relaxed_LE", "SM_NAV_Idle_S_01", "SM_NAV_IdleL_S_01", "SM_NAV_IdleR_S_01"],
        DefaultAnimation = "SM_NAV_Run_F_01",
        TossClip = "SM_RNG_GrenadeToss_01",
        RecoilClip = "SM_RNG_FireIdle_S_01",
        GrenadeLauncherRecoilClip = "SM_RNG_GL_FireIdle_01",
        AimStances = MarineAim(),
        Unfilled = ["death clips: none (the marine goes to the ragdoll at once)"],
    };

    static Dictionary<string, AimClipTriple> MarineAim()
    {
        var a = new Dictionary<string, AimClipTriple>(StringComparer.Ordinal)
        {
            ["rifle:wos"] = new(["SM_NAV_IdleLFocus_S_AimDown_01"], ["SM_NAV_IdleLFocus_S_AimForward_01"], ["SM_NAV_IdleLFocus_S_AimUp_01"]),
            ["pistol:wos"] = new(["SM_1HR_NAV_IdleL_S_AimDown_01"], ["SM_1HR_NAV_IdleL_S_Aim_F_01"], ["SM_1HR_NAV_IdleL_S_AimUp_01"]),
            ["heavy:wos"] = new(["SM_NAV_TB_Idle_S_AimDown_01"], ["SM_NAV_TB_Idle_S_AimFoward_01"], ["SM_NAV_TB_Idle_S_AimUp_01"]),
        };
        foreach (var d in Dirs8)
        {
            a[$"rifle:walk:{d}"] = new([$"SM_NAV_WalkFocus_{d}_AimDown_01"], [$"SM_NAV_WalkFocus_{d}_Aim_F_01"], [$"SM_NAV_WalkFocus_{d}_AimUp_01"]);
            a[$"pistol:walk:{d}"] = new([$"SM_1HR_NAV_Walk_{d}_AimDown_01", $"SM_1HR_NAV_Walk_{d}_AimD_01"], [$"SM_1HR_NAV_Walk_{d}_AimF_01"],
                [$"SM_1HR_NAV_Walk_{d}_AimUp_01", $"SM_1HR_NAV_Walk_{d}_AimU_01"]);
        }
        foreach (var d in new[] { "F", "R", "B", "L" })
            a[$"heavy:walk:{d}"] = new([$"SM_NAV_TB_Walk_{d}_AimDown_01"], [$"SM_NAV_TB_Walk_{d}_AimForward_01"], [$"SM_NAV_TB_Walk_{d}_AimUp_01"]);
        return a;
    }

    /// <summary>
    /// The Ork Boy's table, from the boy's graphs (<c>BOY_NAV_RangedLOD</c>, <c>BOY_NAV_Melee</c>, <c>BOY_2HR_Shoot</c>, <c>Base_RANGED_Coop</c>,
    /// <c>Base_MELEE_Coop</c>). The standing aim is the ranged idle with additive up/down offsets; the 2HR walk has four directions with
    /// aim-down / forward / up clips; the pistol (no set of its own) uses the 1HM idle and walk with the 2HR additive pitch (ours, design section 5).
    /// </summary>
    public static readonly RigClipTable OrkBoy = new()
    {
        IdleCandidates = ["BOY_2HR_RNG_Idle_S_01", "BOY_2HR_NAV_Idle_S_01"],
        PreviewIdleCandidates = ["BOY_2HR_NAV_Idle_S_01", "BOY_2HR_RNG_Idle_S_01", "BOY_1HM_MEL_Idle_Combat_03"],
        DefaultAnimation = "BOY_2HR_NAV_Run_F_AimForward_02",
        TossClip = "BOY_2HR_RNG_GrenadeToss",
        RecoilClip = "BOY_2HR_RNG_Fire_S_01",
        GrenadeLauncherRecoilClip = null,
        AimStances = OrkAim(),
        // Base_RANGED_Coop "Death Behavior": the ragdoll death and the front / back deaths (Base_MELEE_Coop plays the 1HM ones).
        DeathClips = ["BOY_2HR_DIE_B", "BOY_1HM_DIE_Basic_03", "BOY_1HM_DIE_Basic_02", "BOY_2HR_DIE_Fall_Forward_01", "BOY_2HR_DIE_L", "BOY_2HR_DIE_R", "BOY_1HM_DIE_Headshot_01"],
        Unfilled =
        [
            "rifle:walk:FR/BR/BL/FL: no diagonal clips (four directions; blend the neighbours, design section 5)",
            "pistol:walk:*: no 1HM aim-pitch walk (the 1HM walk clips are level only; pitch from the 2HR additive idle offsets, ours)",
            "pistol:walk:FR/BR/BL/FL: no diagonal 1HM clips",
            "heavy:*: no heavy weapon on the Ork",
            "grenade-launcher recoil: no grenade launcher on the Ork",
            "rifle:wos down/up: additive offsets (BOY_2HR_RNG_Idle_F_dn_01 / _up_01), not whole poses",
        ],
    };

    static Dictionary<string, AimClipTriple> OrkAim()
    {
        var a = new Dictionary<string, AimClipTriple>(StringComparer.Ordinal)
        {
            ["rifle:wos"] = new(["BOY_2HR_RNG_Idle_F_dn_01"], ["BOY_2HR_RNG_Idle_S_01"], ["BOY_2HR_RNG_Idle_F_up_01"], AdditivePitch: true),
            ["pistol:wos"] = new(["BOY_2HR_RNG_Idle_F_dn_01"], ["BOY_1HM_MEL_Idle_Combat_03"], ["BOY_2HR_RNG_Idle_F_up_01"], AdditivePitch: true),
        };
        foreach (var d in new[] { "F", "R", "B", "L" })
            a[$"rifle:walk:{d}"] = new([$"BOY_2HR_NAV_Walk_{d}_AimDown_02"], [$"BOY_2HR_NAV_Walk_{d}_AimForward_02"], [$"BOY_2HR_NAV_Walk_{d}_AimUp_02"]);
        a["pistol:walk:F"] = new([], ["BOY_1HM_NAV_walk_F_01"], []);
        a["pistol:walk:B"] = new([], ["BOY_1HM_NAV_Walk_Strafe_B_01"], []);
        a["pistol:walk:L"] = new([], ["BOY_1HM_NAV_Walk_Strafe_L_01"], []);
        a["pistol:walk:R"] = new([], ["BOY_1HM_NAV_Walk_Strafe_R_01"], []);
        return a;
    }
}
