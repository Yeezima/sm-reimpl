namespace SmMapmaker;

/// <summary>Which transform field a spin box edits (the Object tab's position, rotation and scale, the Prefab tab's origin).</summary>
public enum SpinKind { Position, Rotation, Scale }

/// <summary>
/// The Object tab's spin-arrow behaviour (owner 2026-11-19): a right click on an arrow steps by a fixed jump instead of Godot's jump to the minimum or
/// maximum (which sent a position to 100000), and a held arrow starts slowly and speeds up the longer it is held. Pure maths; the editor feeds it time.
/// Our choices (estimates): the jump sizes for rotation and scale, and the hold curve.
/// </summary>
public static class SpinSteps
{
    /// <summary>Right-click jump: position and origin 10 (owner), rotation 45 degrees, scale 0.5.</summary>
    public static double RightClickStep(SpinKind kind) => kind switch { SpinKind.Position => 10, SpinKind.Rotation => 45, SpinKind.Scale => 0.5, _ => 10 };

    /// <summary>Seconds a held arrow waits after its first step before it repeats.</summary>
    public const double HoldDelay = 0.5;
    /// <summary>Seconds between repeats: always 0.2 (5 steps a second); the amount per repeat is what grows.</summary>
    public const double RepeatInterval = 0.2;
    /// <summary>For this long (seconds since the press) each repeat is one plain step: the slow phase.</summary>
    public const double SlowSeconds = 3;
    /// <summary>After the slow phase the step size doubles every this many seconds.</summary>
    public const double DoublingSeconds = 1.5;
    /// <summary>The largest multiple of the plain step (reached about 21 s into the hold).</summary>
    public const double MaxMultiplier = 4096;

    /// <summary>The multiple of the plain step a repeat takes when the arrow has been held <paramref name="heldSeconds"/>: 1 for 3 s, then doubling every 1.5 s up to 4096.</summary>
    public static double HoldMultiplier(double heldSeconds) =>
        heldSeconds <= SlowSeconds ? 1 : Math.Min(MaxMultiplier, Math.Pow(2, (heldSeconds - SlowSeconds) / DoublingSeconds));

    /// <summary>
    /// How many repeats fall in a held arrow's time slice from <paramref name="from"/> to <paramref name="to"/> seconds since the press (repeats fall at
    /// <see cref="HoldDelay"/> + k x <see cref="RepeatInterval"/>); the first step is taken at the press itself and is not counted.
    /// </summary>
    public static int RepeatsBetween(double from, double to)
    {
        static int Count(double t) => t < HoldDelay ? 0 : (int)Math.Floor((t - HoldDelay) / RepeatInterval + 1e-9) + 1;
        return Count(to) - Count(from);
    }
}
