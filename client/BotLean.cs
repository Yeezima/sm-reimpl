using System.Collections.Generic;
using Godot;
using SmNet;

namespace SmClient;

/// <summary>
/// perf/pass-1, merged into perf/lean-bots' lean mode (<see cref="LeanBot.Decide"/>: a headless <c>--bot</c> without <c>--bot-full-load</c> or a presentation
/// test aid; the hosted match's bots, <c>bots.cmd</c> and the release's launcher bots are all lean with no extra flag). perf/lean-bots skips every
/// presentation-only load (NetworkView.Lean.cs); this adds what costs processor time per frame: the frame rate is capped at <see cref="LeanBot.FrameCap"/>
/// (the server's tick rate; <c>--bot-fps n</c> overrides, Godot's own <c>--max-fps</c> before <c>--</c> wins), the engine's physics steps at
/// <see cref="LeanBot.PhysicsTicks"/> a second and the effects are off (<c>--no-fx</c>). Before, each hosted bot was a full client with no frame cap: about
/// 134 frames a second, half a CPU core and 0.65 GB each. The bot's decisions are made from the simulation's state as before. Values are ours.
/// </summary>
static class BotLean
{
    public static void Apply(Dictionary<string, string?> args)
    {
        int fps = LeanBot.FrameCapFor(args.GetValueOrDefault(LeanBot.FpsFlag));
        if (Engine.MaxFps == 0) Engine.MaxFps = fps;   // an explicit --max-fps before -- is kept
        Engine.PhysicsTicksPerSecond = LeanBot.PhysicsTicks;
        GD.Print($"lean bot: at most {Engine.MaxFps} frames a second (0 = no cap), physics {Engine.PhysicsTicksPerSecond} steps a second, no effects, presentation-only loads skipped (--{LeanBot.FullLoadFlag} loads everything)");
    }
}
