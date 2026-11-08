using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmSim;
using SmNet.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// musou/battle-ambience (owner, 2026-11-08: "add random barks for space marines, the player, allied space marines, retainers in musou mode. use intense voice lines from the campaign"): every
/// living Space Marine of a Musou match (the own player as Titus; the allied pair and the other players as Sidonus or Leandros, <see cref="MusouMarineBarkRules"/>) says a random campaign combat
/// line now and then, more often with an Ork near (<see cref="MusouBarkClock"/>), from his own voice emitter (one line at a time with his other speech). Musou Mode only; Classic PvP is untouched.
/// Test aids: <c>--no-musou-marine-barks</c>; <c>--musou-bark-skip id,id</c> (hex line ids, as the log prints them) leaves lines out; <c>--log-sound musou-barks</c>.
/// </summary>
public partial class NetworkView
{
    static readonly bool NoMusouMarineBarks = CmdFlag("no-musou-marine-barks");
    static readonly HashSet<uint> MusouBarkSkip = (CmdArg("musou-bark-skip") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => uint.TryParse(x.StartsWith("0x") ? x[2..] : x, System.Globalization.NumberStyles.HexNumber, null, out uint v) ? v : 0u).Where(v => v != 0).ToHashSet();

    readonly MusouBarkClock _musouBarkClock = new();
    readonly Dictionary<string, List<uint>> _musouBarkLines = new();
    int _musouBarksPosted;
    double _musouBarkLookAt, _musouBarkLogAt;

    /// <summary>A character's lines (each leaf of its line sets), read once.</summary>
    List<uint> MusouBarkLinesOf(string character)
    {
        if (_musouBarkLines.TryGetValue(character, out var lines)) return lines;
        lines = new List<uint>();
        if (_audio is not null)
            foreach (var (evt, prefix) in MusouMarineBarkRules.LineSets(character))
                foreach (uint node in _audio.DialogueLeaves(evt, prefix))
                    if (!MusouBarkSkip.Contains(node) && !lines.Contains(node) && _audio.PostNode(node, new SoundSwitches()).Voices.Count > 0) lines.Add(node);   // a line with no media is left out
        _musouBarkLines[character] = lines;
        GD.Print($"musou barks: {character} has {lines.Count} campaign lines");
        return lines;
    }

    /// <summary>The frame (from <c>MusouSoundsFrame</c>): looks at each Marine twice a second.</summary>
    void MusouMarineBarksFrame(PlayerState me, IReadOnlyList<MusouShown> shown, Num.Vector3 camera, double now)
    {
        if (NoMusouMarineBarks || _sound is null || _audio is null || _client is null || !_audio.HasSpeech || now < _musouBarkLookAt) return;
        _musouBarkLookAt = now + 0.5;
        if (me.Alive && !OwnDead && MusouBarkSpeaks(me)) MusouMarineBark(me, own: true, shown, now);
        int marines = 0, heard = 0;
        foreach (var o in _lastOthers)
        {
            if (!o.Alive || !MusouBarkSpeaks(o)) { _musouBarkClock.Forget(o.PlayerId); continue; }
            marines++;
            if (Num.Vector3.Distance(o.Position, camera) > MusouMarineBarkRules.HearRange) continue;
            heard++;
            MusouMarineBark(o, own: false, shown, now);
        }
        if (LogSound("musou-barks") && now >= _musouBarkLogAt) { _musouBarkLogAt = now + 20.0; GD.Print($"musou barks: t {now:F0} s: other Marines {marines}, within {MusouMarineBarkRules.HearRange:0} m {heard}, posted {_musouBarksPosted}"); }
    }

    /// <summary>A Space Marine (not the playable Ork, not a Chaos player).</summary>
    bool MusouBarkSpeaks(PlayerState s) => _config is not null && Races.Of(_config, s.Class, s.Team) == Race.SpaceMarines;

    void MusouMarineBark(PlayerState s, bool own, IReadOnlyList<MusouShown> shown, double now)
    {
        bool fighting = false;
        float range = MusouMarineBarkRules.CombatRange * MusouMarineBarkRules.CombatRange;
        for (int i = 0; i < shown.Count && !fighting; i++) fighting = Num.Vector3.DistanceSquared(shown[i].Position, s.Position) <= range;
        if (!_musouBarkClock.Due(s.PlayerId, now, fighting)) return;
        string character = MusouMarineBarkRules.CharacterOf(own, s.PlayerId);
        var lines = MusouBarkLinesOf(character);
        if (lines.Count == 0) return;
        uint line = lines[_musouBarkClock.Roll.Next(lines.Count)];
        var result = _sound!.PostLine(line, $"musou bark/{character}", Track(s.PlayerId, own).Emitter);
        if (result == SpeechResult.Started) _musouBarksPosted++;
        if (LogSound("musou-barks")) GD.Print($"MUSOU BARK t {now:F2} P{s.PlayerId}{(own ? " own" : "")} as {character} line {line:x8} {(fighting ? "fighting" : "calm")}: {result} (posted {_musouBarksPosted})");
    }
}
