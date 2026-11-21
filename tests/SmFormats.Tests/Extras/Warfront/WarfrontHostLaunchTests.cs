using SmContent;
using Xunit;

namespace SmFormats.Tests.Extras.Warfront;

/// <summary>Host panel on the Warfront profile (the mode, map and bot rows are left out): the hosted server's arguments are what they were before.</summary>
public class WarfrontHostLaunchTests
{
    [Theory]
    [InlineData(BotDifficulty.Easy)]
    [InlineData(BotDifficulty.Normal)]
    public void Warfront_host_launch_arguments_are_unchanged(BotDifficulty difficulty)
    {
        // What HostPanel.StartHosting builds on the Warfront profile: Annihilation's mode, this PC only, no bots, no map (the profile's own), no developer tools, no extra arguments.
        var options = new HostOptions(ServerLaunch.ModeAnnihilation, 27583, true, "GAME", false, SmSim.ContentProfiles.Name(SmSim.ContentProfiles.WarfrontProfile), 0, difficulty, null, null, false, null, null);
        Assert.Equal(["--port", "27583", "--mode", "annihilation", "--bind", "127.0.0.1", "--game-dir", "GAME", "--no-custom-emblems", "--content", "warfront"], ServerLaunch.Arguments(options));
        Assert.False(ServerLaunch.HostStartsDevTools(true, musou: true));
    }

    [Fact]
    public void Warfront_host_explainer_names_no_classic_field_as_a_choice()
    {
        var text = SmContent.Extras.Warfront.WarfrontLocal.HostExplainer;
        Assert.Contains("up to 3 friends", text);
        Assert.DoesNotContain("Annihilation", text);
    }
}
