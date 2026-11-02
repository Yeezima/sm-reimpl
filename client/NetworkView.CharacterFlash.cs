using System;
using Godot;
using SmClient.ImpactRules;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// fx/character-visuals (owner, 2026-10-30, item 13; client only, no wire change): the armour flashes on every character's model (<see cref="ArmourFlashTracker"/>,
/// <see cref="CharacterViewer.SetArmourFlash"/>). The own model follows the prediction's shield exactly; another player's is derived from the hits the client sees on him (the shot
/// record's armour bit) and the class's regeneration delay, since the snapshot carries no other player's shield. Test aids: <c>--no-armour-flash</c> (before / after),
/// <c>--armour-flash-demo</c> (the own model cycles the three flashes).
/// </summary>
public partial class NetworkView
{
    readonly ArmourFlashTracker _armourFlash = new();
    bool NoArmourFlash => _noArmourFlash ??= CmdFlag("no-armour-flash");
    bool? _noArmourFlash;
    /// <summary><c>--armour-flash-demo [hit|break|glow]</c>: null off; empty cycles; a name holds that flash at 0.6 of its curve (for a screenshot).</summary>
    string? ArmourFlashDemoMode => _armourFlashDemo ??= CmdFlag("armour-flash-demo") ? (CmdArg("armour-flash-demo") ?? "cycle") : "";
    string? _armourFlashDemo;
    /// <summary><c>--old-flash-outline</c> (fx/visuals-2 before / after aid): the round-1 glow and break flash on the model's surface with the outline, no halo.</summary>
    bool OldFlashOutlineFlag => _oldFlashOutline ??= CmdFlag("old-flash-outline");
    bool? _oldFlashOutline;

    /// <summary><c>--old-break-glow</c> (fx/red-glow-blood before / after aid): the subtle round-2 red halo for a break, no re-flash on later damage, no blood splash.</summary>
    bool OldBreakGlowFlag => _oldBreakGlow ??= CmdFlag("old-break-glow");
    bool? _oldBreakGlow;

    BloodSplash? _blood;
    double _bloodAt = double.NegativeInfinity;
    const float BloodSize = 1.0f;

    /// <summary>fx/red-glow-blood: the red splash of a hit on a body whose armour is gone, at the hit point, sprayed back toward the shooter.</summary>
    void BloodSplashAt(Vector3 end, Vector3 dir, double delay)
    {
        if (OldBreakGlowFlag) return;
        if (_connectedFor - _bloodAt < 0.05) return;   // rapid fire does not stack streaks on streaks
        _bloodAt = _connectedFor;
        if (_blood is null || !GodotObject.IsInstanceValid(_blood)) { _blood = new BloodSplash { Name = "BloodSplash" }; AddChild(_blood); }
        _blood.Spawn(end, -dir, BloodSize, delay);
    }

    /// <summary>A shot struck <paramref name="victim"/> (another player's model) whose armour was up (<paramref name="armourUp"/>; null: not known, read as up).</summary>
    void CharacterFlashHit(int victim, bool? armourUp)
    {
        if (victim <= 0 || _client is null || victim == _client.PlayerId || _config is null || NoArmourFlash) return;
        float delay = 5f;
        foreach (var o in _lastOthers) if (o.PlayerId == victim) { delay = HitBars.BodyOf(_config, o.Class).ShieldRegenDelay; break; }
        _armourFlash.Hit(victim, _connectedFor, armourUp ?? true, delay);
    }

    /// <summary>An own hit left <paramref name="victim"/> with no armour (the hit report's shield after the hit): its red ring at once.</summary>
    void CharacterFlashBroke(int victim)
    {
        if (victim <= 0 || _client is null || victim == _client.PlayerId || _config is null || NoArmourFlash) return;
        float delay = 5f;
        foreach (var o in _lastOthers) if (o.PlayerId == victim) { delay = HitBars.BodyOf(_config, o.Class).ShieldRegenDelay; break; }
        _armourFlash.Broke(victim, _connectedFor, delay);
    }

    /// <summary>Per frame: the flashes of the own model and of every other drawn model.</summary>
    void CharacterFlashFrame()
    {
        if (_client is null || _config is null || _client.Status != ClientStatus.Connected) return;
        int own = _client.PlayerId;
        var me = _client.Predicted;
        CharacterViewer.OldFlashOutline = OldFlashOutlineFlag;
        CharacterViewer.OldBreakGlow = ArmourFlashTracker.Old = OldBreakGlowFlag;
        if (NoArmourFlash)
        {
            _local?.SetArmourFlash(0f, 0f, 0f);
            foreach (var character in _remotes.Values) character.SetArmourFlash(0f, 0f, 0f);
            return;
        }
        _armourFlash.OwnShield(own, _connectedFor, me.Shield, Loadouts.Body(_config, me).ShieldMax, me.Alive, me.Health);
        if (_local is not null)
        {
            if (ArmourFlashDemoMode is { Length: > 0 } demo)
            {
                var d = demo switch { "hit" => (0.6f, 0f, 0f), "break" => (0f, 0.6f, 0f), "glow" => (0f, 0f, 0.6f), _ => ArmourFlashDemoAt(_connectedFor) };
                _local.SetArmourFlash(d.Item1, d.Item2, d.Item3, ring: true);
            }
            else { var f = _armourFlash.At(own, _connectedFor); _local.SetArmourFlash(f.Hit, f.Break, f.Glow, _armourFlash.BreakRing(own, _connectedFor)); }
        }
        foreach (var state in _lastOthers)
        {
            if (!state.Alive) { _armourFlash.Reset(state.PlayerId); }
            if (!_remotes.TryGetValue(state.PlayerId, out var character)) continue;
            var f = state.Alive ? _armourFlash.At(state.PlayerId, _connectedFor) : (0f, 0f, 0f);
            character.SetArmourFlash(f.Item1, f.Item2, f.Item3, _armourFlash.BreakRing(state.PlayerId, _connectedFor));
        }
        _armourFlash.Prune(id => id == own || _remotes.ContainsKey(id));
    }

    /// <summary>The demo cycle: 6 s, a hit flash at 1 s, a break at 2.5 s, the recharge glow at 4 s.</summary>
    static (float Hit, float Break, float Glow) ArmourFlashDemoAt(double t)
    {
        double c = t % 6.0;
        return (ArmourFlashTracker.Fade(c - 1.0, ArmourFlashTracker.HitSeconds), ArmourFlashTracker.Fade(c - 2.5, ArmourFlashTracker.BreakSeconds), ArmourFlashTracker.Glow(c - 4.0));
    }
}
