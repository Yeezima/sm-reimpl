using SmContent;
using SmContent.Extras.LoyalistBarks;
using SmSim;
using Xunit;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// musou/hotkey-lines (owner, 2026-11-10): in Musou Mode the T key says FOR THE EMPEROR and the owner's six kept campaign lines in turn, the slot a function of the shout sequence alone; the
/// automatic Marine barks are off unless restored; Classic PvP is not touched (engine-free; the client's two gates pinned by their source).
/// </summary>
public class MusouKeyLinesTests
{
    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("SmReimpl.slnx not found");
    }

    [Fact]
    public void The_pool_is_FOR_THE_EMPEROR_then_the_owners_six_kept_lines_in_the_sheets_order()
    {
        var pool = MusouKeyLines.Pool;
        Assert.Equal(7, pool.Count);
        Assert.True(pool[0].Emperor);
        Assert.Equal("FOR THE EMPEROR!", pool[0].Text);
        Assert.Equal(new uint[] { 0x0616864e, 0x0837844f, 0x0cda2bd9, 0x1ae56979, 0x1f2fe75d, 0x1c832a04 }, pool.Skip(1).Select(l => l.Node));
        Assert.Equal(new uint[] { 202735440, 202735438, 202735430, 202735428, 202735475, 202735038 }, pool.Skip(1).Select(l => l.Wem));
        Assert.Equal(new[] { 2, 3, 4, 5, 6, 8 }, pool.Skip(1).Select(l => l.SheetRow));
        Assert.Equal(new[] { "titus", "titus", "titus", "titus", "titus", "sidonus" }, pool.Skip(1).Select(l => l.Character));
        Assert.All(pool.Skip(1), l => Assert.False(l.Emperor));
        Assert.Equal(7, pool.Select(l => l.Node).Distinct().Count());
        // The campaign lines' level offsets (ours: level with the take): the quietest decoded line gets the most, Sidonus's shout the least, all within the sound system's +24 dB voice cap.
        Assert.Equal(new[] { 0f, 20f, 19f, 13f, 14f, 15f, 12f }, pool.Select(l => l.GainDb));
        Assert.All(pool, l => Assert.InRange(l.GainDb, 0f, 24f));
        string hook = File.ReadAllText(Path.Combine(RepoRoot(), "client", "Extras", "Musou", "NetworkView.MusouKeyLines.cs"));
        Assert.Contains("float gain = (MusouKeyDataLevel ? 0f : line.GainDb) + MusouKeyGainDb;", hook);
        Assert.Contains("new VoiceOffset(gain)", hook);
    }

    [Fact]
    public void Each_press_says_the_next_line_in_order_and_none_repeats_before_all_seven_have_played()
    {
        // The server's shout sequence: 1 for a player's first accepted press.
        Assert.Equal(Enumerable.Range(0, 7), Enumerable.Range(1, 7).Select(n => MusouKeyLines.SlotOf((byte)n)));
        Assert.True(MusouKeyLines.LineOf(1).Emperor);
        Assert.Equal("Purge the xenos!", MusouKeyLines.LineOf(2).Text);
        Assert.Equal("AHHH!", MusouKeyLines.LineOf(7).Text);
        Assert.True(MusouKeyLines.LineOf(8).Emperor);   // the pool starts again
        // Every full turn before the wrap holds all seven, once each.
        for (int start = 1; start + 6 <= 252; start += 7)
            Assert.Equal(7, Enumerable.Range(start, 7).Select(n => MusouKeyLines.SlotOf((byte)n)).Distinct().Count());
        // Two presses in a row never say the same line, across the wrap too.
        for (int n = 1; n <= 600; n++)
            Assert.NotEqual(MusouKeyLines.SlotOf(unchecked((byte)(n - 1))), MusouKeyLines.SlotOf(unchecked((byte)n)));
    }

    [Fact]
    public void The_u8_wrap_starts_the_pool_again_after_four_lines_once_per_256_presses()
    {
        // The documented exception (MusouKeyLines.SlotOf): 256 is no multiple of 7.
        Assert.Equal(new[] { 6, 0, 1, 2, 3, 0, 1 }, new byte[] { 252, 253, 254, 255, 0, 1, 2 }.Select(MusouKeyLines.SlotOf));
        Assert.NotEqual(0, 256 % MusouKeyLines.Pool.Count);
    }

    [Fact]
    public void The_FOR_THE_EMPEROR_slot_still_steps_through_the_four_takes_never_the_same_twice_in_a_row()
    {
        var pack = LoyalistBarkPack.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "client", "Extras", "LoyalistBarks", "assets", "loyalist-barks.json")));
        var takes = LoyalistBarkRules.Candidates(pack, BarkMoment.Key);
        Assert.Equal(4, takes.Count);
        foreach (int player in new[] { 1, 7, 15 })
        {
            var emperor = Enumerable.Range(1, 252).Where(n => MusouKeyLines.LineOf((byte)n).Emperor).Select(n => LoyalistBarkRules.KeyTake(player, (byte)n, takes).Take).ToList();
            Assert.Equal(36, emperor.Count);
            Assert.Equal(4, emperor.Distinct().Count());
            for (int i = 1; i < emperor.Count; i++) Assert.NotEqual(emperor[i - 1], emperor[i]);
        }
    }

    [Fact]
    public void Only_Musou_Mode_takes_the_rotation_Classic_PvP_keeps_FOR_THE_EMPEROR_alone()
    {
        Assert.True(MusouKeyLines.Applies(ContentExtras.Musou));
        Assert.True(MusouKeyLines.Applies(ContentExtras.Musou | ContentExtras.LoyalistBarks | ContentExtras.Orks));
        Assert.False(MusouKeyLines.Applies(ContentExtras.None));
        Assert.False(MusouKeyLines.Applies(ContentExtras.LoyalistBarks));
        Assert.False(MusouKeyLines.Applies(ContentExtras.LoyalistBarks | ContentExtras.Orks));
        // The client's gate: the rotation hook returns before anything outside Musou Mode, and the loyalist key calls it for the key alone.
        string hook = File.ReadAllText(Path.Combine(RepoRoot(), "client", "Extras", "Musou", "NetworkView.MusouKeyLines.cs"));
        Assert.Contains("if (!MusouActive || NoMusouKeyLines || _sound is null) return false;", hook);
        string key = File.ReadAllText(Path.Combine(RepoRoot(), "client", "Extras", "LoyalistBarks", "NetworkView.LoyalistBarks.cs"));
        Assert.Contains("if (moment == BarkMoment.Key && MusouKeyLine(id, (byte)seed, own, who)) return;", key);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(key, @"MusouKeyLine\("));
    }

    [Fact]
    public void The_automatic_Marine_barks_are_off_unless_restored()
    {
        Assert.False(MusouKeyLines.AutoBarks([]));
        Assert.False(MusouKeyLines.AutoBarks(["--connect", "127.0.0.1:27100", "--content", "musou"]));
        Assert.True(MusouKeyLines.AutoBarks([MusouKeyLines.AutoBarksFlag]));
        Assert.False(MusouKeyLines.AutoBarks([MusouKeyLines.AutoBarksFlag, "--no-musou-marine-barks"]));
        Assert.Equal("--musou-auto-barks", MusouKeyLines.AutoBarksFlag);
        // The client's gate: the bark frame returns at once without the switch.
        string barks = File.ReadAllText(Path.Combine(RepoRoot(), "client", "Extras", "Musou", "NetworkView.MusouMarineBarks.cs"));
        Assert.Contains("static readonly bool MusouAutoBarks = MusouKeyLines.AutoBarks(OS.GetCmdlineUserArgs());", barks);
        Assert.Contains("if (!MusouAutoBarks || _sound is null", barks);
    }
}
