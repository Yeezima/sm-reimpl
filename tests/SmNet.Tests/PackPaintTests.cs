using SmContent;
using SmContent.Customization;
using SmSim;
using Xunit;

namespace SmNet.Tests;

/// <summary>
/// Which slot paints a worn jump pack (owner's decision of 2026-10-29: the chest). Engine-free, synthetic data only; the client's <c>WeaponRig.SetPackPaint</c> and the
/// appearance editor's check of the pack's material colour are in <c>--ui-selftest</c>.
/// </summary>
public class PackPaintTests
{
    static ResolvedColour Colour(string name, float r = 0.1f) => new(name, [r, 0.2f, 0.3f], [0f, 0f, 0f]);

    static ResolvedAppearance Look(Dictionary<string, ResolvedPaint> paint, string classKey = "sm - assault marine") =>
        new(CustomizationCatalog.SpaceMarine, classKey, null, [new ResolvedPiece("torso", "torso_a", ["torso_a_pkg"], false)], paint, ResolvedDecal.None, ResolvedDecal.None, null,
            new HashSet<string> { "backpack" }, new HashSet<string>(), "", []);

    [Fact]
    public void The_pack_follows_the_chest_slot()
    {
        Assert.Equal("torso", WornPackPaint.Slot);
        var chest = new ResolvedPaint(Colour("Red"), Colour("Gold"), Colour("Black"), true);
        var look = Look(new()
        {
            ["head"] = new(Colour("Green"), Colour("Green"), null, false),
            ["backpack"] = new(Colour("Blue"), Colour("Blue"), Colour("Blue"), false),   // the data's slot of the pack: hidden on the jump-pack classes, not used
            ["torso"] = chest,
        });
        Assert.Same(chest, WornPackPaint.Of(look));
    }

    [Fact]
    public void Changing_the_chest_colour_changes_the_pack_paint_and_nothing_else_does()
    {
        var head = new ResolvedPaint(Colour("Green"), Colour("Green"), null, false);
        var look1 = Look(new() { ["head"] = head, ["torso"] = new(Colour("Red"), Colour("Red"), null, false) });
        var look2 = Look(new() { ["head"] = head, ["torso"] = new(Colour("Blue"), Colour("Blue"), null, false) });
        var look3 = Look(new() { ["head"] = new(Colour("Black"), Colour("Black"), null, false), ["torso"] = look1.Paint["torso"] });
        Assert.NotEqual("Red", WornPackPaint.Of(look2)!.Primary!.Name);
        Assert.Equal("Blue", WornPackPaint.Of(look2)!.Primary!.Name);
        Assert.Equal("Red", WornPackPaint.Of(look1)!.Primary!.Name);
        Assert.Equal(WornPackPaint.Of(look1), WornPackPaint.Of(look3));   // another part's colour leaves the pack alone
    }

    [Fact]
    public void The_pack_takes_the_chest_secondary_and_tertiary_and_the_pattern_flag_too()
    {
        var look = Look(new() { ["torso"] = new(Colour("Red"), Colour("Gold"), Colour("Black"), true) });
        var paint = WornPackPaint.Of(look)!;
        Assert.Equal(("Red", "Gold", "Black", true), (paint.Primary!.Name, paint.Secondary!.Name, paint.Tertiary!.Name, paint.PatternEnabled));
    }

    [Fact]
    public void A_single_set_look_paints_the_pack_with_its_default_and_a_look_without_paint_leaves_it_to_the_side_scheme()
    {
        var single = new ResolvedPaint(Colour("Ultramarines Blue"), Colour("Ultramarines Blue"), Colour("Burnished Gold"), false);
        Assert.Same(single, WornPackPaint.Of(Look(new() { ["default"] = single })));
        Assert.Null(WornPackPaint.Of(Look(new() { ["head"] = single })));   // no chest, no default: null = the side's default scheme as before
        Assert.Null(WornPackPaint.Of(Look(new())));
    }

    [Fact]
    public void The_look_key_changes_with_the_chest_colour_so_a_new_look_is_built_for_the_pack()
    {
        var a = Look(new() { ["torso"] = new(Colour("Red"), Colour("Red"), null, false) });
        var b = Look(new() { ["torso"] = new(Colour("Blue"), Colour("Blue"), null, false) });
        Assert.NotEqual(SmClient.Looks.LookKeys.Of(a, true), SmClient.Looks.LookKeys.Of(b, true));
    }
}
