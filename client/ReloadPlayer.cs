using System;
using System.Collections.Generic;
using Godot;
using SmContent;

namespace SmClient;

/// <summary>
/// One character's reload on screen (M35): the original reload clip laid over the movement with the original upper-body bone mask
/// (<see cref="CharacterViewer.SetPoseLayer"/>) at the time the reload has run, and the weapon's magazine leaving and returning
/// (<see cref="MagazineDrops"/>): the old magazine drops as a body of its own when the hand takes it, and the weapon shows its magazine again when the new
/// one is in. The clock, weight and magazine moments are <see cref="ReloadTimeline"/> (engine-free, unit-tested); this lays them on the engine's nodes.
/// The caller decides when a reload starts and ends (own: the simulation's reload timer; others: <see cref="ReloadInference"/> from their shots).
/// </summary>
public sealed class ReloadPlayer
{
    public ReloadTimeline Timeline { get; } = new();
    bool _hidden;

    public bool Active => Timeline.Active;
    public bool Shown => Timeline.Shown;
    public int Serial => Timeline.Serial;
    public float Clock => Timeline.Clock;
    public float Total => Timeline.Total;
    public float Weight => Timeline.Weight;
    public float ClipFraction => Timeline.ClipFraction;
    public ReloadClip? Clip => Timeline.Clip;

    /// <summary>
    /// Starts a reload; one still under way is replaced, and its magazine shown again first (<paramref name="rig"/>), so nothing stays hidden.
    /// <paramref name="hand"/>: the measured hand timing (<see cref="ReloadHandProbe"/>) or null.
    /// </summary>
    public void Begin(ReloadClip clip, float total, float elapsed, WeaponRig? rig = null, (float Arrive, float Leave, string Hand, float Closest, float Furthest)? hand = null)
    {
        RestoreMagazine(rig);
        Timeline.Begin(clip, total, elapsed, hand?.Leave, hand?.Arrive);
    }

    public void Advance(float delta, float? target, float tick) => Timeline.Advance(delta, target, tick);
    public void Finish(bool completed) => Timeline.Finish(completed);

    /// <summary>Drops everything at once; the weapons' magazines are shown again when a rig is given.</summary>
    public void Reset(WeaponRig? rig = null) { Timeline.Reset(); RestoreMagazine(rig); }

    /// <summary>Shows the magazines this player's reload hid (a reload replaced, ended or cancelled; the player died).</summary>
    public void RestoreMagazine(WeaponRig? rig)
    {
        if (_hidden) rig?.ShowMagazines();
        _hidden = false;
    }

    /// <summary>Lays the reload clip over the character (call after its locomotion pose is placed); false when nothing is laid.</summary>
    public bool ApplyPose(CharacterViewer character)
    {
        if (!Shown || Timeline.Clip is not { BoneMask: not null } c || Weight <= 1e-3f) return false;
        return character.SetPoseLayer(c.Clip, ClipFraction * c.ClipLength, Weight, additive: false, c.BoneMask);
    }

    /// <summary>
    /// The magazine, once per frame: when the hand takes it the weapon in hand loses it and a copy falls from where it is (seeded
    /// with the carrier's <paramref name="velocity"/>, Godot world space); when the new one is in, or the reload ends or is cancelled, the weapon
    /// shows it again. Returns true in the frame a magazine was dropped.
    /// </summary>
    public bool ApplyMagazine(WeaponRig? rig, MagazineDrops? drops, Vector3 velocity)
    {
        if (rig is null) { _hidden = false; return false; }
        bool dropped = false;
        if (Active)
        {
            if (Timeline.TakeMagazineOut() && rig.TryMagazine(out var magazine, out var world))
            {
                drops?.Drop(magazine, world, velocity);
                rig.SetMagazineVisible(false);
                _hidden = true;
                dropped = drops is not null;
            }
            if (Timeline.TakeMagazineIn()) RestoreMagazine(rig);
        }
        else RestoreMagazine(rig);
        return dropped;
    }
}

/// <summary>
/// Dropped magazines (M35): each is a copy of the weapon model's own magazine mesh (<see cref="WeaponMagazine"/>) at the transform it had
/// in the weapon, a <see cref="RigidBody3D"/> with a box the size of the mesh, falling and bouncing on the level (the same static world the
/// death ragdolls lie on) and removed after <see cref="LifetimeSeconds"/>; at most <see cref="MaxLive"/> exist at once. Client-only decoration.
/// **Ours**, all of the physics values: the original's magazine drop (if any) is not decoded.
/// </summary>
public sealed partial class MagazineDrops : Node3D
{
    public const int MaxLive = 24;
    public const float LifetimeSeconds = 10f;
    /// <summary>Mass (kg), friction, bounce, damping: an empty bolter magazine is light and clatters.</summary>
    public const float Mass = 0.4f, Friction = 0.7f, Bounce = 0.3f, LinearDamp = 0.05f, AngularDamp = 0.6f;
    /// <summary>Largest sideways kick (m/s) and spin (rad/s) added to the carrier's velocity.</summary>
    public const float KickSpeed = 0.6f, SpinSpeed = 5f;

    readonly List<(RigidBody3D Body, float Age)> _live = new();
    int _seed = 7;
    public int Live => _live.Count;
    public int Spawned { get; private set; }
    public int Removed { get; private set; }

    /// <summary>A magazine falls from <paramref name="world"/> (the transform of its node in the weapon, scale included) with the carrier's <paramref name="velocity"/> and a small random kick and spin.</summary>
    public RigidBody3D Drop(WeaponMagazine magazine, Transform3D world, Vector3 velocity)
    {
        while (_live.Count >= MaxLive) Remove(0);
        var rng = new Random(_seed++);
        var scale = world.Basis.Scale;
        var body = new RigidBody3D
        {
            Name = "Magazine" + Spawned, Mass = Mass, CollisionLayer = 0, CollisionMask = Ragdoll.WorldLayer, ContinuousCd = true, CanSleep = true,
            LinearDamp = LinearDamp, AngularDamp = AngularDamp, PhysicsMaterialOverride = new PhysicsMaterial { Friction = Friction, Bounce = Bounce },
        };
        body.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = magazine.Mesh, Scale = scale });
        body.AddChild(new CollisionShape3D { Name = "Box", Shape = new BoxShape3D { Size = new Vector3(magazine.Size.X * MathF.Abs(scale.X), magazine.Size.Y * MathF.Abs(scale.Y), magazine.Size.Z * MathF.Abs(scale.Z)) } });
        AddChild(body);
        body.GlobalTransform = new Transform3D(world.Basis.Orthonormalized(), world.Origin);
        float R() => (float)(rng.NextDouble() * 2.0 - 1.0);
        body.LinearVelocity = velocity + new Vector3(R(), 0.2f * R(), R()) * KickSpeed;
        body.AngularVelocity = new Vector3(R(), R(), R()) * SpinSpeed;
        _live.Add((body, 0f));
        Spawned++;
        return body;
    }

    void Remove(int index)
    {
        var body = _live[index].Body;
        _live.RemoveAt(index);
        Removed++;
        if (IsInstanceValid(body)) { RemoveChild(body); body.QueueFree(); }
    }

    public override void _Process(double delta)
    {
        for (int i = _live.Count - 1; i >= 0; i--)
        {
            var (body, age) = _live[i];
            age += (float)delta;
            _live[i] = (body, age);
            if (age >= LifetimeSeconds || !IsInstanceValid(body) || body.GlobalPosition.Y < Ragdoll.LostBelowY) Remove(i);
        }
    }

    /// <summary>Removes every magazine (the view is torn down).</summary>
    public void Clear() { while (_live.Count > 0) Remove(_live.Count - 1); }
}
