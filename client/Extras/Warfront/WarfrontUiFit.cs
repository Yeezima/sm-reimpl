using Godot;

namespace SmClient.Extras.Warfront;

/// <summary>
/// The root every Warfront panel sits in (slice W5c, second pass): a 1920 by 1080 design space scaled to fit the window (scale = min(height / 1080, width / 1920)) and centred in it, so a panel laid out in the
/// hub's 1920 by 1080 pixels is fully visible at 1280x720 or 1600x900 and sits centred, not small in a corner, above 1080 (the held-M war map did this alone; now one place does it for all). Mouse input passes
/// through the root itself; the panels inside take what they take. Refits itself every frame, so a resized window follows.
/// </summary>
public sealed partial class WarfrontUiFit : Control
{
    public const float DesignWidth = 1920f, DesignHeight = 1080f;

    /// <summary>The scale of the design space in a window of this size (1 for a size that is not positive).</summary>
    public static float ScaleFor(Vector2 viewport) => viewport.X > 0f && viewport.Y > 0f ? Mathf.Min(viewport.Y / DesignHeight, viewport.X / DesignWidth) : 1f;

    /// <summary>Where the design space's top left falls in a window of this size, with <see cref="ScaleFor"/>.</summary>
    public static Vector2 OriginFor(Vector2 viewport) => (viewport - new Vector2(DesignWidth, DesignHeight) * ScaleFor(viewport)) * 0.5f;

    public WarfrontUiFit()
    {
        Name = "WarfrontUiFit";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.TopLeft);
        Size = new Vector2(DesignWidth, DesignHeight);
        PivotOffset = Vector2.Zero;
    }

    /// <summary>Sets the scale, size and position of this root for a window of this size.</summary>
    public void Fit(Vector2 viewport)
    {
        float s = ScaleFor(viewport);
        Scale = new Vector2(s, s);
        Size = new Vector2(DesignWidth, DesignHeight);
        Position = OriginFor(viewport);
    }

    public override void _Ready() => Fit(GetViewport().GetVisibleRect().Size);

    public override void _Process(double delta) => Fit(GetViewport().GetVisibleRect().Size);
}
