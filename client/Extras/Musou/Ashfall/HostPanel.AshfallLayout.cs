namespace SmClient.FrontEnd;

// Musou Mode (added content): the host panel's Ashfall layout choice, reached through HostPanel.ExtrasHostMap.
public sealed partial class HostPanel
{
    /// <summary>
    /// Draft 11 and 12: <c>--ashfall-layout field</c> hosts Musou Mode on Ashfall's battlefield layout (<c>tfw_war_field</c>), <c>--ashfall-layout draft10</c> on the draft-10 rollback (<c>tfw_war_draft10</c>); without it the server's
    /// default (<c>tfw_war</c>, the city layout since draft 12).
    /// </summary>
    partial void ExtrasHostMap(ref string? map)
    {
        if (!Musou || !_args.TryGetValue("ashfall-layout", out var layout)) return;
        if (layout == "field") map = SmSim.Extras.Musou.Campaign.TfwMap.FieldName;
        else if (layout == "draft10") map = SmSim.Extras.Musou.Campaign.TfwMap.Draft10Name;
    }
}
