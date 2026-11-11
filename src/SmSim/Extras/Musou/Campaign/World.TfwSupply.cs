using SmSim.Extras.Musou.Campaign;

namespace SmSim.Extras.Musou.Campaign
{
    /// <summary>
    /// Musou Mode's supply rules (night batch 2026-11-06, the owner's requests; spec "Musou Mode: endless ammunition, grenade refill, the AI Marines' damage"). Musou Mode only: Classic PvP keeps the
    /// original's rules. A change here raises <see cref="TfwRules.Version"/>.
    /// </summary>
    public static class TfwSupplyRules
    {
        /// <summary>The owner: "grenades should refill every 30 seconds automatically": one grenade back every this many seconds while a player carries fewer than its class's most.</summary>
        public const float GrenadeRefillSeconds = 15f;   // 30 until 2026-11-08; the owner: "vanilla grenades every 15s"

        /// <summary>The owner: "bots in musou mode should retain all player attributes and statistics - except make them take 90% reduced damage": the share of every hit an AI Marine takes.</summary>
        public const float BotDamageTaken = 0.10f;
    }
}

namespace SmSim
{
    /// <summary>Musou Mode's endless ammunition, its grenade refill and the AI Marines' damage taken (<see cref="TfwSupplyRules"/>), from <see cref="TfwStep"/> and the damage hook.</summary>
    public sealed partial class World
    {
        readonly Dictionary<int, int> _tfwGrenadeTicks = new();
        readonly List<int> _tfwSupplyIds = new();

        /// <summary>
        /// The AI Marines (the war's puppets: the allied pair) and the bot clients (a client started with <c>--bot</c>, <see cref="IsBotClient"/>; the owner, 2026-11-06: "--bot clients should get the 90%
        /// damage cut in musou") take <see cref="TfwSupplyRules.BotDamageTaken"/> of every hit; humans and everything else are unchanged.
        /// </summary>
        void TfwDamageTaken(int target, ref float scale)
        {
            if (TfwContracts.IsPuppet(target) || IsBotClient(target)) scale *= TfwSupplyRules.BotDamageTaken;
        }

        /// <summary>
        /// The owner: "ammo should be infinite in musou mode". Every living player's (humans and AI Marines) weapons keep a full reserve (the perks' size), so a reload always fills the clip; a weapon
        /// that cannot reload (a brace weapon, or one with no reserve) gets its clip back once it is empty. The clip, the reload, the heat, the overheat and the vent run as ever.
        /// The autocannon (the owner, 2026-11-11: "the autocannon should also have infinite ammo in musou mode"; <see cref="AutocannonRules.EndlessClip"/>) keeps its whole clip every tick, so it never
        /// runs down (its HUD shows the endless sign); its heat and overheat still run.
        /// </summary>
        void TfwEndlessAmmo()
        {
            _tfwSupplyIds.Clear();
            foreach (var (id, p) in _players) if (p.Alive) _tfwSupplyIds.Add(id);
            foreach (int id in _tfwSupplyIds)
            {
                var p = _players[id];
                var q = p;
                foreach (var slot in (ReadOnlySpan<WeaponSlot>)[WeaponSlot.Pistol, WeaponSlot.Ranged, WeaponSlot.Second])
                {
                    if (Loadouts.WeaponIndex(Config, q, slot) < 0 || Loadouts.Weapon(Config, q, slot) is not { } weapon) continue;
                    var a = AmmoOf(q, slot);
                    var full = a with { Reserve = Math.Max(a.Reserve, weapon.ReserveSize) };
                    if ((weapon.Brace is not null || weapon.ReserveSize <= 0) && a.Clip <= 0 && weapon.ClipSize > 0) full = full with { Clip = weapon.ClipSize };
                    if (AutocannonRules.EndlessClip(weapon) && weapon.ClipSize > 0) full = full with { Clip = weapon.ClipSize };   // never spent (2026-11-11)
                    if (full != a) q = WithAmmo(q, slot, full);
                }
                if (q != p) _players[id] = q;
            }
        }

        /// <summary>
        /// The owner: "grenades should refill every 30 seconds automatically". Each living player (humans and AI Marines) carrying fewer grenades than its item's most gets one back every
        /// <see cref="TfwSupplyRules.GrenadeRefillSeconds"/>; the time counts only while it is short, and starts again at a full count or a new life. The combat stims (owner, 2026-11-09: "stimulants need to regenerate the same way grenades do") are the same item slot and run the same rule: one charge back every period, up to the item's most.
        /// </summary>
        void TfwGrenadeRefill()
        {
            _tfwSupplyIds.Clear();
            foreach (int id in _players.Keys) _tfwSupplyIds.Add(id);
            foreach (int id in _tfwSupplyIds)
            {
                var p = _players[id];
                if (!p.Alive || Loadouts.Equipment(Config, p) is not { } item || p.Grenades >= item.Count) { _tfwGrenadeTicks.Remove(id); continue; }
                float seconds = TfwSupplyRules.GrenadeRefillSeconds;
                ArsenalRefillSeconds(item, ref seconds);   // Arsenal/World.Arsenal.cs: an item's own period (W4, the charge's 30 s); 15 s for every item until then
                int period = Math.Max(1, (int)MathF.Round(seconds * Config.TickRate));
                int ticks = _tfwGrenadeTicks.GetValueOrDefault(id) + 1;
                if (ticks >= period)
                {
                    _players[id] = p with { Grenades = p.Grenades + 1 };
                    ticks = 0;
                }
                _tfwGrenadeTicks[id] = ticks;
            }
            if (_tfwGrenadeTicks.Count > _players.Count) foreach (int id in _tfwGrenadeTicks.Keys.Where(id => !_players.ContainsKey(id)).ToList()) _tfwGrenadeTicks.Remove(id);
        }

        /// <summary>Test aid: ticks player <paramref name="id"/> has waited for its next grenade (0: none counting).</summary>
        public int TfwGrenadeWait(int id) => _tfwGrenadeTicks.GetValueOrDefault(id);
    }
}
