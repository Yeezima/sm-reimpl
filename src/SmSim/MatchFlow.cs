namespace SmSim;

/// <summary>How a match ended for one viewer (the splash shows one of three clips of the original's <c>HudText.gfx</c>).</summary>
public enum MatchResult { Victory, Defeat, Draw }

/// <summary>
/// The rules of the match start that client and server share (match flow, ours; spec/behavior.md "Match flow"). While
/// <see cref="MatchState.Countdown"/> runs everyone stands at a spawn point and may look but not move or act (sim/combat-2; before: move and look but not attack): the stick and the buttons
/// are dropped from the input before the step, on the server and in the client's prediction alike (so the prediction
/// does not spend ammunition the server never sees).
/// </summary>
public static class MatchFlow
{
    /// <summary>Buttons that do nothing during the countdown: the trigger, both melee strikes (which carry the charge) and the grenade.</summary>
    public const InputButtons LockedDuringCountdown = InputButtons.Fire | InputButtons.Melee | InputButtons.MeleeHeavy | InputButtons.Grenade;

    /// <summary>The input as the countdown lets it act: the stick and every button dropped (sim/combat-2: before, only <see cref="LockedDuringCountdown"/>); only the view stays.</summary>
    public static PlayerInput Locked(PlayerInput input) => input with { MoveX = 0f, MoveY = 0f, Buttons = InputButtons.None };   // sim/combat-2 (item 42): no movement and no buttons at all; the view (yaw, pitch) stays free

    /// <summary>How a match that ended with <paramref name="winner"/> went for a player of <paramref name="own"/>: a draw, else victory for the winner's side (and for a viewer without a side), defeat for the other.</summary>
    public static MatchResult ResultFor(Team own, Team winner) =>
        winner == Team.None ? MatchResult.Draw : own == Team.None || own == winner ? MatchResult.Victory : MatchResult.Defeat;

    /// <summary>
    /// The music stinger of the original (Wwise events of the in-game bank: music segments) for a result and the viewer's side, or null: a draw
    /// has none in the data. Which side's pair plays for a viewer without a side is ours (the Space Marines').
    /// </summary>
    public static string? StingerFor(Team own, MatchResult result) => result switch
    {
        MatchResult.Victory => own == Team.Chaos ? "play_mp_chaos_victory" : "play_mp_sm_victory",
        MatchResult.Defeat => own == Team.Chaos ? "play_mp_chaos_loss" : "play_mp_sm_loss",
        _ => null,
    };

    /// <summary>Whole seconds shown on the countdown: the seconds left, rounded up (5, 4, 3, 2, 1).</summary>
    public static int DisplaySeconds(double secondsLeft) => (int)Math.Max(0, Math.Ceiling(secondsLeft - 1e-9));

    /// <summary>How long the victory/defeat splash plays before the scoreboard comes up: 195 frames of the HUD clip at 30 fps (<c>WinLose_*_mc</c> of HudText.gfx).</summary>
    public const float SplashSeconds = 195f / 30f;
}
