using SmSim;

namespace SmContent;

/// <summary>
/// The developer tools' map picker (owner, 2026-11-17; ours, a test aid): the maps a <c>--dev-tools</c> server can switch to, in the order the panel lists them. The same set the server's
/// <c>--maps all</c> rotation plays (<see cref="RotationSupervisor"/> in the server: every playable competitive map of the install that offers the game mode), the default Manufactorum first and the
/// rest by display name. The wire carries an index into this list (<c>DevCommandKind.PickMap</c>), so the server and the client must build it the same way from the same install; engine-free so
/// tests can pin it.
/// </summary>
public static class DevMapPicks
{
    /// <summary>The pickable maps for a game mode: playable here and offering that mode (Seize Ground, else Annihilation).</summary>
    public static IReadOnlyList<MapEntry> For(IEnumerable<MapEntry> catalog, GameMode mode) =>
        catalog.Where(m => m.Playable && (mode == GameMode.SeizeGround ? m.SupportsSeize : m.SupportsAnnihilation))
               .OrderBy(m => m.Name == HostMapChoices.DefaultMap ? 0 : 1).ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Name, StringComparer.Ordinal)
               .ToList();
}
