namespace SmClient.FrontEnd;

// Musou Mode (added content): the host panel's Ashfall layout choice, reached through HostPanel.ExtrasHostMap.
public sealed partial class HostPanel
{
    /// <summary>Draft 11: <c>--ashfall-layout field</c> hosts Musou Mode on Ashfall's battlefield layout (<c>tfw_war_field</c>); without it the server's default (<c>tfw_war</c>, draft 10).</summary>
    partial void ExtrasHostMap(ref string? map)
    {
        if (Musou && _args.TryGetValue("ashfall-layout", out var layout) && layout == "field") map = SmSim.Extras.Musou.Campaign.TfwMap.FieldName;
    }
}
