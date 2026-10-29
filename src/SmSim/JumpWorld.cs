using System.Numerics;

namespace SmSim;

/// <summary>
/// A jump-pack event the world resolved: a lift-off, a dive, a landing (with its drop from the apex) or a pound's impact,
/// and what its blast dealt (landing and impact).
/// </summary>
public readonly record struct JumpEvent(uint Tick, int Player, JumpEventKind Kind, Vector3 Position, float Drop,
    int Victims, float ShieldDamage, float HealthDamage, int Kills);

/// <summary>
/// Jump-pack blasts (rules **interpretation**; spec/behavior.md). The world follows each jump's highest point (server only,
/// not part of the predicted state). A pound's impact applies the pack's <see cref="JumpPackSpec.Pound"/> blast; a regular
/// landing applies the <see cref="JumpPackSpec.Landings"/> entry with the largest MinDrop not above the drop from the apex.
/// A blast is centred at the landing player's chest height above its feet and treated like a projectile's
/// (<see cref="ApplyBlast"/>: linear fall-off between the file's points, line of sight against weapon collision, the owner
/// excluded when the file's <c>ignore_self</c> is set — it is, on both). Since M28 the pound's reactions apply
/// (<see cref="SplashSpec.Reactions"/>, or the recorded <see cref="JumpPackSpec.PoundReactions"/> by name when the blast has none;
/// the file's reaction distances are all 0: no knockback); the landings' <c>reaction_flinch</c> is an upper-body flinch, not simulated.
/// </summary>
public sealed partial class World
{
    readonly Dictionary<int, float> _jumpApex = new();
    readonly List<JumpEvent> _jumps = new();

    /// <summary>Jump-pack events during the last <see cref="Step"/>.</summary>
    public IReadOnlyList<JumpEvent> LastJumps => _jumps;

    void TrackJump(int id, PlayerState next, JumpEventKind kind, List<(int, JumpEventKind, PlayerState, float)> jumps)
    {
        if (!next.Alive) { _jumpApex.Remove(id); return; }
        if (kind == JumpEventKind.Launch) _jumpApex[id] = next.Position.Z;
        if (next.Jump is JumpPhase.Ascent or JumpPhase.Hang or JumpPhase.Fall or JumpPhase.Dive)
            _jumpApex[id] = _jumpApex.TryGetValue(id, out float apex) ? MathF.Max(apex, next.Position.Z) : next.Position.Z;
        if (kind == JumpEventKind.None) return;
        float drop = 0f;
        if (kind is JumpEventKind.Land or JumpEventKind.Impact or JumpEventKind.SlashLand)
        {
            drop = _jumpApex.TryGetValue(id, out float apex) ? MathF.Max(0f, apex - next.Position.Z) : 0f;
            _jumpApex.Remove(id);
        }
        jumps.Add((id, kind, next, drop));
    }

    void ResolveJump(int player, JumpEventKind kind, PlayerState state, float drop)
    {
        var pack = Loadouts.JumpPack(Config, state);
        SplashSpec? blast = null;
        if (pack is not null && kind == JumpEventKind.Impact)
        {
            blast = pack.Pound;
            if (blast is { Reactions.Count: 0 } && pack.PoundReactions.Count > 0)
                blast = blast with { Reactions = pack.PoundReactions.Select(r => new ReactionEntry(r.Intensity, Reaction.KindOf(r.Reaction))).Where(e => e.Kind != ReactionKind.None).ToArray() };
        }
        else if (pack is not null && kind is JumpEventKind.Land or JumpEventKind.SlashLand)
            blast = JumpPack.LandingBlast(pack, drop);   // protocol 30: a slash's landing as any landing (ours; no pound blast)
        int first = _hits.Count;
        if (kind == JumpEventKind.Launch) TakeOffBlasts(player, state);   // M43: Blast Off
        if (blast is not null)
        {
            var centre = state.Position + new Vector3(0, 0, ChestOf(state));
            ApplyBlast(player, 0, centre, blast, landing: true);
        }
        var dealt = _hits.Skip(first).ToList();
        _jumps.Add(new JumpEvent(Tick, player, kind, state.Position, drop, dealt.Select(h => h.Target).Distinct().Count(),
            dealt.Sum(h => h.ShieldDamage), dealt.Sum(h => h.HealthDamage), dealt.Count(h => h.Killed)));
    }
}
