using SmSim.Extras.Warfront.Campaign;

namespace SmNet.Tests.Extras.Warfront.Campaign;

/// <summary>
/// Executions X8 drop 2 (kept in the Warfront test folder because <c>WarfrontBoundaryTests</c> keeps Warfront namespaces inside it): the overshield is never saved. A new run, a save that
/// round-trips and a reloaded run carry no player vitals at all, so a stage change or a reload starts every player with the plain armour bar.
/// </summary>
public class WarfrontExecutionSaveAcceptanceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(777)]
    [InlineData(123456789)]
    public void Save_CarriesNoVitals_SoNoOvershieldSurvivesAReload(ulong seed)
    {
        var run = WarfrontSave.NewRun(seed);
        string json = run.ToJson();
        foreach (string word in new[] { "Shield", "Overshield", "Armour", "Armor", "Health" })
            Assert.DoesNotContain(word, json, StringComparison.OrdinalIgnoreCase);
        string again = WarfrontSave.FromJson(json).ToJson();
        foreach (string word in new[] { "Shield", "Overshield", "Armour", "Armor", "Health" })
            Assert.DoesNotContain(word, again, StringComparison.OrdinalIgnoreCase);
    }
}
