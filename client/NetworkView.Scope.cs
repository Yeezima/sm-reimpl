using Godot;
using SmContent;
using SmSim;

namespace SmClient;

/// <summary>
/// The scope overlay in the networked view (feel/scope-lascannon; <see cref="ScopeView"/>, <see cref="ScopeArt"/>): while the player aims with the stalker bolter or the lascannon the
/// original HUD's scope sprites are drawn over the zoomed camera (fading in over the clip's 25 frames at 30 fps, the "active" variant while an enemy is under the crosshair), and the
/// normal crosshair is hidden. Client only; the camera (zoom field of view from the camera sets) is untouched. <c>--no-scope</c> restores the plain zoom.
/// </summary>
public partial class NetworkView
{
    ScopeView? _scopeView;
    double _scopeSince = -1;
    bool? _noScope;

    /// <summary><c>--no-scope</c> (test aid): no overlay, the zoom is a plain camera view as before.</summary>
    bool NoScope => _noScope ??= CmdFlag("no-scope");

    void SetUpScope(CanvasLayer layer)
    {
        if (NoScope || _bot && !AimTest) return;
        _scopeView = new ScopeView(_gameDir);
        layer.AddChild(_scopeView);
    }

    /// <summary>Each frame (from the HUD's data fill): shows or hides the overlay and tells the HUD to drop its crosshair.</summary>
    void UpdateScope(Hud.Frame f, PlayerState me)
    {
        if (_scopeView is null || _config is null) return;
        var kind = me.Alive && Spectate is null && !OwnDead && !PickerBusy ? ScopeArt.KindOf(Loadouts.InHand(_config, me)?.Name) : null;
        if (kind is null) { _scopeView.Hide(); _scopeSince = -1; return; }
        _scopeView.Prewarm(kind.Value);
        if (!me.Aiming) { _scopeView.Hide(); _scopeSince = -1; return; }
        if (_scopeSince < 0) _scopeSince = _connectedFor;
        int frame = (int)((_connectedFor - _scopeSince) * ScopeArt.FramesPerSecond);
        _scopeView.Show(kind.Value, f.AimedAtEnemy, frame, f.SpreadFraction);   // ui/hud-2: the scope's reticle opens with the scatter
        f.Scoped = _scopeView.Visible;   // the crosshair goes only once the overlay is on screen
    }
}
