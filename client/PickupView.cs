using System;
using Godot;
using SmContent;

namespace SmClient;

/// <summary>
/// One dropped pickup in the networked view (fidelity gap 1): the pickup blueprint's own model (<c>renderable_ext</c>: on the install both
/// drops show <c>ammocase_bolter_small</c>; **original asset loaded**, baked like a weapon model by <see cref="WeaponAssets"/>) standing on
/// the ground at the server's place, in game space (a child of the view's game-space node); a grey box of about the case's size when the
/// model cannot be read (**placeholder demonstration**). Its turn about the vertical is ours (from its id), the original's is not known.
/// While it lies there it may hold a looping effect (<see cref="Idle"/>: the grenade drop's <c>pickup_grenades</c>).
/// </summary>
public sealed partial class PickupView : Node3D
{
    public int Id { get; private set; }
    public int Type { get; private set; }
    /// <summary>The looping effect started for it, stopped when it goes.</summary>
    public FxEffect? Idle;
    /// <summary>The lying effect was tried (started, or the drop has none).</summary>
    public bool IdleTried;
    /// <summary>Whether the original model is drawn (false: the placeholder box).</summary>
    public bool Original { get; private set; }

    static StandardMaterial3D? _placeholder;

    public static PickupView Create(int id, int type, Mesh? model)
    {
        var view = new PickupView { Id = id, Type = type, Name = $"Pickup{id}" };
        // A turn about game Z from the id (golden angle steps; ours), so neighbouring drops do not all face one way.
        view.Rotation = new Vector3(0f, 0f, (id * 2.39996323f) % MathF.Tau);
        if (model is not null)
        {
            view.AddChild(new MeshInstance3D { Mesh = model, Layers = 2 });   // layer 2: decals skip it (M38), as players and weapons
            view.Original = true;
        }
        else
        {
            _placeholder ??= new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.38f, 0.3f), Roughness = 0.8f };
            view.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.3f, 0.3f), Material = _placeholder },
                Position = new Vector3(0f, 0f, 0.15f), Layers = 2,
            });
        }
        return view;
    }

    /// <summary>Moves it to the server's place (game space, metres).</summary>
    public void Place(System.Numerics.Vector3 at) => Position = new Vector3(at.X, at.Y, at.Z);

    /// <summary>Stops its looping effect (the drop went).</summary>
    public void StopIdle()
    {
        if (Idle is not { } e) return;
        e.Repeat = false;
        e.Life = Math.Max(0.0, e.Age);
        Idle = null;
    }
}
