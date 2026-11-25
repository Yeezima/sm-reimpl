using System;
using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Executions;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// Executions X5: the victim's half. Each run gets one proxy character (a <see cref="CharacterViewer"/> of its own, never the victim's live character) at the victim's place
/// (<see cref="ExecutionPresentMath.VictimPlace"/>: its feet never move) playing the row's victim clip on the run's clock.
/// <list type="bullet">
/// <item>Classic: the victim player's own look (its character's assets; the default marine while none is built). Its ragdoll, or its held death pose, is removed at the Start
/// (<c>RemoveRagdolls(victim, keepDeathState: true)</c>). Race-swapped rows play the Chaos Marine's victim half on the loyalist's body (the same 50-bone PvP skeleton; owner);
/// an Ork Boy player plays the campaign boy's clip on the Ork rig.</item>
/// <item>Warfront: the crowd Ork's look (<c>MusouLooks.For</c>, the Chaos look where the stage supplies one) at its scale; the crowd skips that member and makes no corpse
/// (<see cref="CrowdView.ExecutionHidden"/>) until the proxy has lain and sunk on the crowd's own corpse timing.</item>
/// <item>After a completed move the proxy lies in its last pose (Classic 10 s, ours; Warfront the crowd's lie time), then sinks and is freed; any other end frees it after
/// <see cref="ExecutionClientState.EndedKeepSeconds"/>. Freed proxies are pooled per look (at most live runs + 2 kept).</item>
/// </list>
/// </summary>
public partial class NetworkView
{
    /// <summary>A Classic proxy corpse lies this long after a completed move before it sinks (seconds, ours: the PvP ragdolls lie until the cap).</summary>
    const float ClassicProxyLieSeconds = 10f;
    const float ProxySinkSeconds = 1.5f, ProxySinkDepth = 1.2f;

    sealed class ExecProxy
    {
        public required ExecutionRun Run;
        public required ExecutionRow Row;
        public CharacterViewer? Viewer;
        public MarineAssets? Assets;
        public float Scale = 1f;
        public float? GroundZ;
        public bool Warfront, StandIn, Gave;
        public double? EndedAt;
        public bool Done;
        public double CreatedAt;
        public double LastTick;
        public PlayerState? VictimState;
        public bool PoseCopied, Gibbed;
        /// <summary>Classic: the executor died mid-move; the proxy's pose has been handed to a ragdoll (waits for it, then the proxy goes).</summary>
        public bool RagdollHanded;
    }

    /// <summary>The body showing a Classic victim now: its held character while the death waits, else its ragdoll's character.</summary>
    CharacterViewer? ExecVictimBody(int id)
    {
        foreach (var rd in _ragdolls) if (rd.PlayerId == id && !rd.Orphan && rd.Character is { } c) return c;
        var live = _client is not null && id == _client.PlayerId ? _local : _remotes.GetValueOrDefault(id);
        return live is not null && live.Visible ? live : null;
    }

    readonly Dictionary<(int Executor, uint Start), ExecProxy> _execProxies = new();
    readonly List<CharacterViewer> _execPool = new();
    readonly HashSet<int> _execProxiedVictims = new();
    readonly HashSet<int> _execHidden = new();
    readonly List<(int, uint)> _execDrop = new();
    /// <summary>
    /// The crowd deaths handed out this frame, copied at their source (<c>MusouDraw</c>'s hook, <see cref="ExecutionsDeathsHanded"/>) before the sound frame empties Musou's own list (X5 review
    /// X5-3); read and cleared by the presenter's frame. Only the executed Orks' are kept.
    /// </summary>
    readonly List<MusouDeathShown> _execDeathsHanded = new();

    partial void ExecutionsDeathsHanded(IReadOnlyList<MusouDeathShown> deaths)
    {
        if (_execHidden.Count == 0) return;
        foreach (var d in deaths) if (_execHidden.Contains(d.Id)) _execDeathsHanded.Add(d);
    }

    /// <summary>Live proxies (the headless self-test reads it).</summary>
    internal int ExecutionProxyCount => _execProxies.Count;

    void ExecutionProxiesFrame(PlayerState me, IReadOnlyList<PlayerState> others, double delta)
    {
        if (_execLib.Catalogue is not { } catalogue || _gameSpace is null) return;
        bool warfront = _config?.Extras.HasFlag(ContentExtras.Musou) ?? false;
        foreach (var run in ExecutionsState.Live.Values)
        {
            var key = (run.Executor, run.StartTick);
            if (_execProxies.ContainsKey(key) || catalogue.AtWire(run.Wire) is not { } row) continue;
            var p = new ExecProxy { Run = run, Row = row, Warfront = run.TargetKind == ExecutionTargetKind.Enemy, CreatedAt = _connectedFor };
            _execProxies[key] = p;
            if (p.Warfront) _execHidden.Add(run.Target);
            else
            {
                _execProxiedVictims.Add(run.Target);
                if (_motion.TryGetValue(run.Target, out var m)) p.GroundZ = m.Position.Z;
                foreach (var o in others) if (o.PlayerId == run.Target) { p.VictimState = o; break; }
                // Built now, before the ragdoll goes: the proxy starts from a copy of the body's pose (the held death pose, else the ragdoll's) and blends into the victim clip.
                var holder = ExecVictimBody(run.Target);
                if (ExecProxyBuild(p) && holder is not null && IsInstanceValid(holder) && p.Viewer is { } built)
                {
                    built.Pose(CharacterViewer.DefaultAnimation, 0f, 0f);   // so the victim clip's first pose is an entry, faded from the copy
                    built.CopyPoseFrom(holder);
                    p.PoseCopied = true;
                }
                RemoveRagdolls(run.Target, keepDeathState: true);   // the held death pose or the ragdoll hands over to the proxy
            }
            if (ExecLog) GD.Print($"EXECPRESENT t {_connectedFor:F2} s: start P{run.Executor} {row.Id} on {(p.Warfront ? "enemy" : "P")}{run.Target} (wire {run.Wire}, tick {run.StartTick}, slide {run.SlideTicks}, distance {run.Distance:F3} m{(run.RaceSwapped ? ", races swapped" : "")})");
        }
        foreach (var ended in ExecutionsState.Ended)
            if (_execProxies.TryGetValue((ended.Run.Executor, ended.Run.StartTick), out var p) && p.EndedAt is null)
            {
                p.EndedAt = _connectedFor;
                p.Done = ended.Reason == ExecutionEndReason.Done;
                if (!p.Warfront && ended.Reason == ExecutionEndReason.Killed) ExecHandToRagdoll(p);   // the executor died mid-move: the victim stays, as a ragdoll from the proxy's pose
                if (ExecLog) GD.Print($"EXECPRESENT t {_connectedFor:F2} s: end P{p.Run.Executor} {p.Row.Id} {ended.Reason} at tick {ended.EndTick}");
            }
        if (warfront && _musouCrowd is { } gibCrowd && _execHidden.Count > 0)
            foreach (var d in _execDeathsHanded)
            {
                // A death of an executed Ork that left no crowd corpse (MusouCrowd.AddBody never asked): gibbed or thrown, so the proxy steps aside for the crowd's own remains.
                if (!_execHidden.Contains(d.Id) || gibCrowd.ExecutionCorpseClaimed.Contains(d.Id)) continue;
                foreach (var p in _execProxies.Values)
                    if (p.Warfront && p.Run.Target == d.Id && !p.Gibbed)
                    {
                        p.Gibbed = true;
                        if (ExecLog) GD.Print($"EXECPRESENT t {_connectedFor:F2} s: enemy {d.Id} gibbed or thrown by the crowd; its proxy is hidden");
                    }
            }
        _execDeathsHanded.Clear();
        _execDrop.Clear();
        foreach (var (key, p) in _execProxies)
        {
            // A run that left the state without an End we saw (a Clear): ended now.
            if (p.EndedAt is null && (!ExecutionsState.TryLive(p.Run.Executor, out var live) || live.StartTick != p.Run.StartTick))
            {
                bool ended = false;
                foreach (var e in ExecutionsState.Ended) if (e.Run.Executor == p.Run.Executor && e.Run.StartTick == p.Run.StartTick) { ended = true; break; }
                if (!ended) p.EndedAt = _connectedFor;
            }
            float lie = p.Warfront ? MusouCrowd.SinkAfter : ClassicProxyLieSeconds;
            float sink = p.Warfront ? MusouCrowd.SinkSeconds : ProxySinkSeconds;
            if (p.Gibbed) { _execDrop.Add(key); continue; }   // Warfront: the crowd tore the Ork apart or threw it; its gore or thrown body is the corpse
            if (p.RagdollHanded)
            {
                // The ragdoll exists (or the death was dropped: the victim respawned, or 6 s passed): the proxy goes.
                bool ragdoll = false;
                foreach (var rd in _ragdolls) if (rd.PlayerId == p.Run.Target) { ragdoll = true; break; }
                if (ragdoll || _connectedFor - (p.EndedAt ?? _connectedFor) > 6.0 || _aliveSeen.TryGetValue(p.Run.Target, out bool alive) && alive) _execDrop.Add(key);
                continue;   // the proxy holds its pose until the ragdoll takes it
            }
            if (p.EndedAt is { } end && _connectedFor - end > ExecutionPresentMath.AfterEnd(p.Done, lie, sink)) { _execDrop.Add(key); continue; }
            ExecProxyPose(p, lie, sink);
        }
        foreach (var key in _execDrop)
        {
            var p = _execProxies[key];
            _execProxies.Remove(key);
            ExecProxyFree(p);
            if (p.Warfront) _execHidden.Remove(p.Run.Target);
            else _execProxiedVictims.Remove(p.Run.Target);
            if (ExecLog) GD.Print($"EXECPRESENT t {_connectedFor:F2} s: proxy of P{p.Run.Executor} {p.Row.Id} freed ({_execProxies.Count} left)");
        }
        // A Classic victim alive again keeps no proxy claim (its next death is its own).
        // The victim's own client too (X5 review X5-2): its own state is not among the others, and a kept claim would hide its next death.
        if (_execProxiedVictims.Count > 0) ExecutionPresentMath.ReleaseClaims(_execProxiedVictims, ExecClaimPlayers(me, others), AnyProxyOn);
        if (warfront && _musouCrowd is { } crowd)
        {
            crowd.ExecutionHidden = _execHidden.Count > 0 ? _execHidden : null;
            if (_execHidden.Count == 0 && crowd.ExecutionSeen.Count > 0) crowd.ExecutionSeen.Clear();
            if (_execHidden.Count == 0 && crowd.ExecutionCorpseClaimed.Count > 0) crowd.ExecutionCorpseClaimed.Clear();
        }
    }

    /// <summary>
    /// Classic: the executor died mid-move (End Killed). The victim is not removed: its death is queued again with the proxy as the body, so the core's
    /// <c>ProcessDeaths</c> starts a ragdoll from the proxy's pose; the proxy holds until that ragdoll exists, then goes. A victim already alive again keeps nothing.
    /// </summary>
    void ExecHandToRagdoll(ExecProxy p)
    {
        int id = p.Run.Target;
        if (p.Viewer is not { } v || !IsInstanceValid(v) || _aliveSeen.TryGetValue(id, out bool alive) && alive) return;
        _execProxiedVictims.Remove(id);
        var (feet, yaw) = ExecutionPresentMath.VictimPlace(p.Run, p.Row, p.GroundZ);
        var state = p.VictimState ?? new PlayerState(id, feet, System.Numerics.Vector3.Zero, yaw, true);
        _pendingDeaths.RemoveAll(d => d.Id == id);
        MarineDeathForget(id); OrkDeathForget(id);   // a fresh death from the proxy's pose (the death clip, then the ragdoll)
        _pendingDeaths.Add((id, v, state, _client is not null && id == _client.PlayerId, Vector3.Zero));
        p.RagdollHanded = true;
        if (ExecLog) GD.Print($"EXECPRESENT t {_connectedFor:F2} s: executor P{p.Run.Executor} killed mid-move; P{id} goes to a ragdoll from the proxy's pose");
    }

    /// <summary>The others and, on a player's own client, the own player (an observer's <paramref name="me"/> is default and not listed).</summary>
    IEnumerable<(int, bool)> ExecClaimPlayers(PlayerState me, IReadOnlyList<PlayerState> others)
    {
        foreach (var o in others) yield return (o.PlayerId, o.Alive);
        if (_client is not null && me.PlayerId == _client.PlayerId) yield return (me.PlayerId, me.Alive);
    }

    bool AnyProxyOn(int victim)
    {
        foreach (var p in _execProxies.Values) if (!p.Warfront && p.Run.Target == victim && p.EndedAt is null) return true;
        return false;
    }

    void ExecProxyPose(ExecProxy p, float lie, float sink)
    {
        if (p.Viewer is null && !ExecProxyBuild(p)) return;
        var viewer = p.Viewer!;
        var row = p.Row;
        // The clock: the executor's while the move lives; after its end the last pose holds.
        double tick = p.EndedAt is null ? ExecDrawTick(p.Run.Executor) : p.LastTick;
        if (p.EndedAt is null) p.LastTick = tick;
        float clip = ExecutionPresentMath.ClipSeconds(p.Run, tick, ExecTickRate);
        if (p.EndedAt is not null && p.Done) clip = MathF.Max(clip, row.VictimSeconds);
        var (feet, yaw) = ExecutionPresentMath.VictimPlace(p.Run, row, p.GroundZ);
        var vclip = _execLib.Victim(row.Target, row.VictimClip);
        if (!p.StandIn && vclip is not null)
        {
            viewer.Pose(ExecutionClipLibrary.Alias(row.VictimClip), ExecutionPresentMath.RawTime(clip, row.VictimSeconds, vclip.Animation.Animation.Duration), 0.2f);   // 0.2 s from the held death pose (architect recommendation 1)
            if (vclip.InPlace) (feet, yaw) = ExecutionPresentMath.RootPlace(feet, yaw, row.VictimRoot, row.VictimSeconds > 0f ? clip / row.VictimSeconds : 1f);
        }
        else ExecutionStandIns.Victim(viewer, row, clip, _notes);
        float down = p.EndedAt is { } end && p.Done ? ExecutionPresentMath.Sink(_connectedFor - end, lie, sink, ProxySinkDepth) : 0f;
        viewer.Transform = new Transform3D(new Basis(new Vector3(0, 0, 1), yaw).Scaled(Vector3.One * p.Scale), new Vector3(feet.X, feet.Y, feet.Z - down));
        viewer.Visible = true;
    }

    bool ExecProxyBuild(ExecProxy p)
    {
        if (_gameSpace is null) return false;
        MarineAssets? assets;
        if (p.Warfront)
        {
            if (_musouCrowd is not { } crowd || !crowd.ExecutionSeen.TryGetValue(p.Run.Target, out var seen))
            {
                if (_connectedFor - p.CreatedAt > 1.0 && !p.Gave) { p.Gave = true; _execHidden.Remove(p.Run.Target); GD.Print($"EXECPRESENT: enemy {p.Run.Target} never drawn by the crowd; no proxy (the crowd keeps it)"); }
                return false;
            }
            var look = _musouLooks?.For((MusouEnemyType)seen.Type);
            if (look is null) return false;
            assets = look.Assets;
            p.Scale = look.Scale;
            p.GroundZ ??= seen.Position.Z;
        }
        else
        {
            var source = _client is not null && p.Run.Target == _client.PlayerId ? _local : _remotes.GetValueOrDefault(p.Run.Target);
            assets = source?.Assets ?? ExecPlaceholderVictim(p.Row);
        }
        if (assets is null) return false;
        var vclip = _execLib.Victim(p.Row.Target, p.Row.VictimClip);
        p.StandIn = !_execLib.Prepare(assets.Rig, vclip, p.Row.VictimClip);
        string first = p.StandIn ? ExecutionStandIns.VictimClip(assets) ?? CharacterViewer.DefaultAnimation : ExecutionClipLibrary.Alias(p.Row.VictimClip);
        CharacterViewer? viewer = null;
        for (int i = 0; i < _execPool.Count; i++)
            if (ReferenceEquals(_execPool[i].Assets, assets) && IsInstanceValid(_execPool[i]))
            {
                viewer = _execPool[i];
                _execPool.RemoveAt(i);
                viewer.Pose(first, 0f, 0f);   // X5 review X5-4: a reused proxy starts from its first clip, not blended from the last move's final pose
                break;
            }
        if (viewer is null)
        {
            viewer = new CharacterViewer(assets, first, gameSpaceRoot: false) { Name = $"ExecutionVictim{p.Run.Target}" };
            _gameSpace.AddChild(viewer);
        }
        if (!p.Warfront && (_client is not null && p.Run.Target == _client.PlayerId ? _local : _remotes.GetValueOrDefault(p.Run.Target)) is { } live && live.Overlay is { } overlay) viewer.SetOverlay(overlay);   // the side's look (team colour overlay) as the victim wore it
        p.Viewer = viewer;
        p.Assets = assets;
        if (ExecLog) GD.Print($"EXECPRESENT t {_connectedFor:F2} s: proxy for {(p.Warfront ? "enemy " : "P")}{p.Run.Target}: {p.Row.VictimClip} on rig {assets.Rig.RigKey}{(p.StandIn ? " (stand-in: placeholder demonstration)" : " (original asset loaded)")} scale {p.Scale:F2}");
        return true;
    }

    void ExecProxyFree(ExecProxy p)
    {
        if (p.Viewer is not { } v || !IsInstanceValid(v)) return;
        v.Visible = false;
        v.ClearPoseLayers();
        if (_execPool.Count < ExecutionsState.Live.Count + 2) _execPool.Add(v);
        else v.QueueFree();
        p.Viewer = null;
    }

    /// <summary>The look a Classic victim proxy wears while the victim has no built character (the side's placeholder): the co-op Ork Boy for the Ork Boy rows, else the default marine.</summary>
    MarineAssets? ExecPlaceholderVictim(ExecutionRow row)
    {
        if (!string.Equals(row.Target, ExecutionTargets.OrkBoy, StringComparison.OrdinalIgnoreCase)) return _assets;
        try { return (_orks ?? OrkAssets.Shared(_gameDir, _notes)).Build("boy_melee"); }
        catch (Exception e) when (e is not OutOfMemoryException) { _notes.Add($"executions: no Ork look for the proxy ({e.Message})"); return _assets; }
    }
}
