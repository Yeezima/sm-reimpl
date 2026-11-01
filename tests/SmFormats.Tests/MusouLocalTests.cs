using SmContent;
using Xunit;

namespace SmFormats.Tests;

/// <summary>fix/musou-local-server: what MUSOU MODE does on each press, and when STOP MUSOU SERVER shows.</summary>
public class MusouLocalTests
{
    [Fact]
    public void A_press_starts_once_waits_while_starting_and_joins_when_ready()
    {
        Assert.Equal(MusouPress.NoServer, MusouLocal.OnPress(hasServerExe: false, running: false, ready: false, musouProfile: false));
        Assert.Equal(MusouPress.Start, MusouLocal.OnPress(true, running: false, ready: false, musouProfile: false));
        Assert.Equal(MusouPress.Wait, MusouLocal.OnPress(true, running: true, ready: false, musouProfile: true));   // never a second server, never an early join
        Assert.Equal(MusouPress.Join, MusouLocal.OnPress(true, running: true, ready: true, musouProfile: true));
        Assert.Equal(MusouPress.OtherServer, MusouLocal.OnPress(true, running: true, ready: true, musouProfile: false));
        Assert.Equal(MusouPress.OtherServer, MusouLocal.OnPress(true, running: true, ready: false, musouProfile: false));
    }

    [Fact]
    public void The_stop_row_shows_only_while_a_musou_server_runs()
    {
        Assert.False(MusouLocal.ShowStopRow(running: false, musouProfile: true));    // it died or was stopped
        Assert.False(MusouLocal.ShowStopRow(running: false, musouProfile: false));
        Assert.False(MusouLocal.ShowStopRow(running: true, musouProfile: false));    // the Host panel's other profile
        Assert.True(MusouLocal.ShowStopRow(running: true, musouProfile: true));      // starting or ready
    }
}
