using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Extras.Warfront.Squad;
using SmContent.Extras.Musou;
using SmNet.Extras.Warfront;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The squad in the networked view (slice W10): the Warfront squad's sections go into a <see cref="WarfrontSquadView"/> (model, crowd, HUD, panel), drawn with the same character renderer and look path the players use.
/// Nothing here is called until W5b's envelope and frame routing exist (the two hook lines are in the W10 report): <see cref="WarfrontSquadReceive"/> takes tags 32 to 34, <see cref="WarfrontSquadTick"/> is the frame,
/// <see cref="WarfrontSquadKey"/> the keys. The ship's "aboard" flag (<see cref="WarfrontSquadAboard"/>) is set by the Warfront hub code. Added content (Warfront); no Musou or core line is touched.
/// </summary>
public partial class NetworkView
{
    WarfrontSquadView? _wfSquad;
    Dictionary<int, Num.Vector3>? _wfEnemies;
    double _wfEnemiesAt = -1;

    /// <summary>Whether the player is aboard the ship (the squad panel opens there only; the revive prompt shows only in the field).</summary>
    internal bool WarfrontSquadAboard { get; set; }

    /// <summary>The own squad's drawn Marines, for the report and the tests.</summary>
    internal WarfrontSquadView? WarfrontSquad => _wfSquad;

    /// <summary>Dresses a squad Marine from the config's class lists and its owner's appearance (the code the client already has by player id).</summary>
    sealed class WarfrontSquadDresser(NetworkView view) : IWarfrontSquadDresser
    {
        public WarfrontMarineDress? Dress(in WarfrontSquadShown s)
        {
            var config = view._config;
            if (config is null || !view.LooksReady || s.Class >= config.Classes.Count || view.CustomizationOf(s.Class) is not { } cls) return null;
            var info = config.Classes[s.Class];
            var owner = s.OwnerPlayer >= 0 && view._client?.AppearanceOf(s.OwnerPlayer) is { } a && a.Race == Races.OfSide(info.Side == Team.Chaos ? Team.Chaos : Team.SpaceMarines) ? a.Code : AppearanceCode.Default;
            if (s.OwnerPlayer == view._client?.PlayerId) owner = view._ownLooks.Active(info.Side == Team.Chaos ? Team.Chaos : Team.SpaceMarines);
            string? Weapon(ushort id) => id < config.Weapons.Count ? config.Weapons[id].Name : null;
            string? melee = s.Melee0 < info.MeleeChoices.Count ? info.MeleeChoices[s.Melee0].Wargear : null;
            var carried = new[] { Weapon(s.Primary), Weapon(s.Secondary), melee }.Where(n => !string.IsNullOrEmpty(n)).Cast<string>().ToList();
            return new WarfrontMarineDress(cls.Key, WarfrontSquadModel.LookCode(owner, s.OwnPieces), carried, Weapon(s.Primary), Weapon(s.Secondary), melee);
        }
    }

    /// <summary>A class's display name in capitals ("TACTICAL MARINE"), as the customizer names it; the config's key when the catalogue has no name.</summary>
    string SquadClassDisplay(ClassLoadout info) =>
        (_lookCatalog is { } catalog ? SmClient.Customizer.Model.LoadoutRows.ClassName(catalog, info.Role, info.Side == Team.Chaos ? SmContent.CustomizationCatalog.Chaos : SmContent.CustomizationCatalog.SpaceMarine) : info.Name).ToUpperInvariant();

    void WarfrontSquadEnsure()
    {
        if (_wfSquad is not null || Lean || _gameSpace is null || _hudView is null || !LooksReady || _looks is null || _lookCatalog is null || _config is null) return;
        var looks = new WarfrontSquadLooks(_looks, _lookCatalog);
        var view = new WarfrontSquadView(looks, _gameDir, id => id < _config.Classes.Count ? SquadClassDisplay(_config.Classes[id]) : "?")
        {
            PlayerName = id => HudName(id),
            OwnHasJumpPack = () => _client is { } c && _config.Classes.Count > c.Predicted.Class && c.Predicted.Class >= 0 && _config.Classes[c.Predicted.Class].Role == "assault",
            MarkPoint = () => null,   // the fireteam mark's point: W5b's mark code supplies it (WarfrontSquad.MarkPoint)
            SendCommand = payload => _client?.SendExtrasCommand(SquadWire.CommandTag, payload),
            UseKeyLabel = ControlsInput.Prompt(GameAction.Use),
        };
        view.Crowd.Dresser = new WarfrontSquadDresser(this);
        view.Crowd.EnemyAt = WarfrontEnemyAt;
        view.Crowd.Shot = WarfrontSquadShotFx;
        _gameSpace.AddChild(view.Crowd);
        var fit = WarfrontFitRoot(_hudView);   // the squad's HUD and panel are laid out in the 1920 by 1080 space: fitted to the window and centred (W5c second pass)
        fit.AddChild(view.Hud);
        fit.AddChild(view.Panel);
        AddChild(view);
        _wfSquad = view;
    }

    /// <summary>The envelope's squad sections (tags 32 to 34); true when one was taken. W5b's <c>WarfrontSquadSection</c> hook calls this.</summary>
    internal bool WarfrontSquadReceive(byte tag, ReadOnlySpan<byte> payload)
    {
        if (tag is < SquadWire.StateTag or > SquadWire.EventsTag) return false;
        WarfrontSquadEnsure();
        if (_wfSquad is null) return true;   // the look path is not ready: the next send carries it again
        _wfSquad.Model.OwnPlayer = _client?.PlayerId ?? -1;
        return _wfSquad.Section(tag, payload, _connectedFor);
    }

    /// <summary>The frame. W5b's <c>WarfrontSquadFrame</c> hook calls this.</summary>
    internal void WarfrontSquadTick(PlayerState me, double dt)
    {
        if (_wfSquad is null) return;
        _wfSquad.Aboard = WarfrontSquadAboard;
        _wfSquad.Model.OwnPlayer = _client?.PlayerId ?? -1;
        _wfSquad.Frame(_connectedFor, dt, _camera, _gameSpace, me.Position);
    }

    /// <summary>The squad's keys (panel, jump-pound); true when used. Called from the networked view's key handling by W5b's hook.</summary>
    internal bool WarfrontSquadKey(InputEventKey key) => _wfSquad?.HandleKey(key) == true;

    /// <summary>The Orks' places by id for the Marines' tracers: the Musou client state's, sampled once a frame.</summary>
    Num.Vector3? WarfrontEnemyAt(int id)
    {
        if (_wfEnemiesAt != _connectedFor)
        {
            _wfEnemiesAt = _connectedFor;
            _wfEnemies = new Dictionary<int, Num.Vector3>();
            double tick = _client?.RemotePlayers().Tick ?? 0;
            foreach (var e in _musouState.Sample(tick > 0 ? tick : _musouState.NewestTick, Time.GetTicksUsec() / 1e6)) _wfEnemies[e.Id] = e.Position;
        }
        return _wfEnemies is not null && _wfEnemies.TryGetValue(id, out var p) ? p : null;
    }

    /// <summary>A Marine's shot: the Guardsmen's muzzle flash and tracer stand in (the effects the view already compiles for them), toward the Ork. **Provisional approximation**: the class weapon's own tracer is not used yet.</summary>
    void WarfrontSquadShotFx(Num.Vector3 from, Num.Vector3 to, byte cls)
    {
        GuardFxLoad();
        if (!_guardFxReady || _fx is not { IsReady: true } fx || !FxActive || _gameSpace is null) return;
        var origin = _gameSpace.Transform * new Vector3(from.X, from.Y, from.Z);
        var end = ToGodot(to);
        var line = end - origin;
        float length = line.Length();
        if (length < 0.4f) return;
        var axes = AxesFromY(line / length);
        fx.Play(GuardRig.MuzzleEffect, origin, axes, GuardRig.MuzzleEffectScale, priority: 0, smokeRein: SmokeRein);
        fx.Play(GuardRig.TracerEffect, origin, axes, 1f, target: end, priority: MusouCrowdFirePriority, smokeRein: SmokeRein);
    }
}
