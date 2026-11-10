using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Godot;
using SmContent;
using SmContent.Extras.Musou.Arsenal;
using SmSim.Extras.Musou.Arsenal;

namespace SmClient;

/// <summary>
/// W1, the autocannon's model (docs/new-weapons-design.md section 4, Presentation): the install's <c>turret_aa</c> (every autocannon wargear names it; 1,883 triangles, about 2.8 m long, a
/// <c>RIG_Prop_M_Chest</c> locator at its grip), held and stowed exactly as the heavy bolter: the heavy bolter's attachment with this model in its place (its wield mount names the chest prop, its stow
/// mount the bolter marker, both by node name, so the mounts fit the new model). Original asset loaded; the placement is a stand-in for the Chaos file's own mounts (supported by evidence, to check in a
/// still). Nothing without the heavy bolter in the catalogue (it is in every multiplayer class set).
/// <para>
/// W1c (the owner, 2026-11-10: "shorten it to 1.6m"): drawn with its barrel shortened (<see cref="AutocannonBarrel"/>: the muzzle 1.6 m from the model's root, was 2.62 m; only what lies ahead of the
/// receiver moves) and its muzzle locator moved with it, so the flash and the tracer leave the new muzzle. Test aids: <c>--old-autocannon-barrel</c> (W1's full barrel), <c>--autocannon-muzzle m</c>
/// (another muzzle distance, for a still).
/// </para>
/// </summary>
public sealed partial class WeaponRig
{
    /// <summary>The model package and visual the autocannon wears (the install's, <c>data/packages/turret_aa.oppc</c>).</summary>
    internal const string AutocannonPackage = "turret_aa", AutocannonVisual = "turret_aa";

    /// <summary>Test aid <c>--old-autocannon-barrel</c>: the full 2.62 m barrel of W1 (before W1c).</summary>
    static readonly bool OldAutocannonBarrel = Array.IndexOf(OS.GetCmdlineUserArgs(), "--old-autocannon-barrel") >= 0;

    /// <summary>Test aid <c>--autocannon-muzzle m</c>: the muzzle's distance from the model's root (default the owner's <see cref="AutocannonBarrel.Muzzle"/>).</summary>
    static readonly float AutocannonMuzzle = AutocannonMuzzleArg() ?? AutocannonBarrel.Muzzle;

    static float? AutocannonMuzzleArg()
    {
        var a = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(a, "--autocannon-muzzle");
        return i >= 0 && i + 1 < a.Length && float.TryParse(a[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && float.IsFinite(v) ? v : null;
    }

    /// <summary>The shortened copy of each loaded <c>turret_aa</c> asset (one per <see cref="WeaponAssets"/>; built once, shared by every rig).</summary>
    static readonly ConditionalWeakTable<WeaponAsset, WeaponAsset> ShortAutocannons = new();

    partial void AutocannonModel(HandRole role, string wargear, WeaponAssets assets, ref WeaponAttachment? attachment, ref WeaponAsset? asset)
    {
        if (!wargear.Equals(ArsenalRules.Autocannon, StringComparison.OrdinalIgnoreCase)) return;
        attachment = AutocannonAttachment(assets.Catalogue.Held(Arsenal.HeavyBolterFile) ?? assets.Catalogue.Held("csm_mp_exotic_heavy_bolter"));
        if (OldAutocannonBarrel || attachment is null || assets.Get(attachment) is not { } full) return;   // no asset: the rig draws the model as read
        asset = ShortAutocannons.GetValue(full, f => ShortBarrel(f, AutocannonBarrel.Knots(AutocannonMuzzle)));
    }

    /// <summary>The heavy bolter's attachment with the autocannon's model, without the heavy bolter's visual upgrades; null without a heavy bolter.</summary>
    internal static WeaponAttachment? AutocannonAttachment(WeaponAttachment? heavy) => heavy is null ? null : heavy with
    {
        Wargear = ArsenalRules.Autocannon, Name = "auto_cannon", VisualPackage = AutocannonPackage, VisualName = AutocannonVisual,
        Upgrades = [], HiddenByDefault = new System.Collections.Generic.HashSet<string>(),
    };

    /// <summary>
    /// <paramref name="full"/> with every vertex and locator moved along the model's +Y (its length, the barrel's axis; the baked meshes and the locators are both in the model's root space) by
    /// <see cref="AutocannonBarrel.Along"/>: the barrel shorter, the muzzle with it, all behind the receiver's front as read. Only Y changes, along parts that run with the axis or move whole, so the
    /// normals and texture coordinates are kept. The handle node (the mounts) is in the object tree and does not move.
    /// </summary>
    static WeaponAsset ShortBarrel(WeaponAsset full, (float From, float To)[] knots)
    {
        var model = full.Model with { Locators = full.Model.Locators.Select(l => Moved(l, knots)).ToList() };
        return new WeaponAsset(model, full.Mesh is { } mesh ? ShortMesh(mesh, knots) : null, full.Worn, full.Triangles, full.Parts, full.Hidden, full.Rigid, full.Magazine)
        {
            WornPaint = full.WornPaint, StowHidden = full.StowHidden is { } inHand ? ShortMesh(inHand, knots) : null,
            SwingWield = full.SwingWield, OneHandGrip = full.OneHandGrip, SwingGrip = full.SwingGrip,
        };
    }

    static WeaponLocator Moved(WeaponLocator locator, (float From, float To)[] knots)
    {
        var m = locator.ToRoot;
        float y = AutocannonBarrel.Along(m.M42, knots);
        if (y == m.M42) return locator;
        m.M42 = y;
        return locator with { ToRoot = m };
    }

    static ArrayMesh ShortMesh(ArrayMesh mesh, (float From, float To)[] knots)
    {
        var copy = new ArrayMesh();
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            var arrays = mesh.SurfaceGetArrays(s);
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            for (int v = 0; v < vertices.Length; v++) vertices[v].Y = AutocannonBarrel.Along(vertices[v].Y, knots);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices;
            copy.AddSurfaceFromArrays(mesh.SurfaceGetPrimitiveType(s), arrays);
            copy.SurfaceSetMaterial(copy.GetSurfaceCount() - 1, mesh.SurfaceGetMaterial(s));
        }
        return copy;
    }
}
