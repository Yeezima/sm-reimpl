using System;
using System.Collections.Generic;
using Godot;
using SmClient.ImpactRules;
using SmNet;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// classic-hit-effects, the Extended profile's blood (added content: gore; owner 2026-11-23, question card "Extended only"; removable with this folder, the core hooks stay empty):
/// <list type="bullet">
/// <item><see cref="ExtrasClassicBlood"/>: the red spray (<see cref="ClassicBloodRibbons"/>, third design after owner answer 4, 2026-11-24: soft ribbons laid along the weapon's path
/// through the contact, or along the shot or the charge, that hang in place and fade over about a second; today's Classic health-hit red); heavier and lethal hits lay more ribbons, a
/// little wider and longer, without filling the shape in.</item>
/// <item>The fatal gunfire headshot's wall decal: the server's headshot kill (<see cref="ExtrasClassicKill"/>) and the head shot that killed (the confirmed lethal head shot,
/// <see cref="ExtrasClassicFatalHeadshot"/>, or else the killer's latest drawn head hit on the victim, <see cref="ExtrasClassicHeadContact"/>: the decal no longer depends on the lethal mark
/// pairing with its shot; review of 2b89de15, finding 3), within <see cref="HeadshotPairSeconds"/>, place one decal where the shot's line, extended from the head contact, first meets the
/// level, if that is a wall within 5 m and nothing else (another body) is in the way (<see cref="ClassicBloodRules.DecalTrace"/>). The splash is made in code from the kill's sequence and
/// the victim (<see cref="ClassicBloodRules.DecalShape"/>), so every view draws the same one; it shares the effect player's alpha-decal cap, clock and eviction fade
/// (<see cref="FxPlayer.PlaceTextureDecal"/>).</item>
/// </list>
/// </summary>
public partial class NetworkView
{
    /// <summary>The longest a fatal head shot and its headshot kill may be apart and still pair (s; ours).</summary>
    const double HeadshotPairSeconds = 1.0;

    readonly Dictionary<int, (int Shooter, Vector3 Contact, Vector3 Dir, double At)> _headshotShots = new();
    readonly Dictionary<int, (int Sequence, double At)> _headshotKills = new();
    /// <summary>Per victim: the latest drawn head hit on him (shooter, contact, direction, when), the decal's shot when the lethal mark found none.</summary>
    readonly Dictionary<int, (int Shooter, Vector3 Contact, Vector3 Dir, double At)> _headContacts = new();
    ClassicBloodRibbons? _bloodRibbons;
    ClassicBloodRibbons BloodRibbons()
    {
        if (_bloodRibbons is null || !GodotObject.IsInstanceValid(_bloodRibbons)) { _bloodRibbons = new ClassicBloodRibbons { Name = "ClassicBloodRibbons" }; AddChild(_bloodRibbons); }
        return _bloodRibbons;
    }
    readonly Dictionary<int, ImageTexture> _bloodDecalTextures = new();
    readonly List<ClassicBloodRules.TraceHit> _decalHits = new();
    int _bloodDecals, _bloodDecalMisses;

    partial void ExtrasClassicBlood(ClassicContact c, ref bool drawn)
    {
        if (!HitDirLook.HasFlag(ClassicHitDirRules.DirLook.BloodSpray)) return;
        var kind = c.Gunfire ? ClassicBloodRules.RibbonKind.Gunfire : c.Charge ? ClassicBloodRules.RibbonKind.Charge : c.Exit ? ClassicBloodRules.RibbonKind.Thrust : ClassicBloodRules.RibbonKind.Sweep;
        var path = c.History is null ? ReadOnlySpan<(double, Vector3)>.Empty : new ReadOnlySpan<(double, Vector3)>(c.History, 0, c.HistoryCount);
        BloodRibbons().Spawn(c.Contact, path, c.Dir, kind, c.Spread, c.Amount, c.PreAge);
        drawn = true;
    }

    partial void ExtrasClassicPrewarm(Vector3 at)
    {
        if (HitDirLook.HasFlag(ClassicHitDirRules.DirLook.BloodSpray)) BloodRibbons().Prewarm(at);
    }

    partial void ExtrasClassicHeadContact(int victim, int shooter, Vector3 contact, Vector3 dir)
    {
        if (!HitDirLook.HasFlag(ClassicHitDirRules.DirLook.HeadshotDecal) || victim <= 0) return;
        _headContacts[victim] = (shooter, contact, dir, _connectedFor);
        if (_headContacts.Count > 32) _headContacts.Clear();
    }

    partial void ExtrasClassicFatalHeadshot(int victim, int shooter, Vector3 contact, Vector3 dir)
    {
        if (!HitDirLook.HasFlag(ClassicHitDirRules.DirLook.HeadshotDecal)) return;
        if (_headshotKills.Remove(victim, out var kill) && _connectedFor - kill.At <= HeadshotPairSeconds) { PlaceHeadshotDecal(victim, kill.Sequence, contact, dir); return; }
        _headshotShots[victim] = (shooter, contact, dir, _connectedFor);
    }

    partial void ExtrasClassicKill(KillView k)
    {
        if (!k.Headshot || k.Victim <= 0) { if (k.Victim > 0) _headshotShots.Remove(k.Victim); return; }
        if (_headshotShots.Remove(k.Victim, out var shot) && _connectedFor - shot.At <= HeadshotPairSeconds) { PlaceHeadshotDecal(k.Victim, k.Sequence, shot.Contact, shot.Dir); return; }
        // the lethal mark found no head shot of its own (a rapid run of own shots, review of 2b89de15 finding 3): the killer's latest drawn head hit on him is the killing shot's line
        if (_headContacts.Remove(k.Victim, out var head) && head.Shooter == k.Killer && _connectedFor - head.At <= HeadshotPairSeconds) { PlaceHeadshotDecal(k.Victim, k.Sequence, head.Contact, head.Dir); return; }
        _headshotKills[k.Victim] = (k.Sequence, _connectedFor);
    }

    void PlaceHeadshotDecal(int victim, int sequence, Vector3 contact, Vector3 dir)
    {
        if (_client?.Collision is not { } world || _fx is null || _gameSpace is null || dir.LengthSquared() < 1e-8f) return;
        var from = ToGame(contact);
        var gd = _gameSpace.Basis.Inverse() * dir.Normalized();
        var d = Num.Vector3.Normalize(new Num.Vector3(gd.X, gd.Y, gd.Z));
        _decalHits.Clear();
        if (world.Weapons.Raycast(from, d, ClassicBloodRules.SearchDistance) is { } wall)
        {
            var n = Num.Vector3.Dot(wall.Normal, d) > 0f ? -wall.Normal : wall.Normal;
            _decalHits.Add(new ClassicBloodRules.TraceHit(wall.Distance, wall.Point, n));
        }
        foreach (var o in _lastOthers)
        {
            if (!o.Alive || o.PlayerId == victim) continue;
            var (zone, distance) = Combat.HitShapes(from, d, o.Position, HitBodyAsDrawn(o), ClassicBloodRules.SearchDistance, o.Yaw);
            if (zone != HitZone.None) _decalHits.Add(new ClassicBloodRules.TraceHit(distance, from + d * distance, -d, Player: true));
        }
        if (_client.Predicted is { Alive: true } me && me.PlayerId == _client.PlayerId && me.PlayerId != victim)
        {
            // the own body in the line blocks it too (review decision 9)
            var (zone, distance) = Combat.HitShapes(from, d, me.Position, HitBodyAsDrawn(me), ClassicBloodRules.SearchDistance, me.Yaw);
            if (zone != HitZone.None) _decalHits.Add(new ClassicBloodRules.TraceHit(distance, from + d * distance, -d, Player: true));
        }
        if (_stagedBlocker is { } sb) _decalHits.Add(new ClassicBloodRules.TraceHit(sb, from + d * sb, -d, Player: true));   // the staged blocked case (NetworkView.ClassicHitDirStage.cs)
        var (result, hit) = ClassicBloodRules.DecalTrace(fatal: true, head: true, gunfire: true, _decalHits);
        if (LogHitDir) HitDirPrint($"HITDIR headshot decal P{victim} kill #{sequence}: {result}{(result == ClassicBloodRules.DecalResult.NoWall ? "" : $" at {hit.Distance:F2} m")} (t {_connectedFor:F2})");
        if (result != ClassicBloodRules.DecalResult.Placed) { _bloodDecalMisses++; return; }
        int seed = ClassicBloodRules.VariantSeed(sequence, victim);
        var tex = BloodDecalTexture(seed);
        var normal = DirToGodot(hit.Normal).Normalized();
        var down = Vector3.Down - normal * Vector3.Down.Dot(normal);
        down = down.LengthSquared() > 1e-6f ? down.Normalized() : normal.Cross(Vector3.Right).Normalized();
        var x = normal.Cross(down).Normalized();
        var basis = new Basis(x, normal, down);
        _fx.PlaceTextureDecal(tex, ToGodot(hit.Point), basis, ClassicBloodRules.DecalSize, ClassicBloodRules.DecalSize, ClassicBloodRules.DecalDepth, ClassicBloodRules.DecalSeconds, PoundRing.Fade);
        _bloodDecals++;
    }

    /// <summary>The splash texture of a seed (made once, the last few kept): today's blood red, a darker blood red in the dense core (<see cref="ClassicBloodRules.DecalValue"/>).</summary>
    ImageTexture BloodDecalTexture(int seed)
    {
        if (_bloodDecalTextures.TryGetValue(seed, out var cached)) return cached;
        const int size = ClassicBloodRules.DecalTexels;
        var (alpha, shade) = ClassicBloodRules.DecalShape(seed, size);
        var bytes = new byte[size * size * 4];
        var c = CharacterViewer.BloodColour;
        for (int i = 0; i < alpha.Length; i++)
        {
            float s = ClassicBloodRules.DecalValue(shade[i]);
            bytes[i * 4] = (byte)Math.Clamp(c.R * s * 255f, 0f, 255f);
            bytes[i * 4 + 1] = (byte)Math.Clamp(c.G * s * 255f, 0f, 255f);
            bytes[i * 4 + 2] = (byte)Math.Clamp(c.B * s * 255f, 0f, 255f);
            bytes[i * 4 + 3] = (byte)Math.Clamp(alpha[i] * 255f, 0f, 255f);
        }
        var image = Image.CreateFromData(size, size, false, Image.Format.Rgba8, bytes);
        image.GenerateMipmaps();
        var tex = ImageTexture.CreateFromImage(image);
        if (_bloodDecalTextures.Count >= 8) _bloodDecalTextures.Clear();
        _bloodDecalTextures[seed] = tex;
        return tex;
    }
}
