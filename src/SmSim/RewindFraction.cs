using System.Numerics;

namespace SmSim;

public sealed partial class World
{
    /// <summary>
    /// fix/hitboxes: how far (m) a player may move in one tick before an interpolation between two ticks counts as a jump (a respawn) and keeps the
    /// earlier state (the client's <c>ViewSmoothing.TeleportDistance</c>, 3 m a tick).
    /// </summary>
    public const float RewindTeleportDistance = 3f;

    readonly Dictionary<(uint Tick, float Fraction), Dictionary<int, PlayerState>> _between = new();
    uint _betweenTick;

    /// <summary>The oldest tick a rewind may reach (<see cref="SimConfig.MaxRewindTicks"/> before now).</summary>
    uint OldestRewind => Tick > (uint)Config.MaxRewindTicks ? Tick - (uint)Config.MaxRewindTicks : 0;

    /// <summary>The players at whole tick <paramref name="tick"/> (its history; the players now for this tick or one not kept).</summary>
    IReadOnlyDictionary<int, PlayerState> PlayersAt(uint tick) => tick < Tick && _history.TryGetValue(tick, out var past) ? past : _players;

    /// <summary>
    /// The players as the client that sent <paramref name="input"/> saw them, for the whole-tick paths (melee, the melee lock, the charge): its view tick
    /// rounded by its fraction (protocol 37: the tick at or before plus one from a fraction of 0.5 on, which is the nearest tick as before the fraction), rewind capped.
    /// </summary>
    IReadOnlyDictionary<int, PlayerState> SeenBy(PlayerInput input)
    {
        long rounded = (long)input.ViewTick + (input.ViewFraction >= 0.5f ? 1 : 0);
        return PlayersAt((uint)Math.Clamp(rounded, OldestRewind, Tick));
    }

    /// <summary>
    /// fix/hitboxes (protocol 37): the players as the client that sent <paramref name="input"/> drew them, for hit-scan shots and cones: its view tick
    /// (capped), moved <see cref="PlayerInput.ViewFraction"/> of the way to the next tick (that tick's history, or the players now when it is this tick)
    /// by <see cref="Between"/>; and the jump poses they showed at the view tick (<see cref="FlightAt"/>). A capped view tick or a fraction of 0: the
    /// whole tick. Built once per tick and view.
    /// </summary>
    (IReadOnlyDictionary<int, PlayerState> Players, IReadOnlyDictionary<int, FlightView> Flight) SeenAt(PlayerInput input)
    {
        uint tick = Math.Clamp(input.ViewTick, OldestRewind, Tick);
        var past = PlayersAt(tick);
        var flight = FlightAt(tick);
        float f = input.ViewFraction;
        if (!(f > 0f) || tick != input.ViewTick || tick >= Tick || ReferenceEquals(past, _players)) return (past, flight);
        if (_betweenTick != Tick) { _between.Clear(); _betweenFlight.Clear(); _betweenTick = Tick; }
        if (_between.TryGetValue((tick, f), out var built)) return (built, _betweenFlight[(tick, f)]);
        var next = PlayersAt(tick + 1);
        built = new Dictionary<int, PlayerState>(past.Count);
        foreach (var (id, a) in past) built[id] = next.TryGetValue(id, out var b) ? Between(a, b, f) : a;
        _between[(tick, f)] = built;
        // The poses too: the take-off time and the flight fraction moved the same part of the way to the next tick's (same phase only).
        var nextFlight = FlightAt(tick + 1);
        IReadOnlyDictionary<int, FlightView> views = flight;
        if (flight.Count > 0)
        {
            var lerped = new Dictionary<int, FlightView>(flight.Count);
            foreach (var (id, a) in flight)
                lerped[id] = nextFlight.TryGetValue(id, out var b) ? Between(a, b, f) : a;
            views = lerped;
        }
        _betweenFlight[(tick, f)] = views;
        return (built, views);
    }

    readonly Dictionary<(uint Tick, float Fraction), IReadOnlyDictionary<int, FlightView>> _betweenFlight = new();

    /// <summary>A jump pose between two ticks' (same phase: the take-off time and the flight fraction lerped; else the earlier tick's).</summary>
    public static FlightView Between(FlightView a, FlightView b, float fraction) => b.Phase != a.Phase || !(fraction > 0f) ? a : a with
    {
        Seconds = a.Seconds + (b.Seconds - a.Seconds) * fraction,
        Fraction = float.IsFinite(a.Fraction) && float.IsFinite(b.Fraction) ? a.Fraction + (b.Fraction - a.Fraction) * fraction : a.Fraction,
    };

    /// <summary>The players an input's whole-tick paths (melee, the lock, the charge) rewind to now (<see cref="SeenBy"/>; for tests and probes).</summary>
    public IReadOnlyDictionary<int, PlayerState> WholeTickView(PlayerInput input) => SeenBy(input);

    /// <summary>The players an input's hit-scan shots and cones rewind to now (<see cref="SeenAt"/>; for tests and probes).</summary>
    public IReadOnlyDictionary<int, PlayerState> ShotView(PlayerInput input) => SeenAt(input).Players;

    /// <summary>
    /// fix/hitboxes (protocol 37): a player between two consecutive ticks' states, as the client interpolates the others (<c>GameClient.RemotePlayers</c>):
    /// position and velocity lerped, yaw by the shorter way; every other field the earlier state's. A move farther than <see cref="RewindTeleportDistance"/>
    /// (a respawn), a fraction of 0 or less, or a state that is not alive in both: the earlier state.
    /// </summary>
    public static PlayerState Between(PlayerState a, PlayerState b, float fraction)
    {
        if (!(fraction > 0f) || !a.Alive || !b.Alive || Vector3.Distance(a.Position, b.Position) > RewindTeleportDistance) return a;
        fraction = MathF.Min(fraction, 1f);
        float dy = b.Yaw - a.Yaw;
        dy -= MathF.Round(dy / (2f * MathF.PI)) * 2f * MathF.PI;
        return a with { Position = Vector3.Lerp(a.Position, b.Position, fraction), Velocity = Vector3.Lerp(a.Velocity, b.Velocity, fraction), Yaw = a.Yaw + dy * fraction };
    }

    // ---- the jump poses viewers see (fix/hitboxes, S4 of the review) ----

    readonly Dictionary<int, FlightTrack> _flightTracks = new();
    Dictionary<int, FlightView> _flightNow = new();
    readonly Dictionary<uint, Dictionary<int, FlightView>> _flightHistory = new();
    static readonly Dictionary<int, FlightView> NoFlight = new();

    /// <summary>
    /// The take-off or flight pose player <paramref name="playerId"/> shows this tick, as a viewer's client draws it (<see cref="FlightPoseRules.Step"/>, advanced
    /// once per tick after everyone moved: the damped fraction, the low or high set with its hysteresis, the take-off window); none without flight volumes.
    /// </summary>
    public FlightView FlightViewOf(int playerId) => _flightNow.TryGetValue(playerId, out var v) ? v : default;

    /// <summary>The poses shown at whole tick <paramref name="tick"/> (kept as long as the players' history; now for this tick or one not kept).</summary>
    IReadOnlyDictionary<int, FlightView> FlightAt(uint tick) => tick < Tick && _flightHistory.TryGetValue(tick, out var past) ? past : _flightNow;

    /// <summary>The config a target is hit with given the poses seen (<see cref="SimConfig.HitBody(in PlayerState, FlightView)"/>; no flight volumes: its class's).</summary>
    SimConfig BodyOf(int id, in PlayerState then, IReadOnlyDictionary<int, FlightView> flight) =>
        Config.FlightHitVolumes is null ? Config.For(then.Class) : Config.HitBody(then, flight.TryGetValue(id, out var v) ? v : default);

    /// <summary>With <see cref="Step"/>'s history: keeps the poses of the tick that ends.</summary>
    void KeepFlightViews()
    {
        if (Config.FlightHitVolumes is null) return;
        _flightHistory[Tick] = _flightNow;
        _flightHistory.Remove(Tick - (uint)Math.Max(Config.MaxRewindTicks, 1) - 2);
    }

    /// <summary>After everyone moved: every player's pose this tick (raycasting the ground only for players in the air).</summary>
    void StepFlightViews()
    {
        if (Config.FlightHitVolumes is null) return;
        Dictionary<int, FlightView>? next = null;
        foreach (var (id, p) in _players)
        {
            if (!_flightTracks.TryGetValue(id, out var track))
            {
                if (!JumpPack.Jumping(p.Jump)) continue;   // nothing to track yet
                _flightTracks[id] = track = new FlightTrack();
            }
            float above = p.Alive && !p.Grounded && JumpPack.Jumping(p.Jump) ? FlightPoseRules.AboveGround(p.Position, Collision) : float.PositiveInfinity;
            float delay = Loadouts.JumpPack(Config, p)?.TakeOffDelay ?? 0.2f;
            var view = FlightPoseRules.Step(track, p, Config.TickSeconds, above, delay);
            if (view.Phase != FlightViewPhase.None) (next ??= new Dictionary<int, FlightView>())[id] = view;
        }
        _flightNow = next ?? NoFlight;
    }

    /// <summary>A player who joins again or leaves starts no view from the old one.</summary>
    void ForgetFlightView(int playerId) => _flightTracks.Remove(playerId);
}
