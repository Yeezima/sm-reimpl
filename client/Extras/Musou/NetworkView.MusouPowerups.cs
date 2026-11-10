using System;
using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// The Musou powerups on the client (owner, 2026-11-10; <see cref="MusouPowerupRules"/>): the own buffs as wire tag 6 lists them after the drops (each kind's end as a server tick), handed to
/// <list type="bullet">
/// <item>the prediction: at each reconcile the own record's <see cref="PlayerState.FireBoostLeft"/> is Rapid Fire's time left at that record's tick (<c>GameClient.ExtrasCarry</c>), so the own fire rate and
/// rounds are predicted as the server steps them;</item>
/// <item>the HUD's buff chips (<see cref="MusouHud"/>, MusouHud.Buffs.cs): icon, time left, a draining bar;</item>
/// <item>the own weapon in hand while Supercharged Melee works: a white-blue glow over it (ours).</item>
/// </list>
/// Fear and Double Damage need nothing more here: the Orks' flight is the server's, the damage too.
/// </summary>
public partial class NetworkView
{
    readonly uint[] _musouBuffEnd = new uint[5];
    bool _musouCarrySet;
    int _musouBuffsSeen;
    MeshInstance3D? _musouChargedMesh;
    Material? _musouChargedWas;
    static StandardMaterial3D? _musouChargedGlow;

    /// <summary>The own buffs from a tag 6 section of <paramref name="tick"/> (the newest list wins: a kind not listed has ended).</summary>
    void MusouBuffsReceived(MusouBuffRecord[] buffs, uint tick)
    {
        Array.Clear(_musouBuffEnd);
        foreach (var b in buffs)
        {
            int k = (int)b.Kind;
            if (k <= 0 || k >= _musouBuffEnd.Length) continue;
            if (_musouBuffEnd[k] == 0) _musouBuffsSeen++;
            _musouBuffEnd[k] = tick + b.TicksLeft;
        }
        if (!_musouCarrySet && _client is { } client)
        {
            _musouCarrySet = true;
            uint end(MusouPowerup k) => _musouBuffEnd[(int)k];
            float rate = Math.Max(1, _config?.TickRate ?? 30);
            client.ExtrasCarry = (record, at) =>
            {
                uint e = end(MusouPowerup.RapidFire);
                float left = record.Alive && e > at ? (e - at) / rate : 0f;
                return record with { FireBoostLeft = left };
            };
        }
    }

    /// <summary>Seconds <paramref name="kind"/> still works for the own player now (0: not active), on the prediction's clock.</summary>
    float MusouBuffLeft(MusouPowerup kind)
    {
        uint end = _musouBuffEnd[(int)kind];
        if (end == 0 || _client is not { } client || _config is null) return 0f;
        return end > client.Tick ? (end - client.Tick) / (float)Math.Max(1, _config.TickRate) : 0f;
    }

    /// <summary>Once a frame (after the drops): the HUD's chips and the Supercharged glow.</summary>
    void MusouPowerupsFrame(PlayerState me)
    {
        if (_musouHud is { } hud)
        {
            hud.Data.Buffs.Clear();
            if (me.Alive)
                foreach (var kind in MusouPowerupRules.All)
                    if (MusouBuffLeft(kind) is var left and > 0f) hud.Data.Buffs.Add((kind, left, MusouPowerupRules.Seconds(kind)));
        }
        MusouChargedGlow(me.Alive && MusouBuffLeft(MusouPowerup.Supercharged) > 0f);
    }

    /// <summary>The own melee weapon in hand glows while Supercharged Melee works (a power sword's own glow gives way to it while it lasts).</summary>
    void MusouChargedGlow(bool on)
    {
        var held = on ? _local?.Weapons?.MeleeMeshInHand : null;
        var mesh = held?.Mesh;
        if (!ReferenceEquals(mesh, _musouChargedMesh))
        {
            if (_musouChargedMesh is { } was && GodotObject.IsInstanceValid(was) && ReferenceEquals(was.MaterialOverlay, _musouChargedGlow)) was.MaterialOverlay = _musouChargedWas;
            _musouChargedMesh = mesh;
            _musouChargedWas = mesh?.MaterialOverlay;
        }
        if (mesh is null) return;
        _musouChargedGlow ??= new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, BlendMode = BaseMaterial3D.BlendModeEnum.Add, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = new Color(0.55f, 0.75f, 1f, 0.55f), CullMode = BaseMaterial3D.CullModeEnum.Back, DisableReceiveShadows = true, Grow = true, GrowAmount = 0.012f,
        };
        float pulse = 0.45f + 0.2f * MathF.Sin((float)_connectedFor * 9f);
        _musouChargedGlow.AlbedoColor = new Color(0.55f, 0.75f, 1f, pulse);
        if (!ReferenceEquals(mesh.MaterialOverlay, _musouChargedGlow)) mesh.MaterialOverlay = _musouChargedGlow;
    }

    /// <summary>The F1 line about the powerups.</summary>
    string MusouPowerupsInfoLine()
    {
        string active = "";
        foreach (var kind in MusouPowerupRules.All) if (MusouBuffLeft(kind) is var left and > 0f) active += $" {kind} {left:0}s";
        return $"Musou powerups: buffs seen {_musouBuffsSeen}{(active.Length > 0 ? ", active" + active : "")}";
    }
}
