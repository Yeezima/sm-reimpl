using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmNet.Extras.Musou;
using SmSim.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Expansion S3's effects (Musou Mode only; <see cref="MusouStormboy"/>, <see cref="MusouBigShoota"/>). Everything played is the data's, by name or through the effect catalogue, read at run time:
/// <list type="bullet">
/// <item>A Stormboy's hop: the loyalist jump pack's lift-off effects (<c>sm_mp_abil_jumppack</c>'s <c>action_ascending</c>, as the players' packs play them) at his back, following him through the arc,
/// and its lift-off sound; his landing the pack's light landing (<c>jumppack_land_light</c>).</item>
/// <item>A crash (shot down): the frag grenade's own explosion (<c>sm_mp_eq_frag_grenade</c>'s projectile, its detonation and its material container) and the heavy landing's crater (<c>jumppack_land_heavy</c>).</item>
/// <item>The Big Shoota Nob's overheat: the plasma gun's own <c>overheat</c> vent (<c>sm_mp_ranged_plasma_gun</c>) at his chest, for as long as it lasts.</item>
/// </list>
/// **Ours**: which original effects stand for these (the Orks' own jump packs and big shoota have no co-op effect data we read), where on the body they play, and the placeholder pack: when the install cannot give
/// the campaign's <c>boy_stormboy</c> body (<see cref="StormboyLooks"/>), each Stormboy is a Boy with a drawn box-and-tubes jump pack on his back (*placeholder demonstration*).
/// </summary>
public partial class NetworkView
{
    const string StormPack = "sm_mp_abil_jumppack", StormFrag = "sm_mp_eq_frag_grenade", StormSteamGun = "sm_mp_ranged_plasma_gun";
    /// <summary>Where the pack's nozzles sit on a Stormboy, in his own space (m: behind, up), and the steam on a Big Shoota Nob's chest (ours).</summary>
    const float StormPackBack = 0.35f, StormPackUp = 1.3f, ShootaSteamUp = 1.5f;

    readonly Dictionary<ushort, MeshInstance3D> _stormPacks = new();
    readonly Dictionary<ushort, List<FxEffect>> _stormThrust = new();
    readonly Dictionary<ushort, List<FxEffect>> _shootaSteam = new();
    readonly Dictionary<ushort, Num.Vector3> _stormAt = new();
    readonly HashSet<ushort> _stormSeen = new();
    Mesh? _stormPackMesh;
    int _stormHops, _stormCrashes, _shootaVents;

    /// <summary>A cue of expansion S3 (a <see cref="MusouEventKind.Telegraph"/> with one of their values): its effects and sound. False for any other.</summary>
    bool MusouStormboyCue(in MusouEvent e, IReadOnlyList<MusouShown> shown)
    {
        if (e.Value < MusouStormboy.HopCue || e.Value > MusouBigShoota.VentCue || _fx is null) return false;
        var up = Basis.Identity;
        switch (e.Value)
        {
            case MusouStormboy.HopCue:
                _stormHops++;
                StormThrust(e.Enemy, shown);
                if (_soundTriggers?.File(StormPack) is { } pack && MusouFind(shown, e.Enemy) is var i and >= 0)
                {
                    var emitter = new SoundEmitter { Position = ToGodot(shown[i].Position) + Vector3.Up * StormPackUp, OneShot = true, Label = "musou stormboy hop", Group = MusouSoundEmitters };
                    PostAll(pack.Of("action_ascending"), emitter, false, "stormboy hop");
                }
                break;
            case MusouStormboy.LandCue:
                StormThrustEnd(e.Enemy);
                _fx.Play("jumppack_land_light", ToGodot(e.Vector) + Vector3.Up * 0.05f, up, 0.6f);
                break;
            case MusouStormboy.CrashCue:
                _stormCrashes++;
                StormThrustEnd(e.Enemy);
                StormCrash(e.Vector);
                break;
            case MusouBigShoota.OverheatCue:
                _shootaVents++;
                ShootaSteam(e.Enemy, e.Vector, shown);
                break;
            case MusouBigShoota.VentCue:
                if (_shootaSteam.Remove(e.Enemy, out var steam)) foreach (var s in steam) { s.Repeat = false; s.Life = Math.Min(s.Life, Math.Max(0.0, s.Age) + 0.4); }
                break;
        }
        return true;
    }

    /// <summary>The pack's lift-off effects on a Stormboy's back, following him (the shown position) until he lands, crashes or is gone.</summary>
    void StormThrust(ushort id, IReadOnlyList<MusouShown> shown)
    {
        StormThrustEnd(id);
        if (_catalogue?.JumpPack(StormPack) is not { } effects) return;
        var started = new List<FxEffect>();
        foreach (var p in effects.Ascending)
        {
            if (StormNozzle(id) is not { } at) break;
            Func<(Vector3, Basis)?> follow = () => StormNozzle(id);
            if (_fx!.Play(p.Name, at.Position, at.Axes, (p.Scale <= 0f ? 1f : p.Scale) * 0.8f, null, follow, 0f, 0.0, repeat: true, priority: 1) is { } fx)
            {
                fx.TrailScale = PackTrailShare;
                started.Add(fx);
            }
        }
        if (started.Count > 0) _stormThrust[id] = started;
    }

    void StormThrustEnd(ushort id)
    {
        if (_stormThrust.Remove(id, out var list)) Fade(list, JumpFxFade);
    }

    /// <summary>A Stormboy's nozzles as last drawn: behind his back at shoulder height, pointing down (null: he is not shown).</summary>
    (Vector3 Position, Basis Axes)? StormNozzle(ushort id)
    {
        if (!_stormAt.TryGetValue(id, out var feet)) return null;
        return (ToGodot(feet), Basis.Identity.Rotated(Vector3.Right, MathF.PI));
    }

    /// <summary>A crash: the frag grenade's explosion where he struck the ground (its detonation effects and its material container's), the heavy landing's crater, and a shake nearby.</summary>
    void StormCrash(Num.Vector3 at)
    {
        var pos = ToGodot(at);
        bool played = false;
        if (_catalogue?.Thrown.GetValueOrDefault(StormFrag)?.Projectile is { } frag)
        {
            var (point, normal, surface) = DetonationSurface(at);
            var (plays, byMaterial) = _catalogue.DetonationPlays(frag, surface, OldPlasmaBlast);
            foreach (var p in plays) { _fx!.Play(p.Name, pos + Vector3.Up * 0.05f, Basis.Identity, p.Scale <= 0f ? 1f : p.Scale, null, null, p.Lifetime, 0.0, false, 2); played = true; }
            foreach (var p in byMaterial)
                _fx!.Play(p.Name, normal is { } n ? point + n * 0.02f : pos + Vector3.Up * 0.05f, normal is { } sn ? AxesFromY(sn) : Basis.Identity, p.Scale <= 0f ? 1f : p.Scale, null, null, p.Lifetime, 0.0, false, 2);
        }
        if (!played) _fx!.Play(WeirdboyBurstEffect, pos + Vector3.Up * 0.5f, Basis.Identity, 0.8f, priority: 2);   // no frag data: the plasma blast stands in
        _fx!.Play("jumppack_land_heavy", pos + Vector3.Up * 0.05f, Basis.Identity, 1f, priority: 2);
        if (_client is { } client)
        {
            float distance = ToGodot(client.Predicted.Position).DistanceTo(pos);
            bool feel = true;
            ExtrasBlastFeel(null, distance, ref feel);   // the weight pass's thinning (Weight/NetworkView.MusouImpact.cs)
            if (feel && distance < 25f) FxShake(Math.Clamp(1f - distance / 25f, 0f, 1f) * 0.9f * 0.012f * 3f);
        }
    }

    /// <summary>The overheat's steam on a Big Shoota Nob: the plasma gun's own vent effects at his chest, following him, until his gun is ready again.</summary>
    void ShootaSteam(ushort id, Num.Vector3 chest, IReadOnlyList<MusouShown> shown)
    {
        if (_shootaSteam.Remove(id, out var old)) Fade(old, 0.0);
        var plays = _catalogue?.Weapon(StormSteamGun)?.Bindings.Where(b => b.IsDefault && b.Event == "overheat" && b.Play is { Name.Length: > 0 }).Select(b => b.Play!).ToList();
        var list = new List<FxEffect>();
        Func<(Vector3, Basis)?> follow = () => MusouFind(shown, id) is var i and >= 0 ? (ToGodot(shown[i].Position + new Num.Vector3(0f, 0f, ShootaSteamUp)), Basis.Identity) : null;
        if (plays is { Count: > 0 })
            foreach (var p in plays)
                if (_fx!.Play(p.Name, ToGodot(chest), Basis.Identity, (p.Scale <= 0f ? 1f : p.Scale) * 1.6f, null, null, 0f, 0.0, repeat: true, priority: 1) is { } fx) list.Add(fx);
        if (list.Count == 0 && _fx!.Play("weirdboy_shield_pulse", ToGodot(chest), Basis.Identity, 0.6f, repeat: true) is { } stand) list.Add(stand);   // no plasma data: a stand-in puff
        foreach (var fx in list) fx.Life = MusouBigShoota.OverheatSeconds + 0.5;
        _shootaSteam[id] = list;
    }

    /// <summary>
    /// Each frame: where every Stormboy is drawn (the thrust follows it) and, while the Stormboy has no model of his own, a placeholder jump pack on each one's back; the effects of those gone are ended.
    /// </summary>
    void MusouStormboyFrame(IReadOnlyList<MusouShown> shown)
    {
        _stormSeen.Clear();
        bool placeholder = _musouLooks is { StormboyOwnLook: false };
        foreach (var s in shown)
        {
            if (s.Type != MusouEnemyType.Stormboy) continue;
            _stormSeen.Add(s.Id);
            var back = new Num.Vector3(MathF.Sin(s.Yaw), -MathF.Cos(s.Yaw), 0f) * StormPackBack;
            _stormAt[s.Id] = s.Position + back + new Num.Vector3(0f, 0f, StormPackUp);
            if (!placeholder || _gameSpace is null) continue;
            if (!_stormPacks.TryGetValue(s.Id, out var pack))
            {
                pack = new MeshInstance3D { Name = "StormPack", Mesh = _stormPackMesh ??= StormPackMesh() };
                _gameSpace.AddChild(pack);
                _stormPacks[s.Id] = pack;
            }
            pack.Visible = s.State != MusouEnemyState.Dying;
            pack.Transform = new Transform3D(new Basis(Vector3.Back, s.Yaw), new Vector3(_stormAt[s.Id].X, _stormAt[s.Id].Y, _stormAt[s.Id].Z - 0.25f));
        }
        foreach (var id in _stormAt.Keys.Where(k => !_stormSeen.Contains(k)).ToList())
        {
            _stormAt.Remove(id);
            StormThrustEnd(id);
            if (_stormPacks.Remove(id, out var pack)) pack.QueueFree();
        }
        foreach (var id in _shootaSteam.Keys.Where(k => MusouFind(shown, k) < 0).ToList()) if (_shootaSteam.Remove(id, out var steam)) Fade(steam, 0.0);
    }

    /// <summary>The placeholder pack, in game axes (Z up, the wearer facing +Y): a box 0.5 x 0.3 x 0.6 m with two tubes under it, dark iron with rust-red tubes. *Placeholder demonstration*.</summary>
    static Mesh StormPackMesh()
    {
        var mesh = new ArrayMesh();
        var iron = new StandardMaterial3D { AlbedoColor = new Color(0.22f, 0.21f, 0.2f), Metallic = 0.6f, Roughness = 0.55f };
        var rust = new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.16f, 0.08f), Metallic = 0.4f, Roughness = 0.6f };
        void Add(PrimitiveMesh p, Transform3D t, Material m)
        {
            var arrays = p.GetMeshArrays();
            var v = (Vector3[])arrays[(int)Mesh.ArrayType.Vertex];
            var n = (Vector3[])arrays[(int)Mesh.ArrayType.Normal];
            for (int k = 0; k < v.Length; k++) { v[k] = t * v[k]; n[k] = (t.Basis * n[k]).Normalized(); }
            arrays[(int)Mesh.ArrayType.Vertex] = v; arrays[(int)Mesh.ArrayType.Normal] = n;
            arrays[(int)Mesh.ArrayType.Tangent] = default(Variant);
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, m);
        }
        // The box in game axes as it is; Godot's cylinders stand along Y, so they are turned a quarter round X to stand along the game's Z.
        var upright = new Basis(Vector3.Right, MathF.PI / 2f);
        Add(new BoxMesh { Size = new Vector3(0.5f, 0.3f, 0.6f) }, Transform3D.Identity, iron);
        foreach (float side in new[] { -0.15f, 0.15f })
            Add(new CylinderMesh { TopRadius = 0.08f, BottomRadius = 0.11f, Height = 0.45f }, new Transform3D(upright, new Vector3(side, 0f, -0.45f)), rust);
        return mesh;
    }

    /// <summary>For the 10 s log line.</summary>
    string MusouStormboyNote() => $"stormboy hops {_stormHops}, crashes {_stormCrashes}, packs drawn {_stormPacks.Count}{(_musouLooks is { StormboyOwnLook: false } ? " (placeholder)" : "")}, big shoota vents {_shootaVents}";
}
