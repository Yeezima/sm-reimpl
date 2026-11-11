using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace SmContent.Extras.Musou;

/// <summary>
/// The adapter from a Blender-authored strike (<c>tools/blender/spear_hew_author.py</c>; export format <c>sm-spear-authored-clip/1</c>) to the existing <see cref="WeaponPoseClip"/> overlay
/// contract (added content, Musou only; <c>docs/claw-spear-animations.md</c> section 3g). No new runtime concept: the export is already in the overlay's own channel semantics (Turn,
/// Aim, Move, Foot keys in the stance frame, the grip tracks), sampled at <c>sample_rate_hz</c> and read back as <see cref="PoseEase.Linear"/> keys, so no authoring constraint or IK is
/// solved a second time at run time (the overlay's own leg solve and foot pin are the runtime's and run as for every clip). The timing is the template's (<see cref="WeaponPoseClip"/>
/// of the original strike): the adapter refuses data whose name, length, hit time or base stance disagree, so an authored clip can never change when the blow lands. Deterministic: the
/// same JSON gives the same keys. Blender's solved joint positions go to a private check file (never committed); the install test compares them with the in-game pose.
/// </summary>
public static class AuthoredPoseClip
{
    public const string Format = "sm-spear-authored-clip/1";

    /// <summary>The clip of <paramref name="json"/> laid on <paramref name="template"/>'s identity and timing.</summary>
    public static WeaponPoseClip FromJson(string json, WeaponPoseClip template)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("format").GetString() != Format) throw new InvalidDataException($"authored clip: format is not {Format}");
        string name = root.GetProperty("name").GetString() ?? "";
        if (!string.Equals(name, template.Name, StringComparison.Ordinal)) throw new InvalidDataException($"authored clip: it is '{name}', the template is '{template.Name}'");
        var timing = root.GetProperty("timing");
        float duration = (float)timing.GetProperty("duration").GetDouble(), hit = (float)timing.GetProperty("hit_time").GetDouble();
        if (MathF.Abs(duration - template.Duration) > 1e-3f) throw new InvalidDataException($"authored clip: length {duration} s, the strike's is {template.Duration} s");
        if (MathF.Abs(hit - template.HitTime) > 1e-3f) throw new InvalidDataException($"authored clip: hit at {hit} s, the strike's is {template.HitTime} s");
        string baseClip = root.GetProperty("rest_pose").GetProperty("clip").GetString() ?? "";
        if (!string.Equals(baseClip, template.BaseClip, StringComparison.Ordinal)) throw new InvalidDataException($"authored clip: laid over {baseClip}, the strike's base is {template.BaseClip}");
        if (root.GetProperty("sample_rate_hz").GetDouble() < 24) throw new InvalidDataException("authored clip: sample rate below 24 Hz");

        var channels = root.GetProperty("channels").EnumerateArray().Select(c =>
        {
            var kind = c.GetProperty("kind").GetString() switch
            {
                "Turn" => PoseChannelKind.Turn, "Aim" => PoseChannelKind.Aim, "Move" => PoseChannelKind.Move, "Foot" => PoseChannelKind.Foot,
                var other => throw new InvalidDataException($"authored clip: channel kind '{other}'"),
            };
            string? toward = kind == PoseChannelKind.Aim ? c.GetProperty("toward").GetString() : null;
            var keys = c.GetProperty("keys").EnumerateArray().Select(k =>
            {
                // [t, a, b, c, weight]: Turn yaw, pitch, roll; Aim azimuth, elevation, 0; Move / Foot forward, right, up (PoseKey's fields).
                float F(int i) => (float)k[i].GetDouble();
                return new PoseKey(F(0), F(1), F(2), F(3), PoseEase.Linear, F(4));
            }).ToArray();
            if (keys.Length < 2) throw new InvalidDataException($"authored clip: channel {c.GetProperty("bone").GetString()} has fewer than two keys");
            return new PoseChannel(c.GetProperty("bone").GetString()!, keys, kind, toward);
        }).ToArray();

        // The grip tracks: the off hand's hold (weight) and where the right hand holds the shaft (metres from the butt, in Yaw: ClawSpearClips.G / P).
        var grip = root.GetProperty("grip").EnumerateArray().Select(k => new PoseKey((float)k[0].GetDouble(), 0f, 0f, 0f, PoseEase.Linear, (float)k[1].GetDouble())).ToArray();
        var point = root.GetProperty("grip_point").EnumerateArray().Select(k => new PoseKey((float)k[0].GetDouble(), (float)k[1].GetDouble(), 0f, 0f, PoseEase.Linear, 1f)).ToArray();
        return template with
        {
            Channels = channels,
            Grip = grip,
            GripPoint = point,
            Description = "authored in Blender (hand-keyed controls, FK and two-bone solves, sampled at "
                + root.GetProperty("sample_rate_hz").GetDouble().ToString("0", CultureInfo.InvariantCulture) + " Hz): " + template.Description,
        };
    }

    /// <summary>The embedded authored clip <paramref name="file"/> (<c>Authored/</c>) on <paramref name="template"/>.</summary>
    public static WeaponPoseClip LoadEmbedded(string file, WeaponPoseClip template) => FromJson(EmbeddedJson(file), template);

    /// <summary>The embedded export <paramref name="file"/> as text (the tests read its <c>check</c> block).</summary>
    public static string EmbeddedJson(string file)
    {
        // Authoring preview: SM_AUTHORED_CLIP_DIR names a folder whose <file> replaces the embedded export (iterate in Blender without rebuilding; never set in a match).
        if (Environment.GetEnvironmentVariable("SM_AUTHORED_CLIP_DIR") is { Length: > 0 } dir && File.Exists(Path.Combine(dir, file))) return File.ReadAllText(Path.Combine(dir, file));
        var asm = typeof(AuthoredPoseClip).Assembly;
        string res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("." + file, StringComparison.Ordinal))
            ?? throw new FileNotFoundException($"authored clip resource {file} is not embedded");
        using var s = asm.GetManifestResourceStream(res)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
