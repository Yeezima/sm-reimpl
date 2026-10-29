using System.IO.Compression;
using System.Text;

namespace SmFormats.Locale;

/// <summary>
/// The game's localised text (<c>data/locale/&lt;language&gt;/ucs.pc</c>): a 4-byte length, a zlib stream, then UTF-16 lines of
/// <c>id&lt;TAB&gt;text</c> (docs/discovery.md, "Seize Ground and Manufactorum"). Read in memory only; the text is never copied into the repo.
/// </summary>
public sealed class LocaleTable
{
    readonly Dictionary<int, string> _text;
    LocaleTable(Dictionary<int, string> text) => _text = text;

    public int Count => _text.Count;
    public string? Get(int id) => _text.TryGetValue(id, out var t) ? t : null;
    public IEnumerable<KeyValuePair<int, string>> Entries => _text.OrderBy(kv => kv.Key);

    /// <summary>The entries with an id in lo..hi (inclusive), in id order.</summary>
    public IEnumerable<KeyValuePair<int, string>> Between(int lo, int hi) => Entries.Where(kv => kv.Key >= lo && kv.Key <= hi);

    public static LocaleTable Open(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        if (file.Length < 6) throw new InvalidDataException("ucs.pc is too short.");
        using var zlib = new ZLibStream(new MemoryStream(file, 4, file.Length - 4), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        return Parse(raw.ToArray());
    }

    /// <summary>Parses the decompressed text (UTF-16 with or without a BOM; UTF-8 accepted when no NUL bytes are present).</summary>
    public static LocaleTable Parse(byte[] decompressed)
    {
        bool utf16 = decompressed.Length >= 2 && (decompressed[1] == 0 || decompressed[0] == 0xFF || decompressed[0] == 0xFE);
        string text = utf16 ? Encoding.Unicode.GetString(decompressed) : Encoding.UTF8.GetString(decompressed);
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var map = new Dictionary<int, string>();
        foreach (var line in text.Split('\n'))
        {
            string l = line.TrimEnd('\r');
            int tab = l.IndexOf('\t');
            if (tab <= 0 || !int.TryParse(l.AsSpan(0, tab), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int id)) continue;
            map.TryAdd(id, l[(tab + 1)..]);
        }
        return new LocaleTable(map);
    }
}
