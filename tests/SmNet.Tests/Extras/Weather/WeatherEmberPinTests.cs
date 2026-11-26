using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SmNet.Tests.Extras.Weather;

/// <summary>
/// Weather fidelity (owner 2026-11-25: "except the ember weather condition. Keep that one as is."): the Ember Wind's code is pinned to its rain-motion text (claude/rain-motion-1124 @ 03f86591): the field shader template and the
/// ember shader parts, the ember layer's build, update, box and counts, and its grade. A change to any of them fails here; the client's own selftest checks the shader the engine compiles.
/// </summary>
public class WeatherEmberPinTests
{
    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("SmReimpl.slnx");
    }

    static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts])).Replace("\r\n", "\n");

    /// <summary>The text from <paramref name="start"/> up to and including the first <paramref name="end"/> after it.</summary>
    static string Span(string text, string start, string end)
    {
        int a = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, "pinned text not found: " + start);
        int b = text.IndexOf(end, a + start.Length, StringComparison.Ordinal);
        Assert.True(b >= 0, "pinned end not found after: " + start);
        return text.Substring(a, b + end.Length - a);
    }

    static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>The ember parts of the field: the shader template the ember shader is made from, its render mode, vertex and fragment, and its shader's construction.</summary>
    public static string FieldEmberText()
    {
        var f = Read("client", "Extras", "Weather", "WeatherField.cs");
        return Span(f, "    static string Code(string render, string vertexTail, string fragment) => $$\"\"\"", "\n        \"\"\";\n")
            + Span(f, "    const string EmberRender =", ";\n")
            + Span(f, "    const string DotVertex = \"\"\"", "\"\"\";\n")
            + Span(f, "    const string EmberFragment = \"\"\"", "\"\"\";\n")
            + Span(f, "        _ => _ember ??= new Shader", ",\n");
    }

    /// <summary>The ember layer in the rig: its build, its per-frame update, its box and middle, its counts and floor, and the Ember Wind grade.</summary>
    public static string RigEmberText()
    {
        var fx = Read("client", "Extras", "Weather", "WeatherRig.Fx.cs");
        var rig = Read("client", "Extras", "Weather", "WeatherRig.cs");
        var grade = Read("client", "Extras", "Weather", "WeatherGrade.cs");
        return Span(fx, "    WeatherField BuildEmbers()", "\n    }\n")
            + Span(fx, "        // embers (Ember Wind, preview)", "\n        }\n")
            + Span(fx, "EmberBox = new(", ";")
            + Span(fx, "EmberMidM = ", ";")
            + Span(rig, "EmberCount = ", ";")
            + Span(rig, "EmberFloor = ", ";")
            + Span(grade, "            WeatherId.EmberWind => new(", "),\n");
    }

    public const string FieldEmberSha = "5A865C78FFF23DC8D885DAB9F8CADCEDD59C63D0A900A6E7CA47E9C09C101E9A";
    public const string RigEmberSha = "FE1C15A5B9DBB01A01047DDB106DE5BA3DDDF2734F126ABBD24ADFCE794BFA31";

    [Fact]
    public void EmberFieldShaderIsUnchanged() => Assert.Equal(FieldEmberSha, Hash(FieldEmberText()));

    [Fact]
    public void EmberLayerIsUnchanged() => Assert.Equal(RigEmberSha, Hash(RigEmberText()));
}
