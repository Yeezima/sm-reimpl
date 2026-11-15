using Godot;
using SmNet;

namespace SmClient;

/// <summary>
/// fix/leave-cleanup (owner, 2026-11-15): one deterministic teardown for a deliberate leave. Leftovers of a match (the death screen's prompts, the HUD's disconnect box, game sounds)
/// were seen on the main menu after leaving a hosted match. <see cref="LeaveNow"/> marks the session as left first (so a late timeout or disconnect says nothing), closes the connection,
/// stops every sound, hides the whole view and stops its processing; <c>ModelViewer.DropNetwork</c> then takes it out of the tree at once, before the front end shows.
/// </summary>
public partial class NetworkView
{
    /// <summary>The player left on purpose (or the front end dropped this view): no status, error box or prompt of this view shows any more.</summary>
    public bool Left { get; private set; }

    /// <summary>The connection's state (null: no client yet), and the seconds connected; for the front end's and the leave check's reading.</summary>
    public ClientStatus? ClientStatusNow => _client?.Status;
    public double ConnectedSeconds => _connectedFor;
    /// <summary>The status line's text now (empty after a leave or while all is well).</summary>
    public string StatusTextNow => StatusLine().Text;
    /// <summary>Whether any HUD, loadout hint or picker layer of this view is still visible.</summary>
    public bool AnyLayerVisible => AnyVisibleLayer(this);
    static bool AnyVisibleLayer(Node n) { foreach (var c in n.GetChildren()) { if (c is CanvasLayer { Visible: true }) return true; if (AnyVisibleLayer(c)) return true; } return false; }

    /// <summary>The teardown; safe to call twice.</summary>
    public void LeaveNow()
    {
        if (Left) return;
        Left = true;
        try { _client?.Leave(); }
        catch (System.Exception ex) when (ex is not (System.OutOfMemoryException or System.StackOverflowException)) { GD.PrintErr("leave: " + ex.Message); }
        SetProcess(false); SetPhysicsProcess(false); SetProcessInput(false); SetProcessUnhandledInput(false); SetProcessUnhandledKeyInput(false);
        StopAllSounds(this);
        HideAll(this);
        Visible = false;
    }

    /// <summary>Stops every audio player below <paramref name="node"/> (the sound system's emitters, the barks, the melee hits); returns how many it stopped.</summary>
    public static int StopAllSounds(Node node)
    {
        int stopped = 0;
        foreach (var child in node.GetChildren())
        {
            if (child is AudioStreamPlayer a) { a.Stop(); stopped++; }
            else if (child is AudioStreamPlayer2D a2) { a2.Stop(); stopped++; }
            else if (child is AudioStreamPlayer3D a3) { a3.Stop(); stopped++; }
            stopped += StopAllSounds(child);
        }
        return stopped;
    }

    /// <summary>Counts the audio players at or below <paramref name="node"/> that are playing now (a test aid's check).</summary>
    public static int PlayingSounds(Node node)
    {
        int playing = node is AudioStreamPlayer { Playing: true } or AudioStreamPlayer2D { Playing: true } or AudioStreamPlayer3D { Playing: true } ? 1 : 0;
        foreach (var child in node.GetChildren()) playing += PlayingSounds(child);
        return playing;
    }

    static void HideAll(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is CanvasLayer layer) layer.Visible = false;
            else if (child is CanvasItem item && item.GetParent() is not CanvasItem) item.Visible = false;
            HideAll(child);
        }
    }
}
