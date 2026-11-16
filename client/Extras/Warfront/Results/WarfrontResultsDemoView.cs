using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using SmSim.Extras.Warfront.Results;

namespace SmClient;

/// <summary>
/// Test aid, its own scene (no product hook): <c>godot.cmd --windowed --resolution 1280x720 res://Extras/Warfront/Results/WarfrontResultsDemo.tscn -- --out dir [--tag _1280] [--defeat]</c> (needs no install): the end-of-run panel with made-up figures, four
/// players (one left before the end) and their squads. Saves <c>results.png</c>, presses COPY FOR DISCORD and EXPORT (into <c>dir\warfront</c>, never the player's real folder), writes three
/// made-up archived runs there and saves <c>honor.png</c> of the honor roll, prints the checks as <c>WF-RESULTS</c> lines and quits.
/// </summary>
public sealed partial class WarfrontResultsDemoView : Node
{
    string _out = "";
    string _tag = "";
    bool _defeat;
    WarfrontResultsPanel? _panel;
    int _frames;

    /// <summary>The scene's script needs no arguments: they come from the command line after <c>--</c>.</summary>
    public WarfrontResultsDemoView() { }

    public static WarfrontRunRecord SampleRecord(bool defeat)
    {
        static WarfrontContributor C(string name, int join, bool conn, int k, int el, int bo, int a, int d, int rv, int cp, params WarfrontRetainerStats[] sq) =>
            new(new WarfrontPlayerStats(name, conn, join, k, el, bo, a, d, rv, cp), sq);
        var rows = WarfrontLeaderboard.Rank(new[]
        {
            C("Alpha", 0, true, 310, 12, 1, 40, 2, 5, 3, new WarfrontRetainerStats("Cato", "Tactical", 1, 212, 9, 1, 14, 2, 3, 0, 7), new WarfrontRetainerStats("Brutus", "Assault", 2, 90, 2, 0, 6, 4, 1, 0, 5), new WarfrontRetainerStats("Dio", "Devastator", 3, 41, 0, 0, 2, 1, 0, 0, 4)),
            C("Bravo", 1, true, 288, 7, 0, 22, 0, 2, 4, new WarfrontRetainerStats("Marcus", "Tactical", 1, 140, 3, 0, 9, 1, 8, 0, 7)),
            C("Charlie", 2, false, 120, 1, 0, 15, 6, 1, 0, new WarfrontRetainerStats("Sevro", "Assault", 1, 55, 0, 0, 3, 5, 0, 0, 3)),
            C("Delta", 3, true, 95, 0, 0, 30, 3, 9, 1),
        });
        return new WarfrontRunRecord(defeat ? "lost" : "won", "2026-11-17T14:03:05Z", 4325, WarfrontScoreWeights.Default,
            new[] { "#1 Node lane 1: Win", "#2 Node lane 2: Win", "#3 Hq: Win", "#4 Fortress lane 1: " + (defeat ? "Wipe" : "Win") }, 6, 2, 1, rows);
    }

    public override void _Ready()
    {
        var user = OS.GetCmdlineUserArgs();
        string Arg(string name) { int i = Array.IndexOf(user, "--" + name); return i >= 0 && i + 1 < user.Length ? user[i + 1] : ""; }
        _out = Arg("out") is { Length: > 0 } o ? o : Path.Combine(Path.GetTempPath(), "wf-results-demo");
        _tag = Arg("tag");
        _defeat = Array.IndexOf(user, "--defeat") >= 0;
        Directory.CreateDirectory(_out);
        var dir = Path.Combine(_out, "warfront");
        var rec = SampleRecord(_defeat);
        _panel = WarfrontResultsPanel.Open(this, GetWindow(), rec, dir);
    }

    public override void _Process(double delta)
    {
        _frames++;
        if (_panel is null) return;
        if (_frames == 20) Shot((_defeat ? "defeat" : "victory") + _tag + ".png");
        if (_frames == 22)
        {
            _panel.Copy();
            _panel.Export();
            GD.Print("WF-RESULTS copied " + (_panel.LastCopied?.Length ?? -1) + " chars, ends fence " + (_panel.LastCopied?.EndsWith("```") ?? false));
            GD.Print("WF-RESULTS status " + _panel.StatusText);
            var dir = Path.Combine(_out, "warfront");
            for (int i = 0; i < 3; i++)
            {
                var r = SampleRecord(i == 1) with { EndedUtc = $"2026-11-1{5 + i}T10:00:00Z", DurationSeconds = 3600 + i * 700 };
                Directory.CreateDirectory(Path.Combine(dir, "records"));
                WarfrontResultsArchive.WriteRecord(Path.Combine(dir, "records", $"2026111{5 + i}T100000Z-{r.Result}.json"), r);
            }
        }
        if (_frames == 24) _panel.ToggleHonorRoll();
        if (_frames == 44)
        {
            Shot("honor" + _tag + ".png");
            var files = Directory.GetFiles(Path.Combine(_out, "warfront", "exports")).Select(Path.GetFileName).ToArray();
            GD.Print("WF-RESULTS exports " + string.Join(",", files) + " rows " + _panel.TableText.Count + " honorRoll " + _panel.ShowingHonorRoll);
            GetTree().Quit();
        }
    }

    void Shot(string name)
    {
        var img = GetViewport().GetTexture().GetImage();
        if (img is null) { GD.Print("WF-RESULTS no image (headless run)"); return; }
        var path = Path.Combine(_out, name);
        img.SavePng(path);
        GD.Print("WF-RESULTS shot " + name);
    }
}
