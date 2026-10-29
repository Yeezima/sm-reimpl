using System.Buffers.Binary;
using SmContent;
using SmFormats.Audio;

namespace SmFormats.Tests;

/// <summary>
/// sound/impacts review: pins, on the player's install (read-only, no media; build 7412325), what reading an unset state group as Wwise's "None"
/// (<see cref="AudioCatalog.NoneState"/>) changes, which events read the two state groups, and which hit-confirm events reset the death state. Only IDs are
/// compared, no original bytes. Skipped when no install is found (CI).
/// </summary>
public class StateSwitchInstallPinTests
{
    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (the pin needs the player's install; never in CI)";
        }
    }

    static bool Reaches(EventChain chain, uint id)
    {
        var visited = new HashSet<ChainNode>(ReferenceEqualityComparer.Instance);
        bool Visit(ChainNode n)
        {
            if (!visited.Add(n)) return false;
            if (n.Id == id) return true;
            foreach (var b in n.Branches) foreach (var c in b.Nodes) if (Visit(c)) return true;
            return false;
        }
        return chain.Actions.Any(a => a.Target is { } t && Visit(t));
    }

    [InstallFact]
    public void Only_the_confirms_kill_layer_changes_with_the_none_state()
    {
        string dir = Path.Combine(Game.Value!, "streamingaudio");
        using var lib = WwiseLibrary.Open(dir, AudioCatalog.PackagesFor(dir, "mp_tdm_09"), out _);
        var switches = lib.Banks.SelectMany(b => b.Bank.Objects).OfType<HircSwitch>().DistinctBy(s => s.Id).Where(s => s.GroupType == 1).OrderBy(s => s.Id).ToList();

        // The MP banks hold two state switch containers: the hit events' impact state (6f0f7f7c, default = "none": unchanged) and the confirms' death state.
        Assert.Equal(new uint[] { 0x2bb5bddf, 0x3cc61118 }, switches.Select(s => s.Id));
        var impact = switches[0];
        Assert.Equal((0x6f0f7f7cu, AudioCatalog.NoneState), (impact.GroupId, impact.DefaultSwitch));
        var death = switches[1];
        Assert.Equal((ImpactSounds.DeathStateGroup, ImpactSounds.DeathArmored), (death.GroupId, death.DefaultSwitch));
        Assert.Empty(death.NodesFor(AudioCatalog.NoneState));                 // unset now: nothing
        Assert.NotEmpty(death.NodesFor(null));                                // before: the default case (the kill layer)
        Assert.NotEmpty(death.NodesFor(ImpactSounds.DeathFlesh));
        // Only containers with a "none" case and another default change: just the death one.
        Assert.Equal(new[] { death.Id }, switches.Where(s => s.DefaultSwitch != AudioCatalog.NoneState && s.Cases.Any(c => c.SwitchId == AudioCatalog.NoneState)).Select(s => s.Id));

        // Who reads them: the impact state only the weapons' hit events (all posted through the client's impact-state bracket: PostLevelImpact, PostPlayerHitSound,
        // SoundDetonation); no melee hit sound. The death state only the three smack events.
        string[] names = ["play_bolter_hits", "play_stalker_hits", "play_sm_lascannon_impacts", "play_plasma_pistol_hit", "play_plasma_rifle_hit", "play_melta_gun_hit",
            "play_ig_lasgun_impacts", "play_plasma_large_hit", "play_bullet_hits_small", "play_bullet_hits_large", "play_plasma_pistol_charged_hit",
            "play_bolter_smack", "play_plasma_smack", "play_stalker_smack", "play_grenade_launcher_smack", "play_sm_kick_hit", "play_sm_chain_sword_hit"];
        var events = lib.Events.ToList();
        var readImpact = events.Where(e => Reaches(lib.ResolveEvent(e.Id), impact.Id)).Select(e => e.Id).ToHashSet();
        var readDeath = events.Where(e => Reaches(lib.ResolveEvent(e.Id), death.Id)).Select(e => e.Id).ToHashSet();
        Assert.Equal(12, readImpact.Count);   // the eleven named here and one unnamed (0xffce1099)
        foreach (var n in names[..11]) Assert.Contains(Fnv.Hash32(n), readImpact);
        Assert.DoesNotContain(Fnv.Hash32("play_sm_kick_hit"), readImpact);
        Assert.DoesNotContain(Fnv.Hash32("play_sm_chain_sword_hit"), readImpact);
        Assert.Equal(new[] { "play_bolter_smack", "play_plasma_smack", "play_stalker_smack" }.Select(Fnv.Hash32).Order(), readDeath.Order());

        // Which confirm events put the death state back (to alive, 50 ms later): all but the grenade launcher's, whose post the client resets itself.
        bool Resets(string name) => lib.Find(Fnv.Hash32(name)) is HircEvent ev && ev.ActionIds.Select(lib.Find).OfType<HircAction>()
            .Any(a => a.Kind == HircActionKind.SetState && a.Parameters.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(a.Parameters.Span) == ImpactSounds.DeathStateGroup);
        Assert.True(Resets("play_bolter_smack") && Resets("play_plasma_smack") && Resets("play_stalker_smack"));
        Assert.False(Resets("play_grenade_launcher_smack"));
    }
}
