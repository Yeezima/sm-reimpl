namespace SmBots;

/// <summary>
/// The parts of Godot's C# <c>Vector2</c> the bot brain uses, with Godot 4's exact single-precision formulas (Godot Engine, MIT licence; GodotSharp's
/// Core/Vector2.cs and Core/Mathf.cs), so the brain moved out of the Godot client computes the same floats as before (checked bit for bit against
/// GodotSharp 4.7.2 in <c>BotBrainSeamTests</c>). Game space, ground plane (x, y). Not a general vector type: use <see cref="System.Numerics.Vector2"/> elsewhere.
/// </summary>
public struct Vector2 : IEquatable<Vector2>
{
    public float X;
    public float Y;

    public Vector2(float x, float y) { X = x; Y = y; }

    public static readonly Vector2 Zero = new(0f, 0f);

    public readonly float Length() => MathF.Sqrt((X * X) + (Y * Y));
    public readonly float LengthSquared() => (X * X) + (Y * Y);
    public readonly float Dot(Vector2 with) => (X * with.X) + (Y * with.Y);
    public readonly float DistanceTo(Vector2 to) => MathF.Sqrt(((X - to.X) * (X - to.X)) + ((Y - to.Y) * (Y - to.Y)));

    /// <summary>Godot's Normalized: divided by its length; the zero vector stays zero.</summary>
    public readonly Vector2 Normalized()
    {
        var v = this;
        float lengthsq = v.LengthSquared();
        if (lengthsq == 0f) { v.X = v.Y = 0f; }
        else
        {
            float length = MathF.Sqrt(lengthsq);
            v.X /= length;
            v.Y /= length;
        }
        return v;
    }

    /// <summary>Godot's Rotated: counter-clockwise by <paramref name="angle"/> radians (its Mathf.SinCos is MathF.SinCos).</summary>
    public readonly Vector2 Rotated(float angle)
    {
        (float sin, float cos) = MathF.SinCos(angle);
        return new Vector2((X * cos) - (Y * sin), (X * sin) + (Y * cos));
    }

    public static Vector2 operator +(Vector2 left, Vector2 right) => new(left.X + right.X, left.Y + right.Y);
    public static Vector2 operator -(Vector2 left, Vector2 right) => new(left.X - right.X, left.Y - right.Y);
    public static Vector2 operator -(Vector2 vec) => new(-vec.X, -vec.Y);
    public static Vector2 operator *(Vector2 vec, float scale) => new(vec.X * scale, vec.Y * scale);
    public static Vector2 operator *(float scale, Vector2 vec) => new(vec.X * scale, vec.Y * scale);
    public static Vector2 operator /(Vector2 vec, float divisor) => new(vec.X / divisor, vec.Y / divisor);
    public static bool operator ==(Vector2 left, Vector2 right) => left.Equals(right);
    public static bool operator !=(Vector2 left, Vector2 right) => !left.Equals(right);

    public readonly bool Equals(Vector2 other) => X == other.X && Y == other.Y;
    public override readonly bool Equals(object? obj) => obj is Vector2 other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(X, Y);
    public override readonly string ToString() => $"({X}, {Y})";
}

/// <summary>Godot's <c>Mathf</c> helpers the brain uses, with Godot 4's formulas (see <see cref="Vector2"/>).</summary>
public static class Mathf
{
    public const float Pi = 3.1415927f;
    public static float DegToRad(float deg) => deg * (Pi / 180.0f);
}
