using SmContent.Extras.LoyalistBarks;
using SmNet.Extras.LoyalistBarks;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.LoyalistBarks;

/// <summary>
/// Added content "loyalist barks": the committed voice pack's manifest, which take is said when (<see cref="LoyalistBarkRules"/>), the key's shout on
/// the wire (<see cref="LoyalistShoutWire"/>, <see cref="LoyalistShoutInbox"/>) and end to end through a server (the key's input bit becomes a shout every
/// other client receives, only from a living loyalist; a server without the flag ignores the bit).
/// </summary>
public class LoyalistBarksTests(ITestOutputHelper output)
{
    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("SmReimpl.slnx not found");
    }

    static string Assets => Path.Combine(RepoRoot(), "client", "Extras", "LoyalistBarks", "assets");

    static LoyalistBarkPack Pack() => LoyalistBarkPack.Parse(File.ReadAllText(Path.Combine(Assets, "loyalist-barks.json")));

    [Fact]
    public void The_committed_pack_has_eight_phrases_of_four_takes_and_every_file_matches_its_hash()
    {
        var pack = Pack();
        Assert.Equal(32, pack.Clips.Count);
        Assert.Equal(8, pack.Clips.Select(c => c.Phrase).Distinct().Count());
        Assert.All(pack.Clips.GroupBy(c => c.Phrase), g => Assert.Equal([1, 2, 3, 4], g.Select(c => c.Take).OrderBy(t => t)));
        Assert.Equal(4, pack.Clips.Count(c => c.Phrase == LoyalistBarkPack.EmperorPhrase && c.Text == "FOR THE EMPEROR!" && c.Event == BarkEvent.BattleCry));
        var files = Directory.GetFiles(Path.Combine(Assets, "audio")).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(pack.Clips.Select(c => Path.GetFileName(c.File)).Order().ToArray(), files);   // nothing more (no wav, dry or preview files), nothing less
        foreach (var c in pack.Clips)
        {
            var bytes = File.ReadAllBytes(Path.Combine(Assets, c.File));
            Assert.True(bytes.AsSpan(0, 4).SequenceEqual("OggS"u8), c.Id);
            Assert.Equal(c.Sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
        }
        Assert.True(File.Exists(Path.Combine(Assets, ".gdignore")));   // Godot never imports or exports them; they are embedded with added content only
    }

    [Fact]
    public void Malformed_manifests_are_refused()
    {
        string ok = """{"schema_version":1,"format":{"sample_rate":48000},"clips":[{"id":"a","file":"audio/a.ogg","text":"x","phrase":"p","event":"attack","take":1,"duration_seconds":1.0}]}""";
        Assert.Single(LoyalistBarkPack.Parse(ok).Clips);
        foreach (var bad in new[] { ok.Replace("attack", "sneeze"), ok.Replace("audio/a.ogg", "../a.ogg"), ok.Replace("audio/a.ogg", "audio/a.wav"), ok.Replace("\"take\":1", "\"take\":0"),
                     ok.Replace("schema_version\":1", "schema_version\":2"), "{", "[]", ok.Replace("\"clips\":[", "\"clips\":[{\"id\":\"a\",\"file\":\"audio/a.ogg\",\"text\":\"x\",\"phrase\":\"p\",\"event\":\"attack\",\"take\":1,\"duration_seconds\":1.0},") })
            Assert.Throws<InvalidDataException>(() => LoyalistBarkPack.Parse(bad));
    }

    [Fact]
    public void Moments_draw_from_the_packs_events_and_the_key_only_from_FOR_THE_EMPEROR()
    {
        var pack = Pack();
        string Phrases(BarkMoment m) => string.Join(",", LoyalistBarkRules.Candidates(pack, m).Select(c => c.Phrase).Distinct().Order());
        Assert.Equal("for_the_emperor", Phrases(BarkMoment.Key));
        Assert.Equal("for_the_emperor,glory_of_the_imperium,none_shall_stop_us", Phrases(BarkMoment.Charge));
        Assert.Equal("cleanse_purge_kill,for_the_emperor,glory_of_the_imperium,no_prisoners,none_shall_stop_us,space_marines_attack", Phrases(BarkMoment.Kill));
        Assert.Equal("cleanse_purge_kill,no_prisoners,space_marines_attack", Phrases(BarkMoment.Melee));
        Assert.Equal("eat_boltgun", Phrases(BarkMoment.Fire));
        Assert.Equal("taking_fire", Phrases(BarkMoment.UnderFire));
    }

    [Fact]
    public void Only_loyalists_bark_and_the_rules_apply_chance_cooldowns_spacing_phrases_and_sm1_precedence()
    {
        var rules = new LoyalistBarkRules(Pack());
        // Seeds whose roll passes or fails the kill's 0.5 chance.
        long Pass(int player, BarkMoment m) => Enumerable.Range(0, 1000).First(s => new LoyalistBarkRules(Pack()).Decide(player, true, m, s, 0, false, false).Verdict == BarkVerdict.Said);
        Assert.Equal(BarkVerdict.NotLoyalist, rules.Decide(1, false, BarkMoment.Key, 1, 0, false, false).Verdict);   // Chaos and Orks never
        Assert.Equal(BarkVerdict.NotLoyalist, rules.Decide(1, false, BarkMoment.Kill, Pass(1, BarkMoment.Kill), 0, false, false).Verdict);
        long s1 = Pass(1, BarkMoment.Kill);
        Assert.Equal(BarkVerdict.Sm1Speech, rules.Decide(1, true, BarkMoment.Kill, s1, 0, false, sm1Speech: true).Verdict);
        Assert.Equal(BarkVerdict.Speaking, rules.Decide(1, true, BarkMoment.Kill, s1, 0, speaking: true, false).Verdict);
        var said = rules.Decide(1, true, BarkMoment.Kill, s1, 10, false, false);
        Assert.Equal(BarkVerdict.Said, said.Verdict);
        Assert.Equal(BarkVerdict.Cooldown, rules.Decide(1, true, BarkMoment.Kill, s1, 17.9, false, false).Verdict);       // 8 s per speaker
        Assert.Equal(BarkVerdict.Spacing, rules.Decide(2, true, BarkMoment.Kill, Pass(2, BarkMoment.Kill), 11, false, false).Verdict);   // 1.5 s anywhere
        // The same phrase from the same speaker waits 20 s ("Eat boltgun!" is the only firing phrase).
        var r2 = new LoyalistBarkRules(Pack());
        var first = r2.Decide(5, true, BarkMoment.Fire, Pass(5, BarkMoment.Fire), 0, false, false).Clip!;
        Assert.Equal(BarkVerdict.PhraseCooldown, r2.Decide(5, true, BarkMoment.Fire, Pass(5, BarkMoment.Fire), 9, false, false).Verdict);   // one phrase only
        var later = r2.Decide(5, true, BarkMoment.Fire, Pass(5, BarkMoment.Fire), 21, false, false);
        Assert.Equal(BarkVerdict.Said, later.Verdict);
        Assert.NotEqual(first.Take, later.Clip!.Take);   // no immediate repeat of a take
    }

    [Fact]
    public void The_key_says_a_FOR_THE_EMPEROR_take_from_the_shout_sequence_never_the_same_take_twice_in_a_row()
    {
        var takes = LoyalistBarkRules.Candidates(Pack(), BarkMoment.Key);
        Assert.Equal(4, takes.Count);
        // The no-repeat rule across the u8 wrap (255 -> 0) needs 256 % takes == 0: a pack with 3, 5, 6 or 7 FOR THE EMPEROR takes would repeat once per wrap.
        Assert.Equal(0, 256 % takes.Count);
        Assert.True(LoyalistBarkRules.KeyTakeWrapsWithoutRepeat(takes.Count));
        Assert.False(LoyalistBarkRules.KeyTakeWrapsWithoutRepeat(3));
        foreach (int player in new[] { 1, 7, 15 })
        {
            var seen = new HashSet<int>();
            BarkClip? last = null;
            for (int n = 1; n <= 600; n++)   // across the u8 wrap
            {
                var clip = LoyalistBarkRules.KeyTake(player, unchecked((byte)n), takes);
                Assert.Same(clip, LoyalistBarkRules.KeyTake(player, unchecked((byte)n), takes));   // a function of player and sequence alone
                Assert.NotEqual(last?.Take, clip.Take);
                seen.Add(clip.Take);
                last = clip;
            }
            Assert.Equal(4, seen.Count);
        }
        // Through the rules: any time, over its own line and SM1's, the take of the sequence; two rule sets (two clients) agree.
        var (a, b) = (new LoyalistBarkRules(Pack()), new LoyalistBarkRules(Pack()));
        for (byte n = 1; n < 20; n++)
        {
            var x = a.Decide(7, true, BarkMoment.Key, n, n * 0.1, speaking: true, sm1Speech: true);
            Assert.Equal(BarkVerdict.Said, x.Verdict);
            Assert.Equal(LoyalistBarkPack.EmperorPhrase, x.Clip!.Phrase);
            Assert.Equal(x.Clip.Id, b.Decide(7, true, BarkMoment.Key, n, 50 + n, false, false).Clip!.Id);
        }
    }

    [Fact]
    public void Shouts_round_trip_and_bad_payloads_are_refused()
    {
        var shouts = new[] { new LoyalistShout(3, 1234567, LoyalistShoutKind.BattleCry, 1), new LoyalistShout(65535, uint.MaxValue, LoyalistShoutKind.BattleCry, 255) };
        var bytes = LoyalistShoutWire.Write(shouts);
        Assert.Equal(1 + 2 * LoyalistShoutWire.RecordBytes, bytes.Length);
        Assert.Equal(shouts, LoyalistShoutWire.Read(bytes));
        Assert.Null(LoyalistShoutWire.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Null(LoyalistShoutWire.Read([]));
        var kind = (byte[])bytes.Clone(); kind[7] = 9;
        Assert.Null(LoyalistShoutWire.Read(kind));
        var many = Enumerable.Range(0, 40).Select(i => new LoyalistShout(i, (uint)i, LoyalistShoutKind.BattleCry, (byte)i)).ToList();
        var capped = LoyalistShoutWire.Read(LoyalistShoutWire.Write(many))!;
        Assert.Equal(LoyalistShoutWire.MaxShouts, capped.Length);
        Assert.Equal(39, capped[^1].PlayerId);
        Assert.True(LoyalistShoutWire.CooledDown(null, 5, 30));
        Assert.False(LoyalistShoutWire.CooledDown(100, 159, 30));
        Assert.True(LoyalistShoutWire.CooledDown(100, 160, 30));
    }

    [Fact]
    public void The_inbox_plays_each_shout_once_not_the_own_echo_and_not_late_ones()
    {
        var inbox = new LoyalistShoutInbox();
        Assert.Equal(1, inbox.NextOwnSequence);
        var a = new LoyalistShout(2, 1000, LoyalistShoutKind.BattleCry, 1);
        var own = new LoyalistShout(1, 1000, LoyalistShoutKind.BattleCry, 5);
        Assert.Equal([a], inbox.Take([a, own], ownId: 1, serverTick: 1005, tickRate: 30));
        Assert.Equal(6, inbox.NextOwnSequence);   // the own echo's sequence is noted
        Assert.Empty(inbox.Take([a, own], 1, 1006, 30));
        Assert.Empty(inbox.Take([new LoyalistShout(3, 900, LoyalistShoutKind.BattleCry, 1)], 1, 1000, 30));
    }

    static PlayerInput Press(uint tick, bool key) => new(tick, 0, 0, 0, key ? InputButtons.ExtraAction : InputButtons.None);

    static List<LoyalistShout> Heard(GameClient c, int tickRate)
    {
        var inbox = new LoyalistShoutInbox();
        var sections = c.TakeExtras();
        Assert.All(sections, s => Assert.Equal(LoyalistShoutWire.Tag, s.Tag));
        return sections.SelectMany(s => inbox.Take(LoyalistShoutWire.Read(s.Payload)!, c.PlayerId, s.ServerTick, tickRate)).ToList();
    }

    [Fact]
    public void The_key_reaches_every_other_client_once_per_cooldown_and_changes_nothing_in_the_simulation()
    {
        var config = new SimConfig { Extras = ContentExtras.LoyalistBarks };
        var h = new Harness(LinkConditions.Perfect, 11, 3, config);
        var log = new List<string>();
        h.Server.Log += log.Add;
        h.Run(TimeSpan.FromSeconds(1.5));
        Assert.All(h.Clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
        int shouter = h.Clients[0].PlayerId;
        Assert.Equal(Race.SpaceMarines, Races.Of(config, h.Server.World.Players[shouter].Class, h.Server.World.Players[shouter].Team));
        uint? pressedAt = null;
        void Phase(double seconds, bool key) => h.Run(TimeSpan.FromSeconds(seconds), (i, t) => { if (i == 0 && key) pressedAt ??= t; return Press(t, i == 0 && key); });
        Phase(0.2, true); Phase(1.0, false); Phase(0.2, true); Phase(LoyalistShoutWire.CooldownSeconds, false); Phase(0.2, true); Phase(0.5, false);
        foreach (var line in log) output.WriteLine(line);
        Assert.Equal((2, 1), (h.Server.LoyalistShouts, h.Server.LoyalistShoutsRefused));
        for (int i = 0; i < h.Clients.Count; i++)
        {
            var heard = Heard(h.Clients[i], config.TickRate);
            output.WriteLine($"client {i} (P{h.Clients[i].PlayerId}): heard {string.Join(", ", heard)}");
            if (i == 0) { Assert.Empty(heard); continue; }   // the shouter's echo is not played again
            Assert.Equal(2, heard.Count);
            Assert.All(heard, s => Assert.Equal(shouter, s.PlayerId));
            Assert.Equal(pressedAt, heard[0].Tick);   // the shout's tick is the input's
            Assert.Equal([1, 2], heard.Select(s => (int)s.Sequence));   // every client picks the take from the same sequence (LoyalistBarkRules.KeyTake)
        }

        // The key changes nothing the simulation does: the same inputs with and without the bit give the same world.
        PlayerState Run(bool key)
        {
            var hh = new Harness(LinkConditions.Perfect, 21, 1, config);
            hh.Run(TimeSpan.FromSeconds(3), (i, t) => Harness.Scripted(i, t) with { Buttons = Harness.Scripted(i, t).Buttons | (key && t % 7 == 0 ? InputButtons.ExtraAction : InputButtons.None) });
            return hh.Server.World.Players.Values.Single();
        }
        var (a, b) = (Run(false), Run(true));
        Assert.Equal((a.Position, a.Velocity, a.Yaw, a.MeleeStep, a.Charging, a.Health, a.Shield), (b.Position, b.Velocity, b.Yaw, b.MeleeStep, b.Charging, b.Health, b.Shield));
    }

    [Fact]
    public void A_bit_held_without_a_new_edge_shouts_once_and_spam_is_bounded_by_the_cooldown()
    {
        var config = new SimConfig { Extras = ContentExtras.LoyalistBarks };
        var h = new Harness(LinkConditions.Perfect, 14, 2, config);
        h.Run(TimeSpan.FromSeconds(1.5));
        h.Run(TimeSpan.FromSeconds(10), (i, t) => Press(t, i == 0));   // held 10 s
        Assert.Equal(1, h.Server.LoyalistShouts);
        h.Run(TimeSpan.FromSeconds(0.5));
        h.Clients.ForEach(c => c.TakeExtras());
        // Spam: a new press every other tick for 10 s gives at most one shout per cooldown, and a bounded envelope traffic.
        int sentBefore = h.Server.ExtrasMessagesSent, shoutsBefore = h.Server.LoyalistShouts;
        h.Run(TimeSpan.FromSeconds(10), (i, t) => Press(t, i == 0 && t % 2 == 0));
        h.Run(TimeSpan.FromSeconds(0.5));
        int shouts = h.Server.LoyalistShouts - shoutsBefore, messages = h.Server.ExtrasMessagesSent - sentBefore;
        output.WriteLine($"spam: {shouts} shouts, {h.Server.LoyalistShoutsRefused} refused, {messages} envelope messages, largest {h.Server.LargestExtrasMessage} bytes");
        Assert.InRange(shouts, 4, 6);   // 10 s / 2 s
        Assert.True(messages <= shouts * LoyalistShoutWire.ResendTicks);
        Assert.True(h.Server.LargestExtrasMessage <= Protocol.ExtrasHeaderBytes + Protocol.ExtrasSectionHeaderBytes + 1 + LoyalistShoutWire.RecordBytes);
        Assert.Equal(shouts, Heard(h.Clients[1], config.TickRate).Count);
    }

    [Fact]
    public void A_four_tick_hold_survives_a_lossy_link()
    {
        var config = new SimConfig { Extras = ContentExtras.LoyalistBarks };
        var lossy = new LinkConditions(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(10), 0.3);
        var h = new Harness(lossy, 15, 3, config);
        h.Run(TimeSpan.FromSeconds(3));
        Assert.All(h.Clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
        h.Clients.ForEach(c => c.TakeExtras());
        const int presses = 6;
        uint? first = null;
        // The client's rule (NetworkView.LoyalistBarks, ExtrasInput): a press holds the bit for HoldTicks ticks; one press every 3 s.
        h.Run(TimeSpan.FromSeconds(3 * presses), (i, t) =>
        {
            first ??= t;
            uint phase = (t - first.Value) % 90;
            return Press(t, i == 0 && phase < LoyalistShoutWire.HoldTicks && (t - first.Value) / 90 < presses);
        });
        h.Run(TimeSpan.FromSeconds(1));
        output.WriteLine($"accepted {h.Server.LoyalistShouts}, refused {h.Server.LoyalistShoutsRefused}");
        Assert.Equal(presses, h.Server.LoyalistShouts);
        foreach (var c in h.Clients.Skip(1))
        {
            int heard = Heard(c, config.TickRate).Count;
            output.WriteLine($"P{c.PlayerId} heard {heard}");
            Assert.InRange(heard, presses - 1, presses);   // each shout goes out in 3 messages; one may still be lost on a 30 % link
        }
    }

    [Fact]
    public void Servers_without_the_flag_drop_the_key_and_chaos_and_ork_players_never_shout()
    {
        foreach (var extras in new[] { ContentExtras.None, ContentExtras.Orks | ContentExtras.Gore })
        {
            var h = new Harness(LinkConditions.Perfect, 12, 2, new SimConfig { Extras = extras });
            h.Run(TimeSpan.FromSeconds(1.5));
            h.Run(TimeSpan.FromSeconds(1), (i, t) => Press(t, (t / 10) % 2 == 0));
            Assert.Equal((0, 0), (h.Server.LoyalistShouts, h.Server.ExtrasMessagesSent));
            Assert.All(h.Clients, c => Assert.Empty(c.TakeExtras()));
        }
        var basic = new SimConfig();
        foreach (var (name, cls, race) in new[] { ("csm", basic.Classes[0] with { Name = "csm", Side = Team.Chaos }, Race.Chaos), ("ork", basic.Classes[0] with { Name = "ork - boy", Race = Race.Orks }, Race.Orks) })
        {
            var config = basic with { Extras = ContentExtras.LoyalistBarks, Classes = [cls] };
            var t2 = new Harness(LinkConditions.Perfect, 13, 2, config);
            t2.Run(TimeSpan.FromSeconds(1.5));
            Assert.All(t2.Clients, c => Assert.Equal(race, Races.Of(config, t2.Server.World.Players[c.PlayerId].Class, t2.Server.World.Players[c.PlayerId].Team)));
            t2.Run(TimeSpan.FromSeconds(0.2), (i, t) => Press(t, true));
            t2.Run(TimeSpan.FromSeconds(0.2));
            Assert.Equal((0, 2), (t2.Server.LoyalistShouts, t2.Server.LoyalistShoutsRefused));
            Assert.All(t2.Clients, c => Assert.Empty(c.TakeExtras()));
            output.WriteLine($"{name}: refused");
        }
    }

    [Fact]
    public void A_dead_loyalist_does_not_shout()
    {
        var config = new SimConfig() with
        {
            Extras = ContentExtras.LoyalistBarks, Damage = 22f, ShotCooldown = 0.133f, ClipSize = 30, ReserveSize = 270, ReloadTime = 1.3f, MaxRange = 250f,
            HealthMax = 100f, ShieldMax = 100f, RespawnTime = 5f,
        };
        var h = new Harness(LinkConditions.Perfect, 16, 2, config);
        h.Run(TimeSpan.FromSeconds(1.5));
        var shooter = h.Clients[0];
        int target = h.Clients[1].PlayerId;
        var end = h.Clock.Now + TimeSpan.FromSeconds(10);
        while (h.Server.World.Players[target].Alive && h.Clock.Now < end)
            h.Step((i, t) =>
            {
                if (i != 0) return Press(t, false);
                var d = h.Server.World.Players[target].Position + new System.Numerics.Vector3(0, 0, 1f) - (shooter.Predicted.Position + new System.Numerics.Vector3(0, 0, config.EyeHeight));
                return new PlayerInput(t, 0, 0, MathF.Atan2(-d.X, d.Y), InputButtons.Fire, MathF.Atan2(d.Z, new System.Numerics.Vector2(d.X, d.Y).Length()));
            });
        Assert.False(h.Server.World.Players[target].Alive);
        h.Run(TimeSpan.FromSeconds(0.2), (i, t) => Press(t, i == 1));   // the dead player presses
        h.Run(TimeSpan.FromSeconds(0.2));
        Assert.Equal((0, 1), (h.Server.LoyalistShouts, h.Server.LoyalistShoutsRefused));
    }

    static GameClient Addressed(Harness h, string ip, uint nonce)
    {
        var c = new GameClient(h.Config, Harness.Fingerprint, h.Net.Connect(new System.Net.IPEndPoint(System.Net.IPAddress.Parse(ip), 1000)), h.Clock, nonce: nonce);
        h.Clients.Add(c);
        return c;
    }

    // The None server's idle kick, the stored-input mask on servers without the flag and IsRealInput are core tests (ActionKeyMaskTests), so the faithful build runs them.
    [Fact]
    public void A_barks_server_keeps_the_key_in_the_stored_input_but_the_key_alone_is_not_play_for_the_idle_kick()
    {
        var config = new SimConfig { Extras = ContentExtras.LoyalistBarks };
        var held = new Harness(LinkConditions.Perfect, 18, 1, config);
        held.Run(TimeSpan.FromSeconds(1.5));
        held.Run(TimeSpan.FromSeconds(0.5), (_, t) => Press(t, true));
        Assert.NotEqual(InputButtons.None, held.Server.LastAppliedForTests(held.Clients[0].PlayerId).Buttons & InputButtons.ExtraAction);

        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(10), TimeSpan.Zero, 0), 17, 0, config, new ServerOptions { IdleKick = TimeSpan.FromSeconds(5) });
        Addressed(h, "10.0.0.1", 1);
        Addressed(h, "10.0.0.2", 2);
        h.Run(TimeSpan.FromSeconds(1));
        int keyOnly = h.Clients[0].PlayerId, moving = h.Clients[1].PlayerId;
        h.Run(TimeSpan.FromSeconds(7), (i, t) => i == 0 ? Press(t, t % 2 == 0) : new PlayerInput(t, (t / 60) % 2 == 0 ? 1 : -1, 0, 0, InputButtons.None));
        Assert.False(h.Server.World.Players.ContainsKey(keyOnly));
        Assert.True(h.Server.World.Players.ContainsKey(moving));
        Assert.Equal(1, h.Server.IdleKicked);
    }

    [Fact]
    public void The_embedded_names_the_csproj_gives_are_the_ones_the_client_asks_for()
    {
        string root = RepoRoot();
        string csproj = File.ReadAllText(Path.Combine(root, "client", "SmClient.csproj"));
        string view = File.ReadAllText(Path.Combine(root, "client", "Extras", "LoyalistBarks", "NetworkView.LoyalistBarks.cs"));
        Assert.Contains("Include=\"Extras/*/assets/**/*.ogg;Extras/*/assets/*.json\" LogicalName=\"Extras/%(Filename)%(Extension)\"", csproj);
        Assert.Contains("Condition=\"'$(SmExtras)' == 'true'\"", csproj);
        Assert.Contains("const string EmbeddedExtrasPrefix = \"Extras/\";", view);
        Assert.Contains("GetManifestResourceStream(EmbeddedExtrasPrefix + name)", view);
        Assert.Contains("EmbeddedExtras(\"loyalist-barks.json\")", view);
        Assert.Contains("EmbeddedExtras(System.IO.Path.GetFileName(c.File))", view);
        Assert.Contains("<EmbeddedResource Remove=\"Extras/**\" />", File.ReadAllText(Path.Combine(root, "Directory.Build.targets")));
        // Every file name is unique (one flat name space under "Extras/").
        var names = Pack().Clips.Select(c => Path.GetFileName(c.File)).Append("loyalist-barks.json").ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void The_flag_is_its_own_profile_and_part_of_extended()
    {
        Assert.True(ContentExtras.All.HasFlag(ContentExtras.LoyalistBarks));
        Assert.True(ContentProfiles.TryParse("loyalist-barks", out var only, out _));
        Assert.Equal(ContentExtras.LoyalistBarks, only);
        Assert.Equal("loyalist-barks", ContentProfiles.Name(ContentExtras.LoyalistBarks));
        Assert.NotEqual(new SimConfig { Extras = ContentExtras.Orks | ContentExtras.Gore }.Hash(), new SimConfig { Extras = ContentExtras.All }.Hash());
        Assert.Equal(InputButtons.ExtraAction, Protocol.Quantize(new PlayerInput(1, 0, 0, 0, InputButtons.ExtraAction)).Buttons);
        Assert.Equal(1200, Protocol.MaxSnapshotBytes);
    }
}
