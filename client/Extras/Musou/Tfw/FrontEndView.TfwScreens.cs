using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;

namespace SmClient.FrontEnd;

/// <summary>
/// Screenshot aids for Musou Mode's menu-type pages (ui/overhaul): <c>--menu-screen tfw-range|tfw-war|tfw-score|tfw-reward|tfw-run</c> show the range confirmation, the station's war map, the score, reward and run pages with made-up
/// figures over the menu background. Added content (Extras/Musou); nothing in a faithful build.
/// </summary>
public sealed partial class FrontEndView
{
    partial void ExtrasMenuScreen(string name, ref bool handled)
    {
        TfwMapScore Map(byte node, TfwMapResult r, ushort kos, ushort chain, uint points) => new(TfwLane.A, node, r, kos, chain, points);
        Control? page = null;
        switch (name.ToLowerInvariant())
        {
            case "tfw-range": { var p = new TfwRangeConfirm(() => { }, () => { }); page = p; p.Focus(); break; }
            case "tfw-war":
            {
                var p = new WarMapPanel(_ => { }, () => { });
                p.Show(new TfwWarView(TfwRunPhase.Hub, TfwLane.A, 5025f, 1f, 3, 12, 0,
                    new TfwLaneState(1, 0.35f, 420f, 310f, 3, 0f, 0), new TfwLaneState(0, -0.2f, 300f, 360f, 1, 18f, 0), new TfwLaneState(2, 0.7f, 520f, 150f, 7, 0f, 0)), true);
                page = p; p.Focus(); break;
            }
            case "tfw-score": { var p = new TfwScorePanel(Map(4, TfwMapResult.Won, 312, 48, 12840), 40250u, () => { }); page = p; p.Focus(); break; }
            case "tfw-reward":
            {
                var offer = new TfwRewardOffer(1, new TfwRewardCard(TfwRewardKind.Weapon, 1), new TfwRewardCard(TfwRewardKind.Equipment, 1), new TfwRewardCard(TfwRewardKind.Perk, 1));
                var p = new TfwRewardPanel(offer, c => c.Kind.ToString().ToUpperInvariant() + " " + c.Item, _ => { }); page = p; p.Focus(); break;
            }
            case "tfw-run":
            {
                var maps = new List<TfwMapScore> { Map(1, TfwMapResult.Won, 220, 31, 8100), Map(2, TfwMapResult.Won, 264, 40, 10300), Map(3, TfwMapResult.Won, 301, 44, 11500), Map(4, TfwMapResult.Won, 312, 48, 12840) };
                var p = new TfwRunPanel(true, maps, 40250u, new List<(string, uint)> { ("Brother Castus", 40250u), ("Sgt. Aurelian", 31200u) }, true, null, "RUN", () => { }, () => { }); page = p; p.Focus(); break;
            }
        }
        if (page is null) return;
        HideAll();
        AddChild(page);
        handled = true;
    }
}
