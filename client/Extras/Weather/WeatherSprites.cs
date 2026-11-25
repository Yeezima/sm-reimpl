using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmFormats.Fx;

namespace SmClient.Extras.Weather;

/// <summary>
/// Weather fidelity (owner 2026-11-25, decisions/2026-11-25-weather-fidelity.md; client only, presentation only, **ours**): the weather's textures are the install's own particle sprites, cut from the effects atlas
/// (<c>master_fx</c>) at run time and kept in memory only (nothing original is written anywhere). Each sprite is named by the original effect whose material it is (spec/behavior.md "Weather fidelity"):
/// <list type="bullet">
/// <item><b>Ash</b> (grit streaks and flakes): <c>paper_texture_page_00</c>, the sheet of the original's falling ash (<c>amb_falling_ash</c>: "dark ash", "light ash") and drifting smoke's ash and paper flecks.</item>
/// <item><b>Dust sheets, fog cards</b>: <c>alpha_volume_soft</c>, the volume puff of <c>amb_dust_cloud_windy</c>, <c>amb_fog_patch</c> and <c>amb_steamy_fog</c>.</item>
/// <item><b>Rain mist</b>: <c>smoke_wispy</c>, the wisp of <c>amb_mist_calm</c>.</item>
/// <item><b>Splashes</b>: <c>alpha_spike_hard</c>, the spray spike of <c>liquid_sludge_splashes_small</c> and the drips' splash (a sheet of four spikes stored pointing sideways; turned to point up here).</item>
/// </list>
/// The rain streaks keep the procedural soft streak: the original has no rain, and its only falling-liquid sprite (<c>drops_beam</c> of <c>liquid_sludge_dripping</c>, 125 x 61 texels) is a sparse scatter of specks that would dot the streaks.
/// The read happens once per run on a worker thread (the package and the atlas decode never touch the main thread); until it is done, or when the install has no such sprite, each layer keeps its procedural look
/// (the rain-motion look). <c>--weather-procedural</c> keeps the procedural look throughout (the switch that restores the old behaviour).
/// </summary>
public static class WeatherSprites
{
    /// <summary>One sprite: its upright texture (mipmapped) and its sheet (cells across, cells down).</summary>
    public sealed record Sprite(Texture2D Texture, int TilesX, int TilesY, string Material, string Effect);

    /// <summary>The weather's sprites by their role.</summary>
    public enum Role { Ash, Volume, Wisp, Splash }

    /// <summary>The (role, material, the original effect it is read from) table.</summary>
    public static readonly (Role Role, string Material, string Effect)[] Sources =
    [
        (Role.Ash, "paper_texture_page_00", "amb_falling_ash"),
        (Role.Volume, "alpha_volume_soft", "amb_dust_cloud_windy"),
        (Role.Wisp, "smoke_wispy", "amb_mist_calm"),
        (Role.Splash, "alpha_spike_hard", "liquid_sludge_splashes_small"),
    ];

    /// <summary>The old procedural look throughout (test aid and restore switch).</summary>
    public static bool Procedural { get; set; } = Array.Exists(OS.GetCmdlineUserArgs(), a => a == "--weather-procedural");

    static readonly Dictionary<Role, Sprite> _sprites = new();
    static Task<List<(Role Role, Image Image, int Tx, int Ty, string Material, string Effect)>>? _load;
    static bool _done;
    static FxLibrary? _failedFor, _loadFor;   // tune (review): a read that faulted is not retried for the same library (this match), but a new match's library reads again
    /// <summary>Bumped when the sprites arrive, so a rig built before then swaps its textures in.</summary>
    public static int Version { get; private set; }
    /// <summary>What the read found (the log line and the selftest).</summary>
    public static string Status { get; private set; } = "not read";

    /// <summary>The sprite of <paramref name="role"/>, or null (not read yet, none in the install, or the procedural switch).</summary>
    public static Sprite? Of(Role role) => Procedural ? null : _sprites.GetValueOrDefault(role);

    /// <summary>Starts the read from the effects library (once per run); call each frame until <see cref="Pump"/> says it is done.</summary>
    public static void Request(FxLibrary? library)
    {
        if (_done || _load is not null || library is null || Procedural || ReferenceEquals(library, _failedFor)) return;
        _loadFor = library;
        _load = Task.Run(() => Read(library));
    }

    /// <summary>A read is under way (requested, not yet turned into textures): the rig waits briefly for it so the first weather does not pop from the procedural look to the sprites.</summary>
    public static bool Pending => _load is not null && !_done;

    /// <summary>On the main thread: turns a finished read into textures. True once the sprites are in (or the read failed).</summary>
    public static bool Pump()
    {
        if (_done) return true;
        if (_load is not { IsCompleted: true } task) return false;
        _load = null;
        if (task.IsFaulted || task.IsCanceled)
        {
            // tune (review): a faulted read leaves the run not done, so the next match (a new library) tries again; this match keeps the procedural look
            _failedFor = _loadFor; _loadFor = null;
            Status = "weather sprites: " + (task.Exception?.GetBaseException().Message ?? "cancelled") + " (the procedural look stays; the next match reads again)"; GD.Print(Status);
            return true;
        }
        _done = true; _loadFor = null;
        foreach (var (role, image, tx, ty, material, effect) in task.Result)
            _sprites[role] = new Sprite(ImageTexture.CreateFromImage(image), tx, ty, material, effect);
        Version++;
        Status = $"weather sprites: {_sprites.Count} of {Sources.Length} from the install ({string.Join(", ", _sprites.Values.Select(s => $"{s.Material} {s.Texture.GetWidth()}x{s.Texture.GetHeight()} {s.TilesX}x{s.TilesY}"))})";
        GD.Print(Status);
        return true;
    }

    /// <summary>The worker: the materials' sheets from their effects, the atlas decoded, each sprite cut, turned upright and mipmapped.</summary>
    static List<(Role, Image, int, int, string, string)> Read(FxLibrary library)
    {
        var result = new List<(Role, Image, int, int, string, string)>();
        var systems = library.Systems(Sources.Select(s => s.Effect).Distinct());
        var dds = library.Texture(library.Page.AtlasTexture);
        if (dds is null) return result;
        var atlas = new Image();
        if (atlas.LoadDdsFromBuffer(dds) != Error.Ok) return result;
        if (atlas.IsCompressed()) atlas.Decompress();
        if (atlas.GetFormat() != Image.Format.Rgba8) atlas.Convert(Image.Format.Rgba8);
        int w = atlas.GetWidth(), h = atlas.GetHeight();
        foreach (var (role, name, effect) in Sources)
        {
            if (!systems.TryGetValue(effect, out var sys)) continue;
            var mat = sys.Materials.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            if (mat is null || library.Page.Find(mat) is not { } r) continue;
            var region = new Rect2I((int)MathF.Round(r.X * w), (int)MathF.Round(r.Y * h), Math.Max(1, (int)MathF.Round(r.Width * w)), Math.Max(1, (int)MathF.Round(r.Height * h)));
            region = region.Intersection(new Rect2I(0, 0, w, h));
            if (region.Size.X < 2 || region.Size.Y < 2) continue;
            var img = atlas.GetRegion(region);
            // FxBatch.Sprite's convention: a rect stored rotated maps sprite (u, v) to atlas (1 - v, u) when RotatedClockwise, which a counter-clockwise turn undoes
            if (r.Rotated) img.Rotate90(FxBatch.RotatedClockwise ? ClockDirection.Counterclockwise : ClockDirection.Clockwise);
            if (DumpDir is { Length: > 0 } dump) { System.IO.Directory.CreateDirectory(dump); img.SavePng(System.IO.Path.Combine(dump, $"{role}_{mat.Name}_{mat.TileX}x{mat.TileY}.png")); }
            int tx = Math.Max(1, (int)MathF.Round(mat.TileX)), ty = Math.Max(1, (int)MathF.Round(mat.TileY));
            // the splash sheet's spikes point along +U (a beam grows along its length): turned a quarter counter-clockwise they rise from their base, and the sheet's rows become columns
            if (role == Role.Splash) { img.Rotate90(ClockDirection.Counterclockwise); (tx, ty) = (ty, tx); }
            img.GenerateMipmaps();
            result.Add((role, img, tx, ty, mat.Name, effect));
        }
        return result;
    }

    /// <summary>
    /// Test aid (<c>--weather-sprites-dump &lt;dir&gt;</c>, set before the read, developer-tools sessions only): the worker also writes the cut sprites as PNGs into that private folder for a look at what the weather uses.
    /// Set through <see cref="TrySetDump"/>, which refuses a folder inside the repository or the game install (hard rule: the install is read-only and no original bytes enter the repository).
    /// </summary>
    public static string? DumpDir { get; private set; }

    /// <summary>Sets <see cref="DumpDir"/> unless <paramref name="dir"/> lies inside <paramref name="repoRoot"/> or <paramref name="gameDir"/>; returns the refusal (null when accepted or nothing was asked).</summary>
    public static string? TrySetDump(string? dir, string repoRoot, string gameDir)
    {
        DumpDir = null;
        if (string.IsNullOrWhiteSpace(dir)) return null;
        string full;
        try { full = System.IO.Path.GetFullPath(dir); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException or System.Security.SecurityException) { return "--weather-sprites-dump: not a usable folder"; }
        if ((repoRoot.Length > 0 && SmFormats.InstallPaths.IsWithin(full, repoRoot)) || (gameDir.Length > 0 && SmFormats.InstallPaths.IsWithin(full, gameDir)))
            return "--weather-sprites-dump must not be inside the repository or the game install; nothing is written";
        DumpDir = full;
        return null;
    }
}
