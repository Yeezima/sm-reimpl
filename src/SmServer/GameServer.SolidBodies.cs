namespace SmServer;

public sealed partial record ServerOptions
{
    /// <summary>
    /// fix/charge-player-collision: the world's guard against bodies passing through each other as they really stand and the charge's check of the bodies as they really moved
    /// (<see cref="SmSim.World.SolidGuard"/>; server-side rules, not predicted). On by default; tests of exact prediction turn it off to keep measuring only what a client predicts.
    /// </summary>
    public bool BodyGuard { get; init; } = true;
}
