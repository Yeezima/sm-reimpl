using SmContent;
using SmFormats.Baf;

namespace SmFormats.Tests;

/// <summary>
/// M40-T13: the Chaos eye glow bindings (<see cref="EyeGlowData"/>). Synthetic wargear files shaped like the install's (names and values invented):
/// eye effects at <c>fx_lefteye</c> / <c>fx_righteye</c> in the while-deployed actions count; other markers, other events and upgrade-only entries do not.
/// </summary>
public class EyeGlowDataTests
{
    static (string, object)[] PlayFx(string name, string marker) =>
    [
        ("$ref", "action_commands\\play_fx"), ("fx_package", "test_fx"), ("fx_name", name), ("scale", 1f), ("length", 1f), ("attach_to_marker", true),
        ("trigger_point", "self"), ("marker", new (string, object)[] { ("marker", marker) }), ("trigger_point_target", ""),
        ("marker_target", Array.Empty<(string, object)>()), ("rotation_source", "trigger_point"), ("trigger_direction", "negative_hit_direction"),
        ("scale_if_from_player", false), ("on_stop_policy", "stop_emitting"), ("lifetime", 0f), ("apply_only_if_on_player", false),
    ];

    static BafDocument Weapon(string table, params (string, object)[] commands) => BafDocument.Read(SyntheticBafWriter.Build(
        ("common_data", new (string, object)[] { ("slot", "ranged") }),
        ("action_data", new (string, object)[] { (table, new (string, object)[] { ("$ref", "_base_templates\\action_entry"), ("commands", commands) }) })));

    static EyeGlowData Data() => EyeGlowData.Build(new Dictionary<string, BafDocument>
    {
        ["test_chaos_rifle"] = Weapon("while_deployed_actions",
            ("play_fx", PlayFx("test_ambient", "fx_muzzle")), ("play_fx", PlayFx("test_eye_rifle", "fx_righteye")), ("play_fx", PlayFx("test_eye_rifle", "fx_lefteye"))),
        ["test_chaos_hammer"] = Weapon("lifetime_actions", ("play_fx", PlayFx("test_eye_hammer", "FX_LeftEye"))),
        ["test_stow_only"] = Weapon("on_stow_actions", ("play_fx", PlayFx("test_eye_stow", "fx_lefteye"))),
        ["test_marine_rifle"] = Weapon("while_deployed_actions", ("play_fx", PlayFx("test_ambient", "fx_muzzle"))),
    });

    [Fact]
    public void WhileDeployedEyeEffectsAreKeptPerWargear()
    {
        var data = Data();
        var rifle = data.For("test_chaos_rifle");
        Assert.Equal(2, rifle.Count);
        Assert.All(rifle, p => Assert.Equal("test_eye_rifle", p.Name));
        Assert.Equal(["fx_lefteye", "fx_righteye"], rifle.Select(p => p.Marker).Order(StringComparer.Ordinal));
        Assert.All(rifle, p => Assert.True(p.Attached));
    }

    [Fact]
    public void LifetimeActionsCountAndMarkerNamesIgnoreCase()
    {
        var hammer = Data().For("TEST_CHAOS_HAMMER");
        Assert.Single(hammer);
        Assert.Equal("test_eye_hammer", hammer[0].Name);
    }

    [Fact]
    public void OtherEventsMarkersAndUnknownWargearGiveNoGlow()
    {
        var data = Data();
        Assert.Empty(data.For("test_stow_only"));      // on_stow_actions: not the deployed state
        Assert.Empty(data.For("test_marine_rifle"));   // a muzzle effect only
        Assert.Empty(data.For("no_such_wargear"));
        Assert.Empty(data.For(null));
        Assert.Equal(["test_chaos_hammer", "test_chaos_rifle"], data.Wargear.Order(StringComparer.Ordinal));
    }
}
