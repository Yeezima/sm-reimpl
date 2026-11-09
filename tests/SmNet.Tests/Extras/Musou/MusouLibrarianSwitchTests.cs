using System.Numerics;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// The Librarian pass 3's restore switches (<see cref="LibrarianVariant"/>, <c>SM_LIBRARIAN_OLD</c>: the pass 2 Shockwave, the Warp Pull back without equipment, the old send timing, no early cast) and the
/// lightning's synthesized sound layers (<see cref="LibrarianSynth"/>: no stored audio, so the tests pin their shape: length, level, a seamless loop, determinism).
/// </summary>
public class MusouLibrarianSwitchTests
{
    const int Rate = 30;
    const float Dt = 1f / Rate;
    static readonly LibrarianPress None = default, ShockKey = new(false, true), PullKey = new(false, false, true);
    static readonly LibrarianVariant Old = LibrarianVariant.Parse("all");

    sealed class Rig
    {
        public readonly MusouSim Sim = new(null) { TickRate = Rate };
        public readonly LibrarianPsyker Psyker = new();
        public LibrarianVariant Variant;
        public uint Tick;
        LibrarianCaster Caster => new(1, Vector3.Zero, new Vector3(0f, 0f, 1f), 0f, 0f);
        public void Cast(LibrarianPress press, int ticks = 1) { for (int k = 0; k < ticks; k++) { Sim.BeginTick(++Tick); Psyker.Step(Sim, Caster, press, Dt, null, Variant); } }
        public void Run(LibrarianPress press, int ticks)
        {
            var targets = new[] { new MusouTarget(1, Vector3.Zero, true) };
            var attacks = new List<MusouAttack>();
            for (int k = 0; k < ticks; k++) { Sim.BeginTick(++Tick); Psyker.Step(Sim, Caster, k == 0 ? press : None, Dt, null, Variant); attacks.Clear(); Sim.Step(targets, null, false, attacks); }
        }
        public ushort Add(MusouEnemyType type, float x, float y, float? health = null) => Sim.Spawn(type, new Vector3(x, y, 0f), 0f, health ?? MusouRules.Default.Of(type).Health);
        public MusouEnemy Get(ushort id) { Assert.True(Sim.TryGet(id, out var e)); return e; }
        public float Distance(ushort id) => new Vector2(Get(id).Position.X, Get(id).Position.Y).Length();
    }

    [Fact]
    public void The_switches_parse_from_the_environment_words()
    {
        Assert.False(LibrarianVariant.Parse(null).Any);
        Assert.False(LibrarianVariant.Parse(" ").Any);
        Assert.Equal(new LibrarianVariant(true, false, false, false), LibrarianVariant.Parse("shockwave"));
        Assert.Equal(new LibrarianVariant(false, true, true, false), LibrarianVariant.Parse("Pull, SEND, nonsense"));
        Assert.Equal(new LibrarianVariant(true, true, true, true), LibrarianVariant.Parse("all"));
        Assert.Equal("shockwave,pull,send,early", Old.ToString());
        Assert.Equal("SM_LIBRARIAN_OLD", LibrarianVariant.EnvironmentVariable);
    }

    [Fact]
    public void The_old_shockwave_is_the_pass_2_one_and_the_new_one_is_untouched_without_the_switch()
    {
        var r = new Rig { Variant = new LibrarianVariant(OldShockwave: true) };
        var boy = r.Add(MusouEnemyType.Boy, 0f, 5f);
        var nob = r.Add(MusouEnemyType.Nob, 4f, 4f);
        var far = r.Add(MusouEnemyType.Boy, 0f, 12f);
        var nobAt = r.Get(nob).Position;
        r.Cast(ShockKey);
        Assert.Equal(85f - LibrarianRules.Pass2.ShockwaveDamage, r.Get(boy).Health, 0.01f);
        Assert.Equal(MusouRules.Default.Of(MusouEnemyType.Boy).Health, r.Get(far).Health);   // outside 9 m
        r.Cast(None, Rate);   // the Librarian alone (the Orks' own walk is not stepped)
        Assert.True(Vector3.Distance(r.Get(nob).Position, nobAt) < 0.3f);   // elites not moved in pass 2

        var now = new Rig();
        var boyNow = now.Add(MusouEnemyType.Boy, 0f, 12f);
        now.Cast(ShockKey);
        Assert.False(now.Get(boyNow).Alive);   // 300 within 60 m
    }

    [Fact]
    public void With_the_pull_switch_grenade_pulls_as_in_pass_2_and_without_it_nothing_is_pulled()
    {
        var r = new Rig { Variant = new LibrarianVariant(WarpPull: true) };
        var boy = r.Add(MusouEnemyType.Boy, 0f, 20f);
        var nob = r.Add(MusouEnemyType.Nob, 3f, 20f);
        float nobStart = r.Distance(nob);
        r.Run(PullKey, (int)(LibrarianRules.Pass2.PullSeconds * Rate) + 2);
        Assert.Equal(LibrarianRules.Pass2.PullStopDistance, r.Distance(boy), 0.6f);
        Assert.Equal(nobStart * (1f - LibrarianRules.Pass2.ElitePullShare), r.Distance(nob), 1.2f);
        Assert.Equal(1, r.Psyker.PullCount);
        Assert.Equal(LibrarianRules.Pass2.PullStress, r.Psyker.Stress, 0.5f);
        Assert.InRange(r.Psyker.PullCooldown, LibrarianRules.Pass2.PullCooldown - 0.5f, LibrarianRules.Pass2.PullCooldown);   // 8 s, less the ticks the pull took

        var off = new Rig();
        var still = off.Add(MusouEnemyType.Boy, 0f, 20f);
        off.Cast(PullKey, 20);   // the Librarian alone: nothing drives the Boy
        Assert.Equal(20f, off.Distance(still), 0.01f);
        Assert.Equal(0, off.Psyker.PullCount);
    }

    [Fact]
    public void The_pull_switch_takes_the_librarians_equipment_away_and_changes_the_config_hash()
    {
        var now = MusouLoadout.Prepare(MusouLibrarianTests.Raw(ContentExtras.Musou));
        var old = MusouLoadout.Prepare(MusouLibrarianTests.Raw(ContentExtras.Musou) with { LibrarianVariant = new LibrarianVariant(WarpPull: true) });
        Assert.NotEmpty(now.Classes[^1].EquipmentChoices);
        Assert.Empty(old.Classes[^1].EquipmentChoices);
        Assert.NotEqual(now.Hash(), old.Hash());   // a client without the switch is refused
        var shockOnly = MusouLoadout.Prepare(MusouLibrarianTests.Raw(ContentExtras.Musou) with { LibrarianVariant = new LibrarianVariant(OldShockwave: true, OldSend: true) });
        Assert.Equal(now.Hash(), shockOnly.Hash());   // the server-side switches leave the config alone
    }

    [Fact]
    public void The_early_switch_refuses_the_cast_command()
    {
        var w = MusouLibrarianTests.NewWorld(out int cls);
        var old = new World(w.Config with { LibrarianVariant = new LibrarianVariant(NoEarly: true) }) { Rules = MusouSimTests.Endless, StartAreas = MusouDirectorTests.Rooms };
        Assert.True(old.StartMusou(null));
        old.Spawn(1, cls);
        old.Step(_ => default);
        Assert.False(old.LibrarianCastEarly(1, old.Tick + 3));
        w.Spawn(1, cls);
        w.Step(_ => default);
        Assert.True(w.LibrarianCastEarly(1, w.Tick + 3));
    }

    // ---------------------------------------------------------------- the synthesized layers

    [Fact]
    public void The_synthesized_layers_have_their_length_and_peak_and_are_repeatable()
    {
        foreach (var (name, x, seconds, peakDb) in new (string, float[], float, float)[]
                 {
                     ("crack", LibrarianSynth.Crack(11), 0.55f, -1f), ("snap", LibrarianSynth.Snap(51), 0.28f, -1f), ("thunder", LibrarianSynth.Thunder(21), 3.4f, -2f),
                     ("boom", LibrarianSynth.Boom(41), 2.8f, -1f), ("sizzle", LibrarianSynth.Sizzle(31), 2.4f, -3f),
                 })
        {
            Assert.Equal((int)(seconds * LibrarianSynth.Rate), x.Length);
            float peak = x.Max(MathF.Abs);
            Assert.InRange(20f * MathF.Log10(peak), peakDb - 0.05f, peakDb + 0.05f);
            Assert.All(x, v => Assert.True(float.IsFinite(v), name));
            double rms = Math.Sqrt(x.Average(v => (double)v * v));
            Assert.True(rms > 0.01, $"{name} is nearly silent (rms {rms:F4})");
        }
        Assert.Equal(LibrarianSynth.Crack(11), LibrarianSynth.Crack(11));
        Assert.NotEqual(LibrarianSynth.Snap(51), LibrarianSynth.Snap(52));
    }

    [Fact]
    public void The_sizzle_loops_without_a_click_and_the_wav_is_mono_16_bit()
    {
        var x = LibrarianSynth.Sizzle(31);
        float seam = MathF.Abs(x[0] - x[^1]);
        float typical = 0f;
        for (int i = 1; i < x.Length; i++) typical = MathF.Max(typical, MathF.Abs(x[i] - x[i - 1]));
        Assert.True(seam <= typical, $"the loop's seam jumps {seam:F3} (the largest step inside is {typical:F3})");
        var wav = LibrarianSynth.Wav(x);
        Assert.Equal(44 + x.Length * 2, wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal((short)1, BitConverter.ToInt16(wav, 22));    // mono
        Assert.Equal(LibrarianSynth.Rate, BitConverter.ToInt32(wav, 24));
        Assert.Equal((short)16, BitConverter.ToInt16(wav, 34));
    }
}
