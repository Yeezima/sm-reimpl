using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmFormats.Fx;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// Fidelity gap 1: the ammunition and grenade drops in the networked view. The server's pickup messages (<see cref="GameClient.Pickups"/>,
/// protocol 26) say what lies where; each pickup is drawn with its blueprint's model (<see cref="PickupView"/>); the grenade drop plays its
/// lifetime effect (<c>pickup_grenades</c>) while it lies there. In reach (the type's <c>interact_distance</c>) the prompt shows the
/// blueprint's message (locale 104312 "Pick up Ammo", 104313 "Pick up Equipment", read from the install at runtime) after the key; E sends
/// <see cref="InputButtons.Interact"/> (the original's "Use / Execute" binding; its PC key is not in the data: E is ours). A collection plays
/// the take effect (<c>pickup_ammo_take</c>) and sound (<c>play_pickup_ammo</c> / <c>play_pickup_grenades</c>) at the pickup, for everyone
/// (the data's <c>apply_only_if_on_player</c> is false). Nothing is predicted: the pickup goes when the server says so.
/// Not drawn: the HUD's world icons (<c>decorator_type</c> <c>ammo_pickup_decorator</c>), the pick-up animation (none in the MP rig).
/// <c>--no-pickups</c> draws none (and sends no Interact); <c>--bot-pickups</c> (test aid) sends bots to the nearest pickup when their
/// ammunition or grenades are low.
/// </summary>
public partial class NetworkView
{
    /// <summary><c>--no-pickups</c>: neither drawn nor prompted (E sends nothing).</summary>
    static bool NoPickups => CmdFlag("no-pickups");
    /// <summary><c>--bot-pickups</c> (test aid): bots press E in reach of a pickup and walk to the nearest one when low (<see cref="BotPickupInput"/>); without it bots never collect.</summary>
    static bool BotPickups => CmdFlag("bot-pickups");

    Task<(IReadOnlyList<PickupLook> Looks, Dictionary<int, string> Text, List<string> Notes)>? _pickupLoad;
    IReadOnlyList<PickupLook> _pickupLooks = [];
    Dictionary<int, string> _pickupText = new();
    readonly Dictionary<int, PickupView> _pickupViews = new();
    readonly Dictionary<int, Mesh?> _pickupModels = new();
    Task<(Dictionary<string, CSystem> Systems, Dictionary<string, byte[]> Decals, List<string> Failures)>? _pickupFxLoad;
    bool _pickupFxReady;
    Label? _pickupPrompt;
    /// <summary>ui/hud-fidelity: the prompt this frame (empty: none), for the HUD.</summary>
    string _hudPrompt = "";
    string _pickupNote = "not loaded";
    int _pickupsSeen, _pickupsCollectedSeen, _pickupsExpiredSeen, _ownCollections;
    double _pickupLogAt;

    /// <summary>Reads the pickups' looks and prompt texts in the background (the blueprints, read-only).</summary>
    void SetUpPickups()
    {
        if (_config?.Pickups is null) { _pickupNote = "none (the configuration has no drops)"; return; }
        if (NoPickups) { _pickupNote = "switched off (--no-pickups)"; return; }
        if (Lean && !BotPickups) { _pickupNote = "not loaded (lean bot; with --bot-pickups they are: the bot walks to the drops' views)"; return; }   // perf/lean-bots
        var config = _config;
        string dir = _gameDir;
        _pickupLoad = Task.Run(() =>
        {
            var notes = new List<string>();
            var (_, looks) = PickupTuning.Read(config, dir, null, null, notes);
            SmFormats.Locale.LocaleTable? locale = null;
            string path = System.IO.Path.Combine(dir, "data", "locale", "english", "ucs.pc");
            try { locale = SmFormats.Locale.LocaleTable.Open(path); }
            catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException) { notes.Add($"locale not read ({ex.Message}); the prompt is in English from the code"); }
            var text = new Dictionary<int, string>();
            foreach (var l in looks) if (locale?.Get(l.Message) is { } t) text[l.Message] = t;
            return (looks, text, notes);
        });
        var layer = new CanvasLayer { Name = "PickupPrompt", Layer = 3 };
        _pickupPrompt = new Label
        {
            Name = "Prompt", Visible = false, HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.68f, AnchorBottom = 0.68f, OffsetLeft = -240, OffsetRight = 240,
        };
        _pickupPrompt.AddThemeFontSizeOverride("font_size", 22);
        _pickupPrompt.AddThemeColorOverride("font_color", new Color(0.95f, 0.92f, 0.8f));
        _pickupPrompt.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.85f));
        _pickupPrompt.AddThemeConstantOverride("outline_size", 6);
        layer.AddChild(_pickupPrompt);
        AddChild(layer);
        _pickupNote = "loading";
    }

    /// <summary>Takes the background load once it is done; false until then.</summary>
    bool PickupsReady()
    {
        if (_pickupLoad is null) return false;
        if (!_pickupLoad.IsCompleted) return false;
        if (_pickupNote == "loading")
        {
            if (_pickupLoad.IsFaulted) { _pickupNote = "looks not available: " + _pickupLoad.Exception?.GetBaseException().Message; GD.Print("pickups: " + _pickupNote); }
            else
            {
                (_pickupLooks, _pickupText, var notes) = _pickupLoad.Result;
                foreach (var n in notes) GD.Print("  pickup note: " + n);
                _pickupNote = string.Join("; ", _pickupLooks.Select(l => $"{l.Blueprint}: model {l.VisualPackage}/{l.Visual} in {(l.Archive.Length > 0 ? l.Archive : "no archive")}, prompt {l.Message} '{_pickupText.GetValueOrDefault(l.Message, "?")}', take {l.TakeFx} + {l.TakeSound}{(l.IdleFx.Length > 0 ? $", lying {l.IdleFx}" : "")}"));
                GD.Print("pickups: " + _pickupNote);
            }
        }
        return true;
    }

    PickupLook? LookOf(int type) => type >= 0 && type < _pickupLooks.Count ? _pickupLooks[type] : null;

    /// <summary>The pickup's model (baked once per type), or null for the placeholder.</summary>
    Mesh? PickupModel(int type)
    {
        if (_pickupModels.TryGetValue(type, out var cached)) return cached;
        Mesh? mesh = null;
        if (LookOf(type) is { Archive.Length: > 0, Visual.Length: > 0 } look && _weaponAssets is not null)
        {
            // The weapon cache reads data/packages/<package>.oppc; the drop's model lies in an entity-set chunk, so name it relative to there.
            string packages = System.IO.Path.Combine(_gameDir, "data", "packages");
            string archive = System.IO.Path.ChangeExtension(System.IO.Path.Combine(_gameDir, look.Archive), null);
            var attachment = new WeaponAttachment("pickup", look.Blueprint, "pickup", System.IO.Path.GetRelativePath(packages, archive), look.Visual, null, null, false, [], []) { IsWorn = false };
            mesh = _weaponAssets.Get(attachment)?.Mesh;
        }
        _pickupModels[type] = mesh;
        GD.Print($"pickups: type {type} drawn {(mesh is null ? "as the placeholder box" : "with its original model")}");
        return mesh;
    }

    /// <summary>
    /// perf/pass-1: the drops' models are read during the map load (the handshake waits for it anyway), not when the first drop appears: the first ammunition
    /// drop's model (<c>ammocase_bolter_small</c> in the versus set chunk) cost a 100 to 120 ms frame about 20 s into every match (measured on the e792504 build).
    /// Waits at most <paramref name="waitMs"/> for the blueprint read started in <see cref="SetUpPickups"/>; without it the model is read at the first drop as before.
    /// </summary>
    void PrewarmPickups(int waitMs = 1500)
    {
        if (!HitchFixes || Lean || _pickupLoad is null || _weaponAssets is null) return;   // a lean bot draws nothing: its drops' models load at the first drop as before
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try { _pickupLoad.Wait(waitMs); } catch (AggregateException) { }   // a failed read is reported by PickupsReady
        if (!PickupsReady()) { GD.Print($"pickups: models not warmed (the blueprint read is still running after {waitMs} ms); they load at the first drop"); return; }
        for (int type = 0; type < _pickupLooks.Count; type++) PickupModel(type);
        GD.Print($"pickups: {_pickupLooks.Count} model(s) warmed during the map load in {watch.ElapsedMilliseconds} ms");
    }

    /// <summary>The take and lying effects compiled in the background once the effect player is ready.</summary>
    void PickupFx()
    {
        if (_pickupFxReady || _fx is not { IsReady: true } fx || _pickupLooks.Count == 0) return;
        if (_pickupFxLoad is null)
        {
            var names = _pickupLooks.SelectMany(l => new[] { l.TakeFx, l.IdleFx }).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            var library = fx.Library;
            if (library is null) { _pickupFxReady = true; return; }
            _pickupFxLoad = Task.Run(() =>
            {
                var failures = new List<string>();
                var systems = CompileSystems(library, names, failures);
                return (systems, library.Textures(FxPlayer.DecalNames(systems.Values)), failures);
            });
            return;
        }
        if (!_pickupFxLoad.IsCompleted) return;
        _pickupFxReady = true;
        if (_pickupFxLoad.IsFaulted) { GD.Print("pickups: effects not available: " + _pickupFxLoad.Exception?.GetBaseException().Message); return; }
        var (loaded, decals, fails) = _pickupFxLoad.Result;
        fx.Preload(loaded, decals);
        GD.Print($"pickups: {loaded.Count} effect(s) compiled ({string.Join(", ", loaded.Keys)}){(fails.Count > 0 ? "; not parsed: " + string.Join(", ", fails) : "")}");
    }

    /// <summary>Once per frame: the pickups shown, the events (effects, sounds), the prompt.</summary>
    void UpdatePickups(PlayerState me)
    {
        if (_client is null) return;
        if (_config?.Pickups is not { } spec || _gameSpace is null || !PickupsReady())
        {
            _client.TakePickupEvents();   // not shown yet (or never): the events are dropped, so none replays late and the queue stays empty
            if (_pickupPrompt is not null) _pickupPrompt.Visible = false;
            return;
        }
        PickupFx();
        var live = _client.Pickups;
        var seen = new HashSet<int>();
        foreach (var p in live)
        {
            seen.Add(p.Id);
            if (!_pickupViews.TryGetValue(p.Id, out var view) || view.Type != p.Type)
            {
                if (view is not null) RemovePickupView(view);
                view = PickupView.Create(p.Id, p.Type, PickupModel(p.Type));
                _gameSpace.AddChild(view);
                _pickupViews[p.Id] = view;
                _pickupsSeen++;
                if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} sees pickup #{p.Id} {LookOf(p.Type)?.Blueprint ?? $"type {p.Type}"} at {p.Position.X:F1},{p.Position.Y:F1},{p.Position.Z:F1}{(view.Original ? "" : " (placeholder box)")}");
            }
            view.Place(p.Position);
            // The lying effect once the effects are compiled (also for pickups that were already lying then).
            if (_pickupFxReady && !view.IdleTried && _fx is { IsReady: true } && !NoFx)
            {
                view.IdleTried = true;
                if (LookOf(p.Type) is { IdleFx.Length: > 0 } look) view.Idle = _fx.Play(look.IdleFx, ToGodot(p.Position), Basis.Identity, look.IdleFxScale, repeat: true);
            }
        }
        foreach (var gone in _pickupViews.Keys.Where(id => !seen.Contains(id)).ToList()) { RemovePickupView(_pickupViews[gone]); _pickupViews.Remove(gone); }

        foreach (var e in _client.TakePickupEvents())
        {
            var look = LookOf(e.Type);
            if (e.End == PickupEnd.Expired) { _pickupsExpiredSeen++; if (_bot) GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} sees pickup #{e.Id} expire"); continue; }
            _pickupsCollectedSeen++;
            bool own = e.Collector == _client.PlayerId;
            if (own) _ownCollections++;
            var at = ToGodot(e.Position);
            if (look is not null && look.TakeFx.Length > 0 && _pickupFxReady && _fx is { IsReady: true } && !NoFx) _fx.Play(look.TakeFx, at, Basis.Identity, look.TakeFxScale);
            if (look is not null && look.TakeSound.Length > 0 && _sound is not null)
            {
                _sound.Post(look.TakeSound, new SoundEmitter { Position = at, Label = "pickup", OneShot = true, Own = own });
                CountSound("pickup");
            }
            if (_bot || own) GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} sees pickup #{e.Id} {look?.Blueprint ?? $"type {e.Type}"} taken by P{e.Collector}{(own ? $" (own; now clip {me.Clip} reserve {me.Reserve}, other {me.OtherClip}/{me.OtherReserve}, equipment {me.Grenades}; the next snapshot adds the gain)" : "")}");
        }

        // The prompt: the nearest pickup in reach of the living own player.
        string? prompt = null;
        if (me.Alive && !NoHud && !PickerBusy)   // not over the Esc menu or the customizer
        {
            float best = float.MaxValue;
            foreach (var p in live)
            {
                if (p.Type < 0 || p.Type >= spec.Types.Count) continue;
                float d = System.Numerics.Vector3.DistanceSquared(p.Position, me.Position);
                if (d < best && Pickups.InReach(spec.Types[p.Type], p.Position, me.Position))
                {
                    best = d;
                    prompt = LookOf(p.Type) is { } l && _pickupText.TryGetValue(l.Message, out var t) ? t : spec.Types[p.Type].Kind == PickupKind.Ammo ? "Pick up Ammo" : "Pick up Equipment";
                }
            }
        }
        _hudPrompt = prompt ?? "";   // ui/hud-fidelity: the HUD draws the prompt from the movie (Hud.DrawMoviePrompt); the label only without it
        if (_pickupPrompt is not null)
        {
            _pickupPrompt.Visible = prompt is not null && _hudView is not { DrawsPrompt: true };
            if (prompt is not null) _pickupPrompt.Text = $"[{PadHost.Hint("E", "B")}]  {prompt}";
        }
        if (_bot && _connectedFor - _pickupLogAt >= 10.0)
        {
            _pickupLogAt = _connectedFor;
            GD.Print($"t {_connectedFor:F0} s: id {_client.PlayerId} pickups: {live.Count} lying, seen {_pickupsSeen}, collected {_pickupsCollectedSeen} (own {_ownCollections}), expired {_pickupsExpiredSeen}; messages {_client.PickupMessages}{(prompt is null ? "" : $"; prompt '{prompt}'")}");
        }
    }

    void RemovePickupView(PickupView view)
    {
        view.StopIdle();
        view.QueueFree();
    }

    /// <summary>The F1 line about the pickups.</summary>
    string PickupInfoLine() => $"Pickups (fidelity gap 1; models and effects original, prompt key ours): {_pickupNote}; lying {_pickupViews.Count}, collected seen {_pickupsCollectedSeen} (own {_ownCollections})";

    /// <summary>An E press seen since the last input sample (a tap shorter than a tick still sends one tick of Interact).</summary>
    bool _interactLatched;

    /// <summary>From <c>_UnhandledInput</c>: latches an E press while the mouse is captured.</summary>
    void PickupKey(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.E } && Input.MouseMode == Input.MouseModeEnum.Captured) _interactLatched = true;
    }

    /// <summary>
    /// The E key (while the mouse is captured): <see cref="InputButtons.Interact"/> while held, or for one sample after a press that was let go
    /// before it (the server acts on the press edge).
    /// </summary>
    InputButtons InteractButton()
    {
        bool latched = _interactLatched;
        _interactLatched = false;
        return !NoPickups && Input.MouseMode == Input.MouseModeEnum.Captured && (latched || Input.IsPhysicalKeyPressed(Key.E)) ? InputButtons.Interact : InputButtons.None;
    }

    // ---------------------------------------------------------------- bots (test aid)

    /// <summary>
    /// With <c>--bot-pickups</c>, bots press E on alternate ticks while a pickup lies in reach (a press edge every other tick) and
    /// walk (on the navigation graph) to the nearest pickup within 30 m when no enemy is in sight and their ammunition or grenades are low
    /// (any carried weapon's reserve, or clip for clip-only weapons, below half, or fewer grenades than the item holds). Test heuristic, not the original's AI.
    /// </summary>
    PlayerInput BotPickupInput(PlayerInput scripted, uint tick)
    {
        if (_client is null || _config?.Pickups is not { } spec || NoPickups || !BotPickups) return scripted;
        var me = _client.Predicted;
        if (!me.Alive) return scripted;
        PickupView? target = null;
        float best = float.MaxValue;
        bool inReach = false;
        foreach (var p in _client.Pickups)
        {
            if (p.Type < 0 || p.Type >= spec.Types.Count) continue;
            if (Pickups.InReach(spec.Types[p.Type], p.Position, me.Position)) inReach = true;
            float d = System.Numerics.Vector3.Distance(p.Position, me.Position);
            if (d < best && d <= 30f && _pickupViews.TryGetValue(p.Id, out var v)) { best = d; target = v; }
        }
        if (inReach) return scripted with { Buttons = scripted.Buttons | (tick % 2 == 0 ? InputButtons.Interact : InputButtons.None) };
        if (target is null || !BotLow(me) || BotFightTarget(me, out _) is not null) return scripted;
        var goal = new System.Numerics.Vector3(target.Position.X, target.Position.Y, target.Position.Z);
        var (to, speed, _, _) = BotSteer(me, goal, $"pickup #{target.Id}");
        _botNote = $"to pickup #{target.Id} at {best:F0} m (low)";
        if (to.LengthSquared() < 1e-6f) return scripted;
        _yaw = MathF.Atan2(-to.X, to.Y);
        _pitch = 0f;
        return BotMoveInput(tick, to * speed, InputButtons.None);
    }

    bool BotLow(PlayerState me)
    {
        var c = _config!;
        foreach (var slot in new[] { WeaponSlot.Ranged, WeaponSlot.Pistol })
        {
            if (Loadouts.Weapon(c, me, slot) is not { } w) continue;
            bool hand = me.Slot == slot;
            int have = w.ReserveSize > 0 ? (hand ? me.Reserve : me.OtherReserve) : (hand ? me.Clip : me.OtherClip);
            int full = w.ReserveSize > 0 ? w.ReserveSize : w.ClipSize;
            if (full > 0 && have * 2 < full) return true;
        }
        return Loadouts.Equipment(c, me) is { } item && me.Grenades < item.Count;
    }
}
