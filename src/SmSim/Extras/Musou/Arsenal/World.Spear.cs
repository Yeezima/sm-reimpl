using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Arsenal;

namespace SmSim;

/// <summary>
/// The power spear's two call sites in the World (W3; <see cref="SpearRules"/>; docs/new-weapons-design.md section 6): its sweeps' volume (<see cref="ArsenalCleave"/>, from the cleave of
/// <c>World.MusouCombat.cs</c>) and the impale at the end of its jump-pack dive (<see cref="ArsenalJump"/>, from <c>ExtrasJump</c>). Both run only in a Musou match and act only for a player with the
/// spear chosen; anyone else's cleave and landing are untouched.
/// </summary>
public sealed partial class World
{
    partial void ArsenalCleave(int attacker, in PlayerState state, MeleeStep step, ref float range, ref float minCos, ref int most, ref float damage)
    {
        if (!SpearRules.Wields(Config, state)) return;
        range = SpearRules.ReachMetres;       // from the feet, the war cleave's own scale not applied on top (the 15 m is the whole reach)
        minCos = SpearRules.MinCos;
        most = SpearRules.MostOrks;
        // The damage is the step's own: the combo was built at the spear's damage (SpearRules.FromHammer).
    }

    partial void ArsenalJump(int player, JumpEventKind kind)
    {
        if (kind != JumpEventKind.Impact || _musou is not { } musou || !_players.TryGetValue(player, out var state) || !SpearRules.Wields(Config, state)) return;
        Span<ushort> near = stackalloc ushort[4];
        int n = musou.Query(state.Position, SpearRules.ImpaleReach, near, Config.MeleeVerticalReach);
        for (int k = 0; k < n; k++)
        {
            if (!musou.TryGet(near[k], out var e) || !e.Alive) continue;   // the pack's blast may have killed the nearest already: the next one is the nearest alive
            var away = new Vector2(e.Position.X - state.Position.X, e.Position.Y - state.Position.Y);
            var push = away.LengthSquared() > 1e-6f ? Vector2.Normalize(away) : new Vector2(-MathF.Sin(state.Yaw), MathF.Cos(state.Yaw));
            musou.Damage(e.Id, player, SpearRules.ImpaleDamage * Stims.MeleeDealt(Config, state), new Vector3(push, 0f), out _, marker: 3, cause: MusouDeathKind.Melee);
            MusouMeleeHeal(player, e.Id);   // a melee strike like the rest: the heal and the armour back
            break;
        }
    }
}
