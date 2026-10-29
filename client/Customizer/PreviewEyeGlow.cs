using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;

namespace SmClient.Customizer;

/// <summary>
/// The Chaos eye glow in the customizer's 3D preview (owner's request 2026-10-28: the eye glow follows the lens colour option, also in the previews). The same
/// <see cref="EyeGlow"/> the match plays, in the preview's own <see cref="FxPlayer"/> (one per <see cref="PreviewStage"/>, inside its viewport): the effect of the weapon the
/// preview holds in hand, at the helmet's eye markers, in the data's colour or (a lens colour chosen) tinted with it (<see cref="EyeGlow.TintOf"/>). The effects package,
/// the wargear files and the effect atlas are read once in the background the first time a Chaos character is previewed (<see cref="Forget"/> drops them when the match
/// starts); until they are in, the preview shows no glow. Space Marine previews never start the load: the data has no eye effects for their helmets (spec "Chaos eye glow").
/// </summary>
sealed class PreviewEyeGlow
{
    sealed record Loaded(EyeGlowData Data, FxLibrary Library, Dictionary<string, CSystem> Systems, Image? Atlas);

    static readonly object Gate = new();
    static Task<Loaded?>? _load;
    static string _loadDir = "";

    readonly Node _parent;
    readonly EyeGlow _glow = new();
    FxPlayer? _fx;
    EyeGlowData? _data;
    bool _failed;

    public PreviewEyeGlow(Node parent) => _parent = parent;

    /// <summary>Effects alive now (0 or 2), for tests and logs.</summary>
    public int Live => _glow.Live;
    /// <summary>Effect quads drawn in the last frame the player ran (0 after <see cref="Clear"/>).</summary>
    public int Quads => _fx?.LastQuads ?? 0;
    /// <summary>The state the glow was last started in (helmet, skeleton, effects, tint).</summary>
    public string Key => _glow.Key;

    /// <summary>Whether the glow is switched off for this run (<c>SM_NO_EYE_GLOW=1</c> or <c>--no-eye-glow</c>).</summary>
    static readonly bool OffByArgument = OS.GetCmdlineUserArgs().Contains("--no-eye-glow");
    static bool Off => EyeGlow.Off || OffByArgument;

    /// <summary>Drops the shared background load (the match's own effect player takes over).</summary>
    public static void Forget()
    {
        lock (Gate) { _load = null; _loadDir = ""; }
    }

    /// <summary>
    /// Per frame: starts, keeps or ends the glow of <paramref name="character"/> holding <paramref name="deployed"/> (the wargear in hand), tinted by <paramref name="tint"/>,
    /// and advances the effects. Call with the preview's camera after it is placed.
    /// </summary>
    public void Update(double delta, Camera3D camera, CharacterViewer character, CustomizerServices services, string? deployed, Color? tint)
    {
        if (_failed || Off || !GodotObject.IsInstanceValid(character)) return;
        if (_fx is null && !TryCreate(services)) return;
        if (_fx is not { } fx || _data is not { } data) return;
        _glow.Update(fx, services.GameDir, data, character, deployed, character.Visible, tint);
        if (_glow.Live > 0 || fx.Live > 0) fx.Update(delta, camera);
        _frames++;
    }

    int _frames;

    /// <summary>
    /// The glow has had its say: it is switched off or failed, or the effect player has been running for a few frames (the glow itself shows only when the weapon in hand
    /// binds it). A screenshot waits for this, so the Chaos preview is not taken while the effects still load.
    /// </summary>
    public bool Settled => _failed || Off || (_frames > 8 && (!_glow.Waiting || _glow.BackedOff));

    /// <summary>Ends the effects now (another race is previewed).</summary>
    public void Stop() => _glow.Stop();

    /// <summary>
    /// Takes the glow off the stage: ends the effects, clears the player and draws one empty frame, so no quad of the last frame stays in the viewport (another race is
    /// previewed, or the preview must show no glow). Cheap when nothing is shown.
    /// </summary>
    public void Clear(double delta, Camera3D camera)
    {
        if (_fx is not { } fx) return;
        bool shown = _glow.Live > 0 || fx.Live > 0 || fx.LastQuads > 0;
        _glow.Reset();   // also forgets the started state, so showing the same character again restarts the glow
        if (!shown) return;
        fx.Clear();
        fx.Update(delta, camera);
    }

    static bool _noted;

    /// <summary>One note per run, however many previews fail the same way.</summary>
    static void Note(CustomizerServices services, string text)
    {
        lock (Gate) { if (_noted) return; _noted = true; }
        lock (services.Notes) services.Notes.Add(text);
    }

    bool TryCreate(CustomizerServices services)
    {
        Task<Loaded?> load;
        lock (Gate)
        {
            if (_load is null || _loadDir != services.GameDir)
            {
                if (services.Config is not { } config) return false;
                var names = config.Weapons.Select(w => w.Name).Concat(config.Classes.SelectMany(c => c.MeleeChoices.Select(m => m.Wargear))).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                string dir = services.GameDir;
                _loadDir = dir;
                _load = Task.Run(() => Read(dir, names));
            }
            load = _load;
        }
        if (!load.IsCompleted) return false;
        if (load.IsFaulted || load.Result is not { } loaded)
        {
            _failed = true;
            Note(services, "preview eye glow: " + (load.Exception?.GetBaseException().Message ?? "not available") + "; the preview shows no glow");
            return false;
        }
        var fx = new FxPlayer { Name = "EyeEffects" };
        _parent.AddChild(fx);
        if (!fx.Initialize(loaded.Library, "", loaded.Atlas))
        {
            _failed = true;
            Note(services, "preview eye glow: effects not available (" + fx.Status + "); the preview shows no glow");
            fx.QueueFree();
            return false;
        }
        fx.Preload(loaded.Systems, new Dictionary<string, byte[]>());
        _fx = fx;
        _data = loaded.Data;
        return true;
    }

    /// <summary>The background read: the eye effects' wargear files, the effects they play and the effect atlas (read-only; for a worker thread).</summary>
    static Loaded? Read(string gameDir, List<string> wargear)
    {
        try
        {
            var data = EyeGlowData.Load(gameDir, wargear);
            if (data.Wargear.Count == 0) return null;
            var library = FxLibrary.Load(gameDir);
            var systems = NetworkView.CompileSystems(library, data.Wargear.SelectMany(w => data.For(w)).Select(p => p.Name).Distinct(StringComparer.Ordinal), new List<string>());
            Image? atlas = null;
            if (library.Texture(library.Page.AtlasTexture) is { } dds) { atlas = new Image(); if (atlas.LoadDdsFromBuffer(dds) != Error.Ok) atlas = null; }
            return new Loaded(data, library, systems, atlas);
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            GD.PrintErr("preview eye glow: " + ex.Message);
            return null;
        }
    }
}
