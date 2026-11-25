using System.Collections.Generic;
using Godot;

namespace SmClient;

/// <summary>
/// Executions X5a/X5: the crowd's two executions hooks (<c>ExecutionsHideEnemy</c> in CrowdView.Draw, <c>ExecutionsCorpseProxied</c> in MusouCrowd.AddBody). A Warfront Ork
/// that is being executed is drawn by its proxy (X5's <c>ExecutionProxy</c>) from the Start until the proxy's corpse has sunk, so the crowd skips it and makes no corpse of
/// its own. The presenter fills <see cref="ExecutionHidden"/>; the crowd notes where each hidden member was drawn (<see cref="ExecutionSeen"/>: its type, feet and yaw, game
/// space) so the proxy wears its look and stands on its ground. Both stay empty everywhere else, Musou Mode included.
/// </summary>
public abstract partial class CrowdView
{
    /// <summary>Enemy ids drawn by an execution proxy (null or empty: none).</summary>
    public HashSet<int>? ExecutionHidden { get; set; }

    /// <summary>The last drawn type, feet (game space) and yaw of each hidden member.</summary>
    public readonly Dictionary<int, (int Type, Vector3 Position, float Yaw)> ExecutionSeen = new();

    partial void ExecutionsHideEnemy(int id, int type, Vector3 position, float yaw, ref bool hide)
    {
        if (ExecutionHidden is not { Count: > 0 } set || !set.Contains(id)) return;
        hide = true;
        ExecutionSeen[id] = (type, position, yaw);
    }
}

public partial class MusouCrowd
{
    partial void ExecutionsCorpseProxied(int id, ref bool proxied)
    {
        if (ExecutionHidden is { Count: > 0 } set && set.Contains(id)) proxied = true;
    }
}
