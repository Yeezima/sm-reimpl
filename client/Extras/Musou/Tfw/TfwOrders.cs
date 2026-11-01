using System;
using Godot;
using SmNet;
using SmNet.Extras.Musou;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The war's order keys (design section G; the owner's approved choice, fixed keys, read here in <see cref="TfwFrame"/>): Z follow, X hold at the ground point under the crosshair, V attack the enemy or
/// the point under the crosshair, P held 1 s for a pickup (or to call a running one off), B an orbital strike on the point under the crosshair (the owner, 2026-11-01). Two of them clash with the defaults, X next weapon and V melee: in a war's match the order keys win, each action
/// keeps its other input (the wheel, the right mouse button). <see cref="TfwOrderKeysFree"/> takes these keys out of the InputMap for this match only: nothing is saved, the player's
/// <c>controls.json</c> and the controls screen are unchanged, and the node <see cref="TfwKeysBack"/> puts them back when the view goes. There is no controller binding for the orders yet.
/// </summary>
public partial class NetworkView
{
    /// <summary>The reach of the crosshair's ray (m, ours: the arena's diagonal).</summary>
    const float OrderReach = 340f;
    /// <summary>How far from the crosshair's ray an enemy still counts as aimed at (m, ours).</summary>
    const float OrderEnemyRadius = 3f;
    static readonly string[] TfwOrderInputs = ["Key:Z", "Key:X", "Key:V", "Key:P", "Key:M", "Key:Key4", "Key:Key5", "Key:B"];

    readonly TfwOrderKeys _tfwKeys = new();
    BindingSet? _tfwKeysFreed;
    bool _tfwOrbitalKey;
    double _tfwOrbitalCalled = -1;

    /// <summary>Every action bound to an order key gives that key up for this match; again after a rebinding in the match (<see cref="ControlsInput.Use"/> rebuilds the InputMap and makes a new set).</summary>
    void TfwOrderKeysFree()
    {
        if (_bot || ReferenceEquals(_tfwKeysFreed, ControlsInput.Bindings)) return;
        if (_tfwKeysFreed is null) AddChild(new TfwKeysBack { Name = "TfwKeysBack" });
        _tfwKeysFreed = ControlsInput.Bindings;
        foreach (var info in ControlActions.All)
            foreach (string input in _tfwKeysFreed.Inputs(BindDevice.Keyboard, info.Action))
            {
                if (Array.IndexOf(TfwOrderInputs, input) < 0 || !Enum.TryParse<Key>(input["Key:".Length..], out var key)) continue;
                InputMap.ActionEraseEvent(info.MapName, new InputEventKey { PhysicalKeycode = key });
                GD.Print($"war: {key} is an order key in this match, not '{info.Label}'");
            }
    }

    /// <summary>Puts the keys <see cref="TfwOrderKeysFree"/> took back into the InputMap when the view leaves the tree.</summary>
    sealed partial class TfwKeysBack : Node
    {
        public override void _ExitTree() => ControlsInput.Apply();
    }

    void TfwOrdersFrame(in TfwWarView war, PlayerState me, float delta)
    {
        if (_bot) return;
        bool enabled = TfwHudModel.InBattle(war.Phase) && me.Alive && !PickerBusy;
        bool orbital = Input.IsPhysicalKeyPressed(Key.B);
        if (orbital && !_tfwOrbitalKey && enabled) { var target = TfwCrosshairPoint(me); _tfwState.Commands.Issue(TfwCommand.Orbital, 0, target); _tfwOrbitalCalled = _connectedFor; TfwDesignatorBeam(me, target); }   // the server decides
        _tfwOrbitalKey = orbital;
        var press = _tfwKeys.Update(Input.IsPhysicalKeyPressed(Key.Z), Input.IsPhysicalKeyPressed(Key.X), Input.IsPhysicalKeyPressed(Key.V), Input.IsPhysicalKeyPressed(Key.P), delta, enabled);
        if (press == TfwOrderPress.None) return;
        if (press.HasFlag(TfwOrderPress.Follow)) _tfwState.Commands.Issue(TfwCommand.Follow);
        if (press.HasFlag(TfwOrderPress.Hold)) _tfwState.Commands.Issue(TfwCommand.Hold, 0, TfwCrosshairPoint(me));
        if (press.HasFlag(TfwOrderPress.Attack)) _tfwState.Commands.Issue(TfwCommand.Attack, 0, TfwCrosshairEnemy(me) ?? TfwCrosshairPoint(me));
        if (press.HasFlag(TfwOrderPress.Pickup)) _tfwState.Commands.Issue(war.Phase == TfwRunPhase.Pickup ? TfwCommand.CancelPickup : TfwCommand.RequestPickup);
    }

    /// <summary>The crosshair's ray in game space: the fresh centre ray, the camera's, or (no camera: headless) the view from the eye.</summary>
    (Num.Vector3 Origin, Num.Vector3 Direction) TfwCrosshairRay(PlayerState me)
    {
        if (FreshCenterRay() is { } fresh) return fresh;
        if (_camera is { } camera && _gameSpace is { } space && camera.IsInsideTree())
        {
            var inverse = space.Transform.AffineInverse();
            var o = inverse * camera.GlobalPosition;
            var d = (inverse.Basis * -camera.GlobalBasis.Z).Normalized();
            return (new Num.Vector3(o.X, o.Y, o.Z), new Num.Vector3(d.X, d.Y, d.Z));
        }
        float cos = MathF.Cos(_pitch);
        return (me.Position + new Num.Vector3(0f, 0f, _config?.EyeHeight ?? 1.8f), new Num.Vector3(-MathF.Sin(_yaw) * cos, MathF.Cos(_yaw) * cos, MathF.Sin(_pitch)));
    }

    /// <summary>
    /// fx/designator (the owner 2026-11-02: "a laser designator effect similar to a lascannon shot visual effect, but thinner and smaller ... when calling an artillery strike"): at the call
    /// a beam runs from the weapon in hand's muzzle to the aimed point, the lascannon's own hard red beam (<see cref="LaserAdd"/>) at <see cref="DesignatorWidth"/> of its width and
    /// <see cref="DesignatorLife"/> of its life. Ours: both numbers, tagged in the refine register. Cosmetic only; the call itself is the server's.
    /// </summary>
    void TfwDesignatorBeam(PlayerState me, Num.Vector3 target)
    {
        if (_fx is null) return;
        var (eye, direction) = TfwCrosshairRay(me);
        var from = (_config is not null && Loadouts.InHand(_config, me) is { } weapon && FxWeapon(weapon.Name) is { } w ? FxPlace(me.PlayerId, w.MuzzleMarker) : null)
            ?? FxFallback(eye, G(direction));
        var to = ToGodot(target);
        if (from.Position.DistanceTo(to) < 0.4f) return;
        LaserAdd(from.Position, null, to, DesignatorWidth, DesignatorLife);
    }

    /// <summary>The designator beam's width against the lascannon's basic shot (which draws at 0.5; ours).</summary>
    const float DesignatorWidth = 0.18f;
    /// <summary>The designator beam's life against the lascannon's (0.4 s core, 0.6 s glow; ours).</summary>
    const float DesignatorLife = 0.5f;

    /// <summary>The ground or wall under the crosshair; looking at the sky, the floor beneath the ray's end.</summary>
    Num.Vector3 TfwCrosshairPoint(PlayerState me)
    {
        var (origin, direction) = TfwCrosshairRay(me);
        if (_client?.Collision is { } world && world.Character.Raycast(origin, direction, OrderReach) is { } hit) return hit.Point;
        var far = origin + direction * OrderReach;
        return new Num.Vector3(far.X, far.Y, 0f);
    }

    /// <summary>The nearest enemy on the crosshair's ray (chest height), its feet; null: none near the ray.</summary>
    Num.Vector3? TfwCrosshairEnemy(PlayerState me)
    {
        if (_client is not { } client) return null;
        var (origin, direction) = TfwCrosshairRay(me);
        var (renderTick, _) = client.RemotePlayers();
        var shown = _musouState.Sample(renderTick > 0 ? renderTick : _musouState.NewestTick, Time.GetTicksUsec() / 1e6);
        float best = float.MaxValue;
        Num.Vector3? found = null;
        for (int i = 0; i < shown.Count; i++)
        {
            var to = shown[i].Position + new Num.Vector3(0f, 0f, 1.1f) - origin;
            float along = Num.Vector3.Dot(to, direction);
            if (along <= 0f || along > OrderReach || along >= best || (to - direction * along).LengthSquared() > OrderEnemyRadius * OrderEnemyRadius) continue;
            best = along;
            found = shown[i].Position;
        }
        return found;
    }
}
