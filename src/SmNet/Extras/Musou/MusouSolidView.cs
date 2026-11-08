using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Extras.Musou;

/// <summary>
/// musou/boss-collision, the client's side (<see cref="MusouSolids"/>): the big Orks this client was sent (<see cref="MusouClientState.SolidsAt"/>) as solid bodies for the own
/// prediction and as melee-lock targets, built exactly as the server's World builds them from its record. The client's Musou view sets it on its <see cref="GameClient"/>.
/// </summary>
public sealed class MusouSolidView : IExtrasSolids
{
    readonly MusouClientState _state;
    readonly MusouRules _rules;
    readonly SimConfig _config;
    readonly List<MusouSolid> _scratch = new();

    public MusouSolidView(MusouClientState state, SimConfig config)
    {
        _state = state;
        _config = config;
        _rules = config.MusouRules ?? MusouRules.Default;
    }

    public void AddBodies(uint tick, ref BodyView? view)
    {
        if (view is null) return;
        _state.SolidsAt(tick, _scratch);
        MusouSolids.AddBodies(ref view, _scratch, _rules);
    }

    public IReadOnlyDictionary<int, PlayerState> WithLockTargets(uint tick, in PlayerState self, IReadOnlyDictionary<int, PlayerState> view)
    {
        _state.SolidsAt(tick, _scratch);
        return MusouSolids.WithLockTargets(view, _scratch, self, _rules, _config);
    }
}
