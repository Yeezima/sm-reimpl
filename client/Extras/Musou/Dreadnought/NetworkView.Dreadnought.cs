using System;
using System.Collections.Generic;
using Godot;
using SmClient.Looks;
using SmContent.Extras.Musou;
using SmNet;
using SmSim;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// The Musou Dreadnought on screen (D2; docs/musou-dreadnought-design.md section 6; added content, Musou only): a player of the Dreadnought class wears the install's model on its own rig
/// (<see cref="DreadnoughtLooks"/>; until it is built, or when the install cannot give it, the Tactical stands in) and is posed from the snapshot alone: the entrance clip when a life begins (the client counts
/// the same 4.833 s the sim's input lock does), the eight-way walk and run clips by speed with the aim pitch laid over them, the sprint clip, the idle, and the death clip held. Which clip plays is **ours**
/// (provisional approximation; the behaviour graph is not evaluated). Strikes, fire and reload poses are added by the later milestones.
/// </summary>
public partial class NetworkView
{
    const string DreadnoughtKey = "dreadnought";
    DreadnoughtLooks? _dreadLooks;
    readonly Dictionary<int, DreadnoughtTrack> _dreadTracks = new();

    sealed class DreadnoughtTrack { public bool WasAlive; public double Born = double.NegativeInfinity, Died = double.NegativeInfinity; }

    bool IsDreadnoughtClass(int playerClass) => _config is not null && DreadnoughtRules.IsDreadnought(_config, playerClass);
    static bool IsDreadnoughtCharacter(CharacterViewer c) => c.HasMeta("look_key") && c.GetMeta("look_key").AsString() == DreadnoughtKey;

    DreadnoughtTrack DreadTrack(int id) { if (!_dreadTracks.TryGetValue(id, out var t)) _dreadTracks[id] = t = new DreadnoughtTrack(); return t; }

    partial void ExtrasEnsureModel(CharacterViewer? current, int playerId, bool local, PlayerState state, ref CharacterViewer? result)
    {
        bool want = IsDreadnoughtClass(state.Class);
        if (!want)
        {
            // A Dreadnought turned back into a marine (the hub's Tactical stand-in): the rig is another one, so it is dropped for the placeholder and the usual look swap follows.
            if (current is not null && IsDreadnoughtCharacter(current) && _assets is not null) { current.QueueFree(); result = Spawn(playerId, local, _assets, LookKeys.Placeholder); }
            return;
        }
        if (current is not null && IsDreadnoughtCharacter(current)) { result = current; return; }
        _dreadLooks ??= new DreadnoughtLooks(_gameDir);
        bool wasDone = _dreadLooks.Done;
        _dreadLooks.Step();
        if (!wasDone && _dreadLooks.Done)
        {
            foreach (var n in _dreadLooks.Notes) { _notes.Add(n); GD.Print("  note: " + n); }
            _dreadLooks.Notes.Clear();
        }
        if (_dreadLooks.Assets is { } assets)
        {
            current?.QueueFree();
            result = Spawn(playerId, local, assets, DreadnoughtKey);
            if (result.GetNodeOrNull<Label3D>("Nameplate") is { } plate) plate.Position = new Vector3(0, 0, 4.2f);
        }
        // not built yet (or failed): result stays null and the usual path shows the Tactical stand-in
    }

    partial void ExtrasPlace(CharacterViewer character, PlayerState state, bool own, ref bool handled)
    {
        if (!IsDreadnoughtCharacter(character)) return;
        handled = true;
        var t = DreadTrack(state.PlayerId);
        if (state.Alive && !t.WasAlive) t.Born = _connectedFor;
        if (!state.Alive && t.WasAlive) t.Died = _connectedFor;
        t.WasAlive = state.Alive;
        character.ClearPoseLayers();
        character.ClearTurnPose();
        float yaw = own ? DisplayYaw(state) : state.Yaw;
        character.Transform = new Transform3D(new Basis(new Vector3(0, 0, 1), yaw), new Vector3(state.Position.X, state.Position.Y, state.Position.Z));
        if (character.GetNodeOrNull<Node3D>("ChargeGlow") is { } glow) glow.Visible = false;
        character.Visible = true;
        if (!state.Alive) { character.Pose(DreadnoughtRig.Death, (float)Math.Min(_connectedFor - t.Died, DreadnoughtRig.DeathSeconds), 0.2f); return; }
        double since = _connectedFor - t.Born;
        if (since < DreadnoughtRig.EntranceSeconds) { character.Pose(DreadnoughtRig.Entrance, (float)since, 0.1f); return; }
        DreadnoughtLocomotion(character, state);
    }

    /// <summary>The walk, run, sprint or idle clip for the state (**ours**: eight sectors off the facing, walk below <see cref="DreadnoughtRig.RunFrom"/>, rate from the speed, the aim pitch across the down / level / up clips).</summary>
    void DreadnoughtLocomotion(CharacterViewer character, PlayerState s)
    {
        float vx = s.Velocity.X, vy = s.Velocity.Y;
        float speed = MathF.Sqrt(vx * vx + vy * vy);
        float fx = -MathF.Sin(s.Yaw), fy = MathF.Cos(s.Yaw);
        float forward = vx * fx + vy * fy, right = vx * fy - vy * fx;
        bool melta = s.Slot == WeaponSlot.Pistol;
        float pitchT = Math.Clamp(s.Pitch / 0.9f, -1f, 1f);   // radians; ~50 degrees is the full clip
        if (speed > 0.4f)
        {
            if (speed > DreadnoughtRules.RunSpeed + 1.5f && forward > 0f)
            {
                character.Stance("dn:sprint", null, DreadnoughtRig.Sprint, null, 0f, Math.Clamp(speed / DreadnoughtRules.SprintSpeed, 0.6f, 1.4f), 0.25f);
                return;
            }
            string dir = DreadnoughtRig.Direction(MathF.Atan2(right, forward) * 180f / MathF.PI);
            bool run = speed > DreadnoughtRig.RunFrom;
            string key = $"dn:{(run ? "run" : "walk")}:{dir}:{(melta ? "l" : "a")}";
            character.Stance(key, DreadnoughtRig.Move(run, dir, melta, -1), DreadnoughtRig.Move(run, dir, melta, 0), DreadnoughtRig.Move(run, dir, melta, 1), pitchT,
                speed / (run ? DreadnoughtRig.RunCycleSpeed : DreadnoughtRig.WalkCycleSpeed), 0.2f);
            return;
        }
        character.Stance(melta ? "dn:idle:l" : "dn:idle:a", DreadnoughtRig.Stand(melta, -1), DreadnoughtRig.Stand(melta, 0), DreadnoughtRig.Stand(melta, 1), pitchT, 1f, 0.25f);
    }

    /// <summary>A Dreadnought's death is his clip, held where it ends; no ragdoll (design section 3.2: "no ragdoll").</summary>
    partial void ExtrasDeath(CharacterViewer character, ref bool handled)
    {
        if (IsInstanceValid(character) && IsDreadnoughtCharacter(character)) handled = true;
    }
}
