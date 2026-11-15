namespace SmContent;

/// <summary>
/// musou/autocannon-flash (owner 2026-11-15: "leave only the bright yellow explosive flash"): which sprites of the autocannon's hit and blast effects are kept when only the bright flash is drawn: an additive
/// sprite named flash or streaks (the data's "1 frame flash", "ground flash", "flash", "streaks"). The fire ("fire", "flare", "hot cloud"), the fireball sprays, the smoke and dust and the debris are not.
/// </summary>
public static class BrightFlash
{
    /// <summary>
    /// The alpha sprites that are dust or grit and not blood: a material naming chunks, debris, pebbles, specks, stone, dirt, dust, rock, gravel, sand, grit or the ground (the data's <c>stone_chunks_small_00</c>,
    /// <c>debris_animated</c>, <c>ground_dust</c>); <see cref="FxEffect.NoGrit"/> drops them from an effect that keeps its blood (a bolt weapon's or the autocannon's hit on an Ork).
    /// </summary>
    public static bool IsGrit(string material)
    {
        foreach (var w in GritWords) if (material.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// fx/bolt-smoke (owner 2026-11-15, "Thin the dark mist"): the alpha sprites of the Ork blood that read as a dark-red cloud: the splash and splatter cards (<c>blood_breakup</c>, at Musou's 1.5x size and amount). The drops
    /// (<c>blood_spec_ring_tp_01</c>), beams, streamers and gibs (<c>blood_meat</c>) are not.
    /// </summary>
    public static bool IsBloodMist(string material) =>
        material.Equals("blood_breakup", StringComparison.OrdinalIgnoreCase) || material.Equals("blood_beam_breakup", StringComparison.OrdinalIgnoreCase) || material.Equals("blood_exit_beam_soft", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// fx/bolt-smoke ("Thin the dark mist", "Flash only"): a sprite a bolt or autocannon hit or kill on an Ork does not draw (<see cref="FxEffect.NoMist"/>): an alpha <see cref="IsBloodMist"/> card or soft beam, or the
    /// additive fire puff (<c>muzzle_fire_anim</c>) of the gore's <c>explosion</c> ring. The bright flashes, the hard blood spikes, drops, streamers and gibs stay.
    /// </summary>
    public static bool IsMistSprite(bool additive, string material) =>
        additive ? material.Contains("fire_anim", StringComparison.OrdinalIgnoreCase) : IsBloodMist(material);

    static readonly string[] GritWords = ["chunk", "debris", "pebble", "speck", "specs", "stone", "dirt", "dust", "rock", "gravel", "sand", "grit", "ground"];

    public static bool Keeps(bool additive, bool distortion, string name) =>
        additive && !distortion && (name.Contains("flash", StringComparison.OrdinalIgnoreCase) || name.Contains("streaks", StringComparison.OrdinalIgnoreCase));
}
