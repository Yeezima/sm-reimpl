using System.Text;

namespace SmContent;

/// <summary>
/// A node of a Valve KeyValues ("VDF") text file: either a string value or an ordered list of named children (keys may repeat;
/// lookups are case-insensitive, as Steam's are). Only what Steam's <c>libraryfolders.vdf</c> and <c>appmanifest_*.acf</c> need.
/// </summary>
public sealed class VdfNode
{
    readonly List<KeyValuePair<string, VdfNode>> _children = new();

    public string? Value { get; }
    public bool IsBlock => Value is null;
    public IReadOnlyList<KeyValuePair<string, VdfNode>> Children => _children;

    VdfNode(string? value) { Value = value; }

    public static VdfNode Leaf(string value) => new(value);
    public static VdfNode Block() => new(null);

    internal void Add(string key, VdfNode child) => _children.Add(new(key, child));

    /// <summary>The first child with this key (null when absent or this node is a value).</summary>
    public VdfNode? Get(string key)
    {
        foreach (var c in _children)
            if (string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)) return c.Value;
        return null;
    }

    /// <summary>The string value of the first child with this key (null when absent or that child is a block).</summary>
    public string? GetString(string key) => Get(key)?.Value;
}

/// <summary>
/// A tiny, forgiving parser for the KeyValues text format (quoted strings with C-style escapes, <c>{ }</c> blocks, <c>//</c> comments, unquoted
/// tokens). It never throws on malformed text: <see cref="TryParse"/> returns false and the caller treats the file as unreadable. Written from the
/// public description of the format; no code from other tools.
/// </summary>
public static class VdfParser
{
    const int MaxDepth = 32;

    /// <summary>Parses a whole file; the result is a block holding the top-level pairs (e.g. <c>libraryfolders</c>).</summary>
    public static bool TryParse(string text, out VdfNode root)
    {
        root = VdfNode.Block();
        int pos = 0;
        var parsed = VdfNode.Block();
        if (!ParsePairs(text, ref pos, parsed, topLevel: true, 0)) return false;
        root = parsed;
        return true;
    }

    static bool ParsePairs(string s, ref int pos, VdfNode into, bool topLevel, int depth)
    {
        if (depth > MaxDepth) return false;
        while (true)
        {
            SkipSpace(s, ref pos);
            if (pos >= s.Length) return topLevel;           // end of text closes only the top level
            if (s[pos] == '}')
            {
                if (topLevel) return false;                 // stray closing brace
                pos++;
                return true;
            }
            if (!ReadToken(s, ref pos, out string key)) return false;
            SkipSpace(s, ref pos);
            if (pos >= s.Length) return false;
            if (s[pos] == '{')
            {
                pos++;
                var child = VdfNode.Block();
                if (!ParsePairs(s, ref pos, child, topLevel: false, depth + 1)) return false;
                into.Add(key, child);
            }
            else
            {
                if (s[pos] == '}') return false;            // a key without a value
                if (!ReadToken(s, ref pos, out string value)) return false;
                into.Add(key, VdfNode.Leaf(value));
            }
        }
    }

    static void SkipSpace(string s, ref int pos)
    {
        while (pos < s.Length)
        {
            char c = s[pos];
            if (char.IsWhiteSpace(c) || c == '﻿') pos++;
            else if (c == '/' && pos + 1 < s.Length && s[pos + 1] == '/')
            {
                while (pos < s.Length && s[pos] != '\n') pos++;
            }
            else break;
        }
    }

    static bool ReadToken(string s, ref int pos, out string token)
    {
        token = "";
        if (pos >= s.Length) return false;
        if (s[pos] == '"')
        {
            pos++;
            var sb = new StringBuilder();
            while (pos < s.Length)
            {
                char c = s[pos++];
                if (c == '"') { token = sb.ToString(); return true; }
                if (c == '\\' && pos < s.Length)
                {
                    char n = s[pos++];
                    sb.Append(n switch { 'n' => '\n', 't' => '\t', '\\' => '\\', '"' => '"', _ => n });
                }
                else sb.Append(c);
            }
            return false;                                   // unterminated string
        }
        int start = pos;
        while (pos < s.Length && !char.IsWhiteSpace(s[pos]) && s[pos] != '{' && s[pos] != '}' && s[pos] != '"') pos++;
        if (pos == start) return false;
        token = s.Substring(start, pos - start);
        return true;
    }
}
