using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmMapmaker;

/// <summary>A failure with a stable error code (docs/mapmaker.md): <c>unknown-id</c>, <c>unknown-asset</c>, <c>bad-file</c>, ...</summary>
public sealed class MapException(string code, string message, string? id = null) : Exception(message)
{
    public string Code { get; } = code;
    public string? Id { get; } = id;
}

/// <summary>
/// Canonical JSON (docs/mapmaker.md "Determinism"): UTF-8 without BOM, LF, two-space indent, properties in the order the node holds them, arrays of
/// numbers on one line, floats as the shortest round-trip text in the invariant culture, negative zero as 0. A trailing LF ends the text.
/// </summary>
public static class CanonicalJson
{
    static readonly JsonSerializerOptions StringOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Write(JsonNode? node)
    {
        var sb = new StringBuilder();
        Emit(sb, node, 0);
        sb.Append('\n');
        return sb.ToString();
    }

    public static byte[] Bytes(JsonNode? node) => new UTF8Encoding(false).GetBytes(Write(node));

    public static string Float(float f)
    {
        if (!float.IsFinite(f)) throw new MapException("bad-transform", "A number is not finite.");
        return f == 0 ? "0" : f.ToString("R", CultureInfo.InvariantCulture);
    }

    static bool IsNumber(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number;

    static void Emit(StringBuilder sb, JsonNode? node, int depth)
    {
        switch (node)
        {
            case null: sb.Append("null"); break;
            case JsonObject o:
                if (o.Count == 0) { sb.Append("{}"); break; }
                sb.Append("{\n");
                int i = 0;
                foreach (var (k, v) in o)
                {
                    sb.Append(' ', 2 * (depth + 1)).Append(JsonSerializer.Serialize(k, StringOptions)).Append(": ");
                    Emit(sb, v, depth + 1);
                    sb.Append(++i < o.Count ? ",\n" : "\n");
                }
                sb.Append(' ', 2 * depth).Append('}');
                break;
            case JsonArray a:
                if (a.Count == 0) { sb.Append("[]"); break; }
                if (a.All(IsNumber))
                {
                    sb.Append('[');
                    for (int j = 0; j < a.Count; j++) { if (j > 0) sb.Append(", "); Emit(sb, a[j], depth); }
                    sb.Append(']');
                    break;
                }
                sb.Append("[\n");
                for (int j = 0; j < a.Count; j++)
                {
                    sb.Append(' ', 2 * (depth + 1));
                    Emit(sb, a[j], depth + 1);
                    sb.Append(j + 1 < a.Count ? ",\n" : "\n");
                }
                sb.Append(' ', 2 * depth).Append(']');
                break;
            case JsonValue v:
                if (v.TryGetValue<string>(out var s)) sb.Append(JsonSerializer.Serialize(s, StringOptions));
                else if (v.TryGetValue<bool>(out var b)) sb.Append(b ? "true" : "false");
                else if (v.TryGetValue<float>(out var f)) sb.Append(Float(f));
                else if (v.TryGetValue<int>(out var n)) sb.Append(n.ToString(CultureInfo.InvariantCulture));
                else if (v.TryGetValue<long>(out var l)) sb.Append(l.ToString(CultureInfo.InvariantCulture));
                else if (v.TryGetValue<double>(out var d)) sb.Append(Float((float)d));
                else sb.Append(v.ToJsonString());
                break;
        }
    }
}

/// <summary>Small readers and builders over <see cref="JsonNode"/> shared by the models and the operations.</summary>
public static class Json
{
    public static JsonNode ParseText(string text, string what)
    {
        try { return JsonNode.Parse(text) ?? throw new MapException("bad-file", $"{what} is empty."); }
        catch (JsonException e) { throw new MapException("bad-file", $"{what} is not valid JSON: {e.Message}"); }
    }

    public static JsonObject Obj(JsonNode? n, string what, string code = "bad-file") =>
        n as JsonObject ?? throw new MapException(code, $"{what} must be a JSON object.");

    public static JsonArray Arr(JsonNode? n, string what, string code = "bad-file") =>
        n as JsonArray ?? throw new MapException(code, $"{what} must be a JSON array.");

    public static string? Str(JsonNode? n, string what, string code = "bad-file", bool nullable = false)
    {
        if (n is null) { if (nullable) return null; throw new MapException(code, $"{what} is missing."); }
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        throw new MapException(code, $"{what} must be a string.");
    }

    public static string ReqStr(JsonObject o, string key, string what, string code = "bad-file") =>
        Str(o[key], $"{what}.{key}", code) ?? throw new MapException(code, $"{what}.{key} is missing.");

    public static bool Bool(JsonNode? n, string what, string code, bool dflt)
    {
        if (n is null) return dflt;
        if (n is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        throw new MapException(code, $"{what} must be true or false.");
    }

    public static float Num(JsonNode? n, string what, string code = "bad-transform")
    {
        try
        {
            if (n is JsonValue v && v.GetValueKind() == JsonValueKind.Number)
            {
                float f = (float)v.GetValue<double>();
                if (float.IsFinite(f)) return f;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { }
        throw new MapException(code, $"{what} must be a finite number.");
    }

    public static int Int(JsonNode? n, string what, string code)
    {
        try { if (n is JsonValue v && v.GetValueKind() == JsonValueKind.Number) return v.GetValue<int>(); }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { }
        throw new MapException(code, $"{what} must be an integer.");
    }

    /// <summary>[x, y, z] as numbers.</summary>
    public static Vector3 Vec(JsonNode? n, string what, string code = "bad-transform")
    {
        if (n is JsonArray { Count: 3 } a) return new Vector3(Num(a[0], what, code), Num(a[1], what, code), Num(a[2], what, code));
        throw new MapException(code, $"{what} must be an array of three numbers.");
    }

    public static JsonArray Vec(Vector3 v) => [JsonValue.Create(v.X), JsonValue.Create(v.Y), JsonValue.Create(v.Z)];

    public static JsonNode? Opt(string? s) => s is null ? null : JsonValue.Create(s);

    public static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        if (s.Length > 63) s = s[..63].Trim('-');
        return s;
    }

    public static bool IsSlug(string? id) =>
        id is { Length: > 0 and <= 63 } && char.IsAsciiLetterOrDigit(id[0]) && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_');
}
