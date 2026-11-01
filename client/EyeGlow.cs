using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// M40-T13: the Chaos eye glow of one character. The data's glow is an effect, not a material (<see cref="EyeGlowData"/>): while a Chaos weapon that binds
/// <c>csm_mp_eye_*</c> is deployed (in hand), the effect plays at each of the helmet's eye markers (<c>fx_lefteye</c>, <c>fx_righteye</c>), attached
/// (it follows the head). Its colours are the data's (blue for the bolters and melee weapons, red-orange for the lascannon, purple for the plasma
/// weapons; spec "Chaos eye glow"). Stopping (another weapon, death, hidden model) ends it; the <c>stop_emitting</c> fade-out is not reproduced. The customizer's lens
/// colour does not drive it (the lens itself is recoloured, in the helmet's material). Space Marine helmets have no eye markers, so they never glow this way.
/// The helmets' markers are read off the main thread (<see cref="PreloadMarkers"/>, or one background read per helmet seen first in <see cref="Update"/>).
/// </summary>
public sealed class EyeGlow
{
    /// <summary>Switch: <c>SM_NO_EYE_GLOW=1</c> or the client's <c>--no-eye-glow</c> turns the glow off.</summary>
    public static bool Off = System.Environment.GetEnvironmentVariable("SM_NO_EYE_GLOW") == "1";

    /// <summary>Markers by helmet package, only for reads that succeeded (an empty list: the helmet has none).</summary>
    static readonly ConcurrentDictionary<string, IReadOnlyList<(string Marker, string Bone, Num.Matrix4x4 Local)>> MarkerCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, byte> Pending = new(StringComparer.OrdinalIgnoreCase);

    readonly List<FxEffect> _playing = new();
    string _key = "";
    (MarineAssets?, ulong, string?, bool) _last;
    bool _waiting, _backedOff;

    /// <summary>The helmet's markers are not read yet (a background read runs, or a failed one waits out its back-off): the glow starts when they are.</summary>
    public bool Waiting => _waiting;
    /// <summary>The wait is a failed read's back-off (<see cref="RetryMilliseconds"/>), not a read in progress: nothing more is to be expected soon.</summary>
    public bool BackedOff => _waiting && _backedOff;

    /// <summary>A helmet whose marker read failed is not read again for this long (a bad file must not be reopened every frame).</summary>
    public const long RetryMilliseconds = 10_000;
    static readonly ConcurrentDictionary<string, long> FailedAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Effects alive now (0 or 2).</summary>
    public int Live => _playing.Count;
    /// <summary>The last state started: helmet, skeleton and effects (for logs).</summary>
    public string Key => _key;

    /// <summary>The helmet piece package a character wears (the first piece whose name contains "_helmet"), or null.</summary>
    public static string? HelmetOf(CharacterViewer c) => c.Assets?.Pieces.FirstOrDefault(p => p.Name.Contains("_helmet", StringComparison.OrdinalIgnoreCase))?.Name;

    /// <summary>
    /// Reads the eye markers of the helmet packages (for a background thread; each read opens the package). A helmet whose read fails is not cached
    /// (a note is added) so a later frame can try again.
    /// </summary>
    public static void PreloadMarkers(string gameDir, IEnumerable<string> helmets, IReadOnlySet<string> boneNames, List<string> notes)
    {
        foreach (var helmet in helmets)
        {
            if (MarkerCache.ContainsKey(helmet)) continue;
            try
            {
                MarkerCache[helmet] = CharacterModel.PackagePath(gameDir, helmet) is { } path ? EyeGlowData.HelmetMarkers(path, boneNames.Contains) : [];
                FailedAt.TryRemove(helmet, out _);
            }
            catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or NotSupportedException or UnauthorizedAccessException)
            {
                FailedAt[helmet] = System.Environment.TickCount64;
                lock (notes) notes.Add($"eye glow: markers of {helmet} unreadable ({ex.Message}); tried again in {RetryMilliseconds / 1000} s when the helmet is seen");
            }
        }
    }

    /// <summary>Every armour helmet package of the install (both races; only Chaos helmets have eye markers), for <see cref="PreloadMarkers"/>.</summary>
    public static IEnumerable<string> HelmetPackages(string gameDir)
    {
        string dir = System.IO.Path.Combine(gameDir, "data", "packages");
        if (!System.IO.Directory.Exists(dir)) return [];
        return System.IO.Directory.EnumerateFiles(dir, "*armour*_helmet.oppc").Select(f => System.IO.Path.GetFileNameWithoutExtension(f)!);
    }

    /// <summary>The cached markers of a helmet; when they are not read yet, starts one background read and returns null (the glow starts on a later frame).</summary>
    static IReadOnlyList<(string Marker, string Bone, Num.Matrix4x4 Local)>? MarkersOf(string gameDir, string helmet, IReadOnlySet<string> boneNames, out bool backedOff)
    {
        backedOff = false;
        if (MarkerCache.TryGetValue(helmet, out var cached)) return cached;
        // A failed read waits out its back-off before the next one (no new read, no note, every frame otherwise).
        if (FailedAt.TryGetValue(helmet, out var failed) && System.Environment.TickCount64 - failed < RetryMilliseconds) { backedOff = true; return null; }
        if (Pending.TryAdd(helmet, 0))
            Task.Run(() =>
            {
                var notes = new List<string>();
                PreloadMarkers(gameDir, [helmet], boneNames, notes);
                foreach (var n in notes) GD.PrintErr(n);
                Pending.TryRemove(helmet, out _);
            });
        return null;
    }

    /// <summary>
    /// Per frame: plays the eye effects of <paramref name="deployedWargear"/> at the character's eye markers, or stops them when <paramref name="show"/> is false,
    /// the weapon binds none, or the helmet, the skeleton or the effects changed, or the effect player dropped them (then they start again).
    /// </summary>
    public void Update(FxPlayer fx, string gameDir, EyeGlowData data, CharacterViewer character, string? deployedWargear, bool show)
    {
        if (Off || !fx.IsReady || character.Skeleton is not { } skeleton || character.Assets is not { } assets) { Stop(); _key = ""; _last = default; return; }
        // The common frame: nothing changed and the effects still run (no lookups, no strings).
        var input = (assets, skeleton.GetInstanceId(), deployedWargear, show);
        bool lost = false;
        foreach (var e in _playing) lost |= e.Dead || e.Age > e.Life;   // ended, evicted by a busier frame (FxPlayer.Play) or cleared
        if (input == _last && !_waiting && !lost) return;
        _last = input;
        _waiting = false;
        var plays = show ? data.For(deployedWargear) : [];
        string? helmet = plays.Count > 0 ? HelmetOf(character) : null;
        IReadOnlyList<(string Marker, string Bone, Num.Matrix4x4 Local)> markers = [];
        if (helmet is not null)
        {
            if (MarkersOf(gameDir, helmet, assets.Rig.BoneNames, out bool backedOff) is { } m) markers = m;
            else { _waiting = true; _backedOff = backedOff; }
        }
        string key = markers.Count == 0 ? "" : $"{helmet}|{skeleton.GetInstanceId()}|{string.Join(",", plays.Select(p => p.Name + "@" + p.Marker))}";
        if (key == _key && _playing.Count > 0 && !lost) return;
        Stop();
        _key = key;
        if (key.Length == 0) return;
        foreach (var p in plays)
        {
            var m = markers.FirstOrDefault(x => x.Marker.Equals(p.Marker, StringComparison.OrdinalIgnoreCase));
            if (m.Bone is null) continue;
            int bone = skeleton.FindBone(m.Bone);
            if (bone < 0) continue;
            var local = MarineAssets.ToGodot(m.Local);
            (Vector3 Position, Basis Axes)? Place()
            {
                if (!GodotObject.IsInstanceValid(skeleton) || !skeleton.IsInsideTree()) return null;
                var world = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(bone) * local;
                return (world.Origin, world.Basis.Orthonormalized());
            }
            if (Place() is not { } at) continue;
            var e = fx.Play(p.Name, at.Position, at.Axes, p.Scale <= 0f ? 1f : p.Scale, null, Place, 0f, 0.0, repeat: true, priority: 1);
            if (e is null) continue;
            e.StopOnLost = true;
            _playing.Add(e);
        }
    }

    /// <summary>Ends the effects now (their streamer trails vanish with them).</summary>
    public void Stop()
    {
        foreach (var e in _playing) e.Life = Math.Min(e.Life, e.Age);
        _playing.Clear();
    }

    /// <summary>Ends the effects and forgets the state they were started in, so the next <see cref="Update"/> starts them afresh (the same character shown again after a pause).</summary>
    public void Reset()
    {
        Stop();
        _key = "";
        _last = default;
        _waiting = false;
    }
}
