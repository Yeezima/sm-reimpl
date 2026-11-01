using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// visual/lascannon-stow (the owner's playtest of 2026-10-31: "change the lascannon position while stowed away (not equipped) as devastator and havoc with bolt pistol equipped.
/// they currently clip. they should not intersect with each other at rest"): where a stowed weapon hangs when the wargear's own stow marker does not fit its model.
/// <para>
/// The data gives every rifle and heavy weapon the same stow (<c>stow_info</c>: handle <c>rig_prop_m_chest</c> on <c>mkr_stow_bolter</c>, a marker under <c>RIG_SpineA</c> on the lower back).
/// That marker lays the gun across the lower back with the barrel to the marine's right and a little down. A bolter's barrel ends about 0.45 m from its handle and stays behind the
/// back; the lascannon's muzzle is 1.39 m from its handle (<c>smtool weapon-parts</c>), so its barrel came out past the right hip, through the right forearm and along the right leg.
/// </para>
/// <para>
/// **Ours** (spec "Stowed lascannon"): the two lascannon models hang upright behind the Devastator's / Havoc's backpack (on <c>RIG_SpineD</c>, the bone the backpack follows), the barrel
/// 20 degrees from straight up toward the marine's right, the gun's side flat against the pack; the loyalist model's rigid placeholder cable is drawn only in hand. The data's marker
/// cannot hold the lascannon anywhere else on the back: the backpack fills it from 1.4 m up. The heavy bolter and the plasma cannon keep the data's marker (their muzzles also reach the
/// right hand; left for the owner). <c>--old-lascannon-stow</c> (test aid) restores the data's marker; <c>--lascannon-stow up,forward,left,degrees</c> tries another spot.
/// </para>
/// </summary>
public static class WeaponStowSpots
{
    /// <summary>
    /// A stow spot in the stow bone's own space: the weapon's handle node is put at <paramref name="Handle"/> (metres) with the barrel (the handle node's +Z: the marine models'
    /// <c>RIG_Prop_M_Chest</c> is the model root turned -90 degrees about Y, so its +Z is the root's -X, toward <c>fx_Muzzle</c>) along <paramref name="Barrel"/> and the gun's top (the
    /// handle's +X, the root's +Z) along <paramref name="Top"/> (made square to the barrel). Directions in the spine bone's frame: X up the body, Y forward, Z to the marine's left.
    /// </summary>
    public sealed record Spot(string Bone, Num.Vector3 Handle, Num.Vector3 Barrel, Num.Vector3 Top, string Label)
    {
        /// <summary>The handle node's frame in the bone's space (row-vector, as <see cref="WeaponMount.BodyLocal"/>).</summary>
        public Num.Matrix4x4 BodyLocal
        {
            get
            {
                var z = Num.Vector3.Normalize(Barrel);
                var x = Top - Num.Vector3.Dot(Top, z) * z;
                x = Num.Vector3.Normalize(x);
                var y = Num.Vector3.Cross(z, x);
                return new Num.Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, Handle.X, Handle.Y, Handle.Z, 1);
            }
        }
    }

    /// <summary>
    /// The lascannon's spot (both races' models share the handle frame: <c>lascannon_mp_basic</c>, <c>chaos_lascannon_mp</c>): the handle at the height of <c>RIG_SpineD</c>, 0.57 m
    /// behind it (against the backpack's rear face) and 0.15 m to the marine's left; the barrel 20 degrees from straight up toward his right. The stock ends at the lower back, the muzzle
    /// about 0.6 m above the head; clear of both arms and the pistol in the relaxed pistol idle, the pistol walk and the run (checked against screenshots, 2026-10-31).
    /// </summary>
    static readonly Spot Lascannon = Tune(Tilted("RIG_SpineD", new Num.Vector3(0f, -0.57f, 0.15f), 20f, "ours:lascannon behind the backpack"));

    /// <summary><c>--lascannon-stow up,forward,left,degrees</c> (test aid, for refining the spot against screenshots): the handle's place in the bone's space and the barrel's tilt from straight up.</summary>
    static Spot Tune(Spot spot)
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--lascannon-stow");
        if (i < 0 || i + 1 >= args.Length) return spot;
        var v = args[i + 1].Split(',').Select(s => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : float.NaN).ToArray();
        if (v.Length != 4 || v.Any(float.IsNaN)) { GD.PrintErr("--lascannon-stow: needs up,forward,left,degrees"); return spot; }
        return Tilted(spot.Bone, new Num.Vector3(v[0], v[1], v[2]), v[3], spot.Label);
    }

    /// <summary>A spot with the barrel <paramref name="fromUp"/> degrees from straight up the spine toward the marine's right, the gun's top square to it in the back's plane (its side flat to the back).</summary>
    static Spot Tilted(string bone, Num.Vector3 handle, float fromUp, string label)
    {
        float a = fromUp * MathF.PI / 180f;
        return new Spot(bone, handle, new Num.Vector3(MathF.Cos(a), 0f, -MathF.Sin(a)), new Num.Vector3(MathF.Sin(a), 0f, MathF.Cos(a)), label);
    }

    /// <summary>Models (visual names, any case) whose stow is ours instead of the data's marker.</summary>
    public static readonly IReadOnlyDictionary<string, Spot> ByModel = new Dictionary<string, Spot>(StringComparer.OrdinalIgnoreCase)
    {
        ["lascannon_mp_basic"] = Lascannon,
        ["chaos_lascannon_mp"] = Lascannon,
    };

    /// <summary><c>--old-lascannon-stow</c> (test aid, before / after): the data's marker for every weapon.</summary>
    public static readonly bool Old = OS.GetCmdlineUserArgs().Contains("--old-lascannon-stow");

    /// <summary>
    /// Whether the model's simulated parts are drawn only in hand: the Space Marine lascannon's <c>GEO_cable</c> (the power cable to the backpack, which the original simulates and we
    /// draw rigid in its bind pose, a loop below the stock) would hang through the legs on our spot. **Ours**, with the spot; off with <c>--old-lascannon-stow</c>.
    /// </summary>
    public static bool HidesSimulatedWhenStowed(WeaponAttachment attachment) => !Old && attachment.Stow is { Bone: not null } && ByModel.ContainsKey(attachment.VisualName);

    /// <summary>The stow mount to use for an attachment: ours for the models in <see cref="ByModel"/> (the spot's own bone, RIG_SpineD, and the spot in its space), else the data's. The rig keeps the result as the item's stow mount, so the attachment bone and the transform always agree.</summary>
    public static WeaponMount? For(WeaponAttachment attachment)
    {
        if (Old || attachment.Stow is not { Bone: not null } stow || !ByModel.TryGetValue(attachment.VisualName, out var spot)) return attachment.Stow;
        return stow with { BodyNode = spot.Label, Bone = spot.Bone, BodyLocal = spot.BodyLocal, ModelAxes = false };
    }
}
