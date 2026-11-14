namespace SmSim;

/// <summary>
/// sim/lascannon-tap-reload (protocol 77; owner request 2026-11-15, **experimental**, classic PvP only): "make tapping the reload bind for lascannon reload 1 round at a time, with a short .4 second
/// inability to shoot during the .4s reload animation (the animation is just the normal animation sped up to match the .4s timing)." In Annihilation and Seize Ground every reload of the lascannon
/// (Space Marines' and Chaos') loads exactly one round from the reserve and takes <see cref="ReloadTime"/>; nothing fires while it runs (the weapon branch of the tick is the reload's, as for any
/// reload). A tap of Reload (shorter than the reload) is one round; the button held on runs another round when the last finishes (the sim has no hold timer, so a hold is round after round, 0.4 s each,
/// until the clip is full or the reserve is empty). The empty-clip auto reload (trigger on an empty clip) is also one round. Musou Mode and every other weapon keep their reloads. The client plays the
/// normal reload clip time-scaled to <see cref="ReloadTime"/> (<c>ReloadLeft</c> drives it).
/// </summary>
public static class LascannonTapTuning
{
    /// <summary>Seconds one tap reload takes and the shot lock it carries (owner's number).</summary>
    public const float ReloadTime = 0.4f;

    /// <summary>Whether <paramref name="w"/> reloads one round per <see cref="ReloadTime"/> under <paramref name="c"/>: the rule is on, Classic PvP (no Musou Mode) and the weapon is a lascannon.</summary>
    public static bool Applies(SimConfig c, WeaponSpec? w) =>
        c.LascannonTapReload && w is not null && !c.Extras.HasFlag(ContentExtras.Musou) && w.Recharge is null && IsLascannon(w);

    /// <summary>The seconds a reload of <paramref name="w"/> takes under <paramref name="c"/> (the client's animation and sound clock): <see cref="ReloadTime"/> for a tap reload, else the weapon's own.</summary>
    public static float Time(SimConfig c, WeaponSpec w) => Applies(c, w) ? ReloadTime : w.ReloadTime;

    /// <summary>The lascannon by its blueprint name (<c>sm_mp_exotic_las_cannon</c>, <c>csm_mp_exotic_las_cannon</c>).</summary>
    public static bool IsLascannon(WeaponSpec w) => w.Name.Contains("las_cannon", StringComparison.OrdinalIgnoreCase) || w.Name.Contains("lascannon", StringComparison.OrdinalIgnoreCase);
}

public sealed partial record SimConfig
{
    /// <summary>sim/lascannon-tap-reload (owner 2026-11-15, experimental): Classic PvP's lascannon reloads one round per <see cref="LascannonTapTuning.ReloadTime"/>. False (restore switch <c>--old-lascannon-reload</c> on the server and every client; config hash): the data's full reload. Hashed only when false.</summary>
    public bool LascannonTapReload { get; init; } = true;
}
