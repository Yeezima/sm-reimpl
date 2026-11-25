using System;
using System.Collections.Generic;
using Godot;
using SmNet;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Executions;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// Executions X6: the networked view's per-frame part (<see cref="ExecutionsViewFrame"/>, from the X5a dispatcher, only with <see cref="ContentExtras.Executions"/>):
/// <list type="bullet">
/// <item><b>Prediction</b>: <see cref="ExecutionPrediction"/> set on the client's <c>ExtrasPredictInput</c> and <c>ExtrasPin</c> once per connection (the own locked ticks stepped with no stick and no
/// buttons, the state pinned by <see cref="ExecutionTrack"/> as the server pins it); cleared with the flag off.</item>
/// <item><b>The Execute marker</b> (<see cref="ExecutionMarker"/>, owner 2026-11-25: a red dot, no text) over the one target the press would take: Classic PvP Extended, over the corpse while the own offer's window is open at the view tick; Warfront, over the Ork the press would take
/// (<see cref="ExecutionPromptRules.WarfrontPick"/>, recomputed at most <see cref="ExecutionPromptRules.PromptHz"/> times a second after the cheap refusals). Nothing on cooldown (the prompt hides; no
/// cooldown indicator, lead 39), airborne, without a melee family, in a Chaos run before X4b, for a puppet, or while executing.</item>
/// <item><b>The shell</b>: <see cref="OvershieldShell"/> on every living character with an overshield (the own from its Shield, the others from the server's overshield list), freed at 0, on death and
/// with the character.</item>
/// </list>
/// </summary>
public partial class NetworkView
{
    ExecutionPrediction? _execPrediction;
    GameClient? _execPredictClient;
    readonly PromptThrottle _execPromptThrottle = new();
    readonly List<MusouEnemy> _execNear = new();
    readonly List<System.Numerics.Vector3> _execBystanders = new();
    readonly HashSet<ushort> _execReserved = new();
    readonly Dictionary<int, OvershieldShell> _execShells = new();
    readonly Dictionary<int, PlayerState> _execOthers = new();
    ushort _execPickedOrk;
    int _execChoices;
    ExecutionPromptHidden _execPromptHidden = ExecutionPromptHidden.NotAlive;

    /// <summary>Why the prompt is hidden now (<see cref="ExecutionPromptHidden.None"/>: shown), the Warfront choices run and the Ork picked (the self-test and the log).</summary>
    internal ExecutionPromptHidden ExecutionPromptHiddenNow => _execPromptHidden;
    internal int ExecutionPromptChoices => _execChoices;
    internal ExecutionPrediction? ExecutionPredictionNow => _execPrediction;
    internal int ExecutionShellsShown => _execShells.Count;

    /// <summary>A row set the test aids put in place of the match's (null: the match's).</summary>
    ExecutionRowSet? _execRowsOverride;

    /// <summary>
    /// The match's row set: <see cref="SimConfig.Executions"/> (X3 puts the install's catalogue there through <c>GameplayTuning.ExecutionsLoad</c>; the server's rows are authoritative and a row
    /// this install lacks resolves to null). Empty: no execution is predicted or marked.
    /// </summary>
    ExecutionRowSet ExecutionRowsOf(SimConfig c) => _execRowsOverride ?? c.Executions;

    /// <summary>The dot's centre over a Classic corpse's feet and over a Warfront Ork's feet (metres; ours: about the stim cross's place over a standing marine's head, lower over a body on the ground).</summary>
    public const float MarkerOverCorpse = 1.3f, MarkerOverOrk = 2.45f;

    ExecutionMarker? _execMarker;
    /// <summary>Whether the Execute marker shows this frame (the self-test and the log).</summary>
    internal bool ExecutionMarkerShown => _execMarker is { } m && GodotObject.IsInstanceValid(m) && m.Visible;

    partial void ExecutionsViewFrame(PlayerState me, IReadOnlyList<PlayerState> others, double delta)
    {
        if (_client is not { } client || _config is not { } config) return;
        if (!me.Alive) ExecutionCameraReset();   // a death: the death camera starts from the camera as it is; nothing of the orbit carries over
        ExecutionsPredictionFrame(client, config);
        _execOthers.Clear();
        foreach (var o in others) _execOthers[o.PlayerId] = o;
        ExecutionsPromptFrame(client, config, me, others);
        ExecutionsShellFrame(config, me);
        ExecutionX6TestFrame(me, delta);   // --executions-x6-test / --executions-x6-demo (NetworkView.ExecutionX6Test.cs)
    }

    /// <summary>The prediction delegates, set once per connection (a new client object is a new connection).</summary>
    void ExecutionsPredictionFrame(GameClient client, SimConfig config)
    {
        if (ReferenceEquals(client, _execPredictClient)) return;
        _execPredictClient = client;
        _execPrediction = new ExecutionPrediction(ExecutionsState, () => _config is { } c ? ExecutionRowsOf(c) : ExecutionRowSet.Empty,
            () => client.Status == ClientStatus.Connected ? client.PlayerId : -1, config.TickRate);
        ExecutionPrediction.Install(client, config, _execPrediction);
        ExecutionCameraReset();
        foreach (var shell in _execShells.Values) shell.Free();
        _execShells.Clear();
    }

    // ---- the prompt ----

    void ExecutionsPromptFrame(GameClient client, SimConfig config, PlayerState me, IReadOnlyList<PlayerState> others)
    {
        if (_execMarker is null || !GodotObject.IsInstanceValid(_execMarker)) AddChild(_execMarker = new ExecutionMarker());
        _execMarker.Hide();
        if (_hudView is not null) _hudView.ExecutionPrompt = null;   // no text (owner 2026-11-25): the marker replaces the X5a text prompt
        var rows = ExecutionRowsOf(config).Rows;
        var hidden = ExecutionPromptRules.Common(config, me, Spectate is not null, ExecutionsState.TryLive(client.PlayerId, out _), rows);
        if (hidden == ExecutionPromptHidden.None && client.Match.State is MatchState.Countdown or MatchState.Ended) hidden = ExecutionPromptHidden.NotInPlay;   // X3: a press outside play is refused
        Vector3? anchor = null;
        if (hidden == ExecutionPromptHidden.None)
        {
            if (config.Extras.HasFlag(ContentExtras.Warfront)) hidden = WarfrontPrompt(client, config, me, others, rows, out anchor);
            else hidden = ClassicPrompt(client, out anchor);
        }
        else _execPickedOrk = 0;
        _execPromptHidden = hidden;
        if (anchor is { } at) _execMarker.Show(at);   // whatever the bindings (owner: no text, pad bound or not)
    }

    /// <summary>Classic PvP Extended: the corpse of the own open offer (the server sends it to the killer alone), while its window is open at the view tick.</summary>
    ExecutionPromptHidden ClassicPrompt(GameClient client, out Vector3? anchor)
    {
        anchor = null;
        if (ExecutionsState.Offer is not { } offer) return ExecutionPromptHidden.NoOffer;
        double viewTick = client.RemotePlayers().Tick;
        if (!ExecutionPromptRules.ClassicOpen(offer, viewTick > 0 ? viewTick : client.LatestSnapshotTick)) return ExecutionPromptHidden.NoOffer;
        if (!_execOthers.TryGetValue(offer.Victim, out var corpse) && !(offer.Victim == client.PlayerId && (ExecutionX6Test || ExecutionX6Demo is not null))) return ExecutionPromptHidden.NoOffer;
        var feet = offer.Victim == client.PlayerId ? client.Predicted.Position : corpse.Position;   // the own id only under the test aid
        anchor = _gameSpace!.Transform * new Vector3(feet.X, feet.Y, feet.Z + MarkerOverCorpse);
        return ExecutionPromptHidden.None;
    }

    /// <summary>Warfront: the Ork the press would take, recomputed at most 10 times a second; its place follows the crowd view every frame.</summary>
    ExecutionPromptHidden WarfrontPrompt(GameClient client, SimConfig config, PlayerState me, IReadOnlyList<PlayerState> others, IReadOnlyList<ExecutionRow> rows, out Vector3? anchor)
    {
        anchor = null;
        bool inBattle = _tfwState.War is { Phase: TfwRunPhase.Battle or TfwRunPhase.Pickup or TfwRunPhase.FinalAssault };
        var hidden = ExecutionPromptRules.Warfront(config, me, inBattle, ExecutionsChaosRun(), ExecutionsState.OwnLastDoneTick, client.Tick, _tfwShown);
        if (hidden != ExecutionPromptHidden.None) { _execPickedOrk = 0; return hidden; }
        if (_execPromptThrottle.Due(_connectedFor))
        {
            ExecutionPromptRules.Gather(me, _tfwShown, _execNear);
            ExecutionPromptRules.Bystanders(config, me, others, _tfwShown, _execBystanders);
            _execReserved.Clear();
            foreach (var run in ExecutionsState.Live.Values) if (run.TargetKind == ExecutionTargetKind.Enemy && run.Target is > 0 and <= ushort.MaxValue) _execReserved.Add((ushort)run.Target);
            _execPickedOrk = ExecutionPromptRules.WarfrontPick(me, me.Yaw, config, rows, client.Collision, _execNear, _execBystanders, _execReserved, ref _execChoices);
        }
        if (_execPickedOrk == 0) return ExecutionPromptHidden.NoFit;
        foreach (var e in _tfwShown)
            if (e.Id == _execPickedOrk && e.State != MusouEnemyState.Dying)
            {
                anchor = _gameSpace!.Transform * new Vector3(e.Position.X, e.Position.Y, e.Position.Z + MarkerOverOrk);
                return ExecutionPromptHidden.None;
            }
        _execPickedOrk = 0;
        return ExecutionPromptHidden.NoFit;
    }

    /// <summary>
    /// Whether this Warfront run fights Chaos (Chaos C2's renegade roster on the Ork type bytes): executions are refused there until X4b. C2 has no client-visible faction on this base yet, so this is
    /// false; the integration with C2 reads the run's faction here (one line).
    /// </summary>
    static bool ExecutionsChaosRun() => false;

    // ---- the overshield shell ----

    void ExecutionsShellFrame(SimConfig config, PlayerState me)
    {
        if (_client is not { } client) return;
        int own = client.PlayerId;
        if (_local is not null) ShellFor(own, _local, me.Alive ? ExecutionX6ShellOverride ?? ExecutionCameraMath.OvershieldFraction(me.Shield, Loadouts.Body(config, me).ShieldMax) : 0f);
        foreach (var (id, character) in _remotes)
            ShellFor(id, character, _execOthers.TryGetValue(id, out var o) && o.Alive ? ExecutionsState.OvershieldOf(id) : 0f);
        if (_execShells.Count == 0) return;
        List<int>? gone = null;
        foreach (var (id, shell) in _execShells)
            if (!GodotObject.IsInstanceValid(shell.Root) || (id != own && !_remotes.ContainsKey(id))) (gone ??= new()).Add(id);
        if (gone is not null) foreach (var id in gone) { _execShells[id].Free(); _execShells.Remove(id); }
    }

    void ShellFor(int id, CharacterViewer character, float fraction)
    {
        _execShells.TryGetValue(id, out var shell);
        if (!(fraction > 0f))
        {
            if (shell is not null) { shell.Free(); _execShells.Remove(id); }
            return;
        }
        if (shell is not null && !shell.Matches(character)) { shell.Free(); _execShells.Remove(id); shell = null; }
        if (shell is null)
        {
            if (OvershieldShell.Attach(character) is not { } made) return;
            _execShells[id] = shell = made;
        }
        shell.Set(fraction);
    }
}
