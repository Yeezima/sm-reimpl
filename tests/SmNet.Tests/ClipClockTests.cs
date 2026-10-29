using SmNet;

namespace SmNet.Tests;

/// <summary>
/// The remote clip clock (client presentation, ours): clip time runs at the frame rate between snapshots whose value steps at
/// the snapshot rate, re-syncs on new values, and restarts when the clip changes. Synthetic times only.
/// </summary>
public class ClipClockTests
{
    const float Frame = 1f / 60f, Snapshot = 0.05f;   // 60 fps frames, 20 Hz snapshots

    /// <summary>The value the interpolated view carries: the clip time at the last snapshot instant (a step function).</summary>
    static float Stepped(float clipTime) => MathF.Floor(clipTime / Snapshot) * Snapshot;

    [Fact]
    public void SteppedSnapshotValueIsPlayedSmoothly()
    {
        var clock = new ClipClock();
        long key = ClipClock.Key(1, 2, 3);
        float previous = -1f, maxStep = 0f; int still = 0, frames = 0;
        float stepped = 0f, maxRawStep = 0f; int rawStill = 0;
        for (int i = 0; i < 120; i++)
        {
            float truth = i * Frame;
            float raw = Stepped(truth);
            float shown = clock.Step(key, raw, i == 0 ? 0f : Frame, 10f);
            if (i > 0)
            {
                frames++;
                maxStep = MathF.Max(maxStep, MathF.Abs(shown - previous));
                if (MathF.Abs(shown - previous) < 1e-6f) still++;
                maxRawStep = MathF.Max(maxRawStep, MathF.Abs(raw - stepped));
                if (raw == stepped) rawStill++;
            }
            previous = shown; stepped = raw;
            Assert.InRange(shown, truth - 0.06f, truth + ClipClock.Ahead + 1e-4f);   // close to the true time throughout
        }
        Assert.True(rawStill > frames / 2, "the raw value stands still between snapshots");
        Assert.Equal(0, still);                                       // the smoothed time moves every frame
        Assert.InRange(maxStep, Frame * 0.5f, Frame * 1.6f);          // and never jumps more than about a frame's worth
        Assert.True(maxRawStep >= Snapshot - 1e-4f);                  // while the raw value jumped a whole snapshot interval
    }

    [Fact]
    public void ChangedClipRestartsAtTheSnapshotValue()
    {
        var clock = new ClipClock();
        clock.Step(ClipClock.Key(1, 1), 0.3f, Frame, 5f);
        clock.Step(ClipClock.Key(1, 1), 0.3f, Frame, 5f);
        Assert.Equal(0.1f, clock.Step(ClipClock.Key(2, 1), 0.1f, Frame, 5f));   // another kind
        Assert.Equal(0.0f, clock.Step(ClipClock.Key(2, 2), 0.0f, Frame, 5f));   // another phase
    }

    [Fact]
    public void SameClipBeginningAgainRestarts()
    {
        var clock = new ClipClock();
        long key = ClipClock.Key(3, 1);
        for (int i = 0; i < 10; i++) clock.Step(key, 0.4f + i * Snapshot, Frame, 5f);
        Assert.Equal(0.05f, clock.Step(key, 0.05f, Frame, 5f), 5);   // the value went back: a new attack of the same step
    }

    [Fact]
    public void StopThenStepStartsAfresh()
    {
        var clock = new ClipClock();
        long key = ClipClock.Key(1);
        clock.Step(key, 0.5f, Frame, 5f);
        clock.Stop();
        Assert.False(clock.Active);
        Assert.Equal(0.2f, clock.Step(key, 0.2f, Frame, 5f));
        Assert.True(clock.Active);
    }

    [Fact]
    public void FarOffSnapshotValueSnapsAndNearOneEases()
    {
        var clock = new ClipClock();
        long key = ClipClock.Key(1);
        clock.Step(key, 0.1f, 0f, 5f);
        Assert.Equal(0.6f, clock.Step(key, 0.6f, Frame, 5f));                 // 0.5 s off: replaced
        float shown = clock.Step(key, 0.65f, Frame, 5f);                      // 0.6 + 1/60 = 0.6167 played, snapshot says 0.65
        Assert.InRange(shown, 0.6167f, 0.65f);
    }

    [Fact]
    public void StalledSnapshotsDoNotRunMoreThanAheadPastTheLastValue()
    {
        var clock = new ClipClock();
        long key = ClipClock.Key(1);
        clock.Step(key, 0.2f, 0f, 5f);
        float shown = 0f;
        for (int i = 0; i < 120; i++) shown = clock.Step(key, 0.2f, Frame, 5f);   // two seconds without a new value
        Assert.Equal(0.2f + ClipClock.Ahead, shown, 5);
    }

    [Fact]
    public void TimeNeverPassesTheClipLengthOrGoesNegative()
    {
        var clock = new ClipClock();
        long key = ClipClock.Key(1);
        Assert.Equal(0.95f, clock.Step(key, 0.95f, 0f, 1f), 5);    // restart at the snapshot value, inside the clip
        for (int i = 0; i < 30; i++) Assert.True(clock.Step(key, 0.95f + (i % 2) * 0.05f, Frame, 1f) <= 1f);
        Assert.Equal(0f, new ClipClock().Step(key, -0.3f, Frame, 1f));
    }
}
