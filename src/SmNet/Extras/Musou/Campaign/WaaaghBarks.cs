using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Extras.Musou.Campaign;

/// <summary>
/// The waves' Waaagh! (expansion S6; the brief: waves attacking a point shout in bursts, at most 3 Waaagh! voices at once, 4-8 s apart per wave, nearest waves first, varied voices, within the sound budget). Every
/// value but the brief's is ours. Seconds and metres.
/// </summary>
public static class WaaaghBarkRules
{
    public const int MaxVoices = 3;
    public const float BurstMin = 4f, BurstMax = 8f;
    /// <summary>How long one Waaagh! voice is counted as sounding (the roars of the install run about 1.5 to 2.5 s).</summary>
    public const float VoiceSeconds = 2.2f;
    /// <summary>A wave this far off is not heard (the Waaagh group's range in <c>MusouSoundRules</c>).</summary>
    public const float Range = 300f;
    /// <summary>A wave first heard waits up to this long for its first burst (so waves arriving together do not shout as one).</summary>
    public const float FirstDelayMost = 2f;
    /// <summary>The voices (types by their roar and their taunt): a burst never repeats the last one heard.</summary>
    public static readonly (MusouEnemyType Type, bool Taunt)[] Voices =
        [(MusouEnemyType.Boy, false), (MusouEnemyType.Nob, false), (MusouEnemyType.ArdBoy, false), (MusouEnemyType.Boy, true), (MusouEnemyType.Nob, true), (MusouEnemyType.ArdBoy, true)];
}

/// <summary>A Waaagh! to post: the wave, where (its front) and which voice.</summary>
public readonly record struct WaaaghBark(byte Army, Vector3 At, int Voice, float Distance);

/// <summary>
/// Plans the waves' Waaagh! bursts on a client (engine-free, deterministic for its clock): each Ork army attacking a point shouts every 4 to 8 s, the nearest first, while fewer than three Waaagh! voices sound.
/// The view posts each through the sound budget; a refused post still spends the wave's turn (so a full budget never makes every wave shout at once later).
/// </summary>
public sealed class WaaaghBarkPlanner
{
    readonly Dictionary<byte, double> _next = new();
    readonly List<double> _sounding = new();
    readonly List<(float D, TfwArmyView A, Vector3 At)> _waves = new();
    readonly List<byte> _gone = new();
    int _last = -1;
    uint _rolls;

    /// <summary>Voices counted as sounding now.</summary>
    public int Sounding => _sounding.Count;

    float Roll() => MusouSim_Unit(++_rolls * 0x9E3779B9u);

    static float MusouSim_Unit(uint h)
    {
        h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
        return (h >> 8) * (1f / 16777216f);
    }

    /// <summary>
    /// The bursts due at <paramref name="now"/> for a listener at <paramref name="listener"/>: <paramref name="armies"/> as heard, each one's front at <paramref name="frontOf"/> (the drawn anchor; null: the
    /// record's). At most <see cref="WaaaghBarkRules.MaxVoices"/> sound at once.
    /// </summary>
    public List<WaaaghBark> Step(double now, Vector3 listener, IReadOnlyList<TfwArmyView> armies, Func<byte, Vector3?>? frontOf = null)
    {
        var due = new List<WaaaghBark>();
        _sounding.RemoveAll(end => end <= now);
        _waves.Clear();
        foreach (var a in armies)
        {
            if (a.Side != TfwArmySide.Ork || a.Order != TfwArmyOrder.Attack || a.Count == 0) continue;
            var at = frontOf?.Invoke(a.Id) ?? a.Anchor;
            float d = Vector3.Distance(at, listener);
            if (d > WaaaghBarkRules.Range) continue;
            _waves.Add((d, a, at));
        }
        foreach (var id in _next.Keys) if (!_waves.Exists(w => w.A.Id == id)) _gone.Add(id);
        foreach (var id in _gone) _next.Remove(id);
        _gone.Clear();
        _waves.Sort((x, y) => x.D.CompareTo(y.D));
        foreach (var (d, a, at) in _waves)
        {
            if (!_next.TryGetValue(a.Id, out double next)) { _next[a.Id] = now + Roll() * WaaaghBarkRules.FirstDelayMost; continue; }
            if (now < next) continue;
            if (_sounding.Count >= WaaaghBarkRules.MaxVoices) break;   // the nearest waves have the voices; the rest wait their turn
            int voices = WaaaghBarkRules.Voices.Length;
            int voice = _last < 0 ? (int)(Roll() * voices) : (int)(Roll() * (voices - 1));
            if (_last >= 0 && voice >= _last) voice++;   // never the voice just heard
            voice = Math.Clamp(voice, 0, voices - 1);
            _last = voice;
            _sounding.Add(now + WaaaghBarkRules.VoiceSeconds);
            _next[a.Id] = now + WaaaghBarkRules.BurstMin + Roll() * (WaaaghBarkRules.BurstMax - WaaaghBarkRules.BurstMin);
            due.Add(new WaaaghBark(a.Id, at, voice, d));
        }
        return due;
    }
}
