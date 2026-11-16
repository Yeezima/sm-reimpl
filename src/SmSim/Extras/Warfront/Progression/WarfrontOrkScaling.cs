namespace SmSim.Extras.Warfront.Progression;

/// <summary>The factors applied to ONE Ork at the moment it spawns.</summary>
public readonly record struct WarfrontOrkScale(float Health, float Damage)
{
    public static readonly WarfrontOrkScale One = new(1f, 1f);
}

/// <summary>
/// Ork scaling (plan section 5; owner brief and answer 4): off the highest player's level, HP x (1 + 0.08 (L-1)), damage x (1 + 0.05 (L-1)), then HP x (1 + 0.20 (humans-1)).
/// This is the single function the spawn hook calls (level x humans x base, once per Ork; no other place applies the humans factor). Fixed at spawn: <see cref="WarfrontOrkScaleTracker"/> records the factors once per Ork and never changes them while it lives.
/// </summary>
public static class WarfrontOrkScaling
{
    public static WarfrontOrkScale For(int highestLevel, int humans)
    {
        int l = Math.Max(1, highestLevel), h = Math.Max(1, humans);
        float hp = (1f + WarfrontProgressionRules.OrkHealthPerLevel * (l - 1)) * (1f + WarfrontProgressionRules.OrkHealthPerExtraHuman * (h - 1)) * WarfrontProgressionRules.OrkHealthBase;
        float dmg = (1f + WarfrontProgressionRules.OrkDamagePerLevel * (l - 1)) * WarfrontProgressionRules.OrkDamageBase;
        return new(hp, dmg);
    }
}

/// <summary>Remembers each living Ork's spawn scale. A spawn always overwrites the id's entry (pool ids are reused); a living Ork is never re-scaled because nothing but a spawn writes here. W3b folds this into W3's per-Ork store.</summary>
public sealed class WarfrontOrkScaleTracker
{
    readonly Dictionary<ushort, WarfrontOrkScale> _scale = new();

    public int Count => _scale.Count;

    /// <summary>Records the scale for a newly spawned Ork (overwriting any earlier use of the id). The humans factor lives only in <see cref="WarfrontOrkScaling.For"/>: call this once per spawn, nowhere else.</summary>
    public WarfrontOrkScale OnSpawn(ushort id, int highestLevel, int humans)
    {
        var s = WarfrontOrkScaling.For(highestLevel, humans);
        _scale[id] = s;
        return s;
    }

    public WarfrontOrkScale Of(ushort id) => _scale.TryGetValue(id, out var s) ? s : WarfrontOrkScale.One;

    public void OnRemove(ushort id) => _scale.Remove(id);

    public void Clear() => _scale.Clear();
}
