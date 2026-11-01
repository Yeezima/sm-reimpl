using SmSim.Extras.Musou;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The class change in the Stormbird (Musou Mode pass 2, item 2; design section G). The owner: in the Stormbird, "deploy as selected custom class" switches classes on the fly, with no death
/// and no respawn wait. The client sends its Setup first (the core stores it as the player's next loadout and acknowledges it), then <see cref="TfwCommand.ChangeClass"/>; here a fresh life is
/// made at once, on the spot, through <see cref="World.TfwRespawnAt"/> (the one routine the Deploy and the extraction use too): the waiting loadout applies, <c>LastRespawns</c> makes the server
/// promote the waiting look, and the next <c>MusouArm</c> arms the life as every other. Nothing here knows what a life carries (the starting weapon, the extra weapon slots a reward gives):
/// whatever the war gives a fresh life, a class change gives, so those rules live in one place.
/// </summary>
public sealed partial class TfwRun
{
    readonly List<TfwFireEvent> _smoke = new();

    /// <summary>Class changes since the server last took them: the smoke clouds (tag 11, <see cref="TfwFireKind.Smoke"/>).</summary>
    public IReadOnlyList<TfwFireEvent> Smoke => _smoke;

    /// <summary>Another record for tag 11 (a big frag's blast, <see cref="World"/>): the smoke's list is the list of what waits for the server.</summary>
    internal void AddFire(TfwFireEvent fire)
    {
        if (_smoke.Count >= MaxSmoke) _smoke.RemoveAt(0);
        _smoke.Add(fire);
    }

    /// <summary>The server took the smoke records.</summary>
    public void ClearSmoke() => _smoke.Clear();

    /// <summary>Most smoke records kept waiting for the server (nothing takes them in a test without a server).</summary>
    const int MaxSmoke = 64;

    /// <summary>
    /// A human's request to take the class its last Setup named, now. It stands only in the hub (<see cref="TfwRunPhase.Hub"/>: not in the travel, not in a battle), for a player alive and
    /// inside the Stormbird, with a Setup waiting (<see cref="World.NextLoadout"/>: without one it would be a free refill and a stripped weapon). Anything else is dropped without a word.
    /// Returns whether the class changed.
    /// </summary>
    internal bool ChangeClass(World w, int player)
    {
        if (Phase != TfwRunPhase.Hub || TfwContracts.IsPuppet(player) || !w.Players.TryGetValue(player, out var p) || !p.Alive || !Field.Hub.Contains(p.Position)) return false;
        if (w.NextLoadout(player) is null) return false;
        if (!w.TfwRespawnAt(player, p.Position, p.Yaw)) return false;
        if (_smoke.Count >= MaxSmoke) _smoke.RemoveAt(0);
        _smoke.Add(new TfwFireEvent(TfwFireKind.Smoke, w.Tick, (ushort)player, p.Position));
        return true;
    }
}
