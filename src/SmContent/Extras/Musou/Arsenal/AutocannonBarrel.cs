namespace SmContent.Extras.Musou.Arsenal;

/// <summary>
/// W1c, the autocannon's shorter barrel (the owner, 2026-11-10, after W1b's clip: "shorten it to 1.6m, then show me a screenshot"). The model is the install's <c>turret_aa</c>: parts <c>gun</c> (stock,
/// receiver, grip and the rod under the barrel), <c>ammobarrel</c> (the drum) and <c>Recoiling_Barrel</c> (the barrel with the clamp that rides the rod), its length along the model's +Y. The "2.6 m"
/// W1b gave the owner is the muzzle locator's distance from the model's root along that axis (<c>FX_muzzle</c> at 2.621 m; <c>smtool weapon-parts --model turret_aa/turret_aa</c>); here it becomes
/// <see cref="Muzzle"/>. Only what lies ahead of the receiver's front face (<see cref="ReceiverFront"/>) moves: the barrel, its clamp and the rod's end. Nothing behind it moves, so the stock and grip
/// sit in the heavy bolter's mounts as before and the stow is unchanged.
/// <para>
/// The barrel is a plain tube from the receiver to its ribbed front (no vertex between 1.072 and 2.277 m but the clamp's), so the shortening takes the length out of the plain tube on both sides of
/// the clamp in proportion, and keeps the clamp with the rod's end cap (<see cref="ClampStart"/> to <see cref="ClampEnd"/>) and the ribbed front (<see cref="JacketStart"/> to the muzzle) at their own
/// lengths: the parts only move back. At 1.6 m that leaves 2 cm of plain tube (0.53 m of barrel past the receiver, was 1.55 m). A muzzle so close that even the kept parts do not fit squeezes them
/// too, evenly. The positions are measured on the install (supported by evidence, <c>AutocannonContentTests</c> checks them against it); the 1.6 m is the owner's; the cut's placement is ours.
/// </para>
/// </summary>
public static class AutocannonBarrel
{
    /// <summary>The install's muzzle: <c>FX_muzzle</c>'s distance from the model's root along +Y (m).</summary>
    public const float InstallMuzzle = 2.621f;
    /// <summary>The owner's barrel: the muzzle's distance from the model's root along +Y (m), measured as W1b measured the 2.6 m.</summary>
    public const float Muzzle = 1.6f;
    /// <summary>The receiver's front face: the gun body's last vertices (only the rod reaches further) and the barrel's rear end (1.069 m) lie at or behind it (m).</summary>
    public const float ReceiverFront = 1.072f;
    /// <summary>The clamp around the barrel (1.364 to 1.466 m) with the rod's end cap under it (to 1.527 m): kept whole (m).</summary>
    public const float ClampStart = 1.364f, ClampEnd = 1.527f;
    /// <summary>Where the barrel's ribbed front begins (its first vertex ring); kept whole to the muzzle (m).</summary>
    public const float JacketStart = 2.277f;
    /// <summary>The shortest muzzle the map takes (m): a few centimetres past the receiver.</summary>
    public const float Shortest = ReceiverFront + 0.05f;

    /// <summary>
    /// The map's knots (install position along +Y, shortened position), increasing, for a muzzle at <paramref name="muzzle"/> m (clamped to <see cref="Shortest"/> and above): identity at the
    /// receiver's front, the clamp and the ribbed front kept at their lengths, the plain tube between scaled by one factor. A muzzle past the install's lengthens the plain tube the same way.
    /// </summary>
    public static (float From, float To)[] Knots(float muzzle = Muzzle)
    {
        if (!float.IsFinite(muzzle)) muzzle = Muzzle;
        muzzle = MathF.Max(muzzle, Shortest);
        float plainRear = ClampStart - ReceiverFront, clamp = ClampEnd - ClampStart, plainFront = JacketStart - ClampEnd, jacket = InstallMuzzle - JacketStart;
        float front = muzzle - ReceiverFront, kept = clamp + jacket, plain = plainRear + plainFront;
        float kp = front >= kept ? (front - kept) / plain : 0f;   // the plain tube's factor
        float kk = front >= kept ? 1f : front / kept;             // the kept parts' (1 unless the muzzle is closer than they are long)
        float a = ReceiverFront + plainRear * kp, b = a + clamp * kk, c = b + plainFront * kp;
        return [(ReceiverFront, ReceiverFront), (ClampStart, a), (ClampEnd, b), (JacketStart, c), (InstallMuzzle, muzzle)];
    }

    /// <summary>
    /// Where a point at <paramref name="y"/> along the model's +Y goes (piecewise linear through <paramref name="knots"/>): unchanged at or behind the first knot, moved with the last one past the last.
    /// </summary>
    public static float Along(float y, IReadOnlyList<(float From, float To)> knots)
    {
        if (knots.Count == 0 || !(y > knots[0].From)) return y;
        for (int i = 1; i < knots.Count; i++)
        {
            var (x0, y0) = knots[i - 1];
            var (x1, y1) = knots[i];
            if (y <= x1) return x1 > x0 ? y0 + (y - x0) * (y1 - y0) / (x1 - x0) : y1;
        }
        var last = knots[^1];
        return y - last.From + last.To;
    }
}
