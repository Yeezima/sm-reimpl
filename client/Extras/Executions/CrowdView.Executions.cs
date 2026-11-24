using System.Collections.Generic;

namespace SmClient;

/// <summary>
/// Executions X5a: the crowd's two executions hooks (<c>ExecutionsHideEnemy</c> in CrowdView.Draw, <c>ExecutionsCorpseProxied</c> in MusouCrowd.AddBody). A Warfront Ork
/// that is being executed is drawn by its proxy (X5's <c>ExecutionProxy</c>) from the Start until the proxy's corpse has sunk, so the crowd skips it and makes no corpse of
/// its own. The presenter fills <see cref="ExecutionHidden"/>; it stays null (nothing hidden) everywhere else, Musou Mode included.
/// </summary>
public abstract partial class CrowdView
{
    /// <summary>Enemy ids drawn by an execution proxy (null or empty: none).</summary>
    public HashSet<int>? ExecutionHidden { get; set; }

    partial void ExecutionsHideEnemy(int id, ref bool hide)
    {
        if (ExecutionHidden is { Count: > 0 } set && set.Contains(id)) hide = true;
    }
}

public partial class MusouCrowd
{
    partial void ExecutionsCorpseProxied(int id, ref bool proxied)
    {
        if (ExecutionHidden is { Count: > 0 } set && set.Contains(id)) proxied = true;
    }
}
