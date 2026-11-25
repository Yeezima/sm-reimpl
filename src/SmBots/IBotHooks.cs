using SmSim;

namespace SmBots;

/// <summary>
/// What the Godot client adds to the brain: its test aids that script the whole input, added content's own bot (the client's <c>ExtrasBotInput</c> hook: Musou Mode's
/// co-op bot) and the charge-direction test aid's press log. Every member does nothing by default (<see cref="None"/>, a server's brain).
/// </summary>
public interface IBotHooks
{
    /// <summary>A test aid's whole input for this tick (the client's --jump-test, --slash-test, --hop-rules-test, ... in their old order), or null for the brain's.</summary>
    PlayerInput? TestAidInput(PlayerState me, uint tick) => null;
    /// <summary>Added content's own bot input (the client's adapter calls its <c>ExtrasBotInput</c> hook here), or null: the Classic bot plays. Not an <c>Extras*</c> name: the hook itself stays the client's pinned partial method.</summary>
    PlayerInput? AddedContentInput(PlayerState me, uint tick) => null;
    /// <summary>A charge or dash attack press, for the client's charge-direction travel log (<c>--bot-charge-dirs</c>, <c>--bot-dash-dirs</c>).</summary>
    void NotePress(float view, string keys, float speed) { }
    /// <summary>The client's frame-profile phase marker (its frame watchdog names the phase a slow frame stalled in).</summary>
    void Phase(string phase) { }

    /// <summary>No hooks: no test aid, no added content's bot.</summary>
    public static readonly IBotHooks None = new NoHooks();

    sealed class NoHooks : IBotHooks;
}
