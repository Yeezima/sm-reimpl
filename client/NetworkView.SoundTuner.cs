using System;
using System.Linq;
using Godot;
using SmContent;

namespace SmClient;

/// <summary>sound/tuning-tool: the owner's mixer panel (<see cref="SoundTunerPanel"/>): F7 or <c>--sound-tuner</c>; the owner's saved overrides load when the match's sound starts.</summary>
public partial class NetworkView
{
    static readonly bool SoundTunerArg = OS.GetCmdlineUserArgs().Contains(SoundTunerPanel.ArgName);
    SoundTunerPanel? _tuner;

    /// <summary>Loads the tuning file (once per run) and makes the panel; called when the sound system starts.</summary>
    void SetUpSoundTuner()
    {
        string repo = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));
        SoundTunerPanel.EnsureLoaded(repo);
        if (_soundTriggers is not null) SoundMixCatalog.Register(_soundTriggers.EventNames());   // sound/mix-expand: every event the triggers can post is listed in the F7 editor from the start
        if (_tuner is not null) return;
        AddChild(_tuner = new SoundTunerPanel(() => MusouActive, _sound, PreviewSound));
        if (SoundTunerArg) _tuner.SetOpen(true);
        if (CmdArg("sound-mix-proof") is { } proof) _ = RunSoundMixProof(proof);
        if (CmdArg("sound-mix-roll-hop-proof") is { } rhProof) _ = RunRollHopProof(rhProof);
    }

    /// <summary>
    /// <c>--sound-mix-proof &lt;png prefix&gt;</c> (test aid, needs <c>godot.cmd --windowed</c> for the pictures): ten seconds into the match opens the panel, picks the Heavy bolter's fire sound, raises it and
    /// previews it, shows it in the live list, clicks a sound another player made in the live list, marks one sound raw, exports the mix and quits; a screenshot after each step
    /// (<c>&lt;prefix&gt;_1.png</c> .. <c>_5.png</c>) and the live list and status lines in the log (<c>SOUNDMIX proof</c>).
    /// </summary>
    async System.Threading.Tasks.Task RunSoundMixProof(string prefix)
    {
        async System.Threading.Tasks.Task Wait(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);
        void Shot(int n)
        {
            var err = GetViewport().GetTexture().GetImage().SavePng($"{prefix}_{n}.png");
            GD.Print($"SOUNDMIX proof shot {n}: {err} {prefix}_{n}.png");
        }
        await Wait(10);
        if (_tuner is null) return;
        _tuner.SetOpen(true);
        _tuner.ProofSelect("play_sm_heavy_bolter_fire", own: true);
        await Wait(0.6);
        Shot(1);
        _tuner.ProofPlay();
        await Wait(0.25);
        GD.Print("SOUNDMIX proof voices, no changes:  " + _sound?.Probe("play_sm_heavy_bolter_fire"));
        await Wait(1.5);
        _tuner.ProofEdit(6f, 2f);
        _tuner.ProofPlay();
        await Wait(0.25);
        GD.Print("SOUNDMIX proof voices, +6 dB +2 st: " + _sound?.Probe("play_sm_heavy_bolter_fire"));
        await Wait(1.5);
        _tuner.ProofRaw(true);
        _tuner.ProofPlay();
        await Wait(0.25);
        GD.Print("SOUNDMIX proof voices, raw too:     " + _sound?.Probe("play_sm_heavy_bolter_fire"));
        _tuner.ProofRaw(false);
        await Wait(1.0);
        _tuner.ProofSelect("play_sm_heavy_bolter_fire", own: true);
        Shot(2);
        GD.Print("SOUNDMIX proof live after the preview: " + _tuner.LiveText);
        var others = SoundMixLive.Snapshot().FirstOrDefault(r => r.Source == SoundSource.Other);
        if (others.Event is not null && _tuner.ProofClickLive(others.Event)) GD.Print($"SOUNDMIX proof clicked {others.Event} in the live list");
        else GD.Print("SOUNDMIX proof: no other player's sound in the live list");
        await Wait(0.6);
        Shot(3);
        _tuner.ProofRaw(true);
        await Wait(0.4);
        Shot(4);
        _tuner.ProofExport();
        await Wait(0.4);
        Shot(5);
        GD.Print("SOUNDMIX proof status: " + _tuner.StatusText);
        GetTree().Quit(0);
    }

    /// <summary>
    /// <c>--sound-mix-roll-hop-proof &lt;png prefix&gt;</c> (sound/editor-roll-hop test aid, needs <c>godot.cmd --windowed</c> for the pictures): ten seconds into the match opens the panel on the
    /// Combat moves group, picks the Tactical roll, previews it (log: the voices' volume with no change, then -9 dB), shows the picture <c>&lt;prefix&gt;_roll.png</c>, picks the Devastator / Havoc hop,
    /// raises it and shows <c>&lt;prefix&gt;_hop.png</c> (log lines <c>SOUNDMIX roll-hop proof</c>), and quits.
    /// </summary>
    async System.Threading.Tasks.Task RunRollHopProof(string prefix)
    {
        async System.Threading.Tasks.Task Wait(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);
        void Shot(string n) => GD.Print($"SOUNDMIX roll-hop proof shot {n}: {GetViewport().GetTexture().GetImage().SavePng($"{prefix}_{n}.png")}");
        await Wait(10);
        if (_tuner is null) return;
        _tuner.SetOpen(true);
        _tuner.ProofSelect(SoundMixCatalog.TacticalRollKey, own: true);
        await Wait(0.5);
        _tuner.ProofPlay();
        await Wait(0.25);
        GD.Print("SOUNDMIX roll-hop proof roll, no change: " + _sound?.Probe("play_evade_start"));
        await Wait(1.5);
        _tuner.ProofEdit(-9f, 0f);
        _tuner.ProofPlay();
        await Wait(0.25);
        GD.Print("SOUNDMIX roll-hop proof roll, -9 dB:     " + _sound?.Probe("play_evade_start"));
        await Wait(0.5);
        Shot("roll");
        await Wait(1.5);
        _tuner.ProofSelect(SoundMixCatalog.HeavyHopKey, own: true);
        _tuner.ProofEdit(4f, 0f);
        _tuner.ProofPlay();
        await Wait(0.25);
        GD.Print("SOUNDMIX roll-hop proof hop, +4 dB:      " + _sound?.Probe("play_evade_start"));
        await Wait(0.4);
        Shot("hop");
        GD.Print("SOUNDMIX roll-hop proof status: " + _tuner.StatusText);
        GetTree().Quit(0);
    }

    /// <summary>
    /// sound/mix-expand: the editor's PLAY button. Posts the event once from where the selected side would be heard: your own (at the camera, as the own player's emitter) or another player's
    /// (6 m ahead of the camera). A sound that loops is stopped after 4 s. It goes through the same mix as any sound, so the owner's changes are heard.
    /// </summary>
    void PreviewSound(string evt, bool own)
    {
        if (_sound is null) return;
        var camera = GetViewport()?.GetCamera3D();
        Vector3 at = camera is null ? Vector3.Zero : camera.GlobalPosition + (own ? Vector3.Zero : -camera.GlobalTransform.Basis.Z * 6f);
        var emitter = new SoundEmitter { Position = at, OneShot = true, Own = own, Label = "preview", Follow = null };
        if (own) emitter.Switches.Set(AudioCatalog.PlayerGroup, AudioCatalog.LocalPlayerValue);
        string post = evt;
        if (SoundMixCatalog.IsAlias(evt))   // sound/editor-roll-hop: the roll and hop entries post the evade event as a player of that evade kind
        {
            post = "play_evade_start";
            emitter.EvadeKind = string.Equals(evt, SoundMixCatalog.HeavyHopKey, StringComparison.OrdinalIgnoreCase) ? SoundMixCatalog.EvadeKindHop : SoundMixCatalog.EvadeKindRoll;
        }
        _sound.Post(post, emitter);
        GetTree().CreateTimer(4.0).Timeout += () => { if (IsInstanceValid(this)) _sound?.StopEmitter(emitter); };
    }

    /// <summary>The panel is up: the game takes no input (<see cref="PickerBusy"/> includes it) and the mouse is free.</summary>
    bool TunerOpen => _tuner?.IsOpen == true;

    /// <summary>
    /// F7 shows or hides the panel; Esc closes it. It does not open over the Esc menu, the customizer, the spawn choice or a Musou modal (<see cref="PickerBusy"/>), or while dead.
    /// While it is open every key and click that no slider took is swallowed here, so a click beside the panel cannot recapture the mouse.
    /// </summary>
    bool SoundTunerInput(InputEvent e)
    {
        if (_tuner is null) return false;
        if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.F7 })
        {
            if (!_tuner.IsOpen && (PickerBusy || OwnDead)) return true;
            _tuner.Toggle();
            return true;
        }
        if (!_tuner.IsOpen) return false;
        if (e is InputEventKey { Pressed: true, PhysicalKeycode: Key.Escape }) _tuner.SetOpen(false);
        return e is InputEventKey or InputEventMouse or InputEventJoypadButton or InputEventJoypadMotion;
    }
}
