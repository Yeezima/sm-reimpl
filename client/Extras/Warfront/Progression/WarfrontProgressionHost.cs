using System;
using Godot;
using SmClient.Customizer;
using SmNet.Extras.Warfront;

namespace SmClient.Extras.Warfront.Progression;

/// <summary>
/// The Warfront progression UI as one node (slice W8): the model, the XP HUD and the picker in their own canvas layer, fed by the two envelope sections (tags 44 and 45) and ticked every frame. Nothing here
/// knows the socket: the network view (W5b's seams: a section handler and a frame hook) calls <see cref="Section"/> with each tag it does not know and <see cref="Frame"/> once per frame, and sends the Pick
/// command (<c>WarfrontWire.Encode(new WarfrontClientCommand(Pick, Slot: slot, Pick: pickIndex))</c>) from <see cref="PickRequested"/>. Created only on a Warfront server, so a Musou or Classic client has none of these nodes.
/// </summary>
public sealed partial class WarfrontProgressionHost : CanvasLayer
{
    /// <summary>Above the match HUD, below the dialogs: the picker covers the field, the HUD does not.</summary>
    public const int LayerNumber = 20;

    public WarfrontProgressModel Model { get; }
    public WarfrontXpHud Hud { get; }
    public WarfrontPickerPanel Picker { get; }

    /// <summary>Slot 0 to 3 and the pick index shown (the Pick command's two bytes).</summary>
    public event Action<int, int>? PickRequested;

    public WarfrontProgressionHost(bool bot = false)
    {
        Name = "WarfrontProgression";
        Layer = LayerNumber;
        Model = new WarfrontProgressModel { Bot = bot };
        var root = new WarfrontUiFit { Name = "WarfrontProgressionRoot", Theme = UiKit.Theme };   // the 1920 by 1080 design space, fitted to the window and centred (W5c second pass)
        AddChild(root);
        Hud = new WarfrontXpHud(Model);
        Picker = new WarfrontPickerPanel(Model);
        root.AddChild(Hud);
        root.AddChild(Picker);
        root.MoveChild(Hud, -1);   // the HUD box hides while an offer stands; the LEVEL N banner stays above the picker's dimming
        Model.PickRequested += (slot, index) => PickRequested?.Invoke(slot, index);
    }

    /// <summary>One envelope section; true when the tag is Progress or Offer (decoded, or refused as malformed: either way it is ours and not passed on).</summary>
    public bool Section(byte tag, ReadOnlySpan<byte> payload)
    {
        switch (tag)
        {
            case WarfrontProgressionWire.ProgressTag:
                if (WarfrontProgressionWire.TryDecode(payload, out WarfrontProgressWire p)) Model.Apply(p);
                return true;
            case WarfrontProgressionWire.OfferTag:
                if (WarfrontProgressionWire.TryDecode(payload, out WarfrontOfferWire o)) Model.Apply(o);
                return true;
            default: return false;
        }
    }

    /// <summary>Once per frame: the countdown, the resend and the banner. <see cref="WarfrontProgressModel.AttackSpeedBonus"/> is the value the predicted local player's fire rate should follow.</summary>
    public void Frame(float dt) => Model.Tick(dt);

}
