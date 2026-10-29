using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using SmContent;

namespace SmClient;

/// <summary>
/// The original HUD art for <see cref="Hud"/> (M44): <see cref="UiArt"/> is read from the install on a worker thread (read-only, nothing
/// cached on disk); textures are decoded on first use on the main thread. Until the art is there, or when it cannot be read, every
/// lookup returns null and the HUD draws its procedural placeholder shapes instead.
/// </summary>
public sealed class HudArt
{
    readonly Task<UiArt>? _loading;
    UiArt? _art;
    readonly Dictionary<string, Texture2D?> _textures = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Texture2D?> _silhouettes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Task<Image?>> _silhouetteJobs = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(UiSprite Sprite, float Margin), StyleBoxTexture?> _nine = new();
    readonly Dictionary<string, string?> _icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Why the art is missing (empty while loading or when loaded).</summary>
    public string Error { get; private set; } = "";

    public HudArt(string? gameDir)
    {
        if (gameDir is null) { Error = "no install"; return; }
        _loading = Task.Run(() => UiArt.Load(gameDir));
    }

    /// <summary>The loaded art, or null while loading / after a failure.</summary>
    public UiArt? Art
    {
        get
        {
            if (_art is null && _loading is { IsCompleted: true } t)
            {
                if (t.IsCompletedSuccessfully) _art = t.Result;
                else { Error = t.Exception?.GetBaseException().Message ?? "failed"; _art = UiArt.Empty; GD.PrintErr($"HUD art not loaded: {Error}"); }
            }
            return _art;
        }
    }

    public bool Ready => Art is { } a && a.TextureNames.Count > 0;

    /// <summary>A texture by name (decoded once), or null.</summary>
    public Texture2D? Texture(string name)
    {
        if (_textures.TryGetValue(name, out var cached)) return cached;
        if (Art is not { } art) return null;
        Texture2D? tex = null;
        if (art.Texture(name) is { } dds)
        {
            var image = new Image();
            if (image.LoadDdsFromBuffer(dds) == Godot.Error.Ok) tex = ImageTexture.CreateFromImage(image);
            else GD.PrintErr($"HUD art: {name} did not decode");
        }
        _textures[name] = tex;
        return tex;
    }

    /// <summary>
    /// The texture as a flat white silhouette (its alpha only; the kill feed draws weapon pictures this way). Built on a worker thread
    /// (decode, decompress, one pass over the bytes); null until it is ready, so the first frames of a kill line show no picture.
    /// </summary>
    public Texture2D? Silhouette(string name)
    {
        if (_silhouettes.TryGetValue(name, out var cached)) return cached;
        if (Art is not { } art) return null;
        if (!_silhouetteJobs.TryGetValue(name, out var job))
        {
            if (art.Texture(name) is not { } dds) return _silhouettes[name] = null;
            _silhouetteJobs[name] = job = Task.Run(() => MakeSilhouette(dds));
        }
        if (!job.IsCompleted) return null;
        _silhouetteJobs.Remove(name);
        var image = job.IsCompletedSuccessfully ? job.Result : null;
        return _silhouettes[name] = image is null ? null : ImageTexture.CreateFromImage(image);
    }

    static Image? MakeSilhouette(byte[] dds)
    {
        var image = new Image();
        if (image.LoadDdsFromBuffer(dds) != Godot.Error.Ok) return null;
        if (image.IsCompressed()) image.Decompress();
        image.ClearMipmaps();
        image.Convert(Image.Format.Rgba8);
        byte[] px = image.GetData();
        for (int i = 0; i + 3 < px.Length; i += 4)
        {
            int a = px[i + 3];
            px[i] = px[i + 1] = px[i + 2] = 255;
            px[i + 3] = (byte)Math.Min(255, a * 255 / 89);   // alpha above 0.35 becomes opaque
        }
        return Image.CreateFromData(image.GetWidth(), image.GetHeight(), false, Image.Format.Rgba8, px);
    }

    /// <summary>A nine-patch of a sprite (corners of <paramref name="margin"/> texture pixels kept, the rest stretched), or null.</summary>
    public StyleBoxTexture? NinePatch(UiSprite sprite, float margin)
    {
        var key = (sprite, margin);
        if (_nine.TryGetValue(key, out var cached)) return cached;
        if (Texture(sprite.Texture) is not { } tex) return Art is null ? null : _nine[key] = null;
        var box = new StyleBoxTexture
        {
            Texture = tex,
            RegionRect = new Rect2(sprite.X, sprite.Y, sprite.Width, sprite.Height),
            TextureMarginLeft = margin, TextureMarginRight = margin, TextureMarginTop = margin, TextureMarginBottom = margin,
        };
        return _nine[key] = box;
    }

    /// <summary>The weapon picture's texture name for a wargear file, or null.</summary>
    public string? WeaponIcon(string wargear)
    {
        if (_icons.TryGetValue(wargear, out var cached)) return cached;
        if (Art is not { } art) return null;
        return _icons[wargear] = art.WeaponIcon(wargear);
    }
}
