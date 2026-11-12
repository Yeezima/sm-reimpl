using System.Linq;
using Godot;
using SmContent.Extras.Musou;

namespace SmClient;

/// <summary>
/// <c>--old-spear-hew</c> (musou/spear-authored, 2026-11-12), read from the client's user arguments like every client switch: Musou's power spear plays pass 3's hand-keyed hew 1
/// (<see cref="ClawSpearClips.SpearHew1Original"/>) instead of the Blender-authored one. SmContent cannot call Godot, so the client sets <see cref="ClawSpearClips.OldHew1"/> (the environment
/// variable <c>SM_OLD_SPEAR_HEW=1</c> sets it there too). Called where a match view or the preview stage is made, before any clip is shown.
/// </summary>
static class SpearHewSwitch
{
    public static bool Apply()
    {
        bool on = OS.GetCmdlineUserArgs().Contains("--old-spear-hew");
        if (on) ClawSpearClips.OldHew1 = true;
        if (OS.GetCmdlineUserArgs().Contains("--old-spear-pass2")) ClawSpearClips.OldPass2 = true;
        if (OS.GetCmdlineUserArgs().Contains("--old-spear-pass3")) ClawSpearClips.OldPass3 = true;   // spear pass 3 (2026-11-12): the carry of pass 2 (hip fist standing, chest fist moving, the shaft leaning forward)
        if (OS.GetCmdlineUserArgs().Contains("--old-spear-pass4b")) ClawSpearClips.OldPass4b = true;   // spear pass 4b (2026-11-12): pass 4's hands and strokes
        if (OS.GetCmdlineUserArgs().Contains("--old-spear-pass4")) ClawSpearClips.OldPass4 = true;   // spear pass 4 (2026-11-12): pass 3's carry and strokes
        return on;
    }
}
