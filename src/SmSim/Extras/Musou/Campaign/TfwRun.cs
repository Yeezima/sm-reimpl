using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The run (design sections D and F): the Stormbird's transport and supplies, the war's clock, the deploy, the battle, the pickup, the revive window, the emergency extraction and the end.
/// The World steps it from <c>TfwStep</c> while it is in play. Commands are applied in the order they came; anything the phase does not allow is dropped (a client resends only until echoed).
/// The whole team travels together: with several humans the fallen wait while another still fights, and an extraction takes all of them (ours; the slice is solo, open question 11).
/// </summary>
public sealed partial class TfwRun
{
    readonly List<int> _deployed = new();
    readonly Dictionary<int, int> _reviveTicks = new();    // a fallen human: ticks left of its revive window (0: closed)
    readonly List<int> _work = new();
    Vector3[] _feet = new Vector3[16];
    TfwRunPhase _fighting;
    double _nextHqTransport;
    int _warTicks;

    public TfwRun()
    {
        Reset();
        ReviveWindow = ReviveOpen;
        LordFallen = lane => War.WoundLord(lane);
    }

    /// <summary>The war of this run.</summary>
    public WarSim War { get; private set; } = new();
    /// <summary>The battle under way (null in the hub and in travel).</summary>
    public TfwBattle? Battle { get; private set; }
    public int Transport { get; private set; }
    public int Supplies { get; private set; }
    /// <summary>Ticks left of the travel, the pickup's countdown and the extraction's prompt (0: none under way).</summary>
    public int TravelTicks { get; private set; }
    public int PickupTicks { get; private set; }
    public int ExtractionTicks { get; private set; }
    /// <summary>Whether a deploy has been made this run: from then on the director's phase shows the war (<see cref="MusouPhase.Campaign"/>).</summary>
    public bool Deployed { get; private set; }
    /// <summary>The humans in the field.</summary>
    public IReadOnlyList<int> InField => _deployed;

    /// <summary>Test seam for the AI Marines: whether a player's retainer can revive it (null: the World asks the roster).</summary>
    public Func<int, bool>? RetainerUp { get; set; }

    /// <summary>Test seam: an order as it reaches the Marines.</summary>
    public Action<int, TfwCommandRecord>? OrderSeen { get; set; }

    public TfwRunPhase Phase => War.Phase;
    public bool Ended => Phase is TfwRunPhase.Won or TfwRunPhase.Lost;

    /// <summary>The objectives of the battle under way (none outside one; tag 8).</summary>
    public IReadOnlyList<TfwObjective> Objectives => Battle?.Objectives ?? [];

    /// <summary>Whether <paramref name="player"/>'s revive window is open (<see cref="ReviveWanted"/> asks this).</summary>
    public bool ReviveOpen(int player) => _reviveTicks.GetValueOrDefault(player) > 0;

    /// <summary>Ticks left of <paramref name="player"/>'s revive window.</summary>
    public int ReviveTicks(int player) => _reviveTicks.GetValueOrDefault(player);

    /// <summary>A new run: the war from its start and full reserves.</summary>
    internal void Reset()
    {
        War = new WarSim();
        Battle = null;
        Transport = TfwBattleRules.StartTransport;
        Supplies = TfwBattleRules.StartSupplies;
        TravelTicks = PickupTicks = ExtractionTicks = 0;
        Deployed = false;
        ActivePosition = null;
        _deployed.Clear();
        _reviveTicks.Clear();
        _nextHqTransport = TfwBattleRules.HqTransportSeconds;
        _warTicks = 0;
    }

    /// <summary>One tick in play, before the fight.</summary>
    internal void Step(World w)
    {
        int rate = Math.Max(1, w.Config.TickRate);
        Commands(w, rate);
        if (Ended) return;
        if (++_warTicks >= rate)
        {
            _warTicks = 0;
            War.Step();
            for (; War.Clock >= _nextHqTransport; _nextHqTransport += TfwBattleRules.HqTransportSeconds) Transport++;
        }
        WarTimeScale = (float)War.TimeScale;
        if (Ended) return;
        if (Phase == TfwRunPhase.Travel) { if (--TravelTicks <= 0) Land(w, rate); }
        else if (Phase is TfwRunPhase.Battle or TfwRunPhase.FinalAssault or TfwRunPhase.Pickup) Fight(w, rate);
        ActivePosition = Battle?.ActivePosition;
    }

    /// <summary>Applies the queue; out of play (or after the end) the commands are dropped.</summary>
    internal void Commands(World? w, int rate)
    {
        while (TryDequeue(out int player, out var c))
        {
            if (w is null || Ended) continue;
            switch (c.Command)
            {
                case TfwCommand.Deploy: Deploy(w, player, c.Argument, rate); break;
                case TfwCommand.RequestPickup: RequestPickup(w, player, rate); break;
                case TfwCommand.CancelPickup: CancelPickup(player); break;
                default: Order(w, player, c); break;
            }
        }
    }

    /// <summary>An order to the Marines: only with a battle on, from a human in it; its point is the client's, so it is kept inside the arena.</summary>
    void Order(World w, int player, TfwCommandRecord c)
    {
        if (Battle is null || !_deployed.Contains(player)) return;
        float half = TfwMap.ArenaHalf;
        var point = new Vector3(Math.Clamp(c.Point.X, -half, half), Math.Clamp(c.Point.Y, -half, half), Math.Clamp(c.Point.Z, 0f, TfwMap.WallHeight));
        var order = c with { Point = point };
        OrderSeen?.Invoke(player, order);
        w.TfwOrderMarines(player, order);
    }

    void Deploy(World w, int player, byte lane, int rate)
    {
        if (Phase != TfwRunPhase.Hub || lane > (byte)TfwLane.C || Transport < TfwBattleRules.DeployCost) return;
        if (TfwContracts.IsPuppet(player) || !w.Players.TryGetValue(player, out var p) || !p.Alive || !InStormbird(p.Position)) return;
        Transport -= TfwBattleRules.DeployCost;
        TravelTicks = Math.Max(1, (int)MathF.Round(TfwBattleRules.TravelSeconds * rate));
        Deployed = true;
        War.Enter(TfwRunPhase.Travel, (TfwLane)lane);
    }

    static bool InStormbird(Vector3 p) =>
        MathF.Abs(p.X - TfwMap.StormbirdCentre.X) <= TfwMap.StormbirdSize.X / 2f + 1f && MathF.Abs(p.Y - TfwMap.StormbirdCentre.Y) <= TfwMap.StormbirdSize.Y / 2f + 1f;

    void RequestPickup(World w, int player, int rate)
    {
        if (Phase is not (TfwRunPhase.Battle or TfwRunPhase.FinalAssault) || Battle is not { } battle || !_deployed.Contains(player) || !w.Players.TryGetValue(player, out var p) || !p.Alive) return;
        if (!battle.Won && Transport < TfwBattleRules.PickupCost) return;
        PickupTicks = Math.Max(1, (int)MathF.Round(TfwBattleRules.PickupSeconds * rate));
        _fighting = Phase;
        War.Enter(TfwRunPhase.Pickup, War.BattleLane);
    }

    void CancelPickup(int player)
    {
        if (Phase == TfwRunPhase.Pickup && _deployed.Contains(player)) EndPickup();
    }

    void EndPickup()
    {
        PickupTicks = 0;
        War.Enter(_fighting, War.BattleLane);
    }

    /// <summary>The landing: the battle (a stronghold's lane: the final assault), a fresh life for each human at the landing zone, the AI Marines.</summary>
    void Land(World w, int rate)
    {
        var lane = War.BattleLane;
        var state = War.LaneState(lane);
        bool final = state.Front == WarRules.FrontStronghold;
        var layout = TfwLayout.For(lane, final ? WarRules.Positions : state.Front + 1);
        var bonus = final ? War.FinalBonus(lane) : default;
        w.TfwClearField();
        _deployed.Clear();
        foreach (int id in w.Players.Keys) if (!TfwContracts.IsPuppet(id)) _deployed.Add(id);
        _deployed.Sort();
        Battle = new TfwBattle(w.Musou!, w.TfwGuards!, layout, state.Ork, state.Imperial, _deployed.Count, final, bonus);
        Battle.Start();
        _fighting = final ? TfwRunPhase.FinalAssault : TfwRunPhase.Battle;
        War.Enter(_fighting, lane);
        var way = new Vector2(layout.Hq.X - layout.LandingZone.X, layout.Hq.Y - layout.LandingZone.Y);
        float yaw = MathF.Atan2(-way.X, way.Y);
        for (int k = 0; k < _deployed.Count; k++)
        {
            int id = _deployed[k];
            var at = layout.LandingZone + new Vector3(k % 2 * 3f - 1.5f, k / 2 * -3f, 0f);
            w.TfwRespawnAt(id, at, yaw);
            w.TfwSpawnRetainer(id, at + new Vector3(0f, -2f, 0f), yaw);
        }
        for (int l = 0; l < 3; l++)
        {
            var other = (TfwLane)l;
            if ((final || other == lane) && War.LaneState(other).LordState == WarRules.LordPresent)
                w.TfwSpawnLord(other, layout.LandingZone + new Vector3(l * 3f - 3f, 3f, 0f), yaw);
        }
    }

    void Fight(World w, int rate)
    {
        var battle = Battle!;
        _deployed.RemoveAll(id => !w.Players.ContainsKey(id));
        if (_deployed.Count == 0) { Leave(w, !battle.Won); return; }

        if (_feet.Length < w.Players.Count) _feet = new Vector3[w.Players.Count * 2];
        int n = 0;
        foreach (var p in w.Players.Values) if (p.Alive) _feet[n++] = p.Position;
        var change = battle.Step(_feet.AsSpan(0, n));
        if (change.HasFlag(TfwBattleChange.DepotSecured))
        {
            Transport += TfwBattleRules.DepotTransport;
            Supplies += TfwBattleRules.DepotSupplies;
        }
        if (change.HasFlag(TfwBattleChange.Won))
        {
            if (_fighting == TfwRunPhase.FinalAssault) { PickupTicks = 0; War.Enter(TfwRunPhase.Won); return; }
            War.PlayerWon(War.BattleLane);
        }
        Downed(w, rate);
        if (Ended || Battle is null || Phase != TfwRunPhase.Pickup || --PickupTicks > 0) return;
        if (!battle.Won) Transport -= TfwBattleRules.PickupCost;
        Leave(w, !battle.Won);
    }

    /// <summary>
    /// The fallen: a death cancels a pickup; a retainer standing gives a revive window; with nobody left fighting and no window open the extraction's prompt runs, then it is paid for or
    /// the run is lost.
    /// </summary>
    void Downed(World w, int rate)
    {
        bool fighting = false, waiting = false;
        foreach (int id in _deployed)
        {
            var p = w.Players[id];
            if (p.Alive) { fighting = true; _reviveTicks.Remove(id); continue; }
            if (Phase == TfwRunPhase.Pickup) EndPickup();
            bool up = RetainerUp?.Invoke(id) ?? w.TfwRetainerUp(id);
            if (!_reviveTicks.TryGetValue(id, out int left)) left = up ? Math.Max(1, (int)MathF.Round(TfwBattleRules.ReviveWindowSeconds * rate)) : 0;
            left = up ? Math.Max(0, left - 1) : 0;
            _reviveTicks[id] = left;
            waiting |= left > 0;
        }
        if (fighting || waiting) { ExtractionTicks = 0; return; }
        if (ExtractionTicks == 0) ExtractionTicks = Math.Max(1, (int)MathF.Round(TfwBattleRules.ExtractionPromptSeconds * rate));
        else if (--ExtractionTicks > 0) return;
        else if (Supplies < TfwBattleRules.ExtractionSupplies) { War.Enter(TfwRunPhase.Lost); return; }
        else
        {
            Supplies -= TfwBattleRules.ExtractionSupplies;
            Leave(w, !Battle!.Won);
        }
    }

    /// <summary>Back to the Stormbird, each with a fresh life: <paramref name="left"/>, the battle counts as left.</summary>
    void Leave(World w, bool left)
    {
        var battle = Battle!;
        if (left) War.PlayerLeft(War.BattleLane, battle.Secured, battle.OrksKilled, w.TfwGuards!.Lost);
        for (int k = 0; k < _deployed.Count; k++)
        {
            var spawn = TfwMap.Spawns[k % TfwMap.Spawns.Length];
            if (w.Players.ContainsKey(_deployed[k])) w.TfwRespawnAt(_deployed[k], spawn.Position, spawn.Yaw);
        }
        w.TfwClearField();
        Battle = null;
        ActivePosition = null;
        PickupTicks = ExtractionTicks = 0;
        _deployed.Clear();
        _reviveTicks.Clear();
        War.Enter(TfwRunPhase.Hub, War.BattleLane);
    }

    /// <summary>After the fight: the humans in the field who are down wait (the core's timer must not bring them back in the Stormbird), and after a lost run nobody returns until the new one.</summary>
    internal void HoldRespawns(World w)
    {
        _work.Clear();
        if (Phase == TfwRunPhase.Lost) foreach (int id in w.Players.Keys) _work.Add(id);
        else _work.AddRange(_deployed);
        foreach (int id in _work) w.TfwHoldRespawn(id);
    }
}
