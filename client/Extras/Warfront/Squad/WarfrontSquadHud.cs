using System;
using System.Collections.Generic;
using Godot;
using SmClient.Customizer;

namespace SmClient.Extras.Warfront.Squad;

/// <summary>
/// The squad in the field's HUD (slice W10): the line under the XP HUD ("SQUAD 7/9", "2 DOWN"; the jump-pound hint for an Assault or Raptor), the event feed ("Brother Castor is down"), and the revive prompt with its progress
/// ring. One text style (<see cref="UiKit.BodySize"/>, white, a dark outline), live labels only; the ring is drawn by arcs, crisp and symmetric. Positions are ours, set by the host.
/// </summary>
public sealed partial class WarfrontSquadHud : Control
{
    /// <summary>Lines kept on the feed.</summary>
    public const int FeedLines = 5;

    sealed partial class Ring : Control
    {
        public float Share;
        public const float Radius = 30f, Width = 5f;
        public Ring() { MouseFilter = MouseFilterEnum.Ignore; CustomMinimumSize = new Vector2(Radius * 2 + 12, Radius * 2 + 12); Size = CustomMinimumSize; }
        public override void _Draw()
        {
            var c = Size / 2f;
            DrawArc(c, Radius, 0f, Mathf.Tau, 64, new Color(0f, 0f, 0f, 0.55f), Width + 4f, true);
            DrawArc(c, Radius, 0f, Mathf.Tau, 64, new Color(1f, 1f, 1f, 0.28f), Width, true);
            if (Share > 0.001f) DrawArc(c, Radius, -Mathf.Pi / 2f, -Mathf.Pi / 2f + Mathf.Tau * Math.Clamp(Share, 0f, 1f), 64, UiKit.White, Width, true);
        }
    }

    readonly Label _line, _jump, _prompt;
    readonly VBoxContainer _feed;
    readonly Ring _ring = new() { Visible = false };
    readonly List<(Label Label, double Until)> _feedLabels = new();

    public string Line => _line.Text;
    public string Prompt => _prompt.Text;
    public bool PromptShown => _prompt.Visible;
    public bool RingShown => _ring.Visible;
    public float RingShare => _ring.Share;
    public IReadOnlyList<string> FeedText => _feedLabels.ConvertAll(f => f.Label.Text);
    public string JumpHint => _jump.Text;

    static Label Styled(string text = "", HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var l = UiKit.Text(text, UiKit.BodySize, UiKit.White, align);
        l.AddThemeConstantOverride("outline_size", 7);
        l.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.92f));
        return l;
    }

    public WarfrontSquadHud(float lineLeft = 28f, float lineTop = 132f)
    {
        Name = "WarfrontSquadHud";
        Theme = UiKit.Theme;
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        var column = UiKit.VBox(4);
        column.Position = new Vector2(lineLeft, lineTop);
        column.MouseFilter = MouseFilterEnum.Ignore;
        _line = Styled(); _line.Visible = false;
        _jump = Styled(); _jump.Visible = false;
        _feed = UiKit.VBox(2); _feed.MouseFilter = MouseFilterEnum.Ignore;
        column.AddChild(_line); column.AddChild(_jump); column.AddChild(_feed);
        AddChild(column);
        _prompt = Styled("", HorizontalAlignment.Center); _prompt.Visible = false;
        _prompt.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterBottom);
        _prompt.OffsetTop = -190; _prompt.OffsetBottom = -150; _prompt.OffsetLeft = -360; _prompt.OffsetRight = 360;
        AddChild(_prompt);
        AddChild(_ring);
    }

    /// <summary>The squad line; null hides it. <paramref name="alert"/> draws it in the alert colour (someone is down).</summary>
    public void SetLine(string? text, bool alert = false)
    {
        _line.Visible = text is not null;
        if (text is null) return;
        _line.Text = text;
        _line.AddThemeColorOverride("font_color", alert ? UiKit.LightRed : UiKit.White);
    }

    /// <summary>The jump-pound hint under the line ("[J] JUMP ASSAULT"); null hides it.</summary>
    public void SetJumpHint(string? text) { _jump.Visible = text is not null; if (text is not null) _jump.Text = text; }

    /// <summary>A feed line, shown <see cref="WarfrontSquadModel.FeedSeconds"/> s.</summary>
    public void AddFeed(string text, double now)
    {
        var label = Styled(text);
        _feed.AddChild(label);
        _feedLabels.Add((label, now + WarfrontSquadModel.FeedSeconds));
        while (_feedLabels.Count > FeedLines) { _feedLabels[0].Label.QueueFree(); _feedLabels.RemoveAt(0); }
    }

    /// <summary>The revive prompt and ring: <paramref name="ringAt"/> is the screen place of the Marine's head (null: no ring); null text hides both.</summary>
    public void SetPrompt(string? text, Vector2? ringAt, float share)
    {
        _prompt.Visible = text is not null;
        if (text is not null) _prompt.Text = text;
        _ring.Visible = text is not null && ringAt is not null;
        if (_ring.Visible)
        {
            _ring.Position = ringAt!.Value - _ring.Size / 2f;
            if (!Mathf.IsEqualApprox(_ring.Share, share)) { _ring.Share = share; _ring.QueueRedraw(); }
        }
    }

    /// <summary>Expires old feed lines.</summary>
    public void Tick(double now)
    {
        for (int i = _feedLabels.Count - 1; i >= 0; i--)
            if (now >= _feedLabels[i].Until) { _feedLabels[i].Label.QueueFree(); _feedLabels.RemoveAt(i); }
    }
}
