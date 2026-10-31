using System;
using Godot;
using SmClient.Customizer;

namespace SmClient.FrontEnd;

/// <summary>
/// The in-match Esc menu (ui/menu-rebuild, the owner's menu; ours): the main menu's title and rows over a slightly opaque dark overlay, the match running behind it, no background image.
/// RESUME closes it (as Esc or B do), LOADOUT opens the loadout picker, OPTIONS the options (controls included), LEAVE MATCH asks and leaves.
/// </summary>
public sealed partial class MatchMenu : UiKit.MenuPage
{
    public const float DimAlpha = 0.5f;
    readonly Action _resume;
    readonly Label _banner;
    readonly Button _resumeButton, _loadoutButton, _leaveButton;

    public MatchMenu(Action resume, Action loadout, Action options, Action? leave) : base("MatchMenu")
    {
        _resume = resume;
        var dim = new ColorRect { Name = "MatchDim", Color = new Color(0f, 0f, 0f, DimAlpha), MouseFilter = MouseFilterEnum.Stop };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);
        MoveChild(dim, 0);
        _resumeButton = UiKit.MenuButton("Resume", () => _resume());
        _loadoutButton = UiKit.MenuButton("Loadout", () => loadout());
        Rows.AddChild(_resumeButton);
        Rows.AddChild(_loadoutButton);
        Rows.AddChild(UiKit.MenuButton("Options", () => options()));
        _leaveButton = UiKit.MenuButton("Leave match", () => leave?.Invoke());
        _leaveButton.Visible = leave is not null;
        Rows.AddChild(_leaveButton);
        Rows.AddChild(UiKit.MenuGap());
        _banner = UiKit.MenuText("", UiKit.White, clip: false);
        _banner.Name = "MatchBanner";
        Rows.AddChild(_banner);
    }

    /// <summary>The respawn line (the picker's banner) under the rows.</summary>
    public string Banner { set => _banner.Text = value.Replace("\n", "   ").ToUpperInvariant(); }

    /// <summary>Shows the menu with the focus on the row a player most likely wants: RESUME, or LOADOUT while dead.</summary>
    public void Open(bool alive)
    {
        Visible = true;
        (alive ? _resumeButton : _loadoutButton).FocusLater();
    }

    public override void _Input(InputEvent e)
    {
        if (!IsVisibleInTree() || !PadHost.IsBack(e)) return;
        GetViewport().SetInputAsHandled();
        _resume();
    }
}
