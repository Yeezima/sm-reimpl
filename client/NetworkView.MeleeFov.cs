using Godot;
using SmClient.Customizer.Model;
using SmSim;

namespace SmClient;

/// <summary>
/// camera/melee-fov-widen (owner 2026-11-15): the own camera's field of view widens by <see cref="MeleeFov.Widen"/> degrees while in melee mode (camera/melee-orbit, owner 2026-11-25: the one
/// <see cref="MeleeMode"/> state the camera orbit uses too: a melee-weapon strike enters it; a shot, aiming, a reload, death or respawn ends it; NetworkView.MeleeOrbit.cs). The blend: <see cref="MeleeFov"/>.
/// The camera shift of the fov option stays on the configured value (no sideways jump). <c>--old-melee-fov</c> restores the plain view; <c>--log-fov</c> logs the changes.
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--old-melee-fov</c> (restore switch, camera/melee-fov-widen): no widening in melee, the configured field of view throughout.</summary>
    static readonly bool OldMeleeFov = CmdFlag("old-melee-fov");
    /// <summary><c>--log-fov</c> (test aid): a line for every widening and its end, with the horizontal degrees shown.</summary>
    static readonly bool LogFov = CmdFlag("log-fov");

    /// <summary><c>--fov-demo</c> (test aid): a standing bot that, in a 22 s loop, strikes, fires, strikes, aims, strikes and throws a grenade, for the <c>--log-fov</c> proof.</summary>
    static readonly bool FovDemo = CmdFlag("fov-demo");

    /// <summary>The scripted buttons of <see cref="FovDemo"/> at this connected time.</summary>
    PlayerInput FovDemoInput(uint tick)
    {
        double t = (_connectedFor - 4.0) % 22.0;
        var b = InputButtons.None;
        if (_connectedFor < 4.0) b = InputButtons.None;
        else if (t is >= 1 and < 1.1 or >= 8 and < 8.1 or >= 15 and < 15.1) b = InputButtons.Melee;
        else if (t is >= 5 and < 5.3) b = InputButtons.Fire;
        else if (t is >= 11 and < 11.6) b = InputButtons.Aim;
        else if (t is >= 18 and < 18.1) b = InputButtons.Grenade;
        return new PlayerInput(tick, 0f, 0f, _yaw, b, _pitch);
    }

    readonly MeleeFov _meleeFov = new();
    float _meleeFovLogT;

    /// <summary>The factor on the data's fields of view for this frame (the option's scale, widened in melee).</summary>
    float MeleeFovScale(float configured, float aspect) => OldMeleeFov ? FovMath.Scale(configured, aspect) : _meleeFov.Scale(configured, aspect);

    /// <summary>Once per frame, before the camera is placed: moves the widening's blend toward the shared melee mode (<see cref="MeleeMode"/>, NetworkView.MeleeOrbit.cs).</summary>
    void MeleeFovUpdate(PlayerState me, float delta)
    {
        if (OldMeleeFov) return;
        bool wasWide = _meleeFov.Wide;
        _meleeFov.Advance(_meleeMode.Active && me.Alive, delta);
        if (!LogFov) return;
        var size = GetViewport().GetVisibleRect().Size;
        float aspect = FovMath.Aspect(size.X, size.Y);
        float h = _meleeFov.Horizontal(FovOverride ?? FovMath.Selected, aspect);
        _meleeFovLogT += delta;
        bool changed = wasWide != _meleeFov.Wide;
        if (changed || (_meleeFov.Progress > 0f && _meleeFov.Progress < 1f && _meleeFovLogT > 0.1f))
        {
            _meleeFovLogT = 0f;
            GD.Print($"FOV t {_connectedFor:F2} s: {(changed ? (_meleeFov.Wide ? "Widened" : _meleeModeLast.ToString()) : "blend")} wide {_meleeFov.Wide} horizontal {h:F1} (configured {FovMath.Clamp(FovOverride ?? FovMath.Selected, aspect):F1}) melee step {me.MeleeStep} rush {me.Rush} jump {me.Jump} fire {me.FireHeld} aim {me.Aiming}");
        }
    }
}
