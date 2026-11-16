using System.Numerics;
using SmSim.Extras.Musou;

namespace SmSim.Extras.Warfront.Squad;

/// <summary>Where a Marine is in a battle: not in it (<see cref="Free"/>), fighting, downed with its window open, or out until the next battle.</summary>
public enum SquadState : byte { Free = 0, Standing = 1, Downed = 2, Out = 3 }

/// <summary>The fireteam orders (Musou Mode semantics): stay by the owner, hold a place, go for a place and fight.</summary>
public enum SquadOrder : byte { Follow = 0, Hold = 1, Attack = 2 }

/// <summary>How hard an Ork is to kill, for the records and the score: fodder, elite, boss.</summary>
public enum SquadEnemyClass : byte { Fodder = 0, Elite = 1, Boss = 2 }

/// <summary>A Marine weapon as the squad rules see it (mirrored from the owner's loadout by W11; defaults in <see cref="SquadRules"/>): a ranged weapon, or a melee one.</summary>
public sealed record SquadWeapon(float Range = SquadRules.DefaultRange, float Damage = SquadRules.DefaultDamage, float Interval = SquadRules.DefaultInterval, float HitChance = SquadRules.DefaultHitChance, bool Melee = false)
{
    /// <summary>A melee weapon: reach 2.5 m (the puppets' MeleeRange), one blow a second [placeholder estimate].</summary>
    public static SquadWeapon Blade { get; } = new(2.5f, 60f, 1f, 0.9f, true);
}

/// <summary>
/// A Marine loadout as a data mirror (this slice only stores it; W11 fills it from the owner and the Librarian rolls): the class and gear ids, the colours, and the body and weapon numbers the
/// rules use. Replace the whole record to change it.
/// </summary>
public sealed record SquadLoadout
{
    public string ClassId { get; init; } = "";
    public string Primary { get; init; } = "";
    public string Secondary { get; init; } = "";
    public string Melee { get; init; } = "";
    public IReadOnlyList<string> Perks { get; init; } = [];
    public IReadOnlyList<string> Armour { get; init; } = [];
    public IReadOnlyList<uint> Colours { get; init; } = [];
    public float HealthMax { get; init; } = SquadRules.DefaultHealth;
    public float ArmourMax { get; init; } = SquadRules.DefaultArmour;
    public SquadWeapon Weapon { get; init; } = new();
    /// <summary>W11: seconds after the last hit before armour (the shield) and health regenerate, and how much a second; 0 rate: none (the W9 default). From the class data (<c>Loadouts.Body</c>).</summary>
    public float ArmourRegenDelay { get; init; }
    public float ArmourRegenRate { get; init; }
    public float HealthRegenDelay { get; init; }
    public float HealthRegenRate { get; init; }
    /// <summary>W11: the class's jump pack (the Assault/Raptor); null: the Marine does not leap.</summary>
    public SquadJump? Jump { get; init; }
}

/// <summary>One Marine's service record (the owner's list: kills, assists, revives, objectives, battles survived; plus times downed and the kill classes).</summary>
public sealed class ServiceRecord
{
    public int Kills, EliteKills, BossKills, Assists, Revives, TimesDowned, Objectives, BattlesSurvived;

    public ServiceRecord Copy() => (ServiceRecord)MemberwiseClone();
}

/// <summary>One Marine of a roster: it lives across battles. The name and the record stay when the loadout changes.</summary>
public sealed class RosterMarine
{
    public string Name = "";
    public SquadLoadout Loadout = new();
    public ServiceRecord Record = new();
}

/// <summary>The saved form of one campaign key's roster (W5's save file carries it); the key is the campaign key of <c>WarfrontPlayerState</c>, never a player id.</summary>
public sealed record SquadOwnerSave(ulong Key, bool Held, IReadOnlyList<SquadMarineSave> Marines);

/// <summary>The saved form of one Marine.</summary>
public sealed record SquadMarineSave(string Name, SquadLoadout Loadout, ServiceRecord Record);

/// <summary>The saved form of every roster; <see cref="NextName"/> keeps default names unique.</summary>
public sealed record SquadRosterSave(int NextName, IReadOnlyList<SquadOwnerSave> Owners);

/// <summary>
/// One human as the squad sees it this tick: the player id, where it is, whether it stands, whether it is downed with its revive window open (<see cref="Fallen"/>), and the squad Marine index
/// it holds Use on (-1: none).
/// </summary>
public readonly record struct SquadHuman(int Id, Vector3 Position, bool Alive, bool Fallen = false, int ReviveTarget = -1);

/// <summary>A kill by a squad Marine this tick (for the owner's XP and score; W7, W15).</summary>
public readonly record struct SquadKill(ulong Key, int Slot, ushort Enemy, SquadEnemyClass Class, MusouEnemyType? Type = null);

/// <summary>A revive this tick: given by a human (<see cref="ByHuman"/>, else -1) or by a Marine (<see cref="ByMarine"/> index, else -1); received by Marine <see cref="Slot"/> of campaign key <see cref="Key"/>'s squad, or by human <see cref="HumanRevived"/> (else -1, and Key 0, Slot -1).</summary>
public readonly record struct SquadRevive(int ByHuman, int ByMarine, ulong Key, int Slot, int HumanRevived);

/// <summary>A Marine knocked down this tick (a "retainer knockout" on the leaderboard).</summary>
public readonly record struct SquadDown(ulong Key, int Slot);

/// <summary>A Marine as the wire shows it (W10): ids, state, place, facing, health share, seconds left of the downed window.</summary>
public readonly record struct SquadMarineView(int Index, ulong Key, int Slot, SquadState State, Vector3 Position, float Yaw, float HealthShare, float DownedLeft, bool Firing = false, int Target = -1, float ReviveShare = 0f, bool Airborne = false);
