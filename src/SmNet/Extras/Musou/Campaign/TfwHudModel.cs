using System.Globalization;
using System.Numerics;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Extras.Musou.Campaign;

/// <summary>The Stormbird's two stations (design section G; client-side prompts, the positions only are the map's).</summary>
public enum TfwStation : byte { None = 0, Destination = 1, Equipment = 2 }

/// <summary>The order keys pressed this frame (design section G: Z, X and V act on the press, P held for <see cref="TfwOrderKeys.PickupHoldSeconds"/>).</summary>
[Flags]
public enum TfwOrderPress : byte { None = 0, Follow = 1, Hold = 2, Attack = 4, Pickup = 8 }

/// <summary>
/// The order keys, engine-free: edges of Z, X and V, and a press of P that counts only after it has been held for <see cref="PickupHoldSeconds"/>, once a press. The view reads the keys in <c>TfwFrame</c>;
/// <paramref name="enabled"/> in <see cref="Update"/> is false while they mean nothing (no battle, a menu open): a key held then does not act when they mean something again.
/// </summary>
public sealed class TfwOrderKeys
{
    /// <summary>How long P is held for a pickup, or to call one off (design section G).</summary>
    public const float PickupHoldSeconds = 1f;

    bool _z, _x, _v, _pickupDone;
    float _held;

    /// <summary>How far the hold of P has come, 0 to 1 (the HUD's bar).</summary>
    public float PickupHold => Math.Clamp(_held / PickupHoldSeconds, 0f, 1f);

    public TfwOrderPress Update(bool z, bool x, bool v, bool p, float seconds, bool enabled)
    {
        var press = TfwOrderPress.None;
        if (enabled)
        {
            if (z && !_z) press |= TfwOrderPress.Follow;
            if (x && !_x) press |= TfwOrderPress.Hold;
            if (v && !_v) press |= TfwOrderPress.Attack;
        }
        _z = z; _x = x; _v = v;
        if (!p) { _held = 0f; _pickupDone = false; return press; }
        if (!enabled) { _held = 0f; _pickupDone = true; return press; }
        if (_pickupDone) return press;
        _held += Math.Max(0f, seconds);
        if (_held < PickupHoldSeconds) return press;
        _held = 0f;
        _pickupDone = true;
        return press | TfwOrderPress.Pickup;
    }
}

/// <summary>The words and rules of the war's HUD and war map (design section G), engine-free so the tests read them. Layout, wording and colours are ours (kind: ours).</summary>
public static class TfwHudModel
{
    /// <summary>The station whose pad <paramref name="feet"/> stands on: in the hub only (the prompts are client-side; the server checks a Deploy itself).</summary>
    public static TfwStation StationAt(Vector3 feet, TfwHub hub)
    {
        float r2 = TfwMap.StationRadius * TfwMap.StationRadius;
        if (Flat(feet, hub.DestinationStation) <= r2) return TfwStation.Destination;
        return Flat(feet, hub.EquipmentStation) <= r2 ? TfwStation.Equipment : TfwStation.None;
    }

    static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).LengthSquared();

    /// <summary>The station's prompt ("E: DESTINATION"); <paramref name="key"/>: the Use action's key on the device in use. Empty at none.</summary>
    public static string StationPrompt(TfwStation station, string key) => station switch
    {
        TfwStation.Destination => $"{key}: DESTINATION",
        TfwStation.Equipment => $"{key}: EQUIPMENT",
        _ => "",
    };

    public static TfwLaneState Lane(in TfwWarView war, TfwLane lane) => lane switch { TfwLane.A => war.A, TfwLane.B => war.B, _ => war.C };

    /// <summary>Whether a Deploy can be asked for now: the hub, the match in play, a transport left. The server decides again.</summary>
    public static bool CanDeploy(in TfwWarView war, bool playing) => playing && war.Phase == TfwRunPhase.Hub && war.Transport >= TfwBattleRules.DeployCost;

    /// <summary>Why a Deploy cannot be asked for, in words; empty when it can.</summary>
    public static string DeployNote(in TfwWarView war, bool playing) =>
        war.Phase != TfwRunPhase.Hub ? "NOT WHILE THE STORMBIRD IS AWAY" : !playing ? "THE WAR HAS NOT STARTED" : war.Transport < TfwBattleRules.DeployCost ? "NO TRANSPORT LEFT" : "";

    /// <summary>Whether the lane's next battle is the final assault.</summary>
    public static bool IsFinal(in TfwLaneState lane) => lane.Front == WarRules.FrontStronghold;

    /// <summary>A front in words.</summary>
    public static string FrontText(sbyte front) => front switch
    {
        WarRules.FrontBesieged => "HQ BESIEGED",
        WarRules.FrontStronghold => "STRONGHOLD",
        _ => $"NODE {front}",
    };

    /// <summary>The front as filled pips, 0 to 4 of them (front -1 to 3).</summary>
    public static int Pips(sbyte front) => Math.Clamp(front - WarRules.FrontBesieged, 0, WarRules.FrontStronghold - WarRules.FrontBesieged);

    /// <summary>What the map says of a lane's lord.</summary>
    public static string LordText(TfwLane lane, byte state) =>
        World.TfwLordName(lane).ToUpperInvariant() + (state == WarRules.LordWounded ? " (WOUNDED)" : "");

    /// <summary>The HQ's line: its lanes under siege, or that it stands.</summary>
    public static string HqText(in TfwWarView war)
    {
        string siege = "";
        for (int l = 0; l < 3; l++)
            if (Lane(war, (TfwLane)l).Front == WarRules.FrontBesieged) siege += (siege.Length > 0 ? ", " : "") + (TfwLane)l;
        return siege.Length > 0 ? $"HQ BESIEGED: {siege}" : "HQ HOLDS";
    }

    /// <summary>The war clock as minutes and seconds.</summary>
    public static string ClockText(float seconds)
    {
        int s = Math.Max(0, (int)seconds);
        return string.Create(CultureInfo.InvariantCulture, $"{s / 60}:{s % 60:00}");
    }

    /// <summary>The war's time scale ("WAR TIME x0.25").</summary>
    public static string ScaleText(float scale) => string.Create(CultureInfo.InvariantCulture, $"WAR TIME x{scale:0.##}");

    public static string ObjectiveName(int index, bool final) =>
        index == TfwBattle.CommanderObjective ? final ? "WARBOSS" : "COMMANDER" : index == 1 ? "B  DEPOT" : $"{(char)('A' + Math.Clamp(index, 0, 25))}";

    /// <summary>
    /// The label over a position in the world and its row on the HUD (owner, 2026-11-01: "label each point so the player can easily identify what he can work on capturing next"): its letter (A, B, C in the order a
    /// battle takes them) and its state, for example "A  HELD" or "C  SECURING 42%".
    /// </summary>
    public static string ObjectiveLabel(in TfwObjective o, bool final, int orksOn = 0) => $"{ObjectiveName(o.Index, final)}  {ObjectiveState(o)}{ClearingNote(o, orksOn)}";

    /// <summary>
    /// What stops a capture, said where the player looks: a position being cleared shows how many Orks the client knows standing in it (" - 7 ORKS"); a point is taken with none alive inside its volume, whatever they are doing.
    /// The count is the client's own (the enemies it was sent, inside the radius and <see cref="ClearingReach"/> up and down), a hint and not the server's count.
    /// </summary>
    public static string ClearingNote(in TfwObjective o, int orksOn) => o.State == TfwPositionState.Clearing && orksOn > 0 ? $" - {orksOn} ORK{(orksOn == 1 ? "" : "S")}" : "";

    /// <summary>The up and down reach the client counts Orks over (the map's own volumes are 10 m; the client does not know the server's value).</summary>
    public const float ClearingReach = 10f;

    /// <summary>One line of the objective tracker.</summary>
    public static string ObjectiveState(in TfwObjective o) => o.State switch
    {
        TfwPositionState.Locked => "LOCKED",
        TfwPositionState.Held => "HELD",
        TfwPositionState.Clearing => "CLEARING",
        TfwPositionState.Securing => string.Create(CultureInfo.InvariantCulture, $"SECURING {MathF.Floor(o.Progress * 100f):0}%"),
        _ => "SECURED",
    };

    /// <summary>The Guardsmen's line.</summary>
    public static string GuardsText(in TfwOwnView own) => own.Guards == 0 && own.Squads == 0 && own.Reserves == 0 ? "" : $"GUARDS {own.Guards}, {own.Squads} SQUADS + {own.Reserves} IN RESERVE";

    /// <summary>The retainer's line: its state and the order in force.</summary>
    public static string RetainerText(in TfwOwnView own) => own.Retainer switch
    {
        TfwRetainerState.Ready => $"RETAINER: {OrderText(own.Order)}",
        TfwRetainerState.Reviving => "RETAINER: REVIVING YOU",
        TfwRetainerState.Recovering => $"RETAINER RECOVERING: {ClockText(own.RecoverySeconds)} WAR TIME",
        _ => "RETAINER: STANDING BY",
    };

    public static string OrderText(MarineOrder order) => order switch { MarineOrder.Hold => "HOLD", MarineOrder.Attack => "ATTACK", _ => "FOLLOW" };

    /// <summary>The big line in the middle of the screen, for what the run asks of the player now; empty when nothing does.</summary>
    public static string Prompt(in TfwWarView war, bool alive)
    {
        var own = war.Own;
        switch (war.Phase)
        {
            case TfwRunPhase.Won: return "THE WARBOSS IS DEAD. THE WAR IS WON";
            case TfwRunPhase.Lost: return "THE WAR IS LOST";
            case TfwRunPhase.Travel: return $"THE STORMBIRD LANDS IN {own.TravelSeconds} S";
            case TfwRunPhase.Hub: return "";
        }
        if (!alive)
        {
            if (own.ReviveSeconds > 0 && own.ReviveTenths > 0) return $"YOUR RETAINER REVIVES YOU: {own.ReviveTenths * 10}%";
            if (own.ReviveSeconds > 0) return $"YOUR RETAINER IS COMING: {own.ReviveSeconds} S";
            if (own.ExtractionSeconds > 0)
            {
                if (!own.BattleWon && war.Supplies < TfwBattleRules.ExtractionSupplies) return $"NO SUPPLIES FOR AN EXTRACTION: THE WAR IS LOST IN {own.ExtractionSeconds} S";
                int cost = ExtractionCost(war);   // after a won battle what is left pays, and the run goes on
                return cost == 0 ? $"EMERGENCY EXTRACTION IN {own.ExtractionSeconds} S" : $"EMERGENCY EXTRACTION IN {own.ExtractionSeconds} S: IT COSTS {cost} {(cost == 1 ? "SUPPLY" : "SUPPLIES")}";
            }
            return "";
        }
        if (war.Phase == TfwRunPhase.Pickup) return $"PICKUP IN {own.PickupSeconds} S: STAY ALIVE";
        return own.BattleWon ? "BATTLE WON: HOLD P FOR A FREE PICKUP" : "";
    }

    /// <summary>Whether P held means a pickup now (a request, or calling a running one off), and what it says beside the bar.</summary>
    public static string PickupKeyText(in TfwWarView war) => war.Phase switch
    {
        TfwRunPhase.Pickup => "CALLING THE PICKUP OFF",
        TfwRunPhase.Battle or TfwRunPhase.FinalAssault => "CALLING A PICKUP",
        _ => "",
    };

    /// <summary>The boss bar's name: the battle's commander, or the Warboss in the final assault.</summary>
    public static string BossName(in TfwWarView war, IReadOnlyList<TfwObjective> objectives) => war.Phase == TfwRunPhase.FinalAssault || objectives.Count == 0 ? "ORK WARBOSS" : "ORK COMMANDER";

    /// <summary>What an extraction takes now: its price, or after a won battle what is left of it.</summary>
    public static int ExtractionCost(in TfwWarView war) => Math.Min((int)war.Supplies, TfwBattleRules.ExtractionSupplies);

    /// <summary>Whether the run holds this client's respawn: it is in the battle under way. One who joined after the landing waits in the Stormbird on the core's respawn, so the match HUD's line is true for it and there is no KILLED banner.</summary>
    public static bool RunHoldsRespawn(in TfwWarView war) => InBattle(war.Phase) && war.Own.Deployed;

    /// <summary>Whether a battle is on for the player: the order keys mean something.</summary>
    public static bool InBattle(TfwRunPhase phase) => phase is TfwRunPhase.Battle or TfwRunPhase.FinalAssault or TfwRunPhase.Pickup;

    /// <summary>Whether a battle's source still sends (the battle's own rule, <see cref="TfwBattle.SourceActive"/>, from the objectives): every source in the final assault.</summary>
    public static bool SourceActive(int index, IReadOnlyList<TfwObjective> objectives, bool won, bool final)
    {
        if (won) return false;
        if (final || index >= objectives.Count) return true;
        return index < WarRules.Positions ? objectives[index].State == TfwPositionState.Held : objectives[index].State != TfwPositionState.Locked;
    }
}
