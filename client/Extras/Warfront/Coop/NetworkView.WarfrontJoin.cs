using System;
using Godot;
using SmClient.Extras.Warfront.Coop;
using SmNet.Extras.Warfront;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Coop;

namespace SmClient;

/// <summary>
/// Warfront's co-op on the networked view (slice W12): the model and panels, the Coop section in (envelope tag 46), the two commands out (tag 42), and the spectator camera's pose for a fallen player or a joiner held
/// aboard. Plain members, no hook is declared here: the routing (W5b's client envelope switch calls <see cref="WarfrontCoopSection"/>, W5a's Down section calls <see cref="WarfrontCoopDown"/>), the spectate keys, the hub
/// customizer's close (<see cref="WarfrontCoopModel.CustomizerClosed"/>) and the one place <c>SpectateCamera</c> asks <see cref="WarfrontSpectatePose"/> are the lines listed in the W12 report. Nothing runs outside a Warfront run.
/// </summary>
public partial class NetworkView
{
    WarfrontCoopModel? _wfCoop;
    WarfrontSpectateHud? _wfSpectateHud;
    WarfrontJoinPanel? _wfJoinPanel;

    /// <summary>The co-op model (created with the first section, or when the UI is built).</summary>
    internal WarfrontCoopModel WarfrontCoop => _wfCoop ??= NewWarfrontCoop();

    WarfrontCoopModel NewWarfrontCoop()
    {
        var m = new WarfrontCoopModel();
        m.ConfirmRequested += () => _client?.SendExtrasCommand(WarfrontCoopWire.CommandTag, WarfrontCoopWire.EncodeCommand(new(WarfrontCoopWire.CommandKind.ConfirmClass, 0)));
        m.WatchRequested += step => _client?.SendExtrasCommand(WarfrontCoopWire.CommandTag, WarfrontCoopWire.EncodeCommand(new(WarfrontCoopWire.CommandKind.Watch, step)));
        return m;
    }

    /// <summary>Adds the spectator HUD and the join panel to <paramref name="parent"/> (the HUD layer); once.</summary>
    internal void WarfrontCoopBuildUi(Control parent)
    {
        if (_wfSpectateHud is not null) return;
        _wfSpectateHud = new WarfrontSpectateHud(WarfrontCoop);
        _wfJoinPanel = new WarfrontJoinPanel(WarfrontCoop);
        parent.AddChild(_wfSpectateHud);
        parent.AddChild(_wfJoinPanel);
    }

    /// <summary>The Coop section bytes (tag 46). Malformed bytes are ignored. The names are resolved by the caller from the scoreboard.</summary>
    internal void WarfrontCoopSection(ReadOnlySpan<byte> payload, string leaderName, string watchName, string ownName, int level)
    {
        if (!WarfrontCoopWire.TryDecode(payload, out var s)) return;
        WarfrontCoop.OwnName = ownName;
        WarfrontCoop.Apply(s, leaderName, watchName, level, WarfrontCoopModel.PicksLeftFor(s, ownName));
    }

    /// <summary>W5a's Down section: the fallen player's phase and the seconds left.</summary>
    internal void WarfrontCoopDown(WarfrontDownPhase phase, float seconds) => WarfrontCoop.ApplyDown(phase, seconds);

    /// <summary>Whether the spectator camera is up for the player's own view (a fallen player past its window, or a joiner held aboard).</summary>
    internal bool WarfrontSpectating => _wfCoop?.Spectating == true;

    /// <summary>
    /// The raw eye and target (Godot space) for <c>SpectateFollow</c> on a watched body: behind and above it, the shape of Classic's "action" view for one subject (<c>SpectateCamera</c>, NetworkView.Seize.cs).
    /// <paramref name="subject"/> is the watched body's position in Godot space and <paramref name="yaw"/> its facing. Pure; the cut, the springs and the level cast are <c>SpectateFollow</c>'s.
    /// </summary>
    internal static (Vector3 Eye, Vector3 Target) WarfrontSpectatePose(Vector3 subject, float yaw)
    {
        var back = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        return (subject + back * 6f + Vector3.Up * 3.5f, subject + Vector3.Up * 1.2f);
    }
}
