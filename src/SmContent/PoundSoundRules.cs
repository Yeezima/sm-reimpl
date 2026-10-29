namespace SmContent;

/// <summary>
/// When the client posts the ground pound's impact sound (M41), kept out of the Godot client so that it can be tested. A pound is "pounding" from the dive to the end of the recovery
/// (the snapshot's pound flag; own: the Dive and Recover phases). The impact is the first state of that run with the player on the ground; the sound plays then, once per pound. If the
/// impact was never seen (a snapshot gap: the run goes from the air to the end), it plays when the pound ends instead. Never twice for one pound.
/// </summary>
public static class PoundSoundRules
{
    /// <summary>
    /// One state step for one player: <paramref name="sounded"/> is this pound's flag from the last step; returns whether to post the sound now and the new flag.
    /// </summary>
    /// <param name="pounding">The player is pounding (dive or recovery) in the new state; <paramref name="prevPounding"/> in the previous one.</param>
    /// <param name="grounded">The player is on the ground in the new state; <paramref name="prevGrounded"/> in the previous one.</param>
    public static (bool Post, bool Sounded) Step(bool pounding, bool grounded, bool prevPounding, bool prevGrounded, bool sounded)
    {
        bool impact = pounding && grounded && !(prevPounding && prevGrounded);
        bool ended = prevPounding && !pounding;
        bool post = !sounded && (impact || ended);
        // The flag holds while the pound lasts and clears once it is over (after the end's own check above).
        return (post, pounding && (sounded || post));
    }
}
