using SmFormats.Audio;

namespace SmContent;

/// <summary>The voice properties Wwise's Set/Reset actions change (M33): volume (dB), pitch (cents), low-pass (0..100) and mute.</summary>
public enum SoundProperty { Volume, Pitch, Lpf, Mute }

/// <summary>
/// The live changes made by Set/Reset pitch, volume, LPF and mute actions (M33), keyed by property, target object (a sound,
/// container or bus) and scope (0 = every emitter, else one emitter's ID). A Set moves a blend weight from where it is to 1 over
/// the action's fade (linear: the curve shapes are not reproduced) and the value to the new one; a Reset moves the weight to 0,
/// after which the entry is gone. A target's contribution is its own value <c>x</c> with the entry blended in: independent
/// (replaces <c>x</c>): <c>x + w * (value - x)</c>; offset: <c>x + w * value</c>. **Interpretation** of Wwise's actions (value
/// meaning 1 = independent, 2 = offset); the data's numbers are used as they are.
/// </summary>
public sealed class SoundModifiers
{
    sealed class Entry
    {
        public double ValueStart, ValueDuration, WeightStart, WeightDuration;
        public float V0, V1, W0, W1;
        public bool Offset;
    }

    readonly Dictionary<(SoundProperty Property, uint Target, ulong Scope), Entry> _entries = new();

    public int Count => _entries.Count;

    static float Lerp(double now, double start, double duration, float a, float b) =>
        duration <= 1e-6 || now >= start + duration ? b : now <= start ? a : a + (b - a) * (float)((now - start) / duration);

    static float ValueOf(Entry e, double now) => Lerp(now, e.ValueStart, e.ValueDuration, e.V0, e.V1);
    static float WeightOf(Entry e, double now) => Lerp(now, e.WeightStart, e.WeightDuration, e.W0, e.W1);

    /// <summary>Starts moving a property of <paramref name="target"/> to <paramref name="value"/> over <paramref name="fadeSeconds"/>.</summary>
    public void Set(SoundProperty property, uint target, ulong scope, float value, bool offset, double now, double fadeSeconds)
    {
        var key = (property, target, scope);
        double fade = Math.Max(0, fadeSeconds);
        if (_entries.TryGetValue(key, out var e) && e.Offset == offset)
        {
            e.V0 = ValueOf(e, now); e.V1 = value; e.ValueStart = now; e.ValueDuration = fade;
            e.W0 = WeightOf(e, now); e.W1 = 1f; e.WeightStart = now; e.WeightDuration = fade;
            return;
        }
        float w = e is null ? 0f : WeightOf(e, now);
        _entries[key] = new Entry
        {
            V0 = value, V1 = value, ValueStart = now, ValueDuration = 0, Offset = offset,
            W0 = w, W1 = 1f, WeightStart = now, WeightDuration = fade,
        };
    }

    /// <summary>Moves the property back to the target's own value over <paramref name="fadeSeconds"/>; the entry is dropped when it is there.</summary>
    public void Reset(SoundProperty property, uint target, ulong scope, double now, double fadeSeconds)
    {
        var key = (property, target, scope);
        if (!_entries.TryGetValue(key, out var e)) return;
        if (fadeSeconds <= 1e-6) { _entries.Remove(key); return; }
        e.V0 = ValueOf(e, now); e.V1 = e.V0; e.ValueStart = now; e.ValueDuration = 0;
        e.W0 = WeightOf(e, now); e.W1 = 0f; e.WeightStart = now; e.WeightDuration = fadeSeconds;
    }

    /// <summary>Applies an action (<see cref="SoundPropertyAction"/>) at time <paramref name="now"/>; <paramref name="emitter"/> is the posting emitter's ID.</summary>
    public void Apply(SoundPropertyAction a, ulong emitter, double now)
    {
        ulong scope = a.EmitterScope ? emitter : 0UL;
        if (a.Reset)
        {
            // A Reset clears whatever Set the target: this emitter's entry and the global one as well as its own scope's.
            Reset(a.Property, a.Target, scope, now, a.Fade);
            if (scope != 0) Reset(a.Property, a.Target, 0UL, now, a.Fade);
            else if (emitter != 0) Reset(a.Property, a.Target, emitter, now, a.Fade);
        }
        else Set(a.Property, a.Target, scope, a.Property == SoundProperty.Mute ? VoiceMixer.MuteDb : a.Value, a.Property == SoundProperty.Mute || a.Offset, now, a.Fade);
    }

    /// <summary>The value of a property on an object given its own value <paramref name="own"/>: the emitter's entry, else the global one, else <paramref name="own"/>.</summary>
    public float Apply(SoundProperty property, uint target, ulong emitter, float own, double now)
    {
        if (_entries.Count == 0) return own;
        if (emitter != 0 && _entries.TryGetValue((property, target, emitter), out var e)) return Blend(e, own, now);
        if (_entries.TryGetValue((property, target, 0UL), out e)) return Blend(e, own, now);
        return own;
    }

    static float Blend(Entry e, float own, double now)
    {
        float w = WeightOf(e, now), v = ValueOf(e, now);
        return e.Offset ? own + w * v : own + w * (v - own);
    }

    /// <summary>Drops every entry scoped to one emitter (it is gone: a finished one-shot place, a player who left).</summary>
    public void ForgetEmitter(ulong emitter)
    {
        if (emitter == 0 || _entries.Count == 0) return;
        foreach (var key in _entries.Keys.Where(k => k.Scope == emitter).ToList()) _entries.Remove(key);
    }

    /// <summary>Drops entries whose reset has finished.</summary>
    public void Prune(double now)
    {
        foreach (var kv in _entries.Where(kv => kv.Value.W1 <= 0f && now >= kv.Value.WeightStart + kv.Value.WeightDuration).ToList())
            _entries.Remove(kv.Key);
    }
}

/// <summary>A voice's live mix: volume (dB, before the client's global gain), pitch (cents) and low-pass (0..100).</summary>
public readonly record struct VoiceMix(float VolumeDb, float PitchCents, float Lpf)
{
    public float PitchScale => MathF.Pow(2f, PitchCents / 1200f);

    /// <summary>The mix with a post's <see cref="VoiceOffset"/> added (the low-pass clamped to 0..100).</summary>
    public VoiceMix With(VoiceOffset o) => new(VolumeDb + o.GainDb, PitchCents + o.PitchCents, Math.Clamp(Lpf + o.Lpf, 0f, 100f));
}

/// <summary>
/// What a post adds to every voice it starts, on top of the data's mix (ours: the owner's footstep level, the deeper impacts and the soft hit confirm of
/// sound/impacts): volume (dB), pitch (cents) and low-pass (Wwise's 0..100 units, added like a node's LPF).
/// </summary>
public readonly record struct VoiceOffset(float GainDb = 0f, float PitchCents = 0f, float Lpf = 0f)
{
    public static VoiceOffset None => default;
    public bool IsNone => GainDb == 0f && PitchCents == 0f && Lpf == 0f;
    public VoiceOffset Plus(VoiceOffset o) => new(GainDb + o.GainDb, PitchCents + o.PitchCents, Lpf + o.Lpf);
    public override string ToString() => IsNone ? "none" : $"{GainDb:+0.#;-0.#;0} dB {PitchCents:+0;-0;0} c lpf {Lpf:+0;-0;0}";
}

/// <summary>
/// How a positive RTPC volume offset is bounded (sound/footsteps-2; <c>--rtpc-volume-cap</c> in the client, <c>--cap</c> in <c>smtool audio-mix</c>; all
/// interpretations, the original's rule is not in the data). <see cref="Peak"/> (default): a volume curve that goes above 0 dB is shifted down by its peak, so
/// the node plays at its own volume where the curve is loudest and quieter elsewhere (the curve's shape, e.g. walk against run, is kept);
/// <see cref="Node"/>: a node's volume with its RTPC offsets stays at or under 0 dB (or under its own value when that is already above 0, which no node of the
/// install is); <see cref="Voice"/>: that bound on the sum over the voice's chain (before the buses); <see cref="Off"/>: the offsets add as they are (the
/// behaviour before this branch). Curves that never go above 0 are never touched. Set/Reset volume changes are added after the bound, so a lowering one still lowers.
/// </summary>
public enum RtpcVolumeCap { Peak, Node, Voice, Off }

/// <summary>The command-line value of an <see cref="RtpcVolumeCap"/> (client <c>--rtpc-volume-cap</c>, <c>smtool --cap</c>).</summary>
public static class RtpcVolumeCaps
{
    public const RtpcVolumeCap Default = RtpcVolumeCap.Peak;

    /// <summary>The choices, lower case, for messages: "peak, node, voice or off".</summary>
    public static string Choices => "peak, node, voice or off";

    /// <summary>Names only (Enum.TryParse would take "1" too); a missing value (the flag last) or an unknown name is an error.</summary>
    public static bool TryParse(string? text, out RtpcVolumeCap cap, out string? error)
    {
        cap = Default;
        error = null;
        if (string.IsNullOrEmpty(text) || text.StartsWith("--", StringComparison.Ordinal)) { error = $"needs a value ({Choices})"; return false; }
        var name = Enum.GetNames<RtpcVolumeCap>().FirstOrDefault(n => n.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (name is null) { error = $"'{text}' is not one of {Choices}"; return false; }
        cap = Enum.Parse<RtpcVolumeCap>(name);
        return true;
    }
}

/// <summary>
/// The live volume, pitch and low-pass of a voice (M33): each node of its parent chain contributes its own value (as drawn at the
/// post) altered by the live <see cref="SoundModifiers"/>, plus the node's RTPC curves (property 0 volume dB, 2 pitch cents, 3 LPF)
/// for the game parameters the client provides; each output bus of the chain adds its volume (and any modifier on it); the
/// attenuation adds its dry volume and its low-pass curve at the listener's distance. Sums are clamped for the LPF (0..100), and a
/// volume curve above 0 dB is bounded as <see cref="RtpcVolumeCap"/> says (default: shifted down by its peak, <see cref="PeakNormalised"/>).
/// RTPC property numbering is an interpretation (0 volume, 2 pitch, 3 LPF; 1 LFE and 4 and above are not applied).
/// </summary>
public static class VoiceMixer
{
    /// <summary>What a mute subtracts, dB.</summary>
    public const float MuteDb = -96f;

    /// <summary>
    /// The volume ceiling (dB): **interpretation** (sound/footsteps-2, docs/discovery.md "Footsteps, second pass"). No node of the 29,264 in the MP banks has an
    /// own volume above 0 dB (a convention of the banks: random ranges do reach +6 dB on 93 sounds), while 10 RTPC curves offset volumes by up to +19.8, +30.2 (the footsteps)
    /// and +89.5 dB (the Ork grenade fuse at 0 m): taken literally the fuse would play at +77 dB at the voice (every weapon voice sits at 0) and a running marine's sole clank 16 dB over the bolter.
    /// </summary>
    public const float VolumeCeilingDb = 0f;

    /// <summary>
    /// A node's volume (or a chain's) with its RTPC volume offset <paramref name="rtpc"/> added: a positive offset lifts it at most to
    /// <see cref="VolumeCeilingDb"/> (to <paramref name="own"/> when that is already higher); a negative offset is added as it is.
    /// </summary>
    public static float CapRtpcVolume(float own, float rtpc) =>
        rtpc > 0f ? Math.Min(own + rtpc, Math.Max(own, VolumeCeilingDb)) : own + rtpc;

    /// <summary>
    /// <see cref="RtpcVolumeCap.Peak"/>: a volume curve's value <paramref name="y"/> shifted down by the curve's highest point when that is above 0 dB, so the
    /// curve's loudest point is the node's own volume; a curve that never goes above 0 is unchanged.
    /// </summary>
    public static float PeakNormalised(float y, IReadOnlyList<CurvePoint> points)
    {
        float peak = 0f;
        for (int i = 0; i < points.Count; i++) if (points[i].Y > peak) peak = points[i].Y;
        return y - peak;
    }

    /// <summary>The game parameters of this game (RTPC IDs = FNV-1 of the names; both found among the multiplayer banks' RTPCs by name).</summary>
    public static readonly uint Distance = Fnv.Hash32("distance"), Speed = Fnv.Hash32("speed");

    /// <param name="voice">The voice.</param>
    /// <param name="modifiers">Live Set/Reset changes, or null.</param>
    /// <param name="emitter">The emitter's ID (for emitter-scoped changes).</param>
    /// <param name="now">Time for the fades (the client's clock, seconds).</param>
    /// <param name="distance">The distance from the listener (m); null leaves out the attenuation and the <c>distance</c> RTPC.</param>
    /// <param name="parameters">Other game parameters (RTPC ID → value), e.g. <see cref="Speed"/>; null for none.</param>
    /// <param name="cap">How positive RTPC volume offsets are bounded (<see cref="RtpcVolumeCap"/>; default <see cref="RtpcVolumeCap.Peak"/>).</param>
    public static VoiceMix Evaluate(SoundVoice voice, SoundModifiers? modifiers, ulong emitter, double now, float? distance, IReadOnlyDictionary<uint, float>? parameters,
        RtpcVolumeCap cap = RtpcVolumeCap.Peak)
    {
        float volume, pitch, lpf = 0f;
        if (voice.Chain.Count == 0 && voice.Buses.Count == 0)
        {
            volume = voice.VolumeDb;
            pitch = voice.PitchCents;
            lpf = voice.LpfBase;
        }
        else
        {
            volume = pitch = 0f;
            float chainOwn = 0f, chainRtpc = 0f, chainModified = 0f;
            foreach (var n in voice.Chain)
            {
                float v = n.Volume, p = n.Pitch, l = n.Lpf, rv = 0f;
                if (modifiers is not null)
                {
                    // v is the node's volume with the live Set/Reset changes; v - n.Volume is their delta, added after the bound below.
                    v = modifiers.Apply(SoundProperty.Volume, n.Id, emitter, v, now) + modifiers.Apply(SoundProperty.Mute, n.Id, emitter, 0f, now);
                    p = modifiers.Apply(SoundProperty.Pitch, n.Id, emitter, p, now);
                    l = modifiers.Apply(SoundProperty.Lpf, n.Id, emitter, l, now);
                }
                foreach (var r in n.Rtpcs)
                {
                    float? x = r.RtpcId == Distance ? distance : parameters is not null && parameters.TryGetValue(r.RtpcId, out float px) ? px : null;
                    if (x is not float xv || r.Points.Count == 0) continue;
                    float y = r.Evaluate(xv);
                    switch (r.ParamId)
                    {
                        case 0: rv += cap == RtpcVolumeCap.Peak ? PeakNormalised(y, r.Points) : y; break;
                        case 2: p += y; break;
                        case 3: l += y; break;
                    }
                }
                float modified = v - n.Volume;
                switch (cap)
                {
                    case RtpcVolumeCap.Node: v = CapRtpcVolume(n.Volume, rv) + modified; break;
                    case RtpcVolumeCap.Voice: chainOwn += n.Volume; chainRtpc += rv; chainModified += modified; v = 0f; break;
                    default: v += rv; break;   // Peak (the curve already shifted) and Off
                }
                volume += v; pitch += p; lpf += l;
            }
            if (cap == RtpcVolumeCap.Voice) volume += CapRtpcVolume(chainOwn, chainRtpc) + chainModified;
            foreach (var b in voice.Buses)
            {
                float v = b.Volume;
                if (modifiers is not null)
                {
                    v = modifiers.Apply(SoundProperty.Volume, b.Id, emitter, v, now) + modifiers.Apply(SoundProperty.Mute, b.Id, emitter, 0f, now);
                    pitch += modifiers.Apply(SoundProperty.Pitch, b.Id, emitter, 0f, now);
                    lpf += modifiers.Apply(SoundProperty.Lpf, b.Id, emitter, 0f, now);
                }
                volume += v;
            }
        }
        if (distance is float d && voice.Attenuation is { } att)
        {
            volume += att.VolumeAt(d);
            lpf += att.LpfAt(d);
        }
        return new VoiceMix(volume, pitch, Math.Clamp(lpf, 0f, 100f));
    }
}
