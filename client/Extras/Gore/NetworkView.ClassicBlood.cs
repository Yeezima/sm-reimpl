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
/// <item><see cref="ExtrasClassicBlood"/>: the red directional spray (<see cref="ClassicHitSpray"/>, today's Classic health-hit red) along the weapon's drawn path or the incoming shot; heavier
/// and lethal hits throw more streaks, a little larger, without filling the shape in.</item>
/// <item>The fatal gunfire headshot's wall decal: the confirmed lethal head shot (<see cref="ExtrasClassicFatalHeadshot"/>) and the server's headshot kill (<see cref="ExtrasClassicKill"/>, either
/// order, within <see cref="HeadshotPairSeconds"/>) place one decal where the shot's line, extended from the head contact, first meets the level, if that is a wall within 5 m and nothing else
/// (another body) is in the way (<see cref="ClassicBloodRules.DecalTrace"/>). The splash is made in code from the kill's sequence and the victim (<see cref="ClassicBloodRules.DecalShape"/>), so every
/// view draws the same one; it shares the effect player's alpha-decal cap, clock and eviction fade (<see cref="FxPlayer.PlaceTextureDecal"/>).</item>
/// </list>
/// </summary>
public partial class NetworkView
{
    /// <summary>The longest a fatal head shot and its headshot kill may be apart and still pair (s; ours).</summary>
    const double HeadshotPairSeconds = 1.0;

    readonly Dictionary<int, (int Shooter, Vector3 Contact, Vector3 Dir, double At)> _headshotShots = new();
    readonly Dictionary<int, (int Sequence, double At)> _headshotKills = new();
    readonly Dictionary<int, ImageTexture> _bloodDecalTextures = new();
    readonly List<ClassicBloodRules.TraceHit> _decalHits = new();
    int _bloodDecals, _bloodDecalMisses;

    partial void ExtrasClassicBlood(ClassicContact c, ref bool drawn)
    {
        if (!HitDirLook.HasFlag(ClassicHitDirRules.DirLook.BloodSpray)) return;
        var spray = HitSpray();
        float size = c.Lethal ? 1.15f : c.Heavy ? 1.08f : 1f;
        if (c.Gunfire) spray.SpawnShot(c.Contact, c.Dir, new ClassicHitSpray.Look(false, c.Amount, 0.32f, size, 0.18f), c.PreAge);
        else spray.SpawnMelee(c.Contact, c.History is null ? ReadOnlySpan<(double, Vector3)>.Empty : new ReadOnlySpan<(double, Vector3)>(c.History, 0, c.HistoryCount), c.Weapon, c.Dir,
                              new ClassicHitSpray.Look(false, c.Amount, c.Spread, size), c.PreAge);
        drawn = true;
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

    /// <summary>The splash texture of a seed (made once, the last few kept): today's blood red, darkest in the core.</summary>
    ImageTexture BloodDecalTexture(int seed)
    {
        if (_bloodDecalTextures.TryGetValue(seed, out var cached)) return cached;
        const int size = 192;
        var (alpha, shade) = ClassicBloodRules.DecalShape(seed, size);
        var bytes = new byte[size * size * 4];
        var c = CharacterViewer.BloodColour;
        for (int i = 0; i < alpha.Length; i++)
        {
            float s = 0.12f + 0.88f * MathF.Pow(shade[i], 1.4f);   // near-black dense core, today's blood red at the thin edges (review of fcd76a6f)
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
