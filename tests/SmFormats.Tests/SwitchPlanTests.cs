using SmContent;
using SmSim;

namespace SmFormats.Tests;

/// <summary>
/// fix/stow-right: how a weapon switch is drawn (<see cref="SwitchPlan"/>): the old weapon's draw played backwards down to its attach tag (released onto its stow marker there), then the
/// new weapon's draw from <see cref="SwitchPlan.DrawLead"/> before its tag (taken into the hand at the tag). The install's two clips as numbers only.
/// </summary>
public class SwitchPlanTests
{
    static readonly IReadOnlyDictionary<string, float> Mask = new Dictionary<string, float> { ["RIG_L_Thigh"] = 0f };
    static readonly SwitchClip Rifle = new("SM_Swap_Rifle_to_Rifle_01", 0.467f, Mask, 0.2335f, 0.21f);
    static readonly SwitchClip Pistol = new("SM_Swap_Rifle_to_Melee_01", 0.667f, Mask, 0.4002f, 0.267f);
    static readonly SwitchAnimations Both = new(Rifle, Pistol, []);

    [Fact]
    public void Primary_to_pistol_puts_the_primary_away_on_the_rifle_draw_backwards_then_draws_the_pistol()
    {
        var plan = SwitchPlan.For(Both, WeaponSlot.Ranged, WeaponSlot.Pistol)!;
        Assert.Equal(2, plan.Segments.Count);
        var (stow, draw) = (plan.Segments[0], plan.Segments[1]);
        Assert.Equal((Rifle.Clip, Rifle.Length, Rifle.SwapAt), (stow.Clip, stow.From, stow.To));   // backwards, from the end down to the attach tag
        Assert.Equal(Pistol.Clip, draw.Clip);
        Assert.Equal(Pistol.SwapAt - Rifle.SwapAt, draw.From, 4);   // the rifle draw's reach before the pistol's tag
        Assert.Equal(Pistol.Length, draw.To, 4);
        float total = (Rifle.Length - Rifle.SwapAt) + (Pistol.Length - draw.From);
        Assert.Equal((Rifle.Length - Rifle.SwapAt) / total, plan.Release, 4);   // the primary leaves the hand where the stow piece ends
        Assert.Equal(plan.Release, stow.End, 4);
        Assert.Equal(plan.Release, draw.Start, 4);
        Assert.Equal(1f, draw.End);
        Assert.Equal(plan.Release + Rifle.SwapAt / total, plan.Take, 4);   // the pistol comes at its own tag inside the draw piece
        Assert.Equal(Pistol.SwapAt, draw.ClipTime(plan.Take), 3);
        Assert.InRange(plan.Release, 0.3f, 0.33f);
        Assert.InRange(plan.Take, 0.62f, 0.65f);
    }

    [Fact]
    public void Pistol_to_primary_holsters_the_pistol_on_its_draw_backwards_then_plays_the_whole_rifle_draw()
    {
        var plan = SwitchPlan.For(Both, WeaponSlot.Pistol, WeaponSlot.Ranged)!;
        var (stow, draw) = (plan.Segments[0], plan.Segments[1]);
        Assert.Equal((Pistol.Clip, Pistol.Length, Pistol.SwapAt), (stow.Clip, stow.From, stow.To));
        Assert.Equal((Rifle.Clip, 0f, Rifle.Length), (draw.Clip, draw.From, draw.To));
        Assert.Equal(Rifle.SwapAt, draw.ClipTime(plan.Take), 3);   // the rifle comes off the back marker at the draw's own tag
        Assert.True(plan.Release < plan.Take);
    }

    [Fact]
    public void Clip_time_runs_backwards_through_a_reversed_piece_and_is_clamped_at_its_ends()
    {
        var plan = SwitchPlan.For(Both, WeaponSlot.Ranged, WeaponSlot.Pistol)!;
        var stow = plan.Segments[0];
        Assert.Equal(Rifle.Length, stow.ClipTime(0f), 4);
        Assert.Equal(Rifle.SwapAt, stow.ClipTime(stow.End), 4);
        Assert.True(stow.ClipTime(stow.End * 0.5f) < Rifle.Length && stow.ClipTime(stow.End * 0.5f) > Rifle.SwapAt);
        Assert.Equal(Rifle.SwapAt, stow.ClipTime(0.9f), 4);
        Assert.Same(stow, plan.SegmentAt(0.1f));
        Assert.Same(plan.Segments[1], plan.SegmentAt(0.5f));
        Assert.Same(plan.Segments[1], plan.SegmentAt(1.2f));
    }

    [Fact]
    public void The_joint_cross_fades_from_the_stow_piece_held_at_its_end()
    {
        var plan = SwitchPlan.For(Both, WeaponSlot.Ranged, WeaponSlot.Pistol)!;
        Assert.Null(plan.Crossfade(plan.Release - 0.01f, 0.1f));
        var joint = plan.Crossfade(plan.Release + 0.05f, 0.1f);
        Assert.NotNull(joint);
        Assert.Same(plan.Segments[0], joint.Value.Previous);
        Assert.Equal(0.5f, joint.Value.Progress, 3);
        Assert.Null(plan.Crossfade(plan.Release + 0.11f, 0.1f));
        Assert.Null(plan.Crossfade(plan.Release + 0.05f, 0f));
    }

    [Fact]
    public void The_old_rule_and_missing_clips_fall_back_to_one_clip_with_one_swap()
    {
        var old = SwitchPlan.For(Both, WeaponSlot.Ranged, WeaponSlot.Pistol, oldRule: true)!;
        Assert.Single(old.Segments);
        Assert.Equal((Pistol.Clip, 0f, Pistol.Length), (old.Segments[0].Clip, old.Segments[0].From, old.Segments[0].To));
        Assert.Equal(0.6f, old.Release, 3);
        Assert.Equal(old.Release, old.Take);

        var noPistol = SwitchPlan.For(new SwitchAnimations(Rifle, null, []), WeaponSlot.Ranged, WeaponSlot.Pistol)!;
        Assert.Single(noPistol.Segments);
        Assert.Equal(Rifle.Clip, noPistol.Segments[0].Clip);
        Assert.Equal(0.5f, noPistol.Release, 2);

        Assert.Null(SwitchPlan.For(Both, WeaponSlot.Pistol, WeaponSlot.Pistol));
        Assert.Null(SwitchPlan.For(new SwitchAnimations(null, null, []), WeaponSlot.Ranged, WeaponSlot.Pistol));
    }
}
