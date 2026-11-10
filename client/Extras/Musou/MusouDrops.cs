using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmNet;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// Added content "Musou" (MU-D; owner 2026-10-31: "Make weapon equip drops glow golden. They should be easy to see."): the own player's weapon drops in the
/// networked view. The server sends this client its own drops only (wire tag 6, the whole list each time; <see cref="MusouWire.ReadDrops"/>); each live one
/// is drawn with the weapon's own model (the held-weapon models, <see cref="WeaponAssets"/>: **original asset loaded**; a gold bar when it cannot be read)
/// under a gold emissive override, turning slowly and bobbing, with a warm light, a tall soft gold beam and a glow on the floor (all ours: the look is the
/// owner's request, not the original's). In reach (1.5 m, the sim's rule) a prompt "E: PICK UP name" shows; an E press hides the drop at once and plays the
/// ammunition pickup's take sound (the core pickup's, when the install has one); the server's list confirms (a refused pickup shows again after 1.5 s).
/// A powerup (owner 2026-11-10) is taken by holding E for <see cref="MusouPowerupRules.HoldSeconds"/> instead: its prompt "HOLD E name" carries a thin bar that fills over the hold, and the drop
/// hides when the hold completes (predicted as the press was; the server checks the hold itself). Headless and lean clients keep nothing drawn.
/// </summary>
public partial class NetworkView
{
    readonly Dictionary<ushort, MusouDropView> _musouDropViews = new();
    readonly Dictionary<ushort, double> _musouDropTaken = new();   // predicted pickups: the drop id and when E took it
    readonly Dictionary<ushort, MusouDropRecord> _musouDropLive = new();
    readonly Dictionary<string, Mesh?> _musouDropMeshes = new(StringComparer.OrdinalIgnoreCase);
    byte[]? _musouDropPayload;
    uint _musouDropTick;
    bool _musouDropUseHeld, _musouDropDirty;
    Label? _musouDropPrompt;
    int _musouDropsSeen, _musouDropsTaken;
    readonly List<(float D2, ushort Id)> _musouDropOrder = new();
    readonly List<ushort> _musouDropScratch = new(), _musouDropGone = new();
    ushort _musouDropPromptFor;
    string? _musouDropPromptText;

    /// <summary>Seconds a predicted pickup stays hidden while the server still lists the drop as lying (a refused pickup shows again).</summary>
    const double MusouDropPredictSeconds = 1.5;

    /// <summary>A tag 6 section for the drops (the newest list wins); false for any other tag (the crowd's).</summary>
    bool MusouDropSection(in ExtrasSection section)
    {
        if (section.Tag != MusouWire.DropsTag) return false;
        if (_musouDropPayload is null || section.ServerTick >= _musouDropTick || _musouDropTick - section.ServerTick > MusouClientState.RestartTicks)
        {
            _musouDropPayload = section.Payload;
            _musouDropTick = section.ServerTick;
            _musouDropDirty = true;
        }
        return true;
    }

    /// <summary>Once a frame (from <see cref="MusouFrame"/>, after the crowd): the list read with the director's box, the views, the prompt and the E press.</summary>
    void MusouDropsFrame(PlayerState me, float delta)
    {
        double now = Time.GetTicksUsec() / 1e6;
        if (_musouDropDirty && _musouDropPayload is not null && _musouState.Director is { } director)
        {
            _musouDropDirty = false;
            if (MusouWire.ReadDrops(_musouDropPayload, director.Box.Sane(), out var buffs) is { } list)
            {
                MusouBuffsReceived(buffs, _musouDropTick);   // the powerups (MusouPowerups.cs): the own buffs after the drops
                _musouDropLive.Clear();
                foreach (var d in list)
                {
                    if (d.State == MusouDropState.Live) { _musouDropLive[d.Id] = d; continue; }
                    // Picked (by this player: the list is its own) or expired: gone; a pickup not predicted plays its sound now.
                    if (d.State == MusouDropState.Picked && _musouDropViews.ContainsKey(d.Id) && !_musouDropTaken.ContainsKey(d.Id)) MusouDropSound(d.Position, me);
                }
                _musouDropScratch.Clear();
                foreach (var id in _musouDropTaken.Keys) if (!_musouDropLive.ContainsKey(id)) _musouDropScratch.Add(id);
                foreach (var id in _musouDropScratch) _musouDropTaken.Remove(id);
            }
        }
        MusouPowerupsFrame(me);   // the own buffs: the HUD's chips and the Supercharged glow (NetworkView.MusouPowerups.cs)
        if (_musouNoDraw || _gameSpace is null) return;

        // The drops to draw: live, not taken by a press just now, the nearest MaxDrawn (the nearest MaxLit with a light).
        _musouDropOrder.Clear();
        foreach (var (id, d) in _musouDropLive)
        {
            if (_musouDropTaken.TryGetValue(id, out double at) && now - at < MusouDropPredictSeconds) continue;
            _musouDropOrder.Add((System.Numerics.Vector3.DistanceSquared(d.Position, me.Position), id));
        }
        _musouDropOrder.Sort();
        if (_musouDropOrder.Count > MusouDropView.MaxDrawn) _musouDropOrder.RemoveRange(MusouDropView.MaxDrawn, _musouDropOrder.Count - MusouDropView.MaxDrawn);
        _musouDropScratch.Clear();
        foreach (var (_, id) in _musouDropOrder) _musouDropScratch.Add(id);
        _musouDropGone.Clear();
        foreach (var id in _musouDropViews.Keys) if (!_musouDropScratch.Contains(id)) _musouDropGone.Add(id);
        foreach (var id in _musouDropGone) MusouRemoveDropView(id);
        for (int k = 0; k < _musouDropOrder.Count; k++)
        {
            ushort id = _musouDropOrder[k].Id;
            var d = _musouDropLive[id];
            if (!_musouDropViews.TryGetValue(id, out var view) || view.Item != d.Item)
            {
                MusouRemoveDropView(id);
                if (MusouPowerupRules.IsPowerup(d.Item)) view = MusouDropView.CreatePowerup(id, d.Item, MusouPowerupRules.KindOf(d.Item));   // owner 2026-11-10: minimal, black and white (MusouDropView.Powerup.cs)
                else
                {
                    string? wargear = _config is null ? null : MusouDropItem.Wargear(_config, me.Class, d.Item);
                    view = MusouDropView.Create(id, d.Item, MusouDropMesh(wargear), MusouDropName(wargear), d.Crate, d.Crate ? PickupModel(0) : null);
                }
                _gameSpace.AddChild(view);
                _musouDropViews[id] = view;
                _musouDropsSeen++;
                GD.Print($"musou: drop {id} ({view.Title}) at {d.Position.X:F1},{d.Position.Y:F1},{d.Position.Z:F1}{(view.Original ? "" : " (gold bar: no model)")}");
            }
            view.Place(d.Position);
            view.Lit = k < MusouDropView.MaxLit;
            view.Frame(delta);
        }

        // The prompt and the press: the nearest own drop in reach of the living own player.
        MusouDropView? near = null;
        float best = float.MaxValue;
        if (me.Alive && !NoHud && !PickerBusy)
            foreach (var (id, view) in _musouDropViews)
            {
                if (!_musouDropLive.TryGetValue(id, out var d) || !World.MusouInReach(d.Position, me.Position)) continue;
                float d2 = System.Numerics.Vector3.DistanceSquared(d.Position, me.Position);
                if (d2 < best) { best = d2; near = view; }
            }
        // Use as the input sends it: the key held or its latched tap while the mouse is captured, or the pad's Use held or latched; nothing with --no-pickups.
        bool held = !NoPickups && ((Input.MouseMode == Input.MouseModeEnum.Captured && (ControlsInput.Held(GameAction.Use) || _interactLatched))
                                   || (_pad is not null && PadHost.Active && (ControlsInput.PadHeld(_pad, GameAction.Use) || _padInteractLatched)));
        bool press = held && !_musouDropUseHeld;
        // A powerup is taken by holding Use (owner 2026-11-10: "Hold the use key"; MusouPowerupRules.HoldSeconds, the server checks it): the press on the nearest begins the hold, which goes
        // on while Use stays down and that drop stays in reach (as the server's); the prompt shows it filling. A weapon or a crate goes at the press, as before.
        if (press && near is not null && near.Minimal) { _musouHoldFor = near.Id; _musouHoldAt = now; }
        float hold = -1f;
        if (_musouHoldFor != 0)
        {
            if (held && me.Alive && !NoHud && !PickerBusy && _musouDropViews.TryGetValue(_musouHoldFor, out var holding) && _musouDropLive.TryGetValue(_musouHoldFor, out var d) && World.MusouInReach(d.Position, me.Position))
            {
                near = holding;   // the prompt stays on the drop being taken
                hold = (float)Math.Clamp((now - _musouHoldAt) / MusouPowerupRules.HoldSeconds, 0.0, 1.0);
            }
            else _musouHoldFor = 0;
        }
        bool take = hold >= 1f || (press && near is not null && !near.Minimal);
        if (take && near is not null && _musouDropLive.TryGetValue(near.Id, out var taken))
        {
            // At once on the picker's own client: hidden and heard now; the server's next list confirms (a refused one shows again after MusouDropPredictSeconds).
            _musouDropTaken[near.Id] = now;
            _musouDropsTaken++;
            MusouDropSound(taken.Position, me);
            GD.Print($"musou: drop {near.Id} ({near.Title}) taken{(near.Minimal ? " (held)" : "")}");
            MusouRemoveDropView(near.Id);
            near = null;
            _musouHoldFor = 0;
            hold = -1f;
        }
        _musouDropUseHeld = held;
        // The prompt's text is made only when the drop in reach changes (no string a frame).
        if (near is null) _musouDropPromptFor = 0;
        else if (near.Id != _musouDropPromptFor || _musouDropPromptText is null)
        {
            _musouDropPromptFor = near.Id;
            _musouDropPromptText = near.Minimal
                ? $"HOLD {ControlsInput.Prompt(GameAction.Use)}   {near.Title}"   // a powerup's prompt: hold the key, and the name (owner 2026-11-10)
                : $"{ControlsInput.Prompt(GameAction.Use)}: {(near.Crate ? "OPEN CRATE" : "PICK UP")} {near.Title.ToUpperInvariant()}";
        }
        MusouDropPromptShow(near is null ? null : _musouDropPromptText, near is { Minimal: true } ? MathF.Max(0f, hold) : -1f);
    }

    /// <summary>The powerup drop the own Use is held on (0: none), and when the hold began (seconds, the frame clock).</summary>
    ushort _musouHoldFor;
    double _musouHoldAt;
    ColorRect? _musouHoldTrack, _musouHoldFill;
    /// <summary>The hold bar under a powerup's prompt (ours): a thin white line filling over the hold, on a faint track.</summary>
    const float MusouHoldBarWidth = 180f, MusouHoldBarHeight = 3f;

    void MusouRemoveDropView(ushort id)
    {
        if (!_musouDropViews.Remove(id, out var view)) return;
        view.QueueFree();
    }

    void MusouDropSound(System.Numerics.Vector3 at, PlayerState me)
    {
        if (_sound is null) return;
        SmContent.PickupLook? look = null;
        foreach (var l in _pickupLooks) if (l.TakeSound.Length > 0) { look = l; break; }
        if (look is null) return;
        _sound.Post(look.TakeSound, new SoundEmitter { Position = ToGodot(at), Label = "pickup", OneShot = true, Own = true });
        CountSound("pickup");
    }

    /// <summary>The prompt line, and under a powerup's (<paramref name="hold"/> 0 to 1; below 0: none) the hold bar: its faint track, and the white fill as far as the hold has run.</summary>
    void MusouDropPromptShow(string? text, float hold = -1f)
    {
        if (text is null)
        {
            if (_musouDropPrompt is not null) _musouDropPrompt.Visible = false;
            if (_musouHoldTrack is not null) _musouHoldTrack.Visible = _musouHoldFill!.Visible = false;
            return;
        }
        if (_musouDropPrompt is null)
        {
            var layer = new CanvasLayer { Name = "MusouDropPrompt", Layer = 3 };
            // The bottom edge's strip (MusouHudLayout, 2026-11-10: over the war's prompts, under the weapon box's top); --musou-old-hud: 62 % down the middle.
            float at = MusouHudLayout.Old ? 0.62f : 0.865f;
            _musouDropPrompt = new Label
            {
                Name = "Prompt", HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore,
                AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = at, AnchorBottom = at, OffsetLeft = -320, OffsetRight = 320,
            };
            if (!MusouHudLayout.Old) _musouDropPrompt.GrowVertical = Control.GrowDirection.Begin;   // its bottom on the line, the text above it: clear of the pickup line at any height
            // The black and white theme (owner 2026-11-10): the menus' small bold white capitals, a thin dark outline (the gold 24 px line before).
            _musouDropPrompt.AddThemeFontSizeOverride("font_size", 18);
            _musouDropPrompt.AddThemeColorOverride("font_color", new Color(0.93f, 0.93f, 0.93f));
            _musouDropPrompt.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.85f));
            _musouDropPrompt.AddThemeConstantOverride("outline_size", 4);
            layer.AddChild(_musouDropPrompt);
            // The hold bar (owner 2026-11-10: "Hold the use key"): just under the line (the new layout: the text sits above it; the old: the text hangs from it, so under the text).
            float top = MusouHudLayout.Old ? 30f : 5f;
            ColorRect Bar(string name, Color colour) => new()
            {
                Name = name, Color = colour, MouseFilter = Control.MouseFilterEnum.Ignore, AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = at, AnchorBottom = at,
                OffsetLeft = -MusouHoldBarWidth / 2f, OffsetRight = MusouHoldBarWidth / 2f, OffsetTop = top, OffsetBottom = top + MusouHoldBarHeight, Visible = false,
            };
            _musouHoldTrack = Bar("HoldTrack", new Color(0.93f, 0.93f, 0.93f, 0.22f));
            _musouHoldFill = Bar("HoldFill", new Color(0.97f, 0.97f, 0.97f, 0.95f));
            layer.AddChild(_musouHoldTrack);
            layer.AddChild(_musouHoldFill);
            AddChild(layer);
        }
        if (!ReferenceEquals(text, _musouDropPromptShown)) { _musouDropPrompt.Text = text; _musouDropPromptShown = text; }
        _musouDropPrompt.Visible = true;
        bool bar = hold >= 0f;
        _musouHoldTrack!.Visible = bar;
        _musouHoldFill!.Visible = bar && hold > 0f;
        if (bar && hold > 0f) _musouHoldFill.OffsetRight = -MusouHoldBarWidth / 2f + MusouHoldBarWidth * Math.Clamp(hold, 0f, 1f);
    }

    string? _musouDropPromptShown;

    /// <summary>The weapon's held model (baked once per wargear), or null.</summary>
    Mesh? MusouDropMesh(string? wargear)
    {
        if (wargear is null || _weaponAssets is null) return null;
        if (_musouDropMeshes.TryGetValue(wargear, out var cached)) return cached;
        Mesh? mesh = null;
        try { if (_weaponAssets.Catalogue.Held(wargear) is { } held) mesh = _weaponAssets.Get(held)?.Mesh; }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { GD.PrintErr($"musou: drop model {wargear}: {ex.Message}"); }
        _musouDropMeshes[wargear] = mesh;
        return mesh;
    }

    /// <summary>A readable name from the wargear file name (ours: "pistol_plasma_pistol" reads "Plasma Pistol").</summary>
    static string MusouDropName(string? wargear)
    {
        if (string.IsNullOrEmpty(wargear)) return "Weapon";
        var words = wargear.ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries).ToList();
        string[] prefixes = ["sm", "csm", "mp", "ranged", "pistol", "melee", "2hand", "heavy"];
        while (words.Count > 1 && prefixes.Contains(words[0])) words.RemoveAt(0);
        bool relic = words.Remove("gold");
        var name = string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        return relic ? "Relic " + name : name;
    }

    /// <summary>The F1 line about the drops.</summary>
    string MusouDropsInfoLine() => $"Musou drops: lying {_musouDropLive.Count}, drawn {_musouDropViews.Count}, seen {_musouDropsSeen}, taken {_musouDropsTaken}";
}

/// <summary>One golden weapon drop in game space (MU-D; the look is ours): the weapon model gold and glowing, turning and bobbing, a warm light, a beam, a floor glow.</summary>
public sealed partial class MusouDropView : Node3D
{
    public ushort Id { get; private set; }
    public ushort Item { get; private set; }
    public string Title { get; private set; } = "";
    public bool Original { get; private set; }
    /// <summary>A Musou Mode weapon crate (M6): the install's ammunition case made large under the weapon, so it reads apart from a boss's drop and from the small boxes on the floor.</summary>
    public bool Crate { get; private set; }
    Node3D? _spin;
    float _age;

    /// <summary>The look (ours): turn rate (rad/s), bob (m and Hz), the model's height over the floor, the beam's height.</summary>
    const float SpinRate = 1.2f, BobMetres = 0.08f, BobHz = 0.5f, Lift = 0.9f, BeamHeight = 16f, ModelScale = 1.4f;
    /// <summary>Most drops drawn at once (the nearest), and of them the nearest that carry a light (ours: the lead's caps).</summary>
    public const int MaxDrawn = 16, MaxLit = 4;

    OmniLight3D? _light;

    /// <summary>Whether this drop carries its warm light (created or freed when it changes).</summary>
    public bool Lit
    {
        get => _light is not null;
        set
        {
            if (Minimal || value == (_light is not null)) return;   // a powerup carries no light
            if (value)
            {
                _light = new OmniLight3D { LightColor = new Color(1f, 0.74f, 0.3f), LightEnergy = 2.2f, OmniRange = 5f, Position = new Vector3(0f, 0f, Lift + 0.3f), ShadowEnabled = false };
                AddChild(_light);
            }
            else { _light!.QueueFree(); _light = null; }
        }
    }

    static StandardMaterial3D? _gold, _beam, _core, _floor;

    static StandardMaterial3D Gold() => _gold ??= new StandardMaterial3D
    {
        AlbedoColor = new Color(0.95f, 0.7f, 0.22f), Metallic = 0.9f, Roughness = 0.32f,
        EmissionEnabled = true, Emission = new Color(1f, 0.62f, 0.14f), EmissionEnergyMultiplier = 0.45f,
    };

    static StandardMaterial3D Glow(float alpha, Texture2D? texture, bool nearFade = false) => new()
    {
        // The beam fades out close to the camera (it would fill the view standing next to it) and stays bright far away.
        DistanceFadeMode = nearFade ? BaseMaterial3D.DistanceFadeModeEnum.PixelAlpha : BaseMaterial3D.DistanceFadeModeEnum.Disabled,
        DistanceFadeMinDistance = 2f, DistanceFadeMaxDistance = 9f,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled, AlbedoColor = new Color(1f, 0.72f, 0.2f, alpha), AlbedoTexture = texture, DisableReceiveShadows = true,
    };

    /// <summary>A gradient from opaque (bottom) to clear (top) along the texture's V.</summary>
    static GradientTexture2D Fade()
    {
        var g = new Gradient();
        g.SetColor(0, new Color(1f, 1f, 1f, 0f));
        g.SetColor(1, new Color(1f, 1f, 1f, 1f));
        g.AddPoint(0.8f, new Color(1f, 1f, 1f, 0.55f));
        return new GradientTexture2D { Gradient = g, Width = 4, Height = 128, FillFrom = new Vector2(0f, 0f), FillTo = new Vector2(0f, 1f) };
    }

    static GradientTexture2D Radial()
    {
        var g = new Gradient();
        g.SetColor(0, new Color(1f, 1f, 1f, 1f));
        g.SetColor(1, new Color(1f, 1f, 1f, 0f));
        return new GradientTexture2D { Gradient = g, Width = 128, Height = 128, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 0.5f) };
    }

    public static MusouDropView Create(ushort id, ushort item, Mesh? model, string title, bool crate = false, Mesh? crateModel = null)
    {
        var view = new MusouDropView { Id = id, Item = item, Title = title, Name = $"MusouDrop{id}", Crate = crate };
        if (crate)
        {
            const float size = 1.5f;   // ours: a crate a Marine stands beside
            var body = crateModel is not null
                ? new MeshInstance3D { Mesh = crateModel, Scale = new Vector3(size, size, size), Layers = 2 }
                : new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.1f, 0.8f, 0.7f) }, Position = new Vector3(0f, 0f, 0.35f), Layers = 2 };
            view.AddChild(body);
        }
        view._spin = new Node3D { Name = "Spin", Position = new Vector3(0f, 0f, Lift), Scale = new Vector3(ModelScale, ModelScale, ModelScale) };
        view.AddChild(view._spin);
        if (model is not null)
        {
            // Centred on its own box so it turns about its middle.
            var box = model.GetAabb();
            var mesh = new MeshInstance3D { Mesh = model, MaterialOverride = Gold(), Layers = 2, Position = -box.GetCenter(), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            view._spin.AddChild(mesh);
            view.Original = true;
        }
        else view._spin.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.9f, 0.14f, 0.24f), Material = Gold() }, Layers = 2 });
        // The light, warm and close.
        // The light: only the nearest MaxLit drops have one (Lit).
        // The beam: a tall soft column (a cylinder's axis is its Y: turned up to game Z), a brighter thin core inside.
        _beam ??= Glow(0.16f, Fade(), nearFade: true);
        _core ??= Glow(0.45f, Fade(), nearFade: true);
        var turnUp = new Vector3(Mathf.Pi / 2f, 0f, 0f);
        view.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.35f, BottomRadius = 0.35f, Height = BeamHeight, CapTop = false, CapBottom = false, RadialSegments = 24, Material = _beam },
            Rotation = turnUp, Position = new Vector3(0f, 0f, BeamHeight / 2f), Layers = 2, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        view.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.07f, BottomRadius = 0.07f, Height = BeamHeight * 0.8f, CapTop = false, CapBottom = false, RadialSegments = 12, Material = _core },
            Rotation = turnUp, Position = new Vector3(0f, 0f, BeamHeight * 0.4f), Layers = 2, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        // The floor glow (a quad lies in its XY plane: flat on the game floor).
        _floor ??= Glow(0.75f, Radial());
        view.AddChild(new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(2.6f, 2.6f), Material = _floor }, Position = new Vector3(0f, 0f, 0.04f), Layers = 2,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        return view;
    }

    /// <summary>Moves it to the server's place (game space, metres; the floor under the drop).</summary>
    public void Place(System.Numerics.Vector3 at) => Position = new Vector3(at.X, at.Y, at.Z);

    /// <summary>The turn and the bob.</summary>
    public void Frame(float delta)
    {
        _age += delta;
        if (_spin is null) return;
        if (Minimal) { _spin.Position = new Vector3(0f, 0f, TileLift + BobMetres * Mathf.Sin(_age * Mathf.Tau * BobHz)); return; }   // a powerup bobs, does not turn
        _spin.Rotation = new Vector3(0f, 0f, (_age * SpinRate + Id * 2.39996323f) % Mathf.Tau);
        _spin.Position = new Vector3(0f, 0f, Lift + BobMetres * Mathf.Sin(_age * Mathf.Tau * BobHz));
    }
}
