using System;
using System.Collections.Generic;
using Godot;
using SmContent;
using SmSim;
using SmSim.Extras.Musou;
using SmNet.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Expansion S2 on the client (Musou Mode only): the Bomb Squig's whistle on its drop, its fuse (a red glow that pulses and beeps faster as it burns), its kick and its burst, and the Rokkit Boy's
/// rocket (the launcher's fire, the trail flying at <see cref="MusouRokkitRules.RocketSpeed"/>, its burst) with the one "incoming" warning (<see cref="MusouRocketWarner"/>: a screen-edge swish
/// towards the rocket and its whine, never stacked).
/// <list type="bullet">
/// <item>*Original asset loaded*: the effects <c>squig_explosion</c>, <c>rokkit_firing_line</c>, <c>rokkit_missile_trail</c>, <c>rokkit_dmg</c> (master_fx, found with <c>smtool fx-dump auto rokkit squig --list</c>);
/// the Squig's burst also plays the frag grenade's own detonation effects and sounds; the sounds <c>play_or_missle_launcher_fire</c> (the launcha's fire), <c>play_or_squig_vox_roar</c> (fuse),
/// <c>play_or_squig_vox_taunt</c> (kick), and the drop pod's <c>play_droppod_incoming</c> whistle as the Squig's fall and the rocket's whine (a stand-in: the data has no such cue).</item>
/// <item>*Placeholder*: the fuse's beep (a generated 2 kHz tone; the Squig's files have no beep), the fuse glow (a light), the edge swish (a drawn arc). The rocket's own burst sound is the frag's (stand-in).</item>
/// </list>
/// </summary>
public partial class NetworkView
{
    const string SquigWhistle = "play_droppod_incoming", SquigFuseSound = "play_or_squig_vox_roar", SquigKickSound = "play_or_squig_vox_taunt", RokkitFireSound = "play_or_missle_launcher_fire";
    const float RocketTrailBack = 0.2f;

    sealed class ShownRocket
    {
        public ushort Serial;
        public Num.Vector3 Position, Velocity;
        public double Born;
        public FxEffect? Trail;
        public SoundEmitter Emitter = new() { Label = "musou rocket" };
    }

    sealed class SquigFuse
    {
        public double Lit, Seconds, NextBeep;
        public OmniLight3D? Light;
        public float Flash;
    }

    readonly Dictionary<ushort, ShownRocket> _rockets = new();
    readonly List<ushort> _rocketGone = new();
    readonly Dictionary<ushort, SquigFuse> _squigFuses = new();
    readonly List<ushort> _squigGone = new();
    readonly HashSet<ushort> _squigSeen = new();
    readonly MusouRocketWarner _rocketWarner = new();
    readonly List<MusouRocketTrack> _rocketTracks = new();
    MusouIncomingEdge? _incomingEdge;
    AudioStreamWav? _beep;
    int _rocketsShown, _squigBlasts, _rocketWarnings;

    /// <summary>One of S2's cues (<see cref="MusouSquigRules"/>, <see cref="MusouRokkitRules"/>): its effects and sounds. False for any other value.</summary>
    bool MusouS2Cue(in MusouEvent e, IReadOnlyList<MusouShown> shown)
    {
        if (e.Value is < MusouSquigRules.DropCue or > MusouRokkitRules.BlastCue) return false;
        int i = MusouFind(shown, e.Enemy);
        var feet = i >= 0 ? shown[i].Position : e.Vector;
        double now = _connectedFor;
        switch (e.Value)
        {
            case MusouSquigRules.DropCue:
                S2Post(SquigWhistle, ToGodot(e.Vector + new Num.Vector3(0f, 0f, MusouSquigRules.FallHeight * 0.5f)), -4f);
                break;
            case MusouSquigRules.FuseCue:
                _squigFuses[e.Enemy] = new SquigFuse { Lit = now, Seconds = e.Player / (double)Math.Max(1, _config?.TickRate ?? 30), NextBeep = now };
                S2Post(SquigFuseSound, ToGodot(feet), 0f);
                break;
            case MusouSquigRules.KickCue:
                S2Post(SquigKickSound, ToGodot(feet), 0f);
                break;
            case MusouSquigRules.BlastCue:
                _squigBlasts++;
                EndFuse(e.Enemy);
                FragBurst(e.Vector);
                if (_fx is not null && FxActive) _fx.Play("squig_explosion", ToGodot(e.Vector), Basis.Identity, 1f, priority: 2);
                break;
            case MusouRokkitRules.FireCue:
            {
                var flat = new Num.Vector2(e.Vector.X - feet.X, e.Vector.Y - feet.Y);
                var forward = flat.LengthSquared() > 1e-6f ? Num.Vector2.Normalize(flat) : Num.Vector2.UnitY;
                var muzzle = feet + new Num.Vector3(forward * MusouRokkitRules.MuzzleForward, MusouRokkitRules.MuzzleHeight);
                StartRocket(e.Player, muzzle, e.Vector);
                break;
            }
            case MusouRokkitRules.BlastCue:
                if (_rockets.Remove(e.Player, out var gone)) { if (gone.Trail is { } t) t.Life = Math.Min(t.Life, t.Age + 0.15); _sound?.StopEmitter(gone.Emitter); }
                if (_fx is not null && FxActive) _fx.Play("rokkit_dmg", ToGodot(e.Vector), Basis.Identity, 1f, priority: 2);
                FragBurst(e.Vector, effects: false);
                break;
        }
        return true;
    }

    /// <summary>A rocket shown from <paramref name="muzzle"/> towards <paramref name="aim"/>: its fire, its trail and its whine's emitter (a Rokkit Boy's, and expansion S4's turret's).</summary>
    void StartRocket(ushort serial, Num.Vector3 muzzle, Num.Vector3 aim)
    {
        double now = _connectedFor;
        var way = aim - muzzle;
        if (way.LengthSquared() < 1e-6f) return;
        var rocket = new ShownRocket { Serial = serial, Position = muzzle, Velocity = Num.Vector3.Normalize(way) * MusouRokkitRules.RocketSpeed, Born = now };
        rocket.Emitter.Position = ToGodot(muzzle);
        rocket.Emitter.Follow = () => _rockets.TryGetValue(serial, out var r) ? ToGodot(r.Position) : null;
        _rockets[serial] = rocket;
        _rocketsShown++;
        S2Post(RokkitFireSound, ToGodot(muzzle), 0f);
        if (_fx is not null && FxActive)
        {
            var axes = AxesAlong(ToGodot(muzzle + rocket.Velocity) - ToGodot(muzzle));
            _fx.Play("rokkit_firing_line", ToGodot(muzzle), axes, 1f);
            rocket.Trail = _fx.Play("rokkit_missile_trail", ToGodot(muzzle), axes, 1f, follow: () =>
                _rockets.TryGetValue(serial, out var r) ? (ToGodot(r.Position - Num.Vector3.Normalize(r.Velocity) * RocketTrailBack), AxesAlong(ToGodot(r.Velocity) - ToGodot(Num.Vector3.Zero))) : null,
                lifetime: MusouRokkitRules.RocketMaxSeconds + 1f);
            if (rocket.Trail is not null) rocket.Trail.StopOnLost = true;
        }
    }

    /// <summary>The frag grenade's own detonation (its effects and its sounds) at <paramref name="at"/>: the Squig's burst is the original frag. <paramref name="effects"/> false: its sounds only.</summary>
    void FragBurst(Num.Vector3 at, bool effects = true)
    {
        if (_config is null) return;
        int frag = -1;
        for (int k = 0; k < _config.Thrown.Count && frag < 0; k++) if (_config.Thrown[k].Name.Contains("frag", StringComparison.OrdinalIgnoreCase)) frag = k;
        if (frag < 0) return;
        var view = new SmNet.ProjectileView(-1, frag, 0, at, SmNet.ProjectileFlags.Thrown | SmNet.ProjectileFlags.Detonated);
        if (effects) FxDetonation(view);
        SoundDetonation(view, "squig");
    }

    void S2Post(string name, Vector3 at, float gainDb)
    {
        if (_sound is null) return;
        _sound.Post(name, new SoundEmitter { Position = at, OneShot = true, Label = "musou s2" }, offset: new VoiceOffset(gainDb));
    }

    void EndFuse(ushort id)
    {
        if (!_squigFuses.Remove(id, out var fuse)) return;
        fuse.Light?.QueueFree();
    }

    /// <summary>S2's effects are in no catalogue the client reads: compiled once on a worker from master_fx when the effect player is up.</summary>
    static readonly string[] S2Effects = ["squig_explosion", "rokkit_firing_line", "rokkit_missile_trail", "rokkit_dmg"];
    System.Threading.Tasks.Task<(Dictionary<string, CSystem> Systems, Dictionary<string, byte[]> Decals, List<string> Failures)>? _s2FxLoad;
    bool _s2FxReady;

    void S2FxLoad()
    {
        if (_s2FxReady || _fx is not { IsReady: true, Library: { } library }) return;
        _s2FxLoad ??= System.Threading.Tasks.Task.Run(() =>
        {
            var failures = new List<string>();
            var systems = CompileSystems(library, S2Effects, failures);
            return (new Dictionary<string, CSystem>(systems), new Dictionary<string, byte[]>(library.Textures(FxPlayer.DecalNames(systems.Values))), failures);
        });
        if (!_s2FxLoad.IsCompleted) return;
        _s2FxReady = true;
        if (_s2FxLoad.IsFaulted) { GD.PrintErr("musou s2: effects: " + _s2FxLoad.Exception?.GetBaseException().Message); return; }
        var (systems, decals, failed) = _s2FxLoad.Result;
        _fx.Preload(systems, decals);
        GD.Print($"musou s2: {systems.Count} effects compiled ({string.Join(", ", systems.Keys)}){(failed.Count > 0 ? "; failed: " + string.Join(", ", failed) : "")}");
    }

    /// <summary>Each frame: the rockets fly on, the warning picks its one rocket, every fused Squig glows and beeps.</summary>
    void MusouS2Frame(IReadOnlyList<MusouShown> shown, double delta)
    {
        S2FxLoad();
        double now = _connectedFor;
        // Rockets: straight on at their speed until their burst comes (or long after they should have).
        _rocketGone.Clear();
        _rocketTracks.Clear();
        foreach (var (serial, r) in _rockets)
        {
            r.Position += r.Velocity * (float)delta;
            if (now - r.Born > MusouRokkitRules.RocketMaxSeconds + 1.0) { _rocketGone.Add(serial); continue; }
            _rocketTracks.Add(new MusouRocketTrack(serial, r.Position, r.Velocity));
        }
        foreach (var serial in _rocketGone) if (_rockets.Remove(serial, out var r)) { if (r.Trail is { } t) t.Life = Math.Min(t.Life, t.Age + 0.15); _sound?.StopEmitter(r.Emitter); }
        if (_client is { } client && client.Predicted.Alive)
        {
            var me = client.Predicted.Position + new Num.Vector3(0f, 0f, MusouRokkitRules.ChestHeight);
            ushort warn = _rocketWarner.Update(now, me, _rocketTracks.ToArray());
            if (warn != 0 && _rockets.TryGetValue(warn, out var threat))
            {
                _rocketWarnings++;
                if (_sound is not null) _sound.Post(SquigWhistle, threat.Emitter, offset: new VoiceOffset(-6f));   // the whine rides on the rocket
                ShowIncoming(threat.Position, (float)(MusouRocketWarner.Threat(me, new MusouRocketTrack(warn, threat.Position, threat.Velocity))?.Seconds ?? 1f));
            }
        }
        _incomingEdge?.Frame((float)delta);

        // Fuses: glow and beep faster as they burn (from 0.45 s between beeps when lit to 0.07 s at the end; ours).
        _squigSeen.Clear();
        for (int k = 0; k < shown.Count; k++)
        {
            var s = shown[k];
            if (s.Type != MusouEnemyType.BombSquig) continue;
            if (s.State == MusouEnemyState.Dying) { EndFuse(s.Id); continue; }
            if (!_squigFuses.TryGetValue(s.Id, out var fuse)) continue;
            _squigSeen.Add(s.Id);
            double burnt = Math.Clamp((now - fuse.Lit) / Math.Max(0.05, fuse.Seconds), 0.0, 1.0);
            if (now >= fuse.NextBeep)
            {
                fuse.NextBeep = now + (0.45 - 0.38 * burnt);
                fuse.Flash = 1f;
                Beep(ToGodot(s.Position + new Num.Vector3(0f, 0f, 0.5f)), (float)burnt);
            }
            fuse.Flash = MathF.Max(0f, fuse.Flash - (float)delta * 9f);
            if (fuse.Light is null)
            {
                fuse.Light = new OmniLight3D { LightColor = new Color(1f, 0.22f, 0.08f), OmniRange = 3.5f, ShadowEnabled = false };
                AddChild(fuse.Light);
            }
            fuse.Light.GlobalPosition = ToGodot(s.Position + new Num.Vector3(0f, 0f, 0.6f));
            fuse.Light.LightEnergy = 0.6f + 1.2f * (float)burnt + 5f * fuse.Flash;
        }
        _squigGone.Clear();
        foreach (var id in _squigFuses.Keys) if (!_squigSeen.Contains(id)) _squigGone.Add(id);
        foreach (var id in _squigGone) EndFuse(id);
    }

    /// <summary>The fuse's beep (placeholder: a short generated tone, rising a little in pitch as it burns).</summary>
    void Beep(Vector3 at, float burnt)
    {
        _beep ??= MakeBeep();
        var player = new AudioStreamPlayer3D { Stream = _beep, Bus = SoundSystem.BusName, UnitSize = 6f, MaxDistance = 60f, VolumeDb = -8f + (_sound?.GlobalGainDb ?? 0f), PitchScale = 1f + 0.35f * burnt };
        AddChild(player);
        player.GlobalPosition = at;
        player.Finished += player.QueueFree;
        player.Play();
    }

    static AudioStreamWav MakeBeep()
    {
        const int rate = 22050, samples = rate * 60 / 1000;
        var data = new byte[samples * 2];
        for (int n = 0; n < samples; n++)
        {
            float envelope = MathF.Min(1f, MathF.Min(n / 60f, (samples - n) / 200f));
            short v = (short)(MathF.Sin(2f * MathF.PI * 2000f * n / rate) * envelope * 9000f);
            data[2 * n] = (byte)v; data[2 * n + 1] = (byte)(v >> 8);
        }
        return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Stereo = false, Data = data };
    }

    /// <summary>The screen-edge swish towards <paramref name="from"/> (game space) for about <paramref name="seconds"/>.</summary>
    void ShowIncoming(Num.Vector3 from, float seconds)
    {
        if (_camera is not { } camera) return;
        if (_incomingEdge is null)
        {
            var layer = new CanvasLayer { Layer = 5 };
            AddChild(layer);
            _incomingEdge = new MusouIncomingEdge { MouseFilter = Control.MouseFilterEnum.Ignore };
            _incomingEdge.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            layer.AddChild(_incomingEdge);
        }
        var d = ToGodot(from) - camera.GlobalPosition;
        var right = camera.GlobalTransform.Basis.X; var forward = -camera.GlobalTransform.Basis.Z;
        float x = d.Dot(right), y = d.Dot(new Vector3(forward.X, 0f, forward.Z).Normalized());
        _incomingEdge.Show(MathF.Atan2(x, y), MathF.Max(0.6f, seconds + 0.3f));
    }

    /// <summary>For the log every 10 s: the rockets, Squig bursts and warnings shown.</summary>
    string MusouS2Log() => $"musou s2: rockets {_rocketsShown} (in flight {_rockets.Count}), squig bursts {_squigBlasts}, fuses {_squigFuses.Count}, rocket warnings {_rocketWarnings}";
}

/// <summary>The "incoming" swish (placeholder look, ours): a soft red-orange arc on the screen's edge towards the rocket, fading out; one at a time.</summary>
public partial class MusouIncomingEdge : Control
{
    float _angle, _left, _total;

    /// <summary>Shows the arc at <paramref name="angle"/> (radians, 0 = straight ahead, clockwise) for <paramref name="seconds"/>.</summary>
    public void Show(float angle, float seconds) { _angle = angle; _left = _total = seconds; QueueRedraw(); }

    public void Frame(float delta)
    {
        if (_left <= 0f) return;
        _left = MathF.Max(0f, _left - delta);
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_left <= 0f) return;
        var size = GetViewportRect().Size;
        var centre = size / 2f;
        float radius = MathF.Min(size.X, size.Y) * 0.46f;
        float a = _angle - MathF.PI / 2f;   // Godot's 0 is to the right; ours ahead (up)
        float t = _left / MathF.Max(0.01f, _total);
        float alpha = 0.55f * MathF.Min(1f, t * 2.5f);
        for (int k = 0; k < 4; k++)
            DrawArc(centre, radius + k * 6f, a - 0.32f, a + 0.32f, 24, new Color(1f, 0.35f, 0.1f, alpha * (1f - k * 0.22f)), 5f, true);
    }
}
