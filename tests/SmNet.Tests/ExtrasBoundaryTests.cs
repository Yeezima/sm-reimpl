using System.Text.RegularExpressions;

namespace SmNet.Tests;

/// <summary>
/// Added content E1 (docs/added-content.md): the source boundary that keeps the added content separable at any time (the owner's rule,
/// 2026-10-28). Every <c>.cs</c> file of <c>src/</c>, <c>client/</c> and <c>tests/</c> is scanned as text:
/// <list type="bullet">
/// <item>no file outside an <c>Extras/</c> folder names an added-content namespace: <c>&lt;Project&gt;</c> dot <c>Extras</c>, or a bare
/// <c>Extras</c> dot <c>&lt;Feature&gt;</c> dot ... (as code inside a project namespace could write it), or <c>using</c> / <c>namespace</c> with it;
/// member access such as <c>SimConfig.Extras</c> or <c>Extras.HasFlag</c> is not a namespace and passes;</item>
/// <item>hook declarations (partial methods named <c>Extras*</c>) only as the pinned (file, hook) pairs of <see cref="Hooks"/>, and every
/// call of one in core code is marked <c>// EXTRAS-HOOK</c>;</item>
/// <item>the <c>SM_EXTRAS</c> symbol in a preprocessor condition only in the files of <see cref="SymbolFiles"/>.</item>
/// </list>
/// This is a text guard that catches mistakes early; the real gate is <c>faithful-build.cmd</c>, which compiles everything without the
/// <c>Extras/</c> folders. A new hook goes on <see cref="Hooks"/>; a file that must test the build symbol goes on <see cref="SymbolFiles"/>.
/// </summary>
public class ExtrasBoundaryTests
{
    /// <summary>The hooks core code may declare: (file relative to the repository with forward slashes, hook name).</summary>
    static readonly HashSet<(string File, string Hook)> Hooks =
    [
        ("src/SmSim/ContentExtras.cs", "ExtrasHash"),              // SimConfig.ExtrasHash(BinaryWriter), called in SimConfig.Hash
        ("src/SmSim/ContentExtras.cs", "ExtrasAirJumps"),
        ("src/SmSim/ContentExtras.cs", "ExtrasJumpPackOwnerCapacity"),   // SimConfig.JumpPackOwnerCapacity: Musou keeps the 300 jump-pack capacity
        ("src/SmSim/ContentExtras.cs", "ExtrasNoBulletSpread"),    // SimConfig.ExtrasNoBulletSpread(ref bool), read by SimConfig.NoBulletSpread (musou/no-spread: Musou Mode's shots have no spread)          // SimConfig.ExtrasAirJumps(ref bool), read by SimConfig.JumpPackAirJumps (sim/air-jumps: Musou Mode keeps the jump-pack air jump)
        ("src/SmContent/GameplayTuning.cs", "ExtrasLoad"),         // GameplayTuning.ExtrasLoad(ref SimConfig, string, List<string>), called in LoadForMatch
        ("src/SmContent/Customization/ProfileView.cs", "ExtrasLookRemoved"),   // ProfileViews.ExtrasLookRemoved(ref CustomizationProfile, string), in RemoveLook: the Musou Librarian's look (musou/loadout-slots review)
        ("src/SmServer/GameServer.Envelope.cs", "ExtrasSend"),     // GameServer.ExtrasSend(), called once a tick after SendPickups (E3's envelope), only with added content
        ("client/NetworkView.cs", "ExtrasInput"),                  // NetworkView.ExtrasInput(ref PlayerInput), on every sampled live input (loyalist barks: the FOR THE EMPEROR key)
        ("client/NetworkView.cs", "ExtrasFrame"),                  // NetworkView.ExtrasFrame(PlayerState, IReadOnlyList<PlayerState>, double), once a frame after the sound (loyalist barks: barks and the key's shouts)
        ("client/NetworkView.Match.cs", "ExtrasKill"),             // NetworkView.ExtrasKill(KillView), each kill-feed entry once (loyalist barks: kill barks)
        ("client/NetworkView.Scoreboard.cs", "ExtrasScoreboard"),  // NetworkView.ExtrasScoreboard(ref single, ref score, ref assists), the Tab scoreboard (Musou: one column, run points, no assists)
        ("client/ModelViewer.cs", "ExtrasTestAid"),                // ModelViewer.ExtrasTestAid(args, ref bool), before the game install is located (Ashfall kit gallery: a test view that needs no install)
        // Musou (MU0): each returns at once unless World._musou is set (World.StartMusou, only under ContentExtras.Musou).
        ("src/SmSim/Simulation.cs", "ExtrasStep"),                 // World.ExtrasStep(), once a tick after ApplyPerkBlasts, before CountProjectilesOut (Musou: the enemies)
        ("src/SmSim/Simulation.cs", "ExtrasMelee"),                // World.ExtrasMelee(int, PlayerState, MeleeStep), at the end of ResolveMelee (Musou: the cleave)
        ("src/SmSim/Projectiles.cs", "ExtrasBlast"),               // World.ExtrasBlast(int, Vector3, SplashSpec, bool), at the end of ApplyBlast (Musou: blasts on enemies)
        ("src/SmSim/JumpWorld.cs", "ExtrasJump"),                  // World.ExtrasJump(int, JumpEventKind), at the end of ResolveJump (Musou: the air slash's landing heals; owner 2026-11-09)
        ("src/SmSim/Stims.cs", "ExtrasStimBoost"),                 // SimConfig.ExtrasStimBoost(ref StimBoost), Musou Mode's combat stims (x3 lifetime, x2 melee, +20 % speed, no ammunition; owner 2026-11-09)
        ("src/SmSim/Stims.cs", "ExtrasThrownBlastScale"),          // SimConfig.ExtrasThrownBlastScale(ThrownSpec?, ref float), Musou Mode's frag blast x2 (owner 2026-11-09)
        ("src/SmSim/Projectiles.cs", "ExtrasProjectileStop"),      // World.ExtrasProjectileStop(in ProjectileState, Vector3, float, ref float), in Fly before a hit is taken (Musou S4: a shield dome or a contraption stops a projectile)
        ("src/SmSim/DamageScale.cs", "ExtrasDamageTaken"),         // World.ExtrasDamageTaken(int target, ref float scale), in DamageTakenScale at every player-damage site (Musou: AI Marines take a tenth)
        ("src/SmSim/Teams.cs", "ExtrasStepMatch"),                 // World.ExtrasStepMatch(ref bool handled), top of StepMatch; the core skips its flow when handled (Musou: one side)
        ("src/SmSim/Teams.cs", "ExtrasJoinSide"),                  // World.ExtrasJoinSide(ref Team, ref bool forced), in AssignClass / AssignSide (Musou: Space Marines only, no balance)
        ("src/SmServer/GameServer.cs", "ExtrasCreated"),           // GameServer.ExtrasCreated(), end of the constructor when Extras is not None (Musou: the nav graph, World.StartMusou)
        // Musou Mode (TF0): each returns at once unless World.Tfw is set (World.StartTfw, only on the map tfw_war under ContentExtras.Musou), or for another map.
        ("src/SmSim/Simulation.cs", "ExtrasSampled"),              // World.ExtrasSampled(int, in PlayerInput), after the input is sanitized (Musou: the Librarian reads its keys)
        ("src/SmSim/Simulation.cs", "ExtrasPlayerInput"),          // World.ExtrasPlayerInput(int, ref PlayerInput, ref bool handled), before Step asks for a player's input (the AI Marines have no client)
        ("src/SmServer/GameServer.cs", "ExtrasGeneratedMap"),      // static GameServer.ExtrasGeneratedMap(ContentExtras, string, ref string?, ref GameMap?, ref string?), in GeneratedMap, which Program.cs calls (tfw_war, composed from the install)
        ("src/SmServer/GameServer.cs", "ExtrasPlayerName"),        // GameServer.ExtrasPlayerName(int, ref string), in ScoreboardOf (the AI Marines' names)
        ("src/SmServer/GameServer.Envelope.cs", "ExtrasCommand"),  // GameServer.ExtrasCommand(int, byte, byte[]), a client's command (message kind 17), only under ContentExtras.CommandUsers
        ("client/NetworkView.cs", "ExtrasMapFile"),                // NetworkView.ExtrasMapFile(ref string), the install's map behind a map name (tfw_war_m09: Manufactorum; tfw_war: none), in LoadMap and for the map's effects and sounds
        ("src/SmServer/GameServer.Appearance.cs", "ExtrasPuppetAppearance"),   // GameServer.ExtrasPuppetAppearance(int, ref (ushort, Race, AppearanceCode)?), in PeerlessLook (scoreboard version, appearance query): Musou Mode's retainers wear their owner's look
        ("client/NetworkView.MeleeFx.cs", "ExtrasSwordOverlay"),    // NetworkView.ExtrasSwordOverlay(PlayerState, WeaponModel, ref ShaderMaterial?), the power sword glow (Musou: the Librarian force sword)
        ("client/NetworkView.MeleeHitSound.cs", "ExtrasMeleeTarget"),   // NetworkView.ExtrasMeleeTarget(PlayerState, MeleeStep, ref Vector3?), the own strike's hit sound: Musou's enemies in reach
        ("client/NetworkView.MeleeHitSound.cs", "ExtrasOwnStrikeContact"),   // NetworkView.ExtrasOwnStrikeContact(PlayerState, MeleeStep), called from MeleeFxFor at the own strike's hit: Musou's predicted contact (combat feel 2026-11-09)
        ("client/NetworkView.Fx.cs", "ExtrasOwnShotEnd"),   // NetworkView.ExtrasOwnShotEnd(WeaponSpec, Vector3, Vector3, ref float, ref bool), in TraceOwn: Musou's Orks stop the drawn own shot
        ("client/NetworkView.Fx.cs", "ExtrasOwnShotContact"),   // NetworkView.ExtrasOwnShotContact(PlayerState, WeaponSpec, Vector3, Vector3, Vector3), in FxPlayOwnShot: the predicted shot's impact and hit sound on an Ork
        ("client/NetworkView.Fx.cs", "ExtrasShakeTotal"),   // NetworkView.ExtrasShakeTotal(float, float, ref float), in FxShake: Musou's shakes do not stack
        ("src/SmSim/WorldBodies.cs", "ExtrasRecordBodies"),   // World.ExtrasRecordBodies(), each tick: added content's solid bodies (Musou's big Orks, musou/boss-collision)
        ("src/SmSim/WorldBodies.cs", "ExtrasBodies"),   // World.ExtrasBodies(uint, ref BodyView?): those bodies in a player's movement collision
        ("src/SmSim/WorldBodies.cs", "ExtrasLockView"),   // World.ExtrasLockView(uint, in PlayerState, ref IReadOnlyDictionary<int, PlayerState>): the melee lock's extra targets (the big Orks)
        ("client/NetworkView.Appearance.cs", "ExtrasLook"),        // NetworkView.ExtrasLook(int, ref Func<ResolvedAppearance, ResolvedAppearance>?), in LookFor before the resolver: an added class's pieces over the look (Musou: the Librarian's armour)
        ("client/NetworkView.Loadout.cs", "ExtrasMatchMenu"),      // NetworkView.ExtrasMatchMenu(MatchMenu), when the Esc menu is built: an added mode's own rows (Musou Mode: the host's BACK TO STORMBIRD during a battle)
        ("client/NetworkView.Sound.cs", "ExtrasTrackEmitter"),   // NetworkView.ExtrasTrackEmitter(SoundEmitter, bool), in Track: Musou protects the own character's voice lines (musou/audio-pass)
        ("client/NetworkView.Sound.cs", "ExtrasFireLayers"),   // NetworkView.ExtrasFireLayers(WeaponSpec, SoundEmitter, bool), in PostFire: Musou's bolter rifles fire deeper (musou/audio-pass)
        ("client/CharacterViewer.cs", "ExtrasBlendSeconds"),       // CharacterViewer.ExtrasBlendSeconds(ref float, bool), in SelectLayer: Musou's stance cross-fade floor (nothing unless the viewer carries it)
        ("client/NetworkView.cs", "ExtrasAttackTime"),             // NetworkView.ExtrasAttackTime(PlayerState, bool, ref float), the attack pose's clip time: Musou's weight pass retimes the strike (hit and end kept) and holds the own pose
        ("client/NetworkView.Fx.cs", "ExtrasBlastFeel"),            // NetworkView.ExtrasBlastFeel(ProjectileView?, float, ref bool), in FxDetonation: Musou's weight pass keeps the jolts of blasts within 12 m (the strongest a 0.25 s window) and of the own grenades
        ("client/NetworkView.Fx.cs", "ExtrasCameraOffset"),         // NetworkView.ExtrasCameraOffset(double, ref Vector3), in FxApplyShake: Musou's camera push along the own heavy swing (camera space, times the Camera shake option)
        ("client/NetworkView.Sound.cs", "ExtrasFootfall"),         // NetworkView.ExtrasFootfall(string, PlayerState, bool, string?), in TagSound after a tag is admitted: Musou's weight pass reads the foot tags (ground contacts)
        ("client/NetworkView.Sound.cs", "ExtrasShotMaterial"),     // NetworkView.ExtrasShotMaterial(ShotView, int, ref bool?), in SoundShot: the material of another player's ranged hit (Musou: armoured Orks armour and flesh)
        // musou/gpu-particles: the effect player's GPU spray path (client/Extras/Musou/FxPlayer.MusouGpu.cs); each returns at once unless MusouGpuEnable ran (Musou Mode, or the --fx-gpu test aid).
        ("client/FxPlayer.cs", "ExtrasFxCap"),                     // FxPlayer.ExtrasFxCap(ref int), in Play: the live-effect cap with the GPU path
        ("client/FxPlayer.cs", "ExtrasFxBegin"),                   // FxPlayer.ExtrasFxBegin(double), in Update after the batch's Begin: the GPU clock
        ("client/FxPlayer.cs", "ExtrasFxEnd"),                     // FxPlayer.ExtrasFxEnd(), in Update after the batch's End: hide undrawn sprays, upload
        ("client/FxPlayer.cs", "ExtrasFxRelease"),                 // FxPlayer.ExtrasFxRelease(FxEffect), in Release: free the effect's records
        ("client/FxPlayer.cs", "ExtrasSpray"),                     // FxPlayer.ExtrasSpray(FxEffect, CSpray, in Ctx, double, int, uint, ref int), in DrawSpray: the GPU draws the ring particles
        ("client/FxPlayer.cs", "ExtrasFxStats"),                   // FxPlayer.ExtrasFxStats(ref string), in ExtraStats: the GPU path's counts for the effects log line
        ("client/FxTest.cs", "ExtrasTestEffect"),                  // FxTestView.ExtrasTestEffect(FxEffect), each effect the --fx test view starts (test aid: Musou's blood settings for the CPU / GPU stills)
        ("client/NetworkView.cs", "ExtrasMapCollision"),           // NetworkView.ExtrasMapCollision(string, ref CollisionWorld, ref ulong), in LoadMap after the level's collision (tfw_war: the Stormbird and the composed hash)
        ("client/NetworkView.cs", "ExtrasMapContent"),             // NetworkView.ExtrasMapContent(string, ref MapContent?), in LoadMap before any file read (tfw_war: Ashfall Citadel, generated)
        ("client/NetworkView.cs", "ExtrasMapVisuals"),             // NetworkView.ExtrasMapVisuals(string, MapContent, ref bool), in LoadMap for a generated map (tfw_war: the graybox view)
        // Musou Mode co-op (2026-11-06): up to 4 players.
        ("src/SmServer/GameServer.cs", "ExtrasPlayerCap"),         // GameServer.ExtrasPlayerCap(ref int), in PlayerCap when Extras is not None (Musou: at most 4 players)
        ("client/NetworkView.cs", "ExtrasBotInput"),               // NetworkView.ExtrasBotInput(PlayerState, uint, ref PlayerInput?), in BotInput after the countdown (Musou Mode: the co-op bot fights the Orks)
        ("client/NetworkView.Death.cs", "ExtrasCorpseHold"),      // NetworkView.ExtrasCorpseHold(Ragdoll, Vector3?, ref bool), before a Marine ragdoll is dropped (Musou Mode: the corpse floor holds it 10 s)
        ("client/FrontEnd/HostPanel.cs", "ExtrasHostMap"),         // HostPanel.ExtrasHostMap(ref string?), before HostOptions (Musou Mode: --ashfall-layout field picks draft 11, tfw_war_field)
        ("client/CharacterViewer.cs", "ExtrasPoseOverlay"),        // CharacterViewer.ExtrasPoseOverlay(), after the pose layers in ApplyStances and ApplyPose (Musou: the Librarian's procedural cast pose; nothing unless the viewer carries one)
        // Musou Mode's customizer (owner 2026-11-10; client/Extras/Musou/Customizer/): the top level Standard Marines / Librarian / Heretic in place of the factions, Musou only.
        ("client/Customizer/CreatorView.cs", "ExtrasTopLevel"),     // CreatorView.ExtrasTopLevel(ref bool), in BuildSideToggle: the Musou entries instead of the faction buttons
        ("client/Customizer/CreatorView.cs", "ExtrasRefresh"),      // CreatorView.ExtrasRefresh(bool, ref bool), at the start of Refresh: the Librarian's and the Heretic's pages
        ("client/Customizer/CreatorView.cs", "ExtrasRoleLabel"),    // CreatorView.ExtrasRoleLabel(string, ref string), the class tabs and rows: the Space Marine name alone
        ("client/Customizer/CreatorView.cs", "ExtrasOwnPage"),      // CreatorView.ExtrasOwnPage(ref bool), AddedPage: the Librarian's / Heretic's page hides the slot (no rename, copy, reset, clear; step keys work)
        ("client/Customizer/CreatorView.cs", "ExtrasStepCount"),    // CreatorView.ExtrasStepCount(ref int), the Librarian's five steps
        ("client/Customizer/CreatorView.cs", "ExtrasOpenAppearance"),   // CreatorView.ExtrasOpenAppearance(ref bool), APPEARANCE...: the look the Librarian wears
        ("client/Customizer/CreatorView.cs", "ExtrasDeploy"),       // CreatorView.ExtrasDeploy(ref bool), DEPLOY: PLAY AS LIBRARIAN on his page
        ("client/Customizer/CustomizerServices.cs", "ExtrasFactions"),   // CustomizerServices.ExtrasFactions(ref List<(string, string)>), the factions offered: the Space Marines alone in Musou
        ("client/Customizer/CustomizerServices.cs", "ExtrasProfileView"),   // CustomizerServices.ExtrasProfileView(ref IProfileView?), the profile view: Musou's own loadout slots (musou/loadout-slots)
        ("client/Customizer/CustomizerServices.cs", "ExtrasLoaded"),   // CustomizerServices.ExtrasLoaded(), the end of the services' load: Musou's own configuration is read before LoadTask completes (W1)
        ("client/Customizer/CustomizerServices.cs", "ExtrasConfig"),   // CustomizerServices.ExtrasConfig(ref SimConfig?), the configuration a Join is planned with: Musou's own, which lists its arsenal (W1, the autocannon)
        ("client/Customizer/PreviewStage.cs", "ExtrasDress"),       // PreviewStage.ExtrasDress(ref Func<ResolvedAppearance, ResolvedAppearance>?, ref string), in CreateModel: the Librarian's armour
        ("client/Customizer/PreviewStage.cs", "ExtrasWeapons"),     // PreviewStage.ExtrasWeapons(ref string?, ref string?, ref string?, ref bool), in ConfigureWeapons: his force sword in hand, no gun
        // W0, Musou's arsenal (docs/new-weapons-design.md section 8: the autocannon, lightning claw, power spear and Remote Detonators, Musou only); each does nothing without Musou's content
        // (the Remote Detonators' sm_mp_eq_demolition_charge is an install name, so the name alone never decides).
        ("src/SmContent/MpClasses.cs", "ExtrasMeleeMap"),           // static MpClasses.ExtrasMeleeMap(string, ref WeaponMap?), in MeleeMapFor when no data map matches: an added melee weapon on a data weapon's map
        ("src/SmSim/Loadout.cs", "ExtrasWieldsHammer"),             // static Loadouts.ExtrasWieldsHammer(SimConfig, string, ref bool), in WieldsHammer when no data hammer matches: the spear takes the hammer's rules
        ("src/SmContent/PerkCatalog.cs", "ExtrasItems"),            // PerkCatalog.ExtrasItems(ContentExtras, ref PerkCatalog), in For: the customizer rows of a content profile (Musou only)
        ("client/WeaponRig.cs", "ExtrasWeaponModel"),               // WeaponRig.ExtrasWeaponModel(HandRole, string, WeaponAssets, ref attachment, ref asset, ref bool), in AddItem when the catalogue has no held model
        ("client/NetworkView.Grenades.cs", "ExtrasThrownVisual"),   // NetworkView.ExtrasThrownVisual(ThrownSpec, ref ProjectileVisual?), in GrenadeModel: a thrown item drawn with another package's model
        ("src/SmContent/AnimationAlias.cs", "ExtrasAnimationAlias"),   // static AnimationAlias.ExtrasAnimationAlias(ref string), in Of: the data weapon an added one borrows its animations from
        ("src/SmContent/Customization/ProfileView.cs", "ExtrasSetCatalog"),   // static ProfileViews.ExtrasSetCatalog(string, ref CustomizationCatalog), in SetCatalog: Musou's set is read with Musou's catalogue
        ("client/NetworkView.MeleeLock.cs", "ExtrasMeleeClip"),   // NetworkView.ExtrasMeleeClip(string, bool, int, ref string?, ref float), in OwnStrikeClip: the animation seam, a strike's custom clip (the claw's and the spear's, once approved)
        // W0b (lead 2026-11-10): presentation by source and the pack moves' clip speed; each asks the match's content (Musou) as well as the name.
        ("client/NetworkView.Presentation.cs", "ExtrasPresentationSources"),   // NetworkView.ExtrasPresentationSources(string, ref IReadOnlyList<string>?), in PresentationSources: the install files an added weapon shows (catalogues and name rules)
        ("client/NetworkView.Presentation.cs", "ExtrasMoveClipSpeed"),   // NetworkView.ExtrasMoveClipSpeed(string, ref float), in MoveClipSpeed: the charge's and the pack's moves at the claw's / spear's clip speed
        ("client/Hud/Hud.Movie.cs", "ExtrasReticle"),               // Hud.ExtrasReticle(string, ref ReticleSpec?), in DrawMovieReticle: the autocannon's own movie reticle (Hud.Content = Musou)
        ("client/Hud/Hud.Fidelity.cs", "ExtrasWeaponBox"),          // Hud.ExtrasWeaponBox(string, bool, ref WeaponBoxSpec?), in DrawMovieWeaponBox: the autocannon's ammunition display (Hud.Content = Musou)
    ];

    /// <summary>
    /// W0, Musou's arsenal (docs/new-weapons-design.md section 8): the call sites W1 to W4 rely on, pinned so a change that drops one fails here (file, text of a non-comment line). The Musou ones
    /// call the no-op partial methods of <c>src/SmSim/Extras/Musou/Arsenal/World.Arsenal.cs</c>; the core ones read the clip-speed factor, the animation alias and the content's catalogue.
    /// </summary>
    static readonly (string File, string Call)[] ArsenalCallSites =
    [
        ("src/SmContent/Extras/GameplayTuning.Extras.cs", "Arsenal.Append("),
        ("src/SmSim/Extras/Musou/World.MusouCombat.cs", "ArsenalCleave("),
        ("src/SmSim/Extras/Musou/World.MusouCombat.cs", "ArsenalProjectileMeets("),
        ("src/SmSim/Extras/Musou/Campaign/World.TfwMeks.cs", "ArsenalProjectileMeets("),
        ("src/SmSim/Extras/Musou/Campaign/World.TfwMelee.cs", "ArsenalShotBlast("),
        ("src/SmSim/Extras/Musou/World.Musou.cs", "ArsenalJump("),
        ("src/SmSim/Extras/Musou/World.Musou.cs", "ArsenalStep("),
        ("src/SmSim/Extras/Musou/Campaign/World.TfwSupply.cs", "ArsenalRefillSeconds("),
        ("src/SmContent/MeleeTuning.cs", "speed *= weapon.ClipSpeed"),
        ("client/NetworkView.MeleeLock.cs", "time * MeleeClipSpeed(s)"),
        ("client/NetworkView.MeleeLock.cs", "OwnStrikeClip(s, time)"),
        ("client/NetworkView.cs", "AnimationAlias.Of(combo.Wargear)"),
        ("client/NetworkView.Reactions.cs", "AnimationAlias.Of(combo.Wargear)"),
        ("src/SmContent/HipFireAnimations.cs", "AnimationAlias.Of("),
        ("src/SmContent/LocomotionAnimations.cs", "AnimationAlias.Of("),
        ("src/SmContent/ReloadAnimations.cs", "AnimationAlias.Of("),
        ("src/SmContent/UpperBodyAnimations.cs", "AnimationAlias.Of("),
        ("client/NetworkView.MeleeFlow.cs", "AnimationAlias.Of("),
        ("client/Customizer/CustomizerServices.cs", "data.For(content)"),
        // W0b: the catalogues read with the sources, the name rules by the source's name, the hammer's air slash, charge and grip by the alias, the pack moves' clip speed.
        ("client/NetworkView.Fx.cs", ".WithSources(sources)"),
        ("client/NetworkView.Sound.cs", ".WithSources(sources)"),
        ("client/NetworkView.MeleeFx.cs", "PresentedAs(MeleeWargear(s))"),
        ("client/NetworkView.Sound.Melee.cs", "PresentedAs(wargear)"),
        ("client/NetworkView.MeleeHitSound.cs", "PresentedAs(MeleeWargear(attacker))"),
        ("client/NetworkView.HammerSwing.cs", "HammerSlashPose.IsHammer(AnimationAlias.Of(w))"),   // HammerSlash: the spear's air slash is the hammer's own clip
        ("client/NetworkView.HammerSwing.cs", "string named = AnimationAlias.Of(wargear);"),   // HammerStrideScale (--old-strike-legs): the spear strides as the hammer
        ("client/NetworkView.MeleeFlow.cs", "!AnimationAlias.Of(w).Contains(\"_2hand_\", StringComparison.OrdinalIgnoreCase) || !clip.StartsWith(\"SM_BUL_Hit_M_\""),   // HammerRush: the spear's charge is the hammer's sprint attack
        ("client/NetworkView.MeleeFlow.cs", "!AnimationAlias.Of(w).Contains(\"_2hand_\", StringComparison.OrdinalIgnoreCase)) return false;"),   // HammerHoldsBothHands: the spear holsters the pistol (W3)
        ("client/NetworkView.cs", "AnimationAlias.Of(twoHand).Contains(\"_2hand_\""),   // the transition probe's two-handed flag
        ("client/NetworkView.cs", "MoveClipSpeed(state)"),
        ("client/NetworkView.Jump.cs", "MoveClipSpeed(s)"),
        ("client/NetworkView.Feel.cs", "MoveClipSpeed(s)"),
        ("client/NetworkView.cs", "_hudView.Content = "),
    ];

    /// <summary>
    /// Files that may test <c>SM_EXTRAS</c> in a preprocessor condition: the switch itself, and the composition roots should they ever need it
    /// (none does yet; everything else asks <c>ContentExtras.Compiled</c>).
    /// </summary>
    static readonly HashSet<string> SymbolFiles =
    [
        "src/SmSim/ContentExtras.cs",
        "src/SmServer/Program.cs",
        "src/SmTool/Program.cs",
    ];

    /// <summary>The project namespaces whose <c>.Extras</c> sub-namespaces hold added content.</summary>
    static readonly Regex ExtrasNamespace = new(@"\b(SmSim|SmContent|SmNet|SmServer|SmTool|SmFormats|SmClient)\s*\.\s*Extras\b", RegexOptions.Compiled);
    /// <summary>A bare <c>Extras.Feature.X</c> (not after a dot or a word: so not <c>Config.Extras.HasFlag</c>), or <c>using</c> / <c>namespace</c> of <c>Extras.Feature</c>.</summary>
    static readonly Regex BareExtrasNamespace = new(@"(?<![\w.])Extras\s*\.\s*[A-Z]\w*\s*\.\s*[A-Za-z_]|^\s*(global\s+)?using\s+(static\s+)?(\w+\s*=\s*)?Extras\s*\.|^\s*namespace\s+Extras\b", RegexOptions.Compiled);
    static readonly Regex HookDeclaration = new(@"\bpartial\s+[\w<>\[\]?.,]+\s+(Extras\w*)\s*\(", RegexOptions.Compiled);
    /// <summary>A call of an <c>Extras*</c> method; not a type's construction or declaration (<c>new ExtrasUpdate(</c>, <c>record ExtrasUpdate(</c>: E3's envelope types).</summary>
    static readonly Regex HookCall = new(@"(?<![\w])(?<!\b(new|record|class|struct|interface)\s+)Extras[A-Z]\w*\s*\(", RegexOptions.Compiled);
    static readonly Regex SymbolCondition = new(@"^\s*#\s*(if|elif)\b.*\bSM_EXTRAS\b", RegexOptions.Compiled);

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("SmReimpl.slnx not found above the test assembly");
    }

    /// <summary>Every source file (relative path with forward slashes, lines), build output and Godot's cache left out.</summary>
    static List<(string Path, string[] Lines)> Sources()
    {
        string root = RepoRoot();
        var files = new List<(string, string[])>();
        foreach (string top in new[] { "src", "client", "tests" })
        {
            string dir = Path.Combine(root, top);
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Split('/').Any(part => part is "bin" or "obj" or ".godot")) continue;
                if (rel == "tests/SmNet.Tests/ExtrasBoundaryTests.cs") continue;   // this file names the patterns
                files.Add((rel, File.ReadAllLines(file)));
            }
        }
        return files;
    }

    static bool InExtras(string rel) => rel.Split('/').Contains("Extras");
    static bool IsComment(string line) => line.TrimStart().StartsWith("//", StringComparison.Ordinal);

    [Fact]
    public void The_scan_sees_the_source_and_the_known_hooks()
    {
        var sources = Sources();
        Assert.True(sources.Count > 300, $"only {sources.Count} files scanned");
        var declared = sources.SelectMany(s => s.Lines.Select(l => (s.Path, Match: HookDeclaration.Match(l))).Where(x => x.Match.Success))
            .Select(x => (x.Path, x.Match.Groups[1].Value)).ToHashSet();
        foreach (var hook in Hooks) Assert.Contains(hook, declared);   // a pinned hook that is gone (renamed or moved) fails here: update Hooks
        Assert.Contains(sources, s => s.Path == "src/SmSim/ContentExtras.cs" && s.Lines.Any(l => SymbolCondition.IsMatch(l)));
    }

    [Theory]
    [InlineData("using SmSim.Extras.Gore;", true)]
    [InlineData("var g = SmContent.Extras.Gore.GoreTuning.Load(dir);", true)]
    [InlineData("using Extras.Gore;", true)]
    [InlineData("using G = Extras.Gore.GoreSpec;", true)]
    [InlineData("namespace Extras.Gore;", true)]
    [InlineData("var spec = Extras.Gore.GoreSpec.Default;", true)]
    [InlineData("if (Config.Extras.HasFlag(ContentExtras.Gore)) return;", false)]
    [InlineData("if (Extras.HasFlag(ContentExtras.Orks)) return;", false)]
    [InlineData("w.Write((uint)Extras); w.Write(ContentExtras.WireVersion);", false)]
    [InlineData("var e = tuning.Config.Extras;", false)]
    public void The_namespace_patterns_tell_namespaces_from_member_access(string line, bool flagged) =>
        Assert.Equal(flagged, ExtrasNamespace.IsMatch(line) || BareExtrasNamespace.IsMatch(line));

    [Theory]
    [InlineData("ExtrasSend();", true)]
    [InlineData("        ExtrasHash(w);", true)]
    [InlineData("this.ExtrasFrame(delta);", true)]
    [InlineData("var u = new ExtrasUpdate(5, [(1, [])]);", false)]
    [InlineData("public sealed record ExtrasUpdate(uint ServerTick, (byte Tag, byte[] Payload)[] Sections) : Message", false)]
    [InlineData("public readonly record struct ExtrasSection(uint ServerTick, byte Tag, byte[] Payload);", false)]
    [InlineData("var q = client.TakeExtras();", false)]
    [InlineData("server.SendExtras(update);", false)]
    public void The_hook_call_pattern_tells_hook_calls_from_the_envelope_types(string line, bool flagged) =>
        Assert.Equal(flagged, HookCall.IsMatch(line));

    [Fact]
    public void No_core_file_names_an_extras_namespace()
    {
        var offenders = Sources().Where(s => !InExtras(s.Path))
            .SelectMany(s => s.Lines.Select((l, i) => (s.Path, Line: i + 1, Text: l))
                .Where(x => !IsComment(x.Text) && (ExtrasNamespace.IsMatch(x.Text) || BareExtrasNamespace.IsMatch(x.Text))))
            .Select(x => $"{x.Path}({x.Line}): {x.Text.Trim()}").ToList();
        Assert.True(offenders.Count == 0, "added-content namespaces named outside an Extras/ folder:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Hooks_are_declared_only_as_pinned_and_called_with_the_marker()
    {
        var sources = Sources().Where(s => !InExtras(s.Path)).ToList();
        var declared = sources
            .SelectMany(s => s.Lines.Select((l, i) => (s.Path, Line: i + 1, Text: l, Match: HookDeclaration.Match(l))).Where(x => !IsComment(x.Text) && x.Match.Success))
            .Where(x => !Hooks.Contains((x.Path, x.Match.Groups[1].Value)))
            .Select(x => $"{x.Path}({x.Line}): {x.Text.Trim()}").ToList();
        Assert.True(declared.Count == 0, "Extras* hooks declared in core code that are not pinned (add (file, hook) to Hooks with a note):\n" + string.Join("\n", declared));
        var unmarked = sources
            .SelectMany(s => s.Lines.Select((l, i) => (s.Path, Line: i + 1, Text: l)))
            .Where(x => !IsComment(x.Text) && !HookDeclaration.IsMatch(x.Text) && HookCall.IsMatch(x.Text) && !x.Text.Contains("// EXTRAS-HOOK", StringComparison.Ordinal))
            .Select(x => $"{x.Path}({x.Line}): {x.Text.Trim()}").ToList();
        Assert.True(unmarked.Count == 0, "Extras* hook calls without the // EXTRAS-HOOK marker:\n" + string.Join("\n", unmarked));
    }

    [Fact]
    public void The_arsenals_call_sites_are_in_place()
    {
        var sources = Sources().ToDictionary(s => s.Path, s => s.Lines);
        var missing = ArsenalCallSites.Where(c => !sources.TryGetValue(c.File, out var lines) || !lines.Any(l => !IsComment(l) && l.Contains(c.Call, StringComparison.Ordinal)))
            .Select(c => $"{c.File}: {c.Call}").ToList();
        Assert.True(missing.Count == 0, "the arsenal's call sites are gone (W1 to W4 rely on them; update ArsenalCallSites only with the design):\n" + string.Join("\n", missing));
        // Every no-op partial of World.Arsenal.cs is called from one of the pinned sites.
        var declared = sources["src/SmSim/Extras/Musou/Arsenal/World.Arsenal.cs"].Select(l => System.Text.RegularExpressions.Regex.Match(l, @"^\s*partial\s+void\s+(Arsenal\w+)\s*\("))
            .Where(m => m.Success).Select(m => m.Groups[1].Value + "(").ToList();
        Assert.Equal(6, declared.Count);
        foreach (var call in declared) Assert.Contains(ArsenalCallSites, c => c.Call == call);
    }

    [Fact]
    public void The_build_symbol_is_tested_only_in_the_switch_and_the_composition_roots()
    {
        var offenders = Sources()
            .SelectMany(s => s.Lines.Select((l, i) => (s.Path, Line: i + 1, Text: l)).Where(x => SymbolCondition.IsMatch(x.Text)))
            .Where(x => !SymbolFiles.Contains(x.Path))
            .Select(x => $"{x.Path}({x.Line}): {x.Text.Trim()}").ToList();
        Assert.True(offenders.Count == 0, "SM_EXTRAS tested outside ContentExtras.cs and the composition roots (use ContentExtras.Compiled):\n" + string.Join("\n", offenders));
    }
}
