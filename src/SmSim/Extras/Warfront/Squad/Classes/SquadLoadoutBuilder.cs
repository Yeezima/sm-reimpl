using System.Numerics;

namespace SmSim.Extras.Warfront.Squad;

/// <summary>
/// Builds a squad <see cref="SquadLoadout"/> from the Classic class data (read only: <see cref="SimConfig"/>, <see cref="Loadouts"/>, <see cref="BotLoadouts"/>): the body (health, armour shield and their
/// regeneration, as <see cref="Loadouts.Body"/> leaves them after the perks), the weapon profile (range, damage, rate, hit chance), the jump pack, and the gear names, perks and the player's colours.
/// Slice W11; label: provisional approximation of player combat (no recoil, perks as numbers), the weapon profile's hit chances ours/estimates (<see cref="SquadClassRules"/>).
/// </summary>
public static class SquadLoadoutBuilder
{
    /// <summary>
    /// The loadout a player's choice gives (<paramref name="choice"/>: a class and indices into its lists, as the customizer sends them), wearing the player's look
    /// (<paramref name="look"/>). <paramref name="ownPieces"/>: the armour list carries the player's own pieces (a standard retainer mirrors them); false: only the class tag, so the client dresses the Marine in the
    /// class's own armour in the player's colours (the Librarian's squad). Null for a class the config does not have.
    /// </summary>
    public static SquadLoadout? FromChoice(SimConfig c, LoadoutChoice choice, AppearanceCode look, bool ownPieces = true)
    {
        if (Loadouts.ClassOf(c, choice.Class) is not { } cl) return null;
        var s = Loadouts.Choose(c, new PlayerState(0, Vector3.Zero, Vector3.Zero, 0f, true) { Class = choice.Class },
            choice.Primary, choice.Pistol, choice.Melee, choice.Equipment, choice.Perk1, choice.Perk2, choice.Second);
        var body = Loadouts.Body(c, s);
        string Name(int i, IReadOnlyList<WeaponSpec> list) => i >= 0 && i < list.Count ? list[i].Name : "";
        var perks = new List<string>();
        foreach (int p in new[] { s.Perk1, s.Perk2 }) if (p >= 0 && p < c.Perks.Count) perks.Add(c.Perks[p].Name);
        string melee = s.MeleeChoice >= 0 && s.MeleeChoice < cl.MeleeChoices.Count ? cl.MeleeChoices[s.MeleeChoice].Wargear : "";
        var armour = new List<string> { "class:" + cl.Role };
        if (ownPieces)
        {
            if (look.HasPredefinedSet) armour.Add("set:" + look.PredefinedSet);
            foreach (var slot in Enum.GetValues<ArmourSlot>())
                if (look.Piece(slot) != AppearanceCode.DefaultIndex) armour.Add($"{slot.ToString().ToLowerInvariant()}:{look.Piece(slot)}");
        }
        var colours = new List<uint>();
        foreach (var slot in Enum.GetValues<ArmourSlot>())
            colours.Add((uint)look.Palette(slot, 0) | (uint)look.Palette(slot, 1) << 8 | (uint)look.Palette(slot, 2) << 16);
        return new SquadLoadout
        {
            ClassId = cl.Name,
            Primary = Name(s.Primary, c.Weapons), Secondary = Name(s.Pistol, c.Weapons), Melee = melee,
            Perks = perks, Armour = armour, Colours = colours,
            HealthMax = body.HealthMax, ArmourMax = body.ShieldMax,
            HealthRegenDelay = body.HealthRegenDelay, HealthRegenRate = body.HealthRegenRate,
            ArmourRegenDelay = body.ShieldRegenDelay, ArmourRegenRate = body.ShieldRegenRate,
            Weapon = WeaponOf(c, s),   // SquadRules.DamageDealt is applied where a Marine fires, not here
            Jump = JumpOf(c, s),
        };
    }

    /// <summary>The squad weapon of a player: the primary when the class has one (a ranged profile from the wargear), else the melee combo (a melee profile averaged over its steps).</summary>
    public static SquadWeapon WeaponOf(SimConfig c, PlayerState s)
    {
        if (Loadouts.Weapon(c, s, WeaponSlot.Ranged) is { } w) return RangedOf(w);
        var steps = Loadouts.Melee(c, s);
        if (steps.Count == 0) return SquadWeapon.Blade;
        float damage = 0f, time = 0f;
        foreach (var st in steps) { damage += st.Damage * Math.Max(0.01f, st.HealthMultiplier); time += Math.Max(0.1f, st.Duration); }
        return new SquadWeapon(SquadClassRules.MeleeReach, damage / steps.Count, Math.Max(SquadClassRules.MinInterval, time / steps.Count), SquadClassRules.HitMelee, true);
    }

    /// <summary>A ranged wargear row as the squad's weapon: range (capped), damage per trigger pull (bursts and a blast's share included), seconds between pulls with the reload spread over the clip.</summary>
    public static SquadWeapon RangedOf(WeaponSpec w)
    {
        bool projectile = w.Delivery == DeliveryKind.Projectile;
        float damage = Math.Max(0f, w.Damage) * Math.Max(1f, w.BodyMultiplier);
        if (projectile && w.Projectile is { } p)
        {
            float blast = 0f;
            foreach (var sp in p.AllSplashes) blast = Math.Max(blast, sp.Damage.Damage);
            if (p.DetonateDamage is { } det) blast = Math.Max(blast, det.Damage);
            damage += blast * SquadClassRules.SplashShare;
        }
        damage *= Math.Max(1, w.BurstLength);
        float interval = Math.Max(SquadClassRules.MinInterval, w.ShotCooldown) + (w.ClipSize > 0 ? Math.Max(0f, w.ReloadTime) / w.ClipSize : 0f);
        float hit = projectile ? SquadClassRules.HitProjectile : w.Trigger == TriggerKind.FullAuto ? SquadClassRules.HitFullAuto : SquadClassRules.HitSemiAuto;
        float range = w.MaxRange > 0f ? Math.Min(w.MaxRange, SquadClassRules.RangeCap) : SquadRules.DefaultRange;
        return new SquadWeapon(range, damage, interval, hit, false);
    }

    /// <summary>The class's pack (<see cref="Loadouts.JumpPack(SimConfig, PlayerState)"/>), or null for a class without one.</summary>
    public static SquadJump? JumpOf(SimConfig c, PlayerState s)
    {
        if (Loadouts.JumpPack(c, s) is not { } p) return null;
        float height = p.HeightMap.Count > 0 ? p.HeightMap[^1].Value : 0f;
        float cost = p.PowerCost.Count > 0 ? p.PowerCost[^1].Value : p.MaxPower;
        float up = p.JumpingGravity > 0f ? MathF.Sqrt(2f * height / p.JumpingGravity) : 0f;
        float down = p.FallingGravity > 0f ? MathF.Sqrt(2f * height / p.FallingGravity) : 0f;
        float air = up + Math.Max(0f, p.HangTime) + down + Math.Max(0f, p.TakeOffDelay);
        return new SquadJump(p.MaxPower, p.RechargeRate, Math.Max(0f, cost), height, Math.Max(0f, p.ForwardSpeed) * air, air, p.ForwardSpeed,
            p.PoundEnabled ? p.Pound?.Radius ?? 0f : 0f, p.PoundEnabled ? p.Pound?.Damage.Damage ?? 0f : 0f, p.PoundRecovery, p.PoundEnabled && p.Pound is not null);
    }

    // ---- the Librarian's rolls

    /// <summary>The class index of <paramref name="role"/> on <paramref name="side"/> in the config; -1 with none.</summary>
    public static int ClassIndex(SimConfig c, string role, Team side)
    {
        for (int i = 0; i < c.Classes.Count; i++)
            if (string.Equals(c.Classes[i].Role, role, StringComparison.OrdinalIgnoreCase) && c.Classes[i].Side == side) return i;
        return -1;
    }

    /// <summary>
    /// The seed of one Marine's roll: a hash of the run seed, the player's campaign key, the roster slot and the selection (the squad class chosen). The same four values always give the same number.
    /// </summary>
    public static ulong RollSeed(ulong runSeed, ulong key, int slot, int selection)
    {
        ulong h = Mix(runSeed ^ 0x9E3779B97F4A7C15UL);
        h = Mix(h ^ key);
        h = Mix(h ^ (ulong)(uint)slot * 0xC2B2AE3D27D4EB4FUL);
        return Mix(h ^ (ulong)(uint)selection * 0x165667B19E3779F9UL);
    }

    static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ x >> 30) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ x >> 27) * 0x94D049BB133111EBUL;
        return x ^ x >> 31;
    }

    /// <summary>
    /// A valid Classic PvP bot loadout (<see cref="BotLoadouts"/>: the owner's fixed presets) for squad class <paramref name="squadClass"/>, picked by <see cref="RollSeed"/>: the first legal preset from the seed's
    /// pick on; a class whose presets are all illegal in this config gets its default loadout. Class armour (no own pieces) in the colours of <paramref name="look"/>. Null for a config without the class.
    /// </summary>
    public static SquadLoadout? Roll(SimConfig c, SquadClass squadClass, ulong runSeed, ulong key, int slot, int selection, AppearanceCode look, Team side = Team.SpaceMarines)
    {
        string role = SquadClassRules.RoleOf(squadClass);
        int cls = ClassIndex(c, role, side);
        if (cls < 0) return null;
        var presets = BotLoadouts.For(role);
        ulong seed = RollSeed(runSeed, key, slot, selection);
        int start = presets.Count == 0 ? 0 : BotLoadouts.PickIndex((int)(seed >> 33), presets.Count);
        for (int t = 0; t < presets.Count; t++)
        {
            var pick = BotLoadouts.Resolve(c, cls, presets[(start + t) % presets.Count]);
            if (!pick.Legal) continue;
            return FromChoice(c, new LoadoutChoice(cls, pick.Primary, pick.Pistol, pick.Melee, pick.Equipment, pick.Perk1, pick.Perk2, pick.Second), look, ownPieces: false);
        }
        return FromChoice(c, new LoadoutChoice(cls), look, ownPieces: false);
    }
}
