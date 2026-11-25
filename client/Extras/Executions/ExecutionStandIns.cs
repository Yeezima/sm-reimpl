using System;
using System.Collections.Generic;
using Godot;
using SmSim.Extras.Executions;

namespace SmClient;

/// <summary>
/// Executions X5: the labelled stand-in, a <b>placeholder demonstration</b>, played only when a row the catalogue holds cannot be shown with its own clips on this character
/// (a clip that would not decode, or a rig whose skeleton is not the clip's): the attacker's own heavy strike timed so its end lands on the row's kill event, and the victim's own
/// death clip from the kill event, both on the row's clock. Never another execution's clips (brief: no lookalike substitutes). Logged once per row.
/// </summary>
public static class ExecutionStandIns
{
    static readonly HashSet<string> Logged = new(StringComparer.OrdinalIgnoreCase);

    static void Note(ExecutionRow row, string half, List<string> notes)
    {
        if (!Logged.Add(row.Id + "/" + half)) return;
        string line = $"executions: {row.Id} {half} plays a stand-in (placeholder demonstration): the install's clip cannot be shown on this rig";
        GD.Print(line);
        notes.Add(line);
    }

    /// <summary>A heavy strike of the character's own rig (null: none).</summary>
    public static string? AttackerClip(MarineAssets assets) => assets.FindAnimation(["SM_MEL_CS_XATK_HVY_01", "SM_MEL_XATK_HVY_01"], "_HVY");

    /// <summary>A death clip of the character's own rig (null: none).</summary>
    public static string? VictimClip(MarineAssets assets) => assets.FindAnimation(["SM_DTH_Front_01", "ORK_DTH_Front_01"], "DTH");

    public static void Attacker(CharacterViewer character, ExecutionRow row, float seconds, List<string> notes)
    {
        Note(row, "attacker", notes);
        if (character.Assets is not { } assets || AttackerClip(assets) is not { } clip || assets.Animation(clip) is not { } anim) return;
        character.Pose(clip, ExecutionPresentMath.RawTime(seconds, MathF.Max(row.KillSeconds, 0.05f), anim.Animation.Duration), ClipBlends.Default);
    }

    public static void Victim(CharacterViewer character, ExecutionRow row, float seconds, List<string> notes)
    {
        Note(row, "victim", notes);
        if (character.Assets is not { } assets || VictimClip(assets) is not { } clip || assets.Animation(clip) is not { } anim) return;
        character.Pose(clip, Math.Clamp(seconds - row.KillSeconds, 0f, anim.Animation.Duration), ClipBlends.Default);
    }
}
