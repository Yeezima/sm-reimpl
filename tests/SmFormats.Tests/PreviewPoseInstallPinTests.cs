using SmContent;

namespace SmFormats.Tests;

/// <summary>
/// ui/customizer-ork (the owner's request of 2026-10-29: "use the bp idle stance as the pose for all classes when customizing"): the customizer previews' pose,
/// <see cref="RigClipTable.PistolRelaxedIdle"/>, is the clip the install's <c>SM_Pistol_NAV</c> "Idle Relaxed" state binds (<see cref="SprintTransitions.PistolRelaxedIdle"/>),
/// first in the marine table's preview idles, and the rig has it. Skipped when no install is found (CI). Reads names only; no original bytes.
/// </summary>
public class PreviewPoseInstallPinTests
{
    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    /// <summary>A fact that is skipped unless the game install is found on this machine.</summary>
    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (the pin needs the player's install; never in CI)";
        }
    }

    [Fact]
    public void The_bolt_pistol_idle_leads_the_marine_preview_idles() =>
        Assert.Equal(RigClipTable.PistolRelaxedIdle, RigProfiles.Marine.Clips.PreviewIdleCandidates[0]);

    [InstallFact]
    public void The_preview_pose_is_the_pistol_graphs_relaxed_idle()
    {
        var transitions = SprintTransitions.Load(Game.Value!);
        Assert.Equal(RigClipTable.PistolRelaxedIdle, transitions.PistolRelaxedIdle);
        var character = RigProfiles.Marine.LoadCharacter(Game.Value!);
        Assert.True(character.LoadAnimation(RigClipTable.PistolRelaxedIdle).Animation.Duration > 0f);
    }
}
