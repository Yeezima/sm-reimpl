using SmSim;

namespace SmClient;

/// <summary>
/// Musou weight pass MW5, transitions: the marines' stance changes (ready, idle, the locomotion blends) cross-fade over at least 0.25 s instead of 0.15 s
/// (<see cref="SmNet.Extras.Musou.MusouTransitionRules.Blend"/>, through <see cref="CharacterViewer.ExtrasBlendSeconds"/>; instant changes stay instant). Measured with
/// <c>--transition-probe</c> (headless, pooled runs): moving to ready turned the body 107 degrees a frame (median), now 47; ready to idle jerk p90 78 to 52 cm/frame², ready to melee
/// 60 to 44, melee to move 19 to 14; idle to ready unchanged (64 to 66). An eased body heading and a floor on clip entries were tried and dropped (each made a switch worse).
/// Switch <c>--no-musou-transitions</c>.
/// </summary>
public partial class NetworkView
{
    static readonly bool NoMusouTransitions = CmdFlag("no-musou-transitions");

    bool MusouTransitionsOn => MusouWeightOn && !NoMusouTransitions;

    /// <summary>Marks the tracked marines' viewers for the cross-fade floors (once a frame, from the weight frame).</summary>
    void MusouTransitionsFrame()
    {
        bool on = MusouTransitionsOn;
        foreach (var (_, s, viewer, _) in _musouMarines) viewer.MusouBlendFloors = on && !IsOrkVoice(s);
    }
}
