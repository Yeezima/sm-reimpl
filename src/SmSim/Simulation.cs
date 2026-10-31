using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace SmSim;

/// <summary>Buttons sampled with an input command.</summary>
[Flags]
public enum InputButtons : ushort
{
    /// <summary>
    /// <see cref="Fire"/>: the weapon in hand's trigger. On a weapon with charged fire (protocol 20; owner's decision 2026-10-28) a
    /// press starts the charge and the release fires: before the first stage with a shot (a tap) the basic shot, after it the
    /// charged shot (<see cref="Combat.StepPlayer"/>). Bit 64 (AltFire, the secondary ability, protocols 7 to 19) is unused.
    /// </summary>
    None = 0, Jump = 1, Sprint = 2, Fire = 4, Reload = 8, Melee = 16, Switch = 32,
    /// <summary>Throws the equipment item (<see cref="ThrownSpec"/>) on a press edge (protocol 8).</summary>
    Grenade = 128,
    /// <summary>
    /// Aim mode, held (protocol 12): with a ranged weapon in hand the player moves at the aiming speeds
    /// (<see cref="SimConfig.AimRunSpeed"/>; original <c>biped_locomotion_ext/speed_aiming</c>) and cannot sprint; the client
    /// switches to the weapon's aim camera. The PC key (Left Ctrl) is from the user's knowledge of the original, not the data.
    /// </summary>
    Aim = 256,
    /// <summary>
    /// Weapon slot 1 (protocol 14), on a press edge: puts the pistol in hand, by the same switch as <see cref="Switch"/>; nothing
    /// when the pistol is already in hand or the player has none. The PC key (1) is from the user's knowledge of the
    /// original, not the data. See <see cref="Combat.StepPlayer"/> for a press of both slot buttons (and Switch) in one tick.
    /// </summary>
    SlotPistol = 512,
    /// <summary>
    /// Weapon slot 2 (protocol 14), on a press edge: puts the primary weapon (<see cref="WeaponSlot.Ranged"/>) in hand, by the
    /// same switch as <see cref="Switch"/>; nothing when it is already in hand or the player has none. PC key 2 (user's knowledge).
    /// </summary>
    SlotPrimary = 1024,
    /// <summary>
    /// The heavy melee strike (protocol 17), on a press edge: the "Stun / Alt Strike" binding (locale 110692,
    /// <c>%ABILITY_MELEE_HEAVY%</c>): alone it starts the combo's first heavy strike, during a light attack's combo window it
    /// chains into the heavy strike after that many light ones (X X Y: the second heavy strike; see <see cref="Combat.StepPlayer"/>).
    /// The PC key is not in the data (the client's choice is listed in spec/behavior.md).
    /// </summary>
    MeleeHeavy = 2048,
    /// <summary>
    /// Interact (protocol 26, fidelity gap 1), on a press edge: collects the nearest dropped pickup in reach (<see cref="World"/>,
    /// <c>Pickups.cs</c>). The original's binding is "Use / Execute" (locale 110664, <c>%ABILITY_CONTEXTINTERACT%</c>); its PC key is not in
    /// the data (the client's choice, E, is listed in spec/behavior.md). The simulation of the player itself ignores it.
    /// </summary>
    Interact = 4096,
    /// <summary>
    /// Added content's action key (extras wire 2, docs/added-content.md): the faithful simulation never reads it, and a server whose match runs no
    /// added content ignores it. Loyalist barks: the FOR THE EMPEROR key (T), honoured only with <see cref="ContentExtras.LoyalistBarks"/>.
    /// </summary>
    ExtraAction = 8192,
    /// <summary>
    /// Weapon slot 3 (protocol 44), on a press edge: puts the second primary (<see cref="WeaponSlot.Second"/>, Tactical with Weapon Versatility) in hand;
    /// nothing without one. PC key 3 (owner's decision of 2026-10-30).
    /// </summary>
    SlotSecond = 16384,
}

/// <summary>
/// One player's input for one simulation tick. Move is the stick in the player's local frame (X right, Y forward),
/// each in [-1, 1]; Yaw is the view heading in radians around the up axis (Z, as in the game files); Pitch is the aim
/// elevation in radians (positive up). ViewTick is the server tick the client was showing remote players at when it
/// sampled this input; the server rewinds targets to it for hit tests (lag compensation, capped).
/// The shot (protocol 12): ShotYawOffset and ShotPitchOffset turn its direction away from the view, in radians;
/// ShotOriginX/Y/Z move its start away from <see cref="Combat.EyePosition"/>, in metres (game space). The client fires
/// along its crosshair's camera ray (<see cref="WithShot"/>: from the ray's point nearest the eye, along the ray), so what is
/// under the crosshair is hit at any range; hit-scan shots, projectiles, thrown items and the melta cone all use it
/// (<see cref="Combat.ShotAim"/>, <see cref="Combat.ShotOrigin"/>; the server bounds both). All zero (the default) shoots
/// from the eye along the view, as before.
/// MeleeTarget (protocol 32, feel/melee-lock): the player id the client locks its melee attack or charge onto (<see cref="MeleeLock.Choose"/>),
/// 0 for none; the simulation keeps it only while <see cref="MeleeLock.Target"/> accepts it in the input's view.
/// ViewFraction (protocol 37, fix/hitboxes): how far past ViewTick toward the next tick the client was showing them (0 ≤ f &lt; 1, in 1/256 steps on the
/// wire); hit-scan shots and cones rewind the targets to that point between the two ticks (<see cref="World.Between"/>), as the client interpolates them.
/// BodyTick (protocol 43, sim/player-collision): the tick of the newest snapshot the client held when it sampled this input (0: none); the movement
/// step collides with the other players as that snapshot had them (<see cref="PlayerCollision.See"/>), on the server and in the prediction alike. On
/// the wire as the ticks back from <see cref="Tick"/> (one byte, 1 to <see cref="PlayerCollision.MaxBodyLagTicks"/>; anything else is 0).
/// </summary>
public readonly record struct PlayerInput(uint Tick, float MoveX, float MoveY, float Yaw, InputButtons Buttons, float Pitch = 0, uint ViewTick = 0,
    float ShotYawOffset = 0, float ShotPitchOffset = 0, float ShotOriginX = 0, float ShotOriginY = 0, float ShotOriginZ = 0, int MeleeTarget = 0, float ViewFraction = 0,
    uint BodyTick = 0)
{
    /// <summary>Largest shot offset a sanitized input keeps (radians; ±π, inside the wire's ±3.2767).</summary>
    public const float MaxShotOffset = MathF.PI;
    /// <summary>Largest shot-origin component a sanitized input keeps (metres; inside the wire's ±327.67 m in centimetres).</summary>
    public const float MaxShotOriginComponent = 300f;
    /// <summary>
    /// Highest view pitch a sanitized input keeps (radians): straight up, π/2 (sim/roll-fire-pitch, the owner 2026-11-01: "there should be no upper bound to the
    /// Y axis angle they can shoot at"; before, 1.4 rad, about 80 degrees).
    /// </summary>
    public const float MaxPitch = MathF.PI / 2f;
    /// <summary>Lowest view pitch a sanitized input keeps (radians; about −80 degrees; a placeholder, unchanged: the clients' camera sets limit looking down).</summary>
    public const float MinPitch = -1.4f;

    /// <summary>The shot origin's offset from the eye (game space, metres).</summary>
    public Vector3 ShotOrigin => new(ShotOriginX, ShotOriginY, ShotOriginZ);

    /// <summary>Clamps values a client could send out of range; the server applies inputs only after this.</summary>
    public PlayerInput Sanitized() => this with
    {
        MoveX = float.IsFinite(MoveX) ? Math.Clamp(MoveX, -1f, 1f) : 0f,
        MoveY = float.IsFinite(MoveY) ? Math.Clamp(MoveY, -1f, 1f) : 0f,
        Yaw = float.IsFinite(Yaw) ? Yaw % (2 * MathF.PI) : 0f,
        Pitch = float.IsFinite(Pitch) ? Math.Clamp(Pitch, MinPitch, MaxPitch) : 0f,
        ShotYawOffset = float.IsFinite(ShotYawOffset) ? Math.Clamp(ShotYawOffset, -MaxShotOffset, MaxShotOffset) : 0f,
        ShotPitchOffset = float.IsFinite(ShotPitchOffset) ? Math.Clamp(ShotPitchOffset, -MaxShotOffset, MaxShotOffset) : 0f,
        ShotOriginX = Origin(ShotOriginX), ShotOriginY = Origin(ShotOriginY), ShotOriginZ = Origin(ShotOriginZ),
        MeleeTarget = MeleeTarget is > 0 and <= ushort.MaxValue ? MeleeTarget : 0,
        ViewFraction = ViewFraction is > 0f and < 1f ? ViewFraction : 0f,   // fix/hitboxes (protocol 37); NaN fails the test
    };

    static float Origin(float v) => float.IsFinite(v) ? Math.Clamp(v, -MaxShotOriginComponent, MaxShotOriginComponent) : 0f;

    /// <summary>
    /// This input with the shot starting at <paramref name="origin"/> and going along <paramref name="direction"/> (game space):
    /// the origin is sent as its offset from <paramref name="eye"/> (<see cref="Combat.EyePosition"/> of the predicted player),
    /// the direction as offsets from the view (<see cref="WithShotDirection"/>). For the crosshair: origin = the camera ray's
    /// point nearest the eye, direction = the camera ray. The server limits the offset to <see cref="SimConfig.MaxShotOriginOffset"/>
    /// and fires from the eye when level geometry lies between the eye and the origin. A non-finite origin means the eye.
    /// </summary>
    public PlayerInput WithShot(Vector3 origin, Vector3 direction, Vector3 eye)
    {
        var offset = origin - eye;
        if (!float.IsFinite(offset.X) || !float.IsFinite(offset.Y) || !float.IsFinite(offset.Z)) offset = Vector3.Zero;
        return WithShotDirection(direction) with { ShotOriginX = offset.X, ShotOriginY = offset.Y, ShotOriginZ = offset.Z };
    }

    /// <summary>
    /// This input with its direction offsets set so the shot goes along <paramref name="direction"/> (game space, any length),
    /// from the eye (the origin offset is left as it is: zero unless set by <see cref="WithShot"/>). A zero or non-finite
    /// direction shoots along the view. The server keeps the shot within <see cref="SimConfig.ShotConeDegrees"/> of the view.
    /// </summary>
    public PlayerInput WithShotDirection(Vector3 direction)
    {
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || !float.IsFinite(direction.Z) || direction.LengthSquared() < 1e-12f)
            return this with { ShotYawOffset = 0f, ShotPitchOffset = 0f };
        float yaw = MathF.Atan2(-direction.X, direction.Y);
        float pitch = MathF.Atan2(direction.Z, new Vector2(direction.X, direction.Y).Length());
        float dy = (yaw - Yaw) % (2 * MathF.PI);
        if (dy > MathF.PI) dy -= 2 * MathF.PI; else if (dy < -MathF.PI) dy += 2 * MathF.PI;
        return this with { ShotYawOffset = dy, ShotPitchOffset = Math.Clamp(pitch - Pitch, -MaxShotOffset, MaxShotOffset) };
    }
}

/// <summary>
/// Authoritative per-player state. Positions use the game's Z-up space, in metres (assumed; see spec/behavior.md).
/// Combat fields: health and armour shield, the class and the weapon in hand (<see cref="Slot"/>) with its clip and
/// reserve, the holstered weapon's ammunition, and timers in seconds.
/// </summary>
public readonly record struct PlayerState(int PlayerId, Vector3 Position, Vector3 Velocity, float Yaw, bool Grounded)
{
    public bool Alive { get; init; }
    public float Health { get; init; }
    public float Shield { get; init; }
    /// <summary>Index into <see cref="SimConfig.Classes"/>; kept across respawns.</summary>
    public int Class { get; init; }
    /// <summary>The weapon in hand (the class's primary or pistol).</summary>
    public WeaponSlot Slot { get; init; }
    /// <summary>Clip of the weapon in hand.</summary>
    public int Clip { get; init; }
    /// <summary>Reserve of the weapon in hand.</summary>
    public int Reserve { get; init; }
    /// <summary>Clip of the holstered weapon (0 when the class has none).</summary>
    public int OtherClip { get; init; }
    /// <summary>Reserve of the holstered weapon.</summary>
    public int OtherReserve { get; init; }
    /// <summary>
    /// Protocol 44 (Weapon Versatility): the chosen second primary (index into <see cref="SimConfig.Weapons"/>, -1 none; <see cref="Loadouts.SecondIndex"/>).
    /// The clip, reserve and heat of the second holstered weapon (<see cref="Loadouts.Third"/>) are the Third fields; the first holstered one's are the Other fields.
    /// </summary>
    public int Second { get; init; } = -1;
    /// <summary>Clip of the second holstered weapon (0 without one).</summary>
    public int ThirdClip { get; init; }
    /// <summary>Reserve of the second holstered weapon.</summary>
    public int ThirdReserve { get; init; }
    /// <summary>Heat of the second holstered weapon.</summary>
    public float ThirdHeat { get; init; }
    /// <summary>Whether the second holstered weapon is overheated.</summary>
    public bool ThirdOverheated { get; init; }
    /// <summary>Seconds since the second holstered weapon last heated.</summary>
    public float ThirdSinceHeat { get; init; }
    /// <summary>The previous input's <see cref="InputButtons.SlotSecond"/> bit (protocol 44).</summary>
    public bool SlotSecondHeld { get; init; }
    /// <summary>Seconds until a weapon switch completes (0 when not switching); the weapon cannot fire or reload meanwhile.</summary>
    public float SwitchLeft { get; init; }
    /// <summary>The previous input's fire bit (a semi-automatic weapon fires on a press edge only).</summary>
    public bool FireHeld { get; init; }
    /// <summary>The previous input's switch bit (a switch starts on a press edge only).</summary>
    public bool SwitchHeld { get; init; }
    /// <summary>The previous input's <see cref="InputButtons.SlotPistol"/> bit (slot selection acts on a press edge only; protocol 14).</summary>
    public bool SlotPistolHeld { get; init; }
    /// <summary>The previous input's <see cref="InputButtons.SlotPrimary"/> bit (protocol 14).</summary>
    public bool SlotPrimaryHeld { get; init; }
    /// <summary>Seconds until the weapon may fire again.</summary>
    public float Cooldown { get; init; }
    /// <summary>Seconds left of a reload in progress (0 when not reloading).</summary>
    public float ReloadLeft { get; init; }
    /// <summary>Protocol 45 (sim/combat-pass; owner's recollection 2026-10-30): seconds left of the first holstered weapon's reload; it runs on while the weapon is not equipped.</summary>
    public float OtherReloadLeft { get; init; }
    /// <summary>
    /// Protocol 45 (sim/combat-pass, directional melee; owner's decision 2026-10-30): the stick's way of eight, relative to the view, that the melee attack in progress
    /// was struck in (<see cref="EvadeDirection.Forward"/> without an attack or a stick). The body faces the view turned by it (<see cref="Rush.DashOffset"/>).
    /// </summary>
    public EvadeDirection MeleeDirection { get; init; }
    /// <summary>Protocol 45: the same for the second holstered weapon (<see cref="ThirdClip"/> family).</summary>
    public float ThirdReloadLeft { get; init; }
    /// <summary>Seconds since this player last took damage (drives regeneration).</summary>
    public float SinceDamage { get; init; }
    /// <summary>Seconds until a dead player respawns.</summary>
    public float RespawnLeft { get; init; }
    public float Pitch { get; init; }
    /// <summary>The melee attack in progress: 0 = none, 1..N = step of <see cref="SimConfig.MeleeSteps"/>.</summary>
    public int MeleeStep { get; init; }
    /// <summary>Seconds into the current melee attack (0 when not attacking).</summary>
    public float MeleeTime { get; init; }
    /// <summary>A melee press is buffered and moves on to the next attack when the combo window opens.</summary>
    public bool MeleeQueued { get; init; }
    /// <summary>The previous input's melee bit (an attack starts on a press edge only).</summary>
    public bool MeleeHeld { get; init; }

    // ---- loadout choice (set at spawn, kept across respawns; see Loadouts) ----
    /// <summary>The chosen primary: index into <see cref="SimConfig.Weapons"/>, -1 none. A value not valid for the class means the class default.</summary>
    public int Primary { get; init; } = -1;
    /// <summary>The chosen pistol: index into <see cref="SimConfig.Weapons"/>, -1 none.</summary>
    public int Pistol { get; init; } = -1;
    /// <summary>The chosen melee combo: index into the class's <see cref="ClassLoadout.MeleeChoices"/>, -1 the class's <see cref="ClassLoadout.Melee"/>.</summary>
    public int MeleeChoice { get; init; } = -1;
    /// <summary>
    /// The perk in slot 1: index into <see cref="SimConfig.Perks"/>; one not valid for the class means the class's default perk
    /// (<see cref="Loadouts.PerkIndex"/>). In the recipient's own record since protocol 23 (M43: its prediction picks the same perk
    /// variants as the server); slim records do not carry it (others' perks act on the server only).
    /// </summary>
    public int Perk1 { get; init; } = -1;
    /// <summary>The perk in slot 2: index into <see cref="SimConfig.Perks"/>, -1 none (also when it repeats slot 1's perk). In the own record since protocol 23.</summary>
    public int Perk2 { get; init; } = -1;
    /// <summary>
    /// Rounds of a burst still to fire (M43, the stalker bolter's Burst Fire: <see cref="WeaponSpec.BurstLength"/>); 0 when no burst
    /// is under way. In the own record since protocol 23.
    /// </summary>
    public int BurstLeft { get; init; }

    // ---- heat, per weapon like the ammunition (0 for a weapon without heat; see Combat.StepSelf) ----
    /// <summary>Heat of the weapon in hand, in [0, its <see cref="HeatSpec.Overheat"/>].</summary>
    public float Heat { get; init; }
    /// <summary>The weapon in hand overheated and cannot fire (or reload) until its heat falls to <see cref="HeatSpec.Recovery"/>.</summary>
    public bool Overheated { get; init; }
    /// <summary>Seconds since the weapon in hand last gained heat (drives the cool-down delay).</summary>
    public float SinceHeat { get; init; }
    /// <summary>Heat of the holstered weapon.</summary>
    public float OtherHeat { get; init; }
    public bool OtherOverheated { get; init; }
    public float OtherSinceHeat { get; init; }

    // ---- the heavy bolter's brace and the plasma vent (fidelity gap 7; Brace, Vent; protocol 29) ----
    /// <summary>The braced stance of the weapon in hand (<see cref="SmSim.Brace"/>); None without one. Every record: slot byte bits 4..5.</summary>
    public BracePhase Brace { get; init; }
    /// <summary>Ticks of a brace transition (entering, exiting) played after this tick, the press tick counting 1; 0 otherwise. Own record: 6 bits.</summary>
    public int BraceTicks { get; init; }
    /// <summary>The weapon in hand is venting heat (<see cref="SmSim.Vent"/>). Every record: slot byte bit 6.</summary>
    public bool Venting { get; init; }

    // ---- charged fire and projectiles (see Combat.StepSelf; protocol 7) ----
    /// <summary>The previous input's Reload bit (remote detonation acts on a press edge).</summary>
    public bool ReloadHeld { get; init; }
    /// <summary>The Reload button held since a press that detonated grenades: it does not reload until released.</summary>
    public bool DetonateHeld { get; init; }
    /// <summary>The weapon in hand is charging (its charge button held since a press that started it).</summary>
    public bool Charging { get; init; }
    /// <summary>Seconds the charge has been held: ticks with the button down × the tick length (0 when not charging).</summary>
    public float ChargeTime { get; init; }
    /// <summary>A released charge's shot waiting for its fire delay: charge stage + 1 (as <see cref="Shots"/> names shots), 0 none.</summary>
    public int PendingShot { get; init; }
    /// <summary>Seconds until the pending shot leaves.</summary>
    public float PendingLeft { get; init; }
    /// <summary>
    /// Remotely detonated projectiles (grenade launcher grenades) of this player in the world, not yet set to detonate.
    /// Counted by <see cref="Combat.StepSelf"/> (a shot adds one up to the limit, a detonation clears it) and set by
    /// the world to the true count every tick.
    /// </summary>
    public int ProjectilesOut { get; init; }

    // ---- thrown equipment (see Combat.StepPlayer; protocol 8) ----
    /// <summary>The chosen thrown item: index into <see cref="SimConfig.Thrown"/>, -1 the class default (see <see cref="Loadouts.EquipmentIndex(SimConfig, PlayerState)"/>).</summary>
    public int Equipment { get; init; } = -1;
    /// <summary>Thrown items carried (refilled to the item's spawn count on respawn).</summary>
    public int Grenades { get; init; }
    /// <summary>Seconds until another throw may start (the item's cooldown, from the release).</summary>
    public float GrenadeCooldown { get; init; }
    /// <summary>A throw is in progress (the toss animation; the weapon waits).</summary>
    public bool Throwing { get; init; }
    /// <summary>Seconds into the throw (0 in the press tick).</summary>
    public float ThrowTime { get; init; }
    /// <summary>The previous input's Grenade bit (a throw starts on a press edge only).</summary>
    public bool GrenadeHeld { get; init; }
    /// <summary>
    /// perks/inert-2: seconds the combat stims' modifiers still work (<see cref="Stims"/>; set to the stim's lifetime at the use's release, counted
    /// down each tick, 0 at death and respawn). Not on the wire: the client keeps its prediction (<see cref="Stims.Carry"/>).
    /// </summary>
    public float StimLeft { get; init; }

    // ---- jump pack (see JumpPack; protocol 9). Canonical zeros for players without a pack. ----
    /// <summary>Where the player is in a jump-pack jump.</summary>
    public JumpPhase Jump { get; init; }
    /// <summary>Ticks since the jump's press (take-off to dive), or since the landing or impact (Landed, Recover); 0 otherwise. Saturates at <see cref="JumpPack.MaxTicks"/>.</summary>
    public int JumpTicks { get; init; }
    /// <summary>In the take-off and ascent: the hold in ticks (M45: in a cut ascent, <see cref="JumpCut"/>, the tick the cut counts from: the release, at the earliest the lift-off); in the hang, fall and dive: <see cref="JumpTicks"/> at the apex; else 0.</summary>
    public int JumpMark { get; init; }
    /// <summary>Jump-pack power (0 without a pack; the pack's MaxPower at spawn).</summary>
    public float JumpPower { get; init; }
    /// <summary>The previous input's Jump bit (a jump starts on a press edge only).</summary>
    public bool JumpHeld { get; init; }
    /// <summary>The Jump button has been held since the press and the hold still raises the jump.</summary>
    public bool JumpHolding { get; init; }
    /// <summary>In <see cref="JumpPhase.Landed"/>: the stick was pushed at the landing (the run landing's locks apply).</summary>
    public bool JumpLandMoving { get; init; }
    /// <summary>
    /// M45: in <see cref="JumpPhase.Ascent"/>, the Jump button was up on a tick from the lift-off on and the rise is being cut (latched until the apex; the button counts from the
    /// lift-off tick: let go and pressed again during the take-off is no cut) (<see cref="JumpPackSpec.ReleaseCut"/>); <see cref="JumpMark"/> is then the tick the cut's delay counts from. False in every other phase.
    /// </summary>
    public bool JumpCut { get; init; }
    /// <summary>sim/jump-hang: in <see cref="JumpPhase.Hang"/>, the jump is the small jump (a medium hold), which hangs <see cref="JumpPackSpec.SmallHangTime"/> longer. False in every other phase.</summary>
    public bool JumpSmall { get; init; }
    /// <summary>
    /// Protocol 30: the <see cref="JumpPhase.Dive"/> or <see cref="JumpPhase.Recover"/> is the sword slash's (<see cref="SlashSpec"/>), not the ground
    /// pound's. Protocol 31 (feel/jump-pack): in the take-off and on the lift-off tick (phase Ascent), a slash press is buffered (<see cref="JumpPack"/>). False in every
    /// other phase. Every record: slot byte bit 7 (with the pounding flag, or the jumping flag for the buffer).
    /// </summary>
    public bool JumpSlash { get; init; }

    // ---- evade roll (see Evade; protocol 13). Canonical zeros for players without an evade. ----
    /// <summary>
    /// Ticks of the evade in progress, its press tick counting 1 (so <see cref="Evade.Time"/> = EvadeTicks × tick length is
    /// the clip time played after this tick); 0 when not evading. At most <see cref="Evade.MaxTicks"/>.
    /// </summary>
    public int EvadeTicks { get; init; }
    /// <summary>The evade's clip relative to the body's facing (<see cref="Yaw"/>, fixed during the evade); Forward when not evading.</summary>
    public EvadeDirection EvadeDirection { get; init; }
    /// <summary>Ticks until another evade may start (<see cref="EvadeSpec.MinInterval"/> after a press); 0 when free.</summary>
    public int EvadeLock { get; init; }
    /// <summary>
    /// M44: the jump-pack dash (<see cref="EvadeSpec.Dash"/>) has landed and plays its landing part (<see cref="EvadeMove.Landing"/>);
    /// <see cref="EvadeTicks"/> then counts from the landing tick (1). False when not evading (protocol 24: slot byte bit 3).
    /// </summary>
    public bool EvadeLanded { get; init; }
    /// <summary>
    /// M44: during a jump-pack take-off that may still become a dash (<see cref="JumpPhase.TakeOff"/>), the dash's direction latched from the stick
    /// at the press (so a key let go a tick before Space still counts); Forward otherwise (canonical). Own record only (protocol 24: bits 5..7 of
    /// the jump byte while taking off).
    /// </summary>
    public EvadeDirection JumpDashDirection { get; init; }

    // ---- aim mode (protocol 12) ----
    /// <summary>In aim mode after this tick (<see cref="InputButtons.Aim"/> held with a ranged weapon in hand; see <see cref="Combat.StepPlayer"/>).</summary>
    public bool Aiming { get; init; }

    // ---- heavy melee strikes and hit reactions (see Combat.StepPlayer and Reaction; protocol 17) ----
    /// <summary><see cref="MeleeStep"/> indexes the combo's heavy strikes (<see cref="Loadouts.MeleeHeavy(SimConfig, PlayerState)"/>) instead of its light attacks.</summary>
    public bool MeleeHeavy { get; init; }
    /// <summary>The buffered press (<see cref="MeleeQueued"/>) is a heavy-melee press.</summary>
    public bool MeleeQueuedHeavy { get; init; }
    /// <summary>The previous input's <see cref="InputButtons.MeleeHeavy"/> bit (a heavy strike starts on a press edge only).</summary>
    public bool MeleeHeavyHeld { get; init; }
    /// <summary>The hit reaction in progress (<see cref="Reaction"/>), or None.</summary>
    public ReactionKind Reaction { get; init; }
    /// <summary>Which of the reaction's clips plays (where the hit came from).</summary>
    public ReactionDirection ReactionDirection { get; init; }
    /// <summary>Ticks of the reaction played after this tick (0 in the tick it was applied).</summary>
    public int ReactionTicks { get; init; }
    /// <summary>The knockback's direction as a yaw byte (0..255, 2π/256 steps; see <see cref="SmSim.Reaction.PushVelocity"/>).</summary>
    public int PushYaw { get; init; }
    /// <summary>The knockback's distance in 1/16 m (0..255; 0: none).</summary>
    public int PushDistance { get; init; }
    /// <summary>Over how many of the reaction's first ticks the knockback moves the body.</summary>
    public int PushTicks { get; init; }
    /// <summary>Ticks of blindness left (the krak grenade's <c>do_blindness</c>; the client draws it); 0 when not blinded.</summary>
    public int BlindTicks { get; init; }

    // ---- the charge, i.e. the bull rush (see SmSim.Rush; M29) ----
    /// <summary>The charge's phase (rush, collision stop), or None.</summary>
    public RushPhase Rush { get; init; }
    /// <summary>Ticks of the phase played after this tick (1 in its first tick).</summary>
    public int RushTicks { get; init; }
    /// <summary>Ticks until another charge may start (<see cref="RushSpec.MinInterval"/> from a start); 0 when free.</summary>
    public int RushLock { get; init; }

    // ---- the weapon raised or lowered (hip fire, M31; see Combat.StepPlayer; protocol 20) ----
    /// <summary>
    /// Ticks the weapon stays up (the ready posture) after this tick: set to <see cref="SimConfig.ReadyHoldTicks"/> by a shot, a
    /// charge, a raise, aim mode, and every tick for an always-ready weapon (<see cref="WeaponSpec.AlwaysReady"/>); counted down
    /// otherwise; 0 when lowered (by a sprint, a switch, melee, a throw, an evade, a jump's start, a reaction or the charge).
    /// </summary>
    public int ReadyTicks { get; init; }
    /// <summary>
    /// Ticks until a weapon that was down is up (<see cref="WeaponSpec.RaiseDelay"/>); 0 when not raising. A basic shot waits for it
    /// and leaves in the tick it reaches 0 (unless the trigger still holds a charge).
    /// </summary>
    public int RaiseTicks { get; init; }

    // ---- weapon spread (weapons/spread; see SmSim.Spread; not on the wire: the client keeps its predicted values, Spread.Carry) ----
    /// <summary>Degrees the scatter has bloomed above the set's initial half-widths (<see cref="Spread"/>); 0 when rested.</summary>
    public float SpreadBloom { get; init; }
    /// <summary>Seconds since the last shot (at most <see cref="Spread.Rested"/>; 0 after a reset, when the bloom is 0 too).</summary>
    public float SpreadSince { get; init; }
    /// <summary>The last shot's scatter turn, radians across (to its right) and up in the shot's frame (<see cref="Spread.Turn"/>).</summary>
    public float SpreadYaw { get; init; }
    /// <summary>See <see cref="SpreadYaw"/>.</summary>
    public float SpreadPitch { get; init; }

    // ---- teams (see World and MatchRules; protocol 10) ----
    /// <summary>The player's side, set when it joins and kept across respawns; <see cref="Team.None"/> without <see cref="MatchRules"/>.</summary>
    public Team Team { get; init; }

    // ---- input buffering (feel/buffer-speed; see SmSim.InputBuffer; protocol 33: own record only) ----
    /// <summary>A press kept for the first tick its action is allowed (<see cref="InputBuffer"/>), or None.</summary>
    public BufferedAction Buffered { get; init; }
    /// <summary>Protocol 45: the buffered heavy press was made with W held (a charge press): replayed only while W is held, never as a stun strike.</summary>
    public bool BufferedCharge { get; init; }
    /// <summary>Ticks left of the buffered press's window (0: kept only while its button is held); 0 when nothing is buffered. At most <see cref="InputBuffer.MaxTicks"/>.</summary>
    public int BufferTicks { get; init; }

    // ---- the sprint stop (feel/locks-sprint-stop; see SmSim.SprintStop; protocol 36: every record's melee byte) ----
    /// <summary>The body is slowing down from a sprint with every movement key let go (<see cref="SmSim.SprintStop"/>); false otherwise.</summary>
    public bool SprintStop { get; init; }
}

/// <summary>
/// Gameplay tuning shared by server and clients. At runtime it is read from the player's install
/// (<c>SmContent.GameplayTuning</c>: the multiplayer marine blueprints' <c>biped_locomotion_ext</c>); the defaults here are
/// neutral **placeholders** used only without an install (tests). Every field is listed in spec/behavior.md. The hash goes
/// into the handshake, so a client with different tuning is refused.
/// </summary>
public sealed partial record SimConfig
{
    /// <summary>
    /// Most classes a config may have (the content loader refuses more): snapshots carry the class in the low 6 bits of a
    /// byte, the team in the top 2 (protocol 10), so indices are 0..63.
    /// </summary>
    public const int MaxClasses = 64;

    public int TickRate { get; init; } = 30;
    /// <summary>Full stick forward (original <c>run_speed</c>).</summary>
    public float RunSpeed { get; init; } = 5.0f;
    /// <summary>Full stick sideways (original <c>strafe_speed</c>).</summary>
    public float StrafeSpeed { get; init; } = 5.0f;
    /// <summary>Full stick backward (original <c>back_speed</c>).</summary>
    public float BackSpeed { get; init; } = 5.0f;
    /// <summary>Sprinting forward (original <c>sprint_speed</c>).</summary>
    public float SprintSpeed { get; init; } = 8.0f;
    /// <summary>Original <c>walk_speed</c>; recorded, but when the original uses it is not known, so movement does not yet.</summary>
    public float WalkSpeed { get; init; } = 3.0f;
    /// <summary>Full stick forward in aim mode (original <c>speed_aiming/run_speed</c> at runtime; placeholder here).</summary>
    public float AimRunSpeed { get; init; } = 3.0f;
    /// <summary>Full stick sideways in aim mode (original <c>speed_aiming/strafe_speed</c>).</summary>
    public float AimStrafeSpeed { get; init; } = 3.0f;
    /// <summary>Full stick backward in aim mode (original <c>speed_aiming/back_speed</c>).</summary>
    public float AimBackSpeed { get; init; } = 2.0f;
    /// <summary>Time to reach the target speed on the ground (original <c>ground_time_till_max_speed</c>).</summary>
    public float GroundTimeToMaxSpeed { get; init; } = 0.125f;
    /// <summary>Cap on the acceleration (original <c>max_acceleration</c>).</summary>
    public float MaxAcceleration { get; init; } = 40f;
    /// <summary>
    /// feel/buffer-speed (the owner's request of 2026-10-29: "Movement feels a bit too fast ... slow movement including flying and jumping hopping
    /// speed by a small degree"; a **deliberate change from the original**, the factor 0.9 is **ours**): the factor the content loader has applied to
    /// the ground speeds and their acceleration cap, the jump-pack flight's horizontal speeds and air control, the jump-pack dash's and the heavy hop's
    /// travel (<c>SmContent.MovementScale</c>). Recorded here (and hashed when not 1) so a config says which speeds it carries; the simulation reads
    /// the scaled fields, never this. 1 (the default, synthetic configs): the data's speeds.
    /// </summary>
    public float MoveSpeedScale { get; init; } = 1f;
    /// <summary>
    /// feel/buffer-speed (the owner's request of 2026-10-29; ours): seconds a blocked press is kept for the first tick its action is allowed
    /// (<see cref="InputBuffer"/>; and longer while its button is held). 0 (the default, synthetic configs): no buffering, presses blocked by an
    /// action are lost as before. The content loader sets 0.3 s.
    /// </summary>
    public float InputBufferWindow { get; init; }
    /// <summary><see cref="InputBufferWindow"/> in whole ticks (rounded up; 0 off; at most <see cref="InputBuffer.MaxTicks"/>: 9 at 30 Hz for 0.3 s).</summary>
    public int InputBufferTicks => float.IsFinite(InputBufferWindow) && InputBufferWindow > 0f
        ? (int)Math.Clamp(MathF.Ceiling(InputBufferWindow / TickSeconds - 1e-3f), 1f, InputBuffer.MaxTicks) : 0;
    /// <summary>
    /// feel/locks-sprint-stop (the owner's request of 2026-10-29: "i generally feel too locked out of movement.. reduce generally the animation timings so
    /// i can jump or hop out of things more easily. mark that as a estimation for later review"; a **deliberate change from the original**, the value is an
    /// **estimate (owner's request)** to review in a playtest): the factor on every time that gates when a player may move, jump or evade out of an action
    /// (<see cref="LockTime"/>): a melee attack's <see cref="MeleeStep.MoveAt"/> and <see cref="MeleeStep.EvadeAt"/>, an evade's <see cref="EvadeMove.MoveAt"/>,
    /// <see cref="EvadeMove.NextAt"/>, a dash landing's <see cref="EvadeMove.JumpAt"/> and the evades' spacing (<see cref="EvadeSpec.MinInterval"/>), a reaction's
    /// <see cref="ReactionClip.RunAt"/> and <see cref="ReactionClip.EvadeAt"/> where the data has them before its end (never before its knockback ends), the bull rush's and its collision stop's
    /// <see cref="RushMove.MoveAt"/> and <see cref="RushMove.EvadeAt"/>, and the evade gate of a throw (<see cref="ThrowDuration"/>). Never damage or hit times,
    /// reaction and attack lengths, combo, fire, switch or melee branches, or the jump pack's own landing locks (spec/behavior.md "Action lock scale").
    /// 1 (the default, synthetic configs): the data's times. The content loader sets 0.7 (<c>GameplayTuning.OwnerLockScale</c>). Hashed when not 1.
    /// </summary>
    public float ActionLockScale { get; init; } = 1f;
    /// <summary>
    /// A move, jump or evade gate <paramref name="time"/> (seconds into an action) as this config applies it: × <see cref="ActionLockScale"/> (a scale that is
    /// not finite or not above 0 counts as 1).
    /// </summary>
    public float LockTime(float time) => float.IsFinite(ActionLockScale) && ActionLockScale > 0f && ActionLockScale != 1f ? time * ActionLockScale : time;
    /// <summary>
    /// feel/locks-sprint-stop (the owner's request of 2026-10-29: "a 'slowdown' when you stop sprinting where an animation plays of the character model slowing
    /// down from the sprint back to a stop, if movement keys are let go of entirely"; a **deliberate change** to the simulation, the curve **ours**): seconds a
    /// stop from full sprint speed takes (<see cref="SprintStop"/>): the body's speed falls as v² = v0² − 2 a t with a = <see cref="SprintSpeed"/>² / (2 × this),
    /// so a stop from <see cref="SprintSpeed"/> lasts exactly this long and covers 2/3 × <see cref="SprintSpeed"/> × this. The content loader sets the sprint
    /// stop clip's <c>BRANCH_Sprint_Finished</c> (<c>SM_NAV_Bullrush2Idle_F_01</c>: 0.625 × 1.333 = 0.833 s). 0 (the default, synthetic configs): no sprint stop
    /// (the ground acceleration rule stops the body as before). Hashed when above 0.
    /// </summary>
    public float SprintStopTime { get; init; }
    /// <summary>Placeholder; no gravity value has been found in the attribute files (likely in the Havok world setup).</summary>
    public float Gravity { get; init; } = 20f;
    /// <summary>0 disables jumping: the multiplayer marine blueprints have no jump parameters (jump packs are separate).</summary>
    public float JumpSpeed { get; init; } = 0f;

    // ---- health and armour shield (player blueprint health_ext; placeholders here, extracted values at runtime) ----
    public float HealthMax { get; init; } = 100f;
    public float HealthRegenDelay { get; init; } = 5f;
    public float HealthRegenRate { get; init; } = 5f;
    public float ShieldMax { get; init; } = 100f;
    public float ShieldRegenDelay { get; init; } = 7f;
    public float ShieldRegenRate { get; init; } = 33f;
    /// <summary>Placeholder: no respawn time found yet.</summary>
    public float RespawnTime { get; init; } = 5f;

    // ---- weapons and classes (SmContent.MpClasses at runtime) ----
    IReadOnlyList<WeaponSpec>? _weapons;
    IReadOnlyList<ClassLoadout>? _classes;
    /// <summary>
    /// The weapons classes refer to by index. Unset (no install, tests): one weapon built from the single-weapon fields
    /// below (<see cref="LegacyWeapon"/>), so those fields keep working as before.
    /// </summary>
    public IReadOnlyList<WeaponSpec> Weapons { get => _weapons ?? [LegacyWeapon]; init => _weapons = value; }
    /// <summary>
    /// The playable classes and their loadouts. Unset: one class "default" with weapon 0 as primary, no pistol, and
    /// <see cref="MeleeSteps"/> as its melee combo.
    /// </summary>
    public IReadOnlyList<ClassLoadout> Classes { get => _classes ?? [new ClassLoadout("default", 0, -1, MeleeSteps)]; init => _classes = value; }
    /// <summary>Seconds to switch between primary and pistol (placeholder; the original value is not decoded).</summary>
    public float WeaponSwitchTime { get; init; } = 0.6f;
    /// <summary>
    /// Seconds the weapon stays up after a shot, a charge or aim mode before it is lowered again (M31; <see cref="PlayerState.ReadyTicks"/>).
    /// M32: 10 s, the **owner's rough estimate, unverified (to check in the original)**: "the gun stays up after a shot for approximately
    /// 10 seconds" (2026-10-28; M31 used 1.0 s, ours). The original sets its <c>BOOL_IsReady</c> in code, not in the data read
    /// (spec/behavior.md "Hip fire").
    /// </summary>
    public float ReadyHold { get; init; } = 10f;
    /// <summary>
    /// The blind grenade blinds only a player looking at the blast (owner's decision 2026-10-28, a **deliberate change from the original**:
    /// "blind grenades are originally distance but should be facing"; <c>World.BlindBlast</c>): the angle between the player's view (yaw and
    /// pitch) and the eye-to-blast direction must be at most this half-angle, in degrees. 60 is ours (low confidence, for the playtest);
    /// 180 or more: every direction, the original's distance-only rule (SM1).
    /// </summary>
    public float BlindViewHalfAngle { get; init; } = 60f;
    /// <summary>Largest <see cref="ReadyHoldTicks"/> and <see cref="PlayerState.ReadyTicks"/>: a u16 in the own record (protocol 21).</summary>
    public const int MaxReadyTicks = ushort.MaxValue;
    /// <summary><see cref="ReadyHold"/> in whole ticks (rounded up; 1 to <see cref="MaxReadyTicks"/>; 300 at 30 Hz).</summary>
    public int ReadyHoldTicks => (int)Math.Clamp(MathF.Ceiling((float.IsFinite(ReadyHold) ? ReadyHold : 0f) / TickSeconds - 1e-3f), 1f, MaxReadyTicks);

    // ---- thrown equipment (SmContent.MpClasses at runtime) ----
    /// <summary>Thrown items classes refer to by index (<see cref="ClassLoadout.EquipmentChoices"/>); empty without an install.</summary>
    public IReadOnlyList<ThrownSpec> Thrown { get; init; } = [];
    /// <summary>
    /// Seconds from the throw press to the release (the toss sequencer's <c>Ability_Ranged_Fire</c> event; 0.2 s on the
    /// install). Placeholder here, read from the behavior graph at runtime.
    /// </summary>
    public float ThrowRelease { get; init; } = 0.2f;
    /// <summary>Seconds the throw blocks the weapon (the toss sequencer's <c>BRANCH_UseEquipment_End</c>; 0.667 s on the install). Placeholder here.</summary>
    public float ThrowDuration { get; init; } = 2f / 3f;

    // ---- perks (SmContent.MpClasses at runtime; M43: their effects are the specs' variants and PlayerUpgrades) ----
    /// <summary>The perks classes refer to by index (<see cref="ClassLoadout.PerkChoices"/>, <see cref="ClassLoadout.DefaultPerk"/>); empty without an install.</summary>
    public IReadOnlyList<PerkSpec> Perks { get; init; } = [];
    /// <summary>Most perks a config may have (M43): the own record carries a perk index in 7 bits, 127 meaning none (protocol 23), so indices are 0..126.</summary>
    public const int MaxPerks = 127;
    /// <summary>
    /// What perks' upgrade strings do to the player blueprint (M43; <c>SmContent.PlayerUpgrades</c> from the marine blueprint):
    /// received-damage multipliers, regeneration, the Iron Halo shield, death and armour-break blasts. Empty: none (tests, no install).
    /// </summary>
    public IReadOnlyList<PlayerUpgrade> PlayerUpgrades { get; init; } = [];
    /// <summary>
    /// The True Grit rule (<see cref="Loadouts.PistolPerkRule"/>): pistol-slot bolters need the True Grit perk. On by default (our
    /// default pending the owner, data-implied); false lets any class pistol be chosen without it.
    /// </summary>
    public bool PistolBoltersNeedTrueGrit { get; init; } = true;

    // ---- jump packs (SmContent.MpClasses at runtime) ----
    /// <summary>Jump packs classes refer to by index (<see cref="ClassLoadout.JumpPack"/>); empty without an install.</summary>
    public IReadOnlyList<JumpPackSpec> JumpPacks { get; init; } = [];

    // ---- evades (SmContent.MpClasses at runtime; M21) ----
    /// <summary>Evades classes refer to by index (<see cref="ClassLoadout.Evade"/>); empty without an install (Jump then does nothing without a pack).</summary>
    public IReadOnlyList<EvadeSpec> Evades { get; init; } = [];

    /// <summary>The weapon described by the single-weapon fields (the bolter's, predating loadouts).</summary>
    public WeaponSpec LegacyWeapon => new("default", WeaponSlot.Ranged, TriggerKind.FullAuto, Damage, ShotCooldown, ClipSize, ReserveSize,
        ReloadTime, MaxRange, FallOffStart, FallOffEnd, FallOffEndMultiplier, HeadMultiplier, BodyMultiplier, ShieldHeadMultiplier, ShieldBodyMultiplier);

    // ---- single weapon: the bolter (wargear sm_mp_ranged_bolter; placeholders here, extracted values at runtime).
    // Used by the simulation only through Weapons when no weapon list is set. ----
    public float Damage { get; init; } = 20f;
    public float ShotCooldown { get; init; } = 0.15f;
    public int ClipSize { get; init; } = 30;
    public int ReserveSize { get; init; } = 270;
    public float ReloadTime { get; init; } = 1.5f;
    public float MaxRange { get; init; } = 250f;
    /// <summary>Damage is ×1 up to FallOffStart, then linear to ×FallOffEndMultiplier at FallOffEnd, then constant.</summary>
    public float FallOffStart { get; init; } = 45f;
    public float FallOffEnd { get; init; } = 70f;
    public float FallOffEndMultiplier { get; init; } = 0.8f;
    public float HeadMultiplier { get; init; } = 2.5f;
    public float BodyMultiplier { get; init; } = 1f;
    public float ShieldHeadMultiplier { get; init; } = 0.5f;
    public float ShieldBodyMultiplier { get; init; } = 0.5f;

    // ---- placeholders: hit volumes, aim origin, lag compensation ----
    /// <summary>
    /// Height of the eye above the feet: where shots start before their origin offset (<see cref="Combat.EyePosition"/>).
    /// Placeholder; lower than the original rig's head (2.13 m) and camera pivot (about 2.2 m): a known gap, spec/behavior.md.
    /// </summary>
    public float EyeHeight { get; init; } = 1.6f;
    public float HeadHeight { get; init; } = 1.95f;
    public float HeadRadius { get; init; } = 0.25f;
    /// <summary>Lowest and highest points of the body capsule above the feet (its outer extents; placeholder).</summary>
    public float BodyBottom { get; init; } = 0.0f;
    public float BodyTop { get; init; } = 1.7f;
    public float BodyRadius { get; init; } = 0.45f;
    /// <summary>
    /// Hit volumes in the character frame (M20): read at runtime from the character rig's ragdoll (<c>SmContent.HitVolumeTuning</c>;
    /// spec/behavior.md). Null (the default, tests): the placeholder head sphere and body capsule from the fields above
    /// (<see cref="SmSim.HitVolumes.Placeholder"/>). Head-zone volumes take headshots; all others count as body.
    /// </summary>
    public IReadOnlyList<HitVolume>? HitVolumes { get; init; }
    /// <summary>
    /// Most ticks the server rewinds targets for a hit test. A client views targets RTT + interpolation delay (100 ms)
    /// + input lead (2 ticks) + about 1 tick in the past: 11 ticks at 150 ms RTT. 15 ticks (500 ms) covers about 250 ms RTT.
    /// </summary>
    public int MaxRewindTicks { get; init; } = 15;
    /// <summary>
    /// Most degrees a shot may turn away from the view direction (<see cref="PlayerInput.ShotYawOffset"/>; protocol 12). Ours,
    /// a sanity bound, not an anti-cheat measure (the view yaw and pitch are the client's anyway). Firing along the camera ray
    /// (<see cref="PlayerInput.WithShot"/>) keeps the direction close to the view, so it rarely applies; for a client that fires
    /// from the eye at the crosshair point instead, that point lies about atan(1 m / d) off the view for a point d metres in
    /// front of the eye (the exploration camera sits about 1 m off the eye's line): 15° at 3.8 m, 30° at 1.7 m.
    /// </summary>
    public float ShotConeDegrees { get; init; } = 30f;
    /// <summary>
    /// Longest shot-origin offset from the eye the server accepts (<see cref="PlayerInput.ShotOrigin"/>; longer ones are
    /// shortened to it). Ours: the camera ray passes about 1 m from the eye with the original exploration camera.
    /// </summary>
    public float MaxShotOriginOffset { get; init; } = 1.5f;

    // ---- character collision (used only when a level's collision is loaded) ----
    /// <summary>Radius of the player's upright collision capsule (placeholder; the original Havok shape is not decoded).</summary>
    public float CapsuleRadius { get; init; } = 0.45f;
    /// <summary>Total height of the capsule, feet to top (placeholder).</summary>
    public float CapsuleHeight { get; init; } = 1.9f;
    /// <summary>Highest ledge a grounded player steps up, and how far below a grounded player still snaps to ground (placeholder).</summary>
    public float StepHeight { get; init; } = 0.4f;
    /// <summary>Steepest walkable ground in degrees (original <c>biped_locomotion_ext/max_walkable_slope</c>: 60).</summary>
    public float MaxWalkableSlope { get; init; } = 60f;
    /// <summary>Fastest fall in m/s (placeholder; no original value found). Applies with level collision only.</summary>
    public float MaxFallSpeed { get; init; } = 60f;
    /// <summary>
    /// Protocol 30 (ours): the walk-off drop (<see cref="Movement.Step"/>) zeroes the horizontal speed only while the feet are more than this many
    /// metres above the ground below (<see cref="Movement.FallsStraight"/>); over smaller descents the body keeps its speed. Evades carried off an
    /// edge follow the same test.
    /// </summary>
    public float StraightDropAbove { get; init; } = 1f;
    /// <summary>
    /// A player whose feet fall this many metres below the lowest point of the level's character collision is killed and
    /// respawns (placeholder for the original's out-of-world handling, not decoded).
    /// </summary>
    public float KillDepth { get; init; } = 20f;

    // ---- melee (combat knife: behavior-graph timing and wargear damage at runtime via SmContent.MeleeTuning) ----
    /// <summary>
    /// Placeholder combo shaped like the combat knife (three attacks); replaced at runtime by the install's values.
    /// Shared instance, so configs built from defaults compare equal (record equality compares the list by reference).
    /// </summary>
    public static readonly IReadOnlyList<MeleeStep> PlaceholderMeleeSteps = Array.AsReadOnly(new[]
    {
        new MeleeStep(1.5f, 0.3f, 0.5f, 0.8f, 0.45f, 0.8f, 40f, 20f, 3.5f),
        new MeleeStep(1.5f, 0.3f, 0.5f, 0.8f, 0.45f, 0.8f, 40f, 20f, 3.5f),
        new MeleeStep(1.5f, 0.3f, 0.5f, 0.8f, 0.45f, 0.8f, 40f, 20f, 3.5f),
    });
    /// <summary>
    /// The attacks of the melee combo, in order (after the last, a buffered press wraps to the first: provisional). The
    /// simulation uses each class's <see cref="ClassLoadout.Melee"/>; this is the combo of the default class when
    /// <see cref="Classes"/> is unset.
    /// </summary>
    public IReadOnlyList<MeleeStep> MeleeSteps { get; init; } = PlaceholderMeleeSteps;
    /// <summary>Horizontal reach of a melee hit in metres (original wargear ability <c>max_range</c> at runtime: 4).</summary>
    public float MeleeRange { get; init; } = 4f;
    /// <summary>Half-angle in degrees around the attacker's heading inside which melee hits land (placeholder).</summary>
    public float MeleeConeDegrees { get; init; } = 60f;
    /// <summary>Largest difference in feet height, metres, between attacker and target for a melee hit (placeholder).</summary>
    public float MeleeVerticalReach { get; init; } = 1.5f;
    /// <summary>Most light or heavy steps a combo may have: a record's melee byte carries the step in 5 bits (protocol 18).</summary>
    public const int MaxMeleeSteps = 31;

    // ---- hit reactions (SmContent.ReactionTuning at runtime; M28) ----
    /// <summary>
    /// The reaction kinds' clips and branch times (the marine rig's <c>MP_SM_Reactions</c> graph at runtime); the install's
    /// values as placeholders here (<see cref="Reaction.Placeholders"/>). A kind missing from the list never starts.
    /// </summary>
    public IReadOnlyList<ReactionSpec> Reactions { get; init; } = Reaction.Placeholders;

    // ---- projectiles (ours: placeholders for behavior the files do not give; spec/behavior.md) ----
    /// <summary>Seconds after launch during which a projectile cannot hit its owner (it starts inside the owner's volumes).</summary>
    public float ProjectileOwnerGrace { get; init; } = 0.1f;
    /// <summary>Fraction of the velocity a bouncing projectile keeps (reflected about the surface normal).</summary>
    public float ProjectileRestitution { get; init; } = 0.5f;
    /// <summary>Most projectiles in the world (1 to <see cref="World.MaxProjectilesLimit"/>; values outside act as the nearest); launching one more detonates the oldest.</summary>
    public int MaxProjectiles { get; init; } = 96;

    // ---- dropped pickups (SmContent.PickupTuning at runtime; fidelity gap 1) ----
    /// <summary>What a death drops (ammunition and grenade pickups; <see cref="PickupSpec"/>); null: nothing (tests, no install).</summary>
    public PickupSpec? Pickups { get; init; }

    public float TickSeconds => 1f / TickRate;

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SimConfig, System.Runtime.CompilerServices.StrongBox<ulong>> HashCache = new();

    /// <summary>
    /// Stable 64-bit hash of every field (independent of process and platform). Computed once per config instance (fix/hitboxes; a code-review finding: the
    /// server compares it with every Hello; a config is immutable, and <c>with</c> makes a new instance with its own hash).
    /// </summary>
    public ulong Hash() => HashCache.GetValue(this, c => new System.Runtime.CompilerServices.StrongBox<ulong>(c.ComputeHash())).Value;

    ulong ComputeHash()
    {
        float[] fields =
        [
            RunSpeed, StrafeSpeed, BackSpeed, SprintSpeed, WalkSpeed, AimRunSpeed, AimStrafeSpeed, AimBackSpeed, ShotConeDegrees, MaxShotOriginOffset, GroundTimeToMaxSpeed, MaxAcceleration, Gravity, JumpSpeed,
            HealthMax, HealthRegenDelay, HealthRegenRate, ShieldMax, ShieldRegenDelay, ShieldRegenRate, RespawnTime,
            Damage, ShotCooldown, ClipSize, ReserveSize, ReloadTime, MaxRange, FallOffStart, FallOffEnd, FallOffEndMultiplier,
            HeadMultiplier, BodyMultiplier, ShieldHeadMultiplier, ShieldBodyMultiplier,
            EyeHeight, HeadHeight, HeadRadius, BodyBottom, BodyTop, BodyRadius, MaxRewindTicks,
            CapsuleRadius, CapsuleHeight, StepHeight, MaxWalkableSlope, MaxFallSpeed, KillDepth,
            MeleeRange, MeleeConeDegrees, MeleeVerticalReach, MeleeSteps.Count,
            ProjectileOwnerGrace, ProjectileRestitution, MaxProjectiles,
            .. MeleeSteps.SelectMany(m => new[] { m.Duration, m.HitTime, m.ComboOpen, m.ComboClose, m.RangedAt, m.MoveAt, m.Damage, m.SecondaryDamage, m.Lunge, m.HealthMultiplier, m.ShieldMultiplier }),
        ];
        var buffer = new byte[4 + 4 * fields.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, TickRate);
        for (int i = 0; i < fields.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(4 + 4 * i), fields[i]);
        // Weapons and classes (BinaryWriter is little-endian on every platform; strings are length-prefixed UTF-8).
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(buffer);
            w.Write(WeaponSwitchTime);
            w.Write(ReadyHold);
            var weapons = Weapons;
            w.Write(weapons.Count);
            foreach (var x in weapons) x.WriteHash(w);
            void Steps(IReadOnlyList<MeleeStep> steps) => MeleeCombo.WriteSteps(w, steps);
            var classes = Classes;
            w.Write(classes.Count);
            foreach (var k in classes)
            {
                w.Write(k.Name); w.Write(k.Primary); w.Write(k.Pistol);
                Steps(k.Melee); Steps(k.MeleeHeavy); RushSpec.Write(w, k.MeleeRush);
                w.Write(k.PrimaryChoices.Count); foreach (int i in k.PrimaryChoices) w.Write(i);
                w.Write(k.PistolChoices.Count); foreach (int i in k.PistolChoices) w.Write(i);
                w.Write(k.MeleeChoices.Count);
                foreach (var combo in k.MeleeChoices) MeleeCombo.Write(w, combo);
                w.Write(k.EquipmentChoices.Count); foreach (int i in k.EquipmentChoices) w.Write(i);
                w.Write(k.JumpPack);
                w.Write(k.Evade);
                w.Write((byte)k.Side); w.Write(k.Role);
                w.Write(k.PerkChoices.Count); foreach (int i in k.PerkChoices) w.Write(i);
                w.Write(k.DefaultPerk);
                if (k.Body >= 0) { w.Write("body"); w.Write(k.Body); }   // Ork O4: only for a class with a body of its own (older hashes unchanged)
                if (k.Race != Race.None) { w.Write("race"); w.Write((byte)k.Race); }   // Ork O5: only for a class with a race of its own (older hashes unchanged)
            }
            w.Write(Perks.Count); foreach (var p in Perks) PerkSpec.Write(w, p);
            w.Write(PistolBoltersNeedTrueGrit);
            w.Write(PlayerUpgrades.Count); foreach (var u in PlayerUpgrades) u.Write(w);
            w.Write(Thrown.Count); foreach (var t in Thrown) ProjectileSpec.Write(w, t);
            w.Write(ThrowRelease); w.Write(ThrowDuration);
            w.Write(JumpPacks.Count); foreach (var j in JumpPacks) j.Write(w);
            w.Write(Evades.Count); foreach (var e in Evades) e.Write(w);
            w.Write(Reactions.Count); foreach (var r in Reactions) r.Write(w);
            // Hit volumes (M20): -1 for the placeholders (hashed through their fields above).
            WriteVolumes(w, HitVolumes);
            PickupSpec.Write(w, Pickups);   // fidelity gap 1 (protocol 26)
            // Ork O4: class bodies, only when there are any (a config without them hashes as before).
            if (Bodies.Count > 0) { w.Write("bodies"); w.Write(Bodies.Count); foreach (var b in Bodies) b.Write(w); }
            w.Write("blind-view"); w.Write(BlindViewHalfAngle);   // owner's decision 2026-10-28: the blind grenade by facing
            if (!PlayerBodies) w.Write("no-player-bodies");   // sim/player-collision: only when off (older hashes unchanged)
            if (!ChargeLock) w.Write("no-charge-lock");   // anim/melee-interaction: only when off (older hashes unchanged)
            if (StraightDropAbove != 1f) { w.Write("straight-drop"); w.Write(StraightDropAbove); }   // protocol 30: only when not the default (older hashes unchanged)
            if (MeleeLock != MeleeLockSpec.Default) { w.Write("melee-lock"); MeleeLock.Write(w); }   // feel/melee-lock: only when not the default (older hashes unchanged)
            if (FlightHitVolumes is { } flight) { w.Write("flight-volumes"); flight.Write(w); }   // fix/hitboxes (protocol 37): only when set (older hashes unchanged)
            if (MoveSpeedScale != 1f) { w.Write("move-scale"); w.Write(MoveSpeedScale); }   // feel/buffer-speed: only when not the default (older hashes unchanged)
            if (InputBufferTicks > 0) { w.Write("input-buffer"); w.Write(InputBufferWindow); }   // feel/buffer-speed: only when on
            if (ActionLockScale != 1f) { w.Write("lock-scale"); w.Write(ActionLockScale); }   // feel/locks-sprint-stop: only when not the default (older hashes unchanged)
            if (SprintStopTime > 0f) { w.Write("sprint-stop"); w.Write(SprintStopTime); }   // feel/locks-sprint-stop: only when on
            if (MeleeLungeScale != 1f) { w.Write("lunge-scale"); w.Write(MeleeLungeScale); }   // feel/melee-charge-2: only when not the default (older hashes unchanged)
            // Added content (ContentExtras.cs): only when any is on, so a faithful config hashes byte for byte as before the switch.
            if (Extras != ContentExtras.None) { w.Write("extras"); w.Write((uint)Extras); w.Write(ContentExtras.WireVersion); ExtrasHash(w); }   // EXTRAS-HOOK
        }
        return BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(stream.ToArray()));
    }

    /// <summary>Writes hit volumes for a config hash: their count (-1 for none: the placeholders) and every field.</summary>
    internal static void WriteVolumes(BinaryWriter w, IReadOnlyList<HitVolume>? volumes)
    {
        w.Write(volumes?.Count ?? -1);
        foreach (var v in volumes ?? [])
        {
            w.Write(v.Name); w.Write((byte)v.Zone); w.Write((byte)v.Kind);
            foreach (float f in new[] { v.A.X, v.A.Y, v.A.Z, v.B.X, v.B.Y, v.B.Z, v.Radius, v.Rotation.X, v.Rotation.Y, v.Rotation.Z, v.Rotation.W }) w.Write(f);
            w.Write(v.Planes?.Count ?? 0); foreach (var q in v.Planes ?? []) { w.Write(q.X); w.Write(q.Y); w.Write(q.Z); w.Write(q.W); }
            w.Write(v.Vertices?.Count ?? 0); foreach (var q in v.Vertices ?? []) { w.Write(q.X); w.Write(q.Y); w.Write(q.Z); }
        }
    }
}

/// <summary>
/// Player movement for one tick: a pure function shared by the server and by client prediction, so a client that
/// has the same inputs reproduces the server exactly. Without a <see cref="CollisionWorld"/> a flat ground plane at z = 0
/// stands in for the level; with one, the capsule controller in <see cref="CharacterMotor"/> walks on its character mesh for
/// the player's side (<see cref="CollisionWorld.CharacterFor"/>: the other side's spawn-room blockers are solid boxes in it).
/// Speeds per direction are the original values when <see cref="SimConfig"/> comes from the install; how they blend
/// between directions and how acceleration uses the two original timing values is a **provisional approximation**.
/// </summary>
public static class Movement
{
    /// <param name="speedScale">Multiplies the wished speed (a charge stage's <c>speed_maximum</c> modifier; 1 otherwise).</param>
    /// <param name="aiming">Aim mode: the aiming speeds apply and Sprint is ignored (interpretation; spec/behavior.md).</param>
    public static PlayerState Step(PlayerState s, PlayerInput input, SimConfig c, CollisionWorld? world = null, float speedScale = 1f, bool aiming = false)
    {
        c = c.For(s.Class);   // Ork O4: the class's own speeds
        input = input.Sanitized();
        float dt = c.TickSeconds;
        var forward = new Vector2(-MathF.Sin(input.Yaw), MathF.Cos(input.Yaw));   // yaw 0 faces +Y
        var right = new Vector2(forward.Y, -forward.X);
        var stick = new Vector2(input.MoveX, input.MoveY);
        if (stick.LengthSquared() > 1f) stick = Vector2.Normalize(stick);
        // Directional speeds on an ellipse: forward run (or sprint), backward back speed, sideways strafe speed; in aim
        // mode the aiming table's run, back and strafe speeds and no sprint.
        float along = aiming ? (stick.Y >= 0 ? c.AimRunSpeed : c.AimBackSpeed)
            : stick.Y >= 0 ? ((input.Buttons & InputButtons.Sprint) != 0 ? c.SprintSpeed : c.RunSpeed) : c.BackSpeed;
        var wish = (right * (stick.X * (aiming ? c.AimStrafeSpeed : c.StrafeSpeed)) + forward * (stick.Y * along)) * speedScale;

        var horizontal = new Vector2(s.Velocity.X, s.Velocity.Y);
        if (!s.Grounded)
        {
            // Protocol 30, the walk-off drop (the owner's knowledge of SM1, 2026-10-28: "when a player walks off a ledge he drops as if gravity
            // pulls him straight down"): a body in the air without a jump-pack flight (it walked or sprinted off an edge, or an evade, reaction or
            // charge stop left it there) is not steered by the stick and, once it falls (vertical speed not 0), has no horizontal velocity
            // (zeroed, not decayed) until it lands. The first tick in the air (vertical speed still 0: the motor sets it to 0 on the ground) keeps
            // the velocity it left with, so the body clears the edge by one tick (ours; 0.33 m at sprint speed) and a capsule wedged between
            // steep faces, which the motor counts as standing again after that tick, still walks along them. Gravity is unchanged.
            // Only over a real drop (ours, chosen in the code review of protocol 30): while the feet are no more than SimConfig.StraightDropAbove above the ground below
            // (a curb, a stair riser, a crate edge the motor does not step down), the body keeps the velocity it left with and lands running.
            if (s.Velocity.Z != 0f && horizontal != Vector2.Zero && FallsStraight(s, c, world)) horizontal = Vector2.Zero;
        }
        else
        {
            var delta = wish - horizontal;
            // Rate = the largest of run, target and current speed over GroundTimeToMaxSpeed, capped by MaxAcceleration.
            // Starting and stopping at run speed take exactly that time with no exponential tail; stopping from a sprint takes
            // a little longer (the rate follows the speed down to run speed); reversals cover more speed and take longer.
            float span = Math.Max(c.RunSpeed, Math.Max(wish.Length(), horizontal.Length()));
            float rate = Math.Min(span / Math.Max(c.GroundTimeToMaxSpeed, 1e-3f), c.MaxAcceleration);
            float maxChange = rate * dt;
            if (delta.Length() > maxChange) delta = Vector2.Normalize(delta) * maxChange;
            horizontal += delta;
        }

        float vz = s.Velocity.Z;
        bool grounded = s.Grounded;
        if (grounded && c.JumpSpeed > 0f && (input.Buttons & InputButtons.Jump) != 0) { vz = c.JumpSpeed; grounded = false; }
        // sim/player-collision (protocol 43): the other players' capsules (PlayerCollision), inside the world's or the prediction's scope only.
        if (PlayerCollision.Current is { } bodies && s.Alive)
        {
            var plan = PlayerCollision.Resolve(s, horizontal, c, bodies);
            return PlayerCollision.Finish(s, Walk(s, input.Yaw, plan.Motor, vz, grounded, c, world), plan, c, bodies, world is not null);
        }
        PlayerCollision.LastPush = Vector2.Zero;
        return Walk(s, input.Yaw, horizontal, vz, grounded, c, world);
    }

    /// <summary>The walking step's move with its velocities decided: against the level with the capsule controller, else over the flat ground.</summary>
    static PlayerState Walk(PlayerState s, float yaw, Vector2 horizontal, float vz, bool grounded, SimConfig c, CollisionWorld? world)
    {
        if (world is not null) return CharacterMotor.Step(world.OutOfBlockers(s, c), yaw, horizontal, vz, grounded, c, world.CharacterFor(s.Team));
        float dt = c.TickSeconds;
        if (!grounded) vz -= c.Gravity * dt;

        var position = s.Position + new Vector3(horizontal.X, horizontal.Y, vz) * dt;
        if (position.Z <= 0f) { position.Z = 0f; vz = 0f; grounded = true; }
        return s with { Position = position, Velocity = new Vector3(horizontal.X, horizontal.Y, vz), Yaw = yaw, Grounded = grounded };
    }

    /// <summary>
    /// Protocol 30 (ours, chosen in code review, 2026-10-29): whether a body in the air drops straight down (the walk-off rule): its feet are more than
    /// <see cref="SimConfig.StraightDropAbove"/> above the ground below them (<see cref="JumpPack.AboveGround"/>: a ray down on the level's
    /// character collision, else the flat ground at z = 0). Small descents the motor does not follow as ground (a curb or a riser above the step
    /// height, a crest steeper than the walkable slope) keep the body's speed.
    /// </summary>
    public static bool FallsStraight(PlayerState s, SimConfig c, CollisionWorld? world) => JumpPack.AboveGround(s, world) > c.StraightDropAbove;

    /// <summary>
    /// One tick with a given horizontal velocity and no acceleration rule (the evade roll's motion, M21): on the ground or in
    /// the air (gravity applies, as when walking off a ledge), against the level with the capsule controller, else over the
    /// flat ground at z = 0. Sets the yaw.
    /// </summary>
    public static PlayerState Glide(PlayerState s, float yaw, Vector2 horizontal, SimConfig c, CollisionWorld? world = null)
    {
        if (PlayerCollision.Current is { } bodies && s.Alive)   // sim/player-collision (protocol 43)
        {
            var plan = PlayerCollision.Resolve(s, horizontal, c, bodies);
            return PlayerCollision.Finish(s, GlideAlone(s, yaw, plan.Motor, c, world), plan, c, bodies, world is not null);
        }
        PlayerCollision.LastPush = Vector2.Zero;
        return GlideAlone(s, yaw, horizontal, c, world);
    }

    static PlayerState GlideAlone(PlayerState s, float yaw, Vector2 horizontal, SimConfig c, CollisionWorld? world)
    {
        if (world is not null) return CharacterMotor.Step(world.OutOfBlockers(s, c), yaw, horizontal, s.Velocity.Z, s.Grounded, c, world.CharacterFor(s.Team));
        float dt = c.TickSeconds;
        float vz = s.Grounded ? 0f : s.Velocity.Z - c.Gravity * dt;
        bool grounded = s.Grounded;
        var position = s.Position + new Vector3(horizontal.X, horizontal.Y, vz) * dt;
        if (position.Z <= 0f) { position.Z = 0f; vz = 0f; grounded = true; }
        return s with { Position = position, Velocity = new Vector3(horizontal.X, horizontal.Y, vz), Yaw = yaw, Grounded = grounded };
    }

    /// <summary>
    /// One airborne tick with a given horizontal velocity, vertical speed and gravity (jump-pack flight): against the level
    /// with the capsule controller, else over the flat ground at z = 0 (reaching it lands).
    /// </summary>
    public static PlayerState Fly(PlayerState s, float yaw, Vector2 horizontal, float vz, float gravity, SimConfig c, CollisionWorld? world = null)
    {
        if (PlayerCollision.Current is { } bodies && s.Alive)   // sim/player-collision (protocol 43)
        {
            var plan = PlayerCollision.Resolve(s, horizontal, c, bodies);
            return PlayerCollision.Finish(s, FlyAlone(s, yaw, plan.Motor, vz, gravity, c, world), plan, c, bodies, world is not null);
        }
        PlayerCollision.LastPush = Vector2.Zero;
        return FlyAlone(s, yaw, horizontal, vz, gravity, c, world);
    }

    static PlayerState FlyAlone(PlayerState s, float yaw, Vector2 horizontal, float vz, float gravity, SimConfig c, CollisionWorld? world)
    {
        if (world is not null) return CharacterMotor.Step(world.OutOfBlockers(s, c), yaw, horizontal, vz, false, c, world.CharacterFor(s.Team), gravity);
        float dt = c.TickSeconds;
        vz -= gravity * dt;
        var position = s.Position + new Vector3(horizontal.X, horizontal.Y, vz) * dt;
        bool grounded = false;
        if (position.Z <= 0f && vz <= 0f) { position.Z = 0f; vz = 0f; grounded = true; }
        return s with { Position = position, Velocity = new Vector3(horizontal.X, horizontal.Y, vz), Yaw = yaw, Grounded = grounded };
    }
}

/// <summary>Where a player (re)spawns, facing <see cref="Yaw"/> (radians, 0 faces +Y).</summary>
public readonly record struct SpawnPoint(Vector3 Position, float Yaw);

/// <summary>
/// A resolved shot or melee hit (<paramref name="Melee"/>; zone Body, distance horizontal), kept for the current tick
/// (diagnostics, tests, hit feedback).
/// </summary>
/// <param name="Projectile">The projectile whose direct hit or detonation this is (0: a ray or melee).</param>
/// <param name="Splash">Blast damage (<see cref="SplashSpec"/>); Distance is then from the blast centre.</param>
/// <param name="Weapon">The weapon that dealt it: index into <see cref="SimConfig.Weapons"/>, or into <see cref="SimConfig.Thrown"/> with
/// <paramref name="ThrownItem"/>; -1 for melee, jump-pack blasts and unknown (protocol 10: the kill feed names it).</param>
/// <param name="Direction">The way the hit pushed the target (M23; unit vector, world space; zero: unknown): a ray's or a projectile's
/// direction, from the shooter's shot origin to the target's body centre for a cone, from the blast point to the body centre
/// for a blast (straight up when they coincide), from the attacker to the target (the attacker's heading when they overlap) for melee.</param>
/// <param name="Armoured">The target's armour shield was up (above 0) when the hit landed, before it took anything (protocol 22, hit
/// feedback: armour hits and flesh hits differ; see <see cref="ShotEvent.Armoured"/>).</param>
/// <param name="Perk">A perk's blast dealt it (M43; index into <see cref="SimConfig.Perks"/>; the kill feed names the perk), else -1.</param>
public readonly record struct HitEvent(uint Tick, int Shooter, int Target, HitZone Zone, float Distance, float ShieldDamage, float HealthDamage, bool Killed, bool Melee = false,
    int Projectile = 0, bool Splash = false, bool Cone = false, bool Landing = false, int Weapon = -1, bool ThrownItem = false, Vector3 Direction = default,
    bool Armoured = false, int Perk = -1);

/// <summary>
/// The authoritative world: players, the current tick, a short history of positions for lag compensation, and the hits
/// resolved in the last tick. With <paramref name="collision"/>, players move against the level and shots stop at it;
/// with <paramref name="spawns"/>, players spawn at those points. No engine types.
/// </summary>
public sealed partial class World(SimConfig config, CollisionWorld? collision = null, IReadOnlyList<SpawnPoint>? spawns = null)
{
    readonly Dictionary<int, PlayerState> _players = new();
    readonly Dictionary<uint, Dictionary<int, PlayerState>> _history = new();
    readonly List<HitEvent> _hits = new();
    readonly SpawnPoint[] _spawns = spawns?.ToArray() ?? [];
    int _spawnCursor;

    public SimConfig Config { get; } = config;
    public CollisionWorld? Collision { get; } = collision;
    public IReadOnlyList<SpawnPoint> Spawns => _spawns;
    public uint Tick { get; private set; }
    public IReadOnlyDictionary<int, PlayerState> Players => _players;
    /// <summary>Shots that hit someone during the last <see cref="Step"/>.</summary>
    public IReadOnlyList<HitEvent> LastHits => _hits;

    /// <summary>
    /// Adds a player of class <paramref name="playerClass"/> (index into <see cref="SimConfig.Classes"/>; the caller
    /// validates it) with the loadout chosen by the three requests (indices into the class's choice lists; negative or out
    /// of range: the class default; see <see cref="Loadouts.Choose"/>). Class and loadout are kept across respawns.
    /// </summary>
    /// <remarks>Perk choices index the class's <see cref="ClassLoadout.PerkChoices"/> (see <see cref="Loadouts.Choose"/>). A pending
    /// loadout change of the id (<see cref="SetNextLoadout"/>) is dropped. <paramref name="team"/> (Ork O4): the side for a class without
    /// one (the Ork Boy; <see cref="AssignSide"/>); ignored for a class with a side and without rules; null: the smaller side.</remarks>
    public PlayerState Spawn(int playerId, int playerClass = 0, int primaryChoice = -1, int pistolChoice = -1, int meleeChoice = -1, int equipmentChoice = -1,
        int perk1Choice = -1, int perk2Choice = -1, Team? team = null, int secondChoice = -1)
    {
        _players.Remove(playerId);   // a re-spawned id is counted afresh for sides
        _nextLoadouts.Remove(playerId);
        _damageScale.Remove(playerId);   // test aid (--bot-damage-scale): a new join of the id starts unscaled
        ForgetFlightView(playerId);
        var classSide = playerClass >= 0 && playerClass < Config.Classes.Count ? Config.Classes[playerClass].Side : Team.None;
        var side = Rules is not null && classSide == Team.None && team is Team.SpaceMarines or Team.Chaos ? team.Value : TeamFor(playerClass);
        var (position, yaw) = TryStartAreaSpawn(playerId, side, out var areaAt, out var areaYaw) ? (areaAt, areaYaw)
            : _spawns.Length > 0 ? ChooseSpawn(playerId, side) : (PlaceholderSpawn(playerId), 0f);
        var chosen = Loadouts.Choose(Config, new PlayerState(playerId, position, Vector3.Zero, yaw, true) { Class = playerClass, Team = side }, primaryChoice, pistolChoice, meleeChoice, equipmentChoice,
            perk1Choice, perk2Choice, secondChoice);
        var state = Combat.Respawned(chosen, Config);
        _players[playerId] = state;
        _scores[playerId] = default;
        return state;
    }

    /// <summary>
    /// Rotates through the spawn points, skipping any within 2 m of another living player when a free one exists
    /// (provisional rule: the original's spawn selection is not decoded). With <see cref="Rules"/> and living enemies, the
    /// free point farthest from the nearest living enemy is taken instead (provisional; see <see cref="FarthestFromEnemies"/>).
    /// </summary>
    (Vector3 Position, float Yaw) ChooseSpawn(int playerId, Team team = Team.None)
    {
        if (FarthestFromEnemies(playerId, team) is int far)
        {
            _spawnCursor = (far + 1) % _spawns.Length;
            return (_spawns[far].Position, _spawns[far].Yaw);
        }
        int chosen = -1;
        for (int k = 0; k < _spawns.Length; k++)
        {
            int i = (_spawnCursor + k) % _spawns.Length;
            if (!Occupied(_spawns[i].Position, playerId)) { chosen = i; break; }
        }
        // sim/player-collision: every point taken: the least crowded one (it was the cursor's, possibly on top of someone).
        if (chosen < 0) chosen = LeastCrowded(_spawns.Select(p => p.Position).ToArray(), _spawnCursor, playerId);
        _spawnCursor = (chosen + 1) % _spawns.Length;
        return (_spawns[chosen].Position, _spawns[chosen].Yaw);
    }

    bool Occupied(Vector3 point, int self)
    {
        foreach (var (id, p) in _players)
            if (id != self && p.Alive && Vector3.DistanceSquared(p.Position, point) < 4f) return true;
        return false;
    }

    /// <summary>
    /// Spread spawns on a circle so players start apart (used when no spawn points are given). sim/player-collision: a spot within two capsule
    /// radii of a living player (ids 13 apart land 0.65 m apart on the 3 m circle) moves out to a wider circle, 1 m at a time (ours).
    /// </summary>
    Vector3 PlaceholderSpawn(int playerId)
    {
        float angle = playerId * 2.399963f;
        var at = new Vector3(MathF.Cos(angle) * 3f, MathF.Sin(angle) * 3f, 0f);
        float gap = 2f * MathF.Max(Config.CapsuleRadius, 0.05f) + 0.1f;
        for (int ring = 1; ring <= 16 && _players.Any(kv => kv.Key != playerId && kv.Value.Alive && Vector3.Distance(kv.Value.Position, at) < gap); ring++)
            at = new Vector3(MathF.Cos(angle) * (3f + ring), MathF.Sin(angle) * (3f + ring), 0f);
        return at;
    }

    /// <summary>Removes the player and its projectiles (projectiles stuck to it stay where they are).</summary>
    public void Remove(int playerId)
    {
        _players.Remove(playerId);
        _jumpApex.Remove(playerId);
        _scores.Remove(playerId);
        _nextLoadouts.Remove(playerId);
        _damageScale.Remove(playerId);
        ForgetFlightView(playerId);
        RemoveProjectilesOf(playerId);
        _perkBlasts.RemoveAll(b => b.Owner == playerId);
    }

    // ---- loadout changes at respawn (custom classes; docs/customizer-architecture.md section 4) ----
    readonly Dictionary<int, LoadoutChoice> _nextLoadouts = new();
    readonly List<int> _respawns = new();

    /// <summary>Players who respawned during the last <see cref="Step"/> (death timer or a match restart), in the order they did.</summary>
    public IReadOnlyList<int> LastRespawns => _respawns;

    /// <summary>
    /// Asks for player <paramref name="playerId"/>'s loadout to change at its next respawn (our default: never mid-life; a later call
    /// replaces an earlier one). The request is resolved now: the class (-1 or out of range: the player's current class) is mapped
    /// to the player's side by role (<see cref="ClassForSide"/>), then every index is checked against that class's lists
    /// (<see cref="Loadouts.Resolve"/>: invalid ones and a duplicate perk become -1, the default; the True Grit rule applies).
    /// The change is consumed at the next respawn by the death timer (<see cref="Step"/>) or a match restart. Returns the
    /// resolved request, or null when the player is not in the world.
    /// </summary>
    public LoadoutChoice? SetNextLoadout(int playerId, LoadoutChoice choice)
    {
        if (!_players.TryGetValue(playerId, out var p)) return null;
        // Added content E2: a class the profile does not have (the Ork Boy without the Orks, or one beyond the config) is refused: the player keeps its class and none of
        // the item choices asked for it apply (they are that class's lists), so they are reset to the defaults.
        if (choice.Class >= 0 && !ContentGate.ClassOffered(Config, choice.Class)) choice = LoadoutChoice.Default;
        int playerClass = choice.Class >= 0 && choice.Class < Config.Classes.Count ? choice.Class : p.Class;
        var resolved = Loadouts.Resolve(Config, ClassForSide(playerClass, p.Team), choice);
        _nextLoadouts[playerId] = resolved;
        return resolved;
    }

    /// <summary>The loadout change waiting for the player's next respawn (resolved), or null.</summary>
    public LoadoutChoice? NextLoadout(int playerId) => _nextLoadouts.TryGetValue(playerId, out var c) ? c : null;

    /// <summary>The player with its waiting loadout change applied (and consumed); unchanged without one.</summary>
    PlayerState WithNextLoadout(PlayerState p)
    {
        if (!_nextLoadouts.Remove(p.PlayerId, out var c)) return p;
        return Loadouts.Choose(Config, p with { Class = c.Class }, c.Primary, c.Pistol, c.Melee, c.Equipment, c.Perk1, c.Perk2, c.Second);
    }

    /// <summary>Shots that left a barrel during the last <see cref="Step"/> (basic and charged, rays and projectiles).</summary>
    public IReadOnlyList<ShotEvent> LastShots => _shots;
    readonly List<ShotEvent> _shots = new();

    /// <summary>
    /// Advances one tick using each player's input for that tick, then: handles remote detonation requests, launches the
    /// projectiles fired and the items thrown in it (in player order), applies lingering blasts that are due, moves every
    /// projectile and detonates those due, resolves the hit-scan and cone shots, then melee hits, and finally sets each
    /// player's <see cref="PlayerState.ProjectilesOut"/> to the true count.
    /// </summary>
    public void Step(Func<int, PlayerInput> inputFor)
    {
        _history[Tick] = new Dictionary<int, PlayerState>(_players);   // the world as clients saw it at this tick
        KeepFlightViews();   // fix/hitboxes: the jump poses shown at this tick
        _history.Remove(Tick - (uint)Math.Max(Config.MaxRewindTicks, 1) - 2);
        Tick++;
        _hits.Clear();
        _shots.Clear();
        _reactions.Clear();
        _blindings.Clear();
        _rushes.Clear();
        _respawns.Clear();
        _pickupSpawns.Clear(); _pickupEvents.Clear();   // fidelity gap 1 (Pickups.cs)
        BeginProjectileTick();
        var aliveBefore = BeginMatchTick();
        var shots = new List<(int Shooter, PlayerInput Input, PlayerState State, WeaponSpec Weapon, int Stage, int Event)>();
        var launches = new List<(int Shooter, PlayerState State, PlayerInput Input, int Weapon, int Stage, ProjectileSpec Projectile, bool Thrown, WeaponSpec? Source)>();
        var detonations = new List<int>();
        var swings = new List<(int Attacker, PlayerInput Input, PlayerState State, int Step, bool Heavy)>();
        var slashes = new List<(int Attacker, PlayerInput Input, PlayerState State, MeleeStep Step)>();   // protocol 30: the jump pack's sword slash
        var rushes = new List<(int Player, PlayerInput Input, PlayerState Before, PlayerState State)>();
        Dictionary<int, RushPhase>? landedCharges = null;   // feel/melee-charge-2: chargers whose landing ended the charge this tick (their contacts still count)
        var jumps = new List<(int Player, JumpEventKind Kind, PlayerState State, float Drop)>();
        _jumps.Clear();
        foreach (var id in _players.Keys.ToList())
        {
            var input = (State == MatchState.Countdown ? MatchFlow.Locked(inputFor(id)) : inputFor(id)).Sanitized() with { Tick = Tick };   // no attacks before the match begins (ours)
            NoteInteract(id, input);   // fidelity gap 1: pickups (Pickups.cs)
            PlayerState next;
            SelfEvents events;
            using (PlayerCollision.Use(BodiesFor(id, input)))   // sim/player-collision: the others' capsules as this player's client knew them (WorldBodies.cs)
                next = Combat.StepPlayer(_players[id], input, Config, out events, Collision, SeenBy(input));   // feel/melee-lock: the lock in the attacker's view
            bool fired = events.Fired;
            int meleeHitStep = events.MeleeHitStep;
            var before = _players[id];
            if (Collision is not null && next.Alive && (next.Position.Z < Collision.Character.Min.Z - Config.KillDepth
                    || (before.Alive && Collision.KillsPlayer(before.Position, next.Position, Config.CapsuleHeight))))
                next = Combat.Killed(next, Config);   // fell out of the level, or into one of its kill volumes (M18)
            if (!next.Alive && next.RespawnLeft <= 0f)
            {
                next = WithNextLoadout(next);   // a loadout change waiting for this respawn
                _respawns.Add(id);
                if (TryHomerSpawn(next, out var homerAt, out var homerYaw))   // Teleport Homer: next to a living carrier on the side
                    next = Combat.Respawned(next with { Position = homerAt, Yaw = homerYaw }, Config);
                else if (TryStartAreaSpawn(id, next.Team, out var areaAt, out var areaYaw))
                    next = Combat.Respawned(next with { Position = areaAt, Yaw = areaYaw }, Config);
                else if (_spawns.Length > 0)
                {
                    var (position, yaw) = ChooseSpawn(id, next.Team);
                    next = Combat.Respawned(next with { Position = position, Yaw = yaw }, Config);
                }
                else next = Combat.Respawned(next with { Position = PlaceholderSpawn(id) }, Config);
            }
            _players[id] = next;
            // A shot leaves from the weapon in hand (a switch never starts in a tick that fires).
            if (fired && next.Alive && Loadouts.InHand(Config, next) is { } weapon)   // a player who fell out of the level this tick does not shoot
            {
                int weaponIndex = Loadouts.WeaponIndex(Config, next, next.Slot);
                if (Shots.ProjectileOf(weapon, events.ShotStage) is { } projectile) launches.Add((id, next, input, weaponIndex, events.ShotStage, projectile, false, weapon));
                else
                {
                    shots.Add((id, input, next, weapon, events.ShotStage, _shots.Count));
                    _shots.Add(new ShotEvent(Tick, id, weaponIndex, events.ShotStage, 0));   // where it went is filled in when it resolves
                }
            }
            // A grenade released this tick (the throw spent it; a player who fell out of the level this tick throws nothing).
            if (events.Threw && next.Alive && Loadouts.EquipmentIndex(Config, next) is var item and >= 0 && Loadouts.Equipment(Config, next)!.Stim is null)   // perks/inert-2: a stim launches nothing
                launches.Add((id, next, input, item, 0, Loadouts.Equipment(Config, next)!.Projectile, true, null));   // M43: the perk variant's projectile
            if (events.Detonate && next.Alive) detonations.Add(id);
            if (meleeHitStep > 0 && next.Alive) swings.Add((id, input, next, meleeHitStep, events.MeleeHeavy));
            if (events.SlashHit && next.Alive && Loadouts.JumpPack(Config, next)?.Slash is { } slash) slashes.Add((id, input, next, slash.Step));   // protocol 30
            if (next.Alive && Rush.Connecting(next, Config)) rushes.Add((id, input, before, next));
            // feel/jump-pack-2 (the review): a charge that met a wall this tick still tests this tick's move against the players (in its charging phase).
            else if (next.Alive && Rush.WallStoppedFrom(before, next, Config) is { } charging && Rush.Connecting(charging, Config)) rushes.Add((id, input, before, charging));
            // feel/melee-charge-2 (protocol 42): a held charge whose landing ended it this tick still tests this tick's move against the players.
            else if (Rush.LandedFrom(before, next) is { } landed && Rush.Connecting(landed, Config)) { rushes.Add((id, input, before, landed)); (landedCharges ??= new())[id] = landed.Rush; }
            TrackJump(id, next, events.Jump, jumps);
        }
        StepFlightViews();   // fix/hitboxes: the jump poses viewers see now that everyone moved (RewindFraction.cs)
        // Projectiles: remote detonations asked this tick, then launches (in player order), then every projectile moves.
        foreach (int id in detonations) DetonateRemotely(id);
        foreach (var (shooter, state, input, weapon, stage, projectile, thrown, source) in launches) Launch(shooter, state, input, weapon, stage, projectile, thrown, source);
        StepAreas();   // lingering blasts first, so one created by a detonation this tick starts counting next tick
        StepProjectiles();
        // Hit-scan shots, then melee hits; within each, in player order. Attackers act with their state from this tick's
        // step even if an earlier resolution in the same tick killed them (simultaneous, as for shots).
        foreach (var (shooter, input, state, weapon, stage, index) in shots)
            if (ResolveShot(shooter, input, state, weapon, stage) is { } outcome)
                _shots[index] = _shots[index] with { Origin = outcome.Origin, End = outcome.End, Hit = outcome.Hit, Surface = outcome.Surface, Target = outcome.Target, Armoured = outcome.Armoured };
        foreach (var (attacker, input, state, step, heavy) in swings)
            ResolveMelee(attacker, input, state, (heavy ? Loadouts.MeleeHeavy(Config, state) : Loadouts.Melee(Config, state))[step - 1]);
        // Protocol 30: the sword slash's cone (the melee cone, every target the ability's one profile: the light interrupt, no stun or knockback).
        foreach (var (attacker, input, state, step) in slashes) ResolveMelee(attacker, input, state, step);
        var connected = new List<int>();   // M29: charges, after melee; M32: their stops once all have resolved
        foreach (var (player, input, before, state) in rushes) if (ResolveRush(player, input, before, state)) connected.Add(player);
        StopRushes(connected, landedCharges);
        foreach (var (player, kind, state, drop) in jumps) ResolveJump(player, kind, state, drop);
        ApplyPerkBlasts();   // M43: armour-break blasts of this tick and death blasts now due
        ExtrasStep();   // EXTRAS-HOOK: added content's own simulation after every player's attacks (Musou: its enemies)
        CountProjectilesOut();
        ResolveDeaths(aliveBefore);
        ScheduleDeathBlasts(aliveBefore);   // M43: Final Vengeance
        StepPickups(aliveBefore);   // fidelity gap 1: collections, expiry, the deaths' drops (Pickups.cs)
        StepMatch();
        RecordBodies();   // sim/player-collision: this tick's end states, as the snapshot after it carries them (WorldBodies.cs)
    }


    /// <summary>
    /// A melee hit (provisional rules; spec/behavior.md): every other living player, as the attacker saw them (same rewind
    /// as shots), whose feet are within <see cref="SimConfig.MeleeRange"/> horizontally, within
    /// ±<see cref="SimConfig.MeleeConeDegrees"/> of the attacker's heading and within ±<see cref="SimConfig.MeleeVerticalReach"/>
    /// vertically, and (with a level) not behind weapon collision on the line from the attacker's eye to the target's
    /// chest. The nearest (ties: lowest id) takes the step's Damage, every other one its SecondaryDamage, on the body; then,
    /// when still alive, the step's PrimaryReaction or SecondaryReaction (M28; <see cref="TryReact"/>, the knockback along the
    /// attacker-to-target direction). A step with a <see cref="MeleeStep.Stomp"/> then sets off that blast at the attacker's
    /// chest height (the heavy combo's foot stomp; as a jump-pack blast: <see cref="ApplyBlast"/>).
    /// </summary>
    void ResolveMelee(int attacker, PlayerInput input, PlayerState state, MeleeStep step)
    {
        var seen = SeenBy(input);
        var origin = state.Position + new Vector3(0, 0, Config.EyeHeight);
        var forward = new Vector2(-MathF.Sin(state.Yaw), MathF.Cos(state.Yaw));
        float minCos = MathF.Cos(Config.MeleeConeDegrees * MathF.PI / 180f);
        var targets = new List<(float Distance, int Id)>();
        foreach (var (id, then) in seen)
        {
            if (id == attacker || !then.Alive || !_players.TryGetValue(id, out var now) || !now.Alive) continue;
            if (!Harms(attacker, id)) continue;   // friendly fire off: allies are not targets (MatchRules)
            var offset = then.Position - state.Position;
            var flat = new Vector2(offset.X, offset.Y);
            float distance = flat.Length();
            if (distance > Config.MeleeRange || MathF.Abs(offset.Z) > Config.MeleeVerticalReach) continue;
            if (distance > 1e-4f && Vector2.Dot(flat / distance, forward) < minCos) continue;   // overlapping players count as in front
            if (Collision is not null)
            {
                var toChest = then.Position + new Vector3(0, 0, ChestOf(then)) - origin;
                float length = toChest.Length();
                if (length > 1e-4f && Collision.Weapons.Raycast(origin, toChest / length, length) is { } wall && wall.Distance < length) continue;
            }
            targets.Add((distance, id));
        }
        targets.Sort();
        for (int i = 0; i < targets.Count; i++)
        {
            var (distance, target) = targets[i];
            float damage = i == 0 ? step.Damage : step.SecondaryDamage;
            float shieldPart = i == 0 ? step.ShieldDamage : step.SecondaryShieldDamage;   // Weapons C: damage_armourshield
            var reaction = i == 0 ? step.PrimaryReaction : step.SecondaryReaction;
            if ((damage <= 0f && shieldPart <= 0f && reaction.Kind == ReactionKind.None) || !_players[target].Alive) continue;   // killed by an earlier hit this tick
            var push = _players[target].Position - state.Position;
            var flatPush = new Vector3(push.X, push.Y, 0f);
            var pushDirection = flatPush.LengthSquared() > 1e-8f ? Vector3.Normalize(flatPush) : new Vector3(forward, 0f);
            if (damage > 0f || shieldPart > 0f)
            {
                // The knife's own multipliers, not the bolter's (wargear do_damage of the attack's ability).
                var before = _players[target];
                bool armoured = before.Shield > 0f;
                var meleeType = step.DamageType == DamageType.Unknown ? DamageType.Melee : step.DamageType;
                float received = Loadouts.Body(Config, before).Received(meleeType)   // M43: Feel No Pain
                    * DamageScaleOf(attacker)   // test aid: --bot-damage-scale
                    * Stims.MeleeDealt(Config, state);   // perks/inert-2: the attacker's combat stims (the whole hit, its shield part too)
                damage *= received;
                // Weapons C: the armour-shield part first, off the shield only (as a blast's and the charge's), then the damage.
                var victim = before;
                float shieldOnly = MathF.Min(victim.Shield, MathF.Max(0f, shieldPart * received));
                if (shieldOnly > 0f) victim = victim with { Shield = victim.Shield - shieldOnly, SinceDamage = 0f };
                var (after, shieldDamage, healthDamage) = Combat.ApplyDamage(victim, damage, step.ShieldMultiplier,
                    step.HealthMultiplier * Stims.HealthTaken(Config, before, meleeType), Config);   // perks/inert-2: the target's stims, health part only
                shieldDamage += shieldOnly;
                _players[target] = after;
                _hits.Add(new HitEvent(Tick, attacker, target, HitZone.Body, distance, shieldDamage, healthDamage, !after.Alive, Melee: true,
                    Direction: pushDirection, Armoured: armoured));
                ShieldBroke(target, before, after, shieldDamage);
                Heal(attacker, i == 0 ? step.Heal : step.SecondaryHeal);   // M43: the heal-on-hit perks
            }
            if (_players[target].Alive) TryReact(attacker, target, reaction, state.Position, new Vector2(pushDirection.X, pushDirection.Y));
        }
        if (step.Stomp is { } stomp)
            ApplyBlast(attacker, 0, state.Position + new Vector3(0, 0, ChestOf(state)), stomp, melee: true);   // a stomp kill is a melee kill
        ExtrasMelee(attacker, state, step);   // EXTRAS-HOOK: added content's targets of the same strike (Musou: the cleave)
    }

    /// <summary>
    /// Hook for added content (docs/added-content.md): once a tick in <see cref="Step"/>, after every player's shots, strikes, charges and perk blasts and
    /// before the projectile counts and the deaths are resolved. Implemented in an <c>Extras/</c> folder; returns at once unless its feature runs.
    /// </summary>
    partial void ExtrasStep();

    /// <summary>
    /// Hook for added content: at the end of <see cref="ResolveMelee"/> (after the players it hit and its stomp), with the attacker's state this tick
    /// and the step it struck with. Implemented in an <c>Extras/</c> folder; returns at once unless its feature runs.
    /// </summary>
    partial void ExtrasMelee(int attacker, PlayerState state, MeleeStep step);

    /// <summary>
    /// Hit test against other living players as they were at the shooter's view tick (rewind capped by
    /// <see cref="SimConfig.MaxRewindTicks"/>), then damage applied to their current state. With a level loaded, the shot
    /// stops at the first weapon-collision surface along the aim ray (static geometry and props, so it is not rewound; a shot
    /// with a <see cref="PierceSpec"/> may pass props and damage players behind them, M39: <see cref="ResolvePierce"/>). The basic
    /// shot (<paramref name="stage"/> 0) has the range, damage, fall-off and multipliers of <paramref name="weapon"/>, the
    /// shooter's weapon in hand; a charged hit-scan shot those of its stage (<see cref="ChargeShot"/>: no fall-off; a range
    /// of 0 means the weapon's).
    /// Returns where the shot went (M25a, for <see cref="ShotEvent"/>), or null when the stage fires nothing hit-scan.
    /// </summary>
    ShotOutcome? ResolveShot(int shooter, PlayerInput input, PlayerState state, WeaponSpec weapon, int stage = 0)
    {
        if (stage == 0 && weapon.Delivery == DeliveryKind.Cone)
            return weapon.Cone is { } cone ? ResolveCone(shooter, input, state, cone) : null;
        var charged = Shots.ChargeShotOf(weapon, stage);
        if (stage > 0 && charged?.Damage is null) return null;
        float range = charged is { MaxRange: > 0f } ? charged.MaxRange : weapon.MaxRange;
        var (seen, flight) = SeenAt(input);   // fix/hitboxes: between ticks, as the client drew them, and the jump poses shown
        var origin = Combat.ShotOrigin(state, input, Config, Collision);
        var direction = Combat.ShotDirection(input, Config, state);   // weapons/spread: this tick's scatter (Spread.cs)
        // M39: a shot that can pierce passes through players and props (Props.cs); its own damage stops at the first surface.
        var pierce = Pierces.Of(weapon, stage, upgrade => Pierces.HasUpgrade(Config, state, upgrade));
        var trace = Collision?.TraceShot(origin, direction, range, pierce);
        var wallHit = trace?.First;
        float blocked = wallHit is { } wall ? wall.Distance : float.MaxValue;
        var struck = new List<(float Distance, int Id, HitZone Zone)>();
        foreach (var (id, then) in seen)
        {
            if (id == shooter || !then.Alive || !_players.TryGetValue(id, out var now) || !now.Alive) continue;
            var (zone, distance) = Combat.HitTest(origin, direction, then.Position, BodyOf(id, then, flight), range, then.Yaw);   // Ork O4: the target's own volumes; fix/hitboxes: as drawn in a jump
            if (zone != HitZone.None && distance < blocked) struck.Add((distance, id, zone));
        }
        struck.Sort((a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Id.CompareTo(b.Id));
        int weaponIndex = Loadouts.WeaponIndex(Config, state, state.Slot);
        // Players along the ray in order: each takes the shot's damage at its distance. Without penetration the first stops the
        // shot; with it the shot goes on while the points left cover the player's material cost (Pierces.PlayerCost), else it
        // stops in that player (who is still hit). An ally still stops or costs as anyone (hit volumes are physical); ApplyHit
        // then deals and records nothing without friendly fire.
        float points = pierce?.Points ?? 0f;
        var hitIds = new HashSet<int>();
        (float Distance, int Id, HitZone Zone, bool Armoured)? last = null;
        bool stopped = false;
        foreach (var (distance, id, zone) in struck)
        {
            var spec = charged?.Damage ?? weapon.DirectDamage with { Damage = Combat.DamageAt(distance, weapon) };
            bool armoured = _players[id].Shield > 0f;   // before this hit (hit feedback, protocol 22)
            ApplyHit(shooter, id, spec, zone, distance, 1f, 0, false, weapon: weaponIndex, direction: direction);
            hitIds.Add(id);
            last = (distance, id, zone, armoured);
            float cost = Pierces.PlayerCost(pierce, zone);
            if (pierce is null || !pierce.CanPierce(points, cost)) { stopped = true; break; }
            points -= cost;
        }
        // The shot event (one per shot; protocol unchanged) names the farthest player the shot damaged: a victim of an exit cone,
        // else the last player the ray hit, else where the ray stopped.
        ShotOutcome? onPlayer = last is { } l
            ? new ShotOutcome(origin, origin + direction * l.Distance, l.Zone == HitZone.Head ? ShotHit.Head : ShotHit.Body, -1, l.Id, l.Armoured) : null;
        if (stopped) return onPlayer!.Value;
        if (trace is { Pierced.Count: > 0 } t && pierce is not null)
        {
            // Past the players: the props, with the points the players left (the trace again when some were spent).
            if (points < pierce.Points) t = Collision!.TraceShot(origin, direction, range, pierce with { Points = points });
            if (t.Pierced.Count == 0) return onPlayer ?? WorldOutcome(origin, direction, range, t.Stop);
            var past = ResolvePierce(shooter, seen, flight, state, origin, direction, range, t, pierce, hitIds);
            return past.Hit is ShotHit.Body or ShotHit.Head ? past : onPlayer ?? past;
        }
        return onPlayer ?? WorldOutcome(origin, direction, range, wallHit);
    }

    /// <summary>Where a ray or cone shot went (M25a): see <see cref="ShotEvent"/>.</summary>
    readonly record struct ShotOutcome(Vector3 Origin, Vector3 End, ShotHit Hit, int Surface, int Target, bool Armoured = false);

    /// <summary>A shot that reached no player: the wall it stopped at (with its surface material), else the point at <paramref name="range"/>.</summary>
    ShotOutcome WorldOutcome(Vector3 origin, Vector3 direction, float range, RayHit? wall) => wall is { } w
        ? new ShotOutcome(origin, w.Point, ShotHit.World, Collision!.Weapons.MaterialOf(w.Triangle), 0)
        : new ShotOutcome(origin, origin + direction * (float.IsFinite(range) && range > 0f ? range : 0f), ShotHit.None, -1, 0);

    /// <summary>
    /// Damage from a <see cref="DamageSpec"/> scaled by <paramref name="intensity"/> (rules **interpretation**;
    /// spec/behavior.md): first its ShieldDamage x intensity comes off the armour shield only (no multiplier), then its
    /// Damage x intensity goes through <see cref="Combat.ApplyDamage(PlayerState, float, float, float, SimConfig)"/> with the
    /// zone's multipliers. A direct hit is always recorded, a blast hit when it dealt anything; the shooter is credited with a kill.
    /// Nothing happens when <see cref="Harms"/> says the shooter cannot hurt the target (an ally without friendly fire, or
    /// the match has ended); self damage is never filtered.
    /// </summary>
    void ApplyHit(int shooter, int target, DamageSpec spec, HitZone zone, float distance, float intensity, int projectile, bool splash, bool cone = false, bool landing = false,
        int weapon = -1, bool thrownItem = false, Vector3 direction = default, bool melee = false, int perk = -1)
    {
        if (!_players.TryGetValue(target, out var victim) || !victim.Alive || intensity <= 0f) return;
        if (!Harms(shooter, target)) return;
        var before = victim;
        bool armoured = victim.Shield > 0f;   // before this hit (hit feedback, protocol 22)
        // M43: the target's received-damage perks (Feel No Pain, Impenetrable) scale the whole hit, its shield part included
        // (interpretation). The type is the file's damagetype, else a blast's explosive, melee's melee, a shot's ranged.
        var type = spec.Type != DamageType.Unknown ? spec.Type : splash ? DamageType.Explosive : melee ? DamageType.Melee : DamageType.Ranged;
        intensity *= Loadouts.Body(Config, victim).Received(type) * DamageScaleOf(shooter);   // test aid: --bot-damage-scale (1 unless set)
        float shieldOnly = MathF.Min(victim.Shield, MathF.Max(0f, spec.ShieldDamage * intensity));
        if (shieldOnly > 0f) victim = victim with { Shield = victim.Shield - shieldOnly, SinceDamage = 0f };
        bool head = zone == HitZone.Head;
        float stim = Stims.HealthTaken(Config, victim, type);   // perks/inert-2: combat stims scale the health part only
        var (after, shieldDamage, healthDamage) = Combat.ApplyDamage(victim, spec.Damage * intensity,
            head ? spec.ShieldHeadMultiplier : spec.ShieldBodyMultiplier, (head ? spec.HeadMultiplier : spec.BodyMultiplier) * stim, Config);
        _players[target] = after;
        ShieldBroke(target, before, after, shieldOnly + shieldDamage);
        if (!splash || shieldOnly + shieldDamage + healthDamage > 0f)   // a direct hit counts even when it deals nothing
            _hits.Add(new HitEvent(Tick, shooter, target, zone, distance, shieldOnly + shieldDamage, healthDamage, !after.Alive, Projectile: projectile, Splash: splash, Cone: cone, Landing: landing,
                Weapon: weapon, ThrownItem: thrownItem, Direction: Unit(direction), Melee: melee, Armoured: armoured, Perk: perk));
    }

    /// <summary>A unit vector along <paramref name="v"/>, or zero when it has no usable direction.</summary>
    static Vector3 Unit(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.Zero;
}
