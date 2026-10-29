using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// Reload animations in the networked view (M35): every player whose weapon is reloading shows the weapon's original reload clip over
/// the legs' movement (the original upper-body bone mask), the weapon's magazine drops to the floor and the new one is shown at the
/// clip's load tag (<see cref="ReloadPlayer"/>), and the clip's sounds play (own: the existing <c>SoundOwnState</c>; others: here, at the
/// same tag fractions of the reload time).
/// <list type="bullet">
/// <item><b>Own player:</b> the simulation's reload timer (<see cref="PlayerState.ReloadLeft"/>) is the clock; a reload the simulation
/// cancels (weapon switch, melee, throw, death, a reaction, the dive of a ground pound) ends it. Original clip, mask, timing: extracted.</item>
/// <item><b>Other players:</b> the snapshot does not carry ammunition or reload timers (protocol, spec "What other players see"). The client
/// counts the shots it sees (<see cref="ShotView"/>, the same events the effects use) against the weapon's clip size; the shot that empties
/// the clip starts a reload of the weapon's reload time (the simulation starts one on the next tick while the trigger is held). A switch, a
/// reaction, a melee attack, a throw or death cancels it; completing it refills the count, and so does a gap since the weapon's last shot
/// longer than its reload time + 1 s (a manual reload while idle); see <see cref="ReloadInference"/>. **Interpretation / inferred**: a reload started by hand (R) with rounds left, or after a cancelled one, is not seen.</item>
/// <item>Weapons with no reload clip (heavy bolter, plasma gun, plasma cannon, melta) show none; neither does the plasma pistol (no reserve).</item>
/// </list>
/// <c>--no-reload</c> switches it all off.
/// </summary>
public partial class NetworkView
{
    bool NoReload => _noReload ??= OS.GetCmdlineUserArgs().Contains("--no-reload");
    bool? _noReload;

    Task<ReloadAnimations>? _reloadLoad;
    ReloadAnimations? _reloadAnims;
    bool _reloadTried;
    string _reloadNote = "not loaded";
    MagazineDrops? _magazineDrops;
    readonly ReloadHandTimes _reloadHands = new();
    readonly Dictionary<int, ReloadTrack> _reloads = new();
    readonly HashSet<string> _reloadLogged = new();
    int _reloadOwnStarts, _reloadOwnCompleted, _reloadOwnCancelled, _reloadSeenStarts, _reloadSeenCompleted, _reloadSeenCancelled, _reloadLayers;

    sealed class ReloadTrack
    {
        public readonly ReloadPlayer Player = new();
        public int Weapon = -1;
        // Other players: the reload inferred from their shots (engine-free, SmContent.ReloadInference).
        public readonly ReloadInference Inference = new();
        public int HipShotsSeen;   // the hip-fire shot counter when last looked at
    }

    readonly List<ReloadInference.Shot> _inferShots = new();

    /// <summary>The reload data, read on a worker thread on first use (the behaviour graphs are slow to read); null until it is there or when it failed.</summary>
    ReloadAnimations? ReloadData()
    {
        if (_reloadAnims is not null) return _reloadAnims;
        if (NoReload) { _reloadNote = "switched off (--no-reload)"; return null; }
        if (!_reloadTried)
        {
            _reloadTried = true;
            string gameDir = _gameDir;
            _reloadNote = "loading";
            _reloadLoad = Task.Run(() => ReloadAnimations.Load(gameDir));
            return null;
        }
        if (_reloadLoad is not { IsCompleted: true } load) return null;
        if (load.IsFaulted || load.IsCanceled)
        {
            _reloadNote = "not available: " + (load.Exception?.GetBaseException().Message ?? "cancelled");
            GD.PrintErr("reload animations: " + _reloadNote);
            _reloadLoad = null;
            return null;
        }
        _reloadAnims = load.Result;
        _reloadLoad = null;
        _reloadNote = string.Join(", ", _reloadAnims.Clips.Select(c => $"{c.Family} {c.Clip} ({c.ClipLength:F2} s{(c.EnforcedDuration is null ? "" : ", stretched to the reload time")}{(c.MagazineOut is null ? ", no magazine tags" : "")})"));
        GD.Print("reload animations: " + _reloadNote);
        foreach (var c in _reloadAnims.Clips) foreach (var n in c.Notes) GD.Print($"  reload note ({c.Family}): {n}");
        return _reloadAnims;
    }

    ReloadTrack ReloadOf(int id)
    {
        if (!_reloads.TryGetValue(id, out var t)) _reloads[id] = t = new ReloadTrack { HipShotsSeen = HipOf(id).Shots };
        return t;
    }

    /// <summary>The reload clip a weapon plays, when the rig has it (else a note, once).</summary>
    ReloadClip? ReloadClipOf(WeaponSpec weapon, ReloadAnimations data)
    {
        if (weapon.Recharge is not null || weapon.ClipSize <= 0 || weapon.ReloadTime <= 0f) return null;
        // Ork O7b: the boy has its own families (RigProfiles.OrkBoy: the shoota and the slugga share the one reload clip) and its own rig to check the clip against.
        var family = _reloadAssets is null ? ReloadAnimations.FamilyOf(weapon.Name) : RigProfiles.OrkBoy.ReloadFamilyOf(weapon.Name);
        if (family is not { } fam || data.For(fam) is not { } clip) return null;
        if ((_reloadAssets ?? _assets!).Animation(clip.Clip) is null)
        {
            if (_reloadLogged.Add("missing:" + clip.Clip)) _notes.Add($"reload clip {clip.Clip} not in the rig or not decodable; {weapon.Name} shows no reload");
            return null;
        }
        return clip;
    }

    /// <summary>
    /// Per player and frame (from <see cref="SyncWeapons"/>, after the weapons are in hand): works out whether the player is reloading and keeps
    /// the <see cref="ReloadPlayer"/>'s clock and magazine. The pose is laid by <see cref="ReloadPose"/> from <see cref="Place"/>.
    /// </summary>
    void ReloadSyncSafe(CharacterViewer character, WeaponRig rig, PlayerState s, bool own, float delta)
    {
        try { ReloadSync(character, rig, s, own, delta); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // A failing reload must not stop the frame or take the weapons with it: say so a few times, go on.
            if (_reloadFaults++ < 5) GD.PrintErr($"reload of P{s.PlayerId} failed: {ex.GetType().Name}: {ex.Message}");
            _reloadNote = "failed: " + ex.Message;
            if (_reloads.TryGetValue(s.PlayerId, out var t)) t.Player.Reset(rig);
            rig.ShowMagazines();
        }
    }

    int _reloadFaults;
    MarineAssets? _reloadAssets;   // the Ork rig's assets while an Ork's reload is worked on (Ork O7b), else null

    void ReloadSync(CharacterViewer character, WeaponRig rig, PlayerState s, bool own, float delta)
    {
        if (_config is null || _client is null || NoReload) return;
        // Ork O7b: an Ork reloads with the boy's clip and rig (NetworkView.OrkActions.cs holds the data); _reloadAssets is the rig the clip is checked and measured on, null for a marine.
        _reloadAssets = IsOrkCharacter(character) ? character.Assets : null;
        if ((_reloadAssets is not null ? OrkReloadData() : ReloadData()) is not { } data) return;
        var t = ReloadOf(s.PlayerId);
        var player = t.Player;
        float tick = _config.TickSeconds;

        // What is happening now: (clip, weapon index, total, elapsed) or nothing.
        (ReloadClip Clip, int Weapon, float Total, float Elapsed)? now = own ? OwnReload(s, data) : InferReload(s, t, data, delta);

        if (now is { } n && (!player.Active || n.Weapon != t.Weapon || !ReferenceEquals(n.Clip, player.Clip)))
        {
            if (player.Active) FinishReload(player, own, completed: false);
            player.Begin(n.Clip, n.Total, n.Elapsed, rig, n.Clip.MagazineIn is null ? null : _reloadHands.For(this, _reloadAssets ?? _assets!, rig, n.Clip.Clip));
            t.Weapon = n.Weapon;
            if (own) _reloadOwnStarts++; else { _reloadSeenStarts++; ReloadSounds(s, t, n.Clip, n.Total, n.Elapsed); }
            if (_reloadLogged.Add($"{(own ? "own" : "seen")}:{n.Clip.Family}") && (_bot || ReferenceEquals(character, _local)))
                GD.Print($"reload ({(own ? "own, from the simulation's timer" : $"P{s.PlayerId}, inferred from shots")}): {_config.Weapons[n.Weapon].Name} plays {n.Clip.Clip} ({n.Clip.ClipLength:F2} s) over {n.Total:F2} s " +
                         $"(rate {n.Clip.PlaybackRate(n.Total):F2}), magazine out at {n.Clip.MagazineOut?.ToString("F3") ?? "-"} and in at {n.Clip.MagazineIn?.ToString("F3") ?? "-"} of the clip, " +
                         $"{(n.Clip.BoneMask is null ? "no bone mask" : $"{n.Clip.BoneMask.Count(kv => kv.Value > 0f)} of {n.Clip.BoneMask.Count} bones")}");
        }
        else if (now is null && player.Active)
        {
            bool completed = player.Clock >= player.Total - 2f * tick;
            FinishReload(player, own, completed);
        }
        player.Advance(delta, now is { } m && player.Active ? m.Elapsed : null, tick);
        if (!s.Alive && player.Shown) player.Reset(rig);

        var velocity = _gameSpace is null ? Vector3.Zero : _gameSpace.Basis * new Vector3(s.Velocity.X, s.Velocity.Y, s.Velocity.Z);
        if (player.ApplyMagazine(rig, MagazineDropsNode(), velocity) && (_bot || own)) _magazinesDropped++;
        if (ReloadCaptureDir is { } dir) ReloadCapture(dir, s, own, t);
    }

    /// <summary><c>--reload-capture &lt;dir&gt;</c> (test aid, windowed client): saves screenshots of reloads under way to a folder inside the private data folder (never the repository or the game folder).</summary>
    string? ReloadCaptureDir
    {
        get
        {
            if (_reloadCaptureResolved) return _reloadCaptureDir;
            _reloadCaptureResolved = true;
            var args = OS.GetCmdlineUserArgs();
            int at = Array.IndexOf(args, "--reload-capture");
            if (at < 0 || at + 1 >= args.Length) return null;
            string dir = System.IO.Path.GetFullPath(args[at + 1]);
            if (!SmFormats.InstallPaths.IsWithinOsRule(dir, GameInstall.DataRoot)) { GD.PrintErr($"reload capture: {dir} is outside {GameInstall.DataRoot}; refused"); return null; }
            return _reloadCaptureDir = dir;
        }
    }
    string? _reloadCaptureDir;
    bool _reloadCaptureResolved;
    readonly HashSet<string> _reloadCaptured = new();
    readonly Dictionary<string, int> _reloadCapturedReloads = new();

    /// <summary>Two screenshots per reload (the hand away at 0.2 of the clip, the magazine on the floor at 0.6), the first three reloads of each weapon for each of own and seen.</summary>
    void ReloadCapture(string dir, PlayerState s, bool own, ReloadTrack t)
    {
        var player = t.Player;
        if (!player.Active || player.Clip is null || _camera is null || DisplayServer.GetName() == "headless" || _reloadCaptured.Count >= 60) return;
        string weapon = _config!.Weapons[t.Weapon].Name;
        string group = (own ? "own_" : "seen_") + weapon;
        if (!own)
        {
            // Another player: close enough to be recognised and in front of the camera.
            var at = _gameSpace is null ? Vector3.Zero : _gameSpace.Transform * new Vector3(s.Position.X, s.Position.Y, s.Position.Z + 1f);
            if (_camera.GlobalPosition.DistanceTo(at) > 14f || _camera.IsPositionBehind(at)) return;
        }
        foreach (float fraction in new[] { 0.2f, 0.6f })
        {
            string key = $"{group}_{player.Serial}_{fraction:0.0}";
            if (player.ClipFraction < fraction || player.ClipFraction > fraction + 0.15f || _reloadCaptured.Contains(key)) continue;
            string reload = $"{group}_{player.Serial}";
            if (!_reloadCapturedReloads.ContainsKey(reload) && _reloadCapturedReloads.Count(kv => kv.Key.StartsWith(group + "_", StringComparison.Ordinal)) >= 3) return;
            _reloadCapturedReloads[reload] = 1;
            _reloadCaptured.Add(key);
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, key.Replace('.', 'p') + ".png");
            var err = GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print(err == Godot.Error.Ok ? $"reload capture -> {path} (P{s.PlayerId}, {weapon}, clip fraction {player.ClipFraction:F2}, {_magazineDrops?.Live ?? 0} magazines live)" : $"reload capture failed: {err}");
        }
    }

    int _magazinesDropped;

    void FinishReload(ReloadPlayer player, bool own, bool completed)
    {
        player.Finish(completed);
        if (own) { if (completed) _reloadOwnCompleted++; else _reloadOwnCancelled++; }
        else if (completed) _reloadSeenCompleted++; else _reloadSeenCancelled++;
    }

    MagazineDrops MagazineDropsNode()
    {
        if (_magazineDrops is { } d && IsInstanceValid(d)) return d;
        if (_ragdollWorld is null || !IsInstanceValid(_ragdollWorld)) EnsureRagdollWorld();   // the level the magazines fall on is the ragdolls' (or a flat floor)
        _magazineDrops = new MagazineDrops { Name = "MagazineDrops" };
        AddChild(_magazineDrops);
        return _magazineDrops;
    }

    // ------------------------------------------------------------------ own player: the simulation's timer

    (ReloadClip Clip, int Weapon, float Total, float Elapsed)? OwnReload(PlayerState s, ReloadAnimations data)
    {
        if (!s.Alive || s.ReloadLeft <= 0f || Loadouts.InHand(_config!, s) is not { } weapon || ReloadClipOf(weapon, data) is not { } clip) return null;
        int index = Loadouts.WeaponIndex(_config!, s, s.Slot);
        return (clip, index, weapon.ReloadTime, Math.Clamp(weapon.ReloadTime - s.ReloadLeft, 0f, weapon.ReloadTime));
    }

    // ------------------------------------------------------------------ other players: inferred from their shots

    (ReloadClip Clip, int Weapon, float Total, float Elapsed)? InferReload(PlayerState s, ReloadTrack t, ReloadAnimations data, float delta)
    {
        bool busy = !s.Alive || s.Reaction != ReactionKind.None || s.MeleeStep > 0 || s.Throwing;
        // The shots seen this frame: ray and cone shots (the snapshot's shot records: weapon, charge stage, age) and, for projectile weapons, the
        // shots the hip-fire code attributed to this player from projectiles appearing at its muzzle (HipTrack.Shots counts both).
        _inferShots.Clear();
        int rays = 0;
        foreach (var shot in _frameShots)
        {
            if (shot.Shooter != s.PlayerId) continue;
            rays++;
            if (shot.Weapon < 0 || shot.Weapon >= _config!.Weapons.Count) continue;
            var spec = _config.Weapons[shot.Weapon];
            int cost = shot.Stage > 0 && spec.Charge is { } charge && shot.Stage - 1 < charge.Stages.Count && charge.Stages[shot.Stage - 1].Shot is { } charged ? charged.AmmoCost : spec.AmmoPerShot;
            _inferShots.Add(new ReloadInference.Shot(shot.Weapon, cost, shot.Age));
        }
        int counted = HipOf(s.PlayerId).Shots;
        int projectiles = counted >= t.HipShotsSeen ? counted - t.HipShotsSeen - rays : 0;
        t.HipShotsSeen = counted;
        if (projectiles > 0 && Loadouts.WeaponIndex(_config!, s, s.Slot) is var inHand && inHand >= 0 && _config!.Weapons[inHand].Delivery == DeliveryKind.Projectile)
            for (int i = 0; i < projectiles; i++) _inferShots.Add(new ReloadInference.Shot(inHand, _config.Weapons[inHand].AmmoPerShot, 0));
        var inferred = t.Inference.Update(_config!.Weapons, w => ReloadClipOf(_config.Weapons[w], data) is not null, _inferShots, s.Alive, busy, (int)s.Slot, _connectedFor, delta, _config.TickSeconds);
        return inferred is { } x && ReloadClipOf(_config.Weapons[x.Weapon], data) is { } clip ? (clip, x.Weapon, x.Total, x.Elapsed) : null;
    }

    /// <summary>A player left: its reload state goes, and the sounds scheduled for its reload no longer play.</summary>
    void ReloadForget(int id)
    {
        if (_reloads.Remove(id, out var t)) t.Player.Reset();
    }

    /// <summary>Sounds of a reload seen on another player: the clip's tags spread over the reload time, as the own player's are (<c>SoundOwnState</c>).</summary>
    void ReloadSounds(PlayerState s, ReloadTrack t, ReloadClip clip, float total, float elapsed)
    {
        if (!SoundReady() || _sound is null || _soundTriggers is null || _config is null) return;
        var weapon = _config.Weapons[t.Weapon];
        if (_soundTriggers.File(weapon.Name) is not { } file) return;
        var emitter = Track(s.PlayerId, own: false).Emitter;
        int serial = t.Player.Serial;
        foreach (var (tag, at) in _soundTriggers.ReloadTags(file))
        {
            double due = _connectedFor + at * total - elapsed;
            if (due < _connectedFor - 0.001) continue;
            string name = tag;
            _soundSchedule.Add((due, () =>
            {
                if (!t.Player.Active || t.Player.Serial != serial) return;   // cancelled: a switch, melee, a hit or death
                PostAll(file.Tag(name), emitter, false, "reload other");
            }));
        }
    }

    // ------------------------------------------------------------------ pose

    /// <summary>From <see cref="Place"/> after the locomotion pose: lays the reload clip over it (the recoil of a shot, which ends a reload, has priority).</summary>
    void ReloadPose(CharacterViewer character, PlayerState state)
    {
        if (NoReload || !_reloads.TryGetValue(state.PlayerId, out var t) || !t.Player.Shown || character.PoseLayerClip is not null) return;
        if (t.Player.ApplyPose(character)) _reloadLayers++;
    }

    /// <summary>The bot log's reload counters.</summary>
    string ReloadStats() =>
        $"reloads: own {_reloadOwnStarts} (completed {_reloadOwnCompleted}, cancelled {_reloadOwnCancelled}), seen {_reloadSeenStarts} (completed {_reloadSeenCompleted}, cancelled {_reloadSeenCancelled}), " +
        $"layers laid {_reloadLayers}, magazines dropped {_magazinesDropped}" + (_magazineDrops is { } d && IsInstanceValid(d) ? $" ({d.Live} live, {d.Removed} removed)" : "");

    /// <summary>The F1 line about reloads.</summary>
    string ReloadInfoLine() =>
        "Reloads (M35): the original reload clip over the legs' movement with the original upper-body mask, stretched to the weapon's reload time; the old magazine drops (physics values ours) and the new one shows at the load tag; " +
        "others' reloads are inferred from their shots (ammunition is not sent). Heavy bolter, plasma and melta have none. " + _reloadNote;
}
