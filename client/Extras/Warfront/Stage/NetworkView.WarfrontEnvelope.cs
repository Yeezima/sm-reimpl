using System;
using Godot;
using SmClient.Extras.Warfront.Stage;
using SmNet.Extras.Warfront;
using SmSim;

namespace SmClient;

/// <summary>
/// Warfront's envelope sections on the client (slice W5b; <see cref="WarfrontWire"/>): the dispatcher <c>NetworkView.Extras.cs</c> offers every section to <see cref="WarfrontEnvelope"/> first. On a
/// Warfront server the Hub, Stage, Down and Results sections (tags 40 to 43) feed <see cref="WarfrontStageModel"/>; the later slices' tags (the squad's 32 to 34, the progression's 44 and 45, the coop's 46)
/// go to the fan-out hooks below, which are no-ops until their slice implements them. On any other server nothing here runs.
/// </summary>
public partial class NetworkView
{
    WarfrontStageModel? _wfModel;

    /// <summary>This match is a Warfront campaign (the profile's flag in the config the handshake agreed).</summary>
    bool WarfrontOn => _config is { } c && c.Extras.HasFlag(ContentExtras.Warfront);

    /// <summary>The stage model of this connection, made on first use (the key and the name are this player's).</summary>
    internal WarfrontStageModel WarfrontModel => _wfModel ??= MakeWarfrontModel();

    // The fan-out for later slices: each declared here as a no-op; the slice that owns it implements it in its own Extras/Warfront folder.
    partial void WarfrontProgressionSection(byte tag, ReadOnlySpan<byte> payload, ref bool handled);
    partial void WarfrontProgressionFrame(PlayerState me, double dt);
    partial void WarfrontSquadSection(byte tag, ReadOnlySpan<byte> payload, ref bool handled);
    partial void WarfrontSquadFrame(PlayerState me, double dt);
    partial void WarfrontCoopSection(byte tag, ReadOnlySpan<byte> payload, ref bool handled);
    partial void WarfrontCoopFrame(PlayerState me, double dt);
    partial void WarfrontChaosSection(byte tag, ReadOnlySpan<byte> payload, ref bool handled);   // Chaos Warfront C4c: tag 51 (Chaos/Breach/; kinds 16 to 31 on to C5c)

    /// <summary>Warfront's tags, before Musou's. Sections of a tag nobody here owns are left to the dispatcher's unknown count.</summary>
    partial void WarfrontEnvelope(byte tag, ReadOnlySpan<byte> payload, ref bool handled)
    {
        if (!WarfrontOn) return;
        double now = _connectedFor;
        switch (tag)
        {
            case WarfrontWire.HubTag:
                if (!WarfrontModel.ApplyHub(payload)) WarfrontDropped++;
                handled = true;
                return;
            case WarfrontWire.StageTag:
            {
                var before = WarfrontModel.Last;
                if (!WarfrontModel.ApplyStage(payload, now)) WarfrontDropped++;
                else if (WarfrontModel.Stage is { } s && s.State != WarfrontStageState.None && (s.State != before || s.State == WarfrontStageState.Preparing && s.Percent / 10 != _wfLoggedPercent / 10))
                {
                    _wfLoggedPercent = s.Percent;
                    GD.Print($"t {now:F1} s: warfront stage {s.State}: {s.Map} ({s.Kind} lane {s.Lane}), {s.Percent} %, {s.ElapsedSeconds} s, arrived {s.Arrived} of {s.Expected}, \"{s.Status}\"");
                }
                handled = true;
                return;
            }
            case WarfrontWire.DownTag:
                if (!WarfrontModel.ApplyDown(payload, now)) WarfrontDropped++;
                handled = true;
                return;
            case WarfrontWire.ResultsTag:
                if (WarfrontModel.ApplyResults(payload) is { } record) WarfrontRecordArrived(record);
                handled = true;
                return;
            case SmNet.Extras.Warfront.WarfrontProgressSaveWire.EnvelopeTag:   // codebase fix 1: the host's copy of this player's progression
                WarfrontProgressReceive(payload);
                handled = true;
                return;
            case 44 or 45:
                WarfrontProgressionSection(tag, payload, ref handled);
                return;
            case >= 32 and <= 34:
                WarfrontSquadSection(tag, payload, ref handled);
                return;
            case 46:
                WarfrontCoopSection(tag, payload, ref handled);
                return;
            case SmNet.Extras.Warfront.WarfrontBattleWire.EnvelopeTag:   // W13b (routed: the lead's fan-out line)
                handled = WarfrontBattleReceive(tag, payload);
                return;
            case WarfrontWire.VoxTag:   // Guardsmen G2: the Guardsmen's vox line (Battle/NetworkView.WarfrontVox.cs)
                WarfrontVoxReceive(payload);
                handled = true;
                return;
            case SmNet.Extras.Warfront.WarfrontChaosWire.Tag:   // Chaos Warfront C4c (routed: the breaches, and C5's finale kinds)
                WarfrontChaosSection(tag, payload, ref handled);
                return;
        }
    }

    int _wfLoggedPercent = -100;

    /// <summary>Warfront sections that did not decode (dropped whole).</summary>
    public int WarfrontDropped { get; private set; }
}
