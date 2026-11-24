using System;

namespace SmClient.Extras.Weather;

/// <summary>
/// The one place the weather's pieces find each other (weather W1): the networked view sets the client's model and its way to send a developer-tools command while a weather-bearing match is connected, and clears them when it
/// ends; <c>LightingGradeView</c>'s hook and the F6 panel's Weather section read them. Static because the lighting grade and the panel are created without a reference to the view; one client per process.
/// </summary>
public static class WeatherClientLink
{
    public static WeatherClientModel? Model { get; set; }

    /// <summary>Sends a weather command payload (<c>WeatherWire.CommandTag</c>); false when not connected. Null while no weather-bearing match is connected.</summary>
    public static Func<byte[], bool>? Send { get; set; }

    /// <summary>Repair 1 test aid: the view's handler of the locomotion words (<c>wxmove</c>, <c>wxslide</c>, <c>wxstop</c>; name, the rest of the word); true when the word was one of them. Set with the link.</summary>
    public static Func<string, string, bool>? TestMove { get; set; }

    /// <summary>The view that set the link lets go of it (it leaves the tree): only the owner's own model is cleared, so a newer view's link is never cleared by an older view.</summary>
    public static void Release(WeatherClientModel owner)
    {
        if (!ReferenceEquals(Model, owner)) return;
        Model = null;
        Send = null;
        TestMove = null;
    }

    /// <summary>The server carries weather (it has sent a state; the panel shows its section only then).</summary>
    public static bool Available => Model is { Has: true } && Send is not null;
}
