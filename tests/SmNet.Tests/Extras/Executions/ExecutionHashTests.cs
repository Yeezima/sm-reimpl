using SmSim;
using SmSim.Extras.Executions;

namespace SmNet.Tests.Extras.Executions;

/// <summary>
/// Executions X3: the config hash. Faithful and Musou never move; the extended and Warfront hashes moved once (rules version 2 and the code's versions and wire order). The install's rows are
/// never hashed (lead 39, 2026-11-25: the server's rows are authoritative, so a client whose install lacks a clip still joins).
/// </summary>
public class ExecutionHashTests
{
    [Fact]
    public void FaithfulAndMusouHashesUnchanged()
    {
        Assert.Equal(0x6F2F6B2B1A7A41F0UL, new SimConfig().Hash());   // OrkSimTests' and MusouIsolationTests' default faithful pin
        var musou = new SimConfig { Extras = ContentExtras.Musou };
        Assert.Equal(musou.Hash(), (musou with { Executions = ExecutionTestKit.ClassicSet() }).Hash());   // rows on a config without the flag: nothing read
    }

    [Fact]
    public void ExtendedAndWarfrontHashMoveOnce()
    {
        // The new pins (MusouIsolationTests.DefaultExtendedHash, ConfigHashPinTests.ExtendedHash) are recorded there; here: the executions block alone moves them.
        var extended = new SimConfig { Extras = ContentExtras.All };
        var without = new SimConfig { Extras = ContentExtras.All & ~ContentExtras.Executions };
        Assert.NotEqual(extended.Hash(), without.Hash());
        Assert.Equal(2, ExecutionRules.Version);
        Assert.Equal(2, ExecutionChoice.RulesVersion);
    }

    [Fact]
    public void InstallRowsNeverChangeHash()
    {
        // The plan's RowsChangeHash, reversed by lead 39: two installs whose rows differ (one event time, a missing row) hash the same.
        var a = new SimConfig { Extras = ContentExtras.All, Executions = ExecutionTestKit.ClassicSet() };
        var b = a with { Executions = ExecutionRowSet.OfAll([ExecutionTestKit.Row(kill: 1.1f)]) };
        Assert.Equal(a.Hash(), b.Hash());
        Assert.Equal(a.Hash(), (a with { Executions = ExecutionRowSet.Empty }).Hash());
        // The rows' full content still tells installs apart (ExecutionRowSet.Write, for the tests and the catalogue).
        Assert.NotEqual(Content(a.Executions), Content(b.Executions));
    }

    [Fact]
    public void EmptyCatalogueHashesStable()
    {
        var c = new SimConfig { Extras = ContentExtras.All };
        Assert.Equal(c.Hash(), (c with { }).Hash());
        Assert.Equal(Content(ExecutionRowSet.Empty), Content(ExecutionRowSet.Empty));
    }

    [Fact]
    public void The_row_wire_order_is_append_only()
    {
        // Lead 39: the wire carries a row's place in ExecutionRows.All; a reorder or a removal would make an older client draw another move. Append new rows at the end, never reorder.
        string[] pinned =
        [
            "O1.chainsword", "O2.chainsword", "O3.chainsword", "O4.chainsword", "O5.power_axe", "O4.power_axe", "O3.power_axe", "O6.power_axe", "O7.thunder_hammer", "O3.thunder_hammer",
            "O3.combat_knife", "O8.combat_knife", "O9.chainsword", "O9.power_axe", "O9.thunder_hammer", "O9.combat_knife", "C1.chainsword", "C2.power_axe", "C3.thunder_hammer",
            "B1.chainsword", "B2.power_axe", "B3.thunder_hammer", "R1.chainsword", "R1.power_axe", "R1.thunder_hammer",
        ];
        var ids = ExecutionRows.All.Select(r => r.Id).ToArray();
        Assert.True(ids.Length >= pinned.Length);
        Assert.Equal(pinned, ids.Take(pinned.Length));
    }

    static byte[] Content(ExecutionRowSet set)
    {
        using var m = new MemoryStream();
        using (var w = new BinaryWriter(m)) set.Write(w);
        return m.ToArray();
    }
}
