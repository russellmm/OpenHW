using OpenHW.Hardware;
using Xunit;

namespace OpenHW.Tests;

public sealed class PauseTrackerTests
{
    // One poll as the hub runs it: returns what statistics would record and which ids stay frozen.
    private static (Dictionary<string, double?> Recorded, IReadOnlySet<string> Held) Poll(PauseTracker tracker, Dictionary<string, double?> engineOutput)
    {
        var frozen = tracker.BeginPoll();
        var readings = new Dictionary<string, double?>(engineOutput.Where(kv => !frozen.Contains(kv.Key)));
        var held = tracker.EndPoll(readings, frozen);
        return (readings, held);
    }

    [Fact]
    public void CollapsedSensorsAreFrozenAndPinnedOnesStayLive()
    {
        var tracker = new PauseTracker();
        tracker.SetPaused(["gpu:temp", "gpu:pcie"]);
        tracker.SetPinned(["gpu:temp"]);

        var frozen = tracker.BeginPoll();

        Assert.Contains("gpu:pcie", frozen);
        Assert.DoesNotContain("gpu:temp", frozen);
        Assert.True(tracker.IsPaused("gpu:pcie"));
        Assert.False(tracker.IsPaused("gpu:temp"));
    }

    [Fact]
    public void ResumedRateSensorStaysFrozenUntilItHasAValue()
    {
        var tracker = new PauseTracker();
        tracker.SetPaused(["net:rate"]);
        Poll(tracker, new() { ["net:rate"] = 5 });

        tracker.SetPaused([]); // expanded
        // First poll after resume: the rate has no baseline yet, so no value.
        var (recorded, held) = Poll(tracker, new() { ["net:rate"] = null });
        Assert.Contains("net:rate", held);               // statistics keep the old value...
        Assert.DoesNotContain("net:rate", recorded.Keys); // ...instead of recording a blank
        Assert.True(tracker.IsPaused("net:rate"));       // and the row stays dimmed

        (recorded, held) = Poll(tracker, new() { ["net:rate"] = 42 });
        Assert.Equal(42, recorded["net:rate"]);
        Assert.DoesNotContain("net:rate", held);
        Assert.False(tracker.IsPaused("net:rate"));
    }

    [Fact]
    public void ExpandedSensorStaysFrozenUntilAPollReadsIt()
    {
        var tracker = new PauseTracker();
        tracker.SetPaused(["x"]);
        Poll(tracker, new() { ["x"] = 1 });

        tracker.SetPaused([]); // expanded, but no poll has read it yet
        Assert.True(tracker.IsPaused("x"));

        Poll(tracker, new() { ["x"] = 2 });
        Assert.False(tracker.IsPaused("x"));
    }

    [Fact]
    public void SensorWithNoValueIsEventuallyShownAsSuch()
    {
        var tracker = new PauseTracker();
        tracker.SetPaused(["gone"]);
        Poll(tracker, new() { ["gone"] = 1 });
        tracker.SetPaused([]);

        for (var i = 0; i < PauseTracker.ResumeAttempts - 1; i++) Poll(tracker, new() { ["gone"] = null });
        var (recorded, held) = Poll(tracker, new() { ["gone"] = null });

        Assert.True(recorded.ContainsKey("gone")); // recorded as "no value" rather than frozen forever
        Assert.Null(recorded["gone"]);
        Assert.DoesNotContain("gone", held);
        Assert.False(tracker.IsPaused("gone"));
    }

    [Fact]
    public void CollapsingAgainCancelsResume()
    {
        var tracker = new PauseTracker();
        tracker.SetPaused(["x"]);
        Poll(tracker, new() { ["x"] = 1 });
        tracker.SetPaused([]);
        Poll(tracker, new() { ["x"] = null }); // resuming
        tracker.SetPaused(["x"]);              // collapsed again before a value arrived

        var (_, held) = Poll(tracker, new() { ["x"] = null });
        Assert.Contains("x", held);
        Assert.True(tracker.IsPaused("x"));
    }

    [Fact]
    public void LiveSensorsWithoutValuesAreNotHeld()
    {
        var tracker = new PauseTracker();
        var (recorded, held) = Poll(tracker, new() { ["unplugged"] = null });
        Assert.Null(recorded["unplugged"]);
        Assert.Empty(held);
    }
}
