using System.Text.RegularExpressions;
using Xunit;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// fx/bolt-smoke (owner 2026-11-15; Musou Mode): source pins for the client's kill gore, which the engine-free tests cannot reach: the Ork kill path plays each point effect with its smoke dropped after a bolt weapon's
/// kill (<c>smoke: !boltKill</c>), and that kill is a shot or a bolt blast read through <c>BoltSmoke.KillDropsSmoke</c> (so melee, charges, grenades and the autocannon keep their gore as the data has it).
/// </summary>
public class BoltSmokeKillPinTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("SmReimpl.slnx");
    }

    static string Source() => File.ReadAllText(Path.Combine(Root(), "client", "Extras", "Musou", "Gore", "NetworkView.OrkBlood.cs"));

    [Fact]
    public void The_kill_path_plays_each_point_effect_without_smoke_after_a_bolt_kill()
    {
        string src = Source();
        Assert.Matches(new Regex(@"bool boltKill = BoltKillNoSmoke\(d\);"), src);
        Assert.Matches(new Regex(@"foreach \(var p in data\.PointEffects\(kill\.Point\)\)[\s\S]{0,900}?OrkBloodPlay\([^;]*smoke: !boltKill, noMist: noMist\)"), src);
        Assert.Contains("bool noMist = boltKill || AutocannonKillNoMist(d);", src);
    }

    [Fact]
    public void A_bolt_kill_is_a_shot_or_a_bolt_blast_through_the_shared_rule_and_the_old_switch()
    {
        string src = Source();
        Assert.Matches(new Regex(@"BoltSmoke\.KillDropsSmoke\(OrkBloodGun\(MusouPlayerState\(d\.Killer\)\), d\.Cause is MusouDeathKind\.Shot or MusouDeathKind\.BoltBlast, OldBoltSmoke\)"), src);
    }

    [Fact]
    public void The_scene_aid_flags_are_read_once_not_every_frame()
    {
        string scene = File.ReadAllText(Path.Combine(Root(), "client", "NetworkView.BoltSmokeScene.cs"));
        Assert.Contains("static readonly string? BoltSceneGunName = CmdFlag(\"bolt-smoke-scene\")", scene);
        Assert.Contains("static readonly bool BoltSceneClassic = CmdFlag(\"bolt-smoke-classic\")", scene);
        Assert.DoesNotContain("=> CmdFlag(\"bolt-smoke-scene\")", scene);
    }
}
