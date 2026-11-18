using System;
using Godot;
using SmContent.Extras.Musou;
using SmSim.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The Dreadnought's torrent of fire and his calmer camera (owner 2026-11-18; added content, Musou Mode only; client drawing only: no change to the shots, damage, hit rays, simulation or wire; Classic PvP untouched).
/// <b>Torrent:</b> every real shot of his assault cannon spawns <see cref="DreadnoughtTorrent.PerShot"/> purely visual streaks from a ring around the barrel (<see cref="DreadnoughtTorrent.Fan"/>), drawn by the bullet-line mesh
/// through <see cref="SoftStreak"/> in a pool of its own (<see cref="TorrentPool"/> entries, at most <see cref="DreadnoughtTorrent.MaxPerFrame"/> new a frame). They have no impact effect, no sound and no sparks: each just ends
/// in the air at the real shot's length. <b>Shake:</b> while the own player is the Dreadnought the camera shake (every source in <c>FxApplyShake</c>) is multiplied by <see cref="DreadnoughtTorrent.ShakeScale"/> (0.2).
/// Test aids: <c>--old-dn-torrent</c> (no extra streaks), <c>--old-dn-shake</c> (full shake), <c>--dn-torrent-count/-radius/-cone/-jitter/-spin N</c>, <c>--dn-torrent-log</c>.
/// </summary>
public partial class NetworkView
{
    const int TorrentPool = 96;
    static readonly bool OldDreadTorrent = CmdFlag("old-dn-torrent");
    static readonly bool OldDreadShake = CmdFlag("old-dn-shake");
    static readonly bool DreadTorrentLog = CmdFlag("dn-torrent-log");
    static readonly DreadnoughtTorrent.Settings TorrentSettings = new(
        CmdFloat("dn-torrent-count") is var c && float.IsFinite(c) && c >= 0f ? (int)MathF.Min(c, 24f) : DreadnoughtTorrent.PerShot,
        CmdFloat("dn-torrent-radius") is var r && float.IsFinite(r) && r >= 0f ? r : DreadnoughtTorrent.RingRadius,
        CmdFloat("dn-torrent-cone") is var k && float.IsFinite(k) && k >= 0f ? k : DreadnoughtTorrent.ConeDegrees,
        CmdFloat("dn-torrent-jitter") is var j && float.IsFinite(j) && j >= 0f ? j : DreadnoughtTorrent.JitterDegrees,
        CmdFloat("dn-torrent-spin") is var sp && float.IsFinite(sp) ? sp : DreadnoughtTorrent.SpinPerShot);

    struct TorrentStreak { public Vector3 From, Dir; public float Length; public double Start; public bool Live; }
    readonly TorrentStreak[] _torrent = new TorrentStreak[TorrentPool];
    int _torrentNext, _torrentShots, _torrentLive, _torrentThisFrame, _torrentSpawned, _torrentPeakLive;
    ulong _torrentFrame;
    readonly Random _torrentRng = new(0x70771);

    /// <summary>From <c>FxShot</c> (a shot that drew its tracer): extra visual streaks for the Dreadnought's cannon.</summary>
    partial void ExtrasShotTorrent(int shooter, string wargear, Vector3 muzzle, Vector3 end, bool own)
    {
        if (OldDreadTorrent || Lean || DreadIndex < 0 || !DreadnoughtPlayer(shooter)) return;
        if (wargear != DreadnoughtRules.CannonWargear && wargear != DreadnoughtRig.ChaosCannonWargear) return;
        var delta = end - muzzle;
        float length = MathF.Min(delta.Length(), DreadnoughtTorrent.MaxLength);
        if (length < 3f) return;
        ulong frame = Engine.GetProcessFrames();
        if (frame != _torrentFrame) { _torrentFrame = frame; _torrentThisFrame = 0; }
        var fan = DreadnoughtTorrent.Fan(new Num.Vector3(delta.X, delta.Y, delta.Z), _torrentShots++, TorrentSettings, () => (float)_torrentRng.NextDouble());
        foreach (var (o, d) in fan)
        {
            if (_torrentThisFrame >= DreadnoughtTorrent.MaxPerFrame) break;
            _torrentThisFrame++; _torrentSpawned++;
            _torrent[_torrentNext] = new TorrentStreak { From = muzzle + new Vector3(o.X, o.Y, o.Z), Dir = new Vector3(d.X, d.Y, d.Z), Length = length, Start = _connectedFor, Live = true };
            _torrentNext = (_torrentNext + 1) % TorrentPool;
        }
        if (DreadTorrentLog && _torrentShots % 20 == 1) GD.Print($"DNTORRENT t {_connectedFor:F2} shot {_torrentShots}: {fan.Length} streaks, length {length:F1} m, spawned {_torrentSpawned}, live {_torrentLive} (peak {_torrentPeakLive})");
    }

    /// <summary>From <c>UpdateBulletLines</c>: whether any torrent streak is live (so the bullet mesh is drawn).</summary>
    partial void ExtrasStreaksLive(ref bool any)
    {
        if (any || _torrentSpawned == 0) return;
        for (int i = 0; i < _torrent.Length && !any; i++) any = _torrent[i].Live;
    }

    /// <summary>From <c>UpdateBulletLines</c>, after the ordinary streaks: draws the torrent's streaks (the same warm ribbon, thinner).</summary>
    partial void ExtrasDrawStreaks(Vector3 camera)
    {
        if (_torrentSpawned == 0) return;
        var cold = new Color(1f, 0.35f, 0.15f, 0f);
        var hot = new Color(1f, 0.8f, 0.45f, 1f);
        int live = 0;
        float oldWidth = _streakWidth;
        _streakWidth = 2.2f;
        for (int i = 0; i < _torrent.Length; i++)
        {
            ref var s = ref _torrent[i];
            if (!s.Live) continue;
            float head = DreadnoughtTorrent.StreakSpeed * (float)(_connectedFor - s.Start);
            float tail = MathF.Max(0f, head - DreadnoughtTorrent.TailLength);
            if (tail >= s.Length || _connectedFor - s.Start > 1.0) { s.Live = false; continue; }
            live++;
            SoftStreak(camera, s.From + s.Dir * tail, s.From + s.Dir * MathF.Min(head, s.Length), cold, hot);
        }
        _streakWidth = oldWidth;
        _torrentLive = live; if (live > _torrentPeakLive) _torrentPeakLive = live;
    }

    /// <summary>The share of the camera shake drawn now: 0.2 while the own player is the Dreadnought (alive or dead), else 1.</summary>
    partial void ExtrasShakeScale(ref float scale)
    {
        if (OldDreadShake || DreadIndex < 0 || _client is null || !IsDreadnoughtClass(_client.Predicted.Class)) return;
        scale *= DreadnoughtTorrent.ShakeScale;
    }
}
