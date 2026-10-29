using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;

namespace SmClient;

/// <summary>
/// The menu sounds of the front end and the customizer (fidelity gap 9; <see cref="UiSoundRules"/>): every <see cref="BaseButton"/> under a root given to <see cref="Attach"/> plays the
/// original's <c>Play_Menu_Move</c> when the pointer or the keyboard focus reaches it, and <c>Play_Menu_Select</c> / <c>Play_Menu_Back</c> when it is pressed. The roots are the front end's
/// layer and the in-match picker's layer, so the scene-tree handler that catches buttons added later exists only while one of them does (none during a match). One node for the whole
/// app. Its own small catalogue (<see cref="AudioCatalog.OpenUi"/>: two packages) opens in the background when the first root is attached, and is used until a match takes over
/// (<see cref="Redirect"/>: the networked view posts through its own sound system, and this node disposes its catalogue, even one still loading). When the match's view is freed the
/// redirect ends by itself in the next frame and the catalogue opens again, before the first press. **Interpretation**: which of our buttons is a Select and which a Back, and that the keyboard
/// focus counts only when the player moved it (a screen that grabs focus when it opens is silent). <c>--no-sound</c> switches it off. The decoded audio stays in the private cache, as the match's.
/// </summary>
public partial class UiSounds : Node
{
    /// <summary>The node of the running app (null before it is made).</summary>
    public static UiSounds? Current { get; private set; }

    readonly string _gameDir;
    readonly string? _repoRoot;
    readonly bool _enabled;
    Task<AudioCatalog>? _load;
    AudioCatalog? _catalog;
    SoundSystem? _sound;
    readonly SoundEmitter _emitter = new() { Label = "ui" };
    readonly List<Node> _roots = new();
    Action<string>? _redirect;
    GodotObject? _redirectOwner;
    bool _failed, _listening;
    double _now, _lastMove = double.NegativeInfinity, _navAt = double.NegativeInfinity;
    static readonly StringName HookedMeta = "ui_sounds_hooked";

    /// <summary>Counts for the log and the tests of this node.</summary>
    public int Posts { get; private set; }

    /// <summary>Whether the scene-tree handler for added buttons is connected (only while a root is attached).</summary>
    public bool Listening => _listening;

    public UiSounds(string gameDir, string? repoRoot, bool enabled)
    {
        _gameDir = gameDir;
        _repoRoot = repoRoot;
        _enabled = enabled;
        Name = "UiSounds";
        ProcessMode = ProcessModeEnum.Always;
    }

    /// <summary>Makes the node under <paramref name="parent"/> once (later calls return the first).</summary>
    public static UiSounds Ensure(Node parent, string gameDir, bool noSound)
    {
        if (Current is { } existing && GodotObject.IsInstanceValid(existing)) return existing;
        string? repo = null;
        try { repo = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "..")); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.IOException) { }
        var node = new UiSounds(gameDir, repo, !noSound);
        parent.AddChild(node);
        return node;
    }

    public override void _EnterTree() => Current = this;

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
        StopListening();
    }

    public override void _Process(double delta)
    {
        _now += delta;
        // The match's view is gone: this node's own sounds again, and the catalogue opens now rather than at the first press.
        if (_redirect is not null && _redirectOwner is not null && !IsInstanceValid(_redirectOwner)) { _redirect = null; _redirectOwner = null; }
        if (_enabled && _redirect is null && _sound is null && !_failed && _roots.Count > 0) TryStart();
    }

    /// <summary>The keyboard or controller moved the focus (a press of a navigation key): the next focus change is a highlight move the player made.</summary>
    public override void _Input(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true } or InputEventJoypadButton { Pressed: true } or InputEventJoypadMotion)
            _navAt = _now;
    }

    /// <summary>
    /// Gives the buttons under <paramref name="root"/> the menu sounds: those there now and those added until the root leaves the tree. The first root attached starts opening the
    /// catalogue (unless a match plays the sounds).
    /// </summary>
    public void Attach(Node root)
    {
        if (!_enabled || _roots.Contains(root)) return;
        _roots.Add(root);
        root.TreeExited += () => Detach(root);
        HookTree(root);
        if (!_listening && IsInsideTree()) { GetTree().NodeAdded += OnNodeAdded; _listening = true; }
    }

    void Detach(Node root)
    {
        _roots.Remove(root);
        if (_roots.Count == 0) StopListening();
    }

    void StopListening()
    {
        if (!_listening) return;
        _listening = false;
        if (IsInsideTree()) GetTree().NodeAdded -= OnNodeAdded;
    }

    /// <summary>
    /// A match owns the sounds now: <paramref name="post"/> plays an event name through the networked view (null: back to this node's own catalogue). The own catalogue is
    /// released while redirected, one still loading is disposed when it finishes. <paramref name="owner"/> is the view; once it is freed the redirect ends by itself.
    /// </summary>
    public void Redirect(Action<string>? post, GodotObject? owner = null)
    {
        _redirect = post;
        _redirectOwner = owner;
        if (post is null) return;
        _sound?.QueueFree();
        _sound = null;
        _catalog = null;
        var load = _load;
        _load = null;
        if (load is null) return;
        if (load.IsCompleted) { if (load.IsCompletedSuccessfully) DisposeLater(load.Result); }
        else _ = load.ContinueWith(t => { if (t.IsCompletedSuccessfully) DisposeQuietly(t.Result); }, TaskScheduler.Default);
    }

    static void DisposeLater(AudioCatalog catalog) => _ = Task.Run(() => DisposeQuietly(catalog));

    static void DisposeQuietly(AudioCatalog catalog)
    {
        try { catalog.Dispose(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    void OnNodeAdded(Node node)
    {
        if (node is not BaseButton button) return;
        foreach (var root in _roots)
            if (IsInstanceValid(root) && root.IsAncestorOf(button)) { Hook(button); return; }
    }

    void HookTree(Node node)
    {
        if (node is BaseButton button) Hook(button);
        foreach (var child in node.GetChildren()) HookTree(child);
    }

    void Hook(BaseButton button)
    {
        if (button.HasMeta(HookedMeta)) return;
        button.SetMeta(HookedMeta, true);
        button.Pressed += () => Play(UiSoundRules.ForPress((button as Button)?.Text));
        button.MouseEntered += () => { if (!button.Disabled) Move(); };
        button.FocusEntered += () => { if (!button.Disabled && _now - _navAt < 0.25) Move(); };
    }

    void Move()
    {
        if (_now - _lastMove < UiSoundRules.MoveSpacing) return;
        _lastMove = _now;
        Play(UiSoundRules.Move);
    }

    /// <summary>Plays a menu event (through the match when redirected).</summary>
    public void Play(string eventName)
    {
        if (!_enabled || _failed) return;
        Posts++;
        if (_redirect is not null && _redirectOwner is not null && !IsInstanceValid(_redirectOwner)) { _redirect = null; _redirectOwner = null; }   // the match is gone: back to this node's own sounds
        if (_redirect is not null) { LogUi(eventName, "through the match", null); _redirect(eventName); return; }
        if (_sound is null && !TryStart()) { LogUi(eventName, "dropped: catalogue still loading", null); return; }
        var play = _sound!.Post(eventName, _emitter);
        LogUi(eventName, "own catalogue", play);
    }

    /// <summary><c>--log-sound ui</c>: each menu sound with where it played and the voices it resolved to.</summary>
    void LogUi(string eventName, string where, SoundPlay? play)
    {
        if (!NetworkView.LogSoundKinds.Contains("ui")) return;
        GD.Print($"UI SOUND {eventName} ({where}) | " + (play is null ? "" : !play.Found ? "event not in the banks" : $"{play.Voices.Count} voices" +
            (play.Voices.Count == 0 ? "" : ": " + string.Join("; ", play.Voices.Select(v => $"wem {v.WemId} {_catalog?.Duration(v.WemId) ?? 0f:F2}s {(v.Is3D ? "3D" : "2D")}")))));
    }

    /// <summary>Starts loading when asked; true once the sound system exists. The sounds before it is ready are dropped (a menu sound that late is worse than none).</summary>
    bool TryStart()
    {
        if (_load is null)
        {
            string gameDir = _gameDir;
            string? repo = _repoRoot;
            _load = Task.Run(() => AudioCatalog.OpenUi(gameDir, null, repo));
            return false;
        }
        if (!_load.IsCompleted) return false;
        if (_load.IsFaulted || _load.IsCanceled)
        {
            _failed = true;
            GD.PrintErr("ui sounds: " + (_load.Exception?.GetBaseException().Message ?? "cancelled"));
            return false;
        }
        _catalog = _load.Result;
        _sound = new SoundSystem(_catalog) { Name = "UiSoundSystem" };
        AddChild(_sound);
        var cat = _catalog;
        _sound.Prefetch(new[] { UiSoundRules.Move, UiSoundRules.Select, UiSoundRules.Back }.SelectMany(cat.WemsOf).Distinct());
        GD.Print($"ui sounds: {_catalog.LoadedPackages.Count} packages in {_catalog.LoadMilliseconds:F0} ms");
        return true;
    }
}
