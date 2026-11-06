using System.Numerics;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;
using Xunit;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>The Ashfall concept pass's marker and capture-tracker rules (the owner, 2026-11-06).</summary>
public class TfwMarkerRulesTests
{
    static readonly Vector3 Point = new(100f, 100f, 0f);
    const float Radius = 10f;

    [Fact]
    public void LetterShowsWhenLookingTowardThePoint()
    {
        var feet = new Vector3(100f, 50f, 0f);
        var eye = feet + new Vector3(0f, -3f, 2f);
        Assert.Equal(1f, TfwMarkerRules.LetterAlpha(Point, Radius, eye, new Vector3(0f, 1f, 0f), feet), 3);
        // a little off the heading still shows; past the fade it is gone
        var off20 = new Vector3(MathF.Sin(20f * MathF.PI / 180f), MathF.Cos(20f * MathF.PI / 180f), 0f);
        Assert.Equal(1f, TfwMarkerRules.LetterAlpha(Point, Radius, eye, off20, feet), 3);
        var off45 = new Vector3(MathF.Sin(45f * MathF.PI / 180f), MathF.Cos(45f * MathF.PI / 180f), 0f);
        Assert.Equal(0f, TfwMarkerRules.LetterAlpha(Point, Radius, eye, off45, feet));
        Assert.Equal(0f, TfwMarkerRules.LetterAlpha(Point, Radius, eye, new Vector3(0f, -1f, 0f), feet));
        // looking up or down does not matter, only the heading
        Assert.Equal(1f, TfwMarkerRules.LetterAlpha(Point, Radius, eye, new Vector3(0f, 1f, -2f), feet), 3);
    }

    [Fact]
    public void LetterHidesOnAndNearThePoint()
    {
        var forward = new Vector3(0f, 1f, 0f);
        var inside = new Vector3(100f, 95f, 0f);
        Assert.Equal(0f, TfwMarkerRules.LetterAlpha(Point, Radius, inside - new Vector3(0f, 3f, 0f), forward, inside));
        var atEdge = new Vector3(100f, 90f, 0f);
        Assert.Equal(0f, TfwMarkerRules.LetterAlpha(Point, Radius, atEdge - new Vector3(0f, 3f, 0f), forward, atEdge));
        var halfway = new Vector3(100f, 90f - TfwMarkerRules.NearMargin / 2f, 0f);
        Assert.Equal(0.5f, TfwMarkerRules.LetterAlpha(Point, Radius, halfway - new Vector3(0f, 3f, 0f), forward, halfway), 3);
    }

    [Fact]
    public void LetterStandsMuchHigher() => Assert.True(TfwMarkerRules.LabelHeight >= 20f);

    [Fact]
    public void OnPositionFindsThePointUnderTheFeet()
    {
        var objectives = new[] { new TfwObjective(0, TfwPositionState.Securing, 0.4f, Point, Radius), new TfwObjective(1, TfwPositionState.Held, 0f, new Vector3(200f, 0f, 0f), 8f) };
        Assert.Equal(0, TfwMarkerRules.OnPosition(objectives, new Vector3(105f, 104f, 1f)));
        Assert.Equal(1, TfwMarkerRules.OnPosition(objectives, new Vector3(203f, 0f, 0f)));
        Assert.Equal(-1, TfwMarkerRules.OnPosition(objectives, new Vector3(150f, 50f, 0f)));
        Assert.Equal(-1, TfwMarkerRules.OnPosition(objectives, new Vector3(100f, 100f, TfwHudModel.ClearingReach + 1f)));   // on a bridge far above it
    }

    [Fact]
    public void TrackerSaysWhatIsHappening()
    {
        var securing = new TfwObjective(1, TfwPositionState.Securing, 0.456f, Point, Radius);
        Assert.Equal(0.456f, TfwMarkerRules.BarFill(securing), 3);
        Assert.Equal("SECURING B  45%", TfwMarkerRules.OnPointLine(securing, 1, 0));
        Assert.Equal(1f, TfwMarkerRules.BarFill(securing with { State = TfwPositionState.Secured }));
        Assert.Equal(1f, TfwMarkerRules.BarFill(securing with { State = TfwPositionState.Held }));
        Assert.Equal(0f, TfwMarkerRules.BarFill(securing with { State = TfwPositionState.Locked }));
        Assert.Equal("CLEARING A  45%  ·  12 ORKS", TfwMarkerRules.OnPointLine(securing with { State = TfwPositionState.Clearing }, 0, 12));
        Assert.Equal("HQ", TfwMarkerRules.TileName(WarRules.Positions));
    }
}
