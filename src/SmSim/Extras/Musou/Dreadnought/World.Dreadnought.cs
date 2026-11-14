using System.Numerics;
using SmSim.Extras.Musou;

namespace SmSim;

/// <summary>
/// Added content "Musou": the Dreadnought in the World (<see cref="DreadnoughtRules"/>; D1). Each Musou step (<see cref="DreadnoughtStep"/>) gives every living Dreadnought an entrance when he has none (he spawned, respawned or
/// took the class) and counts it down; while it runs everything he receives is multiplied by 0 (<see cref="DreadnoughtDamageTaken"/>, from <c>ExtrasDamageTaken</c>) and, for the first 4.833 s (the entrance clip), he cannot
/// move or fire (<see cref="DreadnoughtInputLocked"/>, from <c>ExtrasInputLocked</c>). Nothing of it is in the core snapshot; its wire is D2's, with the clip.
/// <para>
/// The hub rule (owner, 2026-11-14): "there should never be a dreadnaught in the thunderhawk troop bay". In the war's hub a Dreadnought pick plays as the Tactical (the placeholder Ultramarine: the Tactical's body, capsule,
/// health and kit; <see cref="DreadnoughtStandIn"/>), remembered in <see cref="_dreadnoughtWanted"/>; he becomes the Dreadnought at the landing (<see cref="World.TfwRespawnAt"/> outside the hub) and every war respawn starts
/// his entrance again. Outside a war (the 5-wave run has no hub) he is the Dreadnought from his spawn.
/// </para>
/// </summary>
public sealed partial class World
{
    /// <summary>The time left of a living Dreadnought's entrance (seconds; 0: over) by player id; a dead one or one of another class has none.</summary>
    readonly Dictionary<int, float> _dreadnoughtEntry = new();
    readonly List<int> _dreadnoughtGone = new();
    readonly Dictionary<int, DreadnoughtSprintGate> _dreadnoughtSprints = new();
    readonly List<int> _dreadnoughtLive = new();
    /// <summary>Per living Dreadnought: where his last footfall check stood and the ground left to cover before the next footfall's blast (D5).</summary>
    readonly Dictionary<int, (Vector3 Last, float Left)> _dreadnoughtStride = new();
    /// <summary>The players whose pick is the Dreadnought (they play the Tactical in the hub until the landing).</summary>
    readonly HashSet<int> _dreadnoughtWanted = new();

    /// <summary>Whether the war is in its hub (the troop bay): the only place a Dreadnought pick plays as a Tactical.</summary>
    bool DreadnoughtHub => _tfw is { Phase: SmSim.Extras.Musou.Campaign.TfwRunPhase.Hub };

    /// <summary>The class index of the Dreadnought in this config, or -1.</summary>
    int DreadnoughtClassIndex() => Config.Classes.ToList().FindIndex(c => c.Name == DreadnoughtRules.ClassName);

    /// <summary>The Tactical that stands in for the Dreadnought in the hub (the Space Marine Tactical), or -1.</summary>
    int DreadnoughtStandIn()
    {
        int t = Config.Classes.ToList().FindIndex(c => c.Name == "sm - tactical");
        return t >= 0 ? t : Config.Classes.ToList().FindIndex(c => c.Side == Team.SpaceMarines && c.Name != DreadnoughtRules.ClassName && c.Name != LibrarianRules.ClassName);
    }

    /// <summary>
    /// The state a respawn of player <paramref name="id"/> produces (<paramref name="next"/>: the state with its waiting loadout applied; <paramref name="chose"/>: that loadout named a class), by the hub rule: a Dreadnought pick in the hub
    /// is the Tactical's default loadout; outside it the remembered pick is the Dreadnought's. Every respawn starts the entrance again.
    /// </summary>
    PlayerState DreadnoughtAtRespawn(int id, PlayerState next, bool chose)
    {
        _dreadnoughtEntry.Remove(id);
        bool wanted = chose ? DreadnoughtRules.IsDreadnought(Config, next.Class) : _dreadnoughtWanted.Contains(id) || DreadnoughtRules.IsDreadnought(Config, next.Class);
        if (!wanted) { _dreadnoughtWanted.Remove(id); return next; }
        _dreadnoughtWanted.Add(id);
        int target = DreadnoughtHub ? DreadnoughtStandIn() : DreadnoughtClassIndex();
        if (target < 0 || target == next.Class) return next;
        var body = Config.For(target);
        return Loadouts.Choose(Config, next with { Class = target }, -1, -1, -1) with { Health = body.HealthMax, Shield = body.ShieldMax };
    }

    /// <summary>A living Dreadnought in the hub plays as the Tactical (a join, a pick, the return from a landing): remembered, re-armed with the Tactical's kit.</summary>
    void DreadnoughtHubStandIn(int id)
    {
        if (!DreadnoughtHub || !_players.TryGetValue(id, out var p) || !p.Alive || !DreadnoughtRules.IsDreadnought(Config, p.Class)) return;
        int t = DreadnoughtStandIn();
        if (t < 0) return;
        _dreadnoughtWanted.Add(id);
        _dreadnoughtEntry.Remove(id);
        var body = Config.For(t);
        _players[id] = Loadouts.Choose(Config, p with { Class = t }, -1, -1, -1) with { Health = body.HealthMax, Shield = body.ShieldMax };
        _musouArmed.Remove(id);
    }

    /// <summary>How much of the entrance's invulnerability player <paramref name="id"/> has left (0: none, or not a living Dreadnought).</summary>
    public float DreadnoughtEntryLeft(int id) => _dreadnoughtEntry.GetValueOrDefault(id);

    /// <summary>The capsule radius a player's body has beyond a marine's (a big body); 0 for every other class and without class bodies. The Orks' ring, reach and hold use it (<see cref="MusouTarget.Body"/>).</summary>
    float DreadnoughtBodyRing(in PlayerState p) =>
        Config.Bodies.Count == 0 ? 0f : MathF.Max(0f, Config.For(p.Class).CapsuleRadius - Config.CapsuleRadius);

    void DreadnoughtStep()
    {
        float dt = 1f / Math.Max(1, Config.TickRate);
        _dreadnoughtGone.Clear();
        foreach (int id in _respawns) _dreadnoughtEntry.Remove(id);   // the core's respawn after a death: a new life, a new entrance
        if (DreadnoughtHub) foreach (int id in _players.Keys.ToArray()) DreadnoughtHubStandIn(id);
        foreach (var id in _dreadnoughtEntry.Keys)
            if (!_players.TryGetValue(id, out var q) || !q.Alive || !DreadnoughtRules.IsDreadnought(Config, q.Class)) _dreadnoughtGone.Add(id);   // dead, left or changed class: the next spawn is a new entrance
        foreach (int id in _dreadnoughtGone) { _dreadnoughtEntry.Remove(id); _dreadnoughtStride.Remove(id); }
        _dreadnoughtLive.Clear();
        foreach (var (id, p) in _players) if (p.Alive && DreadnoughtRules.IsDreadnought(Config, p.Class)) _dreadnoughtLive.Add(id);   // a copy: the blasts below change players
        foreach (int id in _dreadnoughtLive)
        {
            var p = _players[id];
            if (!p.Alive) continue;
            if (!_dreadnoughtEntry.TryGetValue(id, out float left)) { _dreadnoughtEntry[id] = DreadnoughtRules.InvulnerableSeconds; continue; }   // enters this tick: the whole 5 s are still ahead
            if (left > 0f) _dreadnoughtEntry[id] = MathF.Max(0f, left - dt);
            DreadnoughtFootfalls(id, p);
            if (_players.TryGetValue(id, out var after)) DreadnoughtBuffRegen(id, after);
        }
    }

    /// <summary>
    /// A footfall's blast (D5): the data gives each foot's stomp 25 damage in a 10 m sphere (<c>evt_spawn_r_stomp</c>, <c>evt_spawn_l_stomp</c>) but not which footfalls fire it; **estimate**: one every
    /// <see cref="DreadnoughtRules.FootfallStride"/> metres of ground he walks (the walk cycle's 1.333 s at 3 m/s is two footfalls in 4 m), never while he stands, flies or is in the entrance. Melee damage,
    /// so Musou's melee kill credit and heal rules apply; a Musou blast never hurts a marine.
    /// </summary>
    void DreadnoughtFootfalls(int id, in PlayerState p)
    {
        if (!_dreadnoughtStride.TryGetValue(id, out var st)) { _dreadnoughtStride[id] = (p.Position, DreadnoughtRules.FootfallStride); return; }
        float moved = new Vector2(p.Position.X - st.Last.X, p.Position.Y - st.Last.Y).Length();
        if (!p.Grounded || moved > 3f) moved = 0f;   // airborne, or a teleport (a respawn)
        float left = st.Left - moved;
        if (left <= 0f)
        {
            ApplyBlast(id, 0, p.Position + new Vector3(0, 0, ChestOf(p)), DreadnoughtMelee.Footfall, melee: true);
            left += DreadnoughtRules.FootfallStride;
        }
        _dreadnoughtStride[id] = (p.Position, left);
    }

    /// <summary>Takes the Sprint bit off a living Dreadnought's input past his sprint limit (<see cref="DreadnoughtSprintGate"/>); the client applies the same rule before it sends.</summary>
    void DreadnoughtFilterInput(int id, ref PlayerInput input)
    {
        if (!_players.TryGetValue(id, out var p) || !p.Alive || !DreadnoughtRules.IsDreadnought(Config, p.Class)) { _dreadnoughtSprints.Remove(id); return; }
        if (!_dreadnoughtSprints.TryGetValue(id, out var gate)) _dreadnoughtSprints[id] = gate = new DreadnoughtSprintGate();
        gate.Apply(ref input, 1f / Math.Max(1, Config.TickRate));
    }

    /// <summary>The buff's second regeneration (D6; data <c>dread_buff</c>: regeneration x2): while his stim runs, past the regeneration delay, health comes back at one more regeneration rate.</summary>
    void DreadnoughtBuffRegen(int id, in PlayerState p)
    {
        if (p.StimLeft <= 0f || p.SinceDamage < Config.For(p.Class).HealthRegenDelay) return;
        var body = Config.For(p.Class);
        float health = MathF.Min(body.HealthMax, p.Health + body.HealthRegenRate * (DreadnoughtRules.BuffRegenMultiplier - 1f) / Math.Max(1, Config.TickRate));
        if (health > p.Health) _players[id] = p with { Health = health };
    }

    /// <summary>The entrance clip's part of his entrance: he cannot move or fire while it plays (the clip is shorter than the invulnerability by the difference), nor before his entrance has begun.</summary>
    void DreadnoughtInputLocked(int id, ref bool locked)
    {
        if (locked || !_players.TryGetValue(id, out var p) || !p.Alive || !DreadnoughtRules.IsDreadnought(Config, p.Class)) return;
        if (!_dreadnoughtEntry.TryGetValue(id, out float left) || left > DreadnoughtRules.InvulnerableSeconds - DreadnoughtRules.EntranceSeconds) locked = true;
    }

    /// <summary>Everything a Dreadnought receives is multiplied by 0 while his entrance runs (data <c>evt_spawn_in</c>); a Dreadnought with no entry yet (just spawned, before the step) is still entering.</summary>
    void DreadnoughtDamageTaken(int target, ref float scale)
    {
        if (!_players.TryGetValue(target, out var p) || !DreadnoughtRules.IsDreadnought(Config, p.Class)) return;
        if (!_dreadnoughtEntry.TryGetValue(target, out float left) || left > 0f) scale = 0f;
    }
}
