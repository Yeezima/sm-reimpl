using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmClient.Customizer;

namespace SmClient.FrontEnd;

// ui/text-scale (owner, 2026-11-18): one size per level of text across the menus, a whole number of window pixels at the window's scale, from the one table (UiType).
public sealed partial class UiSelfTest
{
    /// <summary>
    /// Opens the lobby, host, join and the four Options tabs at the window size now and checks every piece of live text on them: its size is a size of the table, it is a whole number of window pixels, every plate
    /// (a button that is not a row) is one size, every BACK is that size too, and the title of Options and of the pages is one size.
    /// </summary>
    async Task TextLevelChecks(string at)
    {
        int plate = UiType.Px(UiType.Plate);
        var table = new HashSet<int> { UiType.Px(UiType.Title), UiType.Px(UiType.Plate), UiType.Px(UiType.Label), UiType.Px(UiType.Value), UiType.Px(UiType.Caption), UiType.Px(UiType.Small), UiType.Px(UiType.Primary) };
        var loose = new List<string>(); var fractional = new List<string>(); var plates = new List<string>(); var backs = new HashSet<int>(); var titles = new HashSet<int>();
        foreach (var screen in new[] { "lobby", "host", "joinlocal", "graphics", "sound", "controls", "interface" })
        {
            _front.ShowMenuPage(screen);
            await Frames(4);
            Node page = screen is "graphics" or "sound" or "controls" or "interface" ? _front.Options! : screen == "lobby" ? _front.LobbyPage! : screen == "host" ? _front.HostPage! : _front.JoinLocalPage!;
            foreach (var item in UiTextAudit.Collect(page))
            {
                if (!table.Contains(item.FontSize)) loose.Add($"{screen}: {item.Text} {item.FontSize}");
                if (item.Fractional) fractional.Add($"{screen}: {item.Text} {item.WindowPx:0.##}");
                var node = page.GetNodeOrNull(item.Path);
                bool isPlate = node is Button and not UiKit.MenuChoice && !(item.Path.Contains("/Choice") || item.Path.Contains("/Slider"));
                if (isPlate && item.FontSize != plate) plates.Add($"{screen}: {item.Text} {item.FontSize}");
                if (node is Button { Text: "BACK" }) backs.Add(item.FontSize);
                if (node is Label title && (title.Name == "PanelTitle" || title.Name == "OptionsPlate")) titles.Add(item.FontSize);
            }
        }
        Check($"text {at}: every piece of text is a size of the one table", loose.Count == 0, string.Join("; ", loose.Take(6)));
        Check($"text {at}: every size is a whole number of window pixels", fractional.Count == 0, string.Join("; ", fractional.Take(6)));
        Check($"text {at}: every plate (button) is one size", plates.Count == 0, string.Join("; ", plates.Take(6)));
        Check($"text {at}: every BACK is the plate size", backs.Count == 1 && backs.Contains(plate), string.Join(",", backs));
        Check($"text {at}: the titles of Options and of the pages are one size", titles.Count == 1, string.Join(",", titles));
        _front.ShowMainMenu();
        await Frames(2);
    }
}
