using System.Numerics;

namespace SmContent;

/// <summary>
/// fx/impact-pass (the owner, 2026-10-29: "make the start of the trail always match the 'exhaust' location on the jumppack"): how far along a locator's +Y axis (the axis an
/// effect played there points along) the drawn model still reaches within <see cref="Radius"/> of that axis, i.e. where a flame leaves the nozzle. Measured on the install
/// (<c>smtool weapon-parts abil_jumppack --exhaust jet</c>): the Space Marine pack's <c>fx_L_jet</c> / <c>fx_R_jet</c> sit at their nozzles' exits (0.02 m), the Chaos pack's only
/// markers, <c>fx_L_jet_inside_center</c> / <c>fx_R_jet_inside_center</c>, 0.28 m inside them (the data plays the Chaos thrust there). The thrust is then started at the exit
/// (**ours**: the original's renderer may hide the inside with its own draw order; here the flame's first 0.28 m showed against the pack's side).
/// </summary>
public static class NozzleExit
{
    /// <summary>Distance from the axis within which the model counts as the nozzle around it (m; ours: a nozzle is about 0.25 m across).</summary>
    public const float Radius = 0.15f;

    /// <summary>Metres from the locator along its +Y to the farthest drawn vertex within <see cref="Radius"/> of that axis; 0 when none lies ahead (or the axis is degenerate).</summary>
    public static float Along(WeaponModel model, WeaponLocator locator, float radius = Radius)
    {
        var y = new Vector3(locator.ToRoot.M21, locator.ToRoot.M22, locator.ToRoot.M23);
        if (y.LengthSquared() < 1e-10f) return 0f;
        var axis = Vector3.Normalize(y);
        float far = 0f;
        foreach (var part in model.Parts)
        {
            if (!part.Visible || part.BlockIndex < 0 || part.BlockIndex >= model.Model.Mesh.Blocks.Count) continue;
            foreach (var p in model.Model.Mesh.Blocks[part.BlockIndex].Positions)
            {
                var w = Vector3.Transform(p, part.ToRoot) - locator.Position;
                float along = Vector3.Dot(w, axis);
                if (along > far && (w - axis * along).LengthSquared() <= radius * radius) far = along;
            }
        }
        return far;
    }
}
