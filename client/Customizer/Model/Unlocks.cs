using System;

namespace SmClient.Customizer.Model;

/// <summary>Why an item can or cannot be chosen (docs/customizer-design.md section 4: <c>Unlock = Unlocked | Level(n) | Challenge(text, progress, target) | NeedsPerk(name) | Unavailable(reason)</c>).</summary>
public enum UnlockKind { Unlocked, Level, Challenge, NeedsPerk, Unavailable }

/// <param name="Level">The player level required (<see cref="UnlockKind.Level"/>).</param>
/// <param name="Text">The challenge text, the perk's name or the reason.</param>
public sealed record Unlock(UnlockKind Kind, int Level = 0, string? Text = null, int Progress = 0, int Target = 0)
{
    public static readonly Unlock Open = new(UnlockKind.Unlocked);
    public bool IsOpen => Kind == UnlockKind.Unlocked;

    /// <summary>The caption of the item card ("Required Level: 8", "Challenge: Bolter: Kill 50 enemies (12/50)", ...); empty when unlocked.</summary>
    public string Caption => Kind switch
    {
        UnlockKind.Level => $"Required Level: {Level}",
        UnlockKind.Challenge => $"Challenge: {Text}" + (Target > 0 ? $" ({Progress}/{Target})" : ""),
        UnlockKind.NeedsPerk => $"Requires perk: {Text}",
        UnlockKind.Unavailable => Text ?? "Not available",
        _ => "",
    };

    /// <summary>The short tag on a row ("LOCKED", "NEEDS PERK").</summary>
    public string Tag => Kind switch { UnlockKind.Level or UnlockKind.Challenge => "LOCKED", UnlockKind.NeedsPerk => "NEEDS PERK", UnlockKind.Unavailable => "UNAVAILABLE", _ => "" };
}

/// <summary>
/// The one switch for level gates (architecture decision 8: <c>UnlockMode.All</c> is the only working mode; the owner wants no level progression).
/// <see cref="Evaluate"/> returns <see cref="Unlock.Open"/> for everything in that mode; the lock visuals exist behind <c>--lock-demo</c>, which forces a
/// sample of each state so they can be seen and tested.
/// </summary>
public sealed class UnlockPolicy
{
    public static readonly UnlockPolicy All = new(false);
    public static UnlockPolicy Demo => new(true);

    readonly bool _demo;
    UnlockPolicy(bool demo) => _demo = demo;
    public bool IsDemo => _demo;

    /// <summary>The unlock state of the <paramref name="index"/>-th item of a list (the index only matters in the demo, which cycles the states).</summary>
    public Unlock Evaluate(int index, int level, string? challengeText)
    {
        if (!_demo) return Open();
        return (index % 5) switch
        {
            1 => new Unlock(UnlockKind.Level, Math.Max(level, 8)),
            2 => new Unlock(UnlockKind.Challenge, 0, challengeText ?? "Kill 50 enemies with it", 12, 50),
            3 => new Unlock(UnlockKind.Unavailable, 0, "Not available in this build"),
            _ => Open(),
        };
    }

    static Unlock Open() => Unlock.Open;
}
