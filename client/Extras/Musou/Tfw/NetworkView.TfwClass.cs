using System;
using Godot;
using SmClient.Looks;
using SmNet;
using SmNet.Extras.Musou;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// Musou Mode pass 2 on the client: the class change in the Stormbird (item 2).
/// <para>
/// Item 2. The picker's DEPLOY already sends the loadout as a Setup (<see cref="DeployFromPicker"/>: the server stores it for the next life and acknowledges it). In the Stormbird that is not
/// enough, so once the acknowledgement of that very Setup is in, the frame sends <see cref="TfwCommand.ChangeClass"/> (resent until the war's echo, like the Deploy), and the server makes a fresh life on
/// the spot. The picker's other buttons (SET ACTIVE, closing the menu) send a Setup as before and change nothing now. When the own class changes the waiting look is taken at once
/// (<see cref="OwnLooks"/> would wait for a respawn or a scoreboard), so the colours change with the model, under the smoke (<see cref="TfwSmoke"/>, tag 11).
/// </para>
/// <para>
/// Item 14's bars over the elites are gone (the owner, 2026-11-02): tag 13 still arrives and fills <c>_tfwState.Bars</c>, and nothing draws it.
/// </para>
/// </summary>
public partial class NetworkView
{
    ushort? _tfwClassSeen, _tfwClassWaiting;
    double _tfwClassWaitingSince;
    int _tfwClassFrom = -1;
    int _tfwSmokes;
    System.Collections.Generic.IReadOnlyList<MusouShown> _tfwShown = [];

    /// <summary>Class changes the Deploy button has sent a command for, and smoke clouds started (the scripted test and the log).</summary>
    public int TfwClassChangesSent { get; private set; }
    public int TfwSmokesStarted => _tfwSmokes;

    /// <summary>The Setup the Deploy button just sent waits for its acknowledgement, then the command goes; the own look follows the class.</summary>
    void TfwClassFrame(in TfwWarView war, PlayerState me)
    {
        if (_bot || _client is not { } client) return;
        TfwClassTestFrame();   // the scripted test (NetworkView.TfwClassTest.cs); off without --tfw-test-class

        // The Deploy button: its Setup is the one the picker marked deployed.
        if (_pickerDeployed && _setupSent && _tfwClassSeen != _lastSetupSequence)
        {
            _tfwClassSeen = _lastSetupSequence;
            if (war.Phase == TfwRunPhase.Hub && me.Alive)
            {
                _tfwClassWaiting = _lastSetupSequence;
                _tfwClassWaitingSince = _connectedFor;
                _tfwClassFrom = me.Class;
            }
        }
        if (_tfwClassWaiting is { } waiting)
        {
            if (war.Phase != TfwRunPhase.Hub || !me.Alive || _connectedFor - _tfwClassWaitingSince > 6.0) _tfwClassWaiting = null;
            else if (client.LastSetupAck is { } ack && ack.Sequence == waiting && !client.SetupPending)
            {
                _tfwClassWaiting = null;
                var sequence = _tfwState.Commands.Issue(TfwCommand.ChangeClass);
                _ackShownUntil = 0;   // the acknowledgement's "applies when you respawn" is not true here: the class changes now
                TfwClassChangesSent++;
                GD.Print($"t {_connectedFor:F1} s: war: class change to {ClassDisplayName(ack.Loadout.Class)} (setup {waiting}, command {sequence})");
            }
        }

        // The class changed under the player: its waiting look becomes the shown one now, not at a respawn that does not come.
        if (_tfwClassFrom >= 0 && me.Alive && me.Class != _tfwClassFrom)
        {
            if (PickerRules.RespawnText(_tfwClassFrom, me.Class, ClassDisplayName(me.Class)) is { Length: > 0 } line) { _respawnLine = line; _respawnLineUntil = _connectedFor + 4.0; }   // "You are now playing ..."
            _tfwClassFrom = -1;
            _setupSent = false;   // the Setup has been applied: a death later must not say "applies when you respawn" again
            _ownLooks.Update(false, 0, true);   // the next frame's Update(alive) is then a respawn of an acknowledged request: it promotes
            GD.Print($"t {_connectedFor:F1} s: war: the own class is now {ClassDisplayName(me.Class)}");
        }
    }

    readonly System.Collections.Generic.Dictionary<(uint Tick, ushort Player), double> _tfwSmokePlayed = new();

    /// <summary>
    /// Starts a cloud for each new smoke record (tag 11): where it says, as old as it already is. It reads this frame's sections itself, not <c>TfwClientState.NewFire</c>, which the guards' frame has
    /// emptied by now; each (tick, player) once (the server repeats a record in three messages).
    /// </summary>
    void TfwSmokeFrame()
    {
        foreach (var section in _tfwSections)
        {
            if (section.Tag != TfwWire.FireTag || TfwWire.ReadFire(section.Payload) is not { } records) continue;
            foreach (var fire in records)
            {
                if (fire.Kind != TfwFireKind.Smoke || !_tfwSmokePlayed.TryAdd((fire.Tick, fire.Subject), _connectedFor)) continue;
                _tfwSmokes++;
                if (Lean || _gameSpace is null || DisplayServer.GetName() == "headless") continue;
                float age = _config is null ? 0f : Math.Max(0f, (_tfwState.NewestTick - fire.Tick) / (float)Math.Max(1, _config.TickRate));
                if (age >= TfwSmoke.TotalSeconds) continue;
                var smoke = new TfwSmoke(((uint)fire.Subject << 16) ^ fire.Tick, age) { Position = new Vector3(fire.At.X, fire.At.Y, fire.At.Z) };
                _gameSpace.AddChild(smoke);
                GD.Print($"t {_connectedFor:F1} s: war: smoke of player {fire.Subject} at ({fire.At.X:F1}, {fire.At.Y:F1}, {fire.At.Z:F1})");
            }
        }
        if (_tfwSmokePlayed.Count > 32)
            foreach (var key in new System.Collections.Generic.List<(uint, ushort)>(_tfwSmokePlayed.Keys)) if (_connectedFor - _tfwSmokePlayed[key] > 10.0) _tfwSmokePlayed.Remove(key);
    }

    /// <summary>The smoke and the frags of the war's frame.</summary>
    void TfwCloudsFrame()
    {
        TfwSmokeFrame();
        TfwFragFrame();   // NetworkView.TfwMelee.cs
    }
}
