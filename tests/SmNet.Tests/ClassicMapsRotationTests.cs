using SmContent;
using SmNet;
using SmServer;
using SmSim;
using Xunit;

namespace SmNet.Tests;

/// <summary>classic-maps-all (protocol 79): the rotation's wire bit, its server flag, the command line and the lobby's map choices. Synthetic data only.</summary>
public sealed class ClassicMapsRotationTests
{
    static Snapshot WithMatch(MatchView m) => new(1, 1, 1, [], Match: m);

    [Fact]
    public void Protocol_is_80_and_the_next_map_bit_travels_with_the_ended_match()
    {
        Assert.Equal(82, Protocol.Version);
        var ended = new MatchView(MatchState.Ended, Team.Chaos, 12, 41, 7, GameMode.SeizeGround, NextMap: true);
        var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(WithMatch(ended))));
        Assert.Equal(ended, back.Match);
        var plain = new MatchView(MatchState.Ended, Team.SpaceMarines, 5, 41, 7, GameMode.Annihilation);
        Assert.False(Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(WithMatch(plain)))).Match!.Value.NextMap);
    }

    [Fact]
    public void Maps_and_the_internal_child_flag_parse()
    {
        Assert.True(ServerCommandLine.TryParse(["--maps", "mp_tdm_17, mp_tdm_01", "--mode", "seize"], out var cli, out var error), error);
        Assert.Equal(["mp_tdm_17", "mp_tdm_01"], cli.Maps);
        Assert.False(cli.RotationChild);
        Assert.True(ServerCommandLine.TryParse(["--map", "mp_tdm_17", "--rotation-child"], out var child, out error), error);
        Assert.True(child.RotationChild);
        Assert.Null(child.Maps);
        Assert.False(ServerCommandLine.TryParse(["--maps"], out _, out _));
    }

    [Fact]
    public void The_child_gets_one_map_and_the_other_arguments_unchanged()
    {
        var args = new[] { "--bind", "127.0.0.1", "--maps", "all", "--port", "27300", "--mode", "seize" };
        Assert.Equal(["--bind", "127.0.0.1", "--port", "27300", "--mode", "seize", "--map", "mp_tdm_11", "--rotation-child"], RotationSupervisor.ChildArguments(args, "mp_tdm_11"));
    }

    static MapEntry Entry(string name, string display, bool playable, params string[] types) => new(name, display, types, playable);

    [Fact]
    public void The_lobby_lists_the_default_map_first_the_rest_by_name_and_a_rotation_last_without_unplayable_maps()
    {
        var catalog = new[]
        {
            Entry("mp_tdm_17", "Shattered Bridge", true, "teamdeathmatch", "domination"),
            Entry("mp_dlc3_map2", "dlc3_map2", false, "teamdeathmatch", "domination"),
            Entry("mp_tdm_09", "Manufactorum", true, "teamdeathmatch", "domination"),
            Entry("mp_tdm_01", "Waste Management", true, "teamdeathmatch", "domination"),
            Entry("cp_arena", "Arena", true, "coop"),
        };
        var choices = HostMapChoices.For(catalog);
        Assert.Equal(["mp_tdm_09", "mp_tdm_17", "mp_tdm_01", HostMapChoices.Rotation], choices.Select(c => c.Value));
        Assert.Equal("MANUFACTORUM", choices[0].Label);
        Assert.Single(HostMapChoices.For([catalog[2]]));   // a single map: no rotation choice
    }

    [Fact]
    public void The_host_arguments_name_a_map_or_a_rotation_and_the_default_names_neither()
    {
        Assert.Equal((null, null), HostMapChoices.ServerOptions(HostMapChoices.DefaultMap));
        Assert.Equal(("mp_tdm_17", null), HostMapChoices.ServerOptions("mp_tdm_17"));
        Assert.Equal((null, "all"), HostMapChoices.ServerOptions(HostMapChoices.Rotation));
        var baseOptions = new HostOptions(ServerLaunch.ModeSeize, 27300, true, "", Bots: 0);
        var map = ServerLaunch.Arguments(baseOptions with { Map = "mp_tdm_18" });
        Assert.Equal("mp_tdm_18", map[map.IndexOf("--map") + 1]);
        Assert.DoesNotContain("--maps", map);
        var rotation = ServerLaunch.Arguments(baseOptions with { Maps = "all" });
        Assert.Equal("all", rotation[rotation.IndexOf("--maps") + 1]);
        Assert.DoesNotContain("--map", rotation);
    }

    [Fact]
    public void The_mode_support_of_an_entry_follows_its_game_types()
    {
        var onlyKills = Entry("mp_x", "X", true, "teamdeathmatch");
        Assert.True(onlyKills.SupportsAnnihilation);
        Assert.False(onlyKills.SupportsSeize);
    }

    static ServerArguments Args(string mode, params string[] maps) =>
        ServerCommandLine.TryParse(["--maps", string.Join(",", maps), "--mode", mode], out var cli, out var error) ? cli : throw new InvalidOperationException(error);

    static readonly MapEntry[] Roster =
    [
        Entry("mp_tdm_17", "Shattered Bridge", true, "teamdeathmatch", "domination"),
        Entry("mp_tdm_01", "Waste Management", true, "teamdeathmatch", "domination"),
        Entry("mp_kills", "Kills Only", true, "teamdeathmatch"),
        Entry("mp_dlc3_map2", "dlc3_map2", false, "teamdeathmatch", "domination"),
    ];

    [Fact]
    public void A_rotation_plan_keeps_the_order_drops_repeats_and_all_takes_the_maps_that_can_be_played_in_the_mode()
    {
        Assert.True(RotationSupervisor.TryPlan(Roster, Args("seize", "mp_tdm_01", "mp_tdm_17", "mp_tdm_01"), out var maps, out var error), error);
        Assert.Equal(["mp_tdm_01", "mp_tdm_17"], maps);
        Assert.True(RotationSupervisor.TryPlan(Roster, Args("annihilation", "all"), out maps, out error), error);
        Assert.Equal(["mp_tdm_17", "mp_tdm_01", "mp_kills"], maps);   // playable only; the stub is left out
        Assert.True(RotationSupervisor.TryPlan(Roster, Args("seize", "all"), out maps, out error), error);
        Assert.Equal(["mp_tdm_17", "mp_tdm_01"], maps);               // the kills-only map has no Seize Ground
    }

    [Fact]
    public void A_rotation_plan_refuses_an_unknown_unplayable_or_wrong_mode_map_with_a_clear_error()
    {
        Assert.False(RotationSupervisor.TryPlan(Roster, Args("annihilation", "mp_nope"), out _, out var error));
        Assert.Contains("not a competitive map", error);
        Assert.False(RotationSupervisor.TryPlan(Roster, Args("annihilation", "mp_dlc3_map2"), out _, out error));
        Assert.Contains("cannot be played", error);
        Assert.False(RotationSupervisor.TryPlan(Roster, Args("seize", "mp_tdm_17", "mp_kills"), out _, out error));
        Assert.Contains("does not offer Seize Ground", error);
        Assert.False(RotationSupervisor.TryPlan([], Args("annihilation", "all"), out _, out error));
    }
}
