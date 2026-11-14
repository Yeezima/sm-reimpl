using System;
using System.Threading.Tasks;
using Godot;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Test aid <c>--tfw-bay-capture</c> (before <c>--tfw-test &lt;dir&gt;</c>; a windowed run on a real Musou server; add <c>--write-movie f.avi --fixed-fps 30</c> for the clip): the scripted player uses each wall asset of the
/// troop bay through the real keys. At each station it walks into the zone, turns to the wall prop, takes a still with the HUD prompt, presses E and takes a still of what opens: EQUIPMENT (the Armory: the loadout
/// screen), FIRING RANGE (the airlock: the confirm, TELEPORT, the range, and back by the return pad), DESTINATION (the drop deployment panel: the war map, DEPLOY). One TFWTEST PASS or FAIL per step.
/// </summary>
public partial class NetworkView
{
    /// <summary>Faces the wall prop of <paramref name="station"/>: the first one listed for it.</summary>
    void TfwBayFace(TfwStation station, float pitch)
    {
        foreach (var p in TfwBayProps.All)
        {
            if (p.Station != station) continue;
            var wall = TfwHub!.ToWorld(new Num.Vector3(TfwBayProps.WallX(p), p.Y, TfwShip.FloorZ + p.AboveFloor));
            var me = _client!.Predicted.Position;
            _yaw = MathF.Atan2(-(wall.X - me.X), wall.Y - me.Y) + (station == TfwStation.Range ? 0.7f : 0.42f);   // the prop right of the marine, who stands left of the middle in the third-person view
            _pitch = pitch;
            return;
        }
    }

    async Task TfwBayCapture()
    {
        var client = _client!;
        var hub = TfwHub!;
        await TfwFrames(30);
        TfwCheck("the bay's wall props are built from the owner's kit", _tfwHubView?.GetNodeOrNull<Node3D>("TfwBay") is { } bay && bay.GetChildCount() >= TfwBayProps.All.Length, "no TfwBay");
        TfwCheck("no floor pad is drawn at the stations", _tfwHubView is not null && _tfwHubView.GetNodeOrNull<Node3D>("RangeButton") is null);

        // EQUIPMENT: the Armory.
        if (!await TfwWalkTo(hub.EquipmentStation, "walked to the Armory's zone")) return;
        TfwBayFace(TfwStation.Equipment, 0.1f);
        await TfwFrames(30);
        TfwCheck("the Armory's prompt shows", _tfwHud is { Data.Station: "E: EQUIPMENT" }, _tfwHud?.Data.Station ?? "no HUD");
        GD.Print($"TFWBAY yaw {_yaw:F2} pitch {_pitch:F2} at {client.Predicted.Position}");
        await TfwShot("bay-armory-prompt");
        await TfwTap(Key.E);
        if (!await TfwUntil("E at the Armory opens the loadout screen", () => _pickerCreator is { Visible: true } && _tfwEquipmentShown, 90.0)) return;
        await TfwWait(1.5);
        await TfwShot("bay-armory-used");
        _pickerCreator!.Back();
        await TfwUntil("BACK returns to the game", () => !PickerBusy, 3.0);

        // FIRING RANGE: the airlock.
        if (!await TfwWalkTo(hub.RangeStation, "walked to the airlock's zone")) return;
        TfwBayFace(TfwStation.Range, 0.05f);
        await TfwFrames(30);
        TfwCheck("the airlock's prompt shows", _tfwHud is { Data.Station: "E: FIRING RANGE" }, _tfwHud?.Data.Station ?? "no HUD");
        await TfwShot("bay-airlock-prompt");
        await TfwTap(Key.E);
        if (!await TfwUntil("E at the airlock opens the range confirm", () => _tfwConfirm is not null, 3.0)) return;
        await TfwWait(1.0);
        await TfwShot("bay-airlock-used");
        ((TfwRangeConfirm)_tfwConfirm!).GoButton.EmitSignal(BaseButton.SignalName.Pressed);
        var range = TfwRangeArea!;
        if (!await TfwUntil("the server moves the player to the range", () => range.Contains(client.Predicted.Position), 6.0)) return;
        await TfwWait(1.5);
        await TfwShot("bay-range-arrived");
        bool onPad = await TfwWalkTo(range.ReturnStation, "walked to the return pad", 20.0);
        if (onPad) await TfwTap(Key.E); else _tfwState.Commands.Issue(TfwCommand.RangeBack);
        await TfwUntil("the player is back in the Stormbird", () => hub.Contains(client.Predicted.Position), 8.0);
        await TfwWait(1.0);

        // DESTINATION: the drop deployment panel, then the battle.
        if (!await TfwWalkTo(hub.DestinationStation, "walked to the deployment panel's zone")) return;
        TfwBayFace(TfwStation.Destination, 0.1f);
        await TfwFrames(30);
        TfwCheck("the deployment panel's prompt shows", _tfwHud is { Data.Station: "E: DESTINATION" }, _tfwHud?.Data.Station ?? "no HUD");
        await TfwShot("bay-deploy-prompt");
        await TfwTap(Key.E);
        if (!await TfwUntil("E at the deployment panel opens the war map", () => _tfwPanel is not null, 3.0)) return;
        await TfwWait(1.5);
        await TfwShot("bay-deploy-used");
        _tfwPanel!.DeployButton(TfwLane.B)?.EmitSignal(BaseButton.SignalName.Pressed);
        await TfwUntil("the battle starts", () => TfwNow.Phase is TfwRunPhase.Travel or TfwRunPhase.Battle, 15.0);
        await TfwWait(2.0);
        await TfwShot("bay-deploy-battle");
    }
}
