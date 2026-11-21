using System;
using System.IO;
using System.Text.Json;
using SmContent;

namespace SmClient.Extras.Weather;

/// <summary>
/// The player's weather options (weather W2; client only, never on the wire, never in the config hash): <see cref="FullFlash"/>, Thunderstorm lightning REDUCED (the default: a faint sky-only glow, no change of the screen's
/// exposure) or FULL (a brighter, short lift of the exposure, sun and shade, still one pulse per strike at the plan's spacing; <c>WeatherPlan.FullFlash</c>). Saved in <c>weather-settings.json</c> beside the client's other settings (the data root);
/// a missing, unreadable or hostile file reads as the default and is never written until the player changes it. Shown in OPTIONS only in a build that has added content (<c>ContentExtras.Compiled</c>).
/// </summary>
public static class WeatherSettings
{
    public const string FileName = "weather-settings.json";
    static bool? _full;

    /// <summary>Test seam: the file to read and write (null: the data root's).</summary>
    public static string? PathOverride { get; set; }

    static string FilePath => PathOverride ?? System.IO.Path.Combine(GameInstall.DataRoot, FileName);

    sealed record Stored(bool FullFlash);

    /// <summary>The setting now (read once, then kept in memory).</summary>
    public static bool FullFlash
    {
        get
        {
            if (_full is { } v) return v;
            try
            {
                string path = FilePath;
                if (File.Exists(path) && new FileInfo(path).Length < 4096 && JsonSerializer.Deserialize<Stored>(File.ReadAllText(path)) is { } s) return (_full = s.FullFlash).Value;
            }
            catch (Exception) { /* a damaged file is the default */ }
            return (_full = false).Value;
        }
        set
        {
            _full = value;
            try
            {
                string path = FilePath;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new Stored(value)));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception) { /* not saved: the choice still holds for this run */ }
        }
    }

    /// <summary>Forget the in-memory value (tests).</summary>
    public static void Reset() => _full = null;
}
