namespace SmSim.Extras.Musou;

/// <summary>
/// The Musou loadout (owner, 2026-10-31: "players spawn with the bolt pistol only"; Musou only). A player (re)spawns with its class's own pistol alone: no
/// primary (heavy weapons included), no second primary, no grenades, the class's default melee combo (a melee choice such as a hammer goes back to the
/// default; the melee strikes themselves stay, every class can always strike). Its class still decides what it can be given later
/// (<see cref="Give"/>: the class's primary and pistol lists).
/// <para>
/// The core falls back to a class's default weapon for an empty slot, so a stripped primary needs a config whose classes have none by default:
/// <see cref="Prepare"/> (the match tuning applies it under <see cref="ContentExtras.Musou"/>; the world strips nothing without it, see
/// <see cref="Prepared"/>).
/// </para>
/// </summary>
public static class MusouLoadout
{
    /// <summary>
    /// The config with every class's default primary taken away (-1), its primary list kept (the old default added when the list was only the default), so
    /// nobody carries a primary unless given one. Unchanged without <see cref="ContentExtras.Musou"/>.
    /// </summary>
    public static SimConfig Prepare(SimConfig config)
    {
        if (!config.Extras.HasFlag(ContentExtras.Musou)) return config;
        var classes = config.Classes.Select(c => c.Primary < 0 ? c : c with
        {
            Primary = -1,
            PrimaryChoices = c.PrimaryChoices.Count > 0 ? (c.PrimaryChoices.Contains(c.Primary) ? c.PrimaryChoices : [c.Primary, .. c.PrimaryChoices]) : [c.Primary],
        }).ToList();
        return config with { Classes = classes, MusouDefaultPrimaries = config.MusouDefaultPrimaries.Count > 0 ? config.MusouDefaultPrimaries : config.Classes.Select(c => c.Primary).ToList() };
    }

    /// <summary>The class's default primary, whether or not <see cref="Prepare"/> took it away; -1: none (Musou Mode's full loadout).</summary>
    public static int DefaultPrimary(SimConfig c, int playerClass)
    {
        int own = Loadouts.WeaponIndex(c, playerClass, WeaponSlot.Ranged);
        if (own >= 0) return own;
        var list = c.MusouDefaultPrimaries;
        return playerClass >= 0 && playerClass < list.Count && list[playerClass] >= 0 && list[playerClass] < c.Weapons.Count ? list[playerClass] : -1;
    }

    /// <summary>Whether <paramref name="config"/> went through <see cref="Prepare"/> (no class has a default primary).</summary>
    public static bool Prepared(SimConfig config) => config.Classes.All(c => c.Primary < 0);

    /// <summary>A spawned player as Musou arms it: the class's pistol in hand with a full clip and reserve, nothing else (see the class summary).</summary>
    public static PlayerState Stripped(SimConfig c, PlayerState s)
    {
        int pistol = Loadouts.WeaponIndex(c, s.Class, WeaponSlot.Pistol);
        var p = s with { Primary = -1, Second = -1, Pistol = pistol, MeleeChoice = -1 };
        var held = Loadouts.Weapon(c, p, WeaponSlot.Pistol);
        return Calm(p) with
        {
            Slot = WeaponSlot.Pistol, Clip = held?.ClipSize ?? 0, Reserve = held?.ReserveSize ?? 0, ReloadLeft = 0f, Heat = 0f, Overheated = false, SinceHeat = 0f,
            OtherClip = 0, OtherReserve = 0, OtherReloadLeft = 0f, OtherHeat = 0f, OtherOverheated = false, OtherSinceHeat = 0f,
            ThirdClip = 0, ThirdReserve = 0, ThirdReloadLeft = 0f, ThirdHeat = 0f, ThirdOverheated = false, ThirdSinceHeat = 0f,
            Grenades = 0,
        };
    }

    /// <summary>
    /// The player given weapon <paramref name="weapon"/> (an index into <see cref="SimConfig.Weapons"/>): a primary its class may carry takes the primary
    /// slot, else a pistol its class may carry the pistol slot; it comes with a full clip and reserve and is put in hand (the weapon held before goes to the
    /// other slot as it was). Null when the class may carry it in neither slot, the index is out of range or the player is dead.
    /// </summary>
    public static PlayerState? Give(SimConfig c, PlayerState s, int weapon)
    {
        if (!s.Alive || weapon < 0 || weapon >= c.Weapons.Count || Busy(s)) return null;   // as the core's switch: not mid-strike, queued, charging or throwing
        WeaponSlot slot;
        if (Loadouts.ValidWeapon(c, s.Class, WeaponSlot.Ranged, weapon)) slot = WeaponSlot.Ranged;
        else if (Loadouts.ValidWeapon(c, s.Class, WeaponSlot.Pistol, weapon)) slot = WeaponSlot.Pistol;
        else return null;
        var p = Calm(slot == WeaponSlot.Ranged ? s with { Primary = weapon } : s with { Pistol = weapon });
        // The clip and reserve as the player's perks change them (as the strip and the refill read them), not the base weapon's.
        if (Loadouts.Weapon(c, p, slot) is not { } w) return null;
        if (p.Slot == slot)   // the same slot in hand: the new weapon replaces it there
            return p with { Clip = w.ClipSize, Reserve = w.ReserveSize, ReloadLeft = 0f, Heat = 0f, Overheated = false, SinceHeat = 0f };
        if (Loadouts.Other(p.Slot) == slot || p.Slot == WeaponSlot.Second)
        {
            // Into the hand from the other slot: the weapon held goes to the other slot with what it had (from the second primary: the second keeps its own).
            var q = p.Slot == WeaponSlot.Second ? p : p with
            {
                OtherClip = p.Clip, OtherReserve = p.Reserve, OtherReloadLeft = p.ReloadLeft, OtherHeat = p.Heat, OtherOverheated = p.Overheated, OtherSinceHeat = p.SinceHeat,
            };
            return q with { Slot = slot, Clip = w.ClipSize, Reserve = w.ReserveSize, ReloadLeft = 0f, Heat = 0f, Overheated = false, SinceHeat = 0f };
        }
        return null;
    }

    /// <summary>
    /// The player given drop item <paramref name="item"/> (<see cref="MusouDropItem"/>): a weapon by <see cref="Give"/>; a melee weapon of the class's
    /// <see cref="ClassLoadout.MeleeChoices"/> becomes its melee combo (only between attacks: not during a strike, a combo or a charge). Null when it cannot
    /// be given now.
    /// </summary>
    public static PlayerState? GiveItem(SimConfig c, PlayerState s, ushort item)
    {
        if (!MusouDropItem.IsMelee(item)) return Give(c, s, MusouDropItem.Index(item));
        int choice = MusouDropItem.Index(item);
        if (!s.Alive || Loadouts.ClassOf(c, s.Class) is not { } loadout || choice >= loadout.MeleeChoices.Count) return null;
        if (Busy(s)) return null;
        return s with { MeleeChoice = choice };
    }

    /// <summary>
    /// Whether a gift waits (the drop stays): the core allows no weapon switch during a melee step (a queued attack only exists in one), a charge (its
    /// input is cleared while rushing) or a throw (<c>Combat.StepPlayer</c>), and a melee weapon changes no combo under way.
    /// </summary>
    public static bool Busy(PlayerState s) => s.MeleeStep != 0 || s.MeleeQueued || s.MeleeQueuedHeavy || s.Rush != RushPhase.None || s.Throwing;

    /// <summary>Nothing of the weapon in hand in flight: no switch, charge, burst, brace or vent under way.</summary>
    internal static PlayerState Calm(PlayerState s) => s with
    {
        SwitchLeft = 0f, Cooldown = 0f, BurstLeft = 0, Charging = false, ChargeTime = 0f, PendingShot = 0, PendingLeft = 0f,
        Brace = BracePhase.None, BraceTicks = 0, Venting = false,
    };
}
