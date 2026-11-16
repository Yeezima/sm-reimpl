using System;
using Godot;

namespace SmClient.FrontEnd;

/// <summary>What the front end remembers about this game's Warfront server across front-end rebuilds (a match replaces the menu; coming back builds it again). Static, in this process only.</summary>
static class WarfrontMenuState
{
    /// <summary>A join was made to the server this client started and the menu has not been built again since.</summary>
    public static bool Joined;
    /// <summary>Joins that failed on this server (one retry is allowed, then the server stops).</summary>
    public static int Retries;
}

/// <summary>
/// The Warfront page's frame node (slice W16): watches the server's start, updates the preparing screen, and takes Esc and B on Warfront's pages (the front end's own back step knows nothing of them).
/// A child of the front end; everything it does is the front end's partial (<c>FrontEndView.WarfrontMenu.cs</c>).
/// </summary>
sealed partial class WarfrontMenuWatcher : Node
{
    readonly Action _tick;
    readonly Func<InputEvent, bool> _back;

    public WarfrontMenuWatcher(Action tick, Func<InputEvent, bool> back)
    {
        Name = "WarfrontWatcher";
        _tick = tick; _back = back;
    }

    public override void _Process(double delta) => _tick();

    public override void _Input(InputEvent e)
    {
        if (PadHost.IsBack(e) && _back(e)) GetViewport().SetInputAsHandled();
    }
}
