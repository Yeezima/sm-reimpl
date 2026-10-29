using SmContent;
using SmFormats.Baf;

namespace SmFormats.Tests;

/// <summary>
/// fx/impact-pass: the melee weapons' effects the client plays (<see cref="MeleeEffects"/>). Synthetic wargear files shaped like the install's (names and values invented):
/// a blast strike's upgrade entries with their radius, damage and plays; the while-deployed effects without the eye glow; the step-to-entry match.
/// </summary>
public class MeleeEffectsTests
{
    static (string, object)[] PlayFx(string name, string marker, float scale = 1f) =>
    [
        ("$ref", "action_commands\\play_fx"), ("fx_package", "test_fx"), ("fx_name", name), ("scale", scale), ("length", 1f), ("attach_to_marker", false),
        ("trigger_point", "self"), ("marker", new (string, object)[] { ("marker", marker) }), ("trigger_point_target", ""),
        ("marker_target", Array.Empty<(string, object)>()), ("rotation_source", "trigger_point"), ("trigger_direction", ""),
        ("scale_if_from_player", false), ("on_stop_policy", "stop_emitting"), ("lifetime", 0f), ("apply_only_if_on_player", false),
    ];

    static (string, object) Entry(string? upgrade, float radius, float damage, params (string, object)[] plays)
    {
        (string, object)[] aoe =
        [
            ("shape", new (string, object)[] { ("radius", radius) }),
            ("hit_actions", new (string, object)[] { ("commands", new (string, object)[] { ("do_damage", new (string, object)[] { ("damage_general", damage) }) }) }),
        ];
        (string, object)[] commands = [("area_of_effect", aoe), .. plays];
        return ("entry", new (string, object)[]
        {
            ("required_upgrade_name", new (string, object)[] { ("required_list", upgrade is null ? Array.Empty<(string, object)>() : new (string, object)[] { ("1", upgrade) }) }),
            ("action", new (string, object)[] { ("commands", commands) }),
        });
    }

    static BafDocument Sword() => BafDocument.Read(SyntheticBafWriter.Build(
        ("action_data", new (string, object)[]
        {
            ("triggered_actions", new (string, object)[]
            {
                ("displace_footstomp", new (string, object)[]
                {
                    ("commands", new (string, object)[]
                    {
                        ("actions_by_upgrade", new (string, object)[]
                        {
                            ("actions", new (string, object)[]
                            {
                                Entry(null, 7f, 25f, ("play_fx", PlayFx("test_stomp", "fx_l_foot"))),
                                Entry("test_furious", 10f, 30f, ("play_fx", PlayFx("test_stomp_furied", "fx_l_foot", 0.85f)), ("play_fx", PlayFx("test_stomp", "fx_l_foot"))),
                            }),
                        }),
                    }),
                }),
                ("displace_quickstomp", new (string, object)[]
                {
                    ("commands", new (string, object)[]
                    {
                        ("actions_by_upgrade", new (string, object)[] { ("actions", new (string, object)[] { Entry(null, 5f, 15f, ("play_fx", PlayFx("test_stomp", "fx_r_foot", 0.6f))) }) }),
                    }),
                }),
                ("displace_footstomp_coop", new (string, object)[]
                {
                    ("commands", new (string, object)[] { ("area_of_effect", new (string, object)[] { ("shape", new (string, object)[] { ("radius", 7f) }) }), ("play_fx", PlayFx("test_coop", "fx_l_foot")) }),
                }),
            }),
            ("while_deployed_actions", new (string, object)[]
            {
                ("$ref", "_base_templates\\action_entry"),
                ("commands", new (string, object)[] { ("play_fx", PlayFx("test_sword_idle", "fx_powersword_swing")), ("play_fx", PlayFx("test_eye", "fx_lefteye")) }),
            }),
        })));

    static MeleeEffects Data() => MeleeEffects.Build(new Dictionary<string, BafDocument> { ["test_sword"] = Sword() });

    [Fact]
    public void BlastStrikesAreReadWithTheirEntries()
    {
        var stomps = Data().Stomps("test_sword");
        Assert.Equal(["displace_footstomp", "displace_quickstomp"], stomps.Select(a => a.Action));   // the co-op copy is left out
        var foot = stomps[0].Entries;
        Assert.Equal(2, foot.Count);
        Assert.True(foot[0].IsDefault);
        Assert.Equal((7f, 25f), (foot[0].Radius, foot[0].Damage));
        Assert.Equal(["test_stomp"], foot[0].Plays.Select(p => p.Name));
        Assert.Equal("fx_l_foot", foot[0].Plays[0].Marker);
        Assert.Equal(["test_furious"], foot[1].RequiredUpgrades);
        Assert.Equal(["test_stomp_furied", "test_stomp"], foot[1].Plays.Select(p => p.Name));
    }

    [Fact]
    public void AStepsBlastFindsItsEntry()
    {
        var data = Data();
        Assert.Equal(25f, data.Match("test_sword", 7f, 25f)!.Damage);              // the foot stomp's default
        Assert.Equal(["test_furious"], data.Match("test_sword", 10f, 30f)!.RequiredUpgrades);   // Furious Stomp
        Assert.Equal(0.6f, data.Match("test_sword", 5f, 15f)!.Plays[0].Scale);     // the quick stomp
        Assert.Equal(25f, data.Match("test_sword", 7.2f, 99f)!.Damage);           // radius first
        Assert.Null(data.Match("no_such_wargear", 7f, 25f));
    }

    [Fact]
    public void DeployedEffectsLeaveTheEyeGlowOut()
    {
        var data = Data();
        Assert.Equal(["test_sword_idle"], data.Deployed("test_sword").Select(p => p.Name));
        Assert.Empty(data.Deployed("no_such_wargear"));
        Assert.Contains("test_stomp_furied", data.EffectNames);
        Assert.DoesNotContain("test_eye", data.EffectNames);
    }
}
