using Godot;
using SmClient.Customizer;
using SmClient.Extras.Warfront.Progression;
using SmNet.Extras.Warfront;

namespace SmClient.Extras.Warfront.Battle;

/// <summary>
/// The Battle section's HUD (slice W13b; plan section 3): under Musou's objective HUD one line of the position to hold and the wave clock ("HOLD THE KEEP  WAVE 2 OF 3 IN 0:40"), the survive countdown below it ("SURVIVE 6:12", the owner's
/// win: surviving 8 minutes) and a centre banner for four seconds when the server sends a message ("THE WALL HAS FALLEN. FALL BACK TO THE KEEP", "THE HQ HOLDS", "THE HQ HAS FALLEN"). In the 1920 by 1080 space like the other Warfront
/// panels; three text levels (body 20, heading 28, banner 44), every label live, no painted text. Shown only in an HQ or fortress battle (kind 1 or 2); a node battle draws nothing. Musou's own objective letters stay A, B, C in this draft
/// (W, K, V in order); a label seam is a W5c candidate.
/// </summary>
public sealed partial class WarfrontBattleHud : Control
{
    public const int BodySize = 20, HeadingSize = 28, BannerSize = 44;
    public const float LineTop = 104f, ClockTop = 138f, BannerTop = 250f, BannerSeconds = 4f;

    readonly Label _line, _clock, _banner;
    byte _lastMessage;
    double _bannerUntil;
    bool _have;

    public Label LineLabel => _line;
    public Label ClockLabel => _clock;
    public Label BannerLabel => _banner;

    public WarfrontBattleHud()
    {
        Name = "WarfrontBattleHud";
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        float w = 1100f, x = (UiKit.VirtualWidth - w) / 2f;
        _line = WarfrontProgressDraw.Text(this, "WarfrontBattleLine", x, LineTop, w, BodySize, UiKit.White, HorizontalAlignment.Center);
        _clock = WarfrontProgressDraw.Text(this, "WarfrontBattleClock", x, ClockTop, w, HeadingSize, WarfrontProgressDraw.Gold, HorizontalAlignment.Center);
        _banner = WarfrontProgressDraw.Text(this, "WarfrontBattleBanner", (UiKit.VirtualWidth - 1500f) / 2f, BannerTop, 1500f, BannerSize, WarfrontProgressDraw.Gold, HorizontalAlignment.Center);
        Visible = false;
        _banner.Visible = false;
    }

    /// <summary>The newest section (<paramref name="now"/>: seconds, any steady clock).</summary>
    public void Apply(in WarfrontBattleWire.Section s, double now)
    {
        _have = s.Kind != 0;
        Visible = _have;
        if (!_have) { _lastMessage = 0; return; }
        WarfrontProgressDraw.Set(_line, WarfrontBattleWire.Line(s));
        WarfrontProgressDraw.Set(_clock, WarfrontBattleWire.Countdown(s));
        if (s.Message != 0 && s.Message != _lastMessage)
        {
            WarfrontProgressDraw.Set(_banner, WarfrontBattleWire.MessageText(s.Message));
            _bannerUntil = now + BannerSeconds;
        }
        _lastMessage = s.Message;
        Frame(now);
    }

    /// <summary>Hides the banner when its four seconds are over.</summary>
    public void Frame(double now) => _banner.Visible = _have && now < _bannerUntil;

    /// <summary>The battle is over or left: nothing is drawn.</summary>
    public void Clear()
    {
        _have = false;
        _lastMessage = 0;
        Visible = false;
        _banner.Visible = false;
    }
}
