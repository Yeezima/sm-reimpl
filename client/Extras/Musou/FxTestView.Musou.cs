namespace SmClient;

/// <summary>
/// musou/gpu-particles (test aid of the effects test view <c>--fx</c>): plays the effects as Musou's blood does (<c>NetworkView.OrkBlood</c>): <c>--fx-amount f</c>
/// (<see cref="FxEffect.Amount"/>), <c>--fx-keep-smoke</c> (<see cref="FxEffect.KeepSmoke"/>) and <c>--fx-gamma</c> (<see cref="FxEffect.GammaColours"/>), for the CPU / GPU spray stills.
/// Nothing without those arguments; the test view plays no match, so no content profile applies.
/// </summary>
public partial class FxTestView
{
    partial void ExtrasTestEffect(FxEffect e)
    {
        if (_args.TryGetValue("fx-amount", out var amount)) e.Amount = F(amount, 1f);
        if (_args.ContainsKey("fx-keep-smoke")) e.KeepSmoke = true;
        if (_args.ContainsKey("fx-gamma")) e.GammaColours = true;
    }
}
