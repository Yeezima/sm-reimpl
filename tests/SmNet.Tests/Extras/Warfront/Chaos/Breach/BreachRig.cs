using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Battle;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Arsenal;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Chaos;

namespace SmNet.Tests.Extras.Warfront.Chaos.Breach;

/// <summary>Chaos Warfront C4's test rigs: synthetic wargear under the install's keys (no install bytes), a Chaos lane battle, a fake field for the pure breach tests, a throw.</summary>
internal static class BreachRig
{
    public const int Rate = 30;
    public const int Human = TfwRig.Human;

    static readonly DamageSpec Hurt = new(10f, 0f, 1f, 1f, 1f, 1f, false);
    static readonly DamageSpec None = new(0f, 0f, 1f, 1f, 1f, 1f, false);

    /// <summary>A straight explosive: 60 m/s, no gravity, no aim raise, goes off on what it touches (a 3 m blast).</summary>
    public static readonly ProjectileSpec Straight = new("ebps\\test\\breach_round", 60f, 5f, "raycast", 0f, [new(0f, 0f), new(1000f, 0f)], true, true, false, 0, 0f, 0f, 0, null,
        new SplashSpec(3f, Hurt, [], true, false, "target_all"));

    public static ThrownSpec Item(string key, ProjectileSpec? projectile = null) => new(key, 99, -1, 1f, 60f, projectile ?? Straight);

    /// <summary>The blind grenade (its blasts only blind).</summary>
    public static readonly ThrownSpec Blind = Item("sm_mp_eq_krak_grenade", Straight with { Splash = new SplashSpec(8f, None, [], true, false, "target_all") { Blindness = 3.5f, BlindOnly = true } });
    /// <summary>A damaging krak (no MP file has one: the rule's other half).</summary>
    public static readonly ThrownSpec DamagingKrak = Item("sm_mp_eq_krak_grenade");
    public static readonly ThrownSpec Stim = new("sm_mp_eq_stimpack", 1, -1, 0.5f, 0f, ThrownSpec.NoProjectile) { Stim = new StimSpec(5f, 0.2f, 0.5f) };

    public static WeaponSpec Gun(string key) => new(key, WeaponSlot.Ranged, TriggerKind.SemiAuto, 10f, 0.3f, 10, 90, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f) { Delivery = DeliveryKind.Projectile, Projectile = Straight };

    /// <summary>The Chaos config with the thrown items <paramref name="thrown"/> (the class carries the first) and the guns <paramref name="guns"/> after the rig's two.</summary>
    public static SimConfig Chaos(ThrownSpec[]? thrown = null, WeaponSpec[]? guns = null, SimConfig? baseConfig = null)
    {
        var c = baseConfig ?? WarfrontChaosRosterTests.ChaosConfig;
        var items = thrown ?? [Item("sm_mp_eq_frag_grenade")];
        return c with
        {
            Thrown = items,
            Weapons = [.. c.Weapons, .. guns ?? []],
            Classes = c.Classes.Select(k => k with { EquipmentChoices = [0] }).ToArray(),
        };
    }

    /// <summary>The same for the Orks.</summary>
    public static SimConfig Orks(ThrownSpec[]? thrown = null) => Chaos(thrown, null, WarfrontBattleTests.WarfrontConfig);

    /// <summary>A lane battle of <paramref name="config"/>, landed, and one more tick (the breaches are placed on the battle's first Chaos tick).</summary>
    public static (World World, TfwBattle Battle) Lane(SimConfig config)
    {
        var (w, b) = WarfrontChaosOrkPins.Lane(config);
        TfwRig.Steps(w, 1);
        return (w, b);
    }

    public static void Tick(World w, InputButtons buttons = InputButtons.None, float yaw = 0f, float pitch = 0f) =>
        w.Step(id => new PlayerInput(w.Tick, 0f, 0f, id == Human ? yaw : w.Players[id].Yaw, id == Human ? buttons : InputButtons.None, Pitch: id == Human ? pitch : 0f));

    /// <summary>The yaw (the sim's: atan2(-x, y)) from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static float YawTo(Vector3 from, Vector3 to) => MathF.Atan2(-(to.X - from.X), to.Y - from.Y);

    /// <summary>Stands the human at <paramref name="at"/> facing <paramref name="yaw"/>, holds the enemies still and away, then throws once at <paramref name="pitch"/> and follows it for a second; the enemies are killed each tick so none stops it.</summary>
    public static List<DetonationEvent> Throw(World w, Vector3 at, float yaw, float pitch, float seconds = 1f)
    {
        w.Musou!.Frozen = true;
        Assert.True(w.TfwRespawnAt(Human, at, yaw));
        TfwRig.KillAll(w);
        Tick(w, InputButtons.None, yaw, pitch);
        var mine = new List<DetonationEvent>();
        for (int k = 0; k < (int)(seconds * Rate); k++)
        {
            TfwRig.KillAll(w);
            Tick(w, k < 2 ? InputButtons.Grenade : InputButtons.None, yaw, pitch);
            mine.AddRange(w.LastDetonations.Where(d => d.Owner == Human));
        }
        return mine;
    }

    /// <summary>A field the pure tests drive: a room, ids handed out from 1, and the ids it calls dead.</summary>
    public sealed class Field : IWarfrontBreachField
    {
        ushort _next = 1;
        public int RoomNow = 1000;
        public bool Full;
        public readonly HashSet<ushort> Dead = new();
        public readonly List<(Vector3 Spot, Vector3 Breach)> Spawns = new();
        public int Room => RoomNow;
        public ushort Spawn(Vector3 spot, Vector3 breach)
        {
            if (Full) return 0;
            Spawns.Add((spot, breach));
            RoomNow--;
            return _next++;
        }
        public bool IsDaemon(ushort id) => !Dead.Contains(id);
        public void Kill(ushort id) { if (Dead.Add(id)) RoomNow++; }
    }

    /// <summary>A layout with the sources given and the arena test's other places.</summary>
    public static TfwLayout Layout(params Vector3[] sources) =>
        new(TfwTestArena.LandingZone, [TfwTestArena.Position1, TfwTestArena.Position2, TfwTestArena.Position3], TfwTestArena.Hq, sources);

    /// <summary>A roll of the given seed (the battle's own Roll stands in).</summary>
    public static Func<uint, float> Roll(uint seed) => salt =>
    {
        uint h = seed * 2654435761u ^ salt;
        h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
        return (h >> 8) * (1f / 16777216f);
    };

    /// <summary>The breaches of a lane node over <paramref name="feet"/> as sources (each passes the rules: the caller places them far from everything), stepped open.</summary>
    public static WarfrontBreaches Placed(int node, params Vector3[] feet) => WarfrontBreaches.Place(Layout(feet), null, Roll(1), new WarfrontBreachSite(WarfrontBreachBattle.Lane, node), Rate);

    public static readonly string RemoteDetonators = ArsenalRules.RemoteDetonators;
}
