using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// Test aid (TF7b; the client's revive and extraction prompts, <c>--tfw-test</c>): <c>SM_TFW_DOWN_TEST=&lt;s&gt;</c> in the server's environment. That long after a landing the battle's first human falls:
/// not in the first battle of a run, alone in the second (its retainer comes to revive it), with its retainer in every later one (the extraction's prompt). Off without the variable.
/// </summary>
public sealed partial class GameServer
{
    static readonly float TfwDownSeconds = float.TryParse(Environment.GetEnvironmentVariable("SM_TFW_DOWN_TEST"), System.Globalization.CultureInfo.InvariantCulture, out float s) ? s : -1f;
    TfwBattle? _tfwDownBattle;
    uint _tfwDownTick;
    int _tfwDownLanding;

    void TfwDownTest()
    {
        if (TfwDownSeconds < 0f || World.Tfw is not { } run) return;
        if (!run.Deployed) _tfwDownLanding = 0;
        if (run.Battle is not { } battle) { _tfwDownBattle = null; return; }
        if (!ReferenceEquals(battle, _tfwDownBattle))
        {
            _tfwDownBattle = battle;
            _tfwDownLanding++;
            _tfwDownTick = World.Tick + (uint)(TfwDownSeconds * World.Config.TickRate);
        }
        if (World.Tick != _tfwDownTick || _tfwDownLanding < 2 || run.InField.Count == 0) return;
        int human = run.InField[0];
        if (_tfwDownLanding > 2 && run.RetainerOf(human) is { } retainer) World.TfwKillForTests(retainer.Id);
        World.TfwKillForTests(human);
        Console.WriteLine($"SM_TFW_DOWN_TEST: player {human} falls in landing {_tfwDownLanding}{(_tfwDownLanding > 2 ? " with its retainer" : "")}");
    }
}
