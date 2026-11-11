using SmSim;

namespace SmNet;

/// <summary>
/// anim/melee-interaction: the own charge's contact predicted (<see cref="ChargeLock.Predict"/>): the server resolves it with the rewound world the input names and the bodies its
/// movement collided with; the client holds both (a charge tick's view tick is a held snapshot: <see cref="MeleeLock.Wants"/>), so the stop into the collision stop and the turn to
/// the victim happen on the contact tick in the own view instead of a round trip later.
/// </summary>
public sealed partial class GameClient
{
    /// <summary>
    /// The last contact the own prediction found: its input tick, the player met (0: none yet) and the lock spot where the server puts a stunned victim (<see cref="ChargeLock.LockDistance"/>
    /// in front of the stopped charger). Presentation reads it (hit-stop, impact effects, drawing the victim at the lock spot until the snapshots show it there).
    /// </summary>
    public (uint Tick, int Target, System.Numerics.Vector3 Spot) LastChargeContact { get; private set; }
    /// <summary>
    /// feel/charge-clash: whether the last predicted contact opens a clash (the victim, as the contact's view shows it, an opponent the bull rush's stun can take: <see cref="ChargeClash.Predicts"/>;
    /// the server decides with the victim as it is, so a victim whose immunity changed within a round trip is corrected).
    /// </summary>
    public bool LastChargeClash { get; private set; }
    /// <summary>Diagnostics (bot log, tests): rush ticks checked for a contact, those without a view, contacts found (replays included).</summary>
    public int ChargeContactChecks { get; private set; }
    public int ChargeContactNoView { get; private set; }
    public int ChargeContactsPredicted { get; private set; }
    /// <summary>charge-ally-bump (protocol 66): own charges predicted to end on an ally (no stop; replays included).</summary>
    public int ChargeBumpsPredicted { get; private set; }

    /// <summary>
    /// feel/charge-clash: the bodies an own step collides with, less the own charge's victim while the collision stop shoves it (<see cref="ChargeClash.Carrying"/>; the server's world
    /// leaves it out of the charger's bodies alike and carries it). The victim is the last predicted contact before this input.
    /// </summary>
    BodyView? ClashBodies(PlayerState before, PlayerInput input, BodyView? bodies) =>
        bodies is not null && LastChargeClash && LastChargeContact.Target > 0 && LastChargeContact.Tick < input.Tick && ChargeClash.Carrying(before, _config)
            ? ChargeClash.Without(bodies, LastChargeContact.Target) : bodies;

    PlayerState PredictChargeContact(PlayerState before, PlayerState next, PlayerInput input, IReadOnlyDictionary<int, PlayerState>? view, BodyView? bodies)
    {
        if (!_config.ChargeLock || ChargeLock.Charging(before, next, _config) is not { Rush: RushPhase.Rush }) return next;   // the press tick's move counts too
        // A replay names no view: the one its input was predicted with, when its view tick is a whole snapshot still held (as the server rounds it).
        view ??= input.ViewFraction == 0f ? ViewAt(input.ViewTick) : null;
        ChargeContactChecks++;
        if (view is null) ChargeContactNoView++;
        // charge-ally-bump (protocol 66): an ally met ends the charge with no stop unless the match has friendly fire (the scoreboard says; before the first one no bump is predicted: the old stop, which the server also plays when it has no rules; review 2026-11-11).
        var after = ChargeLock.Predict(PlayerId, before, next, _config, view, bodies, Collision, out var contact, friendlyFire: Scoreboard?.FriendlyFire ?? true);
        if (contact is { Bump: true })
        {
            ChargeBumpsPredicted++;   // no connect: no impact presentation, no clash, no lock spot
            return after;
        }
        if (contact is { } c)
        {
            ChargeContactsPredicted++;
            var forward = new System.Numerics.Vector3(-MathF.Sin(after.Yaw), MathF.Cos(after.Yaw), 0f);
            if (input.Tick > LastChargeContact.Tick)
            {
                LastChargeContact = (input.Tick, c.Target, after.Position + forward * ChargeLock.LockDistance(_config));
                LastChargeClash = view is not null && view.TryGetValue(c.Target, out var victim) && ChargeClash.Predicts(before.Team, victim, next, _config);
            }
        }
        return after;
    }
}
