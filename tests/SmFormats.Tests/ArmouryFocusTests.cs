using SmClient.Customizer.Model;
using Xunit;

namespace SmFormats.Tests;

/// <summary>The customizer's <c>--step</c> and <c>--armoury-focus</c> aids: which step and which row a name means.</summary>
public class ArmouryFocusTests
{
    static readonly string[] Steps = ["CLASS", "WEAPONS", "EQUIPMENT", "PERKS", "LOOK", "SUPPORT ABILITIES"];

    [Theory]
    [InlineData("weapons", 1)]
    [InlineData("LOOK", 4)]
    [InlineData("support abilities", 5)]
    [InlineData("support", 5)]          // the first word: the documented `--step support` reaches SUPPORT ABILITIES
    [InlineData("support-abilities", 5)]
    [InlineData("abilities", -1)]       // only the first word, never a later one
    [InlineData("retainers", -1)]       // an added page's alias is its own hook's
    [InlineData("", -1)]
    [InlineData(null, -1)]
    public void A_step_is_its_whole_name_or_its_first_word(string? wanted, int expected) => Assert.Equal(expected, ArmouryFocus.StepIndex(Steps, wanted));

    [Fact]
    public void A_step_the_page_does_not_show_is_never_chosen()
    {
        Assert.Equal(-1, ArmouryFocus.StepIndex(Steps, "support", i => i < 5));
        Assert.Equal(3, ArmouryFocus.StepIndex(Steps, "perks", i => i < 5));
    }

    static readonly string[] Rows = ["ARTILLERY STRIKE", "DROP POD", "TWO RETAINERS, RANDOM CLASSES"];

    [Theory]
    [InlineData("drop pod", 1)]
    [InlineData("drop-pod", 1)]
    [InlineData("retainers", 2)]                         // part of the title
    [InlineData("Two retainers, random classes", 2)]     // the whole title, any case
    [InlineData("3", 2)]                                 // a 1-based number
    [InlineData("4", -1)]
    [InlineData("0", -1)]
    [InlineData("lascannon", -1)]
    [InlineData(" ", -1)]
    public void A_row_is_its_title_part_of_it_or_its_number(string wanted, int expected) => Assert.Equal(expected, ArmouryFocus.RowIndex(Rows, wanted));

    [Fact]
    public void The_whole_title_wins_over_an_earlier_partial_match() => Assert.Equal(1, ArmouryFocus.RowIndex(["DROP POD STRIKE", "DROP POD"], "drop pod"));
}
