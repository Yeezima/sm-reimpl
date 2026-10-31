using System;
using Godot;
using SmNet;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// camera/hop-and-walls: the aim point while a wall holds the follow camera in. The rig's camera (<c>wanted</c>) looks along the view; a wall beside the character moves the pivot, so the
/// camera moves off that line and a plain view direction would put the crosshair on another point of the world. The rig ray's first hit, past the character, is the focus; the camera
/// turns toward it by at most <see cref="CameraBoom.AimMaxTurn"/> (<see cref="CameraBoom.StableLook"/>). The shot follows: <see cref="CenterRayTarget"/> reads the camera's own ray.
/// </summary>
public partial class NetworkView
{
    float _rigFocus;

    /// <summary>The rig ray's focus point this frame (Godot space); updates the smoothed focus distance.</summary>
    Vector3 RigFocusPoint(Vector3 wanted, Vector3 view, Vector3 head, float delta)
    {
        float past = MathF.Max(0f, (head - wanted).Dot(view));   // start past the own character: the rig's place may be behind a wall
        float target = CameraBoom.FocusMax;
        if (_client?.Collision is { } world && _gameSpace is not null)
        {
            var inverse = _gameSpace.Transform.AffineInverse();
            var start = inverse * (wanted + view * past);
            var dir = (inverse.Basis * view).Normalized();
            if (world.Weapons.Raycast(new Num.Vector3(start.X, start.Y, start.Z), new Num.Vector3(dir.X, dir.Y, dir.Z), CameraBoom.FocusMax) is { } hit) target = past + hit.Distance;
        }
        _rigFocus = CameraBoom.StepFocus(_rigFocus, target, delta, _rigFocus > 0f);
        return wanted + view * _rigFocus;
    }

    Vector3 StableLook(Vector3 wanted, Vector3 position, Vector3 view)
    {
        var look = CameraBoom.StableLook(new Num.Vector3(wanted.X, wanted.Y, wanted.Z), new Num.Vector3(position.X, position.Y, position.Z), new Num.Vector3(view.X, view.Y, view.Z), _rigFocus);
        return new Vector3(look.X, look.Y, look.Z).Normalized();
    }
}
