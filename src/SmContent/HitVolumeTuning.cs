using SmFormats.Havok;
using SmFormats.Obpk;
using SmSim;

namespace SmContent;

/// <summary>
/// The server's hit volumes from the multiplayer marine rig's ragdoll (M20; Chaos marines share the rig): every ragdoll body's
/// primitives at their rest transforms, in the character frame (the rig's model space: X right, Y forward, Z up, feet at the
/// origin). Spheres, capsules, boxes and convex hulls (by the hull's own planes) are taken as they are (<b>supported by
/// evidence</b>); that the ragdoll is the hit geometry, that the rest pose stands for every pose, and that only the head body takes headshots are
/// <b>interpretations</b> (spec/behavior.md).
/// </summary>
public static class HitVolumeTuning
{
    /// <summary>Most volumes accepted (a few per body; the marine has 22).</summary>
    public const int MaxVolumes = 64;

    /// <summary>Reads the rig's ragdoll from the install (read-only) and builds the volumes; null (with notes) when unusable.</summary>
    public static IReadOnlyList<HitVolume>? Load(string gameDir, List<string> notes) => Load(gameDir, RigProfiles.Marine, notes);

    /// <summary>A profile's hit volumes from its ragdoll (Ork O2: the boy's 15 bodies; the body named "Head" is the head zone).</summary>
    public static IReadOnlyList<HitVolume>? Load(string gameDir, RigProfile profile, List<string> notes)
    {
        byte[]? rig = null;
        using (var archive = ObpkArchive.Open(profile.ArchivePath(gameDir)))
            archive.ReadEntries(e => e.FileName == profile.Entry, (_, d) => rig = d.ToArray());
        if (rig is null) { notes.Add($"hit volumes: '{profile.Entry}' not found; placeholders"); return null; }
        return FromRagdoll(HavokRagdoll.Read(new HavokPackfile(rig), notes), notes);
    }

    /// <summary>
    /// Builds the volumes: one per primitive, named after its body (without <c>Ragdoll_RIG_</c>; "#2" and on for a body's
    /// further primitives); a body whose name contains "Head" is the head zone, every other body is body. Head volumes come
    /// first. Null when there is no head volume, none at all, more than <see cref="MaxVolumes"/>, or an unusable value.
    /// </summary>
    public static IReadOnlyList<HitVolume>? FromRagdoll(IReadOnlyList<RagdollBody> bodies, List<string> notes)
    {
        var volumes = new List<HitVolume>();
        foreach (var body in bodies)
        {
            string name = body.Name.StartsWith("Ragdoll_RIG_", StringComparison.OrdinalIgnoreCase) ? body.Name["Ragdoll_RIG_".Length..] : body.Name;
            var zone = name.Contains("Head", StringComparison.OrdinalIgnoreCase) ? HitZone.Head : HitZone.Body;
            for (int i = 0; i < body.Shapes.Count; i++)
            {
                var s = body.Shapes[i];
                string label = i == 0 ? name : $"{name} #{i + 1}";
                volumes.Add(s.Kind switch
                {
                    RagdollShapeKind.Sphere => HitVolume.Sphere(label, zone, s.A, s.Radius),
                    RagdollShapeKind.Capsule => HitVolume.Capsule(label, zone, s.A, s.B, s.Radius),
                    RagdollShapeKind.Hull => HitVolume.Hull(label, zone, s.Planes!, s.Vertices!),
                    _ => HitVolume.Box(label, zone, s.A, s.B, s.Rotation),
                });
            }
        }
        if (volumes.Count == 0 || volumes.Count > MaxVolumes || !volumes.Any(v => v.Zone == HitZone.Head) || !volumes.All(v => v.IsValid))
        {
            notes.Add($"hit volumes: {volumes.Count} ragdoll volumes, {volumes.Count(v => v.Zone == HitZone.Head)} head, {volumes.Count(v => !v.IsValid)} unusable; placeholders");
            return null;
        }
        return volumes.OrderBy(v => v.Zone == HitZone.Head ? 0 : 1).ToArray();
    }
}
