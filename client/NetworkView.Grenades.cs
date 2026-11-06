using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmNet;

namespace SmClient;

/// <summary>
/// feel/grenades-quickdraw (the owner's request of 2026-11-06: "make the frag and blind grenades match the original game's models"): a thrown item is drawn with the model its projectile
/// blueprint names (<c>extensions/renderable_ext</c> in <c>data/packages/ebps.oppc</c>, <see cref="WeaponModels.ReadProjectileVisuals"/>): the frag grenade's <c>frag_grenade/geo_frag_grenade</c>,
/// the blind grenade's (the files' krak) <c>grenade_guardsman/pp_grenade_lodh</c>; **original asset loaded**. The blueprints are read in the background when the match view starts and the meshes
/// built during the map load (<see cref="PrewarmGrenades"/>); until then, or for an item without a model, the M13 placeholder sphere. The model rolls with its travel (ours: the original's
/// rigid body spins under Havok, not simulated here) and lies still once the grenade rests. <c>--placeholder-grenades</c> keeps the spheres.
/// </summary>
public partial class NetworkView
{
    Task<IReadOnlyDictionary<string, ProjectileVisual>>? _grenadeVisualLoad;
    readonly Dictionary<int, (Mesh Mesh, Material? Material)?> _grenadeModels = new();
    readonly Dictionary<int, (Vector3 At, Basis Turn)> _grenadeTumble = new();
    bool _grenadeModelsNoted;

    /// <summary>Test and comparison aid: the M13 placeholder spheres instead of the original grenade models.</summary>
    static bool PlaceholderGrenades => _placeholderGrenades ??= OS.GetCmdlineUserArgs().Contains("--placeholder-grenades");
    static bool? _placeholderGrenades;

    /// <summary>Whether item <paramref name="index"/>'s look is final (its model decided, or none will come): the placeholder sphere may then be cached.</summary>
    bool GrenadeModelSettled(int index) => PlaceholderGrenades || _grenadeVisualLoad is null || _weaponAssets is null || _grenadeModels.ContainsKey(index);

    /// <summary>Radians a grenade model turns per metre it travels (ours: about a 0.1 m radius rolling).</summary>
    const float GrenadeRollPerMetre = 10f;

    /// <summary>Starts reading the thrown items' projectile visuals (every item of the config) in the background.</summary>
    void SetUpGrenadeModels()
    {
        if (_config is null || PlaceholderGrenades || _grenadeVisualLoad is not null) return;
        var blueprints = _config.Thrown.Select(t => t.Projectile.Blueprint).Where(b => b.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (blueprints.Count == 0) return;
        string game = _gameDir;
        _grenadeVisualLoad = Task.Run(() =>
        {
            var notes = new List<string>();
            var visuals = WeaponModels.ReadProjectileVisuals(game, blueprints, notes);
            foreach (var n in notes) GD.Print($"grenade models: {n}");
            return visuals;
        });
    }

    /// <summary>The visuals once read (null while reading, or when the read failed: noted once).</summary>
    IReadOnlyDictionary<string, ProjectileVisual>? GrenadeVisuals()
    {
        if (_grenadeVisualLoad is not { IsCompleted: true } load) return null;
        if (load.IsCompletedSuccessfully) return load.Result;
        if (!_grenadeModelsNoted) { _grenadeModelsNoted = true; GD.PrintErr($"grenade models: the blueprint read failed ({load.Exception?.GetBaseException().Message}); placeholder spheres"); }
        return null;
    }

    /// <summary>
    /// The original model of thrown item <paramref name="index"/> (<see cref="SmSim.SimConfig.Thrown"/>) with its own materials (no override), or null for the placeholder: still loading
    /// (asked again next frame), no model, or the aid <c>--placeholder-grenades</c>.
    /// </summary>
    (Mesh Mesh, Material? Material)? GrenadeModel(int index)
    {
        if (PlaceholderGrenades || _config is null || index < 0 || index >= _config.Thrown.Count || _weaponAssets is null) return null;
        if (_grenadeModels.TryGetValue(index, out var known)) return known;
        if (GrenadeVisuals() is not { } visuals)
        {
            if (_grenadeVisualLoad is { IsCompleted: true } || _grenadeVisualLoad is null) _grenadeModels[index] = null;   // failed or never started: the sphere from now on
            return null;
        }
        var item = _config.Thrown[index];
        string leaf = item.Projectile.Blueprint.Split('\\', '/')[^1];
        (Mesh, Material?)? result = null;
        if (visuals.TryGetValue(leaf, out var v))
        {
            var attachment = new WeaponAttachment(item.Name, leaf, "thrown", v.VisualPackage, v.VisualName, null, null, false, [], []) { IsWorn = false };
            if (_weaponAssets.Get(attachment)?.Mesh is { } mesh) result = (mesh, null);
        }
        _grenadeModels[index] = result;
        GD.Print($"grenade models: {item.PlayerName ?? item.Name} ({leaf}) drawn {(result is null ? "as the placeholder sphere" : $"with its original model {v!.VisualPackage}/{v.VisualName}")}");
        return result;
    }

    /// <summary>Builds every thrown item's model during the map load (waits at most <paramref name="waitMs"/> for the blueprint read).</summary>
    void PrewarmGrenades(int waitMs = 1500)
    {
        if (_grenadeVisualLoad is null || _config is null || Lean) return;
        try { _grenadeVisualLoad.Wait(waitMs); } catch (AggregateException) { }
        if (!_grenadeVisualLoad.IsCompleted) { GD.Print($"grenade models: not warmed (the blueprint read still runs after {waitMs} ms); built at the first throw"); return; }
        for (int i = 0; i < _config.Thrown.Count; i++) GrenadeModel(i);
    }

    /// <summary>Turns a grenade model about the horizontal axis across its travel by the distance it moved since the last frame (still when it rests); the spheres are left as they are.</summary>
    void GrenadeTumble(MeshInstance3D node, ProjectileView q, bool model)
    {
        if (!q.Thrown || !model) { if (node.Basis != Basis.Identity) node.Basis = Basis.Identity; return; }
        var at = node.Position;
        if (!_grenadeTumble.TryGetValue(q.Id, out var last))
        {
            // A first turn from the id (golden angle steps), so two grenades do not lie alike.
            last = (at, new Basis(Vector3.Up, (q.Id * 2.39996323f) % MathF.Tau) * new Basis(Vector3.Right, (q.Id * 1.3f) % MathF.Tau));
        }
        var moved = at - last.At;
        var flat = new Vector3(moved.X, moved.Y, 0f);   // game space: Z up
        var turn = last.Turn;
        float distance = moved.Length();
        if (distance > 1e-4f && distance < 20f)
        {
            var axis = flat.LengthSquared() > 1e-8f ? new Vector3(-flat.Y, flat.X, 0f).Normalized() : Vector3.Right;
            turn = new Basis(axis, distance * GrenadeRollPerMetre) * turn;
            turn = turn.Orthonormalized();
        }
        _grenadeTumble[q.Id] = (at, turn);
        node.Basis = turn;
    }

    /// <summary>A projectile gone: its tumble is forgotten and the pooled node's turn reset.</summary>
    void GrenadeForget(int id, MeshInstance3D node)
    {
        _grenadeTumble.Remove(id);
        node.Basis = Basis.Identity;
    }
}
