using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using SmFormats.Baf;
using SmFormats.Locale;
using SmTool.Dump;

namespace SmFormats.Tests;

/// <summary>The data-research dump helpers (src/SmTool/Dump), on synthetic data only.</summary>
public class DumpTests
{
    static BafDocument Doc() => BafDocument.Read(SyntheticBafWriter.Build(
        ("display_name", 7),
        ("level", 3),
        ("ui_data", new (string, object)[] { ("name", 8), ("icon", "bolter"), ("visible", true) }),
        ("common_data", new (string, object)[]
        {
            ("slot", "perk"),
            ("modifiers", new (string, object)[] { ("modifier_type", "health_regeneration"), ("amount", 45f) }),
        })));

    static byte[] LocaleBytes(string text)
    {
        var raw = Encoding.Unicode.GetBytes("﻿" + text);
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
        var file = new byte[4 + packed.Length];
        BitConverter.GetBytes(raw.Length).CopyTo(file, 0);
        packed.ToArray().CopyTo(file, 4);
        return file;
    }

    [Fact]
    public void Format_prints_a_tree_with_typed_values()
    {
        string text = BafDump.Format(Doc());
        Assert.Contains("display_name = 7", text);
        Assert.Contains("ui_data {", text);
        Assert.Contains("  icon = \"bolter\"", text);
        Assert.Contains("  visible = True", text);
        Assert.Contains("    amount = 45", text);
    }

    [Fact]
    public void Format_annotates_text_ids_and_honours_depth_and_under()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, LocaleBytes("7\tFirst\r\n8\tSecond\r\n9\tThird\r\n"));
            var locale = LocaleTable.Open(path);
            string text = BafDump.Format(Doc(), locale);
            Assert.Contains("display_name = 7   // First", text);
            Assert.Contains("name = 8   // Second", text);
            Assert.DoesNotContain("level = 3   //", text);      // "level" does not look like a text id

            Assert.Contains("modifiers { 2 entries }", BafDump.Format(Doc(), null, maxDepth: 2));
            string under = BafDump.Format(Doc(), null, under: "common_data/modifiers");
            Assert.StartsWith("modifier_type", under);
            Assert.Contains("no table", BafDump.Format(Doc(), null, under: "common_data/missing"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Grep_finds_names_and_string_values_with_their_table_path()
    {
        var hits = BafDump.Grep(Doc(), new Regex("health_regen|^slot$", RegexOptions.IgnoreCase)).ToList();
        Assert.Contains(hits, h => h == "common_data :: slot = \"perk\"");
        Assert.Contains(hits, h => h == "common_data/modifiers :: modifier_type = \"health_regeneration\"");
        Assert.Empty(BafDump.Grep(Doc(), new Regex("nothing_like_this")));
    }

    [Fact]
    public void Locale_reads_zlib_utf16_lines_and_ranges()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, LocaleBytes("10\tTen\r\n11\tEleven\tstill eleven\r\nnot a line\r\n12\tTwelve\r\n10\tduplicate ignored\r\n"));
            var locale = LocaleTable.Open(path);
            Assert.Equal(3, locale.Count);
            Assert.Equal("Ten", locale.Get(10));
            Assert.Equal("Eleven\tstill eleven", locale.Get(11));
            Assert.Null(locale.Get(99));
            Assert.Equal([11, 12], locale.Between(11, 50).Select(kv => kv.Key));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Locale_rejects_a_truncated_file()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [1, 2, 3]);
            Assert.Throws<InvalidDataException>(() => LocaleTable.Open(path));
        }
        finally { File.Delete(path); }
    }
}
