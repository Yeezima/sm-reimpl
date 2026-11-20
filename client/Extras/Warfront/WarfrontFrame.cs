using Godot;
using SmClient.Customizer;
using SmClient.FrontEnd;

namespace SmClient.Extras.Warfront;

/// <summary>
/// The concept frame of Warfront's full-screen ship screens (war map, travel, results; owner, 2026-11-20): the same painted frame, stone, crest and title plate as OPTIONS and the WARFRONT page
/// (<see cref="ConceptPanelPage"/>), dimmed over the scene, with a <see cref="Stage"/> of <see cref="StageW"/> by <see cref="StageH"/> design pixels inside its dark inner area. The screen lays its content out in
/// the stage's pixels (the size table's own numbers) and the frame scales and places the stage with the frame, like the WARFRONT page. No rows of its own: the screen's controls live in the stage.
/// </summary>
public sealed partial class WarfrontFrame : ConceptPanelPage
{
    /// <summary>The stage in design pixels at 1920 x 1080 (the frame's inner dark area is about 1420 x 556 there).</summary>
    public const float StageW = 1400f, StageH = 556f;

    /// <summary>Where the screen's content goes.</summary>
    public Control Stage { get; } = new() { Name = "Stage", MouseFilter = MouseFilterEnum.Ignore };

    public WarfrontFrame(string name, string title, float dim = 0.72f) : base(name, "", title, 2, dim)
    {
        AddChild(Stage);
        Visible = true;
    }

    protected override void Place()
    {
        base.Place();
        if (Stage is null || PanelScale <= 0f) return;
        float sv = PanelScale, sv0 = Mathf.Min(1080f * 0.95f / (342f + PanelFrame.ExtraV2(2)), 1920f * 0.96f / 736f);
        float f = sv / sv0;
        float totalH = 342f + PanelFrame.ExtraV2(2);
        var o = new Vector2((Size.X - 736f * sv) / 2f, (Size.Y - totalH * sv) / 2f);
        // The inner dark area in the frame picture's pixels: x 79.4 to 659.4 (the rows' area), y from 116 (under the heading rail) to the tray at 262 plus the slices.
        float cx = o.X + (79.4f + 290f) * sv, top = o.Y + 116f * sv;
        Stage.Scale = new Vector2(f, f);
        Stage.Position = new Vector2(cx - StageW * f / 2f, top);
    }
}
