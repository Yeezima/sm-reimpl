namespace SmSim.Extras.Musou.Arsenal;

/// <summary>
/// Musou's arsenal (W0, the base; docs/new-weapons-design.md section 8): the four items Musou Mode adds, by wargear name, and the simulation's rules that are not the
/// <c>World</c>'s (those are <c>World.Arsenal.cs</c>). Musou Mode's: the names reach a config through <c>SmContent.Extras.Musou.Arsenal.Arsenal.Append</c>, which runs under
/// <see cref="ContentExtras.Musou"/>; the one exception is the autocannon, which Classic PvP's <see cref="ContentExtras.Autocannon"/> also appends (the owner, 2026-11-11; Classic's own
/// numbers, <c>Arsenal.WithClassicAutocannon</c>), so no other profile's lists carry them. Three are our own names; the Remote Detonators' is an install name (<see cref="RemoteDetonators"/>), so every hook
/// that acts on a name also checks the Musou flag (the client's model hooks too), never the name alone. Each item's rules are partial methods its own milestone implements in its own file (W1 to W4);
/// until then every one is a no-op and nothing changes.
/// </summary>
public static partial class ArsenalRules
{
    /// <summary>The autocannon (W1): the loyalist twin of the install's <c>csm_mp_exotic_auto_cannon</c>, built in code (ours; never written to disk).</summary>
    public const string Autocannon = "sm_mp_exotic_auto_cannon";

    /// <summary>The lightning claw (W2): ours, no wargear file in the install.</summary>
    public const string LightningClaw = "sm_mp_melee_lightning_claw";

    /// <summary>The power spear (W3): ours, no wargear file in the install.</summary>
    public const string PowerSpear = "sm_mp_melee_power_spear";

    /// <summary>The Remote Detonators (W4): the install's own <c>sm_mp_eq_demolition_charge</c>, which no class lists.</summary>
    public const string RemoteDetonators = "sm_mp_eq_demolition_charge";

    /// <summary>The four names, in milestone order.</summary>
    public static readonly IReadOnlyList<string> Names = [Autocannon, LightningClaw, PowerSpear, RemoteDetonators];

    static readonly HashSet<string> NameSet = new(Names, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="wargear"/> is one of the arsenal's items (case ignored). Allocation-free: the client asks it every frame in Musou (W0b review).</summary>
    public static bool IsArsenal(string? wargear) => wargear is not null && NameSet.Contains(wargear);

    /// <summary>Whether <paramref name="wargear"/> is the autocannon (case ignored).</summary>
    public static bool IsAutocannon(string? wargear) => string.Equals(wargear, Autocannon, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a match or page with <paramref name="extras"/> carries arsenal item <paramref name="wargear"/>, so the client's arsenal hooks (model, presentation, HUD, customizer) act for it:
    /// every arsenal item in Musou Mode (<see cref="ContentExtras.Musou"/>), and the autocannon alone under <see cref="ContentExtras.Autocannon"/> (Classic PvP, the owner 2026-11-11). False for
    /// every other name and profile (the faithful game above all). Allocation-free.
    /// </summary>
    public static bool Shown(ContentExtras extras, string? wargear) =>
        extras.HasFlag(ContentExtras.Musou) ? IsArsenal(wargear) : extras.HasFlag(ContentExtras.Autocannon) && IsAutocannon(wargear);

    /// <summary>
    /// Whether an arsenal melee weapon takes the thunder hammer's jump-pack rules (<c>Loadouts.WieldsHammer</c>: no pound delay, the hop swing, the steered slam), through the
    /// dispatcher of <c>Loadouts.ExtrasWieldsHammer</c>. False until W3 implements <see cref="SpearWieldsHammer"/>.
    /// </summary>
    public static bool WieldsHammer(string wargear)
    {
        bool hammer = false;
        SpearWieldsHammer(wargear, ref hammer);
        return hammer;
    }

    /// <summary>W3 (<c>PowerSpear</c>'s simulation file): the spear takes the hammer's rules.</summary>
    static partial void SpearWieldsHammer(string wargear, ref bool hammer);
}
