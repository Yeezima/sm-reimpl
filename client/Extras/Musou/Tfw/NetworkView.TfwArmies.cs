using System;
using System.Collections.Generic;
using Godot;
using SmContent;
using SmNet.Extras.Musou;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The battle flow's distant armies on the client (expansion S6; tag 18, <see cref="TfwArmyWire"/>): their aggregates drawn as cheap crowd impostors in the server's own slots (<see cref="TfwArmyImpostors"/>): the
/// Orks through <see cref="MusouCrowd"/>'s vertex-animation tier, the Guardsmen through <see cref="GuardCrowd"/>; an aggregate's losses fall as bodies where its slots went; the waves attacking a point shout their
/// Waaagh! (<see cref="WaaaghBarkPlanner"/>). The server decides everything; nothing here is sent. Test aid <c>--no-army-impostors</c>.
/// </summary>
public partial class NetworkView
{
    readonly TfwArmyImpostors _armyImpostors = new() { OldCorpses = CrowdView.OldCorpses, OrkAggregates = OldOrkArmies };
    /// <summary>Test aid <c>--musou-old-ork-armies</c> (with the server's <c>SM_TFW_OLD_ORK_ARMIES=1</c>): the Ork armies' aggregates drawn as impostors again (before 2026-11-09).</summary>
    static readonly bool OldOrkArmies = Array.IndexOf(OS.GetCmdlineUserArgs(), "--musou-old-ork-armies") >= 0;
    readonly WaaaghBarkPlanner _waaagh = new();
    readonly List<TfwImpostor> _armyImpostorList = new();
    readonly List<MusouShown> _armyOrksShown = new();
    readonly List<TfwGuardShown> _armyGuardsShown = new();
    ulong _armyFrame = ulong.MaxValue;
    bool? _armyImpostorsOff;
    bool ArmyImpostorsOff => _armyImpostorsOff ??= CmdFlag("no-army-impostors");
    bool? _armyFarBodies;
    bool ArmyFarBodies => _armyFarBodies ??= CmdFlag("army-far-bodies");

    /// <summary>A crowd id for an impostor's key, above every pool id (the crowd keys its clocks by it).</summary>
    static ushort ImpostorId(int key, int above) => (ushort)(above + 1 + (int)((uint)key * 2654435761u % (uint)(ushort.MaxValue - above - 1)));

    /// <summary>Once a frame: the newest armies, their eased places, and the impostors to draw.</summary>
    void TfwArmiesFrame(Num.Vector3 camera, float delta)
    {
        ulong frame = Engine.GetProcessFrames();
        if (frame == _armyFrame) return;
        _armyFrame = frame;
        double now = Time.GetTicksUsec() / 1e6;
        _armyImpostors.Apply(_tfwState.Armies, _tfwState.ArmiesTick, now);
        _armyImpostors.Frame(delta, now);
        if (ArmyImpostorsOff) { _armyImpostorList.Clear(); return; }
        _armyImpostors.Write(camera, _armyImpostorList);
    }

    /// <summary>The Orks the crowd draws this frame: the shown ones and the armies' Ork impostors; an aggregate's losses become bodies.</summary>
    IReadOnlyList<MusouShown> TfwArmyOrks(IReadOnlyList<MusouShown> shown, Vector3 camera, float delta)
    {
        TfwArmiesFrame(new Num.Vector3(camera.X, camera.Y, camera.Z), delta);   // the camera in game space
        // The owner, 2026-11-08: "All far battles corpses should instantly vanish. If a player cannot see it, it doesn't ever get drawn. Unless the player is close by." An aggregate's loss leaves a body only
        // within the brief's near distance (TfwArmyRules.CoreMetres, 90 m; the real units reach 300 m since 2026-11-09) of this view; farther its impostor just goes (--army-far-bodies, test aid: bodies at any distance, as expansion S6 had).
        // The corpse floor (the owner, 2026-11-09: "they should always leave a corpse for at least 10 seconds before disappearing"): the new floor wins, so a loss leaves a body at any distance, the
        // Guardsmen's too (before: the Orks' alone); --musou-old-corpses restores the 90 m.
        float bodiesWithin = ArmyFarBodies ? float.PositiveInfinity : SmNet.Extras.Musou.MusouCorpseRules.ImpostorBodiesWithin(TfwArmyRules.CoreMetres, CrowdView.OldCorpses);
        foreach (var d in _armyImpostors.Deaths)
        {
            if (ArmyImpostorsOff || new Vector3(d.Position.X, d.Position.Y, d.Position.Z).DistanceTo(camera) > bodiesWithin) continue;
            if (d.Side == TfwArmySide.Ork)
                _musouCrowd?.AddBody(new MusouDeathShown(ImpostorId(d.Key, MusouSim.Capacity), d.Type, d.Position, d.Yaw, Num.Vector3.Zero, MusouDeathKind.Shot, 0, _tfwState.ArmiesTick));
            else if (!CrowdView.OldCorpses)
                _guardCrowd?.AddImpostorBody(new Vector3(d.Position.X, d.Position.Y, d.Position.Z), d.Yaw, d.Key);
        }
        _armyImpostors.Deaths.Clear();
        if (_armyImpostorList.Count == 0) return shown;
        _armyOrksShown.Clear();
        _armyOrksShown.AddRange(shown);
        foreach (var i in _armyImpostorList)
            if (i.Side == TfwArmySide.Ork) _armyOrksShown.Add(new MusouShown(ImpostorId(i.Key, MusouSim.Capacity), i.Position, i.Yaw, i.Type, i.Moving ? MusouEnemyState.Seek : MusouEnemyState.Spawning, 0f));
        return _armyOrksShown;
    }

    /// <summary>The Guardsmen the crowd draws this frame: the shown ones and the armies' Guard impostors.</summary>
    IReadOnlyList<TfwGuardShown> TfwArmyGuards(IReadOnlyList<TfwGuardShown> shown, Vector3 camera, float delta)
    {
        TfwArmiesFrame(new Num.Vector3(camera.X, camera.Y, camera.Z), delta);
        if (_armyImpostorList.Count == 0) return shown;
        _armyGuardsShown.Clear();
        _armyGuardsShown.AddRange(shown);
        foreach (var i in _armyImpostorList)
            if (i.Side == TfwArmySide.Guard) _armyGuardsShown.Add(new TfwGuardShown(ImpostorId(i.Key, TfwWire.GuardCapacity), i.Position, i.Yaw, (byte)(i.Moving ? GuardState.Moving : GuardState.Idle)));
        return _armyGuardsShown;
    }

    /// <summary>The waves' Waaagh!: the planner's bursts, each a roar or a taunt of a Boy, Nob or 'Ard Boy at the wave's front, through the sound budget's Waaagh group (no weapon-fire curve: it carries).</summary>
    void TfwWaaaghFrame(Num.Vector3 camera, double now)
    {
        if (_tfwState.Armies.Count == 0) return;
        foreach (var bark in _waaagh.Step(now, camera, _tfwState.Armies, id => _armyImpostors.Of(id)?.Anchor))
        {
            var (type, taunt) = WaaaghBarkRules.Voices[bark.Voice];
            if (MusouSetOf(type) is not { } set) continue;
            var cue = taunt ? set.Celebrate : set.Roar;
            if (cue.Empty) cue = set.Roar.Empty ? set.Celebrate : set.Roar;
            if (cue.Empty) continue;
            var emitter = new SoundEmitter { Position = ToGodot(bark.At) + Vector3.Up * 1.4f, OneShot = true, Label = "musou waaagh", Group = MusouSoundEmitters };
            MusouCue(cue, set, emitter, MusouSoundGroup.Waaagh, bark.Distance, "waaagh");
        }
    }
}
