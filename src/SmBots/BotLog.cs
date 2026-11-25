namespace SmBots;

/// <summary>
/// Where the bot brain's log lines go (the Godot client: <c>GD.Print</c> and <c>GD.PrintErr</c>, so a bot's log reads as before the move). The brain calls it
/// under the name <c>GD</c> (<see cref="ClassicBotBrain"/>), so its log lines kept their code. A sink without writers drops the lines (a shadow brain, tests).
/// </summary>
public sealed class BotLog(Action<string>? print = null, Action<string>? printErr = null)
{
    /// <summary>Drops every line.</summary>
    public static readonly BotLog None = new();

    public void Print(string text) => print?.Invoke(text);
    public void PrintErr(string text) => printErr?.Invoke(text);
}
