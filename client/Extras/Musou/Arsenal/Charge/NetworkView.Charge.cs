using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmNet;
using SmSim;
using SmSim.Extras.Musou.Arsenal;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The Remote Detonators on the client (W4, docs/new-weapons-design.md section 7; Musou Mode only). The charge is drawn with the install's melta bomb model (<c>RemoteDetonatorsVisual</c>, the
/// <c>ExtrasThrownVisual</c> seam); when its detonation arrives (the projectile record's detonated flag, no new network event) the ring is started from it
/// (<c>ExtrasThrownDetonated</c>, called for every thrown detonation of a Musou match): the centre blast is the core's own detonation effect, and the 16 spots are placed with the client's own collision
/// world by the shared <see cref="DemolitionChargeRules.SpotAt"/> (the same bearings, ring, wall and ground rules as the server) and played on the server's schedule (3 ticks after the centre, then every 2) as
/// the original frag explosion, a sound on every fourth. The damage is the server's: the ring is drawn only. <c>--no-charge-ring</c> draws the centre blast alone.
/// </summary>
public partial class NetworkView
{
    static readonly bool NoChargeRing = OS.GetCmdlineUserArgs().Contains("--no-charge-ring");

    /// <summary>The melta bomb's model (Verified-local: <c>data/packages/melta_bomb.oppc</c>, <c>melta_bomb.dcm</c> / <c>melta_bomb.O3d</c>); the blueprint's own is the Ork stikkbomb (owner D21).</summary>
    public const string MeltaPackage = "melta_bomb", MeltaObject = "melta_bomb";

    /// <summary>Ring spots drawn, charges seen (a test aid's counts).</summary>
    internal int ChargeSpotsDrawn { get; private set; }
    internal int ChargesSeen { get; private set; }

    partial void RemoteDetonatorsVisual(ThrownSpec item, ref ProjectileVisual? visual)
    {
        if (item.Name.Equals(ArsenalRules.RemoteDetonators, StringComparison.OrdinalIgnoreCase)) visual = new ProjectileVisual(item.Projectile.Blueprint, MeltaPackage, MeltaObject);
    }

    /// <summary>A thrown item's detonation reached the view: a Remote Detonators charge starts its ring.</summary>
    partial void ExtrasThrownDetonated(ProjectileView d)
    {
        if (_config is null || !_config.Extras.HasFlag(ContentExtras.Musou) || !d.Thrown || d.Weapon < 0 || d.Weapon >= _config.Thrown.Count) return;
        if (!_config.Thrown[d.Weapon].Name.Equals(ArsenalRules.RemoteDetonators, StringComparison.OrdinalIgnoreCase)) return;
        ChargesSeen++;
        if (_bot) GD.Print($"t {_connectedFor:F1} s: charge #{d.Id} went off at {d.Position.X:F1},{d.Position.Y:F1},{d.Position.Z:F1}: the ring follows");
        if (NoChargeRing || Lean || DisplayServer.GetName() == "headless" || _client is null) return;
        int frag = _config.Thrown.ToList().FindIndex(t => SmSim.Extras.Musou.Campaign.TfwCombatRules.IsFrag(t));
        if (frag < 0) return;
        bool master = ChargeMasterCrafted();
        // The centre effect is the core's, one, at x1.6 (the config's thrown blast scale for the charge, SimConfig.Extras.cs): the 16 m blast against the data's 10 m. Master-Crafted's 24 m centre keeps that size.
        var world = _client.Collision;
        DemolitionChargeRules.Ray? ray = world is null ? null : (o, dir, len) => world.Weapons.Raycast(o, dir, len) is { } hit ? hit.Distance : null;
        float tick = 1f / _config.TickRate;
        for (int k = 0; k < DemolitionChargeRules.Spots; k++)
        {
            var at = DemolitionChargeRules.SpotAt(d.Position, d.Id, k, DemolitionChargeRules.Ring(master), ray);
            var view = new ProjectileView(0, frag, 0, at, ProjectileFlags.Thrown);
            bool sound = k % 4 == 0;
            double delay = DemolitionChargeRules.SpotTick(k) * tick;
            GetTree().CreateTimer(delay).Timeout += () => ChargeSpot(view, sound);
        }
    }

    /// <summary>
    /// Whether the ring is drawn at Master-Crafted Wargear's size: a charge's thrower is not on the wire, so a player (this one or a remote one the client knows) who carries the Remote Detonators
    /// with the perk decides it (several charge carriers who differ in the perk: the larger ring, ours).
    /// </summary>
    bool ChargeMasterCrafted()
    {
        if (_config is null || _client is null) return false;
        bool Has(PlayerState p) => Loadouts.Equipment(_config, p) is { } item && item.Name.Equals(ArsenalRules.RemoteDetonators, StringComparison.OrdinalIgnoreCase)
            && Loadouts.Upgrades(_config, p).Has(DemolitionChargeRules.MasterCrafted);
        if (Has(_client.Predicted)) return true;
        foreach (var p in _client.RemotePlayers().Players) if (Has(p)) return true;
        return false;
    }

    void ChargeSpot(ProjectileView view, bool sound)
    {
        if (!IsInstanceValid(this) || !IsInsideTree()) return;
        if (sound) SoundDetonation(view, "charge ring");
        var shake = _shake;
        if (FxDetonation(view)) ChargeSpotsDrawn++;
        _shake = shake + (_shake - shake) * 0.3f;   // a spot shakes the camera less than a blast (17 of them in a second)
    }
}
