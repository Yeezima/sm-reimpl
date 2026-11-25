using Godot;
using SmClient.Extras.Warfront.Battle;
using SmNet.Extras.Warfront;

namespace SmClient;

/// <summary>
/// The Battle section in the networked view (slice W13b): tag 47 goes into a <see cref="WarfrontBattleHud"/> on the HUD layer. Nothing here is called until W5b's envelope and frame routing exist (the two lines are in the W13b report):
/// <see cref="WarfrontBattleReceive"/> takes the tag, <see cref="WarfrontBattleTick"/> is the frame (the section goes stale after 3 s, so the line leaves with the battle). Added content (Warfront); no Musou or core line is touched.
/// </summary>
public partial class NetworkView
{
    WarfrontBattleHud? _wfBattle;
    double _wfBattleAt = -100;

    /// <summary>The Battle HUD, for the tests and the screenshots.</summary>
    internal WarfrontBattleHud? WarfrontBattleView => _wfBattle;

    /// <summary>The envelope's Battle section (tag 47); true when it was taken. W5b's <c>WarfrontBattleSection</c> hook calls this.</summary>
    internal bool WarfrontBattleReceive(byte tag, System.ReadOnlySpan<byte> payload)
    {
        if (tag != WarfrontBattleWire.EnvelopeTag) return false;
        if (_hudView is null || !WarfrontBattleWire.TryDecode(payload, out var section)) return true;
        if (_wfBattle is null)
        {
            _wfBattle = new WarfrontBattleHud { StripShown = () => WarfrontSpectating };
            WarfrontFitRoot(_hudView).AddChild(_wfBattle);   // the 1920 by 1080 design space fitted to the window, like the spectator overlay that sits under its line (fallen-watch fix)
        }
        _wfBattleAt = Time.GetTicksMsec() / 1000.0;
        WarfrontDuelBattleSeen(section.IsFortress, _wfBattleAt);   // WARFRONT-HOOK: Chaos Warfront C5c, a Chaos fortress battle's silent closes and the shielded Lord
        _wfBattle.Apply(section, _wfBattleAt);
        return true;
    }

    /// <summary>The frame. W5b's <c>WarfrontBattleFrame</c> hook calls this.</summary>
    internal void WarfrontBattleTick()
    {
        if (_wfBattle is null) return;
        double now = Time.GetTicksMsec() / 1000.0;
        if (now - _wfBattleAt > 3.0) _wfBattle.Clear();
        else _wfBattle.Frame(now);
    }
}
