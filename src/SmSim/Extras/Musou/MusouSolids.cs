using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>One big Ork as the players' movement and melee lock meet it: its id, kind and feet (as the wire carries them).</summary>
public readonly record struct MusouSolid(ushort Id, MusouEnemyType Type, Vector3 Position);

/// <summary>
/// musou/boss-collision (the owner, 2026-11-08: "musou mode - boss and elite and warboss orks should have collision with the player - lock on of melee weapons should work
/// with them as well"; rules <b>ours</b>, spec/behavior.md "Musou: solid big Orks"). Musou Mode only (added content; Classic PvP never builds one).
/// <list type="bullet">
/// <item><b>Who is solid</b> (<see cref="Is"/>): the Warboss, the Nobs, the Boss Orks (Boy, Shoota Boy, 'Ard Boy, Nob at 3x), the Big Shoota Nob, the Weirdboy and the Scrap Mek;
/// in any state but <see cref="MusouEnemyState.Dying"/> and <see cref="MusouEnemyState.Vanishing"/>. Boys, Shoota Boys, 'Ard Boys, Gretchin, Rokkit Boys, Stormboyz, Squigs and
/// Chaos Marines stay as before: they keep their own ring off the players (<see cref="MusouRules.PlayerRing"/>), and a player walks through them.</item>
/// <item><b>The body</b> (<see cref="Body"/>): an upright cylinder of the kind's walking radius and height (the Warboss's scaled by <see cref="MusouRules.BossScale"/>, a Boss Ork's
/// 3x already in its row), met by the player's capsule in the shared movement step (<see cref="PlayerCollision"/>) as another player's is, except that it never steps aside
/// (<see cref="PlayerBody.Fixed"/>: the player takes the whole overlap).</item>
/// <item><b>The picture</b>: as with the players (protocol 43), a player's step meets the big Orks as its client last knew them: the server keeps each tick's
/// (<c>World.MusouSolids.cs</c>), quantised over the wire's box exactly as the entity records carry them (<see cref="Quantised"/>); the client takes, for the input's body tick,
/// each big Ork's newest record at or before it (no older than <see cref="MaxRecordAgeTicks"/>). The leaders go first in every tick's send and near ones are sent every tick,
/// so the two agree but for a lost or late datagram (then the next snapshot's replay corrects it, a few centimetres).</item>
/// <item><b>The melee lock</b> (<see cref="LockTarget"/>): each big Ork is offered to the lock as a living player of no side under id <see cref="IdBase"/> + its id (the input's
/// melee target is 16 bits), standing at the point of its body's edge nearest the attacker less a player's radius, so the cone, range, leash, line of sight and the lunge's
/// stop measure to its front as to a player's. Only the lock sees them: the melee hits (the cleave), charges and shots go their own Musou way.</item>
/// </list>
/// </summary>
public static class MusouSolids
{
    /// <summary>The lock and body id of big Ork 1 is this plus 1 (above every player id, below the input's 16-bit cap with the 2560-slot pool).</summary>
    public const int IdBase = 60000;
    /// <summary>Ours: a client's record of a big Ork older than this many ticks before the body tick is not met (1 s; a far one is sent at about 15 Hz).</summary>
    public const uint MaxRecordAgeTicks = 30;

    /// <summary>
    /// Pass-through (the owner, 2026-11-09: "only upsized 'boss' ork should collide with players. players should passthrough everything but the big boss enemies and the warboss"; spec "Musou Mode: who
    /// is solid"): a player's body meets only the kinds of <see cref="Blocks"/> (the Warboss and the four 3x Boss Orks) and no other character, the other players and the allied Marines included; the level
    /// stays solid. Every kind of <see cref="Is"/> is still offered to the melee lock. The server's and the client's <c>SM_MUSOU_OLD_BODIES=1</c> restores the bodies before (every kind of <see cref="Is"/>
    /// solid, and the players to each other).
    /// </summary>
    public static bool OldBodies { get; set; } = Environment.GetEnvironmentVariable("SM_MUSOU_OLD_BODIES") == "1";

    /// <summary>Whether a player's body meets enemies of <paramref name="type"/> (the Warboss and the Boss Orks; before the pass-through every kind of <see cref="Is"/>).</summary>
    public static bool Blocks(MusouEnemyType type) => OldBodies ? Is(type) : type == MusouEnemyType.Warboss || MusouBossOrks.Is(type);

    /// <summary>Whether enemies of <paramref name="type"/> are offered to the melee lock (and, before the pass-through, solid to the players: now <see cref="Blocks"/>).</summary>
    public static bool Is(MusouEnemyType type) => type is MusouEnemyType.Warboss or MusouEnemyType.Nob or MusouEnemyType.BigShootaNob or MusouEnemyType.Weirdboy
        or MusouEnemyType.ScrapMek || MusouBossOrks.Is(type);

    /// <summary>Whether a solid kind in <paramref name="state"/> blocks and can be locked (not dying, not fading out of a teleport).</summary>
    public static bool Counts(MusouEnemyState state) => state is not (MusouEnemyState.Dying or MusouEnemyState.Vanishing);

    /// <summary>The id a big Ork has in the bodies and the lock view.</summary>
    public static int LockId(ushort id) => IdBase + id;

    /// <summary>Whether <paramref name="lockId"/> names a big Ork (and which).</summary>
    public static bool IsLockId(int lockId, out ushort id)
    {
        id = lockId > IdBase && lockId <= IdBase + MusouSim.Capacity ? (ushort)(lockId - IdBase) : (ushort)0;
        return id != 0;
    }

    /// <summary>The body's radius and height (as <see cref="MusouSim.Body"/>: the Warboss scaled, every other kind its row's).</summary>
    public static (float Radius, float Height) Body(MusouRules rules, MusouEnemyType type)
    {
        var spec = rules.Of(type);
        float scale = type == MusouEnemyType.Warboss ? rules.BossScale : 1f;
        return (spec.Radius * scale, spec.Height * scale);
    }

    /// <summary>A position as the entity records carry it over <paramref name="box"/> (the wire's quantisation, then back).</summary>
    public static Vector3 Quantised(Vector3 p, MusouBox box) => new(Q(p.X, box.Min.X, box.Max.X), Q(p.Y, box.Min.Y, box.Max.Y), Q(p.Z, box.Min.Z, box.Max.Z));

    /// <summary>The wire's 16-bit quantisation of <paramref name="v"/> over [<paramref name="min"/>, <paramref name="max"/>] (<c>MusouWire</c> uses it).</summary>
    public static ushort Quantise(float v, float min, float max)
    {
        float span = max - min;
        if (!(span > 0f) || !float.IsFinite(v)) return 0;
        return (ushort)MathF.Round(Math.Clamp((v - min) / span, 0f, 1f) * ushort.MaxValue);
    }

    /// <summary>The value a quantised <paramref name="q"/> stands for (<c>MusouWire</c> uses it).</summary>
    public static float Dequantise(ushort q, float min, float max) => min + q / (float)ushort.MaxValue * (max - min);

    static float Q(float v, float min, float max) => Dequantise(Quantise(v, min, max), min, max);

    /// <summary>
    /// <paramref name="view"/> in Musou Mode: the big Orks of <paramref name="solids"/> that block (<see cref="Blocks"/>) as fixed bodies of their size, and no player's body (the pass-through, 2026-11-09:
    /// the others and the allied Marines are walked through; <see cref="BodyView.Self"/> is kept). Null stays null: no player collision. Before (<see cref="OldBodies"/>): the players' bodies and every big Ork.
    /// </summary>
    public static void AddBodies(ref BodyView? view, IReadOnlyList<MusouSolid> solids, MusouRules rules)
    {
        if (view is null) return;
        var bodies = new List<PlayerBody>(solids.Count);
        foreach (var s in solids)
        {
            if (!Blocks(s.Type)) continue;
            var (radius, height) = Body(rules, s.Type);
            bodies.Add(new PlayerBody(LockId(s.Id), s.Position, s.Position, radius, height, Fixed: true));
        }
        view = OldBodies ? view.With(bodies) : BodyView.Of(bodies, view.Self);
    }

    /// <summary>
    /// <paramref name="view"/> with each big Ork of <paramref name="solids"/> added as a lock target for <paramref name="self"/> (<see cref="LockTarget"/>); the view itself when
    /// there are none or <paramref name="self"/> is dead.
    /// </summary>
    public static IReadOnlyDictionary<int, PlayerState> WithLockTargets(IReadOnlyDictionary<int, PlayerState> view, IReadOnlyList<MusouSolid> solids, in PlayerState self,
        MusouRules rules, SimConfig c)
    {
        if (solids.Count == 0 || !self.Alive) return view;
        var all = new Dictionary<int, PlayerState>(view.Count + solids.Count);
        foreach (var (id, p) in view) all[id] = p;
        foreach (var s in solids) all[LockId(s.Id)] = LockTarget(s, self, rules, c);
        return all;
    }

    /// <summary>
    /// Big Ork <paramref name="s"/> as the lock sees it from <paramref name="self"/>: a living player of no side under <see cref="LockId"/>, its feet at the edge of the Ork's
    /// body toward <paramref name="self"/> less a player's radius (the Ork's own axis for a body no wider than a player's, or one the attacker stands on).
    /// </summary>
    public static PlayerState LockTarget(MusouSolid s, in PlayerState self, MusouRules rules, SimConfig c)
    {
        var (radius, _) = Body(rules, s.Type);
        float inset = MathF.Max(0f, radius - MathF.Max(c.CapsuleRadius, 0.05f));
        var at = s.Position;
        var toward = new Vector2(self.Position.X - at.X, self.Position.Y - at.Y);
        float length = toward.Length();
        if (inset > 0f && length > 1e-4f)
        {
            var step = toward / length * MathF.Min(inset, length);
            at += new Vector3(step.X, step.Y, 0f);
        }
        return new PlayerState(LockId(s.Id), at, Vector3.Zero, 0f, true) { Alive = true, Team = Team.None, Health = 1f };
    }
}
