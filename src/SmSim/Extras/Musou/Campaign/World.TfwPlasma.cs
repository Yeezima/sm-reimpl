using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmSim;

/// <summary>
/// Musou Mode's plasma (the owner, 2026-11-08: "in musou mode all plasma weapons should pierce all enemies the shot passes through + do burning lingering plasma damage over time to them"). The piercing is
/// <c>World.TfwMelee.cs</c>'s (<see cref="TfwCombatRules.PlasmaShot"/> lifts the per-weapon cap and the damage fall-off for the plasma gun, pistol and cannon, light and charged shots alike; the map still
/// stops a ball, and a charged ball's splash still goes off where it ends); this file is the burn each enemy it hits carries. A burn is <see cref="TfwCombatRules.PlasmaBurnShare"/> of the hit that set it,
/// dealt in <see cref="TfwCombatRules.PlasmaBurnTicks"/> equal ticks <see cref="TfwCombatRules.PlasmaBurnInterval"/> apart; a new hit renews it (the ticks start over, the stronger tick stands) and never
/// stacks. A boss takes <see cref="TfwCombatRules.PlasmaBurnBossShare"/> of each tick. The ticks are quiet (no Hit event: no flinch, blood or pain cue per tick); the kill is the shooter's, and the clients
/// draw a flame from a tag 11 record (<see cref="TfwFireKind.Burn"/>) sent when a burn starts and again at most once a second while hits renew it. Every number is ours (spec "Plasma pierce and burn").
/// </summary>
public sealed partial class World
{
    sealed class TfwBurn
    {
        public int Owner;
        public float Tick;       // damage of one burn tick (before a boss's share)
        public int Left;         // ticks still to deal
        public uint Next;        // the world tick of the next one
        public uint Told;        // the world tick of the last record to the clients
    }

    readonly Dictionary<ushort, TfwBurn> _tfwBurns = new();
    readonly List<ushort> _tfwBurnDone = new();

    /// <summary>For the tests and the fight's report: enemies burning now, burn ticks dealt, and burns set (renewals count too).</summary>
    public int TfwBurning => _tfwBurns.Count;
    public int TfwBurnTicksDealt { get; private set; }
    public int TfwBurnsSet { get; private set; }

    /// <summary>
    /// <paramref name="enemy"/> was hit for <paramref name="hit"/> by <paramref name="owner"/>'s plasma: it burns (a new burn, or the old one renewed to the full ticks with the stronger tick). An enemy behind a
    /// Weirdboy's shield does not catch fire.
    /// </summary>
    void TfwPlasmaBurn(ushort enemy, int owner, float hit, TfwCombatRules war)
    {
        var musou = _musou!;
        if (!(hit > 0f) || musou.ShieldOf(enemy) > 0f || !musou.TryGet(enemy, out var e) || !e.Alive) return;
        float tick = hit * war.PlasmaBurnShare / war.PlasmaBurnTicks;
        uint interval = (uint)Math.Max(1, (int)MathF.Round(war.PlasmaBurnInterval * Config.TickRate));
        TfwBurnsSet++;
        if (_tfwBurns.TryGetValue(enemy, out var burn))
        {
            burn.Left = war.PlasmaBurnTicks;
            if (tick > burn.Tick) { burn.Tick = tick; burn.Owner = owner; }   // renewed, not stacked: the stronger tick stands, with its owner
        }
        else
        {
            // The first tick lands one interval later, spread over the interval by the enemy's id so a shot through a crowd does not tick the whole crowd on one tick.
            _tfwBurns[enemy] = burn = new TfwBurn { Owner = owner, Tick = tick, Left = war.PlasmaBurnTicks, Next = Tick + interval + (uint)(enemy % interval), Told = 0 };
        }
        uint second = (uint)Math.Max(1, Config.TickRate);
        if (burn.Told == 0 || Tick - burn.Told >= second)
        {
            burn.Told = Tick;
            _tfw?.Fire.Burn(Tick, enemy, e.Position);
        }
    }

    /// <summary>The burn ticks of this tick, in enemy order of burning; a burn ends with its last tick or with the enemy.</summary>
    void TfwBurnStep()
    {
        if (_tfwBurns.Count == 0) return;
        var musou = _musou!;
        if (musou.War is not { } war) { _tfwBurns.Clear(); return; }
        uint interval = (uint)Math.Max(1, (int)MathF.Round(war.PlasmaBurnInterval * Config.TickRate));
        _tfwBurnDone.Clear();
        foreach (var (id, burn) in _tfwBurns)
        {
            if (!musou.TryGet(id, out var e) || !e.Alive) { _tfwBurnDone.Add(id); continue; }
            if (Tick < burn.Next) continue;
            if (musou.ShieldOf(id) <= 0f)
            {
                float amount = burn.Tick * (TfwCombatRules.IsBoss(e.Type) ? war.PlasmaBurnBossShare : 1f);
                musou.Damage(id, burn.Owner, amount, Vector3.Zero, out bool killed, marker: 1, cause: MusouDeathKind.Shot, quiet: true);
                TfwBurnTicksDealt++;
                if (killed) { _tfwBurnDone.Add(id); continue; }
            }
            burn.Next += interval;
            if (--burn.Left <= 0) _tfwBurnDone.Add(id);
        }
        foreach (ushort id in _tfwBurnDone) _tfwBurns.Remove(id);
    }
}
