using SmFormats.Baf;
using SmFormats.Havok;
using SmFormats.Obpk;
using SmSim;

namespace SmContent;

/// <summary>
/// A melee weapon's combo from the install: per attack, the timing tags of its sequencer in the marine's behavior graph
/// (<see cref="HavokBehavior"/>) turned into seconds with the attack's animation length and playback speed, and the
/// damage, multipliers and reach from the weapon's wargear file (default entries). <see cref="Animations"/> names the clip
/// each attack plays. Since M28 also the heavy strikes (<see cref="Heavy"/>), each hit's reaction (mapped by the
/// multiplayer reaction mapping) and the window of each attack without <c>BRANCH_HitReact</c>.
/// </summary>
public sealed record MeleeTuning(string Weapon, IReadOnlyList<MeleeStep> Steps, IReadOnlyList<string> Animations, float Range, IReadOnlyList<string> Notes)
{
    /// <summary>The heavy strikes (Y1, Y2, …; the strike after that many light attacks), or empty.</summary>
    public IReadOnlyList<MeleeStep> Heavy { get; init; } = [];
    /// <summary>The clip each heavy strike plays.</summary>
    public IReadOnlyList<string> HeavyAnimations { get; init; } = [];

    /// <summary>
    /// A weapon's graph, the attack sequencers in combo order, and the matching wargear abilities; since M28 the heavy strikes'
    /// sequencers and abilities (an empty ability name: a strike whose only effect is the stomp blast, timed by the sequencer's
    /// <see cref="StompTag"/> action and read from the wargear's <see cref="StompAction"/> triggered action).
    /// </summary>
    public sealed record WeaponMap(string Wargear, string Graph, string[] Sequencers, string[] Abilities)
    {
        public string[] HeavySequencers { get; init; } = [];
        public string[] HeavyAbilities { get; init; } = [];
        /// <summary>The branches after which the attacker moves again (the earliest present); the Ork's combos have <c>BRANCH_Run</c>.</summary>
        public string[] MoveBranches { get; init; } = ["BRANCH_Melee_Draw_Run", "BRANCH_Melee_Run"];
        /// <summary>The branches whose first window is an attack's combo window (Weapons C: the power axe's attacks carry <c>BRANCH_Melee_Combo_MISS</c>).</summary>
        public string[] ComboBranches { get; init; } = ["BRANCH_Melee_Combo"];
        /// <summary>
        /// Weapons C: whether the last light attack chains into attack 1 (the knife and chainsword graphs have that transition); false:
        /// its graph state has no light transition out (<see cref="MeleeStep.StringEnd"/>).
        /// </summary>
        public bool LoopLights { get; init; } = true;
        /// <summary>sim/stomp-in-place: the blast strikes of this map (the heavy classes' stomp) keep the body in place (<see cref="MeleeStep.InPlace"/>; no root-motion lunge).</summary>
        public bool InPlaceStomp { get; init; }
        /// <summary>
        /// Weapons C: per light attack, the heavy strike (1-based) a heavy press in its combo window chains to; 0 (or missing): the old rule
        /// (strike n + 1 after attack n), -1: none (<see cref="MeleeStep.HeavyNext"/>; read off the graph's <c>BTN_Melee_Stun</c> transitions).
        /// </summary>
        public int[] LightHeavyNext { get; init; } = [];
        /// <summary>
        /// Weapons C: the Y,Y finishers, appended to the heavy list after <see cref="HeavySequencers"/> (sequencer and ability; an empty
        /// ability: a strike whose effect is its <c>DISPLACE_*</c> blast).
        /// </summary>
        public string[] FinisherSequencers { get; init; } = [];
        public string[] FinisherAbilities { get; init; } = [];
        /// <summary>
        /// Weapons C: per heavy strike (the heavy list: heavies then finishers), the strike (1-based) a heavy press in its
        /// <see cref="YComboBranches"/> window chains to; 0 (or missing): none (<see cref="MeleeStep.YNext"/>).
        /// </summary>
        public int[] YNext { get; init; } = [];
        /// <summary>The branches whose first window is a heavy strike's Y-chain window (the hammer's is <c>BRANCH_To_Slam</c>).</summary>
        public string[] YComboBranches { get; init; } = ["BRANCH_Melee_Y_Combo"];
        /// <summary>
        /// Weapons C reader rules (only the new maps; review 2026-10-28: the knife, chainsword and choppa reads stay byte-identical): the
        /// <c>damage_armourshield</c> part, blast strikes from any <c>DISPLACE_*</c> triggered blast (also for an ability without damage), the
        /// slam ability's reaction immunity and the middle of three clips without a distance name. Off: the M9/M28 rules.
        /// </summary>
        public bool ExtendedRules { get; init; }
        /// <summary>
        /// feel/melee-lock: the blend value without a target, when not the graph's own start value (null: the graph's). The Ork's choppa: 1 (ours;
        /// its co-op AI graph starts at 7, which would lunge a player Ork 5 m at nothing; 1 is the knife's and the lowest variant).
        /// </summary>
        public float? DefaultTargetDistance { get; init; }
    }

    /// <summary>
    /// A playback speed a perk sets on a clip through its graph (Weapons C, Fast Stomp): a <c>hkbSetFloatModifier</c> enabled by
    /// <c>BOOL_UPGRADE_&lt;upgrade&gt;</c> writes <see cref="Speed"/> into the variable the clip's <c>playbackSpeed</c> is bound to.
    /// </summary>
    public sealed record SpeedUpgrade(string Graph, string Animation, string Upgrade, float Speed);

    /// <summary>The heavy combo's stomp: the sequencer's action tag and the wargear's triggered action it fires.</summary>
    public const string StompTag = "DISPLACE_FootStomp", StompAction = "displace_footstomp";

    /// <summary>
    /// The combat knife: Tactical and Devastator melee in multiplayer (unlock level 1). Combo order CK 1 → CK 2 → CK 3 is
    /// taken from the sequencer and ability names (the state machine transitions are not read yet). Heavy strikes CK Y1..Y4
    /// with <c>ability_melee_y</c>, <c>_y2</c>, <c>_y3</c> and the foot stomp (M28; the combat-controls strings "Backhand: Light
    /// stun/interrupt (single target)", "Kick: Medium stun (wedge)", "Shoulder Bash: Heavy stun (large wedge)", "Foot Stomp:
    /// Super Heavy stun (radial)", locale 120693–121018, name the four).
    /// </summary>
    public static readonly WeaponMap CombatKnife = new("sm_mp_melee_combat_knife", "MP_SM_MEL_CombatKnife",
        ["CK 1 Sequence", "CK 2 Sequence", "CK 3 Sequence"], ["ability_melee_ck1", "ability_melee_ck2", "ability_melee_ck3"])
    {
        HeavySequencers = ["CK Y1 Sequence", "CK Y2 Sequence", "CK Y3 Sequence", "CK Y4 Sequence"],
        HeavyAbilities = ["ability_melee_y", "ability_melee_y2", "ability_melee_y3", ""],
        // feel/melee-charge-2 (protocol 42; the owner: "normal combo is backhand > stomp"): the graph's "CK Default Combo End 1 ( Y ) Backhand", "End 2 ( X , Y ) Kick"
        // and "End 3 ( X , X , Y ) Shoulder" go on BTN_Melee_Stun in their BRANCH_Melee_Y_Combo window to "CK Default Combo End 4 ( X , X , X , Y ) AOE Attack"
        // (CK Y4, the foot stomp). Not read before: a second F during a heavy strike did nothing.
        YNext = [4, 4, 4, 0],
    };

    /// <summary>
    /// The Ork Boy's choppa (Ork O2), combo set 1 of <c>Base_MELEE_Coop</c> ("Combo Attack Set 1"): "Combo Start Sequencer"
    /// (<c>BOY_1HM_MEL_Cmbo_01_Atk01_*</c>) → "Combo 2 Sequencer" (<c>_Atk02_*</c>), both <c>ability_melee_light</c>; the heavy strike
    /// "Combo Heavy Attack Sequencer" (<c>_Hvy01_*</c>, <c>ability_melee_heavy</c>). In the graph the heavy comes after the two light
    /// attacks of a held combo (Light to Heavy Combo Start → 2 → Heavy); taking it as the first heavy strike is **ours**. The graph picks set 1
    /// or 2 at random (<c>FLT_Combo_Chooser_CACHED</c>); see <see cref="OrkChoppaSet2"/>.
    /// </summary>
    public static readonly WeaponMap OrkChoppa = new("ork_coop_boy_choppa", "Base_MELEE_Coop",
        ["Combo Start Sequencer", "Combo 2 Sequencer"], ["ability_melee_light", "ability_melee_light"])
    {
        HeavySequencers = ["Combo Heavy Attack Sequencer"],
        HeavyAbilities = ["ability_melee_heavy"],
        MoveBranches = ["BRANCH_Run"], DefaultTargetDistance = 1f,
    };

    /// <summary>The choppa's combo set 2 (<c>BOY_1HM_MEL_Cmbo_02_*</c>), as <see cref="OrkChoppa"/>.</summary>
    public static readonly WeaponMap OrkChoppaSet2 = new("ork_coop_boy_choppa", "Base_MELEE_Coop",
        ["Combo Set 2 Start Sequencer", "Combo Set 2 Follow Sequencer"], ["ability_melee_light", "ability_melee_light"])
    {
        HeavySequencers = ["Combo Heavy Attack 2 Sequencer"],
        HeavyAbilities = ["ability_melee_heavy"],
        MoveBranches = ["BRANCH_Run"], DefaultTargetDistance = 1f,
    };

    /// <summary>
    /// The power sword (Weapons C; Assault and Raptor, <c>sm_mp_melee_powersword</c> / <c>csm_mp_melee_powersword</c>), graph
    /// <c>MP_SM_MEL_PowerSword</c>. States and abilities from the graph's ability modifiers: X1..X4 = <c>Sequencer_X1..X4</c>
    /// (<c>ability_melee_x1..x4</c>; X4 has no light transition out); Y = "PS Default Combo End 1 ( Y ) Backhand Attack"
    /// (<c>CS Y Atk Sequencer00</c>, <c>ability_melee_y</c>), X,Y = "Kick Attack" (<c>Sequencer27</c>, <c>_y2</c>), X,X,Y = "Shoulder Attack"
    /// (<c>Sequencer28</c>, its nested state; the knee follow-up <c>Sequencer29</c> is not played; <c>_y3</c>), X,X,X,Y = "Quick AOE Attack"
    /// (<c>Sequencer31</c>, <c>ability_melee_y4</c>, the <c>displace_quickstomp</c> blast); every heavy chains on a heavy press in its
    /// <c>BRANCH_Melee_Y_Combo</c> window to Y,Y = "AOE Attack" (<c>Sequencer30</c>, the <c>displace_footstomp</c> blast).
    /// </summary>
    public static readonly WeaponMap PowerSword = new("sm_mp_melee_powersword", "MP_SM_MEL_PowerSword",
        ["Sequencer_X1", "Sequencer_X2", "Sequencer_X3", "Sequencer_X4"], ["ability_melee_x1", "ability_melee_x2", "ability_melee_x3", "ability_melee_x4"])
    {
        HeavySequencers = ["CS Y Atk Sequencer00", "Sequencer27", "Sequencer28", "Sequencer31"],
        HeavyAbilities = ["ability_melee_y", "ability_melee_y2", "ability_melee_y3", "ability_melee_y4"],
        LoopLights = false, LightHeavyNext = [0, 0, 0, -1],
        FinisherSequencers = ["Sequencer30"], FinisherAbilities = [""],
        YNext = [5, 5, 5, 5, 0],
        ExtendedRules = true,
    };

    /// <summary>
    /// The power axe and the Chaos chain axe (Weapons C; <c>sm_mp_melee_power_axe</c> / <c>csm_mp_melee_chain_axe</c>), graph
    /// <c>MP_SM_MEL_PowerAxe</c>: X1..X4 = "PA Default 1", "2B", "3B", "4A" (<c>ability_melee_x1..x4</c>; their combo windows are
    /// <c>BRANCH_Melee_Combo_MISS</c>; 4A has none), Y = <c>Sequencer19</c> (backhand), X,Y = <c>Sequencer22</c> (kick), X,X,Y =
    /// <c>Sequencer23</c> (shoulder), X,X,X,Y = <c>Sequencer31</c> (quick AOE, <c>displace_quickstomp</c>), Y,Y = <c>Sequencer24</c>
    /// (<c>SM_MEL_PA_FURY_HitGrnd</c>, the <c>displace_ax_slam</c> blast). Each hit's 250 armour-shield damage is the wargear's.
    /// </summary>
    public static readonly WeaponMap PowerAxe = new("sm_mp_melee_power_axe", "MP_SM_MEL_PowerAxe",
        ["SequencerGenerator_PA_Default_1", "SequencerGenerator_PA_Default_2B", "SequencerGenerator_PA_Default_3B", "PA Default 4 Sequence"],
        ["ability_melee_x1", "ability_melee_x2", "ability_melee_x3", "ability_melee_x4"])
    {
        HeavySequencers = ["Sequencer19", "Sequencer22", "Sequencer23", "Sequencer31"],
        HeavyAbilities = ["ability_melee_y", "ability_melee_y2", "ability_melee_y3", "ability_melee_y4"],
        ComboBranches = ["BRANCH_Melee_Combo", "BRANCH_Melee_Combo_MISS"],
        LoopLights = false, LightHeavyNext = [0, 0, 0, -1],
        FinisherSequencers = ["Sequencer24"], FinisherAbilities = [""],
        YNext = [5, 5, 5, 5, 0],
        ExtendedRules = true,
    };

    /// <summary>
    /// The thunder hammer and the Chaos daemon maul (Weapons C; <c>sm_mp_melee_2hand_thunder_hammer</c> / <c>csm_mp_melee_2hand_power_mace</c>),
    /// graph <c>MP_SM_MEL_ThunderHammer</c>: X1..X3 = "TH Default 1", "2", "3" (<c>SequencerGenerator_TH_1</c>, <c>05</c>, <c>15</c>;
    /// <c>ability_melee_x1..x3</c>; "TH Default 3" has no transition out), Y = "Interrupt Attack" (<c>Sequencer16</c>), X,Y and X,X,Y =
    /// "Sweeping Attack" (<c>SequencerGenerator18</c>, <c>23</c>); a heavy press in the <c>BRANCH_To_Slam</c> window chains to the
    /// Y,Y "Hammer Slam" (right side <c>SequencerGenerator20</c> after Y and X,Y, left side <c>SequencerGenerator22</c> after X,X,Y;
    /// <c>ability_displace_slam</c>, the <c>displace_hammerslam</c> blast). The light-to-heavy window is read from
    /// <c>BRANCH_Melee_Combo</c> (the graph's "TH Default 1" uses <c>BRANCH_To_Slam</c>, 0.34–0.70 against 0.35–0.72: ours).
    /// </summary>
    public static readonly WeaponMap ThunderHammer = new("sm_mp_melee_2hand_thunder_hammer", "MP_SM_MEL_ThunderHammer",
        ["SequencerGenerator_TH_1", "SequencerGenerator05", "SequencerGenerator15"], ["ability_melee_x1", "ability_melee_x2", "ability_melee_x3"])
    {
        HeavySequencers = ["Sequencer16", "SequencerGenerator18", "SequencerGenerator23"],
        HeavyAbilities = ["ability_melee_y", "ability_melee_y2", "ability_melee_y3"],
        LoopLights = false, LightHeavyNext = [0, 0, -1],
        FinisherSequencers = ["SequencerGenerator20", "SequencerGenerator22"], FinisherAbilities = ["ability_displace_slam", "ability_displace_slam"],
        YNext = [4, 4, 5, 0, 0],
        YComboBranches = ["BRANCH_To_Slam"],
        ExtendedRules = true,
    };

    /// <summary>
    /// The Devastator's and the Havoc's melee (Weapons C): their core ability <c>sm_mp_core_abil_heavy_evade</c> (both races) with the
    /// graph <c>MP_SM_MEL_TB_Displace</c> (the heavy-weapon kick <c>SM_MEL_TB_YATK_Kick_*</c>; the other displace graph
    /// <c>MP_SM_MEL_Displace</c> kicks with the chainsword clip: which one the original uses with the pistol is not decoded, ours): melee =
    /// "Displace 1 ( Y )" (<c>Sequencer13</c>, <c>ability_displace_kick</c>), stun = "Displace 2 ( Y HOLD )" (<c>Sequencer15a</c>,
    /// <c>ability_displace_slam</c>: reaction immunity while it lasts, the <c>displace_footstomp</c> blast). The kick chains to the stomp
    /// on a stun press in its <c>BRANCH_Melee_Combo</c> window (its <c>BRANCH_Melee_Tap2Hold</c> 0–0.16 switch is not simulated), so the
    /// stomp is heavy strike 1 and the kick's heavy target.
    /// </summary>
    public static readonly WeaponMap Displace = new("sm_mp_core_abil_heavy_evade", "MP_SM_MEL_TB_Displace", ["Sequencer13"], ["ability_displace_kick"])
    {
        HeavySequencers = ["Sequencer15a"], HeavyAbilities = ["ability_displace_slam"],
        LoopLights = false, LightHeavyNext = [1],
        ExtendedRules = true, InPlaceStomp = true,
    };

    public static MeleeTuning Load(string gameDir, WeaponMap? weapon = null) => Load(gameDir, RigProfiles.Marine, weapon ?? CombatKnife);

    /// <summary>A melee weapon's combo on a profile's rig (Ork O2); without a map the profile's first (<see cref="RigProfile.Melee"/>).</summary>
    public static MeleeTuning Load(string gameDir, RigProfile profile, WeaponMap? weapon = null)
    {
        weapon ??= profile.Melee.Count > 0 ? profile.Melee[0] : CombatKnife;
        byte[] rig = profile.ReadRig(gameDir);
        BafDocument? wargear = null;
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.WargearPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName == weapon.Wargear, (_, d) => wargear = BafDocument.Read(d));
        if (wargear is null) throw new FileNotFoundException($"'{weapon.Wargear}' not found in {GameplayTuning.WargearPackage}.");
        var character = new HavokCharacterFile(rig);
        var mappingNotes = new List<string>();
        var mapping = ReactionTuning.LoadMapping(gameDir, mappingNotes);
        var tuning = From(HavokBehavior.Read(character.Pack), name => character.LoadAnimation(name).Animation.Duration, wargear, weapon, mapping,
            reach: MeleeMotion.Source.From(character, mappingNotes));
        return tuning with { Notes = [.. mappingNotes, .. tuning.Notes] };
    }

    /// <param name="animationLength">Seconds of an animation by name (its clip at playback speed 1).</param>
    /// <param name="mapping">The multiplayer reaction mapping (<see cref="ReactionTuning.ReadMapping"/>); null: reaction types as written.</param>
    /// <param name="speedUpgrades">Clip speeds perks set through the graph (<see cref="ReadSpeedUpgrades"/>; Weapons C, Fast Stomp): applied to a
    /// strike's clip while <see cref="UpgradeScope.Current"/> grants the upgrade; null: none.</param>
    /// <param name="reach">feel/melee-lock: where each attack's root motion and blend data come from (<see cref="MeleeMotion.Source"/>); null: no
    /// <see cref="MeleeStep.Reach"/> (no lunge; the view turns the attack, as before protocol 32).</param>
    public static MeleeTuning From(IReadOnlyList<BehaviorGraph> graphs, Func<string, float> animationLength, BafDocument wargear, WeaponMap weapon,
        IReadOnlyDictionary<string, string>? mapping = null, IReadOnlyList<SpeedUpgrade>? speedUpgrades = null, MeleeMotion.Source? reach = null)
    {
        var notes = new List<string>();
        var graph = graphs.FirstOrDefault(g => g.Name.Equals(weapon.Graph, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Behavior graph '{weapon.Graph}' not found.");
        var abilities = wargear.Root.Table("ability_data/ability_definitions");
        var steps = new List<MeleeStep>();
        var animations = new List<string>();
        var heavy = new List<MeleeStep>();
        var heavyAnimations = new List<string>();
        float range = 0f;
        for (int i = 0; i < weapon.Sequencers.Length; i++)
        {
            var (step, animation) = Step(weapon.Sequencers[i], weapon.Abilities[i]);
            // Weapons C: where the graph leaves this attack for a heavy strike, and whether a light press goes on from it.
            int heavyNext = i < weapon.LightHeavyNext.Length ? weapon.LightHeavyNext[i] : 0;
            step = step with { HeavyNext = Math.Max(-1, heavyNext), StringEnd = !weapon.LoopLights && i == weapon.Sequencers.Length - 1 };
            steps.Add(step);
            animations.Add(animation);
        }
        // The heavy list: the strikes after n light attacks, then the Y,Y finishers (Weapons C).
        var heavySequencers = weapon.HeavySequencers.Concat(weapon.FinisherSequencers).ToArray();
        var heavyAbilities = weapon.HeavySequencers.Select((_, i) => i < weapon.HeavyAbilities.Length ? weapon.HeavyAbilities[i] : "")
            .Concat(weapon.FinisherSequencers.Select((_, i) => i < weapon.FinisherAbilities.Length ? weapon.FinisherAbilities[i] : "")).ToArray();
        for (int i = 0; i < heavySequencers.Length; i++)
        {
            // A heavy strike that cannot be read ends the heavy chain there (the light combo stays usable).
            try
            {
                var (step, animation) = Step(heavySequencers[i], heavyAbilities[i]);
                int yNext = i < weapon.YNext.Length ? weapon.YNext[i] : 0;
                if (yNext > 0)
                {
                    var window = seqOf(heavySequencers[i]).Tags.Where(t => t.Kind == "Branch" && t.Name is { } n && weapon.YComboBranches.Contains(n)).OrderBy(t => t.Start).FirstOrDefault();
                    if (window is null) { notes.Add($"{heavySequencers[i]}: no {string.Join("/", weapon.YComboBranches)}; this strike does not chain into a finisher"); yNext = 0; }
                    else
                    {
                        // Bad data degrades instead of failing the config load (GameplayTuning.StepsOk): the window's end is clamped to the
                        // strike, and a window that opens after it closes (or not finite, or before the strike) drops this Y chain with a note.
                        float open = window.Start * step.Duration, end = window.End * step.Duration, close = MathF.Min(end, step.Duration);
                        if (!float.IsFinite(open) || !float.IsFinite(close) || open < 0f || open > close)
                        { notes.Add($"{heavySequencers[i]}: Y window {window.Start}–{window.End} of the clip not usable; this strike does not chain into a finisher"); yNext = 0; }
                        else
                        {
                            if (end > step.Duration) notes.Add($"{heavySequencers[i]}: Y window ends after the strike ({window.End} of the clip); clamped to its end");
                            step = step with { YNext = yNext, YComboOpen = open, YComboClose = close };
                        }
                    }
                }
                heavy.Add(step);
                heavyAnimations.Add(animation);
            }
            catch (InvalidDataException e) { notes.Add($"heavy strike {i + 1}: {e.Message}; the heavy chain stops before it"); break; }
        }
        // A chain into a strike that could not be read is dropped (the simulation also checks the bounds).
        for (int i = 0; i < steps.Count; i++)
            if (steps[i].HeavyNext > heavy.Count) steps[i] = steps[i] with { HeavyNext = -1 };
        for (int i = 0; i < heavy.Count; i++)
            if (heavy[i].YNext > heavy.Count) heavy[i] = heavy[i] with { YNext = 0, YComboOpen = 0f, YComboClose = 0f };
        if (range <= 0f) { notes.Add("no ability max_range; using 4"); range = 4f; }
        return new MeleeTuning(weapon.Wargear, steps, animations, range, notes) { Heavy = heavy, HeavyAnimations = heavyAnimations };

        BehaviorSequencer seqOf(string sequencer) => graph.Sequencers.First(s => s.Name == sequencer);

        (MeleeStep Step, string Animation) Step(string sequencer, string abilityName)
        {
            var seq = graph.Sequencers.FirstOrDefault(s => s.Name == sequencer)
                ?? throw new InvalidDataException($"Sequencer '{sequencer}' not in {weapon.Graph}.");
            if (seq.Animations.Count == 0) throw new InvalidDataException($"Sequencer '{seq.Name}' plays no animation.");
            // Blended attacks play distance variants (…_1m, …_3.5m, …_7m) of equal length; the middle one is drawn (the 3.5 m one,
            // else the middle of three: Weapons C, the power sword's "Combo4" and the hammer's "Ytap_ATK" name theirs without a distance).
            int chosen = seq.Animations.ToList().FindIndex(a => a.Contains("3.5"));
            if (chosen < 0) chosen = weapon.ExtendedRules && seq.Animations.Count == 3 ? 1 : 0;
            string animation = seq.Animations[chosen];
            float speed = chosen < seq.PlaybackSpeeds.Count ? seq.PlaybackSpeeds[chosen] : 1f;
            if (!float.IsFinite(speed) || speed <= 0f) { notes.Add($"{seq.Name}: playback speed {speed} not usable; using 1"); speed = 1f; }
            else if (speed != 1f) notes.Add($"{seq.Name}: playback speed {speed}");
            // Weapons C: a perk's graph-set speed (Fast Stomp) while the variant read grants its upgrade.
            if (speedUpgrades?.FirstOrDefault(u => u.Graph.Equals(weapon.Graph, StringComparison.OrdinalIgnoreCase) && u.Animation.Equals(animation, StringComparison.OrdinalIgnoreCase)
                    && UpgradeScope.Current.Contains(u.Upgrade, StringComparer.OrdinalIgnoreCase)) is { } faster && float.IsFinite(faster.Speed) && faster.Speed > 0f)
                speed = faster.Speed;
            float duration = animationLength(animation) / speed;
            if (!float.IsFinite(duration) || duration <= 0f) throw new InvalidDataException($"{seq.Name}: animation '{animation}' has no usable length.");
            if (seq.Animations.Select(animationLength).Distinct().Count() > 1) notes.Add($"{seq.Name}: blended clips differ in length; using {animation}");

            // Tag windows are fractions of the playing clip; a tag missing from the data is noted and gets the fallback.
            BehaviorTag? First(string kind, string name) => seq.Tags.Where(t => t.Kind == kind && t.Name == name).OrderBy(t => t.Start).FirstOrDefault();
            var hitTag = First("Event", "ACTN_Hit_Targets_Multiplayer") ?? First("Event", "ACTN_Hit_Primary_Target");
            var ability = abilityName.Length > 0 ? abilities?.Table(abilityName) : null;
            var primaryActions = ability?.Table("primary_target_actions/hit_sim/actions");
            var secondaryActions = ability?.Table("secondary_target_actions/hit_sim/actions");
            var primary = DefaultDamage(primaryActions);
            var secondary = DefaultDamage(secondaryActions);
            // A strike without an ability, or whose ability does no damage of its own (Weapons C: the quick AOE's ability_melee_y4, the
            // slams' ability_displace_slam), is a blast strike: it lands at its first DISPLACE_* action whose triggered action is a blast.
            SplashSpec? stomp = null;
            BehaviorTag? stompTag = null;
            // The knife, chainsword and choppa maps keep the M28 rule: only an ability-less strike, only DISPLACE_FootStomp (review: their reads stay byte-identical).
            if (abilityName.Length == 0 || (weapon.ExtendedRules && primary is null))
                foreach (var tag in seq.Tags.Where(t => t.Kind == "Action" && (weapon.ExtendedRules ? IsDisplaceTag(t.Name) : t.Name == StompTag)).OrderBy(t => t.Start))
                {
                    if (Stomp(wargear, tag.Name!.ToLowerInvariant(), notes) is not { } blast) continue;
                    (stomp, stompTag) = (blast, tag);
                    break;
                }
            if (abilityName.Length == 0 && stompTag is null) throw new InvalidDataException($"{seq.Name}: a strike without an ability needs a DISPLACE_* action with a blast");
            if (hitTag is null && stompTag is null) notes.Add($"{seq.Name}: no hit event; using 0.25 of the clip");
            float hit = (stompTag?.Start ?? hitTag?.Start ?? 0.25f) * duration;

            float comboOpen = 0f, comboClose = 0f;   // no combo tag: no combo from this attack
            var combo = seq.Tags.Where(t => t.Kind == "Branch" && t.Name is { } cn && weapon.ComboBranches.Contains(cn)).OrderBy(t => t.Start).ToList();
            if (combo.Count == 0) notes.Add($"{seq.Name}: no {string.Join("/", weapon.ComboBranches)}; this attack does not chain");
            else
            {
                (comboOpen, comboClose) = (combo[0].Start * duration, combo[0].End * duration);
                if (combo.Count > 1) notes.Add($"{seq.Name}: {combo.Count} combo windows; using the first");
            }
            var rangedTag = First("Branch", "BRANCH_ToRanged");
            if (rangedTag is null) notes.Add($"{seq.Name}: no BRANCH_ToRanged; firing cancels only after the attack");
            float ranged = (rangedTag?.Start ?? 1f) * duration;
            // Moving again: the earliest of the run branches present (the knife has only Draw_Run).
            var moveTag = seq.Tags.Where(t => t.Kind == "Branch" && t.Name is { } n && weapon.MoveBranches.Contains(n)).OrderBy(t => t.Start).FirstOrDefault();
            if (moveTag is null) notes.Add($"{seq.Name}: no run branch; movement returns at the end");
            float move = (moveTag?.Start ?? 1f) * duration;
            // feel/locks-sprint-stop: the first BRANCH_Evade window's start (a Jump press ends the attack from there; none: not during the attack).
            var evadeTag = First("Branch", "BRANCH_Evade");
            float evadeAt = evadeTag is null ? float.PositiveInfinity : Math.Min(evadeTag.Start * duration, duration);
            float lunge = seq.Tags.FirstOrDefault(t => t.Kind == "AttackDistance")?.Distance ?? 0f;

            if (abilityName.Length > 0 && ability is null) notes.Add($"{abilityName}: not in the wargear; no damage");
            else if (abilityName.Length > 0 && primary is null && stomp is null) notes.Add($"{abilityName}: no primary damage found");
            var maxRange = ability?.Table("ability_metadata/generic_metadata")?.Float("max_range");
            if (maxRange is float r && float.IsFinite(r) && r > 0f) range = Math.Max(range, r);
            var (noReactFrom, noReactTo) = ReactionTuning.NoReactWindow(seq, duration);
            // Weapons C: an ability that applies mp_reaction_immunity when it starts (the slams' ability_displace_slam) keeps the attacker out
            // of reactions for the whole strike (the upgrade is removed when the ability ends; that it ends with the strike is ours).
            if (weapon.ExtendedRules && GrantsReactionImmunity(ability)) (noReactFrom, noReactTo) = (0f, duration);
            else if (noReactTo <= noReactFrom && !seq.Tags.Any(t => t.Name == "BRANCH_HitReact")) notes.Add($"{seq.Name}: no BRANCH_HitReact; always interruptible (interpretation)");

            var step = new MeleeStep(duration, hit, comboOpen, comboClose, ranged, move,
                primary?.Damage ?? 0f, secondary?.Damage ?? 0f, float.IsFinite(lunge) ? lunge : 0f,
                primary?.HealthMultiplier ?? 1f, primary?.ShieldMultiplier ?? 1f)
            {
                PrimaryReaction = ReactionTuning.HitReaction(primaryActions, mapping),
                SecondaryReaction = ReactionTuning.HitReaction(secondaryActions, mapping),
                NoReactFrom = noReactFrom, NoReactTo = noReactTo, Stomp = stomp,
                Heal = HealOf(primaryActions), SecondaryHeal = HealOf(secondaryActions),   // M43
                DamageType = primary?.Type is { } t && t != DamageType.Unknown ? t : DamageType.Melee,
                // Weapons C: the axe's 250. Only for the Weapons C maps: the choppa's 5 (light attacks) would change the Ork's balanced combo (open question).
                ShieldDamage = weapon.ExtendedRules ? primary?.ShieldDamage ?? 0f : 0f, SecondaryShieldDamage = weapon.ExtendedRules ? secondary?.ShieldDamage ?? 0f : 0f,
                // feel/melee-lock: the alignment window, distance variants and root motion.
                InPlace = weapon.InPlaceStomp && stomp is not null,
                Reach = reach is null || (weapon.InPlaceStomp && stomp is not null) ? null : MeleeMotion.ReachOf(graph.Name, seq, duration, reach, weapon.DefaultTargetDistance, notes, move),
                EvadeAt = evadeAt,
            };
            if (Problem(step) is { } problem) throw new InvalidDataException($"{seq.Name}: {problem}.");
            return (step, animation);
        }
    }

    /// <summary>
    /// The heavy combo's stomp blast: <c>action_data/triggered_actions/displace_footstomp/commands</c>, its <c>area_of_effect</c>
    /// directly or in the <c>actions_by_upgrade</c> entry without a required upgrade, with its damage, fall-off and reactions.
    /// </summary>
    static SplashSpec? Stomp(BafDocument wargear, string action, List<string> notes)
    {
        if (wargear.Root.Table($"action_data/triggered_actions/{action}") is null) return null;   // a DISPLACE_* tag without a triggered action of its name
        var commands = wargear.Root.Table($"action_data/triggered_actions/{action}/commands");
        var aoe = UpgradeScope.Command(commands, "area_of_effect");   // M43: the perk's entry under a perk read (Sure Strike, Furious Stomp)
        if (aoe is not BafTable blastTable) return null;   // e.g. the heavy evade's displace_kick (effects only)
        return StompBlast(blastTable, notes);
    }

    /// <summary>
    /// Whether an action tag names a blast strike's action: <c>DISPLACE_*</c> (footstomp, quickstomp, AX slam, hammer slam), not the
    /// co-op copies (<c>*_Coop</c>, read only with the <c>is_in_coop</c> upgrade in the original: ours).
    /// </summary>
    static bool IsDisplaceTag(string? name) => name is not null && name.StartsWith("DISPLACE_", StringComparison.OrdinalIgnoreCase)
        && !name.EndsWith("_Coop", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether an ability's <c>start_actions/start_sim/actions</c> apply <c>mp_reaction_immunity</c> (directly or in an upgrade block's chosen entry).</summary>
    static bool GrantsReactionImmunity(BafTable? ability)
    {
        var apply = UpgradeScope.Command(ability?.Table("start_actions/start_sim/actions"), "apply_upgrade_string", allBlocks: true);
        return apply?.Table("strings")?.Entries.Any(e => e.Type == BafType.String && e.AsString().Equals("mp_reaction_immunity", StringComparison.OrdinalIgnoreCase)) == true;
    }

    /// <summary>
    /// Clip speeds perks set through the graphs (Weapons C, Fast Stomp): every <c>hkbSetFloatModifier</c> whose <c>enable</c> is bound
    /// to a <c>BOOL_UPGRADE_&lt;upgrade&gt;</c> variable, against the clips whose <c>playbackSpeed</c> is bound to the variable it writes.
    /// Layout (**interpretation**, one class in the install: "Set Stomp Speed Float" in both displace graphs): the written variable's
    /// index at +44, the value at +48 (1.5 there; the variable's start value is 1, the clip's own speed).
    /// </summary>
    public static IReadOnlyList<SpeedUpgrade> ReadSpeedUpgrades(HavokPackfile pack, List<string>? notes = null)
    {
        var found = new List<SpeedUpgrade>();
        IReadOnlyList<BehaviorGraphDetail> details;
        try { details = HavokBehaviorDetailReader.Read(pack); }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or ArgumentException or IndexOutOfRangeException)
        { notes?.Add($"graph speed upgrades: {e.GetType().Name}: {e.Message}; none read"); return found; }
        foreach (var g in details)
            foreach (var node in g.Nodes.Values.Where(n => n.Class == "hkbSetFloatModifier"))
            {
                if (!node.Bindings.TryGetValue("enable", out var enable) || !enable.StartsWith("BOOL_UPGRADE_", StringComparison.Ordinal)) continue;
                int variable = pack.ReadInt32(node.Offset + 44);
                float value = pack.ReadSingle(node.Offset + 48);
                if (variable < 0 || variable >= g.Variables.Count || !float.IsFinite(value) || value <= 0f)
                { notes?.Add($"{g.Name} '{node.Name}': variable {variable} / value {value} not usable; skipped"); continue; }
                string target = g.Variables[variable];
                foreach (var clip in g.Clips)
                    if (g.Nodes.TryGetValue(clip.Offset, out var clipNode) && clipNode.Bindings.TryGetValue("playbackSpeed", out var bound) && bound == target)
                        found.Add(new SpeedUpgrade(g.Name, clip.Animation, enable["BOOL_UPGRADE_".Length..], value));
            }
        return found;
    }

    static SplashSpec? StompBlast(BafTable aoe, List<string> notes)
    {
        static bool Bool(BafTable t, string f) => t[f] is { Type: BafType.Bool } e && e.AsBool();
        float radius = aoe.Table("shape")?.Float("radius") ?? 0f;
        if (WeaponTuning.DamageOf(aoe.Table("hit_actions/commands/do_damage"), StompAction, notes) is not { } damage || !float.IsFinite(radius) || radius <= 0f)
        { notes.Add($"{StompAction}: blast without radius or damage"); return null; }
        var fallOff = WeaponTuning.Points(aoe.Table("fall_off"), "fall_off_entry", "intensity_value").OrderBy(q => q.Distance).ToList();
        var (reactions, blind) = ReactionTuning.BlastEffects(aoe);
        return new SplashSpec(radius, damage, fallOff, Bool(aoe, "line_of_sight"), Bool(aoe, "ignore_self"), aoe.String("target_filter") ?? "")
        {
            Reactions = reactions, Blindness = blind,
        };
    }

    /// <summary>Times in order within the attack, finite non-negative damage and multipliers; null when consistent.</summary>
    static string? Problem(MeleeStep s)
    {
        float[] all = [s.Duration, s.HitTime, s.ComboOpen, s.ComboClose, s.RangedAt, s.MoveAt, s.Damage, s.SecondaryDamage, s.Lunge, s.HealthMultiplier, s.ShieldMultiplier];
        if (all.Any(v => !float.IsFinite(v) || v < 0f)) return "a value is negative or not finite";
        if (s.HitTime <= 0f || s.HitTime > s.Duration) return $"hit at {s.HitTime} s outside the {s.Duration} s attack";
        if (s.ComboOpen > s.ComboClose || s.ComboClose > s.Duration) return $"combo window {s.ComboOpen}–{s.ComboClose} s not inside the attack";
        if (s.ComboClose > 0f && s.ComboOpen < s.HitTime) return "combo window opens before the hit";
        if (s.RangedAt > s.Duration || s.MoveAt > s.Duration) return "cancel or move time after the attack";
        if (!float.IsFinite(s.ShieldDamage) || s.ShieldDamage < 0f || !float.IsFinite(s.SecondaryShieldDamage) || s.SecondaryShieldDamage < 0f) return "armour-shield damage negative or not finite";
        // The Y window and the chain targets are set after this check (MeleeTuning.From validates them where they are set: Weapons C review).
        return null;
    }

    sealed record Hit(float Damage, float HealthMultiplier, float ShieldMultiplier, DamageType Type)
    {
        /// <summary>The file's separate <c>damage_armourshield</c> (Weapons C).</summary>
        public float ShieldDamage { get; init; }
    }

    /// <summary>
    /// The <c>do_damage</c> a hit applies: directly under the actions, or in an <c>actions_by_upgrade</c> block's entry chosen for the
    /// granted upgrades (<see cref="UpgradeScope.Command"/>: the default entry outside a perk read); with its body multipliers for
    /// health and armour shield and its damage type.
    /// </summary>
    static Hit? DefaultDamage(BafTable? actions)
    {
        if (UpgradeScope.Command(actions, "do_damage") is not { } dd || dd.Float("damage_general") is not float damage) return null;
        return new Hit(damage,
            dd.Table("damage_material_multipliers")?.Float("default_body") ?? 1f,
            dd.Table("armourshield_damage_material_multipliers")?.Float("default_body") ?? 1f, WeaponTuning.TypeOf(dd))
        {
            ShieldDamage = dd.Float("damage_armourshield") is float sd && float.IsFinite(sd) && sd > 0f ? sd : 0f,
        };
    }

    /// <summary>
    /// The health a hit gives back to the attacker (M43, the heal-on-hit perks): the <c>do_heal/health</c> under the actions'
    /// <c>target_self_action</c> (directly or in an upgrade block chosen for the granted upgrades), 0 without one. Its
    /// <c>prerequisites\health</c> (below 0.9999 of the maximum) is the simulation's rule (<c>World</c>), not read.
    /// </summary>
    internal static float HealOf(BafTable? actions)
    {
        static BafTable? Find(BafTable t, string name, int depth)
        {
            if (depth > 12) return null;
            foreach (var e in t.Entries.Where(e => e.Type == BafType.Table))
            {
                if (e.Name == name) return e.AsTable();
                if (Find(e.AsTable(), name, depth + 1) is { } found) return found;
            }
            return null;
        }
        if (UpgradeScope.Command(actions, "target_self_action", allBlocks: true) is not { } self || Find(self, "do_heal", 0) is not { } heal) return 0f;
        return heal.Float("health") is float h && float.IsFinite(h) && h > 0f ? h : 0f;
    }
}
