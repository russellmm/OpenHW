using Avalonia;
using OpenHW.App.Settings;
using Xunit;

namespace OpenHW.App.Tests;

/// <summary>Where windows open: the owner's two 6144×3456 monitors, the secondary (DP-1) left of the primary (DP-2).</summary>
public sealed class WindowPlacesTests
{
    private static readonly PixelRect Secondary = new(0, 0, 6144, 3456), Primary = new(6144, 0, 6144, 3456);

    [Fact]
    public void ANewWindowIsCentredOnThePrimaryMonitor()
    {
        var corner = WindowPlaces.Centered(Primary, new PixelSize(1900, 2000));   // 760×800 at 250 %
        Assert.Equal(new PixelPoint(6144 + 2122, 728), corner);
        Assert.True(Primary.Contains(corner));
        Assert.Equal(new PixelPoint(6144, 0), WindowPlaces.Centered(Primary, new PixelSize(9000, 9000))); // too big: top-left
    }

    [Fact]
    public void ASavedPlaceCountsOnlyOnAMonitorThatIsThere()
    {
        Assert.True(WindowPlaces.IsOnScreen(new PixelPoint(100, 100), [Secondary, Primary]));
        Assert.True(WindowPlaces.IsOnScreen(new PixelPoint(7000, 500), [Secondary, Primary]));
        Assert.False(WindowPlaces.IsOnScreen(new PixelPoint(7000, 500), [Secondary]));     // the primary unplugged
        Assert.False(WindowPlaces.IsOnScreen(new PixelPoint(-2000, 100), [Secondary, Primary]));
    }

    [Fact]
    public void PlacesRoundTripAndABadFileIsEmpty()
    {
        var dir = Directory.CreateTempSubdirectory("openhw-places").FullName;
        var path = Path.Combine(dir, "window-places.json");
        File.WriteAllText(path, """{"settings":{"X":7000,"Y":300,"Width":null,"Height":null},"graph:x":{"X":10,"Y":20,"Width":500,"Height":300}}""");
        var places = WindowPlaces.Load(path);
        Assert.Equal(new WindowPlace(7000, 300), places["settings"]);
        Assert.Equal(500, places["graph:x"].Width);
        File.WriteAllText(path, "not json");
        Assert.Empty(WindowPlaces.Load(path));
        Assert.Empty(WindowPlaces.Load(Path.Combine(dir, "missing.json")));
        Directory.Delete(dir, true);
    }
}
