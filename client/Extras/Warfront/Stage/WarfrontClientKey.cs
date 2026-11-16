using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace SmClient.Extras.Warfront.Stage;

/// <summary>
/// The campaign key of this player (slice W5b; plan section 3): a random 64-bit number made on first use and kept in <c>%LOCALAPPDATA%\sm-reimpl\warfront\client.json</c> (private, outside the repository),
/// or in the file <c>--warfront-key-file</c> names (test clients, so several run on one PC). The server keys the run's players by it; no other client ever sees it (the Hub sends pseudonyms),
/// and it is never logged in full (<see cref="Tail"/>).
/// </summary>
public static class WarfrontClientKey
{
    /// <summary>The private folder: <c>SM_WF_DIR</c> (a test aid the server reads too, so a scripted run never touches the owner's files) or <c>%LOCALAPPDATA%\sm-reimpl\warfront</c>.</summary>
    public static string Folder()
    {
        string? env = Environment.GetEnvironmentVariable("SM_WF_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sm-reimpl", "warfront");
    }

    /// <summary>Set when the last <see cref="LoadOrCreate"/> could not keep its key on disk (empty otherwise): the key then lasts this session only.</summary>
    public static string LastProblem { get; private set; } = "";

    public static string DefaultPath() => Path.Combine(Folder(), "client.json");

    /// <summary>Reads the key from <paramref name="path"/>, or makes one and writes it. A file that cannot be read or written leaves a key for this run only (never zero).</summary>
    public static ulong LoadOrCreate(string path)
    {
        LastProblem = "";
        try
        {
            if (File.Exists(path) && TryParse(File.ReadAllText(path), out ulong existing)) return existing;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.WriteLine("warfront: the key file could not be read"); }
        ulong key = Random();
        LastProblem = "";
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, Format(key));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LastProblem = "THE KEY FILE CANNOT BE WRITTEN: THIS SESSION IS A NEW PLAYER"; }
        return key;
    }

    public static ulong Random()
    {
        Span<byte> b = stackalloc byte[8];
        ulong key;
        do { RandomNumberGenerator.Fill(b); key = BitConverter.ToUInt64(b); } while (key == 0);
        return key;
    }

    public static string Format(ulong key) => JsonSerializer.Serialize(new { key = key.ToString("x16") });

    public static bool TryParse(string json, out ulong key)
    {
        key = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("key", out var k) || k.ValueKind != JsonValueKind.String) return false;
            return ulong.TryParse(k.GetString(), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out key) && key != 0;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>The last four hex digits: enough to tell two keys apart in a log.</summary>
    public static string Tail(ulong key) => "..." + (key & 0xFFFF).ToString("x4");
}
