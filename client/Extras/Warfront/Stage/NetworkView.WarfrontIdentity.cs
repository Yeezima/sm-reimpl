using Godot;
using SmClient.Extras.Warfront.Stage;
using SmNet;

namespace SmClient;

/// <summary>
/// Who this client is on a Warfront server (slice W5b; plan section 3): the campaign key (<see cref="WarfrontClientKey"/>) and the display name go out in Identify right after the join, are resent every second
/// until the Hub names this client (the Hub's viewer handle is the key itself), and go out again after every rejoin (a new view reads the same key file). Test clients name their key file with
/// <c>--warfront-key-file &lt;path&gt;</c>, so several run on one PC.
/// </summary>
public partial class NetworkView
{
    ulong WarfrontIdentityKey()
    {
        string? file = CmdArg("warfront-key-file");
        if (file is { Length: > 0 } && !file.StartsWith("--")) return WarfrontClientKey.LoadOrCreate(file);
        if (_bot) GD.Print("warfront: a bot without --warfront-key-file shares this PC's key file with every other such client (the server keeps one seat per key)");
        return WarfrontClientKey.LoadOrCreate(WarfrontClientKey.DefaultPath());
    }

    string WarfrontDisplayName()
    {
        string? name = _client is { PlayerId: >= 0 } c ? c.NameOf(c.PlayerId) : null;
        if (string.IsNullOrWhiteSpace(name)) name = _bot ? "BOT" : "PLAYER";
        return name!.Length > SmNet.Extras.Warfront.WarfrontWire.MaxName ? name[..SmNet.Extras.Warfront.WarfrontWire.MaxName] : name;
    }

    int _wfIdentifyLogged;

    /// <summary>Once a frame: Identify while the server does not know the key, the leader's Deploy and a NewRun while they are pending.</summary>
    void WarfrontSendCommands(double now)
    {
        if (_client is not { Status: ClientStatus.Connected } client) return;
        var m = WarfrontModel;
        if (m.IdentifyDue(now) is { } identify)
        {
            client.SendExtrasCommand(SmNet.Extras.Warfront.WarfrontWire.CommandTag, identify);
            if (_wfIdentifyLogged++ == 0) GD.Print($"t {now:F1} s: warfront: identify sent (key {WarfrontClientKey.Tail(m.Key)}, name {m.Name})");
        }
        if (m.DeployDue(now) is { } deploy)
        {
            client.SendExtrasCommand(SmNet.Extras.Warfront.WarfrontWire.CommandTag, deploy);
            GD.Print($"t {now:F1} s: warfront: deploy to {m.PendingDeploy?.Kind} lane {m.PendingDeploy?.Lane} sent");
        }
        if (m.NewRunDue(now) is { } newRun)
        {
            client.SendExtrasCommand(SmNet.Extras.Warfront.WarfrontWire.CommandTag, newRun);
            GD.Print($"t {now:F1} s: warfront: new run requested");
        }
    }
}
